using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using DOL.Database;
using DOL.Events;
using DOL.Logging;

namespace DOL.GS.Economy
{
    /// <summary>
    /// Autonomous dynamic auction-house economy.
    ///
    /// Responsibilities:
    ///   - Build a pool of eligible item templates once at startup.
    ///   - Spawn virtual NPC sellers (one set per realm) registered via HouseMgr fallback.
    ///   - Populate the MarketCache with bot listings up to the configured target stock.
    ///   - Continuously rotate a small slice of stock each tick so the market feels alive
    ///     at every moment rather than jumping every N minutes.
    ///
    /// Threading model:
    ///   - All generation work runs on a single dedicated background Task.
    ///   - Manual /economy commands serialize through a SemaphoreSlim so two producers
    ///     can never race to fill capacity.
    ///   - The merchants snapshot is immutable post-spawn, so hot picks need no global lock.
    ///   - The game loop is never blocked.
    /// </summary>
    public static class EconomyManager
    {
        private static readonly Logger log = LoggerManager.Create(MethodBase.GetCurrentMethod().DeclaringType);

        // Reserved fake-lot ranges. Each value is clamped inside its realm band of
        // MarketSearchEngine.GetRealmOfLot so realm filters keep working.
        private const int ALB_LOT_TOP = 1382, ALB_LOT_BOTTOM = 1300;
        private const int MID_LOT_TOP = 2573, MID_LOT_BOTTOM = 2480;
        private const int HIB_LOT_TOP = 4398, HIB_LOT_BOTTOM = 4300;

        // Shared identifiers. Exposed internal so the merchant class can reference them
        // without re-declaring the literal (one source of truth).
        internal const string OWNER_PREFIX = "Economy:";
        internal const string CREATOR_TAG = "Auction Market";
        internal const int ADD_BATCH_WAIT_SECONDS = 30;

        // Tick / safety constants.
        private const int MIN_TICK_SECONDS = 5;
        private const int MIN_FLUSH_SECONDS = 5;
        private const int SHUTDOWN_WAIT_SECONDS = 5;
        // Max items per single AddObject/DeleteObject call. Caps the initial-flush spike
        // when the worker accumulates many items between the first Initialize and the
        // first FlushLoop tick. 100 = each DB batch finishes in ~25ms typical write time on
        // MariaDB so other connections never wait noticeably for the pool, even on a host
        // doing tight loop work elsewhere.
        private const int MAX_FLUSH_CHUNK = 100;
        // Tick-percent calc: ticks_per_hour = 3600/tickSec. perTick = total * pct/100 / ticks_per_hour
        // = total * pct * tickSec / 360000. The 360000 is 100 (percent units) * 3600 (sec/h).
        private const long PERCENT_PER_HOUR_TO_PER_TICK_DIVISOR = 360000L;

        // Init-time state. The snapshot becomes immutable post-Initialize so we can read
        // it lock-free on the hot path.
        private static EconomyConsignmentMerchant[] _merchants = Array.Empty<EconomyConsignmentMerchant>();
        private static Dictionary<int, EconomyConsignmentMerchant> _byLot = new();
        private static readonly object _initLock = new();

        // Serializes all generation work so concurrent /economy refresh/topup invocations
        // cannot overshoot or race with the worker.
        private static readonly SemaphoreSlim _generationGate = new(1, 1);

        // Serializes DoFlush calls. The flusher task ticks every ECONOMY_DB_FLUSH_SECONDS,
        // and Shutdown also calls DoFlush after cancelling the task; if the flusher task
        // is still inside DoFlush when the shutdown wait times out, the two calls would
        // race on CompleteAddBatch. The mutex prevents that.
        private static readonly object _flushMutex = new();

        private static CancellationTokenSource _cts;
        private static Task _worker;
        private static Task _flusher;
        private static volatile bool _initialized;
        private static volatile bool _suspended;

        // Cached snapshot of non-bot listings, refreshed every
        // ECONOMY_PLAYER_LISTINGS_CACHE_SECONDS. Lets PlayerPurchaseTick reuse the same
        // scan across many ticks instead of walking the whole MarketCache each minute.
        private static DbInventoryItem[] _playerListingsCache = Array.Empty<DbInventoryItem>();
        private static long _playerListingsCacheUtcSec;
        private static readonly object _playerListingsCacheLock = new();

        // Anti-doublons: live count of bot listings per template id (Id_nb). The picker
        // skips templates already at ECONOMY_MAX_LISTINGS_PER_TEMPLATE. Concurrent dict
        // because OnRemoveItem (player buy) decrements from the player save thread while
        // the worker increments from its own thread.
        private static readonly ConcurrentDictionary<string, int> _listingsPerTemplate = new(StringComparer.Ordinal);

        public static bool IsInitialized => _initialized;
        public static bool IsSuspended => _suspended;

        public static int TotalListings
        {
            get
            {
                var snapshot = _merchants;
                int n = 0;
                for (int i = 0; i < snapshot.Length; i++)
                    n += snapshot[i].ItemCount;
                return n;
            }
        }

        public static IReadOnlyList<EconomyConsignmentMerchant> Merchants => _merchants;

        /// <summary>
        /// Hook for HouseMgr.GetConsignmentByHouseNumber fallback.
        /// </summary>
        public static GameConsignmentMerchant GetVirtualMerchant(int houseNumber)
        {
            return _byLot.TryGetValue(houseNumber, out var cm) ? cm : null;
        }

        [GameServerStartedEvent]
        public static void OnServerStarted(DOLEvent e, object sender, EventArgs args)
        {
            if (!EconomyConfig.ECONOMY_ENABLED)
            {
                if (log.IsInfoEnabled)
                    log.Info("Economy: disabled by server property economy_enabled=false.");
                return;
            }

            if (!ServerProperties.Properties.MARKET_ENABLE && !ServerProperties.Properties.MARKET_ENABLED)
            {
                if (log.IsInfoEnabled)
                    log.Info("Economy: market_enable/market_enabled both false. Skipping.");
                return;
            }

            AutoMigrateDefaults();

            try { Initialize(); }
            catch (Exception ex) { log.Error("Economy: failed to initialize.", ex); }
        }

        // Bump existing ServerProperty rows that still hold a previous default to the new
        // default. Manual overrides set to any other value are preserved. Runs once per
        // boot, no-op after the first successful migration since Value no longer matches
        // the old default. Also overrides the in-memory static so the current process
        // sees the new value without a property reload.
        private static void AutoMigrateDefaults()
        {
            // Each entry: (key, set of old defaults to catch, new default).
            // Multiple old defaults are listed for properties that already migrated once
            // (so we catch both pre-first-migration and pre-second-migration values).
            // The newDefault is hardcoded rather than read from the static so the guard
            // `oldDefault == newDefault` doesn't bail out when the DB row already holds
            // the previous default (which is the case we actually want to fix).

            // First-wave: 10k stock -> 100k, 6 sellers -> 334
            TryMigrateIntMulti("economy_target_stock",            newDefault: 100000, 10000);
            TryMigrateIntMulti("economy_seller_count_per_realm",  newDefault: 334,    6);

            // Ultra-calm pass: for 100k items + 1000 sellers on a server that's CPU/IO sensitive.
            // List every prior default we shipped so the migration catches installs at any
            // intermediate value. Each row in oldDefaults is one prior default we used.
            TryMigrateIntMulti("economy_initial_batch_size",      newDefault: 25,     200, 50);
            TryMigrateIntMulti("economy_initial_batch_sleep_ms",  newDefault: 300,    20, 50, 100);
            TryMigrateIntMulti("economy_tick_seconds",            newDefault: 300,    60, 120);
            TryMigrateIntMulti("economy_turnover_percent_per_hour", newDefault: 2,    16, 6);
            TryMigrateIntMulti("economy_db_flush_seconds",        newDefault: 180,    30, 90);
        }

        private static void TryMigrateIntMulti(string key, int newDefault, params int[] oldDefaults)
        {
            try
            {
                var row = GameServer.Database.FindObjectByKey<DbServerProperty>(key);
                if (row == null)
                    return;
                if (!int.TryParse(row.Value, out int currentValue))
                    return;

                // Preserve admin overrides: only migrate when Value still equals DefaultValue
                // (i.e. the admin never touched it via /serverproperties). Without this guard,
                // an admin who intentionally set the same integer that happens to coincide
                // with a prior default (e.g. economy_initial_batch_size = 200, which is also
                // one of our oldDefaults) would silently lose their override on every restart.
                if (!string.Equals(row.Value, row.DefaultValue, StringComparison.Ordinal))
                    return;

                bool matchesOldDefault = false;
                for (int i = 0; i < oldDefaults.Length; i++)
                {
                    if (currentValue == oldDefaults[i])
                    {
                        matchesOldDefault = true;
                        break;
                    }
                }
                if (!matchesOldDefault || currentValue == newDefault)
                    return;

                row.Value = newDefault.ToString();
                row.DefaultValue = newDefault.ToString();
                GameServer.Database.SaveObject(row);
                ApplyToStatic(key, newDefault);
                if (log.IsInfoEnabled)
                    log.Info($"Economy: auto-migrated server property {key} from {currentValue} to {newDefault}.");
            }
            catch (Exception ex)
            {
                log.Warn($"Economy: auto-migration of {key} failed: {ex.Message}");
            }
        }

        // Writes the migrated value to the in-memory static so the running process uses
        // it without waiting for a property reload. Switch-by-name keeps this colocated
        // with the migration list above so adding a property is a one-line change.
        private static void ApplyToStatic(string key, int value)
        {
            switch (key)
            {
                case "economy_target_stock":            EconomyConfig.ECONOMY_TARGET_STOCK = value; break;
                case "economy_seller_count_per_realm":  EconomyConfig.ECONOMY_SELLER_COUNT_PER_REALM = value; break;
                case "economy_initial_batch_size":      EconomyConfig.ECONOMY_INITIAL_BATCH_SIZE = value; break;
                case "economy_initial_batch_sleep_ms":  EconomyConfig.ECONOMY_INITIAL_BATCH_SLEEP_MS = value; break;
                case "economy_tick_seconds":            EconomyConfig.ECONOMY_TICK_SECONDS = value; break;
                case "economy_turnover_percent_per_hour": EconomyConfig.ECONOMY_TURNOVER_PERCENT_PER_HOUR = value; break;
                case "economy_db_flush_seconds":        EconomyConfig.ECONOMY_DB_FLUSH_SECONDS = value; break;
            }
        }

        [GameServerStoppedEvent]
        public static void OnServerStopped(DOLEvent e, object sender, EventArgs args)
        {
            try { Shutdown(); }
            catch (Exception ex) { log.Error("Economy: shutdown failed.", ex); }
        }

        public static void Initialize()
        {
            lock (_initLock)
            {
                if (_initialized)
                    return;

                if (!EconomyItemPool.Built)
                    EconomyItemPool.Build();

                if (EconomyItemPool.TotalTemplates == 0)
                {
                    log.Warn("Economy: no eligible item templates found. Economy will not run.");
                    // Don't set _initialized = true: a subsequent Initialize() (e.g. after
                    // the operator imports a template set and calls /economy refresh) can
                    // succeed without a process restart.
                    return;
                }

                bool initSucceeded = false;
                try
                {
                    SpawnMerchants();

                    int reattached = 0;
                    if (EconomyConfig.ECONOMY_PERSIST)
                        reattached = ReloadFromCache();

                    _cts = new CancellationTokenSource();
                    _worker = Task.Run(() => WorkerLoop(_cts.Token));
                    if (EconomyConfig.ECONOMY_PERSIST)
                        _flusher = Task.Run(() => FlushLoop(_cts.Token));
                    _initialized = true;
                    initSucceeded = true;

                    if (reattached > 0 && log.IsInfoEnabled)
                        log.Info($"Economy: re-attached {reattached} persisted listings from MarketCache.");
                }
                finally
                {
                    // Unwind partial state so a later retry doesn't double-spawn merchants
                    // or double-count listings via ReloadFromCache + AttachExistingListing.
                    if (!initSucceeded)
                    {
                        try { _cts?.Cancel(); } catch { }
                        _cts?.Dispose();
                        _cts = null;
                        _worker = null;
                        _flusher = null;
                        _merchants = Array.Empty<EconomyConsignmentMerchant>();
                        _byLot = new Dictionary<int, EconomyConsignmentMerchant>();
                        ResetListingCounts();
                        _initialized = false;
                    }
                }
            }

            if (log.IsInfoEnabled)
                log.Info($"Economy: initialized. Sellers={_merchants.Length}, target stock={EconomyConfig.ECONOMY_TARGET_STOCK}, tick={EconomyConfig.ECONOMY_TICK_SECONDS}s, turnover={EconomyConfig.ECONOMY_TURNOVER_PERCENT_PER_HOUR}%/h, persist={EconomyConfig.ECONOMY_PERSIST}.");
        }

        /// <summary>
        /// Scans MarketCache for items previously written by us (OwnerID prefix) and
        /// re-attaches them to the matching virtual merchant. Orphans (lot has no matching
        /// merchant) are deleted on the spot. Returns the number of items re-attached.
        /// </summary>
        private static int ReloadFromCache()
        {
            var orphans = new List<DbInventoryItem>();
            int attached = 0;

            foreach (DbInventoryItem item in MarketCache.SearchItems(default))
            {
                if (item?.OwnerID == null)
                    continue;
                if (!item.OwnerID.StartsWith(OWNER_PREFIX, StringComparison.Ordinal))
                    continue;

                if (!_byLot.TryGetValue(item.OwnerLot, out var merchant) ||
                    !merchant.AttachExistingListing(item))
                {
                    orphans.Add(item);
                    continue;
                }
                attached++;
            }

            if (orphans.Count > 0)
            {
                foreach (var item in orphans)
                    MarketCache.RemoveItem(item);
                try { GameServer.Database.DeleteObject(orphans); }
                catch (Exception ex) { log.Error("Economy: failed to delete orphan listings.", ex); }
                if (log.IsInfoEnabled)
                    log.Info($"Economy: pruned {orphans.Count} orphan listings (unknown owner lot or duplicate slot).");
            }

            return attached;
        }

        public static void Shutdown()
        {
            CancellationTokenSource cts;
            Task worker, flusher;

            lock (_initLock)
            {
                cts = _cts;
                worker = _worker;
                flusher = _flusher;
                _cts = null;
                _worker = null;
                _flusher = null;
                _initialized = false;
            }

            try
            {
                cts?.Cancel();
                Task.WaitAll(new[] { worker, flusher }.Where(t => t != null).ToArray(), TimeSpan.FromSeconds(SHUTDOWN_WAIT_SECONDS));
            }
            catch { /* ignore on shutdown */ }

            // Final flush so pending changes are persisted before process exit.
            // _flushMutex serializes this against any flusher task that's still finishing.
            if (EconomyConfig.ECONOMY_PERSIST)
            {
                try { DoFlush(); }
                catch (Exception ex) { log.Error("Economy: final flush failed.", ex); }
            }

            // Drop ConsignmentStateManager entries keyed by our OwnerIDs so a subsequent
            // Initialize() (same process, e.g. unit test or in-process restart) doesn't
            // inherit stale ConsignmentState money caches.
            var snapshot = _merchants;
            for (int i = 0; i < snapshot.Length; i++)
            {
                ConsignmentState state = ConsignmentStateManager.GetState(snapshot[i].OwnerId);
                if (state != null)
                    ConsignmentStateManager.RemoveState(snapshot[i].OwnerId, state);
            }

            _merchants = Array.Empty<EconomyConsignmentMerchant>();
            _byLot = new Dictionary<int, EconomyConsignmentMerchant>();
            ResetListingCounts();

            // Reset suspend state so an operator's /economy suspend doesn't carry over
            // into a subsequent re-Initialize within the same process.
            _suspended = false;

            // Reset the player-listings snapshot too so a subsequent Initialize doesn't
            // serve stale entries from the previous run.
            lock (_playerListingsCacheLock)
            {
                _playerListingsCache = Array.Empty<DbInventoryItem>();
                _playerListingsCacheUtcSec = 0;
            }

            cts?.Dispose();
        }

        public static void Suspend(bool suspended) => _suspended = suspended;

        /// <summary>
        /// Force a top-up to target stock. Serialized through the generation gate so
        /// concurrent invocations cannot overshoot.
        /// </summary>
        public static int ForceTopUp()
        {
            _generationGate.Wait();
            try { return GenerateUpToTargetLocked(int.MaxValue, CancellationToken.None); }
            finally { _generationGate.Release(); }
        }

        /// <summary>
        /// Manual rotation triggered by /economy refresh. Pops a fixed slice of stock and
        /// tops back up to target. Serialized through the generation gate.
        /// Returns the number of listings rotated out.
        /// </summary>
        public static int ManualRotate()
        {
            _generationGate.Wait();
            try
            {
                int target = EconomyConfig.ECONOMY_TARGET_STOCK;
                int total = TotalListings;
                if (total == 0)
                {
                    GenerateBatchLocked(target, target);
                    return 0;
                }

                // Same per-tick share as the worker, but at least 1 per non-empty merchant.
                int pctPerHour = Math.Clamp(EconomyConfig.ECONOMY_TURNOVER_PERCENT_PER_HOUR, 1, 100);
                int tickSec = Math.Max(MIN_TICK_SECONDS, EconomyConfig.ECONOMY_TICK_SECONDS);
                long perTickL = (long) total * pctPerHour * tickSec / PERCENT_PER_HOUR_TO_PER_TICK_DIVISOR;
                int toRotate = (int) Math.Max(1, perTickL);

                int popped = 0;
                for (int i = 0; i < toRotate; i++)
                {
                    var seller = PickSellerWithStock();
                    if (seller == null) break;
                    if (seller.PopRandomListing() != null) popped++;
                }

                int deficit = target - TotalListings;
                if (deficit > 0)
                    GenerateBatchLocked(deficit, deficit);

                return popped;
            }
            finally { _generationGate.Release(); }
        }

        /// <summary>
        /// Removes every bot listing. Useful for /economy clear.
        /// </summary>
        public static int ClearAll()
        {
            _generationGate.Wait();
            try
            {
                int removed = 0;
                var snapshot = _merchants;
                for (int i = 0; i < snapshot.Length; i++)
                    removed += snapshot[i].ClearStock();
                return removed;
            }
            finally { _generationGate.Release(); }
        }

        // ---------- internals ----------

        private static void SpawnMerchants()
        {
            int perRealm = Math.Max(1, EconomyConfig.ECONOMY_SELLER_COUNT_PER_REALM);
            var list = new List<EconomyConsignmentMerchant>(perRealm * 3);
            var byLot = new Dictionary<int, EconomyConsignmentMerchant>(perRealm * 3);

            CreateRealmMerchants(eRealm.Albion, ALB_LOT_TOP, ALB_LOT_BOTTOM, perRealm, list, byLot);
            CreateRealmMerchants(eRealm.Midgard, MID_LOT_TOP, MID_LOT_BOTTOM, perRealm, list, byLot);
            CreateRealmMerchants(eRealm.Hibernia, HIB_LOT_TOP, HIB_LOT_BOTTOM, perRealm, list, byLot);

            _merchants = list.ToArray();
            _byLot = byLot;
        }

        private static void CreateRealmMerchants(eRealm realm, int top, int bottom, int count,
                                                 List<EconomyConsignmentMerchant> list,
                                                 Dictionary<int, EconomyConsignmentMerchant> byLot)
        {
            int created = 0;
            // Probe stays strictly inside the realm band so MarketSearchEngine.GetRealmOfLot
            // never disagrees with our SellerRealm.
            for (int lot = top; lot >= bottom && created < count; lot--)
            {
                if (Housing.HouseMgr.GetHouse(lot) != null)
                    continue;

                string ownerId = $"{OWNER_PREFIX}{realm}:{created:D2}";
                string name = $"{realm} Auction Stall {created + 1}";
                var cm = new EconomyConsignmentMerchant(ownerId, lot, realm, name);
                byLot[lot] = cm;
                list.Add(cm);
                created++;
            }

            if (created < count && log.IsWarnEnabled)
                log.Warn($"Economy: only spawned {created}/{count} sellers for {realm} (band {bottom}..{top} exhausted).");
        }

        // ---------- worker loop ----------

        private static async Task WorkerLoop(CancellationToken token)
        {
            try
            {
                if (log.IsInfoEnabled)
                    log.Info("Economy: starting initial population...");

                await PopulateInitialAsync(token).ConfigureAwait(false);

                if (log.IsInfoEnabled)
                    log.Info($"Economy: initial population done. Listings={TotalListings}.");

                while (!token.IsCancellationRequested)
                {
                    int tickSec = Math.Max(MIN_TICK_SECONDS, EconomyConfig.ECONOMY_TICK_SECONDS);
                    try
                    {
                        await Task.Delay(TimeSpan.FromSeconds(tickSec), token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) { return; }

                    if (token.IsCancellationRequested)
                        return;
                    if (_suspended)
                        continue;

                    try { await RotateTickAsync(tickSec, token).ConfigureAwait(false); }
                    catch (Exception ex) { log.Error("Economy: rotation tick failed.", ex); }

                    // Bot purchases from player consignment listings - completes the
                    // bidirectional economy (player can actually sell into a market).
                    if (EconomyConfig.ECONOMY_BOT_BUYS_FROM_PLAYERS)
                    {
                        try { PlayerPurchaseTick(tickSec); }
                        catch (Exception ex) { log.Error("Economy: player purchase tick failed.", ex); }
                    }
                }
            }
            catch (Exception ex)
            {
                log.Error("Economy: worker loop crashed.", ex);
            }
        }

        private static async Task PopulateInitialAsync(CancellationToken token)
        {
            int target = EconomyConfig.ECONOMY_TARGET_STOCK;
            int batch = Math.Max(1, EconomyConfig.ECONOMY_INITIAL_BATCH_SIZE);
            int sleep = Math.Max(0, EconomyConfig.ECONOMY_INITIAL_BATCH_SLEEP_MS);

            while (!token.IsCancellationRequested)
            {
                int added;
                await _generationGate.WaitAsync(token).ConfigureAwait(false);
                try
                {
                    if (TotalListings >= target)
                        return;
                    added = GenerateBatchLocked(batch, target - TotalListings);
                }
                finally { _generationGate.Release(); }

                if (added == 0)
                    break;

                if (sleep > 0)
                {
                    try { await Task.Delay(sleep, token).ConfigureAwait(false); }
                    catch (OperationCanceledException) { return; }
                }
            }
        }

        // Continuous trickle rotation. Each tick rotates the slice of stock that maps to
        // the tick's share of the configured hourly turnover, so the market shifts a few
        // items per minute rather than 800 every 30 minutes.
        private static async Task RotateTickAsync(int tickSec, CancellationToken token)
        {
            int target = EconomyConfig.ECONOMY_TARGET_STOCK;
            int turnoverPctPerHour = Math.Clamp(EconomyConfig.ECONOMY_TURNOVER_PERCENT_PER_HOUR, 0, 100);
            if (turnoverPctPerHour <= 0)
            {
                // Worker still tops up if stock has bled (e.g. players bought items).
                await TopUpOnceAsync(token).ConfigureAwait(false);
                return;
            }

            int total = TotalListings;
            if (total == 0)
            {
                await PopulateInitialAsync(token).ConfigureAwait(false);
                return;
            }

            // Items to rotate this tick = total * pct/100 * tickSec/3600.
            // Computed in long to avoid overflow at large stock sizes.
            long perTickL = (long) total * turnoverPctPerHour * tickSec / PERCENT_PER_HOUR_TO_PER_TICK_DIVISOR;
            int perTick = (int) Math.Min(int.MaxValue, perTickL);
            if (perTick < 1)
                perTick = 1;

            await _generationGate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                // Phase 1: pop random listings.
                for (int i = 0; i < perTick; i++)
                {
                    var seller = PickSellerWithStock();
                    if (seller == null)
                        break;
                    seller.PopRandomListing();
                }

                // Phase 2: top back up to target (in case we bled from purchases).
                int deficit = target - TotalListings;
                if (deficit > 0)
                    GenerateBatchLocked(deficit, deficit);
            }
            finally { _generationGate.Release(); }
        }

        private static async Task TopUpOnceAsync(CancellationToken token)
        {
            await _generationGate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                int deficit = EconomyConfig.ECONOMY_TARGET_STOCK - TotalListings;
                if (deficit > 0)
                    GenerateBatchLocked(deficit, deficit);
            }
            finally { _generationGate.Release(); }
        }

        // ---------- batched DB flush ----------

        private static async Task FlushLoop(CancellationToken token)
        {
            try
            {
                while (!token.IsCancellationRequested)
                {
                    int sec = Math.Max(MIN_FLUSH_SECONDS, EconomyConfig.ECONOMY_DB_FLUSH_SECONDS);
                    try { await Task.Delay(TimeSpan.FromSeconds(sec), token).ConfigureAwait(false); }
                    catch (OperationCanceledException) { return; }

                    try { DoFlush(); }
                    catch (Exception ex) { log.Error("Economy: flush tick failed.", ex); }
                }
            }
            catch (Exception ex) { log.Error("Economy: flush loop crashed.", ex); }
        }

        private static void DoFlush()
        {
            // Serialize: the periodic flusher and the shutdown-final flush must not
            // overlap, or two CompleteAddBatch calls on the same merchant would step on
            // each other's _inFlightAdds / _addBatchIdle state.
            lock (_flushMutex)
                DoFlushLocked();
        }

        private static void DoFlushLocked()
        {
            var snapshot = _merchants;
            List<DbInventoryItem> allAdds = null;
            List<DbInventoryItem> allDels = null;

            // Phase 1: drain pending mutations under per-merchant locks. Items in `adds`
            // are tracked in each merchant's _inFlightAdds; any concurrent pop/buy is
            // reconciled by CompleteAddBatch afterwards.
            for (int i = 0; i < snapshot.Length; i++)
            {
                var (a, d) = snapshot[i].DrainPending();
                if (a != null)
                {
                    allAdds ??= new List<DbInventoryItem>(a.Count);
                    allAdds.AddRange(a);
                }
                if (d != null)
                {
                    allDels ??= new List<DbInventoryItem>(d.Count);
                    allDels.AddRange(d);
                }
            }

            // Phase 2: the actual DB calls happen OUTSIDE any merchant lock so we don't
            // serialize buy/sell/rotation against DB latency. Player buys that touched an
            // in-flight item block briefly on the merchant's _addBatchIdle event.
            // Big batches are chunked so the very first DoFlush after a fresh-server
            // initial population (which accumulates ~10k items into _pendingAdds before
            // the flusher's first tick) doesn't hit the DB with a single huge transaction.
            // try/finally guarantees CompleteAddBatch runs even if the AddObject throws —
            // otherwise _inFlightAdds stays populated and _addBatchIdle stays Reset(),
            // causing every subsequent OnRemoveItem on that merchant to block for 30 s.
            List<DbInventoryItem> followUpDeletes = null;
            try
            {
                FlushChunked(allAdds, isAdd: true);
                FlushChunked(allDels, isAdd: false);
            }
            finally
            {
                // Phase 3: complete the add batch and pick up "raced" rows that were popped
                // or transferred to a player while their AddObject was running. Items left
                // in our slots are kept; ones that left in-memory state with OwnerID still
                // ours get deleted now (in a single follow-up call).
                for (int i = 0; i < snapshot.Length; i++)
                {
                    var leftovers = snapshot[i].CompleteAddBatch();
                    if (leftovers != null && leftovers.Count > 0)
                    {
                        followUpDeletes ??= new List<DbInventoryItem>(leftovers.Count);
                        followUpDeletes.AddRange(leftovers);
                    }
                }
            }

            FlushChunked(followUpDeletes, isAdd: false);

            if (EconomyConfig.ECONOMY_VERBOSE_LOG && log.IsDebugEnabled && (allAdds != null || allDels != null || followUpDeletes != null))
                log.Debug($"Economy flush: +{allAdds?.Count ?? 0} / -{allDels?.Count ?? 0} / raced -{followUpDeletes?.Count ?? 0}");
        }

        private static void FlushChunked(List<DbInventoryItem> items, bool isAdd)
        {
            if (items == null || items.Count == 0)
                return;

            int total = items.Count;
            if (total <= MAX_FLUSH_CHUNK)
            {
                try
                {
                    if (isAdd) GameServer.Database.AddObject(items);
                    else GameServer.Database.DeleteObject(items);
                }
                catch (Exception ex) { log.Error($"Economy: batched {(isAdd ? "AddObject" : "DeleteObject")} failed for {total} listings.", ex); }
                return;
            }

            // Subdivide. Allocations are minimal: we rent slices via List<T>.GetRange.
            for (int offset = 0; offset < total; offset += MAX_FLUSH_CHUNK)
            {
                int take = Math.Min(MAX_FLUSH_CHUNK, total - offset);
                List<DbInventoryItem> chunk = items.GetRange(offset, take);
                try
                {
                    if (isAdd) GameServer.Database.AddObject(chunk);
                    else GameServer.Database.DeleteObject(chunk);
                }
                catch (Exception ex) { log.Error($"Economy: chunked {(isAdd ? "AddObject" : "DeleteObject")} failed at offset {offset}/{total}.", ex); }
            }
        }

        // ---------- bot purchases from player listings ----------

        /// <summary>
        /// Scans the cached player consignment listings and probabilistically buys each.
        /// Per-listing probability is derived from the expected time-to-sale curve in
        /// EconomyPricing.ComputeExpectedSaleSeconds, so cheap items move in minutes, fair
        /// items in hours-to-days, overpriced items linger or never sell.
        /// </summary>
        private static void PlayerPurchaseTick(int tickSec)
        {
            DbInventoryItem[] listings = GetPlayerListingsCached();
            if (listings.Length == 0)
                return;

            int hardCap = Math.Max(1, EconomyConfig.ECONOMY_PLAYER_PURCHASE_MAX_PER_TICK);
            int boughtThisTick = 0;
            int evaluated = 0;

            int priceFloor = Math.Max(1, EconomyConfig.ECONOMY_PRICE_FLOOR_COPPER);

            for (int i = 0; i < listings.Length; i++)
            {
                if (boughtThisTick >= hardCap)
                    break;

                DbInventoryItem item = listings[i];
                if (item == null || item.SellPrice <= 0)
                    continue;

                // Anti-exploit: refuse to buy back items we created ourselves. Otherwise a
                // player could flip bot listings (buy at 70% market, relist at 150%, bot
                // buys back) for a sustainable gold faucet. Mob loot / quest / crafted
                // items have a different Creator and remain eligible.
                if (string.Equals(item.Creator, CREATOR_TAG, StringComparison.Ordinal))
                    continue;

                // Apply the same price floor as bot pricing so cheap listings can't be
                // used to drain the player-listings cache or laundered into the economy.
                if (item.SellPrice < priceFloor)
                    continue;

                DbItemTemplate template = item.Template;
                if (template == null)
                    continue;

                double expectedSec = EconomyPricing.ComputeExpectedSaleSeconds(template, item.SellPrice, Math.Max(1, item.Count));
                if (expectedSec <= 0)
                    continue; // beyond the hard ceiling - never bought

                evaluated++;

                // Exponential-arrival: p(this tick) = 1 - exp(-tickSec / expectedSec).
                // For very short expectedSec the prob saturates near 1 (cheap items sell
                // almost instantly); for very long expectedSec it's near 0.
                double pTick = 1.0 - Math.Exp(-tickSec / expectedSec);
                if (pTick <= 0)
                    continue;

                // Single random draw per listing.
                if (Util.RandomDouble() >= pTick)
                    continue;

                if (TryBotBuyFromPlayer(item))
                    boughtThisTick++;
            }

            if (EconomyConfig.ECONOMY_VERBOSE_LOG && log.IsDebugEnabled && evaluated > 0)
                log.Debug($"Economy: player-purchase tick evaluated {evaluated} listings, bought {boughtThisTick}.");
        }

        /// <summary>
        /// Invalidates the player-listings snapshot so the next PlayerPurchaseTick
        /// re-scans MarketCache. Safe to call from any thread; cheap. Intended to be
        /// invoked by player consignment add/remove paths so the bot purchase loop
        /// picks up fresh listings without waiting for the TTL. Until those hooks are
        /// wired, the TTL is the only invalidation path - which is functionally safe
        /// (MarketCache.RemoveItem deduplicates by reference) but means a brand-new
        /// player listing can wait up to ECONOMY_PLAYER_LISTINGS_CACHE_SECONDS before
        /// being considered by the bot buyer.
        /// </summary>
        public static void InvalidatePlayerListingsCache()
        {
            lock (_playerListingsCacheLock)
                _playerListingsCacheUtcSec = 0;
        }

        /// <summary>
        /// Returns a cached snapshot of non-bot listings. Refreshed every
        /// ECONOMY_PLAYER_LISTINGS_CACHE_SECONDS. This avoids walking the full MarketCache
        /// every tick (which would allocate a fresh list of all ~10k items).
        /// </summary>
        private static DbInventoryItem[] GetPlayerListingsCached()
        {
            int ttl = Math.Max(30, EconomyConfig.ECONOMY_PLAYER_LISTINGS_CACHE_SECONDS);
            long nowSec = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

            lock (_playerListingsCacheLock)
            {
                // Validity is TTL-only. The previous Length>0 guard forced a full MarketCache
                // walk every tick on a server with zero player listings (the common case).
                if (_playerListingsCacheUtcSec != 0 && nowSec - _playerListingsCacheUtcSec < ttl)
                    return _playerListingsCache;
            }

            var collected = new List<DbInventoryItem>();
            foreach (DbInventoryItem item in MarketCache.SearchItems(default))
            {
                if (item?.OwnerID == null) continue;
                if (item.OwnerID.StartsWith(OWNER_PREFIX, StringComparison.Ordinal)) continue;
                if (item.SellPrice <= 0) continue;
                collected.Add(item);
            }
            var snapshot = collected.ToArray();

            lock (_playerListingsCacheLock)
            {
                _playerListingsCache = snapshot;
                _playerListingsCacheUtcSec = nowSec;
            }
            return snapshot;
        }

        /// <summary>
        /// Removes the item from the cache, deletes the row, credits the player's
        /// consignment merchant. MarketCache.RemoveItem is the atomic race guard:
        /// if a real player buy ran first and removed the same item, our call returns
        /// false and we skip - the player buy already handled everything.
        /// </summary>
        private static bool TryBotBuyFromPlayer(DbInventoryItem item)
        {
            int price = item.SellPrice;
            ushort lot = item.OwnerLot;
            string owner = item.OwnerID;
            string itemName = item.Name;

            if (!MarketCache.RemoveItem(item))
                return false;

            try
            {
                GameServer.Database.DeleteObject(item);
            }
            catch (Exception ex)
            {
                if (log.IsWarnEnabled)
                    log.Warn($"Economy: failed to delete purchased player listing '{itemName}' (owner={owner}).", ex);
                // Item is already out of cache; continue with money credit anyway so the
                // player isn't penalized by a transient DB hiccup.
            }

            GameConsignmentMerchant cm = Housing.HouseMgr.GetConsignmentByHouseNumber(lot);
            if (cm != null && cm is not EconomyConsignmentMerchant)
            {
                ConsignmentState state = ConsignmentStateManager.GetState(cm);
                state?.AddMoney(price);
            }

            if (EconomyConfig.ECONOMY_VERBOSE_LOG && log.IsDebugEnabled)
                log.Debug($"Economy: bot bought '{itemName}' from {owner} for {price}.");

            return true;
        }

        // ---------- generation (must be called with _generationGate held) ----------

        private static int GenerateUpToTargetLocked(int maxBatchSize, CancellationToken token)
        {
            int target = EconomyConfig.ECONOMY_TARGET_STOCK;
            int added = 0;
            int safety = target + 100;
            while (!token.IsCancellationRequested && TotalListings < target && safety-- > 0)
            {
                int remaining = target - TotalListings;
                int chunk = Math.Min(maxBatchSize, remaining);
                int n = GenerateBatchLocked(chunk, remaining);
                if (n == 0) break;
                added += n;
            }
            return added;
        }

        private static int GenerateBatchLocked(int batchSize, int globalRemaining)
        {
            if (globalRemaining <= 0)
                return 0;

            int toGenerate = Math.Min(batchSize, globalRemaining);
            int added = 0;

            int totalWeight = WeightSum();
            if (totalWeight <= 0)
                return 0;

            for (int i = 0; i < toGenerate; i++)
            {
                EconomyItemPool.Category cat = RollCategory(totalWeight);
                eRealm realm = RollRealm();

                DbItemTemplate template = PickTemplate(realm, cat);
                if (template == null)
                    continue;

                EconomyConsignmentMerchant seller = PickSellerWithCapacity(realm);
                if (seller == null)
                    continue;

                DbInventoryItem listing = BuildListing(template, cat);
                if (listing == null)
                    continue;

                if (seller.TryAddListing(listing))
                    added++;
            }

            return added;
        }

        private static int WeightSum()
        {
            int s = 0;
            s += Math.Max(0, EconomyConfig.ECONOMY_WEIGHT_ARMOR);
            s += Math.Max(0, EconomyConfig.ECONOMY_WEIGHT_WEAPON);
            s += Math.Max(0, EconomyConfig.ECONOMY_WEIGHT_JEWELRY);
            s += Math.Max(0, EconomyConfig.ECONOMY_WEIGHT_CONSUMABLE);
            s += Math.Max(0, EconomyConfig.ECONOMY_WEIGHT_RESOURCE);
            return s;
        }

        private static EconomyItemPool.Category RollCategory(int totalWeight)
        {
            int roll = Util.Random(1, totalWeight);
            int w = Math.Max(0, EconomyConfig.ECONOMY_WEIGHT_ARMOR);
            if (roll <= w) return EconomyItemPool.Category.Armor;
            roll -= w;
            w = Math.Max(0, EconomyConfig.ECONOMY_WEIGHT_WEAPON);
            if (roll <= w) return EconomyItemPool.Category.Weapon;
            roll -= w;
            w = Math.Max(0, EconomyConfig.ECONOMY_WEIGHT_JEWELRY);
            if (roll <= w) return EconomyItemPool.Category.Jewelry;
            roll -= w;
            w = Math.Max(0, EconomyConfig.ECONOMY_WEIGHT_CONSUMABLE);
            if (roll <= w) return EconomyItemPool.Category.Consumable;
            return EconomyItemPool.Category.Resource;
        }

        private static eRealm RollRealm() => Util.Random(1, 3) switch
        {
            1 => eRealm.Albion,
            2 => eRealm.Midgard,
            _ => eRealm.Hibernia
        };

        private static DbItemTemplate PickTemplate(eRealm realm, EconomyItemPool.Category cat)
        {
            // Realm-specific first, then realm-none fallback. We do NOT cross realms so
            // realm-filtered Market Explorer searches stay honest.
            DbItemTemplate t = PickFromBuckets(realm, cat);
            if (t != null)
                return t;
            return PickFromBuckets(eRealm.None, cat);
        }

        private static DbItemTemplate PickFromBuckets(eRealm realm, EconomyItemPool.Category cat)
        {
            int[] tierWeights = ResolveTierWeights();
            int cap = EconomyConfig.ECONOMY_MAX_LISTINGS_PER_TEMPLATE;

            // Up to 6 tier rolls so a saturated tier doesn't starve the picker. After 6,
            // we fall back to the flat bucket so we always make progress.
            for (int tierAttempt = 0; tierAttempt < 6; tierAttempt++)
            {
                List<DbItemTemplate> bucket = tierWeights == null
                    ? EconomyItemPool.GetBucket(realm, cat)
                    : EconomyItemPool.GetTieredBucket(realm, cat, RollTier(tierWeights));
                if (bucket == null || bucket.Count == 0)
                    continue;

                DbItemTemplate picked = PickRespectingCap(bucket, cap);
                if (picked != null)
                    return picked;
            }

            // Last-resort flat bucket with cap (still honors anti-doublons).
            var flat = EconomyItemPool.GetBucket(realm, cat);
            if (flat == null || flat.Count == 0)
                return null;
            return PickRespectingCap(flat, cap) ?? flat[Util.Random(flat.Count - 1)];
        }

        // Bounded probing: a saturated bucket would otherwise loop forever. After
        // MAX_PROBES failures we return null so the caller can fall back to another tier
        // or a different bucket without blocking the generation tick.
        private const int CAP_PROBE_ATTEMPTS = 24;

        private static DbItemTemplate PickRespectingCap(List<DbItemTemplate> bucket, int cap)
        {
            if (bucket.Count == 0)
                return null;
            if (cap <= 0)
                return bucket[Util.Random(bucket.Count - 1)];

            int probes = Math.Min(CAP_PROBE_ATTEMPTS, bucket.Count);
            for (int i = 0; i < probes; i++)
            {
                DbItemTemplate cand = bucket[Util.Random(bucket.Count - 1)];
                if (!_listingsPerTemplate.TryGetValue(cand.Id_nb, out int n) || n < cap)
                    return cand;
            }
            return null;
        }

        private static int[] _cachedTierWeights;
        private static string _cachedTierWeightsRaw;

        private static int[] ResolveTierWeights()
        {
            string raw = EconomyConfig.ECONOMY_TIER_WEIGHTS;
            if (string.IsNullOrWhiteSpace(raw))
                return null;
            if (ReferenceEquals(raw, _cachedTierWeightsRaw))
                return _cachedTierWeights;

            string[] parts = raw.Split(',');
            if (parts.Length != EconomyItemPool.TIER_COUNT)
            {
                _cachedTierWeightsRaw = raw;
                _cachedTierWeights = null;
                return null;
            }
            int[] w = new int[EconomyItemPool.TIER_COUNT];
            int sum = 0;
            for (int i = 0; i < parts.Length; i++)
            {
                if (!int.TryParse(parts[i].Trim(), out w[i]) || w[i] < 0)
                {
                    _cachedTierWeightsRaw = raw;
                    _cachedTierWeights = null;
                    return null;
                }
                sum += w[i];
            }
            if (sum <= 0)
            {
                _cachedTierWeightsRaw = raw;
                _cachedTierWeights = null;
                return null;
            }
            _cachedTierWeightsRaw = raw;
            _cachedTierWeights = w;
            return w;
        }

        private static int RollTier(int[] weights)
        {
            int sum = 0;
            for (int i = 0; i < weights.Length; i++)
                sum += weights[i];
            int roll = Util.Random(1, sum);
            for (int i = 0; i < weights.Length; i++)
            {
                if (roll <= weights[i])
                    return i;
                roll -= weights[i];
            }
            return weights.Length - 1;
        }

        // ---- Listing-count hooks called by EconomyConsignmentMerchant on add/remove.
        // Kept internal so only the economy module can mutate the counts.
        internal static void OnListingAdded(string idNb)
        {
            if (string.IsNullOrEmpty(idNb))
                return;
            _listingsPerTemplate.AddOrUpdate(idNb, 1, static (_, c) => c + 1);
        }

        internal static void OnListingRemoved(string idNb)
        {
            if (string.IsNullOrEmpty(idNb))
                return;
            // Don't bother cleaning up zero entries: the dictionary upper bound is the
            // number of distinct template ids, which is ~40k and dwarfed by the rest of
            // the economy state.
            _listingsPerTemplate.AddOrUpdate(idNb, 0, static (_, c) => c > 0 ? c - 1 : 0);
        }

        internal static void ResetListingCounts()
        {
            _listingsPerTemplate.Clear();
        }

        // Walks the immutable merchants snapshot. Two-pass reservoir to pick a random
        // capacity-having seller in the requested realm, without allocating.
        private static EconomyConsignmentMerchant PickSellerWithCapacity(eRealm realm)
        {
            var snapshot = _merchants;
            int n = snapshot.Length;
            if (n == 0) return null;

            int countInRealm = 0;
            for (int i = 0; i < n; i++)
            {
                var m = snapshot[i];
                if (m.SellerRealm == realm && m.FreeSlots > 0)
                    countInRealm++;
            }

            if (countInRealm > 0)
                return PickRealmMatch(snapshot, realm, countInRealm);

            // No in-realm capacity: cross realms only as a last resort.
            int countAny = 0;
            for (int i = 0; i < n; i++)
            {
                if (snapshot[i].FreeSlots > 0)
                    countAny++;
            }
            if (countAny == 0) return null;
            return PickAnyMatch(snapshot, countAny);
        }

        private static EconomyConsignmentMerchant PickRealmMatch(EconomyConsignmentMerchant[] snapshot, eRealm realm, int count)
        {
            int pick = Util.Random(0, count - 1);
            int k = 0;
            for (int i = 0; i < snapshot.Length; i++)
            {
                var m = snapshot[i];
                if (m.SellerRealm == realm && m.FreeSlots > 0)
                {
                    if (k++ == pick) return m;
                }
            }
            return null;
        }

        private static EconomyConsignmentMerchant PickAnyMatch(EconomyConsignmentMerchant[] snapshot, int count)
        {
            int pick = Util.Random(0, count - 1);
            int k = 0;
            for (int i = 0; i < snapshot.Length; i++)
            {
                if (snapshot[i].FreeSlots > 0)
                {
                    if (k++ == pick) return snapshot[i];
                }
            }
            return null;
        }

        private static EconomyConsignmentMerchant PickSellerWithStock()
        {
            var snapshot = _merchants;
            int n = snapshot.Length;
            int countWith = 0;
            for (int i = 0; i < n; i++)
            {
                if (snapshot[i].ItemCount > 0)
                    countWith++;
            }
            if (countWith == 0) return null;
            int pick = Util.Random(0, countWith - 1);
            int k = 0;
            for (int i = 0; i < n; i++)
            {
                if (snapshot[i].ItemCount > 0)
                {
                    if (k++ == pick) return snapshot[i];
                }
            }
            return null;
        }

        private static DbInventoryItem BuildListing(DbItemTemplate template, EconomyItemPool.Category category)
        {
            GameInventoryItem item;
            try { item = GameInventoryItem.Create(template); }
            catch (Exception ex)
            {
                if (log.IsWarnEnabled)
                    log.Warn($"Economy: failed to construct GameInventoryItem for {template.Id_nb}", ex);
                return null;
            }

            if (item == null)
                return null;

            if (item.IsStackable)
            {
                int max = template.MaxCount > 0 ? template.MaxCount : 20;
                int count;
                if (category == EconomyItemPool.Category.Consumable)
                    count = Util.Random(1, Math.Min(max, 20));
                else if (category == EconomyItemPool.Category.Resource)
                    count = Util.Random(1, Math.Min(max, 50));
                else
                    count = Math.Max(1, template.PackSize);
                item.Count = count;
            }

            item.IsCrafted = false;
            item.IsROG = false;
            item.Creator = CREATOR_TAG;
            item.AllowAdd = true;
            item.IsPersisted = false;

            item.SellPrice = EconomyPricing.ComputeSellPrice(template);
            return item;
        }
    }
}

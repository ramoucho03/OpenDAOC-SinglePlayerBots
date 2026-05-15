using System;
using System.Collections.Generic;
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

        private const string OWNER_PREFIX = "Economy:";

        // Init-time state. The snapshot becomes immutable post-Initialize so we can read
        // it lock-free on the hot path.
        private static EconomyConsignmentMerchant[] _merchants = Array.Empty<EconomyConsignmentMerchant>();
        private static Dictionary<int, EconomyConsignmentMerchant> _byLot = new();
        private static readonly object _initLock = new();

        // Serializes all generation work so concurrent /economy refresh/topup invocations
        // cannot overshoot or race with the worker.
        private static readonly SemaphoreSlim _generationGate = new(1, 1);

        private static CancellationTokenSource _cts;
        private static Task _worker;
        private static volatile bool _initialized;
        private static volatile bool _suspended;

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

            try { Initialize(); }
            catch (Exception ex) { log.Error("Economy: failed to initialize.", ex); }
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
                    _initialized = true;
                    return;
                }

                SpawnMerchants();

                _cts = new CancellationTokenSource();
                _worker = Task.Run(() => WorkerLoop(_cts.Token));
                _initialized = true;
            }

            if (log.IsInfoEnabled)
                log.Info($"Economy: initialized. Sellers={_merchants.Length}, target stock={EconomyConfig.ECONOMY_TARGET_STOCK}, tick={EconomyConfig.ECONOMY_TICK_SECONDS}s, turnover={EconomyConfig.ECONOMY_TURNOVER_PERCENT_PER_HOUR}%/h.");
        }

        public static void Shutdown()
        {
            CancellationTokenSource cts;
            Task worker;

            lock (_initLock)
            {
                cts = _cts;
                worker = _worker;
                _cts = null;
                _worker = null;
                _initialized = false;
            }

            try
            {
                cts?.Cancel();
                worker?.Wait(TimeSpan.FromSeconds(5));
            }
            catch { /* ignore on shutdown */ }
            finally
            {
                cts?.Dispose();
            }
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
                int tickSec = Math.Max(5, EconomyConfig.ECONOMY_TICK_SECONDS);
                long perTickL = (long) total * pctPerHour * tickSec / 360000L;
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
                    int tickSec = Math.Max(5, EconomyConfig.ECONOMY_TICK_SECONDS);
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
            long perTickL = (long) total * turnoverPctPerHour * tickSec / 360000L;
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

        // ---------- bot purchases from player listings ----------

        /// <summary>
        /// Scans player consignment listings (non-bot) and probabilistically buys any
        /// priced within ECONOMY_MAX_OVERPRICE_PERCENT of the deterministic market value.
        /// Players post on their housing CM, bots buy at the player's asking price, money
        /// is credited to the CM's gold/BP balance via the existing ConsignmentState path.
        /// </summary>
        private static void PlayerPurchaseTick(int tickSec)
        {
            int chancePctPerHour = Math.Clamp(EconomyConfig.ECONOMY_PLAYER_PURCHASE_CHANCE_PER_HOUR_PERCENT, 0, 100);
            if (chancePctPerHour <= 0)
                return;

            // Per-listing roll: probability that this listing is bought this tick.
            // chance_per_tick = chance_per_hour * (tick_sec / 3600). Computed in basis points
            // so we can roll a single integer compare.
            int chanceBpPerTick = (int) ((long) chancePctPerHour * tickSec * 100L / 3600L);
            if (chanceBpPerTick <= 0)
                return;

            int maxOverpricePct = Math.Max(50, EconomyConfig.ECONOMY_MAX_OVERPRICE_PERCENT);
            int hardCap = Math.Max(1, EconomyConfig.ECONOMY_PLAYER_PURCHASE_MAX_PER_TICK);

            int boughtThisTick = 0;
            int evaluated = 0;

            foreach (DbInventoryItem item in MarketCache.SearchItems(default))
            {
                if (boughtThisTick >= hardCap)
                    break;
                if (item == null)
                    continue;

                string owner = item.OwnerID;
                // Skip our own bot listings.
                if (string.IsNullOrEmpty(owner) || owner.StartsWith(OWNER_PREFIX, StringComparison.Ordinal))
                    continue;
                if (item.SellPrice <= 0)
                    continue;

                evaluated++;

                // Fair-price gate: ignore listings priced more than max-overprice above
                // computed market value (per unit × count). This is the anti-cheat rail.
                DbItemTemplate template = item.Template;
                if (template == null)
                    continue;

                long fairUnit = EconomyPricing.ComputeFairValue(template);
                long fairTotal = fairUnit * Math.Max(1, item.Count);
                long ceiling = fairTotal * maxOverpricePct / 100L;
                if (item.SellPrice > ceiling)
                    continue;

                // Per-listing roll.
                if (Util.Random(0, 9999) >= chanceBpPerTick)
                    continue;

                if (TryBotBuyFromPlayer(item))
                    boughtThisTick++;
            }

            if (EconomyConfig.ECONOMY_VERBOSE_LOG && log.IsDebugEnabled && evaluated > 0)
                log.Debug($"Economy: player-purchase tick scanned {evaluated} listings, bought {boughtThisTick}.");
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
            List<DbItemTemplate> bucket = EconomyItemPool.GetBucket(realm, cat);
            if (bucket != null && bucket.Count > 0)
                return bucket[Util.Random(bucket.Count - 1)];

            bucket = EconomyItemPool.GetBucket(eRealm.None, cat);
            if (bucket != null && bucket.Count > 0)
                return bucket[Util.Random(bucket.Count - 1)];

            return null;
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
            item.Creator = "Auction Market";
            item.AllowAdd = true;
            item.IsPersisted = false;

            item.SellPrice = EconomyPricing.ComputeSellPrice(template);
            return item;
        }
    }
}

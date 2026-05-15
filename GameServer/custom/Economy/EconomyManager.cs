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
    ///   - Rotate a fraction of the stock on a timer to simulate a populated server.
    ///
    /// Threading model:
    ///   - All work runs on a single dedicated background Task.
    ///   - Generation is chunked with cooperative sleeps to avoid CPU spikes.
    ///   - MarketCache and EconomyConsignmentMerchant are thread-safe.
    ///   - The game loop is never blocked.
    /// </summary>
    public static class EconomyManager
    {
        private static readonly Logger log = LoggerManager.Create(MethodBase.GetCurrentMethod().DeclaringType);

        // Fake house-number ranges reserved for the bot economy. These must not overlap
        // with any real house in the database. We pick numbers high enough to be safe
        // and clamp them inside realm bands of MarketSearchEngine.GetRealmOfLot
        // so realm filters in the Market Explorer still work.
        private const int ALB_LOT_BASE = 1370; // Albion realm band: 1..1382
        private const int MID_LOT_BASE = 2560; // Midgard realm band: 1383..2573
        private const int HIB_LOT_BASE = 4380; // Hibernia realm band: 2574..4398

        private const string OWNER_PREFIX = "Economy:";

        private static readonly Dictionary<int, EconomyConsignmentMerchant> _byLot = new();
        private static readonly List<EconomyConsignmentMerchant> _all = new();
        private static readonly object _stateLock = new();

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
                int n = 0;
                lock (_stateLock)
                {
                    foreach (var m in _all)
                        n += m.ItemCount;
                }
                return n;
            }
        }

        public static IReadOnlyList<EconomyConsignmentMerchant> Merchants
        {
            get
            {
                lock (_stateLock)
                    return _all.ToArray();
            }
        }

        /// <summary>
        /// Hook for HouseMgr.GetConsignmentByHouseNumber fallback.
        /// </summary>
        public static GameConsignmentMerchant GetVirtualMerchant(int houseNumber)
        {
            lock (_stateLock)
            {
                return _byLot.TryGetValue(houseNumber, out var cm) ? cm : null;
            }
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

            try
            {
                Initialize();
            }
            catch (Exception ex)
            {
                log.Error("Economy: failed to initialize.", ex);
            }
        }

        public static void Initialize()
        {
            lock (_stateLock)
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
                log.Info($"Economy: initialized. Sellers={_all.Count}, target stock={EconomyConfig.ECONOMY_TARGET_STOCK}, refresh every {EconomyConfig.ECONOMY_REFRESH_INTERVAL_MINUTES} min.");
        }

        public static void Shutdown()
        {
            CancellationTokenSource cts;
            Task worker;

            lock (_stateLock)
            {
                cts = _cts;
                worker = _worker;
                _cts = null;
                _worker = null;
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

        public static void Suspend(bool suspended)
        {
            _suspended = suspended;
        }

        /// <summary>
        /// Forces an immediate full top-up to target stock. Returns the number of listings added.
        /// </summary>
        public static int ForceTopUp()
        {
            int target = EconomyConfig.ECONOMY_TARGET_STOCK;
            int added = 0;
            int batch = Math.Max(1, EconomyConfig.ECONOMY_BATCH_SIZE);
            int safety = target + batch; // upper bound to avoid infinite loops

            while (TotalListings < target && safety-- > 0)
            {
                int addedThisBatch = GenerateBatch(batch, target - TotalListings);
                if (addedThisBatch == 0)
                    break;
                added += addedThisBatch;
            }

            return added;
        }

        /// <summary>
        /// Removes every bot listing. Useful for /economy clear.
        /// </summary>
        public static int ClearAll()
        {
            int removed = 0;
            lock (_stateLock)
            {
                foreach (var m in _all)
                    removed += m.ClearStock();
            }
            return removed;
        }

        // ---------- internals ----------

        private static void SpawnMerchants()
        {
            int perRealm = Math.Max(1, EconomyConfig.ECONOMY_SELLER_COUNT_PER_REALM);

            CreateRealmMerchants(eRealm.Albion, ALB_LOT_BASE, perRealm);
            CreateRealmMerchants(eRealm.Midgard, MID_LOT_BASE, perRealm);
            CreateRealmMerchants(eRealm.Hibernia, HIB_LOT_BASE, perRealm);
        }

        private static void CreateRealmMerchants(eRealm realm, int lotBase, int count)
        {
            int created = 0;
            int probe = lotBase;
            int safety = lotBase; // upper bound on attempts

            while (created < count && safety-- > 0)
            {
                int lot = probe--;
                // Skip lots that already host a real house (the HouseMgr fallback would
                // otherwise loop back into us; we want to detect real conflicts only).
                if (Housing.HouseMgr.GetHouse(lot) != null)
                    continue;

                string ownerId = $"{OWNER_PREFIX}{realm}:{created:D2}";
                string name = $"{realm} Auction Stall {created + 1}";
                var cm = new EconomyConsignmentMerchant(ownerId, lot, realm, name);
                _byLot[lot] = cm;
                _all.Add(cm);
                created++;
            }

            if (created < count && log.IsWarnEnabled)
                log.Warn($"Economy: could only spawn {created}/{count} sellers for {realm} (no free lots).");
        }

        private static async Task WorkerLoop(CancellationToken token)
        {
            try
            {
                // Initial population - get to target stock as quickly as the throttle allows.
                if (log.IsInfoEnabled)
                    log.Info("Economy: starting initial population...");

                await PopulateToTargetAsync(token).ConfigureAwait(false);

                if (log.IsInfoEnabled)
                    log.Info($"Economy: initial population done. Listings={TotalListings}.");

                // Periodic rotation
                while (!token.IsCancellationRequested)
                {
                    int minutes = Math.Max(1, EconomyConfig.ECONOMY_REFRESH_INTERVAL_MINUTES);
                    try
                    {
                        await Task.Delay(TimeSpan.FromMinutes(minutes), token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) { return; }

                    if (token.IsCancellationRequested)
                        return;
                    if (_suspended)
                        continue;

                    try
                    {
                        await RotateStockAsync(token).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        log.Error("Economy: rotation tick failed.", ex);
                    }
                }
            }
            catch (Exception ex)
            {
                log.Error("Economy: worker loop crashed.", ex);
            }
        }

        private static async Task PopulateToTargetAsync(CancellationToken token)
        {
            int target = EconomyConfig.ECONOMY_TARGET_STOCK;
            int batch = Math.Max(1, EconomyConfig.ECONOMY_BATCH_SIZE);
            int sleep = Math.Max(0, EconomyConfig.ECONOMY_BATCH_SLEEP_MS);

            while (!token.IsCancellationRequested && TotalListings < target)
            {
                int remaining = target - TotalListings;
                int added = GenerateBatch(batch, remaining);
                if (added == 0)
                    break; // no seller capacity / no templates

                if (sleep > 0)
                {
                    try { await Task.Delay(sleep, token).ConfigureAwait(false); }
                    catch (OperationCanceledException) { return; }
                }
            }
        }

        private static async Task RotateStockAsync(CancellationToken token)
        {
            int total = TotalListings;
            if (total == 0)
            {
                await PopulateToTargetAsync(token).ConfigureAwait(false);
                return;
            }

            int pct = Math.Clamp(EconomyConfig.ECONOMY_ROTATION_PERCENT, 1, 100);
            int toRotate = Math.Max(1, total * pct / 100);
            int batch = Math.Max(1, EconomyConfig.ECONOMY_BATCH_SIZE);
            int sleep = Math.Max(0, EconomyConfig.ECONOMY_BATCH_SLEEP_MS);

            if (EconomyConfig.ECONOMY_VERBOSE_LOG && log.IsDebugEnabled)
                log.Debug($"Economy: rotating {toRotate}/{total} listings.");

            int rotated = 0;
            while (!token.IsCancellationRequested && rotated < toRotate)
            {
                int thisRound = Math.Min(batch, toRotate - rotated);

                // Remove old
                for (int i = 0; i < thisRound; i++)
                {
                    var seller = PickSellerWithStock();
                    if (seller == null)
                        break;
                    seller.PopRandomListing();
                }

                // Then top up to target (won't exceed target due to FreeSlots/target cap)
                int remaining = Math.Min(thisRound, EconomyConfig.ECONOMY_TARGET_STOCK - TotalListings);
                if (remaining > 0)
                    GenerateBatch(remaining, remaining);

                rotated += thisRound;

                if (sleep > 0)
                {
                    try { await Task.Delay(sleep, token).ConfigureAwait(false); }
                    catch (OperationCanceledException) { return; }
                }
            }
        }

        private static int GenerateBatch(int batchSize, int globalRemaining)
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
                    continue; // all sellers full for this realm

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
            int wArmor = Math.Max(0, EconomyConfig.ECONOMY_WEIGHT_ARMOR);
            int wWeapon = Math.Max(0, EconomyConfig.ECONOMY_WEIGHT_WEAPON);
            int wJewel = Math.Max(0, EconomyConfig.ECONOMY_WEIGHT_JEWELRY);
            int wCons = Math.Max(0, EconomyConfig.ECONOMY_WEIGHT_CONSUMABLE);

            if (roll <= wArmor) return EconomyItemPool.Category.Armor;
            roll -= wArmor;
            if (roll <= wWeapon) return EconomyItemPool.Category.Weapon;
            roll -= wWeapon;
            if (roll <= wJewel) return EconomyItemPool.Category.Jewelry;
            roll -= wJewel;
            if (roll <= wCons) return EconomyItemPool.Category.Consumable;
            return EconomyItemPool.Category.Resource;
        }

        private static eRealm RollRealm()
        {
            return Util.Random(1, 3) switch
            {
                1 => eRealm.Albion,
                2 => eRealm.Midgard,
                _ => eRealm.Hibernia
            };
        }

        private static DbItemTemplate PickTemplate(eRealm realm, EconomyItemPool.Category cat)
        {
            // Use the realm-specific bucket, falling back only to the shared (realm none)
            // bucket. We deliberately don't cross realms to keep realm-filtered searches honest.
            List<DbItemTemplate> bucket = EconomyItemPool.GetBucket(realm, cat);
            if (bucket != null && bucket.Count > 0)
                return bucket[Util.Random(bucket.Count - 1)];

            bucket = EconomyItemPool.GetBucket(eRealm.None, cat);
            if (bucket != null && bucket.Count > 0)
                return bucket[Util.Random(bucket.Count - 1)];

            return null;
        }

        private static EconomyConsignmentMerchant PickSellerWithCapacity(eRealm realm)
        {
            // Look first inside the realm; otherwise round-robin across all.
            List<EconomyConsignmentMerchant> candidates = new();
            lock (_stateLock)
            {
                foreach (var m in _all)
                {
                    if (m.SellerRealm == realm && m.FreeSlots > 0)
                        candidates.Add(m);
                }

                if (candidates.Count == 0)
                {
                    foreach (var m in _all)
                    {
                        if (m.FreeSlots > 0)
                            candidates.Add(m);
                    }
                }
            }

            if (candidates.Count == 0)
                return null;

            return candidates[Util.Random(candidates.Count - 1)];
        }

        private static EconomyConsignmentMerchant PickSellerWithStock()
        {
            List<EconomyConsignmentMerchant> withStock = new();
            lock (_stateLock)
            {
                foreach (var m in _all)
                {
                    if (m.ItemCount > 0)
                        withStock.Add(m);
                }
            }

            if (withStock.Count == 0)
                return null;

            return withStock[Util.Random(withStock.Count - 1)];
        }

        private static DbInventoryItem BuildListing(DbItemTemplate template, EconomyItemPool.Category category)
        {
            // GameInventoryItem.Create returns a DbInventoryItem-derived object initialized
            // from the template. We make it non-persisted so it lives only in memory until
            // a purchase moves it into a player's inventory (where it will be saved normally).
            GameInventoryItem item;
            try
            {
                item = GameInventoryItem.Create(template);
            }
            catch (Exception ex)
            {
                if (log.IsWarnEnabled)
                    log.Warn($"Economy: failed to construct GameInventoryItem for {template.Id_nb}", ex);
                return null;
            }

            if (item == null)
                return null;

            // Stack size for stackables - market lots feel more "alive" with realistic counts.
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

            int price = EconomyPricing.ComputeSellPrice(template, GetCategoryWeight(category));
            item.SellPrice = price;

            return item;
        }

        private static int GetCategoryWeight(EconomyItemPool.Category category) => category switch
        {
            EconomyItemPool.Category.Armor => EconomyConfig.ECONOMY_WEIGHT_ARMOR,
            EconomyItemPool.Category.Weapon => EconomyConfig.ECONOMY_WEIGHT_WEAPON,
            EconomyItemPool.Category.Jewelry => EconomyConfig.ECONOMY_WEIGHT_JEWELRY,
            EconomyItemPool.Category.Consumable => EconomyConfig.ECONOMY_WEIGHT_CONSUMABLE,
            _ => EconomyConfig.ECONOMY_WEIGHT_RESOURCE
        };
    }
}

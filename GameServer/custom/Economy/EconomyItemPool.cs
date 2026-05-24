using System;
using System.Collections.Generic;
using DOL.Database;
using DOL.Logging;

namespace DOL.GS.Economy
{
    /// <summary>
    /// Loads and caches DbItemTemplate candidates eligible for the bot auction house.
    /// Buckets templates by realm and category for fast weighted random selection.
    /// Loaded once at startup, never modified afterwards (read-only after Build).
    /// </summary>
    public static class EconomyItemPool
    {
        private static readonly Logger log = LoggerManager.Create(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

        public enum Category
        {
            Armor = 0,
            Weapon = 1,
            Jewelry = 2,
            Consumable = 3,
            Resource = 4,
            // Epic: high-utility / artifact-tier items (utility ≥ 80, Bonus ≥ 20, or
            // procs present). Carved out so the bot market can guarantee a steady
            // trickle of "exciting" listings without diluting them in the regular
            // armour/weapon pools. Items keep their original ObjectType for client
            // rendering — only the EconomyManager picker sees this dimension.
            Epic = 5
        }

        public const int CATEGORY_COUNT = 6;

        // [realm 0..3][category 0..5] => list of templates
        private static List<DbItemTemplate>[][] _buckets;

        // Same data sliced by level tier for stratified picks. Tier boundaries are
        // inclusive upper-bounds; a template's tier index is the smallest tier whose
        // upper bound is >= its level (last tier catches anything above MAX_TIER_LEVEL).
        // [realm][category][tier] => list of templates.
        private static List<DbItemTemplate>[][][] _tieredBuckets;
        public static readonly int[] TIER_UPPER_BOUNDS = { 15, 30, 45, 51 };
        public const int TIER_COUNT = 4;

        private static bool _built;

        public static bool Built => _built;

        public static int TotalTemplates { get; private set; }

        public static void Build()
        {
            if (_built)
                return;

            _buckets = new List<DbItemTemplate>[4][];
            _tieredBuckets = new List<DbItemTemplate>[4][][];

            for (int r = 0; r < 4; r++)
            {
                _buckets[r] = new List<DbItemTemplate>[CATEGORY_COUNT];
                _tieredBuckets[r] = new List<DbItemTemplate>[CATEGORY_COUNT][];
                for (int c = 0; c < CATEGORY_COUNT; c++)
                {
                    _buckets[r][c] = new List<DbItemTemplate>();
                    _tieredBuckets[r][c] = new List<DbItemTemplate>[TIER_COUNT];
                    for (int t = 0; t < TIER_COUNT; t++)
                        _tieredBuckets[r][c][t] = new List<DbItemTemplate>();
                }
            }

            int minLevel = Math.Max(1, EconomyConfig.ECONOMY_MIN_LEVEL);
            int maxLevel = Math.Max(minLevel, EconomyConfig.ECONOMY_MAX_LEVEL);

            // One large SELECT; the table is PreCache so this returns quickly.
            IList<DbItemTemplate> all;
            try
            {
                all = GameServer.Database.SelectAllObjects<DbItemTemplate>();
            }
            catch (Exception ex)
            {
                log.Error("EconomyItemPool: failed to load item templates.", ex);
                _built = true;
                return;
            }

            int kept = 0;
            foreach (DbItemTemplate t in all)
            {
                if (t == null || string.IsNullOrEmpty(t.Id_nb))
                    continue;

                // IsTradable is the only hard requirement — the AH cannot trade an item
                // the engine has flagged as bind-on-pickup. IsDropable=false used to be
                // a filter here but it eliminates almost every Epic / instance / quest
                // reward template (the "high-end" items the player is actually looking
                // for), even though those items are perfectly safe to buy off the AH.
                // IsPickable is treated as advisory: most useful items have it set, but
                // some legitimate craft outputs / older templates leave it false.
                if (!t.IsTradable)
                    continue;

                if (t.Level < minLevel || t.Level > maxLevel)
                    continue;

                // Templates with Price=0 (the vast majority of craft outputs and many recipe
                // components) are eligible: EconomyPricing.ComputeBaseValue falls back to a
                // category-aware level formula when basePrice <= 0.

                // ClassTypes with destructive OnReceive (guild banners, mythical bind-on-equip
                // logic, etc.) must never appear in the bot market - they would consume
                // themselves the moment a player buys them.
                if (IsBlockedClassType(t.ClassType))
                    continue;

                Category? cat = ClassifyTemplate(t);
                if (cat == null)
                    continue;

                int realmIndex = ClampRealmIndex(t.Realm);
                int catIndex = (int) cat.Value;
                _buckets[realmIndex][catIndex].Add(t);
                _tieredBuckets[realmIndex][catIndex][TierForLevel(t.Level)].Add(t);

                // High-utility / proc-bearing pieces also get indexed in the Epic
                // bucket so the picker can guarantee a percentage of "exciting"
                // listings (configured via EconomyConfig category weights). Dual-
                // bucketing keeps them visible through the normal category filter
                // too — the Market Explorer still shows them as armour/weapon/etc.
                if (IsEpicCandidate(t))
                {
                    _buckets[realmIndex][(int) Category.Epic].Add(t);
                    _tieredBuckets[realmIndex][(int) Category.Epic][TierForLevel(t.Level)].Add(t);
                }

                kept++;
            }

            TotalTemplates = kept;
            _built = true;

            if (log.IsInfoEnabled)
            {
                log.Info($"EconomyItemPool: loaded {kept} eligible templates from {all.Count} total.");
                string[] realmNames = { "Shared", "Albion", "Midgard", "Hibernia" };
                string[] catNames = { "Armor", "Weapon", "Jewelry", "Consumable", "Resource", "Epic" };
                for (int r = 0; r < 4; r++)
                {
                    var parts = new List<string>(CATEGORY_COUNT);
                    int realmTotal = 0;
                    for (int c = 0; c < CATEGORY_COUNT; c++)
                    {
                        int n = _buckets[r][c].Count;
                        // Epic is dual-indexed so don't double-count toward realmTotal.
                        if (c != (int) Category.Epic)
                            realmTotal += n;
                        parts.Add($"{catNames[c]}={n}");
                    }
                    log.Info($"EconomyItemPool: {realmNames[r]} total={realmTotal} ({string.Join(", ", parts)})");
                }
            }
        }

        /// <summary>
        /// Rebuild the pool from scratch, discarding the cached buckets so a subsequent
        /// Build() picks up new/edited templates. Intended for the /economy rebuild-pool
        /// admin command after a content push.
        /// </summary>
        public static void Reset()
        {
            _built = false;
            _buckets = null;
            _tieredBuckets = null;
            TotalTemplates = 0;
        }

        /// <summary>
        /// A template qualifies for the Epic bucket if it carries a proc/charge, a
        /// notable spellcraft red bonus, or a utility score consistent with high-end
        /// gear. The thresholds are deliberately permissive so the Epic bucket is
        /// well-populated even on light template sets.
        /// </summary>
        private static bool IsEpicCandidate(DbItemTemplate t)
        {
            if (t == null) return false;
            if (t.ProcSpellID > 0 || t.ProcSpellID1 > 0) return true;
            if (t.SpellID > 0 || t.SpellID1 > 0) return true;
            if (t.Bonus >= 20) return true;
            if (t.Quality >= 100) return true;
            double utility = EconomyPricing.ComputeUtility(t);
            return utility >= 80.0;
        }

        public static List<DbItemTemplate> GetBucket(eRealm realm, Category category)
        {
            if (!_built || _buckets == null)
                return null;

            int r = ClampRealmIndex((int) realm);
            return _buckets[r][(int) category];
        }

        public static List<DbItemTemplate> GetTieredBucket(eRealm realm, Category category, int tier)
        {
            if (!_built || _tieredBuckets == null)
                return null;
            if ((uint) tier >= TIER_COUNT)
                return null;

            int r = ClampRealmIndex((int) realm);
            return _tieredBuckets[r][(int) category][tier];
        }

        public static int TierForLevel(int level)
        {
            for (int t = 0; t < TIER_COUNT; t++)
            {
                if (level <= TIER_UPPER_BOUNDS[t])
                    return t;
            }
            return TIER_COUNT - 1;
        }

        public static int CountForRealm(eRealm realm)
        {
            if (!_built || _buckets == null)
                return 0;

            int r = ClampRealmIndex((int) realm);
            int total = 0;
            // Epic is dual-indexed (same templates also live in Armor/Weapon/etc.) so
            // exclude it from the realm-total count to avoid double counting.
            for (int c = 0; c < CATEGORY_COUNT; c++)
            {
                if (c == (int) Category.Epic) continue;
                total += _buckets[r][c].Count;
            }
            return total;
        }

        private static bool IsBlockedClassType(string classType)
        {
            if (string.IsNullOrEmpty(classType))
                return false;
            // Suffix-match on the type name. Templates store fully qualified names like
            // "DOL.GS.GuildBannerItem". Anything whose OnReceive transforms / consumes /
            // bind-on-pickup'd the item belongs here — the moment a player buys such an
            // item off the AH it would fire its handler and disappear or refund nothing.
            string lower = classType.ToLowerInvariant();
            if (lower.EndsWith("guildbanneritem")) return true;
            if (lower.EndsWith("gamesiegeitem"))   return true;
            if (lower.EndsWith("housingitem"))     return true;
            if (lower.EndsWith("houseitem"))       return true;
            // Mythirian items / mounts are filtered by ClassifyTemplate too, but the
            // ClassType filter catches the variants that don't set Item_Type cleanly.
            if (lower.EndsWith("mythirianitem"))   return true;
            if (lower.EndsWith("horseitem"))       return true;
            if (lower.EndsWith("mountitem"))       return true;
            // Items that "open" or "transform" when received — chests, gift packages,
            // teleport stones. Safe heuristic: anything whose class name carries one
            // of these self-consume keywords.
            if (lower.Contains("teleportstone"))   return true;
            if (lower.Contains("teleporter"))      return true;
            if (lower.Contains("giftbox"))         return true;
            if (lower.Contains("loottreasure"))    return true;
            if (lower.Contains("respec"))          return true;
            return false;
        }

        private static int ClampRealmIndex(int realm)
        {
            if (realm == (int) eRealm.Albion)
                return 1;
            if (realm == (int) eRealm.Midgard)
                return 2;
            if (realm == (int) eRealm.Hibernia)
                return 3;
            return 0; // None / shared
        }

        /// <summary>
        /// Decides which category a template belongs to. Returns null for items
        /// that should never appear in the bot market (housing parts, mythirians, etc.).
        /// </summary>
        private static Category? ClassifyTemplate(DbItemTemplate t)
        {
            eObjectType ot = (eObjectType) t.Object_Type;
            eInventorySlot itemType = (eInventorySlot) t.Item_Type;

            // Hard exclusions: housing, siege, mounts, garden, etc.
            if (ot >= eObjectType.GardenObject && ot <= eObjectType.HouseCarpetFourth)
                return null;
            if (ot >= eObjectType.SiegeBalista && ot <= eObjectType.SiegeTrebuchet)
                return null;
            if (itemType == eInventorySlot.Mythical)
                return null;
            if (itemType == eInventorySlot.Horse || itemType == eInventorySlot.HorseArmor || itemType == eInventorySlot.HorseBarding)
                return null;

            // Consumables and ammo
            if (ot == eObjectType.Arrow || ot == eObjectType.Bolt || ot == eObjectType.Poison)
                return Category.Consumable;

            if (ot == eObjectType.Magical)
            {
                // Magical bucket contains potions/charges. Treat as consumable for the market.
                return Category.Consumable;
            }

            // Crafting tinctures / spellcraft gems
            if (ot == eObjectType.AlchemyTincture || ot == eObjectType.SpellcraftGem)
                return Category.Resource;

            // Generic items - usually crafting components / resources
            if (ot == eObjectType.GenericItem)
                return Category.Resource;

            // Jewelry slots
            if (itemType == eInventorySlot.Jewelry || itemType == eInventorySlot.Neck ||
                itemType == eInventorySlot.Cloak || itemType == eInventorySlot.Waist ||
                itemType == eInventorySlot.LeftBracer || itemType == eInventorySlot.RightBracer ||
                itemType == eInventorySlot.LeftRing || itemType == eInventorySlot.RightRing)
                return Category.Jewelry;

            // Armor types
            if (ot >= eObjectType.GenericArmor && ot <= eObjectType.Scale)
                return Category.Armor;

            if (ot == eObjectType.Shield)
                return Category.Armor;

            // Instruments grouped with weapons (DistanceWeapon slot)
            if (ot == eObjectType.Instrument)
                return Category.Weapon;

            // Weapons (covers melee/ranged classes between 1..28 except listed above)
            if (ot >= eObjectType.GenericWeapon && ot <= eObjectType.MaulerStaff)
                return Category.Weapon;

            return null;
        }
    }
}

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
            Resource = 4
        }

        // [realm 0..3][category 0..4] => list of templates
        private static List<DbItemTemplate>[][] _buckets;
        private static bool _built;

        public static bool Built => _built;

        public static int TotalTemplates { get; private set; }

        public static void Build()
        {
            if (_built)
                return;

            _buckets = new List<DbItemTemplate>[4][];

            for (int r = 0; r < 4; r++)
            {
                _buckets[r] = new List<DbItemTemplate>[5];
                for (int c = 0; c < 5; c++)
                    _buckets[r][c] = new List<DbItemTemplate>();
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

                if (!t.IsTradable || !t.IsDropable || !t.IsPickable)
                    continue;

                if (t.Level < minLevel || t.Level > maxLevel)
                    continue;

                if (t.Price <= 0)
                    continue;

                Category? cat = ClassifyTemplate(t);
                if (cat == null)
                    continue;

                int realmIndex = ClampRealmIndex(t.Realm);
                _buckets[realmIndex][(int) cat.Value].Add(t);
                kept++;
            }

            TotalTemplates = kept;
            _built = true;

            if (log.IsInfoEnabled)
                log.Info($"EconomyItemPool: loaded {kept} eligible templates from {all.Count} total.");
        }

        public static List<DbItemTemplate> GetBucket(eRealm realm, Category category)
        {
            if (!_built || _buckets == null)
                return null;

            int r = ClampRealmIndex((int) realm);
            return _buckets[r][(int) category];
        }

        public static int CountForRealm(eRealm realm)
        {
            if (!_built || _buckets == null)
                return 0;

            int r = ClampRealmIndex((int) realm);
            int total = 0;
            for (int c = 0; c < 5; c++)
                total += _buckets[r][c].Count;
            return total;
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

using DOL.GS.ServerProperties;

namespace DOL.GS.Economy
{
    /// <summary>
    /// Server-property bindings for the dynamic auction-house economy module.
    /// Items live in-memory only and are exposed through the existing MarketCache
    /// so they appear in the Market Explorer window.
    /// </summary>
    public static class EconomyConfig
    {
        [ServerProperty("economy", "economy_enabled", "Enable the autonomous dynamic auction-house economy.", true)]
        public static bool ECONOMY_ENABLED;

        [ServerProperty("economy", "economy_target_stock", "Target number of bot-listed items kept in the market at any time.", 10000)]
        public static int ECONOMY_TARGET_STOCK;

        [ServerProperty("economy", "economy_refresh_interval_minutes", "How often the bot market rotates a fraction of its stock (minutes).", 30)]
        public static int ECONOMY_REFRESH_INTERVAL_MINUTES;

        [ServerProperty("economy", "economy_rotation_percent", "Percent of stock replaced at each refresh tick (5 = 5%).", 8)]
        public static int ECONOMY_ROTATION_PERCENT;

        [ServerProperty("economy", "economy_batch_size", "Maximum items inserted per batch when populating the market. Lower to reduce CPU spikes.", 200)]
        public static int ECONOMY_BATCH_SIZE;

        [ServerProperty("economy", "economy_batch_sleep_ms", "Sleep between batches in milliseconds. Prevents long blocking work.", 50)]
        public static int ECONOMY_BATCH_SLEEP_MS;

        [ServerProperty("economy", "economy_min_level", "Lowest item level the bot market lists.", 1)]
        public static int ECONOMY_MIN_LEVEL;

        [ServerProperty("economy", "economy_max_level", "Highest item level the bot market lists.", 51)]
        public static int ECONOMY_MAX_LEVEL;

        // Category weights (sum of weights = relative probability when picking)
        [ServerProperty("economy", "economy_weight_armor", "Selection weight for armor pieces.", 30)]
        public static int ECONOMY_WEIGHT_ARMOR;

        [ServerProperty("economy", "economy_weight_weapon", "Selection weight for weapons.", 25)]
        public static int ECONOMY_WEIGHT_WEAPON;

        [ServerProperty("economy", "economy_weight_jewelry", "Selection weight for jewelry/neck/cloak/waist/bracer/ring.", 15)]
        public static int ECONOMY_WEIGHT_JEWELRY;

        [ServerProperty("economy", "economy_weight_consumable", "Selection weight for consumables (potions, poisons, ammo).", 18)]
        public static int ECONOMY_WEIGHT_CONSUMABLE;

        [ServerProperty("economy", "economy_weight_resource", "Selection weight for crafting resources / materials.", 12)]
        public static int ECONOMY_WEIGHT_RESOURCE;

        // Pricing
        [ServerProperty("economy", "economy_price_min_multiplier", "Lower bound of the random pricing multiplier (percent of base).", 70)]
        public static int ECONOMY_PRICE_MIN_MULTIPLIER;

        [ServerProperty("economy", "economy_price_max_multiplier", "Upper bound of the random pricing multiplier (percent of base).", 150)]
        public static int ECONOMY_PRICE_MAX_MULTIPLIER;

        [ServerProperty("economy", "economy_price_floor_copper", "Hard minimum sell price in copper, regardless of template price.", 100)]
        public static int ECONOMY_PRICE_FLOOR_COPPER;

        [ServerProperty("economy", "economy_seller_count_per_realm", "Number of virtual NPC sellers spawned per realm. Items are spread across them.", 6)]
        public static int ECONOMY_SELLER_COUNT_PER_REALM;

        [ServerProperty("economy", "economy_seller_capacity", "Maximum items a single virtual seller can hold. Should be <= 100 to match consignment slots.", 100)]
        public static int ECONOMY_SELLER_CAPACITY;

        [ServerProperty("economy", "economy_verbose_log", "Verbose logging for the dynamic economy.", false)]
        public static bool ECONOMY_VERBOSE_LOG;
    }
}

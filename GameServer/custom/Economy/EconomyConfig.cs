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

        // Continuous trickle rotation. The worker wakes every TICK_SECONDS and rotates
        // a small slice of stock so the market feels alive at every moment instead of
        // jumping every 30 minutes. Default values: every 60s, ~16%/hour turnover at
        // 10000 stock target = ~27 listings rotated per minute.
        [ServerProperty("economy", "economy_tick_seconds", "How often the rotation worker ticks (seconds). Lower = smoother, slightly higher CPU.", 60)]
        public static int ECONOMY_TICK_SECONDS;

        [ServerProperty("economy", "economy_turnover_percent_per_hour", "Percent of total stock rotated each hour. 16 = ~16% of listings refreshed every hour.", 16)]
        public static int ECONOMY_TURNOVER_PERCENT_PER_HOUR;

        [ServerProperty("economy", "economy_initial_batch_size", "Items inserted per batch during the one-time initial population. Lower to reduce CPU spikes at startup.", 200)]
        public static int ECONOMY_INITIAL_BATCH_SIZE;

        [ServerProperty("economy", "economy_initial_batch_sleep_ms", "Sleep between batches during initial population (ms).", 50)]
        public static int ECONOMY_INITIAL_BATCH_SLEEP_MS;

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

        // ---- Bot-buys-from-player: gives the solo player an actual market to sell into.
        // Time-to-sale is a continuous function of how the player priced the item against
        // the deterministic market value, so cheap items move fast, fair items move within
        // hours-to-days, and overpriced items linger or never sell. See EconomyPricing.
        [ServerProperty("economy", "economy_bot_buys_from_players", "If true, bots periodically purchase player consignment listings using the dynamic price/time curve.", true)]
        public static bool ECONOMY_BOT_BUYS_FROM_PLAYERS;

        [ServerProperty("economy", "economy_fair_price_base_hours", "Mean hours to sale when a listing is priced exactly at market value. Lower = faster economy.", 12)]
        public static int ECONOMY_FAIR_PRICE_BASE_HOURS;

        [ServerProperty("economy", "economy_price_elasticity_x100", "Price-elasticity exponent x100. 450 means T_sale = base * ratio^4.5. Higher = steeper penalty for overpricing.", 450)]
        public static int ECONOMY_PRICE_ELASTICITY_X100;

        [ServerProperty("economy", "economy_hard_max_overprice_percent", "Hard ceiling: listings priced above this percent of market value are never bought. 300 = up to 3x market.", 300)]
        public static int ECONOMY_HARD_MAX_OVERPRICE_PERCENT;

        [ServerProperty("economy", "economy_player_purchase_max_per_tick", "Hard cap on bot purchases of player listings per tick, to prevent burst gold faucets.", 5)]
        public static int ECONOMY_PLAYER_PURCHASE_MAX_PER_TICK;

        [ServerProperty("economy", "economy_player_listings_cache_seconds", "TTL of the cached player-listings scan, used by the bot purchase loop to avoid scanning the entire market every tick.", 300)]
        public static int ECONOMY_PLAYER_LISTINGS_CACHE_SECONDS;

        // ---- Persistence: store bot listings to DB so a server restart loads them from
        // the existing MarketCache instead of regenerating ~10k items. Mutations are
        // batched and flushed asynchronously, so the DB sees a few rows of churn per
        // minute instead of per-rotation thrash.
        [ServerProperty("economy", "economy_persist", "If true, persist bot listings to the Inventory table so they survive server restarts.", true)]
        public static bool ECONOMY_PERSIST;

        [ServerProperty("economy", "economy_db_flush_seconds", "Period between batched DB flushes for bot listings (seconds).", 30)]
        public static int ECONOMY_DB_FLUSH_SECONDS;
    }
}

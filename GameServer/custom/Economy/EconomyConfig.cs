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
        // Bots periodically scan player consignment listings and buy any priced within
        // a fair band of the market value. Player decides the price; bots filter out
        // overpriced items, but pay the player's full asking price when within band.
        [ServerProperty("economy", "economy_bot_buys_from_players", "If true, bots periodically purchase fairly-priced player consignment listings.", true)]
        public static bool ECONOMY_BOT_BUYS_FROM_PLAYERS;

        [ServerProperty("economy", "economy_max_overprice_percent", "Maximum percent of the computed market value a player listing can have to be eligible for bot purchase. 130 = up to 30% above market.", 130)]
        public static int ECONOMY_MAX_OVERPRICE_PERCENT;

        [ServerProperty("economy", "economy_player_purchase_chance_per_hour_percent", "Per-hour percent chance each eligible player listing is bought by a bot. 50 = a fairly-priced item has ~50%/hour of being sold.", 50)]
        public static int ECONOMY_PLAYER_PURCHASE_CHANCE_PER_HOUR_PERCENT;

        [ServerProperty("economy", "economy_player_purchase_max_per_tick", "Hard cap on bot purchases of player listings per tick, to prevent burst gold faucets.", 5)]
        public static int ECONOMY_PLAYER_PURCHASE_MAX_PER_TICK;
    }
}

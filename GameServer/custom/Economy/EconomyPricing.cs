using System;
using DOL.Database;

namespace DOL.GS.Economy
{
    /// <summary>
    /// Computes a dynamic sale price for a market listing. Inputs: template price,
    /// quality, level scaling, magical bonus count, procs, and a per-listing random
    /// multiplier. Prices vary item-to-item without server-wide volatility tracking.
    /// </summary>
    public static class EconomyPricing
    {
        public static int ComputeSellPrice(DbItemTemplate template)
        {
            if (template == null)
                return EconomyConfig.ECONOMY_PRICE_FLOOR_COPPER;

            double finalPrice = ComputeBaseValue(template);

            int minMul = Math.Max(10, EconomyConfig.ECONOMY_PRICE_MIN_MULTIPLIER);
            int maxMul = Math.Max(minMul, EconomyConfig.ECONOMY_PRICE_MAX_MULTIPLIER);
            int rolledMul = Util.Random(minMul, maxMul);
            finalPrice *= rolledMul / 100.0;

            // BP mode: SellPrice is consumed as BountyPoints by ConsignmentState. Divide by
            // the configured gold->BP equivalence (RENT_BOUNTY_POINT_TO_GOLD is "copper per BP",
            // so default 10000 = 1 BP per gold). Floor applies in both modes.
            int floor = Math.Max(1, EconomyConfig.ECONOMY_PRICE_FLOOR_COPPER);
            if (ServerProperties.Properties.CONSIGNMENT_USE_BP)
            {
                long bpDivisor = Math.Max(1, ServerProperties.Properties.RENT_BOUNTY_POINT_TO_GOLD);
                finalPrice /= bpDivisor;
            }

            if (finalPrice < floor)
                finalPrice = floor;

            if (finalPrice > int.MaxValue - 1)
                finalPrice = int.MaxValue - 1;

            return (int) finalPrice;
        }

        /// <summary>
        /// Deterministic market value used as the reference price when judging whether a
        /// player listing is fairly priced. Same formula as ComputeSellPrice MINUS the
        /// random multiplier and the BP conversion - returns copper.
        /// </summary>
        public static int ComputeFairValue(DbItemTemplate template)
        {
            if (template == null)
                return EconomyConfig.ECONOMY_PRICE_FLOOR_COPPER;

            double price = ComputeBaseValue(template);

            // Stack count is multiplied by ComputeSellPrice's callers (item.Count is not
            // factored here); fair value is per-unit. SellPrice on player listings is the
            // listing price for the stack as-is, so callers compare item.SellPrice to
            // fair * item.Count when relevant.

            if (price < EconomyConfig.ECONOMY_PRICE_FLOOR_COPPER)
                price = EconomyConfig.ECONOMY_PRICE_FLOOR_COPPER;
            if (price > int.MaxValue - 1)
                price = int.MaxValue - 1;
            return (int) price;
        }

        private static double ComputeBaseValue(DbItemTemplate template)
        {
            long basePrice = template.Price;
            if (basePrice <= 0)
                basePrice = Math.Max(1, template.Level) * 200L;

            double qualityFactor = 1.0;
            if (template.Quality >= 100)
                qualityFactor = 1.4;
            else if (template.Quality >= 99)
                qualityFactor = 1.25;
            else if (template.Quality >= 95)
                qualityFactor = 1.1;
            else if (template.Quality < 90)
                qualityFactor = 0.85;

            double levelFactor = 0.8 + (template.Level / 50.0) * 0.6;

            int magicalBonuses = CountMagicalBonuses(template);
            double magicalFactor = 1.0 + magicalBonuses * 0.05;

            if (template.ProcSpellID > 0 || template.ProcSpellID1 > 0)
                magicalFactor *= 1.15;
            if (template.SpellID > 0 || template.SpellID1 > 0)
                magicalFactor *= 1.10;

            return basePrice * qualityFactor * levelFactor * magicalFactor;
        }

        /// <summary>
        /// Computes the expected time-to-sale (in seconds) for a player listing given its
        /// asking price vs the deterministic market value. The model is:
        ///     ratio = listed_price / (fair_value * count)
        ///     T_sale = base_hours * ratio^elasticity * 3600
        /// At ratio = 1 (priced at market) T_sale = base_hours, default 12h.
        /// At ratio = 0.5 T_sale plummets to ~30 min; at ratio = 1.5 it stretches to ~70h;
        /// at ratio = 2 it stretches to ~12 days; above the hard ceiling we return -1 to
        /// signal "never bought".
        /// </summary>
        public static double ComputeExpectedSaleSeconds(DbItemTemplate template, long listedPrice, int count)
        {
            if (template == null || listedPrice <= 0 || count <= 0)
                return -1.0;

            long fairUnit = ComputeFairValue(template);
            if (fairUnit <= 0)
                return -1.0;

            // (long) cast prevents int*int overflow when fairUnit is near int.MaxValue
            // and count is a large stack (e.g. resource piles).
            long fairTotal = (long) fairUnit * count;
            if (fairTotal <= 0)
                return -1.0;

            double ratio = (double) listedPrice / fairTotal;

            int hardCeilPct = Math.Max(100, EconomyConfig.ECONOMY_HARD_MAX_OVERPRICE_PERCENT);
            if (ratio * 100.0 >= hardCeilPct)
                return -1.0;

            // Sanity clamps: base time at least 1 hour (was 60, mis-typed - the user
            // sets HOURS, the conversion happens via * 3600); elasticity at least 1.0
            // so doubling the price genuinely doubles (at minimum) the sale time.
            double baseSeconds = Math.Max(1, EconomyConfig.ECONOMY_FAIR_PRICE_BASE_HOURS) * 3600.0;
            double elasticity = Math.Max(100, EconomyConfig.ECONOMY_PRICE_ELASTICITY_X100) / 100.0;

            // Ratio floored at 0.01 to avoid arithmetic underflow; below that the
            // listing is essentially free and sells almost instantly anyway (which is
            // the intended "cheap = ultra-rapide" behavior). The per-stack ECONOMY_PRICE_FLOOR_COPPER
            // gate in PlayerPurchaseTick and the Creator="Auction Market" anti-flip check
            // close the DoS/exploit angles.
            double clampedRatio = Math.Max(0.01, ratio);
            double seconds = baseSeconds * Math.Pow(clampedRatio, elasticity);

            // Anything beyond a year is effectively "never bought" - signal it explicitly.
            const double ONE_YEAR_SECONDS = 365.0 * 24.0 * 3600.0;
            if (seconds > ONE_YEAR_SECONDS)
                return -1.0;
            return seconds;
        }

        private static int CountMagicalBonuses(DbItemTemplate t)
        {
            int n = 0;
            if (t.Bonus1Type != 0 && t.Bonus1 != 0) n++;
            if (t.Bonus2Type != 0 && t.Bonus2 != 0) n++;
            if (t.Bonus3Type != 0 && t.Bonus3 != 0) n++;
            if (t.Bonus4Type != 0 && t.Bonus4 != 0) n++;
            if (t.Bonus5Type != 0 && t.Bonus5 != 0) n++;
            if (t.Bonus6Type != 0 && t.Bonus6 != 0) n++;
            if (t.Bonus7Type != 0 && t.Bonus7 != 0) n++;
            if (t.Bonus8Type != 0 && t.Bonus8 != 0) n++;
            if (t.Bonus9Type != 0 && t.Bonus9 != 0) n++;
            if (t.Bonus10Type != 0 && t.Bonus10 != 0) n++;
            return n;
        }
    }
}

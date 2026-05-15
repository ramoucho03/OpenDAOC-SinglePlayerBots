using System;
using DOL.Database;

namespace DOL.GS.Economy
{
    /// <summary>
    /// Computes a dynamic sale price for a market listing.
    /// The model is deterministic in inputs (template, level, quality, randomness)
    /// but each new listing rolls a fresh random multiplier, so prices vary item-to-item
    /// without server-wide volatility tracking.
    /// </summary>
    public static class EconomyPricing
    {
        /// <summary>
        /// Compute a sale price for a template. Returns a clamped positive int.
        /// </summary>
        public static int ComputeSellPrice(DbItemTemplate template, int categoryWeight)
        {
            if (template == null)
                return EconomyConfig.ECONOMY_PRICE_FLOOR_COPPER;

            long basePrice = template.Price;
            if (basePrice <= 0)
                basePrice = Math.Max(1, template.Level) * 200L;

            // Quality bumps price for high-end pieces.
            double qualityFactor = 1.0;
            if (template.Quality >= 100)
                qualityFactor = 1.4;
            else if (template.Quality >= 99)
                qualityFactor = 1.25;
            else if (template.Quality >= 95)
                qualityFactor = 1.1;
            else if (template.Quality < 90)
                qualityFactor = 0.85;

            // Level scaling - high level items command more, beyond their template price.
            double levelFactor = 0.8 + (template.Level / 50.0) * 0.6;

            // Magical bonus boosts price proportionally to the bonus count.
            int magicalBonuses = CountMagicalBonuses(template);
            double magicalFactor = 1.0 + magicalBonuses * 0.05;

            // Procs / charges premium.
            if (template.ProcSpellID > 0 || template.ProcSpellID1 > 0)
                magicalFactor *= 1.15;
            if (template.SpellID > 0 || template.SpellID1 > 0)
                magicalFactor *= 1.10;

            // Random multiplier - rolled per listing.
            int minMul = Math.Max(10, EconomyConfig.ECONOMY_PRICE_MIN_MULTIPLIER);
            int maxMul = Math.Max(minMul, EconomyConfig.ECONOMY_PRICE_MAX_MULTIPLIER);
            int rolledMul = Util.Random(minMul, maxMul);
            double randomFactor = rolledMul / 100.0;

            double finalPrice = basePrice * qualityFactor * levelFactor * magicalFactor * randomFactor;

            // Cap to int range, apply floor.
            if (finalPrice < EconomyConfig.ECONOMY_PRICE_FLOOR_COPPER)
                finalPrice = EconomyConfig.ECONOMY_PRICE_FLOOR_COPPER;

            // Hard cap at int.MaxValue - 1 (DbInventoryItem.SellPrice is int).
            if (finalPrice > int.MaxValue - 1)
                finalPrice = int.MaxValue - 1;

            return (int) finalPrice;
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

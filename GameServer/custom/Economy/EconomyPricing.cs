using System;
using DOL.Database;

namespace DOL.GS.Economy
{
    /// <summary>
    /// Computes a dynamic sale price for a market listing.
    ///
    /// Formula highlights (rewrite — see commit history for the previous flat model):
    ///   * Quality is a power curve (Q100=1.0, Q90=0.86, Q70=0.58) instead of a 1.4× cap
    ///   * Level is exponential (L1=1.0, L25=3.4, L50≈11.5) — a L50 should not be only
    ///     75 % more valuable than a L1
    ///   * Magical bonuses contribute weighted UTILITY (not just a count) using a stat-
    ///     weight table inspired by DAoC's official utility metric
    ///   * The red Bonus field (SC+0..+35 on spellcrafted pieces) — previously IGNORED —
    ///     now multiplies the price up to 2.4× at +35
    ///   * Weapons and armours get a DPS_AF / SPD_ABS factor so a DPS 16.5 staff is
    ///     valued over a DPS 5.0 staff at the same level
    ///   * Procs/charges are split: ProcSpellID (active proc) is worth more than a
    ///     passive SpellID, ID1 slots add a smaller bonus
    ///   * EconomyManager exposes a per-template demand multiplier; recently-bought
    ///     templates get a soft markup, slow-movers a soft markdown
    ///
    /// Target calibration: a top-tier L50 Q100 SC+35 utility-100 proc weapon hits
    /// approximately 10 platines (~10 000 gold). A trivial L10 crafted piece stays
    /// under a few silver. Knob: ECONOMY_PRICE_GLOBAL_MULTIPLIER (default 400).
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

            // Demand multiplier from the recent-sales tracker. Live signal so popular
            // templates climb and shelf-warmers slide. Bounded 0.7..1.6 inside the
            // tracker so a single hot template can't 10× the market by itself.
            finalPrice *= EconomyManager.GetDemandMultiplier(template.Id_nb);

            // Apply the copper floor BEFORE the BP conversion so the floor is interpreted
            // in copper (its declared unit). Then post-divide for BP mode, with a final
            // floor of 1 BP so trivially cheap items don't round to 0 (ConsignmentState
            // refuses 0-price buys).
            int copperFloor = Math.Max(1, EconomyConfig.ECONOMY_PRICE_FLOOR_COPPER);
            if (finalPrice < copperFloor)
                finalPrice = copperFloor;

            if (ServerProperties.Properties.CONSIGNMENT_USE_BP)
            {
                long bpDivisor = Math.Max(1, ServerProperties.Properties.RENT_BOUNTY_POINT_TO_GOLD);
                finalPrice /= bpDivisor;
                if (finalPrice < 1)
                    finalPrice = 1;
            }

            if (finalPrice > int.MaxValue - 1)
                finalPrice = int.MaxValue - 1;

            return (int) finalPrice;
        }

        /// <summary>
        /// Deterministic market value used as the reference price when judging whether a
        /// player listing is fairly priced. Same formula as ComputeSellPrice MINUS the
        /// random multiplier and the demand multiplier — returns copper.
        /// </summary>
        public static int ComputeFairValue(DbItemTemplate template)
        {
            if (template == null)
                return EconomyConfig.ECONOMY_PRICE_FLOOR_COPPER;

            double price = ComputeBaseValue(template);

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
                basePrice = ComputeFallbackPrice(template);

            // 1. Quality — power curve (Q100=1.0, Q99=0.985, Q95=0.927, Q90=0.86,
            //    Q70=0.58). Items below Q70 should be cheap because they're a
            //    breakage risk and lose stats per repair cycle anyway.
            int quality = Math.Max(1, (int) template.Quality);
            double qualityFactor = Math.Pow(quality / 100.0, 1.5);

            // 2. Level — gentle exponential. 1.05^(L-1) gives 1.0 at L1, 3.4 at L25
            //    and 11.5 at L50. Matches DAoC's empirical "high-level gear costs
            //    20–50× low-level" curve without exploding past L50 (cap at 51).
            int level = Math.Max(1, (int) template.Level);
            level = Math.Min(level, 51);
            double levelFactor = Math.Pow(1.05, level - 1);

            // 3. Utility — weighted sum of bonus magnitudes by stat type. Replaces the
            //    flat "count of non-zero bonuses" of the previous formula, which
            //    over-valued filler bonuses (Str+1) and under-valued power bonuses
            //    (Skill+5 on a damage line, +25 % to a resist).
            double utility = ComputeUtility(template);
            double utilityFactor = 1.0 + Math.Pow(utility / 50.0, 1.8);

            // 4. Red Bonus (SC+0..+35) — previously not read. Each point ≈ +4 %
            //    (cap +140 % at SC+35) because that's roughly the live-market
            //    spread between a 0-bonus craft and a max-bonus craft.
            int redBonus = Math.Max(0, (int) template.Bonus);
            redBonus = Math.Min(redBonus, 35);
            double redBonusFactor = 1.0 + redBonus * 0.04;

            // 5. DPS_AF / SPD_ABS for weapons and armours. DPS_AF is stored ×10 in
            //    the upstream schema (165 = DPS 16.5), so the divisor here is 100.0
            //    intentionally: dps16.5 → 1.0 + 0.165 * 0.5 ≈ 1.08, dps5.0 → 1.025.
            double dpsAfFactor = 1.0;
            if (IsWeapon(template))
                dpsAfFactor = 1.0 + (template.DPS_AF / 100.0) * 0.5;
            else if (IsArmor(template))
                dpsAfFactor = 1.0 + (template.SPD_ABS / 100.0) * 0.3;

            // 6. Procs and charges. Active procs (ProcSpellID) are the headline
            //    feature on rare drops and are valued highest; passive charge spells
            //    (SpellID) less so. ID1 slots stack additively with a smaller bump.
            double procFactor = 1.0;
            if (template.ProcSpellID > 0)  procFactor *= 1.5;
            if (template.ProcSpellID1 > 0) procFactor *= 1.3;
            if (template.SpellID > 0)      procFactor *= 1.2;
            if (template.SpellID1 > 0)     procFactor *= 1.1;

            double value = basePrice
                         * qualityFactor
                         * levelFactor
                         * utilityFactor
                         * redBonusFactor
                         * dpsAfFactor
                         * procFactor;

            int globalMul = Math.Max(10, EconomyConfig.ECONOMY_PRICE_GLOBAL_MULTIPLIER);
            value *= globalMul / 100.0;
            return value;
        }

        /// <summary>
        /// Fallback per-unit copper value when an ItemTemplate has no merchant price,
        /// which is the case for almost all crafted outputs. The per-level rate is chosen
        /// by object/item category so a level-50 cloth piece doesn't end up valued like a
        /// level-50 jewelry. Resources/components stay cheap on purpose so the AH stays
        /// affordable for low-level players sourcing materials.
        /// </summary>
        private static long ComputeFallbackPrice(DbItemTemplate t)
        {
            int level = Math.Max(1, (int) t.Level);
            eObjectType ot = (eObjectType) t.Object_Type;
            eInventorySlot slot = (eInventorySlot) t.Item_Type;

            long perLevel;

            if (slot == eInventorySlot.Jewelry || slot == eInventorySlot.Neck ||
                slot == eInventorySlot.Cloak || slot == eInventorySlot.Waist ||
                slot == eInventorySlot.LeftBracer || slot == eInventorySlot.RightBracer ||
                slot == eInventorySlot.LeftRing || slot == eInventorySlot.RightRing)
            {
                perLevel = 800;
            }
            else if (ot == eObjectType.Arrow || ot == eObjectType.Bolt || ot == eObjectType.Poison)
            {
                perLevel = 50;  // ammo stacks - kept cheap because Count multiplies the total
            }
            else if (ot == eObjectType.Magical)
            {
                perLevel = 300; // potions / charge items
            }
            else if (ot == eObjectType.GenericItem || ot == eObjectType.AlchemyTincture || ot == eObjectType.SpellcraftGem)
            {
                perLevel = 80;  // crafting components
            }
            else if (ot == eObjectType.Instrument)
            {
                perLevel = 700;
            }
            else if (ot == eObjectType.Shield)
            {
                perLevel = 450;
            }
            else if (ot >= eObjectType.GenericArmor && ot <= eObjectType.Scale)
            {
                perLevel = 400;
            }
            else if (ot >= eObjectType.GenericWeapon && ot <= eObjectType.MaulerStaff)
            {
                perLevel = 600;
            }
            else
            {
                perLevel = 200;
            }

            return level * perLevel;
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

        /// <summary>
        /// Sum of |bonus_i| × weight(bonus_type_i) across all 10 magical-bonus slots.
        /// Weights are coarse-grained category buckets (stat / resist / skill / etc.)
        /// rather than the full per-stat table DAoC uses, but it's enough to make a
        /// "Skill+5 on damage line" worth meaningfully more than a "Str+1" bonus.
        /// </summary>
        public static double ComputeUtility(DbItemTemplate t)
        {
            if (t == null) return 0.0;
            double total = 0.0;
            total += BonusUtility(t.Bonus1Type,  t.Bonus1);
            total += BonusUtility(t.Bonus2Type,  t.Bonus2);
            total += BonusUtility(t.Bonus3Type,  t.Bonus3);
            total += BonusUtility(t.Bonus4Type,  t.Bonus4);
            total += BonusUtility(t.Bonus5Type,  t.Bonus5);
            total += BonusUtility(t.Bonus6Type,  t.Bonus6);
            total += BonusUtility(t.Bonus7Type,  t.Bonus7);
            total += BonusUtility(t.Bonus8Type,  t.Bonus8);
            total += BonusUtility(t.Bonus9Type,  t.Bonus9);
            total += BonusUtility(t.Bonus10Type, t.Bonus10);
            return total;
        }

        private static double BonusUtility(int typeRaw, int value)
        {
            if (typeRaw == 0 || value == 0) return 0.0;
            int mag = Math.Abs(value);
            return mag * GetStatWeight((eProperty) typeRaw);
        }

        /// <summary>
        /// Coarse utility weight per eProperty category. Numbers were picked to make
        /// realistic late-game pieces converge on utility ≈ 100–150, which the
        /// utilityFactor curve turns into a ~2.5–3.5× price multiplier — the slice
        /// of the total price model that separates "BiS" from "filler".
        /// </summary>
        private static double GetStatWeight(eProperty prop)
        {
            int p = (int) prop;

            // 1..8 = base stats (Str, Dex, Con, Qui, Int, Pie, Emp, Cha)
            if (p >= 1 && p <= 8) return 0.5;

            // 9 = MaxMana, 10 = MaxHealth — values are in raw points (e.g. +60 HP)
            // so the weight is small per-point to keep totals comparable to stats.
            if (p == 9 || p == 10) return 0.25;

            // 11..19 = Resists (all 9). Each percent of resist is genuinely valuable.
            if (p >= 11 && p <= 19) return 2.0;

            // 20..117 = Skill_First..Skill_Last (spell lines, weapon lines, focus).
            // Each point of skill is worth a chunk of late-game DPS/healing scaling.
            if (p >= 20 && p <= 117) return 5.0;

            switch ((eProperty) p)
            {
                case eProperty.ArmorFactor:        return 0.5;
                case eProperty.ArmorAbsorption:    return 2.0;
                case eProperty.SpellRange:         return 1.0;
                case eProperty.MeleeSpeed:         return 1.5;
                case eProperty.Acuity:             return 0.5;
                case eProperty.FatigueConsumption: return 1.0;
                case eProperty.MeleeDamage:        return 2.0;
                case eProperty.Fatigue:            return 1.0;
                case eProperty.HealingEffectiveness:return 2.0;
                case eProperty.PowerPool:          return 0.25;
                case eProperty.SpellDamage:        return 2.0;
                case eProperty.StyleDamage:        return 2.0;
                case eProperty.PowerPoolCapBonus:  return 0.25;
                case eProperty.MagicAbsorption:    return 2.0;
                case eProperty.StyleAbsorb:        return 1.5;
                default:                            return 1.0;
            }
        }

        private static bool IsWeapon(DbItemTemplate t)
        {
            eObjectType ot = (eObjectType) t.Object_Type;
            if (ot >= eObjectType.GenericWeapon && ot <= eObjectType.MaulerStaff)
                return true;
            if (ot == eObjectType.Instrument)
                return true;
            return false;
        }

        private static bool IsArmor(DbItemTemplate t)
        {
            eObjectType ot = (eObjectType) t.Object_Type;
            if (ot >= eObjectType.GenericArmor && ot <= eObjectType.Scale)
                return true;
            if (ot == eObjectType.Shield)
                return true;
            return false;
        }
    }
}

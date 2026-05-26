using System;

namespace DOL.GS.PropertyCalc
{
    /// <summary>
    /// The power regen rate calculator
    /// 
    /// BuffBonusCategory1 is used for all buffs
    /// BuffBonusCategory2 is used for all debuffs (positive values expected here)
    /// BuffBonusCategory3 unused
    /// BuffBonusCategory4 unused
    /// BuffBonusMultCategory1 unused
    /// </summary>
    [PropertyCalculator(eProperty.PowerRegenerationAmount)]
    public class PowerRegenerationAmountCalculator : PropertyCalculator
    {
        public PowerRegenerationAmountCalculator() { }

        public override int CalcValue(GameLiving living, eProperty property)
        {
            /* PATCH 1.87 COMBAT AND REGENERATION
              - While in combat, health and power regeneration ticks will happen twice as often.
              - Each tick of health and power is now twice as effective.
              - All health and power regeneration aids are now twice as effective.
             */

            // Boosted passive mana regen for single-player play. The old
            // `2.5 + level*0.2` (12.5/tick at L50) was tuned for live-pop where
            // healer downtime is part of the encounter pacing — with mostly
            // mimics around, a solo caster spends too much of their time idle
            // waiting on the bar. Doubled to `5 + level*0.4` (25/tick at L50).
            // The MANA_REGEN_AMOUNT_MODIFIER server property still multiplies
            // on top, and the < 50% list-caster penalty below is unchanged.
            double regen = 5 + living.Level * 0.4;
            int debuff = living.SpecBuffBonusCategory[property];

            if (debuff < 0)
                debuff = -debuff;

            regen += living.BaseBuffBonusCategory[property] + living.AbilityBonus[property] + living.ItemBonus[property] - debuff;

            if (ServerProperties.Properties.MANA_REGEN_AMOUNT_HALVED_BELOW_50_PERCENT &&
                living is GamePlayer player &&
                player.CharacterClass.ClassType is eClassType.ListCaster &&
                player.ManaPercent < 50)
            {
                regen /= 2;
            }

            regen *= ServerProperties.Properties.MANA_REGEN_AMOUNT_MODIFIER;
            return Math.Max(1, (int) regen);
        }
    }
}

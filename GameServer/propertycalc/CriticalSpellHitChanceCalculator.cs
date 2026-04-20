using System;
using DOL.AI.Brain;
using DOL.GS.Scripts;

namespace DOL.GS.PropertyCalc
{
    /// <summary>
    /// The critical hit chance calculator. Returns 0 .. 100 chance.
    /// 
    /// BuffBonusCategory1 unused
    /// BuffBonusCategory2 unused
    /// BuffBonusCategory3 unused
    /// BuffBonusCategory4 unused
    /// BuffBonusMultCategory1 unused
    /// AbilityBonus used
    /// </summary>
    [PropertyCalculator(eProperty.CriticalSpellHitChance)]
    public class CriticalSpellHitChanceCalculator : PropertyCalculator
    {
        public CriticalSpellHitChanceCalculator() { }

        public override int CalcValue(GameLiving living, eProperty property) 
        {
            int chance = living.AbilityBonus[property];

            if (living is IGamePlayer player)
            {
                if (player.CharacterClass.ClassType is eClassType.ListCaster)
                    chance += 10;
            }
            else if (living is NecromancerPet necroPet)
            {
                chance += 10;
                chance += necroPet.Owner.AbilityBonus[property];
            }
            else if (living is GameNPC npc && ServerProperties.Properties.EXPAND_WILD_MINION) // Summoned or Charmed pet.
            {
                if (npc.Brain is IControlledBrain petBrain && petBrain.GetIPlayerOwner() is IGamePlayer playerOwner)
                    chance += playerOwner.GetAbility<RealmAbilities.AtlasOF_WildMinionAbility>()?.Amount ?? 0;
            }

            return Math.Min(chance, 50);
        }
    }
}

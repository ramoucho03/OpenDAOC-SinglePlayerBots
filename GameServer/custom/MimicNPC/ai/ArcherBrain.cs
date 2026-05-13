using DOL.AI.Brain;

namespace DOL.GS.Scripts
{
    public class ArcherBrain : MimicBrain
    {
        // Set to true once we've armed Critical Shot for the next arrow. Cleared
        // once the shot fires (RangeAttackComponent flips it back to Normal).
        private bool _criticalShotArmed;

        public ArcherBrain()
        { }

        public override void OnLeaderAggro()
        {
            Body.Stealth(true);
            TryArmCriticalShotFromStealth();
        }

        public override void OnEnterAggro()
        {
            // First arrow of the engagement: prime Critical Shot if we are
            // stealthed and the ability is off cooldown. The next ranged
            // attack will fire as a critical shot for massive opener damage.
            TryArmCriticalShotFromStealth();
            base.OnEnterAggro();
        }

        public override bool CheckSpells(eCheckSpellType type)
        {
            if (type == eCheckSpellType.Defensive)
            {
                if (base.CheckSpells(type))
                    return true;

                if (Body.Group == null || Body.Group.MimicGroup.CampPoint != null && !MimicBody.MimicBrain.IsMainPuller)
                    Body.Stealth(true);
                else
                    Body.Stealth(false);

                if (Body.ControlledBrain != null && PvPMode)
                    MimicBody.CommandNpcRelease();

                return false;
            }

            return base.CheckSpells(type);
        }

        protected override bool CheckInstantOffensiveSpells(Spell spell)
        {
            if (Body.IsStealthed)
                return false;

            return base.CheckInstantOffensiveSpells(spell);
        }

        /// <summary>
        /// Primes a critical-shot for the next arrow if all the conditions hold:
        /// stealthed, has a target, Critical Shot ability not on cooldown, and a
        /// ranged weapon is equipped. Bots have no SendMessage spam either so we
        /// just flip RangedAttackType directly instead of going through the
        /// player-facing CriticalShotAbilityHandler.
        /// </summary>
        private void TryArmCriticalShotFromStealth()
        {
            if (_criticalShotArmed)
                return;

            if (!Body.IsStealthed)
                return;

            Ability critShot = Body.GetAbility(Abilities.Critical_Shot);
            if (critShot == null || Body.GetSkillDisabledDuration(critShot) > 0)
                return;

            if (Body.Inventory?.GetItem(eInventorySlot.DistanceWeapon) == null)
                return;

            MimicBody.SwitchWeapon(eActiveWeaponSlot.Distance);
            Body.rangeAttackComponent.RangedAttackType = eRangedAttackType.Critical;
            Body.DisableSkill(critShot, 30000);
            _criticalShotArmed = true;
        }
    }
}
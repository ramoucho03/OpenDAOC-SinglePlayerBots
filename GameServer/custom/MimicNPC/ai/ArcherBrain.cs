using DOL.AI.Brain;
using DOL.Database;

namespace DOL.GS.Scripts
{
    public class ArcherBrain : MimicBrain
    {
        // Set to true once we've armed Critical Shot for the next arrow. Cleared
        // once the shot fires (RangeAttackComponent flips it back to Normal).
        private bool _criticalShotArmed;

        // Cached "am I currently flagged as needing the melee weapon?" — gates
        // the per-tick range check in AttackMostWanted so we only call
        // SwitchWeapon on actual transitions (point-blank entry / leaving
        // melee range), not on every Think.
        private bool _meleeFallbackActive;

        // Tick we last decided to skip the close-the-gap follow because we're
        // already inside the comfortable archery sweet-spot (~90% of bow
        // range). Used to avoid recomputing on every tick of a long fight.
        private long _stayAtRangeUntilTick;

        // Bow range is a function of weapon + ammo, but it doesn't change shot-
        // to-shot — cache it for the lifetime of the currently equipped
        // distance weapon to avoid the property walk on every tick.
        private int _cachedBowRange;
        private int _cachedBowRangeForWeaponId;

        public ArcherBrain()
        { }

        private bool UsesArcherProfile => MimicBody?.CombatProfile?.HasRole(eMimicCombatRole.Archer) == true;

        public override void OnLeaderAggro()
        {
            Body.Stealth(true);

            if (UsesArcherProfile)
                TryArmCriticalShotFromStealth();
        }

        public override void OnEnterAggro()
        {
            // First arrow of the engagement: prime Critical Shot.
            //   - From stealth: opener for massive damage (the historical path).
            //   - From range vs an unaware target: also worth a crit-shot, since
            //     the long draw won't be interrupted before it lands.
            if (UsesArcherProfile)
            {
                if (!TryArmCriticalShotFromStealth())
                    TryArmCriticalShotForOpener();
            }

            base.OnEnterAggro();
        }

        public override void OnExitAggro()
        {
            // Combat just ended. If we're an archer in a context that allows
            // stealth (solo / camped / not the active puller), drop back into
            // stealth so the next pull starts with a Critical Shot opener
            // again. CheckSpells(Defensive) also handles this, but it only
            // fires once we're back in CAMP / IDLE — re-stealthing here
            // avoids a visible "stand in the open" window between kills.
            _meleeFallbackActive = false;
            _criticalShotArmed = false;
            _stayAtRangeUntilTick = 0;

            if (UsesArcherProfile && Body.IsAlive && !Body.IsStealthed)
            {
                bool canStealthHere = Body.Group == null
                                      || (Body.Group.MimicGroup?.CampPoint != null && !IsMainPuller);
                if (canStealthHere)
                    Body.Stealth(true);
            }

            base.OnExitAggro();
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
            if (UsesArcherProfile && Body.IsStealthed)
                return false;

            return base.CheckInstantOffensiveSpells(spell);
        }

        /// <summary>
        /// Archer-specific combat tick. Handles two things on top of the base
        /// behaviour, both gated on state changes (not per-tick polling) so the
        /// Think loop stays allocation-free:
        ///
        ///   1. Point-blank melee fallback: when a hostile closes into melee
        ///      range while we're holding the bow, swap to the appropriate
        ///      melee weapon so we stop firing arrows at 0 range (those shots
        ///      either auto-cancel via AimInterrupt OR connect for ~half the
        ///      damage of a styled swing — either way it's a DPS loss).
        ///   2. Stay-at-range: if we're already inside the bow's effective
        ///      sweet-spot (≥ ~75% of max range), stop closing further. The
        ///      vanilla StartAttack path tries to follow to a tight radius;
        ///      for archers that means walking into melee on a moving target.
        /// </summary>
        public override void AttackMostWanted()
        {
            // Non-archer profile (e.g. a Ranger spec'd melee) — keep the
            // standard MimicBrain behaviour with no archer-specific logic.
            if (!UsesArcherProfile)
            {
                _meleeFallbackActive = false;
                base.AttackMostWanted();
                return;
            }

            // Resolve the prospective target once. We only need a distance
            // check; the actual target selection still goes through the base
            // method (CheckMainTankTarget + CalculateNextAttackTarget).
            GameLiving prospective = Body.TargetObject as GameLiving;
            if (prospective == null || !prospective.IsAlive)
            {
                base.AttackMostWanted();
                return;
            }

            // --- (1) Melee fallback transitions ---
            // Cheap squared-distance check — avoids the sqrt in GetDistanceTo
            // and we only need an inequality vs MeleeAttackRange + small
            // hysteresis band so we don't flip-flop on a target jittering on
            // the threshold.
            int meleeReach = Body.MeleeAttackRange;
            int closeBand = meleeReach + 16;      // engage melee at this distance
            int reopenBand = meleeReach + 220;    // re-arm bow when target steps out

            int dx = prospective.X - Body.X;
            int dy = prospective.Y - Body.Y;
            int dz = prospective.Z - Body.Z;
            long distSqr = (long)dx * dx + (long)dy * dy + (long)dz * dz;

            bool hasBow = Body.Inventory?.GetItem(eInventorySlot.DistanceWeapon) != null;
            bool hasMelee = Body.Inventory?.GetItem(eInventorySlot.RightHandWeapon) != null
                            || Body.Inventory?.GetItem(eInventorySlot.TwoHandWeapon) != null
                            || Body.Inventory?.GetItem(eInventorySlot.LeftHandWeapon) != null;

            if (!_meleeFallbackActive)
            {
                // Currently on bow — should we drop to melee?
                if (hasMelee
                    && Body.ActiveWeaponSlot == eActiveWeaponSlot.Distance
                    && distSqr <= (long)closeBand * closeBand)
                {
                    SwitchToMelee();
                    _meleeFallbackActive = true;
                }
            }
            else
            {
                // Currently in melee fallback — should we re-arm the bow?
                if (hasBow && distSqr >= (long)reopenBand * reopenBand)
                {
                    Body.SwitchWeapon(eActiveWeaponSlot.Distance);
                    // The Critical Shot ability is on cooldown after the
                    // opener, so we don't re-arm here — just resume normal
                    // shots at range.
                    _meleeFallbackActive = false;
                }
            }

            // --- (2) Stay-at-range hint ---
            // Only meaningful when we're actually still shooting (bow slot,
            // not in melee fallback). The vanilla StartAttack chases to a
            // tight follow radius on ranged attacks too, which makes archer
            // bots inch forward on every tick. If we're already at ≥ 75% of
            // max bow range, stop following so we hold the line.
            if (!_meleeFallbackActive
                && Body.ActiveWeaponSlot == eActiveWeaponSlot.Distance
                && GameLoop.GameLoopTime >= _stayAtRangeUntilTick)
            {
                int bowRange = GetCachedBowRange();
                if (bowRange > 200)
                {
                    long sweetSpotSqr = (long)(bowRange * 0.75) * (long)(bowRange * 0.75);
                    if (distSqr >= sweetSpotSqr && Body.IsMoving)
                    {
                        // Hold position; the AttackComponent will keep firing
                        // as long as the target stays in range.
                        Body.StopFollowing();
                    }
                    // Re-check at most twice a second to keep the Think loop
                    // cheap — distance only matters on movement transitions
                    // and the FSM will retick us if AttackState changes.
                    _stayAtRangeUntilTick = GameLoop.GameLoopTime + 500;
                }
            }

            base.AttackMostWanted();
        }

        /// <summary>
        /// Swap from bow to the spec-appropriate melee weapon and engage. We
        /// stop the ranged attack first so the AttackAction doesn't immediately
        /// try to re-equip the bow on its next tick (it has a no-interrupt /
        /// not-in-melee-range gate for that path — see MimicAttackAction).
        /// </summary>
        private void SwitchToMelee()
        {
            Body.StopAttack();

            if (MimicBody?.MimicSpec != null && MimicBody.MimicSpec.Is2H
                && Body.Inventory?.GetItem(eInventorySlot.TwoHandWeapon) != null)
            {
                MimicBody.SwitchWeapon(eActiveWeaponSlot.TwoHanded);
            }
            else
            {
                MimicBody.SwitchWeapon(eActiveWeaponSlot.Standard);
            }
        }

        private int GetCachedBowRange()
        {
            DbInventoryItem bow = Body.Inventory?.GetItem(eInventorySlot.DistanceWeapon);
            if (bow == null)
            {
                _cachedBowRange = 0;
                _cachedBowRangeForWeaponId = 0;
                return 0;
            }

            int weaponKey = bow.Id_nb?.GetHashCode() ?? bow.Object_Type;
            if (weaponKey != _cachedBowRangeForWeaponId || _cachedBowRange <= 0)
            {
                // attackComponent.AttackRange reflects the CURRENTLY equipped
                // slot's range, not the bow's. If the archer is in melee mode
                // when this fires (post-fight cleanup, opener evaluation
                // before stealth, etc.) the cached value collapses to the
                // melee reach (~150) and silently breaks every range gate
                // downstream. Briefly swap to Distance, sample, restore.
                eActiveWeaponSlot oldSlot = Body.ActiveWeaponSlot;
                if (oldSlot != eActiveWeaponSlot.Distance)
                    Body.SwitchWeapon(eActiveWeaponSlot.Distance);

                _cachedBowRange = Body.attackComponent?.AttackRange ?? 1500;
                _cachedBowRangeForWeaponId = weaponKey;

                if (oldSlot != eActiveWeaponSlot.Distance)
                    Body.SwitchWeapon(oldSlot);
            }

            return _cachedBowRange;
        }

        /// <summary>
        /// Primes a critical-shot for the next arrow if all the conditions hold:
        /// stealthed, has a target, Critical Shot ability not on cooldown, and a
        /// ranged weapon is equipped. Bots have no SendMessage spam either so we
        /// just flip RangedAttackType directly instead of going through the
        /// player-facing CriticalShotAbilityHandler.
        /// </summary>
        private bool TryArmCriticalShotFromStealth()
        {
            if (_criticalShotArmed)
                return false;

            if (!Body.IsStealthed)
                return false;

            if (!TryArmCriticalShotCore())
                return false;

            return true;
        }

        /// <summary>
        /// Crit-shot for an opener vs an unaware target — not stealthed but
        /// the target hasn't engaged us yet AND we have time to draw without
        /// being interrupted (≥ 60% of bow range). Same arming mechanism as
        /// the stealth opener, different gating.
        /// </summary>
        private bool TryArmCriticalShotForOpener()
        {
            if (_criticalShotArmed)
                return false;

            if (Body.TargetObject is not GameLiving target || !target.IsAlive)
                return false;

            // "Unaware" gate: target isn't actively in combat and doesn't
            // already have us on its aggro list (if it's a mob). Without
            // this we'd burn the long crit-shot cooldown on every shot in
            // a sustained fight.
            if (target.InCombat)
                return false;

            // Range gate: a crit-shot has a ~4–6s draw. If we're not far
            // enough out it'll get interrupted by an approaching melee
            // target and we'd waste the cooldown.
            int bowRange = GetCachedBowRange();
            if (bowRange <= 0)
                return false;

            long minOpenerRangeSqr = (long)(bowRange * 0.6) * (long)(bowRange * 0.6);
            int dx = target.X - Body.X;
            int dy = target.Y - Body.Y;
            long distSqr = (long)dx * dx + (long)dy * dy;
            if (distSqr < minOpenerRangeSqr)
                return false;

            return TryArmCriticalShotCore();
        }

        /// <summary>
        /// Shared arming step for both stealth- and range-opener crit-shots.
        /// Verifies the ability is off cooldown and a bow is equipped, then
        /// flips RangedAttackType to Critical and consumes the cooldown.
        /// </summary>
        private bool TryArmCriticalShotCore()
        {
            Ability critShot = Body.GetAbility(Abilities.Critical_Shot);
            if (critShot == null || Body.GetSkillDisabledDuration(critShot) > 0)
                return false;

            if (Body.Inventory?.GetItem(eInventorySlot.DistanceWeapon) == null)
                return false;

            MimicBody.SwitchWeapon(eActiveWeaponSlot.Distance);
            Body.rangeAttackComponent.RangedAttackType = eRangedAttackType.Critical;
            Body.DisableSkill(critShot, 30000);
            _criticalShotArmed = true;
            return true;
        }
    }
}

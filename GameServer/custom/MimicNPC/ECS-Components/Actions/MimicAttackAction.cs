using DOL.AI.Brain;
using DOL.GS.Keeps;
using DOL.GS.PacketHandler;
using DOL.GS.Scripts;
using DOL.GS.ServerProperties;
using System;
using static DOL.GS.GameObject;
using static DOL.GS.NpcAttackAction;

namespace DOL.GS
{
    public class MimicAttackAction : AttackAction
    {
        private const double TIME_TO_TARGET_THRESHOLD_BEFORE_RANGED_SWITCH = 500; // NPCs will switch to ranged if further than melee range + (this * maxSpeed * 0.001).

        private MimicNPC _mimicOwner;
        private bool _hasLos;
        private CheckLosTimer _checkLosTimer;
        private GameObject _losCheckTarget;
        private bool _wasMeleeWeaponSwitchForced; // Used to prevent NPCs from switching to their ranged weapon automatically if they explicitly switched to a melee weapon during combat.

        private static int LosCheckInterval => Properties.CHECK_LOS_DURING_RANGED_ATTACK_MINIMUM_INTERVAL;

        public MimicAttackAction(MimicNPC owner) : base(owner)
        {
            _mimicOwner = owner;
        }

        protected override bool PrepareMeleeAttack()
        {
            int meleeAttackRange = _mimicOwner.MeleeAttackRange;
            int maxSpeed = _mimicOwner.MaxSpeed;

            if (maxSpeed > 0)
                meleeAttackRange = meleeAttackRange + (int)(TIME_TO_TARGET_THRESHOLD_BEFORE_RANGED_SWITCH * maxSpeed * 0.001);

            // NPCs try to switch to their ranged weapon whenever possible.
            if ((_mimicOwner.CharacterClass.ID == (int)eCharacterClass.Scout ||
                _mimicOwner.CharacterClass.ID == (int)eCharacterClass.Hunter  ||
                _mimicOwner.CharacterClass.ID == (int)eCharacterClass.Ranger) &&
                !_mimicOwner.IsBeingInterrupted &&
                _mimicOwner.Inventory?.GetItem(eInventorySlot.DistanceWeapon) != null &&
                !_mimicOwner.IsWithinRadius(_target, meleeAttackRange) &&
                !_wasMeleeWeaponSwitchForced)
            {
                // But only if there is no timer running or if it has LoS on its current target.
                // If the timer is running, it'll check for LoS continuously.
                if (!Properties.CHECK_LOS_BEFORE_NPC_RANGED_ATTACK || _checkLosTimer == null || !_checkLosTimer.IsAlive)
                {
                    SwitchToRangedAndTick();
                    return false;
                }

                if (_losCheckTarget != _target)
                {
                    _hasLos = false;
                    _checkLosTimer.ChangeTarget(_target);
                }
                else if (_hasLos)
                {
                    SwitchToRangedAndTick();
                    return false;
                }
            }

            _combatStyle = StyleComponent.MimicGetStyleToUse();

            if (!base.PrepareMeleeAttack())
                return false;

            // The target isn't in melee range yet. Check if another target is in range to attack on the way to the main target.
            if (!_mimicOwner.IsWithinRadius(_target, meleeAttackRange) &&
                _mimicOwner.Brain is not IControlledBrain &&
                _mimicOwner.Brain is StandardMobBrain npcBrain)
            {
                _target = npcBrain.LastHighestThreatInAttackRange;

                if (_target == null || !_mimicOwner.IsWithinRadius(_target, meleeAttackRange))
                {
                    _interval = TICK_INTERVAL_FOR_NON_ATTACK;
                    return false;
                }
            }

            return true;
        }

        protected override bool PrepareRangedAttack()
        {
            // TODO: Fix archer LOS
            //if (Properties.CHECK_LOS_BEFORE_NPC_RANGED_ATTACK)
            //{
            //    if (_checkLosTimer == null)
            //        _checkLosTimer = new CheckLosTimer(_mimicOwner, _target, LosCheckCallback);
            //    else if (_losCheckTarget != _target)
            //    {
            //        _hasLos = false;
            //        _checkLosTimer.ChangeTarget(_target);
            //    }

            //    if (!_hasLos)
            //    {
            //        _interval = TICK_INTERVAL_FOR_NON_ATTACK;
            //        return false;
            //    }
            //}
            //else
                _hasLos = true;

            return base.PrepareRangedAttack();
        }

        protected override void PerformRangedAttack()
        {
            _mimicOwner.rangeAttackComponent.RemoveEnduranceAndAmmoOnShot();
            base.PerformRangedAttack();
        }

        public override void OnAimInterrupt(GameObject attacker)
        {
            if (attacker is GameLiving livingAttacker &&
                livingAttacker.ActiveWeaponSlot is not eActiveWeaponSlot.Distance &&
                livingAttacker.IsWithinRadius(_mimicOwner, livingAttacker.attackComponent.AttackRange))
                SwitchToMeleeAndTick();
        }

        public override void OnForcedWeaponSwitch()
        {
            switch (_mimicOwner.ActiveWeaponSlot)
            {
                case eActiveWeaponSlot.Standard:
                case eActiveWeaponSlot.TwoHanded:
                {
                    _wasMeleeWeaponSwitchForced = true;
                    break;
                }
                case eActiveWeaponSlot.Distance:
                {
                    _wasMeleeWeaponSwitchForced = false;
                    break;
                }
            }
        }

        public override bool OnOutOfRangeOrNoLosRangedAttack()
        {
            if (AttackComponent.AttackState && !_hasLos)
            {
                SwitchToMeleeAndTick();
                return true;
            }

            return false;
        }

        private void SwitchToMeleeAndTick()
        {
            if (_mimicOwner.ActiveWeaponSlot is not eActiveWeaponSlot.Distance)
                return;

            _mimicOwner.StartAttackWithMeleeWeapon(_target);
        }

        private void SwitchToRangedAndTick()
        {
            if (_mimicOwner.ActiveWeaponSlot is eActiveWeaponSlot.Distance)
                return;

            _mimicOwner.StartAttackWithRangedWeapon(_target);
        }

        public override void CleanUp()
        {
            if (_checkLosTimer != null)
            {
                _checkLosTimer.Stop();
                _checkLosTimer = null;
            }

            _wasMeleeWeaponSwitchForced = false;
            base.CleanUp();
        }

        private void LosCheckCallback(GamePlayer player, eLosCheckResponse response, ushort sourceOID, ushort targetOID)
        {
            _losCheckTarget = _mimicOwner.CurrentRegion.GetObject(targetOID);

            if (_losCheckTarget == null || _losCheckTarget != _target)
                _hasLos = false;
            else
                _hasLos = response is eLosCheckResponse.TRUE;

            if (!_hasLos)
            {
                OnOutOfRangeOrNoLosRangedAttack();
                return;
            }

            _mimicOwner.TurnTo(_losCheckTarget);
        }

        public class CheckLosTimer : ECSGameTimerWrapperBase
        {
            private GameNPC _mimicOwner;
            private GameObject _target;
            private CheckLosResponse _callback;
            private GamePlayer _losChecker;

            public CheckLosTimer(GameObject owner, GameObject target, CheckLosResponse callback) : base(owner)
            {
                _mimicOwner = owner as GameNPC;
                _callback = callback;
                ChangeTarget(target);
            }

            public void ChangeTarget(GameObject newTarget)
            {
                if (newTarget == null)
                {
                    Stop();
                    return;
                }

                if (_target != newTarget)
                {
                    _target = newTarget;

                    if (_target is GamePlayer targetPlayer)
                        _losChecker = targetPlayer;
                    else if (_target is GameNPC npcTarget && npcTarget.Brain is IControlledBrain targetBrain)
                        _losChecker = targetBrain.GetPlayerOwner();
                }

                // Don't bother starting the timer if there's no one to perform the LoS check.
                if (_losChecker == null)
                {
                    //_callback(null, eLosCheckResponse.TRUE, 0, 0);
                    return;
                }

                if (!IsAlive && _losChecker != null)
                {
                    Start(1);
                    Interval = LosCheckInterval;
                }
            }

            protected override int OnTick(ECSGameTimer timer)
            {
                // We normally rely on `AttackActon.CleanUp()` to stop this timer.
                if (!_mimicOwner.attackComponent.AttackState || _mimicOwner.ObjectState is not eObjectState.Active)
                    return 0;

                _losChecker.Out.SendCheckLos(_mimicOwner, _target, new CheckLosResponse(_callback));
                return LosCheckInterval;
            }
        }
    }
}

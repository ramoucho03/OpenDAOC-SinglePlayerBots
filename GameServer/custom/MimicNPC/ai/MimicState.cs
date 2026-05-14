using System;
using System.Collections.Generic;
using System.Diagnostics.Eventing.Reader;
using System.Linq;
using System.Reflection;
using DOL.Database;
using System.Runtime.InteropServices;
using DOL.GS;
using DOL.GS.Scripts;
using DOL.GS.ServerProperties;

namespace DOL.AI.Brain
{
    public class MimicState : FSMState
    {
        protected static readonly Logging.Logger log = Logging.LoggerManager.Create(MethodBase.GetCurrentMethod().DeclaringType);

        protected MimicBrain _brain = null;

        public bool Init;

        public MimicState(MimicBrain brain) : base()
        {
            _brain = brain;
        }

        public override void Think()
        { }

        public override void Enter()
        { }

        public override void Exit()
        { }

        /// <summary>
        /// Synchronises the bot's sprint state with its leader so the bot can
        /// keep up when the player presses Sprint. Only follows player leaders
        /// (bot leaders never sprint by themselves). Public so it can be called
        /// from <see cref="MimicBrain.Think"/> on every tick, regardless of FSM
        /// state — otherwise a bot in ROAMING / WAKING_UP would fall behind.
        /// </summary>
        public static void MirrorLeaderSprint(MimicBrain brain, GameLiving leader)
        {
            if (brain?.MimicBody == null)
                return;

            MimicNPC body = brain.MimicBody;

            // Pick a reference human player to mirror. The group leader is the
            // first candidate but it's commonly a mimic (when /mgroup builds an
            // all-bot group). In that case fall back to any player in the group
            // — if ANY human in the group sprints, all bots sprint too.
            GamePlayer playerLeader = leader as GamePlayer;
            if (playerLeader == null && body.Group != null)
            {
                foreach (GameLiving gl in body.Group.GetMembersInTheGroup())
                {
                    if (gl is GamePlayer p)
                    {
                        playerLeader = p;
                        break;
                    }
                }
            }

            if (playerLeader == null)
            {
                if (body.IsSprinting)
                    body.Sprint(false);
                return;
            }

            bool leaderSprinting = playerLeader.IsSprinting;
            bool botSprinting = body.IsSprinting;

            if (leaderSprinting)
            {
                // Refill endurance ONLY when the bot is about to drop out of
                // sprint (Sprint effect ends at Endurance <= 5). Refilling on
                // every Think tick flooded the player with Group.UpdateMember
                // packets (one per endurance-percent change × N bots × 2 Hz)
                // which visually corrupted the player's own endurance bar.
                if (body.Endurance < 25)
                    body.Endurance = body.MaxEndurance;

                if (!botSprinting)
                    body.Sprint(true); // checks alive/stealth internally
            }
            else if (botSprinting)
            {
                body.Sprint(false);
            }
        }
    }

    public class MimicState_WakingUp : MimicState
    {
        public MimicState_WakingUp(MimicBrain brain) : base(brain)
        {
            StateType = eFSMStateType.WAKING_UP;
        }

        public override void Think()
        {
            _brain.AlreadyCheckedHeals = false;

            if (!Init)
            {
                _brain.AggroLevel = 100;
                _brain.AggroRange = 3600;

                _brain.PvPMode = _brain.Body.CurrentRegion.IsRvR || _brain.Body.CurrentZone.IsRvR;
                _brain.Roam = true;
                _brain.Defend = false;

                _brain.Body.RoamingRange = 100000;

                //_brain.CheckDefensiveAbilities();
                //_brain.Body.SortSpells();

                Init = true;
            }

            if (_brain.Body.Group != null)
            {
                if (_brain.Body.Group.MimicGroup.CampPoint != null && _brain.Body.IsWithinRadius(_brain.Body.Group?.MimicGroup.CampPoint, 1500))
                {
                    _brain.FSM.SetCurrentState(eFSMStateType.CAMP);
                    return;
                }
                else if (_brain.Body.Group.LivingLeader != _brain.Body)
                {
                    _brain.FSM.SetCurrentState(eFSMStateType.FOLLOW_THE_LEADER);
                    return;
                }
            }

            if (!_brain.Body.attackComponent.AttackState && _brain.Body.CanRoam)
            {
                _brain.FSM.SetCurrentState(eFSMStateType.ROAMING);
                return;
            }

            if (_brain.Body.CanMoveOnPath)
            {
                _brain.FSM.SetCurrentState(eFSMStateType.PATROLLING);
                return;
            }

            if (!_brain.PreventCombat && !_brain.IsHealer)
            {
                if (_brain.CheckProximityAggro(_brain.AggroRange))
                {
                    _brain.FSM.SetCurrentState(eFSMStateType.AGGRO);
                    return;
                }
            }

            _brain.FSM.SetCurrentState(eFSMStateType.IDLE);
            base.Think();
        }
    }

    public class MimicState_Idle : MimicState
    {
        public MimicState_Idle(MimicBrain brain) : base(brain)
        {
            StateType = eFSMStateType.IDLE;
        }

        public override void Enter()
        {
            base.Enter();
        }

        public override void Think()
        {
            _brain.AlreadyCheckedHeals = false;

            _brain.CheckSpells(MimicBrain.eCheckSpellType.Defensive);

            base.Think();
        }
    }

    public class MimicState_FollowLeader : MimicState
    {
        private GameLiving _leader;
        private int _followDistance;
        private int _targetFollowDistance => 80 + _brain.Body.GroupIndex * 20;

        public MimicState_FollowLeader(MimicBrain brain) : base(brain)
        {
            StateType = eFSMStateType.FOLLOW_THE_LEADER;
        }

        public override void Enter()
        {
            if (_brain.Body.Group != null)
            {
                _leader = _brain.Body.Group.LivingLeader;
                _followDistance = _targetFollowDistance;
                _brain.Body.Follow(_brain.Body.Group.LivingLeader, _followDistance, 5000);
            }
            else
                _brain.FSM.SetCurrentState(eFSMStateType.WAKING_UP);

            base.Enter();
        }

        public override void Think()
        {
            _brain.AlreadyCheckedHeals = false;
            if (_brain.CheckHeals())
                return;

            if (_brain.IsMainCC)
                _brain.CheckMainCC();

            if (_brain.Body.Group == null || _leader == _brain.Body)
            {
                _brain.Body.StopFollowing();
                _brain.FSM.SetCurrentState(eFSMStateType.WAKING_UP);

                return;
            }

            if (_leader == null || (_leader != null && _leader.ObjectState != GameObject.eObjectState.Active || !_brain.Body.Group.IsInTheGroup(_leader)))
                _leader = _brain.Body.Group.LivingLeader;

            // Mirror the leader's sprint state so bots can keep up when the player presses Sprint.
            MirrorLeaderSprint(_brain, _leader);

            if (_followDistance != _targetFollowDistance)
            {
                _followDistance = _targetFollowDistance;
                _brain.Body.Follow(_leader, _followDistance, 5000);
            }

            if (!_brain.IsHealer 
                && ((_leader.IsCasting && _leader.castingComponent.SpellHandler.Spell.IsHarmful) || _leader.IsAttacking)
                && _leader.TargetObject is GameLiving livingTarget && _brain.CanAggroTarget(livingTarget))
            {
                _brain.OnLeaderAggro();
                _brain.AddToAggroList(livingTarget, 1);
                return;
            }

            if (_brain.Body.FollowTarget != _leader)
                _brain.Body.Follow(_brain.Body.Group.LivingLeader, _followDistance, 5000);

            if (!_brain.Body.InCombat)
            {
                if (_brain.Body.IsSitting && !_brain.CheckStats(75))
                    _brain.MimicBody.Sit(false);

                if (!_brain.Body.IsSitting && !_brain.Body.IsCasting && !_brain.CheckSpells(MimicBrain.eCheckSpellType.Defensive))
                    _brain.MimicBody.Sit(_brain.CheckStats(75));
            }

            base.Think();
        }

        public override void Exit()
        {
            _brain.Body.StopFollowing();

            _brain.OnExitAggro();

            base.Exit();
        }
    }

    public class MimicState_Aggro : MimicState
    {
        private const int LEAVE_WHEN_OUT_OF_COMBAT_FOR = 10000;
        private long _aggroEndTime;
        private long _checkAggroTime;

        public MimicState_Aggro(MimicBrain brain) : base(brain)
        {
            StateType = eFSMStateType.AGGRO;
        }

        public override void Enter()
        {
            _brain.MimicBody.IsSitting = false;

            _aggroEndTime = GameLoop.GameLoopTime + LEAVE_WHEN_OUT_OF_COMBAT_FOR;
            _checkAggroTime = GameLoop.GameLoopTime;

            _brain.OnEnterAggro();

            base.Enter();
        }

        public override void Exit()
        {
            _brain.Body.StopAttack();
            _brain.Body.StopMoving();
            _brain.Body.StopCurrentSpellcast();
            _brain.ClearAggroList();
            _brain.Body.TargetObject = null;

            _brain.IsFleeing = false;
            _brain.TargetFleePosition = null;
            _brain.ResetFlanking();

            foreach (ECSPulseEffect pulseEffect in _brain.Body.effectListComponent.GetPulseEffects())
            {
                if (pulseEffect.SpellHandler?.Spell != null &&
                    pulseEffect.SpellHandler.Spell.UsePulsePower)
                    pulseEffect.End();
            }

            _brain.OnExitAggro();

            base.Exit();
        }

        public override void Think()
        {
            _brain.AlreadyCheckedHeals = false;

            if (_brain.PvPMode && _checkAggroTime < GameLoop.GameLoopTime)
            {
                _brain.CheckProximityAggro(_brain.AggroRange);
                _checkAggroTime = GameLoop.GameLoopTime + 5000;
                _aggroEndTime = GameLoop.GameLoopTime + LEAVE_WHEN_OUT_OF_COMBAT_FOR;
            }
 
            if (!_brain.HasAggro || (!_brain.Body.InCombatInLast(LEAVE_WHEN_OUT_OF_COMBAT_FOR) && GameServiceUtils.ShouldTick(_aggroEndTime)))
            {
                if (!_brain.Body.IsMezzed && !_brain.Body.IsStunned)
                {
                    if (_brain.PvPMode)
                    {
                        if (_brain.Roam)
                        {
                            if (_brain.Body.Group != null)
                            {
                                if (_brain.Body.Group.LivingLeader == _brain.Body)
                                    _brain.FSM.SetCurrentState(eFSMStateType.ROAMING);
                                else
                                    _brain.FSM.SetCurrentState(eFSMStateType.FOLLOW_THE_LEADER);
                            }
                            else
                                _brain.FSM.SetCurrentState(eFSMStateType.ROAMING);
                        }
                        else if (_brain.Defend)
                        {
                            if (_brain.Body.Group != null)
                            {
                                if (_brain.Body.Group.LivingLeader == _brain.Body)
                                    _brain.FSM.SetCurrentState(eFSMStateType.RETURN_TO_SPAWN);
                                else
                                    _brain.FSM.SetCurrentState(eFSMStateType.FOLLOW_THE_LEADER);
                            }
                            else
                                _brain.FSM.SetCurrentState(eFSMStateType.RETURN_TO_SPAWN);
                        }
                    }
                    else
                    {
                        if (_brain.Body.Group != null)
                        {
                            if (_brain.Body.Group.MimicGroup.CampPoint != null)
                                _brain.FSM.SetCurrentState(eFSMStateType.CAMP);
                            else if (_brain.Body.Group.LivingLeader == _brain.Body)
                                _brain.FSM.SetCurrentState(eFSMStateType.WAKING_UP);
                            else
                                _brain.FSM.SetCurrentState(eFSMStateType.FOLLOW_THE_LEADER);
                        }
                        else
                            _brain.FSM.SetCurrentState(eFSMStateType.WAKING_UP);
                    }

                    return;
                }
            }

            if (_brain.IsMainCC)
                _brain.CheckMainCC();

            if (_brain.IsHealer)
            {
                // Healer survival: if a mob got past the tank, run away before healing.
                _brain.HealerEmergencyFlee();
                _brain.CheckHeals();
            }
            else
                _brain.AttackMostWanted();

            if (_brain.HasAggro && _brain.Body.TargetObject == null && !_brain.Body.IsMoving)
                _aggroEndTime = Math.Min(_aggroEndTime, GameLoop.GameLoopTime + 5000);

            base.Think();
        }
    }

    public class MimicState_Roaming : MimicState
    {
        private long _nextRoamingTick;
        private bool _nextRoamingTickSet;
        protected virtual short Speed => _brain.Body.MaxSpeed;
        protected virtual int MinCooldown => 1;
        protected virtual int MaxCooldown => 5;

        private bool delayRoam;

        public MimicState_Roaming(MimicBrain brain) : base(brain)
        {
            StateType = eFSMStateType.ROAMING;
        }

        public override void Enter()
        {
            if (!_brain.PvPMode)
                _brain.AggroRange = 2000;

            //_nextRoamingTickSet = false;
            _brain.Body.SpawnPoint = new Point3D(_brain.Body.X, _brain.Body.Y, _brain.Body.Z);
            base.Enter();
        }

        public override void Exit()
        {
            base.Exit();
        }

        public override void Think()
        {
            _brain.AlreadyCheckedHeals = false;

            if (_brain.PreventCombat)
                return;

            if (!_brain.HasAggro)
            {
                if (_brain.Body.IsSitting && !_brain.CheckStats(75))
                    _brain.MimicBody.Sit(false);

                delayRoam = _brain.CheckDelayRoam();

                if (delayRoam && _brain.Body.IsDestinationValid)
                {
                    if (_brain.Debug)
                        log.Warn(_brain.Body.Name + " delayRoam && _brain.Body.IsDestinationValid");

                    _brain.Body.StopMoving();
                }
                else if (!delayRoam && !_brain.Body.IsSitting && !_brain.Body.IsMoving && !_brain.Body.movementComponent.HasActiveResetHeadingAction)
                {
                    if (!_nextRoamingTickSet)
                    {
                        _nextRoamingTickSet = true;
                        _nextRoamingTick = GameLoop.GameLoopTime + Util.Random(MinCooldown, MaxCooldown) * 1000;
                        _brain.Body.SpawnPoint = new Point3D(_brain.Body.X, _brain.Body.Y, _brain.Body.Z);
                    }

                    if (GameServiceUtils.ShouldTick(_nextRoamingTick))
                    {
                        // We're not updating `_nextRoamingTick` here because we want it to be set after the NPC stopped moving.
                        _nextRoamingTickSet = false;
                        _brain.Body.Roam(Speed);
                    }
                }

                if (!_brain.PvPMode && delayRoam)
                    return;

                if (_brain.CheckProximityAggro(_brain.AggroRange))
                {
                    _brain.FSM.SetCurrentState(eFSMStateType.AGGRO);
                    return;
                }
            }

            base.Think();
        }
    }

    public class MimicState_Camp : MimicState
    {
        public int AggroRange = 0; // Used to set custom AggroRange
        private int prevAggroRange;
        // Stable per-bot offset from the camp point, picked once and reused
        // for the lifetime of the bot. Previously we rolled a new random
        // offset on every Enter, which caused bots to "shuffle" left/right
        // each time the FSM dipped through camp (aggro→camp→aggro chains),
        // and that constant repositioning interfered with the puller.
        private bool _campOffsetPicked;
        private int _campOffsetX;
        private int _campOffsetY;

        public MimicState_Camp(MimicBrain brain) : base(brain)
        {
            StateType = eFSMStateType.CAMP;
        }

        public override void Enter()
        {
            if (_brain.Body.Group?.MimicGroup.CampPoint == null || !_brain.Body.IsWithinRadius(_brain.Body.Group?.MimicGroup.CampPoint, 1500))
            {
                _brain.FSM.SetCurrentState(eFSMStateType.WAKING_UP);
                return;
            }

            if (!_campOffsetPicked)
            {
                _campOffsetX = _brain.Body.CurrentRegion.IsDungeon ? Util.Random(-50, 50) : Util.Random(-100, 100);
                _campOffsetY = _brain.Body.CurrentRegion.IsDungeon ? Util.Random(-50, 50) : Util.Random(-100, 100);
                _campOffsetPicked = true;
            }

            _brain.Body.SpawnPoint = new Point3D(_brain.Body.Group.MimicGroup.CampPoint);
            _brain.Body.SpawnPoint.X += _campOffsetX;
            _brain.Body.SpawnPoint.Y += _campOffsetY;

            prevAggroRange = _brain.AggroRange;
            _brain.AggroRange = _brain.Body.CurrentRegion.IsDungeon ? 250 : 550;

            // Tanks scan further than the rest of the camp so they spot incoming
            // mobs first and start the intercept run while DPS/healers are still
            // sitting. When the tank engages, OnAttackedByEnemy propagates the
            // aggro through the group within one tick, so the rest catches up
            // without staying alone at camp for long.
            if (_brain.IsMainTank)
                _brain.AggroRange += _brain.Body.CurrentRegion.IsDungeon ? 100 : 250;

            if (AggroRange != 0)
                _brain.AggroRange = AggroRange;

            _brain.ClearAggroList();

            // Only path back to the camp slot if we're actually away from it.
            // ReturnToSpawnPoint on every Enter caused bots in melee range of
            // their slot to keep nudging back to dead centre and looked like
            // they were shuffling sideways.
            if (!_brain.Body.IsWithinRadius(_brain.Body.SpawnPoint, 60))
                _brain.Body.ReturnToSpawnPoint(_brain.Body.MaxSpeed);

            // Clear stale puller state — LastTargetObject, sticky mana throttle,
            // pulling flag — so a returning puller can immediately re-engage.
            _brain.ResetPullerState();
            _brain.PvPMode = false;

            base.Enter();
        }

        public override void Exit()
        {
            _brain.AggroRange = prevAggroRange;
            // Whether the bot leaves camp by aggro, /mfollow, or death, the fire
            // it deployed during the regen break should disappear with the camp.
            _brain.MimicBody?.RemoveCampFire();

            base.Exit();
        }

        public override void Think()
        {
            _brain.AlreadyCheckedHeals = false;
            if (_brain.CheckHeals())
                return;

            if (!_brain.IsPulling && _brain.Body.IsDestinationValid)
                return;

            if (_brain.IsMainPuller)
                _brain.CheckPuller();

            if (_brain.IsMainCC)
                _brain.CheckMainCC();

            // Engage on leader-initiated combat. Without this the camp sits idle
            // until a mob actually lands a hit on a group member (OnAttackedByEnemy
            // is the only other trigger that reaches us at camp because the camp
            // AggroRange is only 250/550). That made bots look broken — they only
            // moved after the puller/leader had already taken damage.
            //
            // Skipped for the puller (still bringing the mob in) and healers (they
            // engage reactively via aggro propagation, not by chasing the leader).
            if (!_brain.IsPulling && !_brain.IsHealer)
            {
                // The group leader is the player in mixed groups, a bot in pure
                // bot groups. LivingLeader is authoritative; MimicGroup.MainLeader
                // is a separate role and may be null.
                GameLiving leader = _brain.Body.Group?.LivingLeader;
                GameLiving leaderTarget = leader?.TargetObject as GameLiving;

                bool leaderEngaging = leader != null
                    && leader != _brain.Body
                    && ((leader.IsCasting && leader.castingComponent?.SpellHandler?.Spell?.IsHarmful == true)
                        || leader.IsAttacking);

                // Cap engagement range so the camp doesn't break formation for a
                // mob the leader pulled three rooms over.
                const int LEADER_ENGAGE_RANGE = 2500;

                if (leaderEngaging
                    && leaderTarget != null
                    && leaderTarget.IsAlive
                    && _brain.CanAggroTarget(leaderTarget)
                    && _brain.Body.IsWithinRadius(leaderTarget, LEADER_ENGAGE_RANGE))
                {
                    _brain.AddToAggroList(leaderTarget, 1);
                    _brain.Body.StopMoving();
                    _brain.FSM.SetCurrentState(eFSMStateType.AGGRO);
                    return;
                }
            }

            if (_brain.CheckProximityAggro(_brain.AggroRange))
            {
                _brain.Body.StopMoving();
                _brain.FSM.SetCurrentState(eFSMStateType.AGGRO);
                return;
            }

            if (!_brain.Body.IsMoving && !_brain.Body.InCombat)
            {
                if (!_brain.CheckSpells(MimicBrain.eCheckSpellType.Defensive))
                    _brain.MimicBody.Sit(_brain.CheckStats(75));

                // Group-level "keep the fire alive" rule: every bot in the camp
                // checks whether at least one group member still owns an active
                // campfire. If none, the first bot that gets here this tick
                // deploys one — and if its fire is destroyed/despawned, the
                // next tick re-deploys. This guarantees ≥1 fire during camp
                // regardless of bot deaths/respawns.
                EnsureGroupHasCampFire(_brain);
            }

            base.Think();
        }

        /// <summary>
        /// Looks through the group's mimic members and ensures at least one of
        /// them owns an active GameStaticItem campfire. If the existing fire
        /// has been removed (object despawned, owner died), the caller deploys
        /// a fresh one. Cheap enough to run every Think tick.
        /// </summary>
        private static void EnsureGroupHasCampFire(MimicBrain brain)
        {
            MimicNPC body = brain?.MimicBody;
            if (body == null || body.Group == null)
                return;

            // Need an actual camp point + we must be close to it. Without this
            // a stray bot far from camp would deploy fires across the zone.
            Point3D camp = body.Group.MimicGroup?.CampPoint;
            if (camp == null)
                return;
            if (!body.IsWithinRadius(camp, 1500))
                return;

            // Already a fire alive somewhere in the group → nothing to do.
            foreach (GameLiving gl in body.Group.GetMembersInTheGroup())
            {
                if (gl is MimicNPC m && m.HasActiveCampFire)
                    return;
            }

            // Drop the fire at the camp point itself so it doesn't shift each
            // time a different bot becomes the deploy candidate.
            body.DeployCampFireAt(camp);
        }
    }

    public class MimicState_ReturnToSpawn : MimicState
    {
        public MimicState_ReturnToSpawn(MimicBrain brain) : base(brain)
        {
            StateType = eFSMStateType.RETURN_TO_SPAWN;
        }

        public override void Enter()
        {

            if (_brain.Body.WasStealthed)
                _brain.Body.Flags |= GameNPC.eFlags.STEALTH;

            _brain.ClearAggroList();
            _brain.Body.ReturnToSpawnPoint(GamePlayer.PLAYER_BASE_SPEED);
            base.Enter();
        }

        public override void Think()
        {
            _brain.AlreadyCheckedHeals = false;
            if (_brain.CheckHeals())
                return;

            if (!_brain.Body.IsNearSpawn &&
                (!_brain.HasAggro || !_brain.Body.IsEngaging) &&
                (!_brain.Body.IsReturningToSpawnPoint) &&
                _brain.Body.CurrentSpeed == 0)
            {
                _brain.FSM.SetCurrentState(eFSMStateType.WAKING_UP);
                _brain.Body.TurnTo(_brain.Body.SpawnHeading);
                return;
            }

            if (_brain.Body.IsNearSpawn)
            {
                _brain.FSM.SetCurrentState(eFSMStateType.WAKING_UP);
                _brain.Body.TurnTo(_brain.Body.SpawnHeading);
                return;
            }

            if (!_brain.PreventCombat && !_brain.IsHealer)
            {
                if (_brain.CheckProximityAggro(_brain.AggroRange))
                {
                    if (_brain.Body.Group != null && _brain.Body.Group.MimicGroup.CampPoint != null)
                        _brain.FSM.SetCurrentState(eFSMStateType.CAMP);

                    _brain.FSM.SetCurrentState(eFSMStateType.AGGRO);
                    return;
                }
            }

            base.Think();
        }
    }

    public class MimicState_Patrolling : MimicState
    {
        public MimicState_Patrolling(MimicBrain brain) : base(brain)
        {
            StateType = eFSMStateType.PATROLLING;
        }

        public override void Enter()
        {
            _brain.Body.MoveOnPath(_brain.Body.MaxSpeed);
            _brain.ClearAggroList();
            base.Enter();
        }

        public override void Think()
        {
            _brain.AlreadyCheckedHeals = false;
            if (_brain.CheckHeals())
                return;

            if (!_brain.PreventCombat && !_brain.IsHealer)
            {
                if (_brain.CheckProximityAggro(_brain.AggroRange))
                {
                    _brain.FSM.SetCurrentState(eFSMStateType.AGGRO);
                    return;
                }
            }

            base.Think();
        }
    }

    public class MimicState_Duel : MimicState
    {
        public MimicState_Duel(MimicBrain brain) : base(brain)
        {
            StateType = eFSMStateType.DUEL;
        }

        public override void Enter()
        {
            _brain.ClearAggroList();

            _brain.MimicBody.IsDuelReady = false;
            _brain.Body.IsSitting = false;
            _brain.AggroLevel = 100;
            _brain.PvPMode = true;
            _brain.AggroRange = 3600;
            _brain.Body.StopMoving();

            base.Enter();
        }

        public override void Think()
        {
            _brain.AlreadyCheckedHeals = false;

            if (!_brain.CheckSpells(MimicBrain.eCheckSpellType.Defensive))
                _brain.MimicBody.IsDuelReady = true;

            if (_brain.MimicBody.DuelPartner != null && _brain.MimicBody.DuelPartner is IGamePlayer gPlayer)
            {
                if (gPlayer.IsDuelReady)
                {
                    _brain.CheckProximityAggro(_brain.AggroRange);
                    _brain.AttackMostWanted();
                }
            }
        }
    }

    public class MimicState_Dead : MimicState
    {
        public MimicState_Dead(MimicBrain brain) : base(brain)
        {
            StateType = eFSMStateType.DEAD;
        }

        public override void Enter()
        {
            _brain.ClearAggroList();
            base.Enter();
        }

        public override void Think()
        {
            _brain.FSM.SetCurrentState(eFSMStateType.WAKING_UP);
            base.Think();
        }
    }
}
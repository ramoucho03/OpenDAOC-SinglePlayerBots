using System;
using System.Collections.Generic;
using System.Diagnostics.Eventing.Reader;
using System.Linq;
using System.Reflection;
using DOL.Database;
using System.Runtime.InteropServices;
using DOL.GS;
using DOL.GS.PacketHandler;
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

            // Rez first — a group member just died on the leader's run and
            // we're still in follow mode. Land the rez before catching up.
            if (_brain.CheckResurrect())
                return;

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

            // Rez beats everything else when a group member is down — even in
            // the middle of combat. An experienced healer/druid drops their
            // current swing/cast to start a rez; the brain mirrors that.
            if (_brain.CheckResurrect())
                return;

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

    /// <summary>
    /// Camp mode 2.0 — a group-aware farming FSM state.
    ///
    /// The camp is treated as a coordinated farming session: the puller draws
    /// mobs, the tank intercepts and locks aggro, the CC mezzes adds before
    /// they reach the line, DPS assist the tank, and healers stay safe behind
    /// the tank. Between pulls everyone sits to regen with a campfire while a
    /// readiness gate (HP/mana) blocks the next pull until the group recovers.
    ///
    /// Phase tracking lives on <see cref="MimicGroup.CampPhase"/> so each bot
    /// can read the current phase instead of re-deriving it. Transitions are
    /// driven exclusively by the puller (or any bot when no puller is set, as
    /// a safety net) so the phase never races with itself across N bots.
    /// </summary>
    public class MimicState_Camp : MimicState
    {
        public int AggroRange = 0; // Used to set custom AggroRange
        private int prevAggroRange;

        // Per-bot stable offset from the camp point. Picked once at first
        // Enter and reused for the lifetime of the bot. Slots are role-aware:
        // tanks take a forward slot, healers a back slot, DPS the sides.
        private bool _campOffsetPicked;
        private int _campOffsetX;
        private int _campOffsetY;

        // Throttle for the per-tick "ensure tank guards the squishy" pass —
        // applying Guard re-checks ability effects, so we rate-limit to ~1Hz.
        private long _nextTankSupportTick;

        // Watchdog: if the puller stays in the Pulling phase for this long
        // without a mob actually engaging the group, the camp force-recovers
        // (resets puller state, ground phase to Regen). Beats permanent stalls
        // caused by LoS misses, pathing dead-ends, or a mob despawn mid-pull.
        private const int PULL_WATCHDOG_MS = 12_000;

        // Cooldown between forced phase recoveries — keeps the watchdog from
        // re-firing every tick if the puller still can't recover.
        private const int WATCHDOG_RETRY_MS = 5_000;
        private long _nextWatchdogTick;

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
                PickCampSlotOffset();
                _campOffsetPicked = true;
            }

            _brain.Body.SpawnPoint = new Point3D(_brain.Body.Group.MimicGroup.CampPoint);
            _brain.Body.SpawnPoint.X += _campOffsetX;
            _brain.Body.SpawnPoint.Y += _campOffsetY;

            prevAggroRange = _brain.AggroRange;
            _brain.AggroRange = _brain.Body.CurrentRegion.IsDungeon ? 250 : 550;

            // Tanks scan further than the rest of the camp so they spot incoming
            // mobs first and start the intercept run while DPS/healers are still
            // sitting.
            if (_brain.IsMainTank)
                _brain.AggroRange += _brain.Body.CurrentRegion.IsDungeon ? 100 : 250;

            // Pullers need a wider local scan to notice the chain candidates as
            // they walk back from a successful shot; without this they wait at
            // the slot for the next CheckPuller tick before re-engaging.
            if (_brain.IsMainPuller)
                _brain.AggroRange = Math.Max(_brain.AggroRange, 900);

            if (AggroRange != 0)
                _brain.AggroRange = AggroRange;

            _brain.ClearAggroList();

            if (!_brain.Body.IsWithinRadius(_brain.Body.SpawnPoint, 60))
                _brain.Body.ReturnToSpawnPoint(_brain.Body.MaxSpeed);

            // Clear stale puller state — LastTargetObject, sticky mana throttle,
            // pulling flag — so a returning puller can immediately re-engage.
            _brain.ResetPullerState();
            _brain.PvPMode = false;

            // Phase reset: entering camp from elsewhere means a fresh regen
            // window. Only the first bot through (or the puller) needs to
            // drive this — calling SetCampPhase from every bot is idempotent.
            MimicGroup mg = _brain.Body.Group?.MimicGroup;
            if (mg != null && mg.CampPhase != MimicGroup.eCampPhase.Regen
                           && mg.CampPhase != MimicGroup.eCampPhase.Ready)
                mg.SetCampPhase(MimicGroup.eCampPhase.Regen);

            base.Enter();
        }

        public override void Exit()
        {
            _brain.AggroRange = prevAggroRange;
            _brain.MimicBody?.RemoveCampFire();

            // Drop tank-side defensive buffs (Guard/Protect) when leaving camp
            // so they get reapplied on the next Enter against whoever is the
            // current squishy.
            _brain.ClearGuardAtCamp();

            base.Exit();
        }

        public override void Think()
        {
            _brain.AlreadyCheckedHeals = false;

            // Rez first — covers the camp scenario where a member died on the
            // last pull and the group is now sitting back down to regen.
            if (_brain.CheckResurrect())
                return;

            if (_brain.CheckHeals())
                return;

            MimicGroup mg = _brain.Body.Group?.MimicGroup;

            // Drive phase transitions from a single bot — by convention the
            // puller (it owns the pull lifecycle). If no puller is set we let
            // the group leader do it so the phase never stalls.
            if (_brain.IsMainPuller || (mg != null && mg.MainPuller == null && _brain.IsMainLeader))
                DriveCampPhase(mg);

            // Aggro check FIRST — never let the "wait while returning to slot"
            // shortcut skip the only line that lets the camp respond to a mob
            // that just walked into our range. Previously this lived BELOW the
            // IsDestinationValid early-return, which meant the bot could go
            // silent for the entire path-back leg even with a mob 200u away.
            if (CheckCampAggroTriggers(mg))
                return;

            // Now it's safe to skip the rest of the tick while we're pathing
            // back to our slot (puller exempt — they still need CheckPuller).
            if (!_brain.IsPulling && _brain.Body.IsDestinationValid)
            {
                // The bot is moving back to its slot — still keep ourselves
                // useful by buffing on the move if applicable.
                _brain.CheckSpells(MimicBrain.eCheckSpellType.Defensive);
                base.Think();
                return;
            }

            if (_brain.IsMainPuller)
                _brain.CheckPuller();

            if (_brain.IsMainCC)
                _brain.CheckMainCC();

            // Tank stays useful between pulls: keep Guard on the most fragile
            // group member so when the next pull lands the protection is
            // already in place.
            if (_brain.IsMainTank && GameLoop.GameLoopTime >= _nextTankSupportTick)
            {
                _nextTankSupportTick = GameLoop.GameLoopTime + 1000;
                _brain.MaintainTankCampSupport();
            }

            if (!_brain.Body.IsMoving && !_brain.Body.InCombat)
            {
                if (!_brain.CheckSpells(MimicBrain.eCheckSpellType.Defensive))
                {
                    // Healers stay standing & alert; everyone else sits to regen.
                    if (_brain.IsHealer)
                        _brain.MimicBody.Sit(false);
                    else
                        _brain.MimicBody.Sit(_brain.CheckStats(75));
                }

                EnsureGroupHasCampFire(_brain);
            }

            base.Think();
        }

        /// <summary>
        /// Centralised aggro/engage check for the camp. Returns true when the
        /// bot transitioned to AGGRO this tick — caller should short-circuit.
        /// Runs in this order:
        ///   1. Bot has aggro of its own (took a hit since last tick).
        ///   2. A group member is engaging the incoming pull (puller has fired).
        ///   3. Leader (player) opened combat manually.
        ///   4. Passive proximity scan.
        /// </summary>
        private bool CheckCampAggroTriggers(MimicGroup mg)
        {
            if (_brain.IsHealer)
                return false; // healers engage reactively via aggro propagation only

            // 1. We already have aggro (took a hit, group member relayed aggro).
            if (_brain.HasAggro)
            {
                _brain.Body.StopMoving();
                _brain.FSM.SetCurrentState(eFSMStateType.AGGRO);
                return true;
            }

            // 2. The puller's mob is on its way and we should pre-engage. The
            //    IncomingPullTarget is set by the puller as soon as the shot
            //    lands; using it here means DPS and tank converge BEFORE the
            //    mob reaches the camp instead of waiting for first blood.
            if (mg != null && mg.IncomingPullTarget is GameLiving incoming
                && incoming.IsAlive
                && incoming.ObjectState == GameObject.eObjectState.Active
                && _brain.CanAggroTarget(incoming))
            {
                // Range to the mob OR to the puller — whichever is closer. We
                // want camp members to react when EITHER is in line of sight,
                // since the mob may be behind a corner while the puller is in
                // the open.
                int distToMob = _brain.Body.GetDistanceTo(incoming);
                int distToPuller = mg.MainPuller != null
                    ? _brain.Body.GetDistanceTo(mg.MainPuller)
                    : int.MaxValue;
                int effective = Math.Min(distToMob, distToPuller);

                const int CAMP_ENGAGE_RANGE = 2500;
                if (effective <= CAMP_ENGAGE_RANGE)
                {
                    _brain.AddToAggroList(incoming, 1);
                    _brain.Body.StopMoving();
                    _brain.FSM.SetCurrentState(eFSMStateType.AGGRO);
                    return true;
                }
            }

            // 3. Leader (player) opened combat on something — DPS/tank/CC
            //    converge. Puller stays put (it has its own pulling work).
            if (!_brain.IsPulling)
            {
                GameLiving leader = _brain.Body.Group?.LivingLeader;
                if (leader != null && leader != _brain.Body)
                {
                    bool leaderEngaging = (leader.IsCasting && leader.castingComponent?.SpellHandler?.Spell?.IsHarmful == true)
                                          || leader.IsAttacking;

                    if (leaderEngaging && leader.TargetObject is GameLiving leaderTarget
                        && leaderTarget.IsAlive
                        && _brain.CanAggroTarget(leaderTarget))
                    {
                        const int LEADER_ENGAGE_RANGE = 2500;
                        if (_brain.Body.IsWithinRadius(leaderTarget, LEADER_ENGAGE_RANGE))
                        {
                            _brain.AddToAggroList(leaderTarget, 1);
                            _brain.Body.StopMoving();
                            _brain.FSM.SetCurrentState(eFSMStateType.AGGRO);
                            return true;
                        }
                    }
                }
            }

            // 4. Passive proximity scan (camp's small AggroRange).
            if (_brain.CheckProximityAggro(_brain.AggroRange))
            {
                _brain.Body.StopMoving();
                _brain.FSM.SetCurrentState(eFSMStateType.AGGRO);
                return true;
            }

            return false;
        }

        /// <summary>
        /// Advances the group-level camp phase based on observable state.
        /// Only one bot runs this per tick (the puller, or the leader as
        /// fallback) so the phase never flaps between bots.
        ///
        /// Transitions:
        ///   Regen     → Ready       : group has recovered HP/mana
        ///   Ready     → Pulling     : puller is in flight (IsPulling=true)
        ///   Pulling   → Engaging    : mob acquired aggro (IncomingPullTarget alive + has aggro)
        ///   Engaging  → Combat      : any group member is InCombat with the target
        ///   Combat    → PostCombat  : no group member InCombat, no aggro list
        ///   PostCombat→ Regen       : group has been idle ≥ 2s
        ///   Watchdog: Pulling stuck > PULL_WATCHDOG_MS → forced Regen + reset puller
        /// </summary>
        private void DriveCampPhase(MimicGroup mg)
        {
            if (mg == null)
                return;

            long now = GameLoop.GameLoopTime;
            MimicGroup.eCampPhase phase = mg.CampPhase;

            // Watchdog: kill stalled pulls.
            if (phase == MimicGroup.eCampPhase.Pulling
                && now - mg.CampPhaseSinceTick > PULL_WATCHDOG_MS
                && now >= _nextWatchdogTick)
            {
                _nextWatchdogTick = now + WATCHDOG_RETRY_MS;
                _brain.ForcePullerRecovery();
                mg.SetCampPhase(MimicGroup.eCampPhase.Regen);
                return;
            }

            bool groupInCombat = AnyGroupMemberInCombat();

            switch (phase)
            {
                case MimicGroup.eCampPhase.Inactive:
                    mg.SetCampPhase(MimicGroup.eCampPhase.Regen);
                    break;

                case MimicGroup.eCampPhase.Regen:
                    if (groupInCombat)
                    {
                        mg.SetCampPhase(MimicGroup.eCampPhase.Combat);
                        break;
                    }
                    if (IsGroupReady(mg))
                        mg.SetCampPhase(MimicGroup.eCampPhase.Ready);
                    break;

                case MimicGroup.eCampPhase.Ready:
                    if (groupInCombat)
                    {
                        mg.SetCampPhase(MimicGroup.eCampPhase.Combat);
                        break;
                    }
                    // Puller is in flight → switch.
                    if (mg.MainPuller is MimicNPC mp && mp.MimicBrain != null && mp.MimicBrain.IsPulling)
                    {
                        mg.SetCampPhase(MimicGroup.eCampPhase.Pulling);
                        break;
                    }
                    // No more ready? drop back to regen so the gate re-evaluates.
                    if (!IsGroupReady(mg))
                        mg.SetCampPhase(MimicGroup.eCampPhase.Regen);
                    break;

                case MimicGroup.eCampPhase.Pulling:
                    if (groupInCombat)
                    {
                        mg.SetCampPhase(MimicGroup.eCampPhase.Combat);
                        break;
                    }
                    if (mg.IncomingPullTarget is GameNPC inc
                        && inc.IsAlive
                        && inc.Brain is StandardMobBrain smb && smb.HasAggro)
                    {
                        mg.SetCampPhase(MimicGroup.eCampPhase.Engaging);
                    }
                    break;

                case MimicGroup.eCampPhase.Engaging:
                    if (groupInCombat)
                        mg.SetCampPhase(MimicGroup.eCampPhase.Combat);
                    else if (mg.IncomingPullTarget == null
                             || !mg.IncomingPullTarget.IsAlive)
                        mg.SetCampPhase(MimicGroup.eCampPhase.PostCombat);
                    break;

                case MimicGroup.eCampPhase.Combat:
                    if (!groupInCombat && !AnyGroupMemberHasAggro())
                        mg.SetCampPhase(MimicGroup.eCampPhase.PostCombat);
                    break;

                case MimicGroup.eCampPhase.PostCombat:
                    if (groupInCombat)
                    {
                        mg.SetCampPhase(MimicGroup.eCampPhase.Combat);
                        break;
                    }
                    // Give a 2s grace period for post-combat loot/buff swaps.
                    if (now - mg.CampPhaseSinceTick > 2000)
                        mg.SetCampPhase(MimicGroup.eCampPhase.Regen);
                    break;
            }
        }

        private bool AnyGroupMemberInCombat()
        {
            if (_brain.Body.Group == null)
                return false;
            foreach (GameLiving gl in _brain.Body.Group.GetMembersInTheGroup())
            {
                if (gl != null && gl.IsAlive && gl.InCombat)
                    return true;
            }
            return false;
        }

        private bool AnyGroupMemberHasAggro()
        {
            if (_brain.Body.Group == null)
                return false;
            foreach (GameLiving gl in _brain.Body.Group.GetMembersInTheGroup())
            {
                if (gl is MimicNPC m && m.MimicBrain != null && m.MimicBrain.HasAggro)
                    return true;
            }
            return false;
        }

        /// <summary>
        /// Group readiness gate. Returns true when the whole group is healthy
        /// enough to pull again — every member must be at ≥ READY_HP_PCT health,
        /// and every caster/healer at ≥ READY_MANA_PCT mana. Endurance is
        /// checked separately (tanks burn it fast on styles).
        /// </summary>
        private bool IsGroupReady(MimicGroup mg)
        {
            const int READY_HP_PCT = 90;
            const int READY_MANA_PCT = 85;
            const int READY_END_PCT = 70;

            if (_brain.Body.Group == null)
                return true;

            foreach (GameLiving gl in _brain.Body.Group.GetMembersInTheGroup())
            {
                if (gl == null || !gl.IsAlive)
                    continue;
                if (gl.HealthPercent < READY_HP_PCT)
                    return false;
                if (gl.MaxMana > 0 && gl.ManaPercent < READY_MANA_PCT)
                    return false;
                if (gl.EndurancePercent < READY_END_PCT)
                    return false;
            }
            return true;
        }

        /// <summary>
        /// Picks the camp-slot offset based on the bot's role. Tanks anchor a
        /// step toward the pull point so they intercept first; healers sit
        /// behind in the back row away from cleaves; CC sits to the side so
        /// it has LoS without taking melee splash; everyone else picks a
        /// scattered slot to the sides.
        /// </summary>
        private void PickCampSlotOffset()
        {
            bool dungeon = _brain.Body.CurrentRegion.IsDungeon;
            int spread = dungeon ? 50 : 100;

            // Default scatter for generic DPS.
            _campOffsetX = Util.Random(-spread, spread);
            _campOffsetY = Util.Random(-spread, spread);

            // Tank forward, healer back, CC offset to a flank — relative to
            // the pull origin so the formation actually faces the threat.
            MimicGroup mg = _brain.Body.Group?.MimicGroup;
            Point2D pullFrom = mg?.PullFromPoint;
            Point3D camp = mg?.CampPoint;
            if (camp == null)
                return;

            // Compute a "forward" unit vector from camp → pull origin (or
            // east by default). Magnitudes work in raw map units; we only
            // need direction.
            double fx = 1, fy = 0;
            if (pullFrom != null)
            {
                double dx = pullFrom.X - camp.X;
                double dy = pullFrom.Y - camp.Y;
                double len = Math.Sqrt(dx * dx + dy * dy);
                if (len > 1) { fx = dx / len; fy = dy / len; }
            }
            // Perpendicular (right-hand) vector.
            double rx = -fy, ry = fx;

            int forward = dungeon ? 80 : 150;
            int side = dungeon ? 60 : 120;

            if (_brain.IsMainTank)
            {
                _campOffsetX = (int)(fx * forward);
                _campOffsetY = (int)(fy * forward);
            }
            else if (_brain.IsHealer)
            {
                _campOffsetX = (int)(-fx * forward);
                _campOffsetY = (int)(-fy * forward);
                // Small random jitter so two healers don't overlap.
                _campOffsetX += Util.Random(-30, 30);
                _campOffsetY += Util.Random(-30, 30);
            }
            else if (_brain.IsMainCC)
            {
                int sign = Util.Random(1) == 0 ? 1 : -1;
                _campOffsetX = (int)(rx * side * sign);
                _campOffsetY = (int)(ry * side * sign);
            }
            else if (_brain.IsMainPuller)
            {
                // Puller sits slightly forward of the line so its first shot
                // doesn't risk clipping a teammate.
                _campOffsetX = (int)(fx * (forward + 40));
                _campOffsetY = (int)(fy * (forward + 40));
            }
        }

        /// <summary>
        /// Looks through the group's mimic members and ensures at least one of
        /// them owns an active GameStaticItem campfire.
        /// </summary>
        private static void EnsureGroupHasCampFire(MimicBrain brain)
        {
            MimicNPC body = brain?.MimicBody;
            if (body == null || body.Group == null)
                return;

            Point3D camp = body.Group.MimicGroup?.CampPoint;
            if (camp == null)
                return;
            if (!body.IsWithinRadius(camp, 1500))
                return;

            foreach (GameLiving gl in body.Group.GetMembersInTheGroup())
            {
                if (gl is MimicNPC m && m.HasActiveCampFire)
                    return;
            }

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

    /// <summary>
    /// Flavor state for capital-city idle bots. The goal is "the city is alive"
    /// without any combat behavior: bots sit/stand cycle, occasionally say
    /// something, drift a few units around their spawn. AggroLevel is set to 0
    /// by the spawner so they won't engage even if attacked — they'd just stand
    /// and take it (consistent with vendor/idle NPCs).
    /// </summary>
    public class MimicState_CityIdle : MimicState
    {
        private long _nextActionTick;

        private static readonly string[] _albionLines =
        {
            "Glory to Albion!",
            "Anyone heading to Camelot Hills?",
            "By Arthur's beard, that was a rough fight.",
            "I hear the frontier is hot again.",
            "Need a smith? I know a good one.",
        };

        private static readonly string[] _midgardLines =
        {
            "For Midgard!",
            "Skol! Long live the king!",
            "By Odin's eye, that was close.",
            "The wolves howl tonight in Yggdra Forest.",
            "Anyone selling iron bars?",
        };

        private static readonly string[] _hiberniaLines =
        {
            "Eriu watches over us.",
            "Anyone heading to Lough Derg?",
            "May the wind be at your back.",
            "I heard the Sidhe were stirring.",
            "Anyone need a few coins lent?",
        };

        public MimicState_CityIdle(MimicBrain brain) : base(brain)
        {
            StateType = eFSMStateType.CITY_IDLE;
        }

        public override void Enter()
        {
            // Idle bots don't fight back. The population manager already sets
            // AggroLevel=0/AggroRange=0 when spawning them, but defensive
            // belt-and-suspenders here in case a script switches the state.
            _brain.AggroLevel = 0;
            _brain.AggroRange = 0;
            _brain.Body.MaxSpeedBase = 0;

            _nextActionTick = GameLoop.GameLoopTime + Util.Random(2000, 6000);
            base.Enter();
        }

        public override void Think()
        {
            // Mimic any rez our group needs even in city — it's a healer behavior.
            if (_brain.CheckResurrect())
                return;

            // Cheap throttle: most ticks this state should be a no-op so 30
            // capital bots cost almost nothing.
            if (GameLoop.GameLoopTime < _nextActionTick)
                return;

            _nextActionTick = GameLoop.GameLoopTime + Util.Random(15_000, 45_000);

            int roll = Util.Random(0, 99);
            if (roll < 25)
                ToggleSit();
            else if (roll < 50)
                PerformRandomEmote();
            else if (roll < 70)
                SayRandomLine();
            // 30% chance the bot just stands there this cycle — keeps the
            // crowd from feeling like a perfectly choreographed sketch.
        }

        private void ToggleSit()
        {
            if (_brain.MimicBody == null)
                return;

            // Sit/stand cycle. MimicNPC.Sit handles the underlying flags.
            _brain.MimicBody.Sit(!_brain.MimicBody.IsSitting);
        }

        private void PerformRandomEmote()
        {
            // Subset of player-style emotes that look natural in a town.
            eEmote[] options = { eEmote.Yes, eEmote.No, eEmote.Wave, eEmote.Laugh, eEmote.Cheer, eEmote.Clap, eEmote.Shrug };
            _brain.Body.Emote(options[Util.Random(0, options.Length - 1)]);
        }

        private void SayRandomLine()
        {
            string[] lines = _brain.Body.Realm switch
            {
                eRealm.Albion   => _albionLines,
                eRealm.Midgard  => _midgardLines,
                eRealm.Hibernia => _hiberniaLines,
                _               => _albionLines,
            };

            _brain.Body.Say(lines[Util.Random(0, lines.Length - 1)]);
        }
    }
}
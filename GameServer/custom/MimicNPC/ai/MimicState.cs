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
        /// A grouped non-leader bot belongs in FOLLOW_THE_LEADER (or CAMP when
        /// a camp anchor is set). The wander states — Roaming, Idle,
        /// Patrolling, ReturnToSpawn — have no group→follow transition of
        /// their own, so a bot that lands in one of them while grouped
        /// (summoned-then-invited race, a combat-end transition, a manual
        /// state poke) would never start following and would drift away until
        /// the recall teleports it. Call this at the top of those states'
        /// Think(); returns true when a transition was issued (caller must
        /// then return immediately).
        /// </summary>
        protected bool TryFollowGroupLeader()
        {
            Group group = _brain?.Body?.Group;
            if (group == null)
                return false;
            if (group.LivingLeader == _brain.Body)
                return false; // we ARE the leader — nothing to follow

            DOL.GS.Scripts.MimicGroup mimicGroup = group.MimicGroup;
            if (mimicGroup?.CampPoint != null
                && _brain.Body.IsWithinRadius(mimicGroup.CampPoint, 1500))
            {
                _brain.FSM.SetCurrentState(eFSMStateType.CAMP);
                return true;
            }

            _brain.FSM.SetCurrentState(eFSMStateType.FOLLOW_THE_LEADER);
            return true;
        }

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
                // Cache lookup: searching the group for a player every tick
                // (Group.GetMembersInTheGroup takes a lock and allocates a
                // pooled list copy) was a measurable overhead with 8+ bots.
                // Re-resolve every 2 seconds, or sooner if the cached player
                // is no longer eligible (left group, died, changed region).
                long now = GameLoop.GameLoopTime;
                GamePlayer cached = brain.CachedPlayerLeader;
                bool cacheValid = cached != null
                    && cached.IsAlive
                    && cached.Group == body.Group
                    && cached.CurrentRegion == body.CurrentRegion
                    && now < brain.CachedPlayerLeaderExpireTick;

                if (cacheValid)
                {
                    playerLeader = cached;
                }
                else
                {
                    foreach (GameLiving gl in body.Group.GetMembersInTheGroup())
                    {
                        if (gl is GamePlayer p)
                        {
                            playerLeader = p;
                            break;
                        }
                    }
                    brain.CachedPlayerLeader = playerLeader;
                    brain.CachedPlayerLeaderExpireTick = now + 2000;
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
                // No more brute-force refill here. The endurance math in
                // MimicNPC.EnduranceRegenerationTimerCallback now mirrors the
                // leader's potion + Long Wind context, so a player running
                // Endurance Regen potion + Long Wind RA keeps their bots in
                // sprint indefinitely without us forcibly resetting the bar
                // every tick (which used to flood the group with
                // Group.UpdateMember packets and visibly corrupt the player's
                // own endurance UI). When the leader has no sprint buffs the
                // bot now drains realistically and eventually falls behind,
                // exactly like a real human groupmate would.

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

        public override void Enter()
        {
            // Re-arm the one-shot Init block on every entry. The FSM state
            // instances are singletons that survive bot death, region
            // transitions, and manual /reset; without this reset the
            // aggro/range/PvP-mode snapshot taken on the very first wake-up
            // would stick forever even after the bot rezzes in a new region
            // (PvE → RvR teleport, dungeon → outdoor, etc.) which left bots
            // with stale PvP flags and the wrong aggro radius for their new
            // surroundings.
            Init = false;
            base.Enter();
        }

        public override void Think()
        {
            _brain.AlreadyCheckedHeals = false;

            // Rez before anything else — the bot might be waking up after
            // a group wipe, with corpses already on the ground.
            if (_brain.CheckResurrect())
                return;

            if (!Init)
            {
                _brain.AggroLevel = 100;
                _brain.AggroRange = 3600;

                // Snapshot region/zone in case the body is being moved between
                // worlds during the same tick — both can be transiently null.
                Region region = _brain.Body.CurrentRegion;
                Zone zone = _brain.Body.CurrentZone;
                _brain.PvPMode = (region != null && region.IsRvR) || (zone != null && zone.IsRvR);
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

            // Rez before everything else — a dead group member dropped on
            // the last fight and the bot just settled into idle without
            // anyone clearing the corpse. Without this call site, a healer
            // who finished combat into Idle would never rez fallen members.
            if (_brain.CheckResurrect())
                return;

            // A grouped non-leader bot must follow, not idle in place.
            if (TryFollowGroupLeader())
                return;

            // Idle still means "no current threat" but we DO want to react if
            // a group member opens fire on something — without this, a bot
            // sitting in IDLE (camp without a CampPoint, or a holding bot)
            // would never join its group's fights until melee-touched.
            if (!_brain.IsHealer && _brain.ScanGroupCombat())
            {
                _brain.FSM.SetCurrentState(eFSMStateType.AGGRO);
                return;
            }

            _brain.CheckSpells(MimicBrain.eCheckSpellType.Defensive);

            base.Think();
        }
    }

    public class MimicState_FollowLeader : MimicState
    {
        private GameLiving _leader;
        private int _followDistance;
        private int _targetFollowDistance => 80 + _brain.Body.GroupIndex * 20;

        // Stuck recovery — record the body's last X/Y and the tick it was
        // sampled. If the body hasn't moved meaningfully in STUCK_GRACE_MS
        // while it should still be following, side-step or teleport.
        private int _lastStuckX;
        private int _lastStuckY;
        private long _lastStuckSampleTick;
        private long _lastRecallTick;
        private const int STUCK_GRACE_MS = 3000;
        private const int STUCK_MIN_DELTA_SQ = 60 * 60;        // 60 units = roughly half a step
        private const int DISTANCE_OVERFLOW = 3500;            // beyond this we teleport
        private const int RECALL_COOLDOWN_MS = 5000;           // don't spam recalls back-to-back

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

            // Prime the stuck-recovery baseline so the very first Think after
            // Enter doesn't see a 0-tick timestamp and immediately fire a
            // spurious perpendicular side-step (GameLoopTime - 0 is always
            // larger than STUCK_GRACE_MS). Without this the bot would jitter
            // on every entry into FOLLOW_THE_LEADER.
            _lastStuckX = _brain.Body.X;
            _lastStuckY = _brain.Body.Y;
            _lastStuckSampleTick = GameLoop.GameLoopTime;

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

            // Region/distance recovery: if the leader zoned or wandered way
            // beyond the visible follow range, teleport on top of them rather
            // than running into a wall forever. Cooldown prevents back-to-back
            // recall churn during a long zone-change handshake.
            if (_leader != null && GameLoop.GameLoopTime - _lastRecallTick > RECALL_COOLDOWN_MS)
            {
                bool regionMismatch = _brain.Body.CurrentRegionID != _leader.CurrentRegionID;
                bool tooFar = _brain.Body.GetDistanceTo(_leader) > DISTANCE_OVERFLOW;
                if (regionMismatch || tooFar)
                {
                    _brain.Body.MoveTo(_leader.CurrentRegionID, _leader.X, _leader.Y, _leader.Z, _leader.Heading);
                    _lastRecallTick = GameLoop.GameLoopTime;
                    _lastStuckSampleTick = GameLoop.GameLoopTime; // reset stuck baseline post-recall
                    _lastStuckX = _brain.Body.X;
                    _lastStuckY = _brain.Body.Y;
                    return;
                }
            }

            // Stuck recovery: if we should be moving (FollowTarget set, not in
            // melee range yet) but the body hasn't budged for 3s, side-step.
            // Avoids the classic "stuck on a wall corner" follow death.
            if (_leader != null && _brain.Body.FollowTarget == _leader && !_brain.Body.IsWithinRadius(_leader, _followDistance + 100))
            {
                int dx = _brain.Body.X - _lastStuckX;
                int dy = _brain.Body.Y - _lastStuckY;
                if (dx * dx + dy * dy >= STUCK_MIN_DELTA_SQ)
                {
                    _lastStuckX = _brain.Body.X;
                    _lastStuckY = _brain.Body.Y;
                    _lastStuckSampleTick = GameLoop.GameLoopTime;
                }
                else if (GameLoop.GameLoopTime - _lastStuckSampleTick > STUCK_GRACE_MS)
                {
                    // Unstick: nudge perpendicular to the leader bearing by ~150 units.
                    // The follow goal will re-acquire next tick.
                    double heading = _brain.Body.GetAngle(_leader) * Math.PI / 180.0;
                    int sx = _brain.Body.X + (int)(150 * Math.Cos(heading + Math.PI / 2));
                    int sy = _brain.Body.Y + (int)(150 * Math.Sin(heading + Math.PI / 2));
                    _brain.Body.WalkTo(new Point3D(sx, sy, _brain.Body.Z), _brain.Body.MaxSpeed);
                    _lastStuckSampleTick = GameLoop.GameLoopTime;
                    _lastStuckX = sx;
                    _lastStuckY = sy;
                }
            }

            // Sprint mirroring is centralised in MimicBrain.Think() so it runs
            // regardless of FSM state (ROAMING / WAKING_UP / AGGRO chases keep
            // up too). Calling it here again would just duplicate the work.

            if (_followDistance != _targetFollowDistance)
            {
                _followDistance = _targetFollowDistance;
                _brain.Body.Follow(_leader, _followDistance, 5000);
            }

            // Group-combat assist: react when ANY group member (player or
            // mimic) is engaged or being attacked — not just the leader. The
            // legacy leader-only check missed every secondary engagement
            // (a second player in the group pulling, a mimic taking aggro
            // from a wandering mob, etc.) and left non-tank bots dormant
            // until the mob actually melee-touched them.
            // Healers stay in FollowLeader on purpose — their heal cycle
            // runs from this state via the strategy framework, which
            // continuously polls group HP and casts heals without needing
            // an AGGRO target. Forcing them into AGGRO breaks the heal
            // dispatch (AGGRO state expects a combat target the healer
            // doesn't have).
            if (!_brain.IsHealer && _brain.ScanGroupCombat())
            {
                _brain.OnLeaderAggro();
                _brain.FSM.SetCurrentState(eFSMStateType.AGGRO);
                return;
            }

            if (_brain.Body.FollowTarget != _leader)
                _brain.Body.Follow(_brain.Body.Group.LivingLeader, _followDistance, 5000);

            if (!_brain.Body.InCombat)
            {
                // A bot in FOLLOW must NEVER sit.
                //
                // The old sit-to-regen logic here was the root cause of the
                // "bot flickers / resets / TPs every ~6s, even when the player
                // stands still" bug. Mechanism:
                //   - CheckStats(75) returns true the instant ANY stat (HP,
                //     mana OR endurance) dips below 75%. A caster's mana is
                //     almost always below 75% out of combat, so the bot was
                //     perpetually "eligible to rest".
                //   - It sat down. The IsSitting setter cancels the current
                //     spellcast; Sit() fires an emote and stops attack/sprint.
                //   - Next tick it stood to recast / because a stat crossed
                //     the single 75% threshold, then sat again — an endless
                //     sit/stand/emote/cast-cancel thrash on the ~6s regen
                //     cadence that the player sees as the bot bugging out.
                //   - While seated the bot cannot move, so the moment the
                //     player walked it fell behind and got teleport-recalled.
                //
                // A following group member simply stands and keeps up.
                // Resting belongs to the CAMP state, which is a stable
                // dedicated rest state with no oscillation.
                if (_brain.Body.IsSitting)
                    _brain.MimicBody.Sit(false);

                // Still keep self-buffs topped up while following — this only
                // actually casts when a buff has genuinely expired (minutes
                // timescale), so it cannot oscillate.
                if (!_brain.Body.IsSitting && !_brain.Body.IsCasting)
                    _brain.CheckSpells(MimicBrain.eCheckSpellType.Defensive);
            }

            base.Think();
        }

        public override void Exit()
        {
            // Always stand on leaving FOLLOW — a bot that exits this state
            // while still seated (e.g. straight into AGGRO) would otherwise
            // be frozen sitting in combat.
            if (_brain.Body.IsSitting)
                _brain.MimicBody.Sit(false);

            _brain.Body.StopFollowing();

            // NOTE: do NOT call OnExitAggro() here. FollowLeader is the
            // out-of-combat travel state — calling the aggro-exit hook on
            // every Follow exit (e.g. WAKING_UP → FOLLOW → AGGRO transitions)
            // would fire teardown logic for an aggro session that never
            // started, clobbering flank state / flee target / pulse effects
            // owned by the AGGRO state. MimicState_Aggro.Exit is the only
            // legitimate place to invoke OnExitAggro.

            base.Exit();
        }
    }

    public class MimicState_Aggro : MimicState
    {
        // Out-of-combat window before leaving the aggro state. The original 10s
        // value made bots stand around for 10s after every kill before resuming
        // follow / roam — players resume in 1-2s. 3s keeps a small grace window
        // so a follow-up add still picks the same combat state without churning
        // through ENTER/EXIT.
        private const int LEAVE_WHEN_OUT_OF_COMBAT_FOR = 3000;
        private long _aggroEndTime;
        private long _checkAggroTime;

        // Stuck-in-combat recovery: a melee mob blocked on a wall in AGGRO state
        // would otherwise stand still flailing at empty air. Sample position each
        // tick; if the body hasn't moved >= COMBAT_STUCK_MIN_DELTA_SQ within
        // COMBAT_STUCK_GRACE_MS while trying to reach an out-of-melee target, side-step.
        private int _lastCombatStuckX;
        private int _lastCombatStuckY;
        private long _lastCombatStuckSampleTick;
        private const int COMBAT_STUCK_GRACE_MS = 2500;
        private const int COMBAT_STUCK_MIN_DELTA_SQ = 50 * 50;

        public MimicState_Aggro(MimicBrain brain) : base(brain)
        {
            StateType = eFSMStateType.AGGRO;
        }

        public override void Enter()
        {
            _brain.MimicBody.IsSitting = false;

            _aggroEndTime = GameLoop.GameLoopTime + LEAVE_WHEN_OUT_OF_COMBAT_FOR;
            _checkAggroTime = GameLoop.GameLoopTime;
            _lastCombatStuckX = _brain.Body.X;
            _lastCombatStuckY = _brain.Body.Y;
            _lastCombatStuckSampleTick = GameLoop.GameLoopTime;

            _brain.OnEnterAggro();

            base.Enter();
        }

        public override void Exit()
        {
            _brain.Body.StopAttack();
            _brain.Body.StopMoving();
            // StopMoving doesn't always release the FollowTarget reference
            // (Follow is set by AttackComponent.StartAttack with a separate
            // movementComponent flag). Without an explicit StopFollowing the
            // tank can keep drifting toward a corpse's last position after
            // leaving AGGRO. Call it explicitly here.
            _brain.Body.StopFollowing();
            _brain.Body.StopCurrentSpellcast();
            // Hard-prune dead entries first so the decay sweep below only
            // weighs LIVE threats. Without this, a recently-killed mob's
            // 10 s soft-decay window would carry its entry into the next
            // engagement and could mislead the AggroList threat ranking.
            _brain.PruneDeadAggroEntries();
            // Soft-decay instead of full wipe: keep entries refreshed within
            // the last 10s so a brief out-of-combat blip → Follow → re-aggro
            // doesn't erase the threat picture. Stop()/ForcePullerRecovery
            // and friends still call the hard ClearAggroList when truly leaving combat.
            _brain.DecayAggroList(10_000);
            _brain.Body.TargetObject = null;

            _brain.IsFleeing = false;
            _brain.TargetFleePosition = null;
            _brain.ResetFlanking();

            // Pulse-effect cleanup wrapped: if End() throws (corrupt spell ref,
            // disposed handler, race with effect-tick removal), Exit() must
            // continue running. Otherwise the FSM would stay stuck in AGGRO
            // with stale aggro list, and the next Enter() never fires.
            try
            {
                foreach (ECSPulseEffect pulseEffect in _brain.Body.effectListComponent.GetPulseEffects())
                {
                    if (pulseEffect.SpellHandler?.Spell != null &&
                        pulseEffect.SpellHandler.Spell.UsePulsePower)
                    {
                        try { pulseEffect.End(); }
                        catch { /* swallow: don't let a single broken pulse stall Exit() */ }
                    }
                }
            }
            catch { /* same for the enumeration itself */ }

            _brain.OnExitAggro();

            base.Exit();
        }

        // Maximum distance a mimic in AGGRO can stray from its group's camp
        // before being force-disengaged and pulled back. Without a leash, a
        // bot chasing a fleeing mob or kited add wanders into another pull
        // and wipes the group. 4500u ~= 1.25 screens, generous enough for
        // ranged DPS to keep firing but tight enough to avoid runaway pulls.
        private const int CAMP_LEASH_DISTANCE = 4500;

        public override void Think()
        {
            _brain.AlreadyCheckedHeals = false;

            // Proactive aggro-list cleanup. Without this, dead/despawned
            // entries linger until the AGGRO.Exit soft-decay (10 s), keeping
            // HasAggro true and causing the tank to keep swinging on the
            // corpse — exactly the "tank attacks after combat is over"
            // symptom. Running it at the TOP of Think guarantees every
            // downstream call (AttackMostWanted, CheckMainTankTarget,
            // CalculateNextAttackTarget) sees a clean list.
            _brain.PruneDeadAggroEntries();

            // Also actively retire the body's auto-attack if it's still
            // wired up to a dead/inactive target. AttackAction can run on
            // the next ECS tick before Think fires again, so we stop the
            // attack here as a belt-and-suspenders measure.
            if (_brain.Body.TargetObject is GameLiving curTgt
                && (!curTgt.IsAlive || curTgt.ObjectState != GameObject.eObjectState.Active))
            {
                _brain.Body.StopAttack();
                _brain.Body.TargetObject = null;
            }

            if (_brain.PvPMode && _checkAggroTime < GameLoop.GameLoopTime)
            {
                _brain.CheckProximityAggro(_brain.AggroRange);
                _checkAggroTime = GameLoop.GameLoopTime + 5000;
                _aggroEndTime = GameLoop.GameLoopTime + LEAVE_WHEN_OUT_OF_COMBAT_FOR;
            }

            // Leash check (PvE only — PvP/RvR is intentionally roam-free).
            // If we have a camp point set and we've drifted too far from it,
            // drop aggro and return to camp instead of continuing the chase.
            if (!_brain.PvPMode && _brain.Body.Group?.MimicGroup?.CampPoint is Point3D camp
                && !_brain.Body.IsWithinRadius(camp, CAMP_LEASH_DISTANCE))
            {
                _brain.ClearAggroList();
                _brain.Body.StopAttack();
                _brain.FSM.SetCurrentState(eFSMStateType.CAMP);
                return;
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

            // Mezzed / stunned guard: the bot literally can't act this tick.
            // Without this gate, CheckHeals / CheckMainCC / AttackMostWanted
            // would still run their dispatchers (CheckSpells fails internally
            // but only after work was done), draining CPU and occasionally
            // re-arming target objects that won't be consumable for seconds.
            // Skip the action block entirely; aggro decay / exit conditions
            // were already evaluated above.
            if (_brain.Body.IsMezzed || _brain.Body.IsStunned)
            {
                base.Think();
                return;
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

            // Combat stuck recovery: if the bot is in AGGRO with a target it
            // can't reach (out of melee range) and hasn't moved meaningfully
            // for 2.5s, side-step perpendicular to the target bearing. Without
            // this, a melee bot wedged against a wall while chasing keeps
            // failing the swing range check forever, producing 0 DPS.
            if (_brain.Body.TargetObject is GameLiving combatTgt
                && combatTgt.IsAlive
                && !_brain.Body.IsWithinRadius(combatTgt, _brain.Body.attackComponent.AttackRange + 32))
            {
                int dx = _brain.Body.X - _lastCombatStuckX;
                int dy = _brain.Body.Y - _lastCombatStuckY;
                long now = GameLoop.GameLoopTime;
                if (dx * dx + dy * dy >= COMBAT_STUCK_MIN_DELTA_SQ)
                {
                    _lastCombatStuckX = _brain.Body.X;
                    _lastCombatStuckY = _brain.Body.Y;
                    _lastCombatStuckSampleTick = now;
                }
                else if (now - _lastCombatStuckSampleTick > COMBAT_STUCK_GRACE_MS)
                {
                    double heading = _brain.Body.GetAngle(combatTgt) * Math.PI / 180.0;
                    int sx = _brain.Body.X + (int)(120 * Math.Cos(heading + Math.PI / 2));
                    int sy = _brain.Body.Y + (int)(120 * Math.Sin(heading + Math.PI / 2));
                    _brain.Body.WalkTo(new Point3D(sx, sy, _brain.Body.Z), _brain.Body.MaxSpeed);
                    _lastCombatStuckSampleTick = now;
                    _lastCombatStuckX = sx;
                    _lastCombatStuckY = sy;
                }
            }

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

            // Rez before roaming — a wandering bot should still go pick up
            // dead group members it walks past.
            if (_brain.CheckResurrect())
                return;

            // A grouped non-leader bot must follow, never roam. ROAMING has
            // no group→follow transition of its own, so without this a bot
            // that ended up here while grouped would wander off forever.
            if (TryFollowGroupLeader())
                return;

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

                // Assist any group member already in combat before falling
                // back to passive proximity scan. A grouped roamer should
                // never wander past their healer / puller while a fight is
                // happening one slot over.
                if (_brain.Body.Group != null && _brain.ScanGroupCombat())
                {
                    _brain.FSM.SetCurrentState(eFSMStateType.AGGRO);
                    return;
                }

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

        // Per-bot throttle on the campfire presence check. With 8 bots in
        // camp, an unthrottled check meant 8×8 = 64 group iterations every
        // tick to ask "is there a fire?". The fire's lifetime is far longer
        // than this throttle so we only check ~once per 2s per bot, and
        // even less frequently when a fire IS detected (cache it).
        private long _nextCampFireCheckTick;

        // Throttle for the adaptive aggro-range refresh (Think-time). We
        // re-pick the per-phase range every 2s instead of every tick — the
        // group composition can't realistically change faster than that.
        private long _nextAggroRecomputeTick;

        // Throttle for the tank's intercept "step forward" during the Pulling
        // phase. The MaintainTankCampSupport call is already 1Hz; we piggy-
        // back on its cadence to also issue intercept-positioning.
        private long _nextTankInterceptTick;

        // Last intercept point the tank was moved to, so we don't spam WalkTo
        // every cycle for a position the bot has already reached.
        private Point3D _lastInterceptPoint;

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
            _brain.AggroRange = ComputeGroupAggroRange();

            if (AggroRange != 0)
                _brain.AggroRange = AggroRange;

            _brain.ClearAggroList();

            if (!_brain.Body.IsWithinRadius(_brain.Body.SpawnPoint, 60))
                _brain.Body.ReturnToSpawnPoint(_brain.Body.MaxSpeed);

            // Clear stale puller state — LastTargetObject, sticky mana throttle,
            // pulling flag — so a returning puller can immediately re-engage.
            _brain.ResetPullerState();

            // Only clear PvPMode when we're physically in a non-RvR zone.
            // Resetting it unconditionally broke frontier camp behaviour:
            // a group camping inside a RvR zone would forget it's in PvP
            // mode the moment any member sat down, and target-priority
            // arrays would flip back to PvE values mid-engagement.
            Region region = _brain.Body.CurrentRegion;
            Zone zone = _brain.Body.CurrentZone;
            bool inRvR = (region != null && region.IsRvR) || (zone != null && zone.IsRvR);
            if (!inRvR)
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
            // puller (it owns the pull lifecycle). Fall back to the lowest-
            // ObjectID living bot when the puller role is held by a non-mimic
            // (e.g. the player, which is the MimicGroup constructor default)
            // so the phase machine never stalls in player-led mixed groups.
            if (ShouldDriveCampPhase(mg))
                DriveCampPhase(mg);

            // Phase-aware tunings (cheap, runs every tick on data we already
            // have): refresh the camp aggro zone so it shrinks during regen,
            // and let the healer lock onto the tank while combat is brewing.
            RefreshAdaptiveAggroRange(mg);
            MaintainHealerFocus(mg);

            // Aggro check FIRST — never let the "wait while returning to slot"
            // shortcut skip the only line that lets the camp respond to a mob
            // that just walked into our range. Previously this lived BELOW the
            // IsDestinationValid early-return, which meant the bot could go
            // silent for the entire path-back leg even with a mob 200u away.
            if (CheckCampAggroTriggers(mg))
                return;

            // Now it's safe to skip the rest of the tick while we're pathing
            // back to our slot. The MAIN PULLER is always exempt: it still
            // needs CheckPuller to (re-)trigger casts when it closes range on
            // a target, and to abort stale pulls. The previous "!IsPulling"
            // check only covered bots already in-flight on a shot — it did
            // NOT exempt the puller while closing range on the caster path,
            // which is why the puller could "go back and forth" without ever
            // firing the pull spell.
            if (!_brain.IsMainPuller && !_brain.IsPulling && _brain.Body.IsDestinationValid)
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

            // Proactive tank intercept: during the Pulling phase, step the tank
            // forward toward the incoming mob's path so it picks up aggro
            // first instead of waiting for the mob to walk into camp.
            MaintainTankIntercept(mg);

            if (!_brain.Body.IsMoving && !_brain.Body.InCombat)
            {
                bool inCampRegen = mg != null && mg.CampPhase == MimicGroup.eCampPhase.Regen;

                // Healer sit-state: sync to phase intent EVERY tick,
                // unconditionally. Sit during Regen (mana regen ≈ 2-3×),
                // stand otherwise so CheckSpells(Defensive) is not skipped
                // by the !IsSitting guard below and so the bot reacts on
                // the next pull without waiting for an emergency to auto-
                // stand it.
                //
                // The previous design forced Sit(true) only on entry to
                // Regen and tried to flip back to Sit(false) inside the
                // `!IsSitting && !CheckSpells(...)` block — but that block
                // is short-circuited the moment the bot is already sitting,
                // so a healer who sat during Regen never got the chance to
                // stand back up when the phase advanced. The observable
                // symptom was a healer frozen mid-camp, not moving and not
                // healing anything below the emergency threshold, until a
                // FOLLOW switch + brain reset (Body.Follow() / Enter()
                // force a movement that auto-stands the bot).
                //
                // For non-healers, keep the original "sit when low stats"
                // heuristic but place it next to the cast attempt so the
                // CheckSpells(Defensive) path is preserved.
                if (_brain.IsHealer)
                    _brain.MimicBody.Sit(inCampRegen);

                if (!_brain.Body.IsSitting && !_brain.CheckSpells(MimicBrain.eCheckSpellType.Defensive))
                {
                    if (!_brain.IsHealer)
                        _brain.MimicBody.Sit(_brain.CheckStats(75));
                }

                // Throttle the campfire check to ~once per 2 seconds. Calling
                // EnsureGroupHasCampFire on every tick walks the group looking
                // for an active fire — wasteful when the fire's lifetime is
                // tens of seconds. With 8 bots this drops 8 group walks/tick to
                // effectively zero between checks.
                long now = GameLoop.GameLoopTime;
                if (now >= _nextCampFireCheckTick)
                {
                    _nextCampFireCheckTick = now + 2000;
                    EnsureGroupHasCampFire(_brain);
                }
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

            // Group safety floor (per user spec): if any member is below
            // MIMIC_GROUP_SAFETY_HEALTH_PCT (default 35 %), don't *start* a
            // new fight via Cases 2/4 (proactive engage). Case 1 (HasAggro —
            // bot is already being attacked) still fires below so an already-
            // engaged bot keeps fighting.
            bool groupUnsafe = mg != null && mg.IsGroupHealthCritical();

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
            //    Skipped entirely under the safety floor — we don't want any
            //    bot voluntarily walking into a fresh fight while a member is
            //    critically wounded.
            if (!groupUnsafe
                && mg != null && mg.IncomingPullTarget is GameLiving incoming
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

                // Tank-specific gate: only engage when the MOB itself is close.
                // The original "mob OR puller within 2500u" triggered as soon
                // as the puller fired (the puller is always right next to the
                // tank in camp), so the tank charged out with Follow(…, 5000)
                // and effectively pulled in the puller's place. Restricting
                // the tank to distToMob keeps the tank in camp until the mob
                // actually arrives; DPS / CC / ranged still pre-arm via the
                // wider gate below.
                if (_brain.IsMainTank)
                {
                    const int TANK_ENGAGE_RANGE = 700;
                    if (distToMob <= TANK_ENGAGE_RANGE)
                    {
                        EngageIncomingTarget(incoming, mg.MainPuller);
                        return true;
                    }
                }
                else if (effective <= CAMP_ENGAGE_RANGE)
                {
                    EngageIncomingTarget(incoming, mg.MainPuller);
                    return true;
                }
            }

            // 3. Leader / non-mimic puller opened combat on a hostile target
            //    BEFORE any bot puller fired (player-initiated pull, or a
            //    different player took the puller slot). Promote the target
            //    to IncomingPullTarget so the rest of the camp picks up the
            //    same Engaging/Combat machinery — without this the camp would
            //    sit on Ready until the mob actually swung in melee.
            if (!_brain.IsPulling)
            {
                GameLiving externalPuller = ResolveExternalPuller(mg);
                if (externalPuller != null && externalPuller != _brain.Body)
                {
                    // IsAttacking flips true the instant the auto-attack timer
                    // starts (before the first swing lands), so a player who
                    // just target-selects with auto-attack on would promote
                    // the click into a pull. Use InCombatInLast(2000) instead
                    // so we only react to *real* engagement (a swing actually
                    // exchanged in the last 2s). Harmful spell cast still
                    // counts as engagement.
                    bool castIsHarmfulNonCc = externalPuller.IsCasting
                                              && externalPuller.castingComponent?.SpellHandler?.Spell is Spell extSpell
                                              && extSpell.IsHarmful
                                              && extSpell.SpellType is not (eSpellType.Mez
                                                                            or eSpellType.Mesmerize
                                                                            or eSpellType.Stun
                                                                            or eSpellType.Amnesia
                                                                            or eSpellType.SpeedDecrease);
                    bool pullerEngaging = castIsHarmfulNonCc
                                          || externalPuller.InCombatInLast(2000);

                    if (pullerEngaging && externalPuller.TargetObject is GameLiving pulledTarget
                        && pulledTarget.IsAlive
                        && _brain.CanAggroTarget(pulledTarget))
                    {
                        const int LEADER_ENGAGE_RANGE = 2500;
                        if (_brain.Body.IsWithinRadius(pulledTarget, LEADER_ENGAGE_RANGE)
                            || _brain.Body.IsWithinRadius(externalPuller, LEADER_ENGAGE_RANGE))
                        {
                            PromotePlayerPullToCamp(mg, pulledTarget);
                            EngageIncomingTarget(pulledTarget, externalPuller);
                            return true;
                        }
                    }
                }
            }

            // 4. Group-combat assist: react when ANY group member (player or
            //    mimic, not just leader/puller) is engaging or being attacked.
            //    Bound the scan to a wider camp-aware radius (3000u) so a
            //    side-fight at the line still triggers the rest of the camp,
            //    without sucking in remote brawls across the zone.
            //
            // Tank gate during Pulling: skip the group-combat assist while a
            // pull is in flight. ScanGroupCombat would otherwise pick up the
            // puller engaging the pull target (puller takes a hit / casts
            // harmful) and drop the mob into the tank's aggro list. Once in
            // AGGRO, AttackMostWanted chases with StickMaximumRange=5000 and
            // the tank runs out to meet the mob mid-route — i.e. it pulls in
            // the puller's place. Case 5 (proximity) and Case 1 (HasAggro
            // from a direct hit) still engage the tank when the mob actually
            // reaches camp.
            //
            // Safety floor: when the group is critically wounded, suppress
            // the assist scan entirely — joining an unrelated fight while a
            // member is at <35 % HP is exactly how a camp wipes. Case 1
            // (already in combat) still keeps engaged bots fighting.
            const int CAMP_ASSIST_RADIUS = 3000;
            bool tankSkipScan = _brain.IsMainTank
                                && mg != null
                                && mg.CampPhase == MimicGroup.eCampPhase.Pulling;
            if (!groupUnsafe && !tankSkipScan && _brain.ScanGroupCombat(CAMP_ASSIST_RADIUS))
            {
                _brain.Body.StopMoving();
                _brain.FSM.SetCurrentState(eFSMStateType.AGGRO);
                return true;
            }

            // 5. Passive proximity scan (camp's small AggroRange).
            //
            // Camp-rest suppression: during Ready / Regen / PostCombat, the
            // group is supposed to be at rest waiting for the puller. A
            // proximity scan that aggros on any hostile in range turns the
            // tank into a magnet — visible as "the tank pulls every mob in
            // sight". Suppress the scan during those phases; the camp will
            // still react to:
            //   - mobs that actually hit a group member (Case 1: HasAggro
            //     for the bot that took the hit, Case 4: assist via
            //     ScanGroupCombat for the rest of the camp)
            //   - the puller's intentional pull (Case 2 / 3)
            // Pulling / Engaging / Combat phases keep the proximity scan
            // so adds wandering in during an active fight get picked up.
            // The safety floor (`groupUnsafe`) also disables the scan
            // regardless of phase, so a wounded group never voluntarily
            // grabs a new mob.
            bool restPhase = mg != null
                             && (mg.CampPhase == MimicGroup.eCampPhase.Ready
                                 || mg.CampPhase == MimicGroup.eCampPhase.Regen
                                 || mg.CampPhase == MimicGroup.eCampPhase.PostCombat);

            if (!groupUnsafe && !restPhase
                && _brain.CheckProximityAggro(_brain.AggroRange))
            {
                _brain.Body.StopMoving();
                _brain.FSM.SetCurrentState(eFSMStateType.AGGRO);
                return true;
            }

            return false;
        }

        /// <summary>
        /// Shared proactive-engage path used by Case 2 (mimic puller fired)
        /// and Case 3 (player/leader pulled). Picks the right reaction per
        /// role so the camp doesn't wait for the mob to melee-touch before
        /// reacting: tank charges, ranged DPS pre-arm the bow + step forward
        /// for LoS, CC pre-targets, melee DPS stand up and pre-target. All
        /// branches transition to AGGRO at the end so AttackMostWanted picks
        /// up next tick with the right weapon/target/position.
        /// </summary>
        private void EngageIncomingTarget(GameLiving incoming, GameLiving puller)
        {
            _brain.AddToAggroList(incoming, 1);
            _brain.MimicBody?.Sit(false);

            // Pick the anchor for the "advance for LoS / pre-position" step.
            // Prefer the puller (likely visible, in the open) over the mob,
            // which can be tucked behind a corner mid-pull.
            GameLiving anchor = (puller != null && puller.IsAlive) ? puller : incoming;

            if (_brain.IsMainTank)
            {
                // Pre-target the incoming mob so the AGGRO state's first tick
                // resolves the right threat instantly. We do NOT charge the
                // mob here: an unconditional Follow(…, 5000) + StartAttack
                // sent the tank out to meet the mob mid-route (the puller is
                // always right next to the tank, so Case 2's "puller within
                // 2500u" gate triggered the instant the pull arrow flew),
                // effectively turning the tank into the puller. The tank's
                // forward motion is handled by MaintainTankIntercept (1Hz,
                // capped at ~220u along the camp→puller axis) and by the
                // natural AGGRO chase once the mob is actually close.
                _brain.Body.TargetObject = incoming;

                // Exception: if the mob is already in melee/charge range,
                // engaging this tick is correct — there's no "pull" to
                // hijack at that distance, just normal aggro pickup.
                int distToMob = _brain.Body.GetDistanceTo(incoming);
                const int TANK_IMMEDIATE_ENGAGE_RANGE = 700;
                if (distToMob <= TANK_IMMEDIATE_ENGAGE_RANGE)
                {
                    int attackRange = _brain.Body.attackComponent?.AttackRange ?? 200;
                    _brain.Body.Follow(incoming, Math.Max(80, attackRange - 30), TANK_IMMEDIATE_ENGAGE_RANGE);
                    _brain.Body.StartAttack(incoming);
                }
                else
                {
                    // Mob still far: hold camp, let MaintainTankIntercept do
                    // the contained forward step. Don't StartAttack — that
                    // wires up auto-attack on a target out of range and the
                    // tank starts sprinting after it.
                    _brain.Body.StopFollowing();
                }
            }
            else if (_brain.MimicBody != null
                     && _brain.MimicBody.Inventory?.GetItem(eInventorySlot.DistanceWeapon) != null
                     && !_brain.IsMainPuller)
            {
                // Ranged DPS (Scout/Ranger/Hunter not pulling): pre-arm the
                // bow, pre-target, and step forward toward the anchor for LoS
                // / range. They'll still hold-fire if the tank hasn't
                // established aggro (DPS hold-fire in CalculateNextAttackTarget).
                _brain.Body.TargetObject = incoming;
                _brain.Body.SwitchWeapon(eActiveWeaponSlot.Distance);
                AdvanceTowardAnchor(anchor, 180);
            }
            else if (_brain.IsMainCC)
            {
                // CC pre-targets so the mez/root spell picker resolves the
                // incoming instantly when the gate opens. Slight side-step
                // for LoS — but never directly into the incoming axis.
                _brain.Body.TargetObject = incoming;
                AdvanceTowardAnchor(anchor, 120);
            }
            else if (!_brain.IsMainPuller && !_brain.IsHealer)
            {
                // Generic DPS (melee or caster non-CC): stand, pre-target,
                // and step a short distance forward so the mob lands on a
                // formation that's already moving — not a flat-footed line.
                _brain.Body.TargetObject = incoming;
                AdvanceTowardAnchor(anchor, 120);
            }
            else
            {
                _brain.Body.StopMoving();
            }

            _brain.FSM.SetCurrentState(eFSMStateType.AGGRO);
        }

        /// <summary>
        /// Steps the bot a short distance toward <paramref name="anchor"/>
        /// without breaking from camp entirely. Used by EngageIncomingTarget
        /// to nudge ranged DPS / CC / melee out of their seated positions on
        /// the pull-side of camp before the mob arrives, instead of standing
        /// flat-footed. No-ops when already close, when the camp point is
        /// missing, or when the bot is in motion.
        /// </summary>
        private void AdvanceTowardAnchor(GameLiving anchor, int stepUnits)
        {
            if (anchor == null) return;
            if (_brain.Body.IsCasting || _brain.Body.attackComponent.AttackState) return;

            int dist = _brain.Body.GetDistanceTo(anchor);
            if (dist <= stepUnits + 60) return; // already close enough

            double dx = anchor.X - _brain.Body.X;
            double dy = anchor.Y - _brain.Body.Y;
            double len = Math.Sqrt(dx * dx + dy * dy);
            if (len < 50) return;

            int ax = _brain.Body.X + (int)(dx / len * stepUnits);
            int ay = _brain.Body.Y + (int)(dy / len * stepUnits);
            Point3D dest = new(ax, ay, _brain.Body.Z);
            _brain.Body.StopFollowing();
            _brain.Body.WalkTo(dest, _brain.Body.MaxSpeed);
        }

        /// <summary>
        /// Returns the most likely non-mimic puller we should track for
        /// player-initiated pulls — preferring the explicit MainPuller when
        /// it's a player (a user could /maspuller themselves), and falling
        /// back to the group's living leader. Returns null when a mimic
        /// puller is currently in flight so we don't fight its own
        /// IncomingPullTarget machinery.
        /// </summary>
        private GameLiving ResolveExternalPuller(MimicGroup mg)
        {
            if (mg == null)
                return _brain.Body.Group?.LivingLeader;

            // If a mimic puller is actively pulling, defer to Case 2.
            if (mg.MainPuller is MimicNPC mp && mp.MimicBrain != null && mp.MimicBrain.IsPulling)
                return null;

            // Prefer an explicit non-mimic puller (rare: another player holds
            // the role). Otherwise fall back to the group leader, which is
            // the default puller when a player runs the camp.
            if (mg.MainPuller is GamePlayer gp)
                return gp;

            return _brain.Body.Group?.LivingLeader;
        }

        /// <summary>
        /// Surfaces a player-initiated pull to the rest of the camp by
        /// posting <paramref name="target"/> as the group's IncomingPullTarget
        /// and advancing the camp phase to Pulling. This is the bridge that
        /// makes downstream systems — MaintainTankIntercept, MaintainHealerFocus,
        /// IncomingAddsTrigger, IsCastingCcTrigger phase gates — work the same
        /// way for a player puller as for a mimic puller. Idempotent and
        /// guarded so we don't overwrite a mimic puller's in-flight target.
        /// </summary>
        private void PromotePlayerPullToCamp(MimicGroup mg, GameLiving target)
        {
            if (mg == null || target == null)
                return;

            // Don't trample a mimic puller's active pull — the mimic might
            // be locked on a different mob, and overwriting would confuse
            // adds-detection / phase tracking.
            if (mg.MainPuller is MimicNPC mp && mp.MimicBrain != null && mp.MimicBrain.IsPulling)
                return;

            if (mg.IncomingPullTarget != target)
                mg.IncomingPullTarget = target;

            switch (mg.CampPhase)
            {
                case MimicGroup.eCampPhase.Ready:
                case MimicGroup.eCampPhase.PostCombat:
                    mg.SetCampPhase(MimicGroup.eCampPhase.Pulling);
                    break;
                // INTENTIONALLY OMITTED: Regen.
                // When the group is in Regen the mana floor was breached;
                // the only way out is the RESUME gate (IsGroupReady, ~85 %).
                // Letting a player-initiated pull yank the group out of
                // Regen is the bypass that broke "sans faille" — the
                // puller would re-engage before mana had actually climbed
                // back, and the next fight wiped. Bots will still defend
                // reactively if the mob aggros them (groupInCombat flips
                // Regen → Combat at line ~1439); they just won't initiate
                // a NEW pull while the group is supposed to be resting.
            }
        }

        /// <summary>
        /// Picks exactly one bot per group to run <see cref="DriveCampPhase"/>
        /// each tick so the phase machine doesn't race with itself across N
        /// bots. Order of preference:
        ///   1. The MainPuller — owns the pull lifecycle, natural driver.
        ///   2. The MainLeader — when the puller role isn't held by a mimic.
        ///   3. Lowest ObjectID mimic in the group — last-resort tiebreaker
        ///      so player-led groups with no mimic puller (the constructor
        ///      default assigns roles to the player) still get a driver.
        /// </summary>
        private bool ShouldDriveCampPhase(MimicGroup mg)
        {
            if (mg == null)
                return false;

            // Case 1: this bot is the puller.
            if (_brain.IsMainPuller)
                return true;

            bool pullerIsMimic = mg.MainPuller is MimicNPC;

            // Case 2: this bot is the leader, AND no mimic puller exists.
            if (!pullerIsMimic && _brain.IsMainLeader)
                return true;

            // Case 3: neither a mimic puller nor a mimic leader exists.
            // Pick a single deterministic driver — the lowest ObjectID mimic
            // alive in the group — so the same bot runs the FSM each tick
            // and the phase doesn't flap.
            bool leaderIsMimic = mg.MainLeader is MimicNPC;
            if (pullerIsMimic || leaderIsMimic)
                return false;

            Group g = _brain.Body.Group;
            if (g == null)
                return false;

            MimicNPC chosen = null;
            foreach (GameLiving gl in g.GetMembersInTheGroup())
            {
                if (gl is MimicNPC m && m.IsAlive
                    && (chosen == null || m.ObjectID < chosen.ObjectID))
                    chosen = m;
            }
            return chosen == _brain.Body;
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

            // Dead-puller failover: when the MainPuller dies and no human
            // re-clicks the role on a survivor, the camp would otherwise
            // stay frozen — Regen → Ready loops forever because CheckPuller
            // never runs (the puller is a corpse). Promote the first alive
            // mimic that can pull. Throttled by the watchdog cadence so we
            // don't spam SetMainPuller.
            if (mg.MainPuller is GameLiving puller
                && (!puller.IsAlive || puller.ObjectState != GameObject.eObjectState.Active)
                && now >= _nextWatchdogTick)
            {
                _nextWatchdogTick = now + WATCHDOG_RETRY_MS;
                PromoteAliveMimicAsPuller(mg);
            }

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
                    // Player-puller bridge: when a non-mimic (player) starts a
                    // pull, no IsPulling flag flips, but PromotePlayerPullToCamp
                    // has already posted IncomingPullTarget. Recognise it here
                    // so the phase advances and tank-intercept / healer-focus
                    // strategies kick in instead of waiting for melee contact.
                    if (mg.IncomingPullTarget is GameLiving ppt
                        && ppt.IsAlive
                        && ppt.ObjectState == GameObject.eObjectState.Active)
                    {
                        mg.SetCampPhase(MimicGroup.eCampPhase.Pulling);
                        break;
                    }
                    // Hysteresis: stay in Ready unless a member is SIGNIFICANTLY
                    // degraded. Using the strict IsGroupReady gate here caused
                    // Ready ↔ Regen flapping the moment any caster's mana
                    // ticked from 80% to 79% — and that flap blocked the
                    // puller in CheckDelayPull's phase gate, producing the
                    // "Camp pret" → no-pull → back-and-forth symptom.
                    if (!IsGroupStillFresh(mg))
                        mg.SetCampPhase(MimicGroup.eCampPhase.Regen);
                    break;

                case MimicGroup.eCampPhase.Pulling:
                    if (groupInCombat)
                    {
                        mg.SetCampPhase(MimicGroup.eCampPhase.Combat);
                        break;
                    }
                    if (mg.IncomingPullTarget is GameNPC inc && inc.IsAlive)
                    {
                        // Trigger Engaging on ANY brain type that has aggro on a
                        // group member. The legacy check required StandardMobBrain
                        // (BAF-style) which excluded epic mobs, unique bosses, and
                        // custom AI brains — those would stay stuck in Pulling
                        // until the watchdog fired (12s), and never benefit from
                        // tank intercept positioning or CC pre-mez.
                        bool incHasAggro = false;
                        if (inc.Brain is StandardMobBrain smb && smb.HasAggro)
                            incHasAggro = true;
                        else if (inc.attackComponent?.AttackState == true)
                            incHasAggro = true; // any brain currently swinging
                        else if (inc.TargetObject is GameLiving incTgt
                                 && incTgt.Group != null
                                 && incTgt.Group == _brain.Body.Group)
                            incHasAggro = true; // any brain targeting a group member

                        if (incHasAggro)
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
                    // Chain-pull while EVERY caster is still above the STOP
                    // floor (~30 %, IsGroupStillFresh). Above the floor the
                    // group is good to keep going — no needless Regen pause.
                    // Below the floor we fall through to Regen, and from
                    // there the only exit is the RESUME gate (~85 %,
                    // IsGroupReady) — that's where "sans faille" lives:
                    // once Regen kicks in, nothing lets the puller out
                    // until everyone is properly topped.
                    if (IsGroupStillFresh(mg))
                    {
                        mg.SetCampPhase(MimicGroup.eCampPhase.Ready);
                        break;
                    }
                    // Residual aggro (stray mob walking back, a DoT ticking)
                    // gets a short 1 s grace so the still-resolving fight
                    // isn't pre-empted by an early Regen transition.
                    int grace = AnyGroupMemberHasAggro() ? 1000 : 0;
                    if (now - mg.CampPhaseSinceTick > grace)
                        mg.SetCampPhase(MimicGroup.eCampPhase.Regen);
                    break;
            }
        }

        /// <summary>
        /// Find a live mimic in the group that can pull and install it as
        /// MainPuller. Falls back to MainLeader (which may be the human
        /// player) when no candidate exists so the role is never null.
        /// Picks deterministically — lowest ObjectID — so different drivers
        /// don't fight over the choice across ticks.
        /// </summary>
        private void PromoteAliveMimicAsPuller(MimicGroup mg)
        {
            if (mg == null || _brain.Body.Group == null)
                return;

            MimicNPC chosen = null;
            foreach (GameLiving gl in _brain.Body.Group.GetMembersInTheGroup())
            {
                if (gl is not MimicNPC m || !m.IsAlive || m.MimicBrain == null)
                    continue;
                // Skip healers — their job is heal, not pull. SetMainPuller's
                // CanPull check already rejects most healers but mid-spec
                // healers (Friar/Bard/Warden) can pass; respect IsHealer flag.
                if (m.MimicBrain.IsHealer)
                    continue;

                if (chosen == null || m.ObjectID < chosen.ObjectID)
                    chosen = m;
            }

            if (chosen != null)
            {
                if (!mg.SetMainPuller(chosen))
                    mg.ForceSetMainPullerForBodyPull(chosen);
            }
            else
            {
                mg.ClearMainPuller(); // falls back to MainLeader internally
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
            // Regen -> Ready gate: the group may only START a new pull cycle
            // once every caster mimic has rested back to the RESUME mana % —
            // i.e. near-full. Pulling again the instant a caster scraped past
            // a low floor (the old hard-coded 30%) sent the group into fights
            // with no mana reserve, the healer ran dry, and the group wiped.
            // The human leader is excluded — their resource flow is decoupled
            // from the bot regen cycle.
            int readyManaPct = MimicConfig.MIMIC_PULL_MANA_RESUME_PCT;

            if (_brain.Body.Group == null)
                return true;

            // HP gate: respect the same safety floor CheckDelayPull uses
            // (default 35 %). Without this the camp could phase-transition
            // Regen → Ready with the tank at 30 % HP; the player-puller
            // bridge would then fire a pull on the wounded group because
            // the phase machine had already said "Ready".
            if (mg != null && mg.IsGroupHealthCritical())
                return false;

            // Minimum rest window: even when nobody in the group has a mana
            // pool (pure-melee composition) we still want a short pause
            // between consecutive fights — otherwise the camp chain-pulls
            // continuously and the healer (which itself is a caster) never
            // gets to climb past RESUME %. The check below ran the mana
            // loop and short-circuited to true on every melee-only group.
            // 8s matches what a real DAoC group spends "buffing up" between
            // pulls and is short enough to feel natural.
            const long MIN_REGEN_WINDOW_MS = 8000;
            if (mg != null && mg.CampPhase == MimicGroup.eCampPhase.Regen
                && GameLoop.GameLoopTime - mg.CampPhaseSinceTick < MIN_REGEN_WINDOW_MS)
                return false;

            // Mana gate: every caster mimic must be back above RESUME %.
            // Skipped for non-mimic players (their mana flow is decoupled).
            bool anyMimicCaster = false;
            foreach (GameLiving gl in _brain.Body.Group.GetMembersInTheGroup())
            {
                if (gl == null || !gl.IsAlive)
                    continue;
                if (gl is GamePlayer)
                    continue;
                if (gl.MaxMana <= 0)
                    continue;
                anyMimicCaster = true;
                if (gl.ManaPercent < readyManaPct)
                    return false;
            }

            // Pure-melee mimic groups (no member with MaxMana > 0) fall
            // through to true here — but the MIN_REGEN_WINDOW_MS gate above
            // already forced an 8s pause, so the pause is enforced.
            _ = anyMimicCaster;
            return true;
        }

        /// <summary>
        /// "Stay in Ready" gate — relaxed thresholds vs IsGroupReady so a
        /// healthy group doesn't flap back to Regen on every regen tick.
        /// Mana threshold matches the puller's MANA_STOP_PCT (30%) — once a
        /// caster drops there, the puller's CheckDelayPull will throttle and
        /// the camp drops to Regen for a real rest window. This gives the
        /// "pull above 80% / stop below 30%" behaviour end-to-end.
        /// </summary>
        private bool IsGroupStillFresh(MimicGroup mg)
        {
            // "Keep chain-pulling" gate: the camp stays in Ready (puller may
            // chain straight into the next pull) until a caster mimic drops
            // below the STOP mana % — then it falls to Regen for a real rest.
            // STOP is well below RESUME, so the chain runs RESUME% -> STOP%
            // and the wide gap between them is the rest window (no Ready <->
            // Regen flapping). STOP stays high enough that the last fight of
            // a chain still begins with a usable mana buffer.
            int freshManaPct = MimicConfig.MIMIC_PULL_MANA_STOP_PCT;

            if (_brain.Body.Group == null)
                return true;

            foreach (GameLiving gl in _brain.Body.Group.GetMembersInTheGroup())
            {
                if (gl == null || !gl.IsAlive)
                    continue;
                if (gl is GamePlayer)
                    continue;
                if (gl.MaxMana > 0 && gl.ManaPercent < freshManaPct)
                    return false;
            }
            return true;
        }

        /// <summary>
        /// Computes the camp aggro-scan range for this bot, scaled by the
        /// group's composition and role. A stronger group sees further; a
        /// tank/puller scans even further than the camp baseline so the
        /// formation reacts to incoming mobs before they cross the line.
        ///
        /// Inputs that grow the zone:
        ///   - Indoor vs outdoor baseline (dungeon = tighter, outdoor = wider)
        ///   - Each alive group member (+30 each, capped at 8 members)
        ///   - A living main tank (+100, anchors melee)
        ///   - A living healer (+100, sustains intercept fights)
        ///   - A living main CC (+100, can lock incoming pulls early)
        /// Role-side bonuses (additive to the group base):
        ///   - Main tank: +250 outdoor / +100 dungeon  (intercept eyes)
        ///   - Main puller: forced to ≥ 1000 outdoor / 600 dungeon (chain scan)
        ///   - Main CC: +200 (line-of-sight pre-mez window)
        /// Returns the resulting aggro range; the user-overridable
        /// <see cref="AggroRange"/> still takes precedence in Enter().
        /// </summary>
        private int ComputeGroupAggroRange()
        {
            // CurrentRegion can be transiently null during a world transfer;
            // fall back to the outdoor profile rather than throwing.
            bool dungeon = _brain.Body.CurrentRegion?.IsDungeon == true;

            int range = dungeon ? 250 : 550;

            MimicGroup mg = _brain.Body.Group?.MimicGroup;
            int aliveMembers = 0;
            bool tankAlive = false, healerAlive = false, ccAlive = false;
            if (_brain.Body.Group != null)
            {
                foreach (GameLiving gl in _brain.Body.Group.GetMembersInTheGroup())
                {
                    if (gl == null || !gl.IsAlive)
                        continue;
                    aliveMembers++;
                    if (mg != null && gl == mg.MainTank)
                        tankAlive = true;
                    if (mg != null && gl == mg.MainCC)
                        ccAlive = true;
                    if (gl is MimicNPC m && m.MimicBrain != null && m.MimicBrain.IsHealer)
                        healerAlive = true;
                }
            }

            range += Math.Min(aliveMembers, 8) * 30;
            if (tankAlive)   range += 100;
            if (healerAlive) range += 100;
            if (ccAlive)     range += 100;

            // Role-side bonuses.
            if (_brain.IsMainTank)
                range += dungeon ? 100 : 250;
            if (_brain.IsMainCC)
                range += 200;
            // Puller's scan reaches as far as the tool it pulls with — there's
            // no point seeing closer than we can actually shoot/cast at. We
            // pick the wider of the two: bow attack range (when a distance
            // weapon is equipped) or the cached pull spell range. Default to
            // 2000 if neither is resolvable yet (first Enter before spells
            // sorted). Indoors we cap a little tighter because LoS is short.
            if (_brain.IsMainPuller)
            {
                int pullReach = 2000;

                if (_brain.Body.Inventory?.GetItem(eInventorySlot.DistanceWeapon) != null)
                {
                    int bow = _brain.Body.attackComponent?.AttackRange ?? 0;
                    if (bow > pullReach) pullReach = bow;
                }

                Spell ps = _brain.SelectPullSpell();
                if (ps != null && ps.Range > pullReach)
                    pullReach = ps.Range;

                int pullerFloor = dungeon ? Math.Min(pullReach, 1500) : pullReach;
                range = Math.Max(range, pullerFloor);
            }

            // Safety clamp — keep the zone within the brain's max aggro radius
            // so we never scan beyond what the bot can actually engage.
            return Math.Clamp(range, 250, MimicBrain.MAX_AGGRO_DISTANCE);
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
            // CurrentRegion can be transiently null during a world transfer.
            bool dungeon = _brain.Body.CurrentRegion?.IsDungeon == true;
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
        /// Re-runs <see cref="ComputeGroupAggroRange"/> at most every 2s so the
        /// camp's reaction zone follows the live group state — shrinks while
        /// the group is regening (we don't want a tired group dragging extra
        /// mobs in), grows once Ready/Combat/etc. The user override
        /// <see cref="AggroRange"/> still wins when set.
        /// </summary>
        private void RefreshAdaptiveAggroRange(MimicGroup mg)
        {
            long now = GameLoop.GameLoopTime;
            if (now < _nextAggroRecomputeTick)
                return;
            _nextAggroRecomputeTick = now + 2000;

            // Explicit user override — leave it alone.
            if (AggroRange != 0)
            {
                _brain.AggroRange = AggroRange;
                return;
            }

            int target = ComputeGroupAggroRange();

            // Phase modifier: while regening, reduce the zone so bots don't
            // stand up over a stray wandering mob; ramp it back as soon as
            // we're combat-ready.
            if (mg != null)
            {
                switch (mg.CampPhase)
                {
                    case MimicGroup.eCampPhase.Regen:
                        target = (int)(target * 0.6);
                        break;
                    case MimicGroup.eCampPhase.PostCombat:
                        target = (int)(target * 0.8);
                        break;
                }
            }

            _brain.AggroRange = Math.Max(target, 200);
        }

        /// <summary>
        /// Steers the main tank a couple body-lengths forward along the pull
        /// axis when the puller has a mob in flight (camp phase = Pulling).
        /// Cuts the time between "mob enters camp" and "tank has aggro" by
        /// closing some of the distance early. Throttled to 1Hz and idempotent
        /// against the same intercept point so it doesn't fight the camp's
        /// own slot positioning logic.
        /// </summary>
        private void MaintainTankIntercept(MimicGroup mg)
        {
            if (!_brain.IsMainTank || _brain.IsPulling)
                return;
            if (mg == null || mg.CampPhase != MimicGroup.eCampPhase.Pulling)
                return;
            if (mg.IncomingPullTarget is not GameLiving incoming || !incoming.IsAlive)
                return;
            if (_brain.Body.InCombat || _brain.HasAggro)
                return;

            long now = GameLoop.GameLoopTime;
            if (now < _nextTankInterceptTick)
                return;
            _nextTankInterceptTick = now + 1000;

            Point3D camp = mg.CampPoint;
            if (camp == null)
                return;

            // Direction: camp → puller (or pull-from point). Tank steps that
            // way by ~200 units, but never past the puller itself — we don't
            // want the tank racing the puller into the mob.
            double dx, dy;
            GameLiving puller = mg.MainPuller;
            if (puller != null && puller.IsAlive)
            {
                dx = puller.X - camp.X;
                dy = puller.Y - camp.Y;
            }
            else if (mg.PullFromPoint != null)
            {
                dx = mg.PullFromPoint.X - camp.X;
                dy = mg.PullFromPoint.Y - camp.Y;
            }
            else
            {
                dx = incoming.X - camp.X;
                dy = incoming.Y - camp.Y;
            }

            double len = Math.Sqrt(dx * dx + dy * dy);
            if (len < 50)
                return; // axis too short to bother

            const int INTERCEPT_STEP = 220;
            int ix = camp.X + (int)(dx / len * INTERCEPT_STEP);
            int iy = camp.Y + (int)(dy / len * INTERCEPT_STEP);

            // Skip if we already moved here on a prior cycle — re-issuing the
            // same WalkTo would just thrash the movement component.
            if (_lastInterceptPoint != null
                && Math.Abs(_lastInterceptPoint.X - ix) < 50
                && Math.Abs(_lastInterceptPoint.Y - iy) < 50
                && _brain.Body.IsWithinRadius(_lastInterceptPoint, 80))
                return;

            Point3D dest = new(ix, iy, camp.Z);
            _lastInterceptPoint = dest;
            _brain.Body.StopFollowing();
            _brain.MimicBody?.Sit(false);
            _brain.Body.WalkTo(dest, _brain.Body.MaxSpeed);
        }

        /// <summary>
        /// Healer pre-target lock: during Engaging/Combat phases, make sure
        /// the healer's current target is the main tank (or the most-injured
        /// member). This way the healer's heal hotkey / spell picker finds a
        /// valid friendly target instantly when the first hit lands, instead
        /// of wasting the first cast cycle searching.
        /// </summary>
        private void MaintainHealerFocus(MimicGroup mg)
        {
            if (mg == null || !_brain.IsHealer)
                return;

            // Only lock during the windows where a heal is imminent — outside
            // of those the heal logic itself drives target selection.
            if (mg.CampPhase != MimicGroup.eCampPhase.Engaging
                && mg.CampPhase != MimicGroup.eCampPhase.Combat
                && mg.CampPhase != MimicGroup.eCampPhase.Pulling)
                return;

            GameLiving focus = mg.MainTank;
            if (focus == null || !focus.IsAlive)
                focus = mg.MemberToHeal;
            if (focus == null || focus == _brain.Body || !focus.IsAlive)
                return;

            // Don't override a hostile target the healer is actively dealing
            // with (e.g. interrupting a caster mob) — only set when the
            // current target is null, dead, or a non-group hostile we have
            // no reason to be targeting from a heal seat.
            GameObject cur = _brain.Body.TargetObject;
            if (cur == focus)
                return;
            if (cur is GameLiving curLiving && curLiving.IsAlive
                && _brain.Body.Group != null && _brain.Body.Group.IsInTheGroup(curLiving))
                return;

            _brain.Body.TargetObject = focus;
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
                    // Group with a camp anchor: fall back to camp duty so
                    // the puller / healer routine takes over. Solo or no
                    // camp set: dive straight into combat. The previous
                    // code set CAMP and then immediately overrode it with
                    // AGGRO in the same tick, making the camp branch dead
                    // code (last SetCurrentState wins).
                    if (_brain.Body.Group != null && _brain.Body.Group.MimicGroup?.CampPoint != null)
                        _brain.FSM.SetCurrentState(eFSMStateType.CAMP);
                    else
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

            // Rez before patrol heals — a dead member on the route should
            // not be skipped just because the bot is wandering.
            if (_brain.CheckResurrect())
                return;

            // A grouped non-leader bot must follow, not patrol a path.
            if (TryFollowGroupLeader())
                return;

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

        // Remember whether the duel was started outside an RvR zone so Exit
        // can put PvPMode back to its true (zone-derived) value instead of
        // leaving it stuck at true forever.
        private bool _prevPvPMode;

        public override void Enter()
        {
            _brain.ClearAggroList();

            if (_brain.MimicBody != null)
                _brain.MimicBody.IsDuelReady = false;
            _brain.Body.IsSitting = false;
            _brain.AggroLevel = 100;
            _prevPvPMode = _brain.PvPMode;
            _brain.PvPMode = true;
            _brain.AggroRange = 3600;
            _brain.Body.StopMoving();

            base.Enter();
        }

        public override void Exit()
        {
            // Restore PvPMode to whatever it was before the duel — without
            // this, the bot stayed in PvP target-priority mode forever after
            // a duel in a PvE zone.
            _brain.PvPMode = _prevPvPMode;
            if (_brain.MimicBody != null)
                _brain.MimicBody.IsDuelReady = false;
            base.Exit();
        }

        public override void Think()
        {
            _brain.AlreadyCheckedHeals = false;

            // Defensive exit transitions: if the duel partner is gone (logged
            // off, died, accepted another duel, or the bot's owner cancelled),
            // bail back to WAKING_UP so the bot doesn't get stuck mid-stance.
            if (_brain.MimicBody == null)
            {
                _brain.FSM.SetCurrentState(eFSMStateType.WAKING_UP);
                return;
            }

            GameLiving partner = _brain.MimicBody.DuelPartner;
            if (partner == null || !partner.IsAlive || partner.ObjectState != GameObject.eObjectState.Active)
            {
                _brain.FSM.SetCurrentState(eFSMStateType.WAKING_UP);
                return;
            }

            if (!_brain.CheckSpells(MimicBrain.eCheckSpellType.Defensive))
                _brain.MimicBody.IsDuelReady = true;

            if (partner is IGamePlayer gPlayer && gPlayer.IsDuelReady)
            {
                _brain.CheckProximityAggro(_brain.AggroRange);
                _brain.AttackMostWanted();
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
            // Tear down any leftover combat / movement intent so the corpse
            // doesn't try to keep swinging or pathing during the rez window.
            _brain.ClearAggroList();
            _brain.Body.StopFollowing();
            _brain.Body.StopAttack();
            _brain.Body.TargetObject = null;
            _brain.IsFleeing = false;
            _brain.TargetFleePosition = null;
            base.Enter();
        }

        public override void Think()
        {
            // Stay parked while the body is still dead — wait for the rez /
            // revive path (MimicNPC.ProcessDeath → OnRezWaitExpired → revive
            // OR a group rez landing) to flip IsAlive back on. The legacy
            // behaviour fell straight through to WAKING_UP every tick, which
            // (a) made the DEAD state a one-frame transient that never
            // actually waited, and (b) caused WakingUp.Think to run against
            // a corpse — burning cycles on group/region queries that would
            // re-resolve a moment later anyway when the rez actually landed.
            if (!_brain.Body.IsAlive)
                return;

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
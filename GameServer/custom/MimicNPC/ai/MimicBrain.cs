using DOL.Database;
using DOL.GS;
using DOL.GS.API;
using DOL.GS.Effects;
using DOL.GS.Keeps;
using DOL.GS.PacketHandler;
using DOL.GS.RealmAbilities;
using DOL.GS.Scripts;
using DOL.GS.Scripts.AI.Strategies;
using DOL.GS.Scripts.AI.Strategies.Builtin;
using DOL.GS.ServerProperties;
using DOL.GS.SkillHandler;
using DOL.GS.Spells;
using DOL.Language;
using Microsoft.Extensions.Configuration.UserSecrets;
using Microsoft.VisualBasic;
using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Reflection;
using System.Text;
using System.Threading;
using static DOL.AI.Brain.StandardMobBrain;
using static DOL.GS.Styles.Style;

namespace DOL.AI.Brain
{
    // MimicBrain is intentionally split across several partial files
    // (MimicBrain.*.cs) — heal cycle, pull cycle, etc. — so each subsystem
    // stays navigable. Add a new partial file rather than growing this one
    // further.
    public partial class MimicBrain : ABrain, IOldAggressiveBrain
    {
        protected static readonly Logging.Logger log = Logging.LoggerManager.Create(MethodBase.GetCurrentMethod().DeclaringType);

        // Upstream ABrain.IsActive is now non-virtual and matches our
        // expression (alive + Active + visible). Drop our override — base
        // value is correct. Kept commented so reviewers see the intent.
        // public override bool IsActive => Body != null && Body.IsAlive && Body.ObjectState == GameObject.eObjectState.Active;

        /// <summary>
        /// Runtime "I'm acting as the group healer" flag. Set by group
        /// composition logic and toggleable at runtime (e.g. via
        /// <c>MimicGroup.SetHealer</c> or /mset). Distinct from the
        /// class-level CAPABILITY: see <see cref="IsHealerCapable"/>.
        /// </summary>
        public bool IsHealer = false;

        /// <summary>
        /// True when the bot's class profile carries the Healer role,
        /// independently of whether it has been assigned the group healer
        /// duty this session. Sites that need "can this bot heal at all?"
        /// (loot decisions, group composition, rez eligibility) should ask
        /// this rather than <see cref="IsHealer"/>.
        /// </summary>
        public bool IsHealerCapable
            => MimicBody?.CombatProfile?.HasRole(DOL.GS.Scripts.eMimicCombatRole.Healer) == true;

        public bool IsMainPuller { get { return Body.Group?.MimicGroup.MainPuller == Body; } }
        public bool IsMainTank { get { return Body.Group?.MimicGroup.MainTank == Body; } }
        public bool IsMainLeader { get { return Body.Group?.MimicGroup.MainLeader == Body; } }
        public bool IsMainCC { get { return Body.Group?.MimicGroup.MainCC == Body; } }
        public bool IsMainAssist { get { return Body.Group?.MimicGroup.MainAssist == Body; } }

        private MimicNPC _mimicBody;

        public MimicNPC MimicBody
        {
            get { return _mimicBody; }
            set { _mimicBody = value; }
        }

        private long _emoteDelay;

        public const int MAX_AGGRO_DISTANCE = 3600;
        public const int MAX_AGGRO_LIST_DISTANCE = 6000;

        // Effective aggro reduction is calculated using an exponential decay function, starting from the distance threshold. A reduction of 2/3rd is ensured at 1500.
        private const int EFFECTIVE_AGGRO_DISTANCE_THRESHOLD = 250; // Should be higher than players' melee range.
        private static readonly double EFFECTIVE_AGGRO_EXPONENT = Math.Log(1 / 3.0) / (1500 - EFFECTIVE_AGGRO_DISTANCE_THRESHOLD);

        public bool PreventCombat;
        public bool PvPMode;
        public bool Defend;
        public bool Roam;
        public bool IsFleeing;
        public bool IsPulling;
        public bool Debug;

        public GameObject LastTargetObject;
        public bool IsFlanking;
        public Point2D TargetFlankPosition;

        public Point3D TargetFleePosition;

        // Used for AmbientBehaviour "Seeing" - maintains a list of GamePlayer in range
        public List<GamePlayer> PlayersSeen = new();

        // Cached "human player in this bot's group" used by MirrorLeaderSprint.
        // Resolved by iterating Group.GetMembersInTheGroup() — expensive enough
        // (lock + pooled-list copy) that we don't want to do it on every Think
        // tick for every bot. Refreshed on a short interval and invalidated
        // when the cached player leaves the region/dies.
        internal GamePlayer CachedPlayerLeader;
        internal long CachedPlayerLeaderExpireTick;

        /// <summary>
        /// Constructs a new MimicBrain
        /// </summary>
        public MimicBrain() : base()
        {
            FSM = new();
            FSM.Add(new MimicState_Idle(this));
            FSM.Add(new MimicState_WakingUp(this));
            FSM.Add(new MimicState_Aggro(this));
            FSM.Add(new MimicState_ReturnToSpawn(this));
            FSM.Add(new MimicState_Patrolling(this));
            FSM.Add(new MimicState_Roaming(this));
            FSM.Add(new MimicState_FollowLeader(this));
            FSM.Add(new MimicState_Camp(this));
            FSM.Add(new MimicState_Duel(this));
            FSM.Add(new MimicState_Dead(this));
            FSM.Add(new MimicState_CityIdle(this));
            _aggroLosCheckListener = new(this);
        }

        /// <summary>
        /// Returns the string representation of the MimicBrain
        /// </summary>
        public override string ToString()
        {
            return base.ToString() + ", AggroLevel=" + AggroLevel.ToString() + ", AggroRange=" + AggroRange.ToString();
        }

        public override bool Stop()
        {
            // tolakram - when the brain stops, due to either death or no players in the vicinity, clear the aggro list
            if (base.Stop())
            {
                ClearAggroList();
                return true;
            }

            return false;
        }

        public override void KillFSM()
        {
            FSM.KillFSM();
        }

        #region AI

        private BotStrategyManager _strategyManager;

        /// <summary>
        /// Lazy strategy manager. Returns null when the strategy system is
        /// disabled or no MimicNPC body is available — callers must
        /// null-check.
        /// </summary>
        public BotStrategyManager StrategyManager
        {
            get
            {
                if (_strategyManager != null)
                    return _strategyManager;

                if (MimicBody == null)
                    return null;

                _strategyManager = new BotStrategyManager(MimicBody, this);
                EnableDefaultStrategies(_strategyManager, MimicBody);
                return _strategyManager;
            }
        }

        /// <summary>
        /// Turns on the baseline strategy bundle every mimic should be running:
        /// survival (sit/stand auto), awareness (self callouts + banter),
        /// assist (focus the assist target), peel (protect vulnerable members),
        /// support (announce mezz/crit/CC) and camp (group-dynamics layer for
        /// /mcamp). Each strategy is individually toggleable later via
        /// /mstrategy if the player wants to silence one bot.
        /// </summary>
        private static void EnableDefaultStrategies(BotStrategyManager mgr, MimicNPC bot)
        {
            if (mgr == null)
                return;

            mgr.Enable(SurvivalStrategy.Key);
            mgr.Enable(AwarenessStrategy.Key);
            mgr.Enable(AssistStrategy.Key);
            mgr.Enable(PeelStrategy.Key);
            mgr.Enable(SupportStrategy.Key);
            mgr.Enable(CampStrategy.Key);
            // Activated on every bot; the bindings inside filter on the
            // Leader role so only the actual MainLeader fires the lines.
            mgr.Enable(LeaderStrategy.Key);

            // Bot AI v2 — role-specific strategies. Each role is opted in
            // per class via the matching CSV in MimicConfig (healer/tank/
            // melee_dps/ranged_dps/caster_dps/cc). Strategies are
            // composable: a Druid runs healer + caster_dps, a Bard runs
            // healer + cc, a Reaver runs tank + melee_dps, a Friar runs
            // healer + caster_dps. Pure tanks like the Paladin stay
            // tank-only; assassins like Infiltrator/Nightshade/Shadowblade
            // stay melee_dps-only. Operators tune the CSV at runtime;
            // new bots pick up the change on spawn.
            if (bot?.CharacterClass == null)
                return;

            int classId = bot.CharacterClass.ID;

            if (MimicConfig.IsHealerClass(classId))    mgr.Enable(HealerStrategy.Key);
            if (MimicConfig.IsTankClass(classId))      mgr.Enable(TankStrategy.Key);
            if (MimicConfig.IsMeleeDpsClass(classId))  mgr.Enable(MeleeDpsStrategy.Key);
            if (MimicConfig.IsRangedDpsClass(classId)) mgr.Enable(RangedDpsStrategy.Key);
            if (MimicConfig.IsCasterDpsClass(classId)) mgr.Enable(CasterDpsStrategy.Key);
            if (MimicConfig.IsCcClass(classId))        mgr.Enable(CcStrategy.Key);
        }

        public override void Think()
        {
            // Mirror the group leader's sprint state every tick so the bot can
            // keep up no matter which FSM state it's in (follow, roam, aggro
            // chase, etc.). Mirroring only inside FollowLeader.Think misses
            // any state where the bot is moving but not actively following.
            if (Body?.Group?.LivingLeader is GameLiving gl)
                MimicState.MirrorLeaderSprint(this, gl);

            if (MimicConfig.USE_STRATEGY_SYSTEM)
                StrategyManager?.Tick();

            FSM.Think();
        }

        public virtual void OnLeaderAggro()
        { }
        public virtual void OnEnterAggro()
        { }

        public virtual void OnExitAggro()
        { }

        public virtual void OnEnterRoam()
        { }

        public virtual void OnExitRoam()
        { }

        public virtual void OnLevelUp()
        { }

        public virtual void OnRefreshSpecDependantSkills()
        { }

        public void OnGroupMemberAttacked(AttackData ad)
        {
            if (FSM.GetState(eFSMStateType.CAMP) == FSM.GetCurrentState())
            {
                // Camp-mode aggro filter. Original behaviour was tight to the
                // bot's own AggroRange (250/550) which silenced the camp the
                // moment anyone took a hit from outside their personal zone.
                // We now propagate aggressively — any victim in the group, or
                // any attacker close to ANY group member, wakes the camp.
                MimicGroup mg = Body.Group?.MimicGroup;
                bool isIncomingPull = mg != null
                    && mg.IncomingPullTarget == ad.Attacker;
                bool victimIsGroupMember = Body.Group != null
                    && ad.Target is GameLiving victim
                    && Body.Group.IsInTheGroup(victim);

                // Probe: is the attacker within AggroRange of *some* group
                // member, not just us? Avoids the silent-camp bug where the
                // puller / scout is hit 1000u away and only the puller
                // himself sees the attacker.
                bool attackerNearGroup = false;
                if (!isIncomingPull && !victimIsGroupMember && Body.Group != null)
                {
                    foreach (GameLiving gl in Body.Group.GetMembersInTheGroup())
                    {
                        if (gl == null || !gl.IsAlive) continue;
                        if (gl.IsWithinRadius(ad.Attacker, AggroRange))
                        {
                            attackerNearGroup = true;
                            break;
                        }
                    }
                }

                if (!isIncomingPull
                    && !victimIsGroupMember
                    && !attackerNearGroup
                    && !Body.IsWithinRadius(ad.Attacker, AggroRange))
                    return;
            }

            switch (ad.AttackResult)
            {
                case eAttackResult.Blocked:
                case eAttackResult.Evaded:
                case eAttackResult.Fumbled:
                case eAttackResult.HitStyle:
                case eAttackResult.HitUnstyled:
                case eAttackResult.Missed:
                case eAttackResult.Parried:
                AddToAggroList(ad.Attacker, 1);
                break;
            }

            if (FSM.GetState(eFSMStateType.AGGRO) != FSM.GetCurrentState() && !IsHealer)
                FSM.SetCurrentState(eFSMStateType.AGGRO);
        }

        /// <summary>
        /// Group-combat assist scan. Walks every other group member (player or
        /// mimic) and adds their current engagement target plus any active
        /// attacker to our aggro list, so the bot reacts the moment ANY
        /// member opens or receives combat — instead of waiting for the mob
        /// to actually melee-touch us, or for the leader specifically to
        /// engage. Returns true when at least one target was queued.
        ///
        /// <para>What gets surfaced per member:</para>
        /// <list type="bullet">
        ///   <item>The member's TargetObject when they are auto-attacking
        ///   or casting a harmful spell on it.</item>
        ///   <item>Every living in the member's AttackerTracker (anyone
        ///   currently hitting them).</item>
        /// </list>
        ///
        /// <para>Filters:</para>
        /// <list type="bullet">
        ///   <item>Skips dead/inactive members and self.</item>
        ///   <item>Skips targets we can't legally aggro (faction, mez state).</item>
        ///   <item>Skips targets too far from us (per <paramref name="maxRange"/>),
        ///   so a bot 10000u away doesn't sprint across the zone for a brawl.</item>
        /// </list>
        ///
        /// Used by every non-passive FSM state (Camp, FollowLeader, Idle,
        /// Roaming) so combat assist is uniform regardless of where the bot
        /// is when the group engages.
        /// </summary>

        // CC spell types that should NOT trigger group assist when a member starts
        // casting them. A real CC player mezzes/roots before the engage so the
        // group can pick targets cleanly; if the group jumps on the CC target as
        // soon as the cast starts, it just breaks the lock.
        private static bool IsCrowdControlSpell(Spell spell)
        {
            if (spell == null)
                return false;
            return spell.SpellType is eSpellType.Mez
                or eSpellType.Mesmerize
                or eSpellType.Stun
                or eSpellType.Amnesia
                or eSpellType.SpeedDecrease;
        }

        public bool ScanGroupCombat(int maxRange = 0)
        {
            Group g = Body.Group;
            if (g == null)
                return false;

            if (maxRange <= 0)
                maxRange = MAX_AGGRO_LIST_DISTANCE;

            bool found = false;

            foreach (GameLiving member in g.GetMembersInTheGroup())
            {
                if (member == null || !member.IsAlive || member == Body)
                    continue;

                // (1) Member is actively engaging a hostile of their own.
                // Note: `IsAttacking` flips to true the moment the auto-attack
                // *timer* starts, even before the first swing — so a player who
                // just target-selects with auto-attack on would pull the whole
                // group instantly. Tighten to "has actually exchanged blows in
                // the last 2s" so target-selection alone never triggers the
                // group follow. Harmful spell-cast still counts as engagement
                // EXCEPT for CC spells (mez/root/snare/stun/amnesia) — those
                // are pre-pull tools and the group attacking the target would
                // immediately break the CC.
                bool castIsHarmfulNonCc = member.IsCasting
                                          && member.castingComponent?.SpellHandler?.Spell is Spell mSpell
                                          && mSpell.IsHarmful
                                          && !IsCrowdControlSpell(mSpell);
                bool engaging = member.InCombatInLast(2000) || castIsHarmfulNonCc;

                // Also count engagement by the member's controlled pet (Theurgist
                // air/cold/earth swarms, Cabalist/Spiritmaster pets, Animist
                // turrets). Without this the group would idle while a pet caster
                // is actively melting the target through their pets.
                GameLiving petBody = (member as GameNPC)?.ControlledBrain?.Body
                                     ?? (member as GamePlayer)?.ControlledBrain?.Body;
                bool petEngaging = petBody != null
                                   && (petBody.InCombatInLast(2000)
                                       || (petBody.IsCasting
                                           && petBody.castingComponent?.SpellHandler?.Spell?.IsHarmful == true
                                           && !IsCrowdControlSpell(petBody.castingComponent.SpellHandler.Spell)));
                if (petEngaging)
                    engaging = true;

                // Pick the target only if it's the SAME entity that's actually
                // in combat with the member. `InCombatInLast(2000)` stays true
                // for 2s after ANY combat ended, so a player who finishes one
                // fight and target-selects another mob within that window
                // would otherwise drag the group to the new (unrelated) target.
                // Require a concrete combat link: member was hit by it, OR
                // member hit it, OR we have a harmful cast in flight on it,
                // OR our pet is actually swinging on it.
                GameLiving engageTarget = null;
                if (engaging)
                {
                    GameLiving mTgt = member.TargetObject as GameLiving;
                    if (mTgt != null && mTgt.IsAlive && mTgt.ObjectState == GameObject.eObjectState.Active && CanAggroTarget(mTgt))
                    {
                        bool memberWasHitByTarget = member.attackComponent?.AttackerTracker?.Attackers?.Contains(mTgt) == true;
                        bool targetWasHitByMember = mTgt.attackComponent?.AttackerTracker?.Attackers?.Contains(member) == true;
                        bool castingAtTarget = castIsHarmfulNonCc && member.castingComponent?.SpellHandler?.Target == mTgt;
                        if (memberWasHitByTarget || targetWasHitByMember || castingAtTarget)
                            engageTarget = mTgt;
                    }

                    if (engageTarget == null && petBody?.TargetObject is GameLiving pTgt
                        && pTgt.IsAlive && pTgt.ObjectState == GameObject.eObjectState.Active && CanAggroTarget(pTgt))
                    {
                        // Pet path: only adopt if pet is in actual combat with this target.
                        bool petWasHitBy = petBody.attackComponent?.AttackerTracker?.Attackers?.Contains(pTgt) == true;
                        bool petHitTarget = pTgt.attackComponent?.AttackerTracker?.Attackers?.Contains(petBody) == true;
                        if (petWasHitBy || petHitTarget)
                            engageTarget = pTgt;
                    }
                }

                if (engageTarget != null && Body.IsWithinRadius(engageTarget, maxRange))
                {
                    if (!AggroList.ContainsKey(engageTarget))
                        AddToAggroList(engageTarget, 1);
                    found = true;
                }

                // (2) Someone is hitting the member right now — engage the
                //     attacker so a mob beating on a healer/caster doesn't go
                //     untouched while everyone else clings to the assist target.
                if (member.attackComponent?.AttackerTracker != null)
                {
                    foreach (GameLiving attacker in member.attackComponent.AttackerTracker.Attackers)
                    {
                        if (attacker == null || !attacker.IsAlive)
                            continue;
                        if (attacker == Body)
                            continue;
                        if (!CanAggroTarget(attacker))
                            continue;
                        if (!Body.IsWithinRadius(attacker, maxRange))
                            continue;
                        if (!AggroList.ContainsKey(attacker))
                            AddToAggroList(attacker, 1);
                        found = true;
                    }
                }
            }

            // (3) Pre-hit aggro detection. Paths (1) and (2) cover post-event
            //     signals (someone is actively attacking / has been hit), but
            //     they miss the window between a mob aggroing a member and
            //     landing its first swing. Without this branch, the camp /
            //     group sits idle while a mob runs across the field at the
            //     healer. We scan nearby hostile NPCs and engage any whose
            //     own aggro list / current target includes a group member.
            int scanRange = Math.Min(maxRange, MAX_AGGRO_DISTANCE);
            foreach (GameNPC npc in Body.GetNPCsInRadius((ushort)scanRange))
            {
                if (npc == null || !npc.IsAlive)
                    continue;
                if (npc is MimicNPC || npc is GameTaxi or GameTrainingDummy)
                    continue;
                if (npc == Body)
                    continue;
                if (g.IsInTheGroup(npc))
                    continue;
                if (!CanAggroTarget(npc))
                    continue;
                if (AggroList.ContainsKey(npc))
                    continue;

                // STRICT engagement signal: a mob just having `TargetObject` set
                // to a group member is NOT enough. Some mobs flag-look at any
                // player who target-selects them, so the legacy
                // `npc.TargetObject is GameLiving && IsInTheGroup` check would
                // pull the group every time the leader clicked a passing hostile.
                //
                // We require one of:
                //  - the mob is currently swinging (AttackState true) on a group member
                //  - the mob's brain has aggro AND a group member is in its aggro list
                //  - the mob already has a HARMFUL effect dispatched on a group member
                //    (DoT/snare/CC landing)
                bool targetsGroup = false;
                if (npc.attackComponent?.AttackState == true
                    && npc.TargetObject is GameLiving tgt
                    && g.IsInTheGroup(tgt))
                {
                    targetsGroup = true;
                }

                if (!targetsGroup
                    && npc.Brain is StandardMobBrain mobBrain
                    && mobBrain.HasAggro)
                {
                    var ordered = mobBrain.GetOrderedAggroList();
                    if (ordered != null)
                    {
                        for (int i = 0; i < ordered.Count; i++)
                        {
                            GameLiving aggroed = ordered[i].Living;
                            if (aggroed != null && g.IsInTheGroup(aggroed))
                            {
                                targetsGroup = true;
                                break;
                            }
                        }
                    }
                }

                if (targetsGroup)
                {
                    AddToAggroList(npc, 1);
                    found = true;
                }
            }

            return found;
        }

        public virtual bool CheckProximityAggro(int aggroRange)
        {
            //FireAmbientSentence();

            if (PvPMode || AggroLevel > 0 && AggroRange > 0 && Body.CurrentSpellHandler == null && !HasAggro && !_aggroLosCheckListener.HasPendingLosChecks)
            {
                CheckPlayerAggro();
                CheckNPCAggro(aggroRange);
            }

            // Some calls rely on this method to return if there's something in the aggro list, not necessarily to perform a proximity aggro check.
            // But this doesn't necessarily return whether or not the check was positive, only the current state (LoS checks take time).
            return HasAggro;
        }

        /// <summary>
        /// Check for aggro against players
        /// </summary>
        protected virtual void CheckPlayerAggro()
        {
            foreach (GamePlayer player in Body.GetPlayersInRadius((ushort)AggroRange))
            {
                if (!CanAggroTarget(player))
                    continue;

                if (player.Steed != null)
                    continue;

                if (player.effectListComponent.ContainsEffectForEffectType(eEffect.Shade))
                    continue;

                if (Properties.CHECK_LOS_BEFORE_AGGRO)
                    SendAggroLosCheck(player, player);
                else
                {
                    AddToAggroList(player);
                    return;
                }

                // We don't know if the LoS check will be positive, so we have to ask other players
            }
        }

        /// <summary>
        /// Check for aggro against close NPCs
        /// </summary>
        protected virtual void CheckNPCAggro(int aggroRange)
        {
            foreach (GameNPC npc in Body.GetNPCsInRadius((ushort)aggroRange))
            {
                if (!CanAggroTarget(npc))
                    continue;

                if (npc is GameTaxi or GameTrainingDummy)
                    continue;

                if (Properties.CHECK_LOS_BEFORE_AGGRO)
                {
                    // Check LoS if either the target or the current mob is a pet
                    if (npc.Brain is ControlledMobBrain theirControlledNpcBrain && theirControlledNpcBrain.GetPlayerOwner() is GamePlayer theirOwner)
                    {
                        SendAggroLosCheck(theirOwner, npc);
                        continue;
                    }
                }

                AddToAggroList(npc);

                //return;
            }
        }

        protected void SendAggroLosCheck(GamePlayer losChecker, GameObject target)
        {
            if (losChecker.Out.SendLosCheckRequest(Body, target, _aggroLosCheckListener))
                _aggroLosCheckListener.OnLosCheckStarted();
        }

        public virtual void FireAmbientSentence()
        {
            if (Body.ambientTexts != null && Body.ambientTexts.Any(item => item.Trigger == "seeing"))
            {
                // Check if we can "see" players and fire off ambient text
                List<GamePlayer> currentPlayersSeen = GameLoop.GetListForTick<GamePlayer>();

                foreach (GamePlayer player in Body.GetPlayersInRadius((ushort)AggroRange))
                {
                    if (!PlayersSeen.Contains(player))
                    {
                        Body.FireAmbientSentence(GameNPC.eAmbientTrigger.seeing, player);
                        PlayersSeen.Add(player);
                    }

                    currentPlayersSeen.Add(player);
                }

                for (int i = PlayersSeen.Count - 1; i >= 0; i--)
                {
                    if (!currentPlayersSeen.Contains(PlayersSeen[i]))
                        PlayersSeen.SwapRemoveAt(i);
                }
            }
        }

        /// <summary>
        /// <summary>
        /// Adaptive tick interval: 500ms when we are in combat or have a player
        /// or live target near us (responsive AI), 2000ms when truly idle (no
        /// player within 5000u, not in combat, not following). Cuts CPU usage
        /// dramatically when hundreds of frontier bots are roaming far from
        /// any human player. Sampled cheaply — no full region scan.
        /// </summary>
        private long _nextIdleCheckMs;
        private eMimicActivity _activity = eMimicActivity.Active;

        /// <summary>
        /// Coarse activity bucket driving ThinkInterval, exposed so the
        /// population manager can decide when to hibernate / despawn a bot.
        /// </summary>
        public eMimicActivity Activity => _activity;

        public enum eMimicActivity
        {
            /// <summary>Active fight or pulling — fastest tick (500ms).</summary>
            Active,
            /// <summary>Player within 5000u — medium tick (1500ms).</summary>
            Idle,
            /// <summary>Player only within 5000–10000u — slow tick (4000ms).</summary>
            Dormant,
            /// <summary>No player within 10000u for >30s — minimal tick (8000ms), eligible for despawn.</summary>
            Hibernating,
        }

        /// <summary>
        /// Timestamp (GameLoopTime ms) of the last tick during which a player
        /// was observed within 10000u. Used by the population manager to
        /// hibernate bots whose zone has emptied out.
        /// </summary>
        public long LastSeenByPlayerTick { get; private set; }

        public override int ThinkInterval
        {
            get
            {
                if (Body == null)
                    return 2000;

                if (Body.InCombat || HasAggro || IsPulling)
                {
                    _activity = eMimicActivity.Active;
                    return 500;
                }

                long now = GameLoop.GameLoopTime;

                // Re-evaluate activity tier only every 5s — full proximity scan
                // is the most expensive call in the tick. The 4-tier model
                // collapses ~5x more bots than the binary fast/slow split for
                // the same CPU budget.
                if (now >= _nextIdleCheckMs)
                {
                    _nextIdleCheckMs = now + 5000;

                    bool playerNear = false;
                    bool playerMid = false;
                    foreach (var p in Body.GetPlayersInRadius(10000))
                    {
                        if (p == null) continue;
                        int d = Body.GetDistanceTo(p);
                        if (d <= 5000) { playerNear = true; break; }
                        playerMid = true;
                    }

                    if (playerNear)
                    {
                        _activity = eMimicActivity.Idle;
                        LastSeenByPlayerTick = now;
                    }
                    else if (playerMid)
                    {
                        _activity = eMimicActivity.Dormant;
                        LastSeenByPlayerTick = now;
                    }
                    else if (now - LastSeenByPlayerTick > 30_000)
                        _activity = eMimicActivity.Hibernating;
                    else
                        _activity = eMimicActivity.Dormant;
                }

                return _activity switch
                {
                    eMimicActivity.Idle        => 1500,
                    eMimicActivity.Dormant     => 4000,
                    eMimicActivity.Hibernating => 8000,
                    _                          => 500,
                };
            }
        }

        /// <summary>
        /// If this brain is part of a formation, it edits it's values accordingly.
        /// </summary>
        /// <param name="x">The x-coordinate to refer to and change</param>
        /// <param name="y">The x-coordinate to refer to and change</param>
        /// <param name="z">The x-coordinate to refer to and change</param>
        public virtual bool CheckFormation(ref int x, ref int y, ref int z)
        {
            return false;
        }

        /// <summary>
        /// Checks the Abilities
        /// </summary>
        public virtual void CheckDefensiveAbilities()
        {
            if (Body.Abilities == null || Body.Abilities.Count <= 0)
                return;

            foreach (Ability ab in Body.Abilities.Values)
            {
                switch (ab.KeyName)
                {
                    case Abilities.Intercept:
                    {
                        //if (Body.Group != null)
                        //{
                        //    GameLiving interceptTarget;
                        //    List<GameLiving> interceptTargets = new List<GameLiving>();

                        //    foreach (GameLiving groupMember in Body.Group.GetMembersInTheGroup())
                        //    {
                        //        if (groupMember is MimicNPC mimic)
                        //        {
                        //            if (mimic.CharacterClass.ID == (int)eCharacterClass.Cleric ||
                        //                mimic.CharacterClass.ID == (int)eCharacterClass.Druid ||
                        //                mimic.CharacterClass.ID == (int)eCharacterClass.Healer ||
                        //                mimic.CharacterClass.ID == (int)eCharacterClass.Friar ||
                        //                mimic.CharacterClass.ID == (int)eCharacterClass.Bard ||
                        //                mimic.CharacterClass.ID == (int)eCharacterClass.Shaman)
                        //            {
                        //                interceptTargets.Add(groupMember);
                        //            }
                        //        }
                        //    }
                        //}
                        break;
                    }
                    case Abilities.Guard:
                    {
                        break;
                    }
                    case Abilities.Protect:
                    {
                        break;
                    }
                }
            }
        }

        public void CheckOffensiveAbilities()
        {
            if (Body.Abilities == null || Body.Abilities.Count <= 0)
                return;

            if (CanUseAbility())
            {
                foreach (Ability ab in Body.GetAllAbilities())
                {
                    if (Body.GetSkillDisabledDuration(ab) == 0)
                    {
                        switch (ab.KeyName)
                        {
                            case Abilities.Berserk:
                            {
                                if (Body.TargetObject is GameLiving target)
                                {
                                    if (Body.IsWithinRadius(Body.TargetObject, Body.MeleeAttackRange) &&
                                        GameServer.ServerRules.IsAllowedToAttack(Body, target, true))
                                    {
                                        ECSGameEffectFactory.Create(new(Body, BerserkAbilityHandler.DURATION, 1), static (in i) => new BerserkECSGameEffect(i));
                                        Body.DisableSkill(ab, 420000);
                                    }
                                }

                                break;
                            }

                            case Abilities.Stag:
                            {
                                if (Body.TargetObject is GameLiving target)
                                {
                                    // Parenthesis fix: original was
                                    //   (inMeleeRange && canAttack) || lowHealth
                                    // which fired Stag at any 74% HP situation
                                    // even out of range / not in combat. The
                                    // intent is "I have a valid target in melee
                                    // AND (it's a real fight OR I'm in trouble)".
                                    bool engageOk = Body.IsWithinRadius(Body.TargetObject, Body.MeleeAttackRange)
                                                    && GameServer.ServerRules.IsAllowedToAttack(Body, target, true);
                                    bool inTrouble = Body.HealthPercent < 75;
                                    if (engageOk && (inTrouble || Body.InCombat))
                                    {
                                        ECSGameEffectFactory.Create(new(Body, StagAbilityHandler.DURATION, 1), ab.Level, static (in i, level) => new StagECSGameEffect(i, level));
                                        Body.DisableSkill(ab, 900000);
                                    }
                                }

                                break;
                            }

                            case Abilities.Triple_Wield:
                            {
                                if (Body.TargetObject is GameLiving target)
                                {
                                    if (Body.IsWithinRadius(Body.TargetObject, Body.MeleeAttackRange) &&
                                        GameServer.ServerRules.IsAllowedToAttack(Body, target, true))
                                    {
                                        ECSGameEffectFactory.Create(new(Body, 30000, 1), static (in i) => new TripleWieldECSGameEffect(i));
                                        Body.DisableSkill(ab, 420000);
                                    }
                                }

                                break;
                            }

                            case Abilities.DirtyTricks:
                            {
                                if (Body.TargetObject is GameLiving target)
                                {
                                    IGamePlayer gamePlayer = target as IGamePlayer;

                                    if (gamePlayer != null && gamePlayer.CharacterClass.ClassType == eClassType.ListCaster)
                                        break;

                                    if (Body.IsWithinRadius(Body.TargetObject, Body.MeleeAttackRange) &&
                                        GameServer.ServerRules.IsAllowedToAttack(Body, target, true))
                                    {
                                        ECSGameEffectFactory.Create(new(Body, 30000, 1), static (in i) => new DirtyTricksECSGameEffect(i));
                                        Body.DisableSkill(ab, 420000);
                                    }
                                }

                                break;
                            }

                            case Abilities.ChargeAbility:
                            {
                                if (Body.TargetObject is GameLiving target &&
                                    GameServer.ServerRules.IsAllowedToAttack(Body, target, true) &&
                                    !Body.IsWithinRadius(target, 500))
                                {
                                    ChargeAbility charge = Body.GetAbility<ChargeAbility>();

                                    if (charge != null && Body.GetSkillDisabledDuration(charge) <= 0)
                                        charge.Execute(Body);
                                }

                                break;
                            }

                            case Abilities.Sprint:
                            {
                                // Use sprint to close a melee gap when we still
                                // have stamina. Capped so we never sprint with
                                // tank-level endurance reserves. 2H specs prefer
                                // saving endurance for their styles.
                                if (Body.TargetObject is not GameLiving sprintTarget)
                                    break;

                                if (MimicBody.IsSprinting)
                                    break;

                                if (Body.EndurancePercent <= 40)
                                    break;

                                if (MimicBody.MimicSpec != null && MimicBody.MimicSpec.Is2H)
                                    break;

                                if (!GameServer.ServerRules.IsAllowedToAttack(Body, sprintTarget, true))
                                    break;

                                int dist = Body.GetDistanceTo(sprintTarget);
                                if (dist <= Body.MeleeAttackRange + 50 || dist > 2500)
                                    break;

                                MimicBody.Sprint(true);
                                break;
                            }
                        }
                    }
                }
            }
        }

        private bool CanUseAbility()
        {
            if (!Body.IsAlive ||
                Body.IsMezzed ||
                Body.IsStunned ||
                Body.IsSitting)
                return false;

            return true;
        }

        public bool SetGuard(GameLiving target, out bool ourEffect)
        {
            if (target != null && target != Body)
            {
                GuardAbilityHandler.CheckExistingEffectsOnTarget(Body, target, true, out bool foundOurEffect, out GuardECSGameEffect existingEffectFromAnotherSource);

                ourEffect = foundOurEffect;

                if (foundOurEffect)
                    return false;

                if (existingEffectFromAnotherSource != null)
                    return false;

                GuardAbilityHandler.CancelOurEffectThenAddOnTarget(Body, target);

                return true;
            }

            ourEffect = false;
            return false;
        }

        public bool SetProtect(GameLiving target, out bool ourEffect)
        {
            if (target != null && target != Body)
            {
                ProtectAbilityHandler.CheckExistingEffectsOnTarget(Body, target, true, out bool foundOurEffect, out ProtectECSGameEffect existingEffectFromAnotherSource);

                ourEffect = foundOurEffect;

                if (foundOurEffect)
                    return false;

                if (existingEffectFromAnotherSource != null)
                    return false;

                ProtectAbilityHandler.CancelOurEffectThenAddOnTarget(Body, target);

                return true;
            }

            ourEffect = false;
            return false;
        }

        public bool SetIntercept(GameLiving target, out bool ourEffect)
        {
            if (target != null && target != Body)
            {
                InterceptAbilityHandler.CheckExistingEffectsOnTarget(Body, target, true, out bool foundOurEffect, out InterceptECSGameEffect existingEffectFromAnotherSource);

                ourEffect = foundOurEffect;

                if (foundOurEffect)
                    return false;

                if (existingEffectFromAnotherSource != null)
                    return false;

                InterceptAbilityHandler.CancelOurEffectThenAddOnTarget(Body, target);

                return true;
            }

            ourEffect = false;
            return false;
        }

        #endregion AI

        #region MimicGroup AI

        #region MainPuller

        // Tracks how many mobs have been committed in the current pull chain.
        // Resets to zero whenever we start a fresh pull cycle (no live target
        // chasing us). Used to gate chain pulls against the group's budget.
        private int _chainPullCount;

        /// <summary>
        /// Number of additional shots fired in the current chain-pull cycle
        /// (0 means we are NOT chain-pulling; >0 means the puller has
        /// already locked one mob this cycle and is now stacking another).
        /// Read by IsChainPullingTrigger so the puller can call out
        /// "Another one incoming!" as soon as the second arrow flies.
        /// </summary>
        public int ChainPullCount => _chainPullCount;

        // Time (GameLoopTime ms) the current pull shot was fired. Used as a
        // watchdog so a pull that never resolves doesn't permanently brick
        // the puller (e.g. LoS lost mid-flight, mob despawned, path blocked).
        private long _pullStartTick;

        // Time the mana throttle activated. If it stays sticky for more than
        // MimicConfig.MIMIC_MAX_MANA_THROTTLE_MS we forcibly release it: the
        // group is either stuck (a dead caster the throttle is waiting on)
        // or the heuristic is wrong, and either way we want farming to resume.
        private long _manaThrottleSinceTick;

        public void CheckPuller()
        {
            // Pre-flight: a pull might have started but already timed out.
            // Soft-reset the puller in place — DON'T sprint all the way back
            // to spawn, that round-trip was the visible "back and forth" the
            // user observed. The next tick re-evaluates from where we are.
            if (IsPulling
                && _pullStartTick > 0
                && GameLoop.GameLoopTime - _pullStartTick > MimicConfig.MIMIC_PULL_TIMEOUT_MS)
            {
                SoftResetPullerInPlace();
                return;
            }

            if (IsPulling && Body.TargetObject != null && Body.TargetObject.ObjectState == GameObject.eObjectState.Active)
            {
                // Target died mid-pull before HasAggro registered (one-shot
                // by an intercepting DPS, or tank-cleave AoE). CheckResetPuller
                // only fires on HasAggro, so without this branch the puller
                // would idle at IsPulling=true until the 12s watchdog —
                // appearing as "frozen in place after the shot".
                if (Body.TargetObject is GameLiving dead && !dead.IsAlive)
                {
                    IsPulling = false;
                    LastTargetObject = null;
                    _committedPullTarget = null;
                    Body.StopAttack();
                    ClearAggroList();
                    MimicGroup mgDead = Body.Group?.MimicGroup;
                    if (mgDead != null && IsMainPuller)
                        mgDead.IncomingPullTarget = null;
                    Body.ReturnToSpawnPoint(Body.MaxSpeed);
                    return;
                }

                if (CheckResetPuller())
                {
                    _chainPullCount++;
                    Body.ReturnToSpawnPoint(Body.MaxSpeed);

                    // Switch back to a melee weapon after firing the pull —
                    // unless the bot's role IS the ranged engagement (archer)
                    // or relies on cast pressure (any ListCaster archetype).
                    // Previously this was a hand-maintained list of class
                    // IDs (Hunter / Ranger / Scout); driving it off the
                    // combat profile means new archer/caster specs don't
                    // need a code patch to be recognised.
                    bool stayRanged = MimicBody.CombatProfile?.HasRole(eMimicCombatRole.Archer) == true
                                      || MimicBody.CharacterClass.ClassType == eClassType.ListCaster;

                    if (!stayRanged)
                    {
                        if (MimicBody.MimicSpec.Is2H)
                            Body.SwitchWeapon(eActiveWeaponSlot.TwoHanded);
                        else
                            Body.SwitchWeapon(eActiveWeaponSlot.Standard);
                    }

                    return;
                }

                // Still drawing the bow / waiting for aggro on current target —
                // hold off chain pulling until the first arrow lands.
                return;
            }

            // Chain pull: previous arrow locked aggro and we're running back. If
            // the group can absorb more mobs, fire another arrow at a nearby
            // hostile before we reach camp. This is how an experienced scout
            // farms — one shot per mob, not one mob per pull cycle.
            if (TryChainPull())
                return;

            // Caster-puller hold: archers chain on the move, but a caster
            // (Sorc, Wiz, etc.) usually has the next mob outside its spell
            // range, so TryChainPull above returned false. Without this
            // guard the next block falls through to PerformPull → Body.Follow
            // on a fresh target, sending the puller running forward again
            // while the just-aggro'd mob is still en route — producing the
            // visible "advance / retreat / advance" oscillation the user
            // reports. Hold position at spawn until the previous mob engages
            // (its InCombat flips) or dies. Once that happens, LastTargetObject
            // is cleared downstream and the natural fresh-pull path resumes.
            if (_chainPullCount > 0
                && LastTargetObject is GameLiving prevPull
                && prevPull.IsAlive
                && prevPull.ObjectState == GameObject.eObjectState.Active
                && !prevPull.InCombat)
            {
                Body.StopAttack();
                Body.StopCurrentSpellcast();
                if (!Body.IsWithinRadius(Body.SpawnPoint, 80))
                    Body.ReturnToSpawnPoint(Body.MaxSpeed);
                else
                    Body.StopFollowing();
                return;
            }

            if (!Body.InCombat)
            {
                if (CheckDelayPull())
                {
                    Body.StopAttack();
                    Body.StopFollowing();
                    // Drop the committed target too — if we're blocked we don't
                    // want to immediately resume on the same (possibly stale)
                    // mob the next time the gate opens.
                    _committedPullTarget = null;
                }
                else
                {
                    _chainPullCount = 0;
                    // Reuse the locked target across ticks while we close range
                    // / line up the cast. Only re-scan when we have nothing
                    // committed yet, or the previous target has become invalid.
                    GameLiving pullTarget = IsCommittedPullTargetValid()
                        ? _committedPullTarget
                        : GetPullTarget();
                    _committedPullTarget = pullTarget;
                    PerformPull(pullTarget);
                }
            }
        }

        /// <summary>
        /// Hard-recover the puller from a stalled pull (mob unreachable, lost
        /// LoS, despawned). Used both by the in-CheckPuller timeout and the
        /// camp-state watchdog when the Pulling phase exceeds its budget.
        /// </summary>
        public void ForcePullerRecovery()
        {
            Body.StopAttack();
            Body.StopCurrentSpellcast();
            Body.StopFollowing();
            ClearAggroList();
            ResetPullerState();
            MimicGroup mg = Body.Group?.MimicGroup;
            if (mg != null)
                mg.IncomingPullTarget = null;
            Body.ReturnToSpawnPoint(Body.MaxSpeed);
        }

        /// <summary>
        /// Lightweight reset for the in-CheckPuller pull timeout. Clears just
        /// enough state to retry from the current position on the next tick —
        /// no return-to-spawn (which is what produced the visible back-and-
        /// forth), no aggro-list wipe (the rest of the camp may have valid
        /// aggro on the failed mob already). The full ForcePullerRecovery is
        /// still used by the camp-state watchdog as a hard fallback.
        /// </summary>
        private void SoftResetPullerInPlace()
        {
            Body.StopAttack();
            Body.StopCurrentSpellcast();
            Body.StopFollowing();
            IsPulling = false;
            _pullStartTick = 0;
            _committedPullTarget = null;
            MimicGroup mg = Body.Group?.MimicGroup;
            if (mg != null && IsMainPuller)
                mg.IncomingPullTarget = null;
        }

        /// <summary>
        /// Fire a chain pull on the next candidate while running back from the
        /// previous one, up to the group's pull budget. Class-agnostic:
        ///   - Archer / thrown (DistanceWeapon equipped): release another shot
        ///   - Caster (instant harmful spell): cast in motion, no run-back stall
        ///   - Caster (short cast-time pull spell ≤ 1500ms): stand & cast, then resume
        ///   - Pure melee with no ranged tag: skipped (can't chain physically)
        /// Returns true if a chain pull was initiated this tick.
        /// </summary>
        private bool TryChainPull()
        {
            if (_chainPullCount == 0)
                return false; // no chain in progress

            if (_chainPullCount >= GetMaxPullCount())
                return false; // budget reached

            if (Body.IsAttacking || Body.IsCasting)
                return false;

            // Honour the same mana gate as initial pulls — without this the
            // puller would keep chain-firing until everyone is OOM, and the
            // group would end up in over-extended combat with no resources.
            if (_pullManaThrottled)
                return false;

            GameLiving chainTarget = GetPullTarget();
            if (chainTarget == null)
                return false;

            bool hasBow = Body.Inventory?.GetItem(eInventorySlot.DistanceWeapon) != null;

            // --- Archer / thrown path: bow chain.
            if (hasBow)
            {
                int bowRange = Body.attackComponent?.AttackRange ?? 1700;
                if (!Body.IsWithinRadius(chainTarget, bowRange))
                    return false;

                Body.SwitchWeapon(eActiveWeaponSlot.Distance);
                Body.TargetObject = chainTarget;
                Body.StopFollowing();
                Body.StartAttack(chainTarget);
                IsPulling = true;
                _pullStartTick = GameLoop.GameLoopTime;
                MimicGroup mgChain = Body.Group?.MimicGroup;
                if (mgChain != null)
                    mgChain.IncomingPullTarget = chainTarget;
                return true;
            }

            // --- Caster path: chain via harmful spell.
            // Need a spellbook & at least one harmful spell available.
            if (MimicBody == null
                || (!MimicBody.CanCastInstantHarmfulSpells && !MimicBody.CanCastHarmfulSpells))
                return false;

            // AoE preference when the chain target is part of a pack — one
            // AoE volley tags the whole cluster instead of single-target
            // chain-shotting one mob at a time.
            bool chainPack = chainTarget is GameNPC chainNpc && EstimatePackSize(chainNpc) > 0;
            Spell chainSpell = SelectPullSpell(chainPack);
            if (chainSpell == null)
                return false;

            // Must be in cast range of the chain target — never abort the
            // run-back to chase a chain candidate.
            if (!Body.IsWithinRadius(chainTarget, chainSpell.Range))
                return false;

            // Non-instant spells halt the run-back. Only accept ones that
            // finish quickly (≤ 1500ms cast time) so we lose at most one tick
            // of run-back; otherwise skip chain and keep moving.
            if (!chainSpell.IsInstantCast && chainSpell.CastTime > 1500)
                return false;

            Body.TargetObject = chainTarget;
            Body.StopFollowing();
            Body.TurnTo(chainTarget);

            if (chainSpell.IsInstantCast)
                Body.CastSpell(chainSpell, MimicBody.GetSpellLineForSpell(chainSpell));
            else
                CheckOffensiveSpells(chainSpell);

            IsPulling = true;
            _pullStartTick = GameLoop.GameLoopTime;
            MimicGroup mgCaster = Body.Group?.MimicGroup;
            if (mgCaster != null)
                mgCaster.IncomingPullTarget = chainTarget;
            return true;
        }

        // Sticky throttle for group regen. Once a caster drops below 30% mana
        // the puller stops; pulling only resumes when every caster is at 80%+
        // (per user spec). The hysteresis between the two values avoids
        // flapping on regen ticks.
        private bool _pullManaThrottled;

        // Locked-in pull target. Without this, every tick spent closing range
        // would re-run GetPullTarget — and the scoring isn't fully stable
        // (pack size estimate / distance can flip between similar candidates),
        // so the puller would oscillate between mobs, turning left/right and
        // never finishing the cast. Committed once at pull start, cleared on
        // success/timeout/death.
        private GameLiving _committedPullTarget;

        private bool IsCommittedPullTargetValid()
        {
            if (_committedPullTarget == null)
                return false;
            if (!_committedPullTarget.IsAlive)
                return false;
            if (_committedPullTarget.ObjectState != GameObject.eObjectState.Active)
                return false;
            if (_committedPullTarget.CurrentRegion != Body.CurrentRegion)
                return false;
            // Stay in scan radius (with slight slack) so a mob that wandered
            // out of practical range gets reconsidered instead of chased forever.
            if (!Body.IsWithinRadius(_committedPullTarget, MimicConfig.MIMIC_PULL_SCAN_RADIUS + 500))
                return false;
            if (!GameServer.ServerRules.IsAllowedToAttack(Body, _committedPullTarget, true))
                return false;
            return true;
        }

        public bool CheckDelayPull()
        {
            // Old pull target — only block if it's still ALIVE and still in
            // combat (someone is fighting it). The previous "alive + active"
            // gate would block re-pulling forever when a mob was alive in the
            // world but unattached (e.g. the puller's shot interrupted, mob
            // walked off, never aggro'd). We now require it to be in combat
            // with the group to count as "still in flight".
            if (LastTargetObject is GameLiving lt
                && lt.IsAlive
                && lt.ObjectState == GameObject.eObjectState.Active
                && lt.InCombat
                && Body.Group != null
                && Body.IsWithinRadius(lt, MAX_AGGRO_LIST_DISTANCE))
                return true;

            // Clear stale pointer so the next tick doesn't keep evaluating it.
            if (LastTargetObject != null
                && (LastTargetObject.ObjectState != GameObject.eObjectState.Active
                    || (LastTargetObject is GameLiving dead && !dead.IsAlive)))
                LastTargetObject = null;

            // The puller itself still tries to top up first — but only its own
            // spells/sit, not "any group member is sitting" which used to brick
            // the puller for the entire group regen cycle.
            if (CheckSpells(eCheckSpellType.Defensive))
                return true;

            // Camp phase gate — never start a pull while the group is still
            // recovering. The Ready/Pulling/Engaging/Combat phases all permit
            // pulling (chain shots etc.); only Regen/PostCombat block.
            MimicGroup mg = Body.Group?.MimicGroup;
            if (mg != null && mg.CampPoint != null)
            {
                if (mg.CampPhase == MimicGroup.eCampPhase.Regen
                    || mg.CampPhase == MimicGroup.eCampPhase.PostCombat
                    || mg.CampPhase == MimicGroup.eCampPhase.Inactive)
                    return true;
            }

            // Group regen gate (per user spec): puller stops the moment any
            // caster drops below MimicConfig.MIMIC_PULL_MANA_STOP_PCT (default 30%),
            // resumes the moment every caster recovers back above
            // MimicConfig.MIMIC_PULL_MANA_RESUME_PCT (default 35%). The
            // tight hysteresis prevents flap on regen ticks without forcing
            // the long 80% wait that produced visible idle gaps. The human
            // leader is excluded — their mana flow is decoupled from the bot
            // regen cycle, and a player intentionally holding mana would
            // otherwise wedge the puller forever.
            if (Body.Group != null)
            {
                int manaStopPct = MimicConfig.MIMIC_PULL_MANA_STOP_PCT;
                int manaResumePct = Math.Max(manaStopPct, MimicConfig.MIMIC_PULL_MANA_RESUME_PCT);

                bool anyLow = false;
                bool allHigh = true;

                foreach (GameLiving gl in Body.Group.GetMembersInTheGroup())
                {
                    if (gl == null || !gl.IsAlive || gl.MaxMana <= 0)
                        continue;
                    if (gl is GamePlayer)
                        continue;

                    int pct = gl.ManaPercent;
                    if (pct < manaStopPct) anyLow = true;
                    if (pct < manaResumePct) allHigh = false;
                }

                if (anyLow)
                {
                    if (!_pullManaThrottled)
                        _manaThrottleSinceTick = GameLoop.GameLoopTime;
                    _pullManaThrottled = true;
                }
                else if (allHigh)
                {
                    _pullManaThrottled = false;
                    _manaThrottleSinceTick = 0;
                }

                if (_pullManaThrottled)
                {
                    // Escape valve: if the throttle has been stuck for too long,
                    // it almost always means a caster is dead or disconnected
                    // and will never recover. Lift the throttle and let the
                    // group keep farming — better than standing idle forever.
                    if (_manaThrottleSinceTick > 0
                        && GameLoop.GameLoopTime - _manaThrottleSinceTick > MimicConfig.MIMIC_MAX_MANA_THROTTLE_MS)
                    {
                        _pullManaThrottled = false;
                        _manaThrottleSinceTick = 0;
                        return false;
                    }
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Resets the puller's transient state. Called when the bot (re-)enters
        /// the CAMP state so a stale LastTargetObject or sticky mana throttle
        /// from a previous session doesn't permanently block pulling.
        /// </summary>
        public void ResetPullerState()
        {
            LastTargetObject = null;
            IsPulling = false;
            _pullManaThrottled = false;
            _manaThrottleSinceTick = 0;
            _chainPullCount = 0;
            _pullStartTick = 0;
            _committedPullTarget = null;

            // Camp state is shared — clear the group's incoming-pull pointer
            // too so the rest of the camp stops chasing a phantom mob.
            MimicGroup mg = Body.Group?.MimicGroup;
            if (mg != null && IsMainPuller)
                mg.IncomingPullTarget = null;
        }

        // Pull scan / pack radii now live on MimicConfig; see MIMIC_PULL_SCAN_RADIUS
        // and MIMIC_PULL_PACK_RADIUS. Defaults match the previous 3600 / 500 constants.

        /// <summary>
        /// How many mobs the group can realistically chew through at once.
        /// 1 by default; each CC-capable mimic in the group adds slots so a
        /// well-formed group with CC + tank can grab a small pack on purpose
        /// instead of bouncing 3 trash mobs one at a time. With multiple
        /// healers we tolerate one extra add to sustain bigger chains.
        /// </summary>
        private int GetMaxPullCount()
        {
            MimicGroup mg = Body.Group?.MimicGroup;

            if (mg == null)
                return 1;

            // No tank — can't safely chain. Force single-pull.
            if (mg.MainTank == null || !mg.MainTank.IsAlive)
                return 1;

            int ccCount = 0;
            int healerCount = 0;
            int aliveMembers = 0;
            foreach (GameLiving gm in Body.Group.GetMembersInTheGroup())
            {
                if (gm == null || !gm.IsAlive)
                    continue;
                aliveMembers++;

                if (gm is MimicNPC m)
                {
                    if (m.CanCastCrowdControlSpells)
                        ccCount++;
                    if (m.MimicBrain != null && m.MimicBrain.IsHealer)
                        healerCount++;
                }
            }

            // Base 2 (a tank can comfortably hold a small pack) + 1 per CC +
            // 1 per healer past the first (sustain) + 1 bonus for a full 6+
            // member group (more raw DPS = bigger pack). The previous base-1
            // made the puller score for *lonely* mobs (idealPack=0) which is
            // why single-class pulls felt one-by-one.
            int budget = 2 + ccCount;
            if (healerCount >= 2)
                budget++;
            if (aliveMembers >= 6)
                budget++;

            // Cap at 5 — playtests show larger pulls regularly outpace heals
            // regardless of CC count, especially when mob density is high.
            return Math.Clamp(budget, 1, 5);
        }

        /// <summary>
        /// Count of nearby hostile mobs around the candidate — proxy for how
        /// many adds BAF will pull along when the candidate is attacked.
        /// Cheap heuristic: same-realm hostile NPCs within MimicConfig.MIMIC_PULL_PACK_RADIUS.
        /// </summary>
        private int EstimatePackSize(GameNPC candidate)
        {
            if (candidate == null)
                return 0;

            int count = 0;
            foreach (GameNPC neighbor in candidate.GetNPCsInRadius((ushort)MimicConfig.MIMIC_PULL_PACK_RADIUS))
            {
                if (neighbor == candidate)
                    continue;
                if (neighbor == null || !neighbor.IsAlive)
                    continue;
                if (neighbor is GameTaxi or GameTrainingDummy)
                    continue;
                if (neighbor is MimicNPC)
                    continue;
                if (neighbor.Brain is StandardMobBrain mb && mb.HasAggro)
                    continue;
                if (!GameServer.ServerRules.IsAllowedToAttack(Body, neighbor, true))
                    continue;
                count++;
            }

            return count;
        }

        public GameLiving GetPullTarget()
        {
            // Always stand the puller up before scanning — the legacy "if
            // sitting, return null" silently blocked the entire pull cycle the
            // moment the bot sat down between two pulls. With the auto-stand
            // here we just take the cost of the next cast/shot.
            if (Body.IsSitting)
                MimicBody?.Sit(false);

            if (Body.IsAttacking || Body.IsCasting)
                return null;

            if (Body.Group == null || Body.Group.MimicGroup == null)
                return null;

            // Honour the group's chosen CC pull target list when present.
            GameLiving preMezPick = Body.Group.MimicGroup.PickRandomCcTarget();
            if (preMezPick != null)
                return preMezPick;

            // Tiered scan: strict first (con/grey/no-add filtering), then a
            // progressively looser fallback so a dense camp or low-con zone
            // never leaves the puller with nothing to do.
            for (int tier = 0; tier < 3; tier++)
            {
                GameLiving picked = ScanPullCandidates(tier);
                if (picked != null)
                    return picked;
            }

            return null;
        }

        /// <summary>
        /// One scan pass over candidates with the supplied strictness tier:
        ///   0 — strict: con filter, no grey, no already-aggroed, pack budget
        ///   1 — relaxed: allow grey con + accept slightly oversized packs
        ///   2 — desperate: any attackable hostile in range, pack-size irrelevant
        /// Returns the best-scored target for the tier or null.
        /// </summary>
        private GameLiving ScanPullCandidates(int tier)
        {
            int conFilter = Body.Group.MimicGroup.ConLevelFilter;
            Point2D pullFrom = Body.Group.MimicGroup.PullFromPoint;
            Point3D camp = Body.Group.MimicGroup.CampPoint;

            int maxPull = GetMaxPullCount();
            bool wantPack = maxPull > 1;
            int packAllowed = tier switch
            {
                0 => maxPull,
                1 => maxPull + 1,
                _ => int.MaxValue,
            };
            bool allowGrey = tier >= 1;
            bool allowAggroed = tier >= 2;
            bool applyConFilter = tier < 2;

            GameLiving best = null;
            int bestScore = int.MaxValue;

            foreach (GameNPC npc in Body.GetNPCsInRadius((ushort)MimicConfig.MIMIC_PULL_SCAN_RADIUS))
            {
                if (npc == null || !npc.IsAlive || npc.ObjectState != GameObject.eObjectState.Active)
                    continue;

                if (npc is MimicNPC otherBot && otherBot.Group == Body.Group)
                    continue;

                if (npc is GameTaxi or GameTrainingDummy)
                    continue;

                if (!GameServer.ServerRules.IsAllowedToAttack(Body, npc, true))
                    continue;

                if (applyConFilter && Body.GetConLevel(npc) < conFilter)
                    continue;

                if (!allowGrey && Body.IsObjectGreyCon(npc))
                    continue;

                if (!allowAggroed && npc.Brain is StandardMobBrain mb && mb.HasAggro)
                    continue;

                int pack = EstimatePackSize(npc);
                if (pack + 1 > packAllowed)
                    continue;

                int score;
                if (pullFrom != null)
                    score = npc.GetDistance(pullFrom);
                else if (camp != null)
                    score = npc.GetDistance(new Point2D(camp.X, camp.Y));
                else
                    score = Body.GetDistanceTo(npc);

                if (wantPack)
                {
                    int idealPack = maxPull - 1;
                    int packMiss = Math.Abs(pack - idealPack);
                    score += packMiss * 300;
                }
                else
                {
                    score += pack * 2000;
                }

                // Fallback tiers pay a flat penalty so a strict-tier hit on a
                // later scan tick still wins against a desperate-tier hit
                // from this tick.
                score += tier * 500;

                if (score < bestScore)
                {
                    bestScore = score;
                    best = npc;
                }
            }

            return best;
        }

        private bool CheckResetPuller()
        {
            if (Body.TargetObject is GameNPC npcTarget && npcTarget.Brain is StandardMobBrain mobBrain && mobBrain.HasAggro)
            {
                LastTargetObject = Body.TargetObject;
                IsPulling = false;
                _committedPullTarget = null;
                Body.StopAttack();
                ClearAggroList();

                return true;
            }

            return false;
        }

        public void PerformPull(GameLiving target)
        {
            if (target == null)
                return;

            MimicGroup mg = Body.Group?.MimicGroup;

            // Pre-announce the candidate so DPS/CC/tank can pre-stage. We do
            // this even before the shot/cast actually fires — worst case it
            // gets cleared on the next tick if the pull aborts.
            if (mg != null)
                mg.IncomingPullTarget = target;

            // Archer-style: distance weapon takes priority when equipped.
            // StartAttack handles chase-to-bow-range + auto-fire, so it's safe
            // to commit IsPulling immediately.
            if (Body.Inventory.GetItem(eInventorySlot.DistanceWeapon) != null)
            {
                Body.SwitchWeapon(eActiveWeaponSlot.Distance);
                Body.StartAttack(target);
                CommitPullStart(mg, target);
                return;
            }

            // Caster-style: no bow but can throw a ranged harmful spell.
            // Prefer an AoE damage spell when the target has BAF neighbours
            // so every mob in the cluster aggros at once — much cleaner than
            // single-tagging and hoping the BAF mechanic chains them in.
            bool pullPack = target is GameNPC pulledNpc && EstimatePackSize(pulledNpc) > 0;
            Spell pullSpell = SelectPullSpell(pullPack);

            if (pullSpell == null)
            {
                // Last resort: melee body-pull. Real players walk up, land one hit,
                // then sprint back to camp so the mob follows them home alone rather
                // than ambushing whoever was standing still. Without that retreat the
                // puller stays in melee and any BAF add joins on top of them.
                Body.StartAttack(target);
                CommitPullStart(mg, target);
                StartBodyPullRetreat(target);
                return;
            }

            // Stop any current move/attack so we can position cleanly for the cast.
            Body.TargetObject = target;

            int castRange = Math.Max(200, pullSpell.Range - 100);

            if (!Body.IsWithinRadius(target, pullSpell.Range))
            {
                // Close the gap. We deliberately do NOT set IsPulling yet —
                // if we did, the next tick's CheckPuller would take the
                // "waiting on aggro" branch (since IsPulling=true) and never
                // call PerformPull again, leaving the puller frozen at cast
                // range without firing. By keeping IsPulling=false here, the
                // next tick re-enters PerformPull and actually casts once
                // we're in range.
                Body.Follow(target, castRange, 5000);
                return;
            }

            // In range: face the target and cast. Stop following for the cast.
            Body.StopFollowing();
            Body.TurnTo(target);

            if (MimicBody == null)
                return;

            // Use CheckOffensiveSpells if the spell is non-instant so the normal
            // duration/effect checks apply; otherwise cast directly.
            if (pullSpell.IsInstantCast)
                Body.CastSpell(pullSpell, MimicBody.GetSpellLineForSpell(pullSpell));
            else
                CheckOffensiveSpells(pullSpell);

            CommitPullStart(mg, target);
        }

        // Body-pull retreat: schedule a one-shot timer that stops the melee
        // swing and moves the puller back to the camp point. Without this, the
        // melee body-pull keeps swinging on the mob in place and any BAF add
        // chains to the puller standing there alone.
        private void StartBodyPullRetreat(GameLiving target)
        {
            MimicGroup mg = Body.Group?.MimicGroup;
            Point3D camp = mg?.CampPoint;
            if (camp == null)
                return;

            // 1.5s gives the first auto-swing time to land and tag the mob
            // before we drop attack and head home.
            new ECSGameTimer(Body, _ =>
            {
                if (Body == null || !Body.IsAlive || target == null || !target.IsAlive)
                    return 0;

                if (Body.IsAttacking)
                    Body.StopAttack();

                Body.WalkTo(camp, Body.MaxSpeed);
                return 0;
            }).Start(1500);
        }

        /// <summary>
        /// Marks the puller as actively in-flight on the supplied target and
        /// advances the camp phase to Pulling. Centralised so every branch of
        /// PerformPull (archer / spell / melee) commits identically, and so
        /// the "still closing range" caster branch can deliberately skip it.
        /// </summary>
        private void CommitPullStart(MimicGroup mg, GameLiving target)
        {
            IsPulling = true;
            _pullStartTick = GameLoop.GameLoopTime;
            if (mg != null)
            {
                mg.IncomingPullTarget = target;
                if (mg.CampPhase == MimicGroup.eCampPhase.Regen
                    || mg.CampPhase == MimicGroup.eCampPhase.Ready)
                    mg.SetCampPhase(MimicGroup.eCampPhase.Pulling);
            }
        }

        // Cached pull spell chosen at first request and reused for subsequent pulls.
        // The cache is keyed against MimicNPC.SpellbookRevision: if SortSpells
        // bumps the revision (level-up, spec change, item bonus refresh), the
        // next access recomputes. InvalidatePullSpellCache stays for back-compat
        // and forces a re-evaluation without needing to touch SpellbookRevision.
        private Spell _cachedPullSpell;
        private int _cachedPullSpellRevision = -1;
        private Spell _cachedAoePullSpell;
        private int _cachedAoePullSpellRevision = -1;

        public void InvalidatePullSpellCache()
        {
            _cachedPullSpell = null;
            _cachedPullSpellRevision = -1;
            _cachedAoePullSpell = null;
            _cachedAoePullSpellRevision = -1;
        }

        private int CurrentSpellbookRevision => MimicBody?.SpellbookRevision ?? -1;

        /// <summary>
        /// Picks the best spell for pulling. Two scoring modes:
        ///   <para>• <paramref name="preferAoe"/> = false (default): single-target
        ///   bias — Snare/Root, DoT, debuffs, then weakest direct damage.</para>
        ///   <para>• <paramref name="preferAoe"/> = true: pack-aware — prefer a
        ///   ranged AoE damage spell (Radius > 0, not PBAoE) so every mob in
        ///   the cluster aggros at once instead of relying on BAF
        ///   propagation. Falls back to the single-target pick when no AoE
        ///   spell exists.</para>
        /// Cached per mode so the per-tick lookup stays cheap. Cache is
        /// auto-invalidated when MimicNPC.SpellbookRevision changes. Returns
        /// null when the caster has no eligible harmful spell.
        /// </summary>
        public Spell SelectPullSpell(bool preferAoe = false)
        {
            int rev = CurrentSpellbookRevision;

            if (preferAoe)
            {
                if (_cachedAoePullSpellRevision != rev)
                {
                    _cachedAoePullSpell = ComputePullSpell(true);
                    _cachedAoePullSpellRevision = rev;
                }
                return _cachedAoePullSpell ?? _cachedPullSpellCore();
            }

            return _cachedPullSpellCore();
        }

        private Spell _cachedPullSpellCore()
        {
            int rev = CurrentSpellbookRevision;
            if (_cachedPullSpellRevision == rev)
                return _cachedPullSpell;
            _cachedPullSpell = ComputePullSpell(false);
            _cachedPullSpellRevision = rev;
            return _cachedPullSpell;
        }

        private Spell ComputePullSpell(bool preferAoe)
        {
            if (MimicBody == null)
                return null;

            // Build candidate list from both non-instant and instant harmful spells.
            // We exclude PBAoE (radius > 0 and pulse > 0 or PBAoE flag), pets-only targets and self-only spells.
            List<Spell> candidates = new();

            if (MimicBody.HarmfulSpells != null)
                candidates.AddRange(MimicBody.HarmfulSpells);
            if (MimicBody.InstantHarmfulSpells != null)
                candidates.AddRange(MimicBody.InstantHarmfulSpells);

            // For an AoE pull we additionally require Radius > 0 so the spell
            // actually hits the cluster. Single-target pulls don't filter on
            // radius (a radius=0 nuke is still a fine single-target pull).
            candidates = candidates
                .Where(s => s != null
                            && s.Range >= 500            // need real distance
                            && !s.IsPBAoE
                            && s.Target != eSpellTarget.SELF
                            && s.Target != eSpellTarget.PET
                            && (!preferAoe || s.Radius > 0))
                .ToList();

            if (candidates.Count == 0)
                return null;

            // Score: lower is better.
            int Score(Spell s)
            {
                if (preferAoe)
                {
                    // Pack-aware: prefer AoE damage that tags every mob.
                    // Snare/DoT can still be picked if no real AoE damage
                    // exists, but they sit behind damage AoE in the order.
                    switch (s.SpellType)
                    {
                        case eSpellType.DirectDamage: return 0;
                        case eSpellType.DirectDamageWithDebuff: return 1;
                        case eSpellType.DamageOverTime: return 2;
                        case eSpellType.DamageSpeedDecrease: return 3;
                        case eSpellType.Lifedrain: return 4;
                        case eSpellType.SpeedDecrease: return 5;
                        case eSpellType.Bolt: return 6;
                        default: return 10;
                    }
                }

                // Single-target bias: low-alpha, low-noise effects first.
                switch (s.SpellType)
                {
                    case eSpellType.SpeedDecrease: return 0;     // snare/root: safest puller
                    case eSpellType.DamageSpeedDecrease: return 1;
                    case eSpellType.DamageOverTime: return 2;     // DoT: low alpha damage
                    case eSpellType.Disease: return 3;
                    case eSpellType.StrengthDebuff:
                    case eSpellType.DexterityDebuff:
                    case eSpellType.StrengthConstitutionDebuff:
                    case eSpellType.DexterityQuicknessDebuff:
                    case eSpellType.MeleeDamageDebuff:
                    case eSpellType.CombatSpeedDebuff:
                    case eSpellType.ArmorFactorDebuff:
                    case eSpellType.AllStatsPercentDebuff:
                    case eSpellType.CrushSlashThrustDebuff:
                    case eSpellType.EffectivenessDebuff:
                        return 4;
                    case eSpellType.DirectDamageWithDebuff: return 5;
                    case eSpellType.Lifedrain: return 6;
                    case eSpellType.DirectDamage: return 7;
                    case eSpellType.Bolt: return 8;
                    default: return 10;
                }
            }

            // Best = lowest score, then longest range, then lowest damage
            // (single mode) / highest damage (aoe mode — we want it to bite).
            var ordered = candidates.OrderBy(Score).ThenByDescending(s => s.Range);
            return preferAoe
                ? ordered.ThenByDescending(s => s.Damage).First()
                : ordered.ThenBy(s => s.Damage).First();
        }

        #endregion MainPuller

        #region MainLeader

        public bool CheckDelayRoam()
        {
            if (Body.IsCasting || CheckSpells(eCheckSpellType.Defensive) || MimicBody.Sit(CheckStats(75)))
                return true;

            // Manual scan instead of `.Any(lambda)` — the lambda captured `this`
            // and `Body` (closure allocation per call), and CheckDelayRoam runs
            // on every Roaming tick of the group leader.
            if (Body.Group != null)
            {
                foreach (GameLiving groupMember in Body.Group.GetMembersInTheGroup())
                {
                    if (groupMember == null)
                        continue;

                    if (groupMember.IsCasting || groupMember.IsSitting)
                        return true;

                    if (groupMember is MimicNPC mimic && !mimic.InCombat)
                    {
                        MimicBrain mb = mimic.MimicBrain;
                        if (mb == null)
                            continue;
                        if (mb.CheckStats(75))
                            return true;
                        if (mb.FSM.GetCurrentState() == mb.FSM.GetState(eFSMStateType.FOLLOW_THE_LEADER)
                            && !Body.IsWithinRadius(mimic, 1000))
                            return true;
                    }
                }
            }

            return false;
        }

        #endregion MainLeader

        #region MainCC

        public void CheckMainCC()
        {
            // Pre-emptive: while the puller is still bringing the mob in, we
            // also queue the mob's BAF neighbours so the CC has a head-start
            // on locking them down BEFORE they reach the line. This is the
            // difference between a CC that mezzes adds AT THE TANK (too late,
            // healer already taking hits) and one that mezzes them in transit.
            PreMezIncomingAdds();

            // Auto-detect adds: scan the aggro list for hostile mobs that are NOT
            // the group's focus target and add them to CCTargets so the CC bot
            // mezzes them. Caps at 2 adds to avoid mezzing the entire pack.
            PopulateAddsForCC();

            if (Body.Group.MimicGroup.CcTargetCount > 0)
            {
                if (CheckSpells(eCheckSpellType.CrowdControl))
                    return;
            }

            if (!Body.InCombat && Body.Group.MimicGroup.CcTargetCount > 0)
            {
                // Drop CC targets we can no longer legitimately CC (dead, despawned,
                // no longer attacking the group). Done in-place under the dedicated
                // lock — no more "rebuild the list and replace" race.
                Body.Group.MimicGroup.ValidateCcTargets(IsValidCcTarget);
            }
        }

        /// <summary>
        /// CC pre-mez window: while the puller's pull is in flight and the mob
        /// has been hit but hasn't reached the camp yet, scan the area around
        /// the incoming mob for likely BAF adds and stage them on the CC queue.
        /// These adds will become mez targets the second they enter mez range.
        /// </summary>
        private void PreMezIncomingAdds()
        {
            MimicGroup mg = Body.Group?.MimicGroup;
            if (mg == null)
                return;

            if (mg.CampPhase != MimicGroup.eCampPhase.Pulling
                && mg.CampPhase != MimicGroup.eCampPhase.Engaging)
                return;

            if (mg.IncomingPullTarget is not GameNPC pulled || !pulled.IsAlive)
                return;

            int maxAddsToCc = ComputeMaxAddsToCc(mg);
            if (mg.CcTargetCount >= maxAddsToCc)
                return;

            // The focus mob itself stays the tank's responsibility — never mez it.
            // Only scan close neighbours; mobs further than ~600 won't actually
            // come along with the pull (BAF radius is typically smaller).
            foreach (GameNPC neighbour in pulled.GetNPCsInRadius(600))
            {
                if (neighbour == null || !neighbour.IsAlive || neighbour == pulled)
                    continue;
                if (neighbour is MimicNPC || neighbour is GameTaxi or GameTrainingDummy)
                    continue;
                if (neighbour.IsMezzed || neighbour.IsRooted)
                    continue;
                if (mg.ContainsCcTarget(neighbour))
                    continue;
                if (!GameServer.ServerRules.IsAllowedToAttack(Body, neighbour, true))
                    continue;
                // Skip mobs that are already aggro'd on something else (not our pull) —
                // attacking them would steal aggro from another fight.
                if (neighbour.Brain is StandardMobBrain mb
                    && mb.HasAggro
                    && mb.Body.TargetObject != Body
                    && (Body.Group == null || !IsTargetInGroup(mb.Body.TargetObject)))
                    continue;

                mg.AddCcTarget(neighbour);
                if (mg.CcTargetCount >= maxAddsToCc)
                    break;
            }
        }

        private bool IsTargetInGroup(GameObject candidate)
        {
            if (candidate is not GameLiving gl || Body.Group == null)
                return false;
            foreach (GameLiving gm in Body.Group.GetMembersInTheGroup())
                if (gm == gl)
                    return true;
            return false;
        }

        // Picks up to a dynamic cap of hostile mobs from the aggro list (excluding
        // the assist's current focus) and pushes them into the group's CC queue.
        // Targets are sorted by threat (proximity, low HP = easier to finish if mez
        // breaks) so the most dangerous add is mezzed first.
        //
        // The cap was a hard 2. With multiple CC casters in the group (e.g. two
        // Sorcerers + a Bard) the bottleneck was the limit, not the CC bandwidth:
        // 3 adds in => 1 free add hitting the group. Now the cap scales with the
        // count of CC-capable group members, clamped to a sane minimum/maximum so
        // we never accidentally try to mez an entire BAF pull.
        private const int MAX_ADDS_TO_CC_FLOOR = 2;
        private const int MAX_ADDS_TO_CC_CEIL = 5;

        private static int ComputeMaxAddsToCc(MimicGroup mg)
        {
            Group standardGroup = mg?.MainLeader?.Group ?? mg?.MainAssist?.Group ?? mg?.MainTank?.Group;
            if (standardGroup == null)
                return MAX_ADDS_TO_CC_FLOOR;

            int ccCapable = 0;
            foreach (GameLiving member in standardGroup.GetMembersInTheGroup())
            {
                if (member is MimicNPC mn && mn.CanCastCrowdControlSpells)
                    ccCapable++;
            }
            int cap = Math.Max(MAX_ADDS_TO_CC_FLOOR, ccCapable);
            return Math.Min(MAX_ADDS_TO_CC_CEIL, cap);
        }

        private void PopulateAddsForCC()
        {
            MimicGroup mg = Body.Group?.MimicGroup;

            if (mg == null || AggroList.Count == 0)
                return;

            GameLiving focus = mg.MainAssist?.TargetObject as GameLiving;
            int maxAddsToCc = ComputeMaxAddsToCc(mg);

            if (mg.CcTargetCount >= maxAddsToCc)
                return;

            // Build candidate list (alive, not the focus target, not already
            // queued for CC), then sort: closest first, tie-break by lowest
            // health-percent so a near-dead add gets cleaned up.
            //
            // Already-mezzed/rooted mobs are included if their effect is about
            // to expire (<3s remaining), so a CC bot can stage a re-cast and
            // refresh the lock before the gap. Otherwise they're skipped.
            List<GameLiving> candidates = new();
            foreach (var kv in AggroList)
            {
                GameLiving c = kv.Key;
                if (c == null || !c.IsAlive) continue;
                if (c == focus) continue;
                if (mg.ContainsCcTarget(c)) continue;
                if ((c.IsMezzed || c.IsRooted) && !ShouldRecastCcOn(c))
                    continue;
                candidates.Add(c);
            }

            candidates.Sort((a, b) =>
            {
                int da = Body.GetDistanceTo(a);
                int db = Body.GetDistanceTo(b);
                int cmp = da.CompareTo(db);
                if (cmp != 0) return cmp;
                return a.HealthPercent.CompareTo(b.HealthPercent);
            });

            int room = maxAddsToCc - mg.CcTargetCount;
            for (int i = 0; i < candidates.Count && room > 0; i++)
            {
                if (mg.AddCcTarget(candidates[i]))
                    room--;
            }
        }

        // Returns true when a mob already mezzed or rooted should be put back
        // in the CC queue for a pre-emptive recast. We treat any of the soft
        // CC effects as a candidate and recast if the strongest remaining time
        // is under CC_RECAST_LEAD_MS.
        private static bool ShouldRecastCcOn(GameLiving target)
        {
            if (target == null)
                return false;

            // Pick the longest still-active soft-CC effect we know about. If
            // it's about to expire, the bot can stage a recast.
            long longestRemaining = 0;
            void Probe(eEffect effect)
            {
                var fx = EffectListService.GetEffectOnTarget(target, effect);
                if (fx == null) return;
                long remaining = fx.ExpireTick - GameLoop.GameLoopTime;
                if (remaining > longestRemaining)
                    longestRemaining = remaining;
            }

            Probe(eEffect.Mez);
            Probe(eEffect.MovementSpeedDebuff);
            Probe(eEffect.Snare);

            return longestRemaining > 0 && longestRemaining <= CC_RECAST_LEAD_MS;
        }

        /// <summary>
        /// Predicate variant of the legacy ValidateCCList: returns true when
        /// the supplied CC target is still worth keeping in the group's queue.
        /// A CC target is kept while it's a live NPC backed by a
        /// StandardMobBrain that still has aggro — anything else gets dropped.
        /// Used with MimicGroup.ValidateCcTargets to mutate the list under the
        /// dedicated lock rather than rebuild-and-replace.
        /// </summary>
        private static bool IsValidCcTarget(GameLiving cc)
        {
            return cc is GameNPC npc
                && npc.IsAlive
                && npc.Brain is StandardMobBrain smb
                && smb.HasAggro;
        }

        #endregion MainCC

        #region MainTank

        // Vulnerability tier used to decide which group member the tank should
        // peel for. Lower = more important to protect.
        private static int VulnerabilityTier(GameLiving gl)
        {
            MimicCombatProfile profile = MimicCombatProfileRegistry.GetForLiving(gl);
            if (profile == null)
            {
                if (gl is IGamePlayer player && player.CharacterClass != null)
                {
                    int classId = player.CharacterClass.ID;

                    if (MimicConfig.IsHealerClass(classId))
                        return 0;

                    if (MimicConfig.IsCcClass(classId)
                        || MimicConfig.IsCasterDpsClass(classId)
                        || player.CharacterClass.ClassType == eClassType.ListCaster)
                        return 1;
                }

                return 3;
            }

            if (profile.HasRole(eMimicCombatRole.Healer))
                return 0;

            if (profile.HasRole(eMimicCombatRole.CrowdControl)
                || profile.HasRole(eMimicCombatRole.CasterDps)
                || profile.HasRole(eMimicCombatRole.PetCaster)
                || profile.HasRole(eMimicCombatRole.Support))
                return 1;

            return profile.HasRole(eMimicCombatRole.Tank) ? 3 : 2;

        }

        private bool TryGetProtectedVictim(GameLiving hostile, out GameLiving victim, out int tier)
        {
            victim = null;
            tier = int.MaxValue;

            if (hostile?.TargetObject is not GameLiving target)
                return false;

            if (!target.IsAlive || target.ObjectState != GameObject.eObjectState.Active)
                return false;

            Group group = Body.Group;
            if (group == null || !group.IsInTheGroup(target))
                return false;

            tier = VulnerabilityTier(target);
            if (tier > 1)
                return false;

            victim = target;
            return true;
        }

        /// <summary>
        /// Finds an urgent hostile currently targeting a healer, CC/support,
        /// or caster. Used before normal assist logic so the group can peel
        /// dangerous adds instead of tunnel-visioning the main target.
        /// </summary>
        public GameLiving SelectProtectedMemberThreat()
        {
            if (Body?.Group == null || AggroList.Count == 0 || IsHealer)
                return null;

            GameLiving best = null;
            int bestScore = int.MaxValue;
            int bestDistance = int.MaxValue;
            MimicGroup mg = Body.Group.MimicGroup;
            eMimicCombatMode mode = PvPMode ? eMimicCombatMode.PvP : eMimicCombatMode.PvE;

            foreach (var pair in AggroList)
            {
                GameLiving hostile = pair.Key;

                if (hostile == null
                    || !hostile.IsAlive
                    || hostile.ObjectState != GameObject.eObjectState.Active)
                    continue;

                if (ShouldBeRemovedFromAggroList(hostile) || ShouldBeIgnoredFromAggroList(hostile))
                    continue;

                if (!CanAggroTarget(hostile))
                    continue;

                if (ShouldAvoidCrowdControlledTarget(hostile, mode, false))
                    continue;

                if (!TryGetProtectedVictim(hostile, out GameLiving victim, out int victimTier))
                    continue;

                if (mg?.MainTank != null
                    && mg.MainTank != Body
                    && mg.MainTank.IsAlive
                    && TargetHasAggroOnTank(hostile, mg.MainTank))
                    continue;

                int distance = Body.GetDistanceTo(hostile);
                int score = victimTier * 1000 + victim.HealthPercent * 4 + distance / 80;

                if (hostile.TargetObject == Body)
                    score -= 250;
                else if (victim.HealthPercent <= 60)
                    score -= 150;

                if (hostile == Body.TargetObject)
                    score -= 25;

                if (score < bestScore || (score == bestScore && distance < bestDistance))
                {
                    best = hostile;
                    bestScore = score;
                    bestDistance = distance;
                }
            }

            return best;
        }

        public bool TryPeelProtectedMemberThreat()
        {
            if (PreventCombat)
                return false;

            GameLiving threat = SelectProtectedMemberThreat();
            if (threat == null)
                return false;

            Body.TargetObject = threat;
            AddToAggroList(threat, 1);
            return true;
        }

        public bool CheckMainTankTarget()
        {
            if (!IsMainTank || AggroList.Count == 0)
                return false;

            // Peel pass: among hostiles not mezzed/rooted, prefer one whose
            // *target* is the most vulnerable group member. Tiebreak by proximity
            // so the tank closes the gap fastest on the most imminent threat.
            // This replaces the previous random pick which sometimes left a
            // healer being beaten on while the tank chased a peripheral add.
            GameLiving peelBest = null;
            int peelTier = int.MaxValue;
            int peelDist = int.MaxValue;

            // Fallback: a mob actively on the tank itself. Keeps the tank
            // engaged even when nothing is peelable (everything is on us).
            GameLiving fallbackOnTank = null;
            int fallbackOnTankDist = int.MaxValue;

            foreach (var kv in AggroList)
            {
                GameLiving mob = kv.Key;

                if (mob == null || !mob.IsAlive || mob.ObjectState != GameObject.eObjectState.Active)
                    continue;
                if (mob.IsMezzed || mob.IsRooted)
                    continue;

                int dist = Body.GetDistanceTo(mob);

                if (mob.TargetObject is GameLiving mobTarget && mobTarget != Body)
                {
                    int tier = VulnerabilityTier(mobTarget);

                    if (tier < peelTier || (tier == peelTier && dist < peelDist))
                    {
                        peelBest = mob;
                        peelTier = tier;
                        peelDist = dist;
                    }
                }
                else if (mob.TargetObject == Body && dist < fallbackOnTankDist)
                {
                    fallbackOnTank = mob;
                    fallbackOnTankDist = dist;
                }
            }

            GameLiving target = peelBest ?? fallbackOnTank;

            if (target != null)
            {
                Body.TargetObject = target;
                return true;
            }

            return false;
        }

        // Last living the tank applied Guard to. Tracked so we can call
        // GuardAbilityHandler.RemoveOurEffect on camp exit without scanning
        // every group member for our effect.
        private GameLiving _campGuardTarget;
        // Last living the paladin/equivalent applied Protect to.
        private GameLiving _campProtectTarget;

        /// <summary>
        /// Tank-side per-tick maintenance at camp: keeps Guard up on the most
        /// fragile member, and applies Protect if the bot has that ability
        /// too. Skipped for non-tanks. Idempotent: the underlying handlers
        /// no-op when the effect is already on the chosen target.
        /// </summary>
        public void MaintainTankCampSupport()
        {
            if (!IsMainTank || Body == null || !Body.IsAlive)
                return;

            MimicGroup mg = Body.Group?.MimicGroup;
            if (mg == null)
                return;

            GameLiving guardChoice = mg.PickGuardTarget(Body);
            if (guardChoice == null)
                return;

            // Only re-apply when the target changes — Guard is a stable effect.
            if (HasAbility(Abilities.Guard) && _campGuardTarget != guardChoice
                && Body.IsWithinRadius(guardChoice, 1000))
            {
                if (SetGuard(guardChoice, out _))
                    _campGuardTarget = guardChoice;
            }

            // Protect targets the same squishy by default. Different ability,
            // different stacking rules — both can sit on the same member.
            if (HasAbility(Abilities.Protect) && _campProtectTarget != guardChoice
                && Body.IsWithinRadius(guardChoice, 1000))
            {
                if (SetProtect(guardChoice, out _))
                    _campProtectTarget = guardChoice;
            }
        }

        /// <summary>
        /// Drops Guard/Protect targets when the tank leaves the camp state.
        /// Doesn't actively cancel the ECS effects (they're handled by the
        /// existing ability system); we just forget the bookkeeping so the
        /// next camp entry will reassign cleanly.
        /// </summary>
        public void ClearGuardAtCamp()
        {
            _campGuardTarget = null;
            _campProtectTarget = null;
        }

        private bool HasAbility(string abilityKey)
        {
            return Body?.GetAbility(abilityKey) != null;
        }

        #endregion MainTank

        public bool CheckStats(short threshold)
        {
            if (Body.HealthPercent < threshold || (Body.MaxMana > 0 && Body.ManaPercent < threshold) || Body.EndurancePercent < threshold)
            {
                if (GameLoop.GameLoopTime > _emoteDelay)
                {
                    _emoteDelay = GameLoop.GameLoopTime + 10000;
                    Body.Emote(eEmote.Drink);
                }

                return true;
            }

            return false;
        }

        #endregion MimicGroup AI

        #region Aggro

        protected int _aggroRange;

        /// <summary>
        /// Max Aggro range in that this npc searches for enemies
        /// </summary>
        public virtual int AggroRange
        {
            get => Math.Min(_aggroRange, MAX_AGGRO_DISTANCE);
            set => _aggroRange = value;
        }

        /// <summary>
        /// Aggressive Level in % 0..100, 0 means not Aggressive
        /// </summary>
        public virtual int AggroLevel { get; set; }

        private ConcurrentDictionary<GameLiving, AggroAmount> _tempAggroList;
        protected ConcurrentDictionary<GameLiving, AggroAmount> AggroList { get; private set; } = new();
        protected List<OrderedAggroListElement> OrderedAggroList { get; private set; } = new();
        protected readonly Lock _orderedAggroListLock = new();
        public GameLiving LastHighestThreatInAttackRange { get; private set; }

        public class AggroAmount
        {
            public AggroAmount(long baseAggro = 0)
            {
                Base = baseAggro;
                LastTouchedTick = GameLoop.GameLoopTime;
            }

            public long Base { get; set; }
            public long Effective { get; set; }
            /// <summary>
            /// GameLoopTime ms at which this entry was last refreshed (added
            /// or boosted). Used by the soft-decay sweep on Aggro.Exit so a
            /// brief out-of-combat gap doesn't wipe long-running threat data.
            /// </summary>
            public long LastTouchedTick { get; set; }
        }

        /// <summary>
        /// Drops aggro entries last touched longer than <paramref name="maxAgeMs"/>
        /// milliseconds ago. Used instead of a wholesale ClearAggroList when
        /// leaving the AGGRO FSM state — a 5–10 second out-of-combat blip
        /// should not erase threat info that's still relevant for the next
        /// engagement. Returns the count dropped.
        /// </summary>
        public int DecayAggroList(long maxAgeMs)
        {
            long now = GameLoop.GameLoopTime;
            int dropped = 0;
            foreach (var pair in AggroList)
            {
                if (now - pair.Value.LastTouchedTick > maxAgeMs)
                {
                    if (AggroList.TryRemove(pair.Key, out _))
                        dropped++;
                }
            }
            return dropped;
        }

        /// <summary>
        /// Checks whether living has someone on its aggrolist
        /// </summary>
        public virtual bool HasAggro => !AggroList.IsEmpty;

        /// <summary>
        /// Add aggro table of this brain to that of another living.
        /// </summary>
        public void AddAggroListTo(StandardMobBrain brain)
        {
            if (!brain.Body.IsAlive)
                return;

            foreach (var pair in AggroList)
                brain.AddToAggroList(pair.Key, pair.Value.Base);
        }

        public virtual void AddToAggroList(GameLiving living, long aggroAmount = 0)
        {
            if (!Body.IsAlive || living == null)
                return;

            ForceAddToAggroList(living, aggroAmount);
        }

        public void ForceAddToAggroList(GameLiving living, long aggroAmount = 0)
        {
            if (aggroAmount > 0)
            {
                foreach (ProtectECSGameEffect protect in living.effectListComponent.GetAbilityEffects(eEffect.Protect))
                {
                    if (protect.Target != living)
                        continue;

                    GameLiving protectSource = protect.Source;

                    if (protectSource.IsIncapacitated || protectSource.IsSitting)
                        continue;

                    if (!living.IsWithinRadius(protectSource, ProtectAbilityHandler.PROTECT_DISTANCE))
                        continue;

                    // P I: prevents 10% of aggro amount
                    // P II: prevents 20% of aggro amount
                    // P III: prevents 30% of aggro amount
                    // guessed percentages, should never be higher than or equal to 50%
                    int abilityLevel = protectSource.GetAbilityLevel(Abilities.Protect);
                    long protectAmount = (long)(abilityLevel * 0.1 * aggroAmount);

                    if (protectAmount > 0)
                    {
                        aggroAmount -= protectAmount;

                        if (protectSource is GamePlayer playerProtectSource)
                        {
                            playerProtectSource.Out.SendMessage(LanguageMgr.GetTranslation(playerProtectSource.Client.Account.Language, "AI.Brain.StandardMobBrain.YouProtDist", living.GetName(0, false),
                                Body.GetName(0, false, playerProtectSource.Client.Account.Language, Body)), eChatType.CT_System, eChatLoc.CL_SystemWindow);
                        }

                        AggroList.AddOrUpdate(protectSource, Add, Update, protectAmount);
                    }
                }
            }

            AggroList.AddOrUpdate(living, Add, Update, aggroAmount);

            // Change state and reschedule the next think tick to improve responsiveness.
            if (FSM.GetCurrentState() != FSM.GetState(eFSMStateType.AGGRO) && HasAggro && !IsHealer)
            {
                FSM.SetCurrentState(eFSMStateType.AGGRO);
                NextThinkTick = GameLoop.GameLoopTime;
            }

            static AggroAmount Add(GameLiving key, long arg)
            {
                // Always add at least 1 if the key is not present to ensure the NPC goes to the puller and not a group member.
                // It's still technically possible for two group members to pull at the exact same time, but this should be fine.
                return new(Math.Max(1, arg));
            }

            static AggroAmount Update(GameLiving key, AggroAmount oldValue, long arg)
            {
                oldValue.Base = Math.Max(0, oldValue.Base + arg);
                oldValue.LastTouchedTick = GameLoop.GameLoopTime;
                return oldValue;
            }
        }

        public virtual void RemoveFromAggroList(GameLiving living)
        {
            AggroList.TryRemove(living, out _);
        }

        public long GetMaxAggro()
        {
            if (AggroList.IsEmpty)
                return 0;

            long max = 0;
            foreach (var kv in AggroList)
            {
                long eff = kv.Value.Effective;
                if (eff > max)
                    max = eff;
            }
            return max;
        }

        // Reusable comparer for OrderedAggroList sort. Stored as a static
        // delegate so the sort doesn't capture/alloc a closure each call.
        private static readonly Comparison<OrderedAggroListElement> _aggroDescByAmount =
            static (a, b) => b.AggroAmount.CompareTo(a.AggroAmount);

        public List<OrderedAggroListElement> GetOrderedAggroList()
        {
            // Potentially slow, so we cache the result.
            lock (_orderedAggroListLock)
            {
                if (OrderedAggroList.Count == 0)
                {
                    // Build the ordered list manually instead of going through
                    // .OrderByDescending().Select().ToList() — that chain allocated
                    // three intermediates (enumerator, projection, output list).
                    // We sort the existing storage in-place using a static
                    // comparison delegate (no closure capture).
                    int aggroCount = AggroList.Count;
                    if (OrderedAggroList.Capacity < aggroCount)
                        OrderedAggroList.Capacity = aggroCount;
                    foreach (var pair in AggroList)
                        OrderedAggroList.Add(new OrderedAggroListElement(pair.Key, pair.Value.Effective));
                    OrderedAggroList.Sort(_aggroDescByAmount);
                }

                // Defensive copy — callers iterate without the lock. Pre-size
                // the result to skip the List growth path.
                List<OrderedAggroListElement> copy = new(OrderedAggroList.Count);
                copy.AddRange(OrderedAggroList);
                return copy;
            }
        }

        public long GetBaseAggroAmount(GameLiving living)
        {
            return AggroList.TryGetValue(living, out AggroAmount aggroAmount) ? aggroAmount.Base : 0;
        }

        public bool SetTemporaryAggroList()
        {
            if (_tempAggroList != null)
                return false;

            _tempAggroList = AggroList;
            AggroList = new();
            return true;
        }

        public bool UnsetTemporaryAggroList()
        {
            // Keep the current aggro list if the previous one is empty.
            // This can happen when amnesia is used during confusion.
            if (_tempAggroList == null || _tempAggroList.IsEmpty)
                return false;

            AggroList = _tempAggroList;
            _tempAggroList = null;

            if (HasAggro)
            {
                if (FSM.GetCurrentState() != FSM.GetState(eFSMStateType.AGGRO))
                    FSM.SetCurrentState(eFSMStateType.AGGRO);

                NextThinkTick = GameLoop.GameLoopTime;
            }

            return true;
        }

        /// <summary>
        /// Remove all livings from the aggrolist.
        /// </summary>
        public virtual void ClearAggroList()
        {
            AggroList.Clear();
            _tempAggroList = null;

            lock (_orderedAggroListLock)
            {
                OrderedAggroList.Clear();
            }

            LastHighestThreatInAttackRange = null;
        }

        /// <summary>
        /// Selects and attacks the next target or does nothing.
        /// </summary>
        public virtual void AttackMostWanted()
        {
            if (!IsActive)
                return;

            if (!CheckMainTankTarget())
                Body.TargetObject = CalculateNextAttackTarget();

            if (Body.TargetObject == null)
                return;

            // IsAlive guard: stop wasting ticks (and mana from CheckOffensiveSpells
            // below) on a corpse. Without this, a target that died between two ticks
            // still triggers a cast attempt + a melee swing attempt before being
            // reaped from the aggro list on the next CleanUpAggroListAndGet...() pass.
            if (Body.TargetObject is GameLiving gl && (!gl.IsAlive || gl.ObjectState != GameObject.eObjectState.Active))
            {
                Body.TargetObject = null;
                Body.StopAttack();
                return;
            }

            if (Body.ControlledBrain != null)
                Body.ControlledBrain.Attack(Body.TargetObject);

            if (!IsFleeing && CheckSpells(eCheckSpellType.Offensive))
            {
                Body.StopAttack();
                return;
            }

            if (Body.IsCasting)
                return;

            CheckOffensiveAbilities();

            if (MimicBody.CharacterClass.ClassType == eClassType.ListCaster ||
                MimicBody.CharacterClass.ID == (int)eCharacterClass.Valewalker)
            {
                if (Body.IsBeingInterrupted)
                {
                    // Try to QuickCast a CC spell
                    ECSGameAbilityEffect quickCast = EffectListService.GetAbilityEffectOnTarget(Body, eEffect.QuickCast);

                    if (quickCast != null)
                        CheckSpells(eCheckSpellType.CrowdControl);

                    // Solo casters flee outright; grouped casters kite SHORT
                    // so the tank's peel actually catches up. Previously the
                    // grouped caster did nothing and ate hits until the tank
                    // arrived — by then the caster was usually dead because
                    // CheckMainTankTarget needs the mob's AggroList[tank] to
                    // beat the mob's AggroList[caster] before retargeting.
                    if (Body.Group == null)
                    {
                        if (TryFlee())
                            return;

                        if (TryResumeAfterFlee())
                            return;
                    }
                    else
                    {
                        // Grouped: take a short kite step (300u) to give the
                        // tank one extra second to peel, then resume casting
                        // next tick (interrupt window is short).
                        TryShortKiteStep(300);
                    }

                    return;
                }

                // Not being interrupted - cast normally
                if (!IsFleeing && CheckSpells(eCheckSpellType.Offensive))
                    return;

                // Solo casters resume after flee if needed
                if (Body.Group == null && TryResumeAfterFlee())
                    return;

                return;
            }

            if (Body.TargetObject != LastTargetObject)
                ResetFlanking();

            bool isMinstrel = MimicBody.CharacterClass.ID == (int)eCharacterClass.Minstrel;
            bool isSoloBard = MimicBody.CharacterClass.ID == (int)eCharacterClass.Bard && Body.Group == null;

            if ((isMinstrel || isSoloBard) && Body.ActiveWeaponSlot != eActiveWeaponSlot.Standard)
                Body.SwitchWeapon(eActiveWeaponSlot.Standard);

            bool inMeleeRange = Body.ActiveWeapon?.Item_Type != (int)eInventorySlot.DistanceWeapon
                && Body.IsWithinRadius(Body.TargetObject, Body.attackComponent.AttackRange);

            if (inMeleeRange && Flank())
                return;

            Body.StartAttack(Body.TargetObject);
            LastTargetObject = Body.TargetObject;
        }

        private bool Flank()
        {
            if (!MimicBody.CanUsePositionalStyles || IsMainTank || Body.ActiveWeapon == null)
                return false;

            if (Body.TargetObject is not GameLiving livingTarget)
                return false;

            if (livingTarget.IsMoving || livingTarget.TargetObject == Body)
                ResetFlanking();

            if (BeginFlanking(livingTarget))
                return true;

            if (Body.IsDestinationValid)
            {
                if (TargetFlankPosition == null)
                    Body.Follow(Body.TargetObject, 75, 5000);

                return TargetFlankPosition != null;
            }

            if (TargetFlankPosition != null && Body.GetDistance(TargetFlankPosition) < 5)
            {
                IsFlanking = true;
                TargetFlankPosition = null;
            }
            else if (TargetFlankPosition != null && !Body.IsDestinationValid && !Body.IsMoving)
            {
                ResetFlanking();
            }

            return false;
        }

        private bool BeginFlanking(GameLiving livingTarget)
        {
            if (TargetFlankPosition != null || IsFlanking || livingTarget.IsMoving || livingTarget.TargetObject == Body)
                return false;

            LastTargetObject = Body.TargetObject;
            TargetFlankPosition = GetStylePositionPoint(livingTarget, GetPositional());
            Body.StopFollowing();
            //Body.StopAttack();
            Body.PathTo(new Point3D(TargetFlankPosition.X, TargetFlankPosition.Y, livingTarget.Z), Body.MaxSpeed);

            return true;
        }

        public void ResetFlanking()
        {
            IsFlanking = false;
            TargetFlankPosition = null;
        }

        // True when this healer should skip offensive duty because the group
        // actually needs heals right now. Used to relax the IsHealer DPS gate
        // for hybrids (Friar / Heretic / Shaman / Bard) so they stop standing
        // idle in melee/cast range during 100%-HP fights.
        // - "Need" = any group member below the heal threshold, OR our own
        //   mana is too low to safely commit a long offensive cast.
        // - When the group is full HP and we have mana, we let the offensive
        //   cycle drive — heals will pre-empt next tick if anything drops.
        private bool NeedsToHealNow()
        {
            // Always defer to heal duty if we're low on mana — committing a
            // long offensive cast here would just OOM us out of an incoming heal.
            if (MimicBody?.ManaPercent < 30)
                return true;

            int threshold = MimicConfig.MIMIC_HEAL_THRESHOLD > 0
                ? MimicConfig.MIMIC_HEAL_THRESHOLD
                : 80;

            if (Body.HealthPercent < threshold)
                return true;

            Group g = Body.Group;
            if (g == null)
                return false;

            foreach (GameLiving member in g.GetMembersInTheGroup())
            {
                if (member == null || member == Body || !member.IsAlive)
                    continue;
                if (member.HealthPercent < threshold)
                    return true;
            }
            return false;
        }

        private bool TryFlee()
        {
            if (TargetFleePosition != null || IsFleeing || !Body.IsBeingInterrupted)
                return false;

            int fleeDistance = 2000 - Body.GetDistance(Body.TargetObject);
            Flee(fleeDistance);

            return true;
        }

        // Grouped-caster kite: take a small step (default 300u) away from the
        // interrupting target so the tank's peel actually catches up before
        // the caster eats a full melee combo. Returns false if we can't (no
        // target / already fleeing / movement blocked).
        private bool TryShortKiteStep(int distance)
        {
            if (Body.TargetObject is not GameLiving threat || !threat.IsAlive)
                return false;
            if (IsFleeing || TargetFleePosition != null)
                return false;
            if (Body.IsCasting)
                return false; // don't break our own cast

            // Vector away from the threat.
            double dx = Body.X - threat.X;
            double dy = Body.Y - threat.Y;
            double len = Math.Sqrt(dx * dx + dy * dy);
            if (len < 1)
                return false;
            int dest_x = Body.X + (int)(distance * dx / len);
            int dest_y = Body.Y + (int)(distance * dy / len);
            Body.WalkTo(new Point3D(dest_x, dest_y, Body.Z), Body.MaxSpeed);
            return true;
        }

        // Healer survival: when a healer drops below the threshold and has a mob in
        // melee range, run away from it so the tank/peeler can taunt the add off.
        // Sets Body.TargetObject to the threat temporarily so the existing flee
        // routines compute the escape direction correctly.
        public bool HealerEmergencyFlee()
        {
            if (!IsHealer || IsFleeing || TargetFleePosition != null)
                return false;

            if (Body.HealthPercent >= MimicConfig.MIMIC_HEALER_FLEE_HEALTH_PCT)
                return false;

            GameLiving threat = null;
            int closestSqr = int.MaxValue;
            int meleeReach = Math.Max(150, Body.MeleeAttackRange + 50);

            foreach (var pair in AggroList)
            {
                GameLiving candidate = pair.Key;

                if (candidate == null || !candidate.IsAlive)
                    continue;

                if (!Body.IsWithinRadius(candidate, meleeReach))
                    continue;

                int dx = candidate.X - Body.X;
                int dy = candidate.Y - Body.Y;
                int sqr = dx * dx + dy * dy;

                if (sqr < closestSqr)
                {
                    closestSqr = sqr;
                    threat = candidate;
                }
            }

            if (threat == null)
                return false;

            GameObject savedTarget = Body.TargetObject;
            Body.TargetObject = threat;

            int fleeDistance = 1500 - Body.GetDistance(threat);

            if (fleeDistance > 0)
                Flee(fleeDistance);

            // Restore target so heal logic continues to operate on the right entity.
            Body.TargetObject = savedTarget;

            return IsFleeing;
        }

        private void Flee(int distance)
        {
            TargetFleePosition = GetFleePoint(distance);

            if (TargetFleePosition != null)
            {
                IsFleeing = true;
                MimicBody.Sprint(true);

                Body.PathTo(TargetFleePosition, Body.MaxSpeed);
            }
            else
            {
                IsFleeing = false;
            }
        }

        private bool TryResumeAfterFlee()
        {
            if (!Body.IsDestinationValid)
                return false;

            if (TargetFleePosition == null)
                return true;

            if (Body.GetDistance(TargetFleePosition) >= 5)
                return true;

            IsFleeing = false;
            TargetFleePosition = null;

            if (Body.IsWithinRadius(Body.TargetObject, 400))
            {
                Flee(1800);
                return true;
            }

            if (Body.TargetObject != Body)
                Body.TurnTo(Body.TargetObject);

            return false;
        }

        private Point3D GetFleePoint(int fleeDistance)
        {
            ushort heading;
            if (Body.IsObjectInFront(Body.TargetObject, 120))
                heading = (ushort)(Body.Heading - 2048);
            else
                heading = Body.Heading;

            if (heading < 0)
                heading += 4096;

            if (heading > 4096)
                heading -= 4096;

            Point2D point = Body.GetPointFromHeading(heading, fleeDistance);

            if (Body.CurrentRegion.GetZone(point.X, point.Y) == null)
            {
                log.Warn(Body.Name + "Tried to flee to null zone.");

                Point2D validPoint = null;

                for (int i = 0; i < 8; i++)
                {
                    heading += 512;

                    if (heading > 4096)
                        heading -= 4096;

                    validPoint = Body.GetPointFromHeading(heading, fleeDistance);

                    if (Body.CurrentRegion.GetZone(validPoint.X, validPoint.Y) != null)
                    {
                        point = validPoint;
                        break;
                    }
                }

                if (point == null)
                {
                    log.Warn(Body.Name + "Could not get valid flee point for " + Body.Name);
                    return null;
                }
            }

            if (PathfindingProvider.Instance.HasNavmesh(Body.CurrentZone))
            {
                 Vector3? target = PathfindingProvider.Instance.GetClosestPoint(Body.CurrentZone, new Vector3(point.X, point.Y, Body.Z), PathfindingProvider.Instance.DefaultFilters);

                if (target.HasValue)
                    return new Point3D(target.Value.X, target.Value.Y, target.Value.Z);
            }

            return new Point3D(point.X, point.Y, Body.Z);
        }

        private eOpeningPosition GetPositional()
        {
            eOpeningPosition positional = 0;

            if (MimicBody.CanUseSideStyles && MimicBody.CanUseBackStyles)
            {
                if (Util.Random(1) == 0)
                    positional = eOpeningPosition.Back;
                else
                    positional = eOpeningPosition.Side;
            }
            else if (MimicBody.CanUseSideStyles)
                positional = eOpeningPosition.Side;
            else if (MimicBody.CanUseBackStyles)
                positional = eOpeningPosition.Back;

            return positional;
        }

        private Point2D GetStylePositionPoint(GameLiving living, eOpeningPosition positional)
        {
            ushort heading = positional switch
            {
                eOpeningPosition.Back => (ushort)((living.Heading + 2048) & 0xFFF),
                eOpeningPosition.Side => (ushort)((living.Heading + (Util.Random(1) == 0 ? 1024 : 3072)) & 0xFFF),
                eOpeningPosition.Front => living.Heading,
                _ => living.Heading
            };

            return living.GetPointFromHeading(heading, 75);
        }

        private GameObject CheckAssist()
        {
            // Null-guarded: MainAssist isn't set until the group composer runs,
            // and CurrentTarget can be cleared mid-fight. Earlier code NPE'd on
            // MainAssist.InCombat the first tick after group creation.
            MimicGroup mg = Body.Group?.MimicGroup;
            if (mg == null || mg.MainAssist == null || !mg.MainAssist.InCombat)
                return null;

            GameObject assistTarget = mg.CurrentTarget;
            if (assistTarget is GameLiving living && CanAggroTarget(living))
                return assistTarget;

            return null;

            //if (Body.Group != null)
            //{
            //    foreach (GameLiving groupMember in Body.Group.GetMembersInTheGroup())
            //    {
            //        if (groupMember is GameLiving living)
            //            foreach (var attacker in living.attackComponent.Attackers)
            //                AddToAggroList(attacker.Key, 1);
            //    }
            //}
        }

        public virtual void Disengage()
        {
            ClearAggroList();
            Body.StopAttack();
            Body.StopCurrentSpellcast();
            Body.TargetObject = null;
        }

        private readonly AggroLosCheckListener _aggroLosCheckListener;
        public int PendingAggroLosCheckCount => _aggroLosCheckListener.PendingLosCheckCount;
        protected virtual bool CanAddToAggroListFromMultipleLosChecks => false;

        private class AggroLosCheckListener : ILosCheckListener
        {
            private MimicBrain _owner;
            private int _pendingLosCheckCount;
            public int PendingLosCheckCount => Volatile.Read(ref _pendingLosCheckCount);
            public bool HasPendingLosChecks => PendingLosCheckCount > 0;

            public AggroLosCheckListener(MimicBrain owner)
            {
                _owner = owner;
            }

            public void HandleLosCheckResponse(GamePlayer player, LosCheckResponse response, ushort targetId)
            {
                try
                {
                    if (response is LosCheckResponse.True)
                    {
                        if (!_owner.HasAggro || _owner.CanAddToAggroListFromMultipleLosChecks)
                        {
                            GameObject gameObject = _owner.Body.CurrentRegion.GetObject(targetId);

                            if (gameObject is GameLiving gameLiving)
                                _owner.AddToAggroList(gameLiving);
                        }
                    }
                }
                finally
                {
                    Interlocked.Decrement(ref _pendingLosCheckCount);
                }
            }

            public void OnLosCheckStarted()
            {
                Interlocked.Increment(ref _pendingLosCheckCount);
            }
        }

        protected virtual bool ShouldBeRemovedFromAggroList(GameLiving living)
        {
            // Keep Necromancer shades so that we can attack them if their pets die.
            return !living.IsAlive ||
                   living.CurrentRegion != Body.CurrentRegion ||
                   (!GameServer.ServerRules.IsAllowedToAttack(Body, living, true) && !living.effectListComponent.ContainsEffectForEffectType(eEffect.Shade));
        }

        protected virtual bool ShouldBeIgnoredFromAggroList(GameLiving living)
        {
            // We're keeping shades in the aggro list so that mobs attack them after their pet dies, so they need to be filtered out here.
            // We also keep entities outside MAX_AGGRO_LIST_DISTANCE in case they come back.
            return living.effectListComponent.ContainsEffectForEffectType(eEffect.Shade) || !Body.IsWithinRadius(living, MAX_AGGRO_LIST_DISTANCE);
        }

        protected virtual GameLiving CleanUpAggroListAndGetHighestModifiedThreat()
        {
            // Clear cached ordered aggro list under the same lock that
            // GetOrderedAggroList takes — without this, a concurrent reader
            // can be iterating while the list is mid-Clear, throwing
            // InvalidOperationException ("Collection was modified").
            // It's built on demand, when `GetOrderedAggroList` is called.
            lock (_orderedAggroListLock)
            {
                OrderedAggroList.Clear();
            }

            int attackRange = Body.attackComponent.AttackRange;
            GameLiving highestThreat = null;
            KeyValuePair<GameLiving, AggroAmount> currentTarget = default;
            long highestEffectiveAggro = -1; // Assumes that negative aggro amounts aren't allowed in the list.
            long highestEffectiveAggroInAttackRange = -1; // Assumes that negative aggro amounts aren't allowed in the list.

            foreach (var pair in AggroList)
            {
                GameLiving living = pair.Key;

                if (Body.TargetObject == living)
                    currentTarget = pair;

                if (ShouldBeRemovedFromAggroList(living))
                {
                    AggroList.TryRemove(living, out _);
                    continue;
                }

                if (ShouldBeIgnoredFromAggroList(living))
                    continue;

                // Livings further than `EFFECTIVE_AGGRO_AMOUNT_CALCULATION_DISTANCE_THRESHOLD` units away have a reduced effective aggro amount.
                // Using `Math.Ceiling` helps differentiate between 0 and 1 base aggro amount.
                AggroAmount aggroAmount = pair.Value;
                double distance = Body.GetDistanceTo(living);
                double distanceOverThreshold = distance - EFFECTIVE_AGGRO_DISTANCE_THRESHOLD;

                if (distanceOverThreshold <= 0)
                    aggroAmount.Effective = aggroAmount.Base;
                else
                    aggroAmount.Effective = (long)Math.Ceiling(aggroAmount.Base * Math.Exp(EFFECTIVE_AGGRO_EXPONENT * distanceOverThreshold));

                if (aggroAmount.Effective > highestEffectiveAggroInAttackRange)
                {
                    if (distance <= attackRange)
                    {
                        highestEffectiveAggroInAttackRange = aggroAmount.Effective;
                        LastHighestThreatInAttackRange = living;
                    }

                    if (aggroAmount.Effective > highestEffectiveAggro)
                    {
                        highestEffectiveAggro = aggroAmount.Effective;
                        highestThreat = living;
                    }
                }
            }

            if (highestThreat != null)
            {
                // Don't change target if our new found highest threat has the same effective aggro.
                // This helps with BAF code to make mobs actually go to their intended target.
                if (currentTarget.Key != null && currentTarget.Key != highestThreat && currentTarget.Value.Effective >= highestEffectiveAggro)
                    highestThreat = currentTarget.Key;
            }
            else
            {
                // The list seems to be full of shades. It could mean we added a shade to the aggro list instead of its pet.
                // Ideally, this should never happen, but it currently can be caused by the way `AddToAggroList` propagates aggro to group members.
                // When that happens, don't bother checking aggro amount and simply return the first pet in the list.
                return AggroList.FirstOrDefault().Key?.ControlledBrain?.Body;
            }

            return highestThreat;
        }

        /// <summary>
        /// Returns the best target to attack from the current aggro list.
        /// Group-aware:
        ///  - DPS / casters not holding a special role focus the MainAssist's current
        ///    target so the group concentrates damage on one mob at a time.
        ///  - DPS waits for the MainTank to actually engage (i.e. the tank's target
        ///    has aggro on us) before piling on, preventing the classic "casters pull
        ///    aggro before tank" wipe.
        /// </summary>
        protected virtual GameLiving CalculateNextAttackTarget()
        {
            MimicGroup mg = Body.Group?.MimicGroup;

            GameLiving protectedThreat = SelectProtectedMemberThreat();
            if (protectedThreat != null)
                return protectedThreat;

            if (PvPMode)
            {
                GameLiving pvpTarget = SelectProfileTargetFromAggroList(eMimicCombatMode.PvP);
                if (pvpTarget != null)
                    return pvpTarget;
            }

            if (mg != null
                && mg.MainAssist != null
                && mg.MainAssist != Body
                && !IsMainTank
                && !IsMainCC
                && !IsHealer)
            {
                if (mg.MainAssist.TargetObject is GameLiving assistTarget
                    && assistTarget.IsAlive
                    && assistTarget.ObjectState == GameObject.eObjectState.Active
                    && CanAggroTarget(assistTarget))
                {
                    // Only follow the assist target when the assist is ACTUALLY
                    // engaging it. Just target-selecting a passing mob isn't an
                    // engage signal — without these checks the bot would dogpile
                    // any mob clicked during the post-combat AGGRO decay window.
                    //
                    // We accept any of:
                    //  - assist is auto-attacking that exact target
                    //  - assist is casting a harmful spell on that exact target
                    //  - the target is already attacking a group member
                    //  - we already have meaningful aggro on the target (took/dealt a hit)
                    bool assistAttackingTarget = mg.MainAssist.IsAttacking
                                                 && mg.MainAssist.TargetObject == assistTarget;
                    bool assistCastingHarmfulOnTarget = mg.MainAssist.IsCasting
                                                       && mg.MainAssist.castingComponent?.SpellHandler?.Spell?.IsHarmful == true
                                                       && mg.MainAssist.TargetObject == assistTarget;
                    bool assistTargetEngagedOnGroup = assistTarget.TargetObject is GameLiving aE
                                                      && Body.Group != null
                                                      && Body.Group.IsInTheGroup(aE);
                    bool weHaveAggroOnAssistTarget = AggroList.ContainsKey(assistTarget) && AggroList[assistTarget].Effective > 1;

                    bool assistIsEngaging = assistAttackingTarget
                                            || assistCastingHarmfulOnTarget
                                            || assistTargetEngagedOnGroup
                                            || weHaveAggroOnAssistTarget;

                    if (assistIsEngaging && !ShouldAvoidCrowdControlledTarget(assistTarget, eMimicCombatMode.PvE, true))
                    {
                        // DPS hold-fire: if a mimic tank exists and hasn't established
                        // aggro on this target yet, wait. The bot is already targeting
                        // via the aggro list (we kept it), but returning null here
                        // suppresses the next attack/cast in AttackMostWanted. This
                        // prevents casters from pulling aggro before the tank has the mob.
                        // <para>Skipped when:</para>
                        // <list type="bullet">
                        //   <item>MainTank is a player — players are unpredictable
                        //   tanks and we'd rather have responsive DPS than idle bots
                        //   waiting on a player who may never engage.</item>
                        //   <item>The target is already attacking a group member —
                        //   the mob is locked, no risk of stealing aggro from a vacuum.</item>
                        //   <item>The bot is itself being attacked by the target — it's
                        //   already taking hits, fighting back is the safer move.</item>
                        // </list>
                        bool tankIsPlayer = mg.MainTank is GamePlayer;
                        bool targetEngagedOnGroup = assistTarget.TargetObject is GameLiving aTgt
                                                    && Body.Group != null
                                                    && Body.Group.IsInTheGroup(aTgt);
                        bool selfUnderAttack = assistTarget.TargetObject == Body
                                               || (assistTarget.attackComponent != null
                                                   && assistTarget.attackComponent.AttackState
                                                   && assistTarget.TargetObject == Body);

                        if (mg.MainTank != null
                            && mg.MainTank != Body
                            && mg.MainTank.IsAlive
                            && !tankIsPlayer
                            && !targetEngagedOnGroup
                            && !selfUnderAttack
                            && !TargetHasAggroOnTank(assistTarget, mg.MainTank))
                        {
                            // Still record interest so we'll engage as soon as tank locks in.
                            if (!AggroList.ContainsKey(assistTarget))
                                AddToAggroList(assistTarget, 1);

                            return null;
                        }

                        // Keep the target in our aggro list so threat tracking stays consistent.
                        if (!AggroList.ContainsKey(assistTarget))
                            AddToAggroList(assistTarget, 1);

                        return assistTarget;
                    }
                }
            }

            GameLiving profileTarget = SelectProfileTargetFromAggroList(eMimicCombatMode.PvE);
            if (profileTarget != null)
                return profileTarget;

            GameLiving fallback = CleanUpAggroListAndGetHighestModifiedThreat();
            if (ShouldAvoidCrowdControlledTarget(fallback, eMimicCombatMode.PvE, false))
                return null;

            return fallback;
        }

        private GameLiving SelectProfileTargetFromAggroList(eMimicCombatMode mode)
        {
            MimicCombatProfile profile = MimicBody?.CombatProfile;
            if (profile == null || AggroList.Count == 0)
                return null;

            GameLiving focus = Body.Group?.MimicGroup?.MainAssist?.TargetObject as GameLiving;
            GameLiving best = null;
            int bestScore = int.MaxValue;

            foreach (var pair in AggroList)
            {
                GameLiving candidate = pair.Key;

                if (candidate == null
                    || !candidate.IsAlive
                    || candidate.ObjectState != GameObject.eObjectState.Active)
                    continue;

                if (ShouldBeRemovedFromAggroList(candidate) || ShouldBeIgnoredFromAggroList(candidate))
                    continue;

                if (!CanAggroTarget(candidate))
                    continue;

                bool isFocus = candidate == focus;
                if (ShouldAvoidCrowdControlledTarget(candidate, mode, isFocus))
                    continue;

                bool attackingSelf = candidate.TargetObject == Body;
                bool attackingProtected = IsAttackingProtectedMember(candidate);
                bool lowHealth = candidate.HealthPercent <= 35;
                MimicCombatProfile targetProfile = MimicCombatProfileRegistry.GetForLiving(candidate);

                int score = profile.ScoreTarget(
                    targetProfile,
                    mode,
                    isFocus,
                    attackingSelf,
                    attackingProtected,
                    lowHealth,
                    candidate.IsCrowdControlled,
                    Body.GetDistanceTo(candidate));

                if (score < bestScore)
                {
                    bestScore = score;
                    best = candidate;
                }
            }

            return best;
        }

        private bool IsAttackingProtectedMember(GameLiving hostile)
        {
            if (hostile?.TargetObject is not GameLiving target || target == Body)
                return false;

            if (Body.Group == null || !Body.Group.IsInTheGroup(target))
                return false;

            return VulnerabilityTier(target) <= 1;
        }

        private bool ShouldAvoidCrowdControlledTarget(GameLiving target, eMimicCombatMode mode, bool isFocusTarget)
        {
            if (target == null || !target.IsCrowdControlled)
                return false;

            if (IsMainCC)
                return false;

            MimicGroup mg = Body.Group?.MimicGroup;
            if (isFocusTarget && mg?.MainTank != null && TargetHasAggroOnTank(target, mg.MainTank))
                return false;

            if (IsMainTank && target.TargetObject == Body)
                return false;

            return true;
        }

        /// <summary>
        /// True if the tank has clearly committed to this target: it has the
        /// target focused, is attacking it, is in melee range of it, or the
        /// target's aggro list already names the tank. The old version only
        /// checked the aggro-list path, which forced DPS to idle for several
        /// ticks waiting for the tank's first hit to register.
        /// </summary>
        private static bool TargetHasAggroOnTank(GameLiving target, GameLiving tank)
        {
            if (target == null || tank == null)
                return false;

            // Tank already focused / attacking the target — engage.
            if (tank.TargetObject == target && (tank.IsAttacking || tank.IsCasting))
                return true;

            // Mob looking back at the tank — engage.
            if (target.TargetObject == tank)
                return true;

            // Tank is within melee swing range and moving in — close enough.
            if (tank.IsWithinRadius(target, 250) && tank.TargetObject == target)
                return true;

            if (target is GameNPC npc && npc.Brain is StandardMobBrain mb && mb.HasAggro)
            {
                var ordered = mb.GetOrderedAggroList();
                if (ordered != null)
                {
                    for (int i = 0; i < ordered.Count; i++)
                    {
                        if (ordered[i].Living == tank)
                            return true;
                    }
                }
            }

            return false;
        }

        public virtual bool CanAggroTarget(GameLiving target)
        {
            if (!GameServer.ServerRules.IsAllowedToAttack(Body, target, true))
                return false;

            if (target.IsStealthed && !MimicBody.CanDetect(target))
            {
                RemoveFromAggroList(target);
                return false;
            }

            // Get owner if target is pet or subpet
            GameLiving realTarget = target;

            if (realTarget is GameNPC npcTarget && npcTarget.Brain is IControlledBrain npcTargetBrain)
                realTarget = npcTargetBrain.GetLivingOwner();

            /// Only attack if target is green+
            if (Body.IsObjectGreyCon(realTarget))
                return false;

            if (!PvPMode && FSM.GetCurrentState() == FSM.GetState(eFSMStateType.ROAMING))
            {
                ConColor conLimit = (ConColor)Body.GetConLevel(realTarget);

                if (conLimit >= ConColor.PURPLE)
                    return false;

                if (Body.Group == null && conLimit >= ConColor.ORANGE)
                    return false;

                if (realTarget is GameNPC npc && npc.Brain is StandardMobBrain brain && brain.HasAggro)
                    return false;
            }

            if (realTarget is IGamePlayer && realTarget.Realm != Body.Realm)
                return true;

            // TODO: Work on keepguard fighting
            if (realTarget is GameKeepGuard)
                return false;

            if (realTarget is GameNPC && realTarget is not MimicNPC && realTarget is not GameKeepGuard && PvPMode)
                return false;

            // We put this here to prevent aggroing non-factions npcs
            return (Body.Realm != eRealm.None || realTarget is not GameNPC) && AggroLevel > 0;
        }

        public virtual void OnAttackedByEnemy(AttackData ad)
        {
            ConvertAttackToAggroAmount(ad);
        }

        // <summary>
        /// Converts an amount into an aggro amount, and splits it between the pet and its owner if necessary.
        /// </summary>
        protected void ConvertAttackToAggroAmount(AttackData ad)
        {
            if (!ad.GeneratesAggro || !Body.IsAlive || Body.ObjectState is not GameObject.eObjectState.Active || FSM.GetCurrentState() == FSM.GetState(eFSMStateType.PASSIVE))
                return;

            int damage = Math.Max(0, ad.Damage + ad.CriticalDamage);
            GameLiving attacker = ad.Attacker;

            if (attacker is GameNPC npcAttacker && npcAttacker.Brain is ControlledMobBrain controlledBrain)
            {
                damage = controlledBrain.ModifyDamageWithTaunt(damage);

                // A pet generates 100% of the aggro from its damage; the owner receives 30% additional aggro as a tag, without reducing the pet's contribution.
                // The pet should be added first to the aggro list in case the attack does no damage (see `AddToAggroList` implementation).
                AddToAggroList(npcAttacker, damage);
                PropagateAggroToGroupMembers(npcAttacker);
                AddToAggroList(controlledBrain.Owner, (int)(damage * 0.3));
                PropagateAggroToGroupMembers(controlledBrain.Owner);
                return;
            }

            AddToAggroList(attacker, damage);
            PropagateAggroToGroupMembers(attacker);
        }

        private void PropagateAggroToGroupMembers(GameLiving attacker)
        {
            // Propagate aggro to group members and pets. This only applies to attacks, not to body pulling.
            if (attacker is IGamePlayer player)
            {
                // Populate the aggro list with our own pet, group members and their pets.
                // This ensures NPCs can attack other players and pets on their way.

                AddPetAndSubPetsToAggroList(player);

                // This is done on every attack, but we may consider doing it only once per group, somehow.
                if (player.Group != null)
                {
                    foreach (IGamePlayer playerInGroup in player.Group.GetIPlayersInTheGroup())
                    {
                        if (playerInGroup == attacker)
                            continue;

                        if (!AggroList.ContainsKey((GameLiving)playerInGroup))
                            AggroList.TryAdd((GameLiving)playerInGroup, new(0));

                        AddPetAndSubPetsToAggroList(playerInGroup);
                    }
                }
            }
            else if (attacker is GameNPC npc && npc.Brain is IControlledBrain brain)
            {
                // If the attacker is a pet, we also add its owner.
                // this prevents both receiving an aggro amount of 1 if the attack is a debuff for example, ensuring the NPC attacks the pet first.
                IGamePlayer owner = brain.GetIPlayerOwner();

                if (!AggroList.ContainsKey((GameLiving)owner))
                    AggroList.TryAdd((GameLiving)owner, new(0));
            }
        }

        private void AddPetAndSubPetsToAggroList(IGamePlayer player)
        {
            GameNPC pet = player.ControlledBrain?.Body;

            if (pet == null)
                return;

            if (!AggroList.ContainsKey(pet))
                AggroList.TryAdd(pet, new(0));

            IControlledBrain[] controlledBrains = pet.ControlledNpcList;

            if (controlledBrains == null)
                return;

            foreach (IControlledBrain subPetBrain in controlledBrains)
            {
                if (subPetBrain == null)
                    continue;

                GameNPC subPet = subPetBrain.Body;

                if (subPet == null)
                    continue;

                if (!AggroList.ContainsKey(subPet))
                    AggroList.TryAdd(subPet, new(0));
            }
        }

        #endregion Aggro

        #region Spells

        public enum eCheckSpellType
        {
            Offensive,
            Defensive,
            CrowdControl
        }

        #region Resurrect

        /// <summary>
        /// Locates the bot's Resurrect spell, regardless of which spell list
        /// the engine sorted it into. Rez is classified as misc but some
        /// builds end up putting it in Spells directly.
        /// </summary>
        private Spell FindResurrectSpell()
        {
            if (MimicBody == null)
                return null;

            if (MimicBody.MiscSpells != null)
            {
                foreach (Spell s in MimicBody.MiscSpells)
                    if (s != null && s.SpellType == eSpellType.Resurrect)
                        return s;
            }

            if (MimicBody.InstantMiscSpells != null)
            {
                foreach (Spell s in MimicBody.InstantMiscSpells)
                    if (s != null && s.SpellType == eSpellType.Resurrect)
                        return s;
            }

            if (Body.Spells != null)
            {
                foreach (Spell s in Body.Spells)
                    if (s != null && s.SpellType == eSpellType.Resurrect)
                        return s;
            }

            return null;
        }

        /// <summary>
        /// True if any other group member is currently casting a Resurrect
        /// spell on this dead member. Prevents two rezzers in the same group
        /// from wasting both casts on the same corpse.
        /// </summary>
        public bool IsBeingRezzedByGroup(GameLiving deadMember)
        {
            if (deadMember == null || Body.Group == null)
                return false;

            foreach (GameLiving gm in Body.Group.GetMembersInTheGroup())
            {
                if (gm == null || gm == Body || !gm.IsAlive || !gm.IsCasting)
                    continue;

                Spell active = gm.castingComponent?.SpellHandler?.Spell;
                if (active != null
                    && active.SpellType == eSpellType.Resurrect
                    && gm.TargetObject == deadMember)
                    return true;
            }

            return false;
        }

        /// <summary>
        /// True if any OTHER alive member of this bot's group is currently
        /// casting a crowd-control spell on <paramref name="target"/>. Used by
        /// <see cref="CheckSpells"/> when picking a CC candidate so two CC bots
        /// in the same group don't lock onto the same add — that is the most
        /// visible "CC bots don't coordinate" complaint, since one of the two
        /// casts gets wasted and another add stays loose.
        ///
        /// Mirrors <see cref="IsBeingRezzedByGroup"/>: we only check spells
        /// currently in flight (the caster is still IsCasting on a CC spell
        /// type), not effects already landed — those are filtered out by the
        /// existing <c>!LivingHasEffect(ccTarget, spell)</c> guard.
        /// </summary>
        public bool IsBeingCcedByGroup(GameLiving target)
        {
            if (target == null || Body.Group == null)
                return false;

            foreach (GameLiving gm in Body.Group.GetMembersInTheGroup())
            {
                if (gm == null || gm == Body || !gm.IsAlive || !gm.IsCasting)
                    continue;

                Spell active = gm.castingComponent?.SpellHandler?.Spell;
                if (active == null || gm.TargetObject != target)
                    continue;

                // The five spell types we consider "soft CC" for de-dupe
                // purposes. SpeedDecrease covers snares + roots; Amnesia is
                // primarily a caster lock-out so it should also de-dupe with
                // a Mez landing on the same target.
                if (active.SpellType is eSpellType.Mesmerize
                    or eSpellType.Mez
                    or eSpellType.Stun
                    or eSpellType.Amnesia
                    or eSpellType.SpeedDecrease)
                    return true;
            }

            return false;
        }

        // Lead time before the existing CC expires when we already start the
        // recast. 3 s is the standard rule of thumb: long enough to land the
        // recast under a normal cast time (2.5-3 s for mez/root), short enough
        // that the duplicate doesn't waste mana.
        private const int CC_RECAST_LEAD_MS = 3000;

        /// <summary>
        /// Variant of <see cref="LivingHasEffect"/> that only returns true when
        /// the target carries the relevant effect AND it still has more than
        /// <paramref name="minRemainingMs"/> ms before expiring. Used by the CC
        /// picker so a bot can pre-recast mez/root a few seconds before the
        /// current effect runs out, matching real-player CC discipline.
        ///
        /// Falls back to <see cref="LivingHasEffect"/>'s semantics for spells
        /// that don't map to a single ECSGameSpellEffect (procs, etc.) so we
        /// never accidentally double-cast something that is genuinely fresh.
        /// </summary>
        public bool LivingHasFreshEffect(GameLiving target, Spell spell, int minRemainingMs)
        {
            if (target == null || spell == null)
                return true;

            if (!LivingHasEffect(target, spell))
                return false;

            // Probe the actual ECSGameSpellEffect to read its ExpireTick.
            eEffect spellEffect = EffectHelper.GetEffectFromSpell(spell);
            if (spellEffect is eEffect.Unknown or eEffect.Pet or eEffect.DirectDamage)
                return true; // can't compute remaining; treat as fresh

            var active = EffectListService.GetEffectOnTarget(target, spellEffect);
            if (active == null)
                return false;

            long remaining = active.ExpireTick - GameLoop.GameLoopTime;
            return remaining > minRemainingMs;
        }

        /// <summary>
        /// If the bot has a Resurrect spell and there's a dead group member in
        /// range, start casting rez on them. Runs regardless of combat state —
        /// an experienced healer drops everything to rez. Returns true when a
        /// rez was started (or is currently in progress) so the caller can
        /// short-circuit its normal action loop.
        /// </summary>
        public bool CheckResurrect()
        {
            Spell rezSpell = FindResurrectSpell();
            if (rezSpell == null)
                return false;

            if (Body.IsStunned || Body.IsMezzed || Body.IsSilenced)
                return false;

            // Already mid-cast on rez — let it land.
            if (Body.IsCasting
                && Body.castingComponent?.SpellHandler?.Spell?.SpellType == eSpellType.Resurrect)
                return true;

            if (Body.Mana < MimicBody.PowerCost(rezSpell))
                return false;

            if (rezSpell.HasRecastDelay && Body.GetSkillDisabledDuration(rezSpell) > 0)
                return false;

            if (Body.IsBeingInterrupted && !rezSpell.Uninterruptible)
                return false;

            if (Body.Group == null)
                return false;

            GameLiving target = null;
            foreach (GameLiving gm in Body.Group.GetMembersInTheGroup())
            {
                if (gm == null || gm == Body || gm.IsAlive)
                    continue;
                if (!Body.IsWithinRadius(gm, rezSpell.Range))
                    continue;
                if (IsBeingRezzedByGroup(gm))
                    continue;

                target = gm;
                break;
            }

            if (target == null)
                return false;

            // Rez wins priority over whatever the bot was doing. Stop any
            // current cast or melee swing so the rez can start cleanly.
            if (Body.IsCasting)
                Body.StopCurrentSpellcast();
            if (Body.IsAttacking)
                Body.StopAttack();

            Body.TargetObject = target;
            return Body.CastSpell(rezSpell, MimicBody.GetSpellLineForSpell(rezSpell));
        }

        #endregion Resurrect


        /// <summary>
        /// Checks if any spells need casting
        /// </summary>
        /// <param name="type">Which type should we go through and check for?</param>
        public virtual bool CheckSpells(eCheckSpellType type)
        {
            if (Body == null || Body.Spells == null || Body.Spells.Count <= 0)
                return false;

            bool casted = false;
            List<Spell> spellsToCast = new();
            MimicCombatProfile combatProfile = MimicBody?.CombatProfile;

            // Healers should heal whether in combat or out of it.
            if (CheckHeals())
                return true;

            if (!casted && type == eCheckSpellType.CrowdControl)
            {
                GameLiving ccTarget = null;

                if (PvPMode)
                {
                    // In PvP an experienced CCer locks down healers and casters first
                    // — those are the highest-impact targets and their cast times mean
                    // a mez lands cleanly. Fall back to the current target if no high-
                    // value enemy is in range.
                    ccTarget = PickPvpCcTarget() ?? (Body.TargetObject is GameLiving livingTarget
                        && CanAggroTarget(livingTarget)
                        && !livingTarget.IsCrowdControlled
                            ? livingTarget
                            : null);
                }
                else if (MimicBody.CanCastCrowdControlSpells)
                {
                    // Pick the first CCTarget that isn't already being CC'd by
                    // another group member's in-flight cast. Random selection
                    // (the previous behaviour) caused two CC bots ticking in
                    // the same frame to lock onto the same add and waste one
                    // of the two casts. Walk the list deterministically and
                    // skip claimed targets. Falls back to null if every entry
                    // is already being handled — CheckSpells then drops out
                    // and the strategy cooldown will retry on the next tick.
                    //
                    // Snapshot is now thread-safe: MimicGroup.CCTargets returns a
                    // copy taken under the dedicated lock, so we can enumerate
                    // freely without worrying about cross-thread Add/Remove.
                    var ccTargetsSnapshot = MimicBody.Group?.MimicGroup.CCTargets;
                    if (ccTargetsSnapshot != null)
                    {
                        foreach (GameLiving candidate in ccTargetsSnapshot)
                        {
                            if (candidate == null
                                || !candidate.IsAlive
                                || candidate.ObjectState != GameObject.eObjectState.Active)
                                continue;

                            if (IsBeingCcedByGroup(candidate))
                                continue;

                            ccTarget = candidate;
                            break;
                        }
                    }
                }

                if (ccTarget != null && MimicBody.CanCastCrowdControlSpells)
                {
                    Body.TargetObject = ccTarget;

                    foreach (Spell spell in MimicBody.CrowdControlSpells)
                    {
                        // Re-cast window: a real CC player recasts mez ~3 s before it
                        // expires so the mob never gets a free swing in the gap. We
                        // treat "effect present with > 3 s remaining" as "still mezzed";
                        // anything shorter is a recast candidate.
                        if (CanCastOffensiveSpell(spell) && !LivingHasFreshEffect(ccTarget, spell, CC_RECAST_LEAD_MS))
                            spellsToCast.Add(spell);
                    }

                    if (spellsToCast.Count > 0)
                    {
                        // Prefer an AoE mez/stun when at least MIN_AOE_CLUSTER_HOSTILES
                        // non-mezzed hostiles are inside its radius around ccTarget.
                        // CountAoeHostiles vetoes (returns -1) if a mezzed mob is in
                        // the splash, so we won't re-mez and break our own CC.
                        Spell spell = spellsToCast.FirstOrDefault(s =>
                        {
                            if (s.Radius <= 0)
                                return false;

                            int hostiles = CountAoeHostiles(s, ccTarget);
                            return combatProfile?.ShouldUseAoe(hostiles, hostiles < 0, true) == true;
                        });

                        if (spell == null)
                        {
                            // Reuse the working list rather than allocating a new
                            // filtered copy — we don't read spellsToCast again
                            // after this branch picks a target. RemoveAll uses a
                            // static lambda (no captures) so it allocates zero.
                            spellsToCast.RemoveAll(static s => s.Radius > 0);
                            if (spellsToCast.Count == 0)
                                return false;

                            spell = spellsToCast[Util.Random(spellsToCast.Count - 1)];
                        }

                        casted = Body.CastSpell(spell, MimicBody.GetSpellLineForSpell(spell));

                        if (casted)
                        {
                            if (!PvPMode)
                                MimicBody.Group.MimicGroup.RemoveCcTarget(ccTarget);

                            if (spell.CastTime > 0)
                                Body.StopFollowing();
                            else if (Body.FollowTarget != Body.TargetObject)
                                Body.Follow(Body.TargetObject, spell.Range - 10, 5000);
                        }
                    }
                }
            }
            else if (!casted && type == eCheckSpellType.Defensive)
            {
                if (Body.CanCastMiscSpells)
                    casted = CheckDefensiveSpells(Body.MiscSpells);

                //if (Body.CanCastMiscSpells)
                //{
                //    foreach (Spell spell in Body.MiscSpells)
                //    {
                //        if (CheckDefensiveSpells(spell))
                //        {
                //            casted = true;
                //            break;
                //        }
                //    }
                //}
            }
            else if (!casted && type == eCheckSpellType.Offensive)
            {
                // Healer-gate-relaxed: a flagged healer can DPS when the group
                // is healthy. The original blanket block (`return false`) silenced
                // Friar / Heretic / Shaman / Bard hybrids entirely, even when
                // every member was at 100% HP — they'd stand in melee/cast range
                // doing nothing. We now skip offensive ONLY when there's actual
                // heal demand (someone below threshold) or we're low on mana.
                if (IsHealer && combatProfile?.HasRole(eMimicCombatRole.Healer) == true)
                {
                    if (NeedsToHealNow())
                        return false;
                }

                // ----------------------------------------------------------------
                // Generic mana throttle.
                // Below 20% mana, all caster archetypes stop nuking entirely so the
                // group's healer/buffer can still cast emergency spells. Between 20%
                // and 50%, casters skip every other tick (chance scales with mana).
                // The previous hard-coded Cleric-only check is now subsumed by this
                // generic rule.
                // ----------------------------------------------------------------
                if (combatProfile?.PrefersCasting == true
                    || combatProfile?.HasRole(eMimicCombatRole.Healer) == true
                    || combatProfile?.HasRole(eMimicCombatRole.Support) == true)
                {
                    if (Body.ManaPercent < 20)
                        return false;

                    if (Body.ManaPercent < 50 && !Util.Chance(Math.Max(5, Body.ManaPercent - 20)))
                        return false;
                }

                // Check instant spells, but only cast one to prevent spamming
                if (Body.CanCastInstantHarmfulSpells)
                {
                    foreach (Spell spell in Body.InstantHarmfulSpells)
                    {
                        if (CheckInstantOffensiveSpells(spell))
                            break;
                    }
                }

                if (Body.CanCastInstantMiscSpells)
                {
                    foreach (Spell spell in Body.InstantMiscSpells)
                    {
                        if (CheckInstantDefensiveSpells(spell))
                            break;
                    }
                }

                // TODO: Better nightshade casting logic. For now just make them melee but still use instants.
                if (MimicBody.CharacterClass.ID == (int)eCharacterClass.Nightshade)
                    return false;

                // TODO: This makes Thane and Valewalker use melee when in range rather than cast in all situations.
                //        but still use instants. Need to include other exceptions like maybe low health or endurance.
                if (combatProfile?.PrefersMelee == true
                    && (MimicBody.CanUsePositionalStyles || MimicBody.CanUseAnytimeStyles)
                    && (Body.IsWithinRadius(Body.TargetObject, 550) || Body.ManaPercent <= 10))
                    return false;

                if (MimicBody.CanCastCrowdControlSpells)
                {
                    GameLiving livingTarget = Body.TargetObject as GameLiving;

                    // Guard against null / non-living targets — earlier code
                    // unconditionally cast Body.TargetObject to GameLiving
                    // inside the inner loop and NPE'd when the target died or
                    // was a GameStaticItem.
                    if (livingTarget != null)
                    {
                        int ccChance = 50;

                        if (livingTarget.TargetObject == Body && Body.IsWithinRadius(livingTarget, 500))
                            ccChance = 95;

                        if (Body.Group?.MimicGroup.CurrentTarget == livingTarget)
                            ccChance = 0;

                        if (Util.Chance(ccChance))
                        {
                            foreach (Spell spell in MimicBody.CrowdControlSpells)
                            {
                                if (CanCastOffensiveSpell(spell) && !LivingHasEffect(livingTarget, spell))
                                    spellsToCast.Add(spell);
                            }
                        }
                    }
                }

                if (MimicBody.CanCastBolts && spellsToCast.Count < 1)
                {
                    foreach (Spell spell in MimicBody.BoltSpells)
                    {
                        if (CanCastOffensiveSpell(spell))
                            spellsToCast.Add(spell);
                    }
                }

                if (spellsToCast.Count < 1)
                {
                    if (Body.CanCastHarmfulSpells)
                    {
                        GameLiving liveTarget = Body.TargetObject as GameLiving;

                        foreach (Spell spell in Body.HarmfulSpells)
                        {
                            if (spell.SpellType == eSpellType.Charm ||
                                spell.SpellType == eSpellType.Amnesia ||
                                spell.SpellType == eSpellType.Confusion ||
                                spell.SpellType == eSpellType.Taunt)
                                continue;

                            if (!CanCastOffensiveSpell(spell))
                                continue;

                            if (spell.Radius > 0
                                && combatProfile != null
                                && !ShouldUseDamageAoe(combatProfile, spell, combatProfile.DamageAoeMinTargets, false))
                                continue;

                            // Skip debuffs / DoTs already applied on the target. We
                            // would just refresh-stomp our own effect for no gain.
                            if (liveTarget != null && spell.Duration > 0 && LivingHasEffect(liveTarget, spell))
                                continue;

                            // Don't try a spell we cannot afford. Saves mana for
                            // a future cast that might land instead of fizzling.
                            if (Body.Mana < MimicBody.PowerCost(spell))
                                continue;

                            spellsToCast.Add(spell);
                        }
                    }
                }

                if (spellsToCast.Count > 0)
                {
                    // Pre-score AoE clustering once per candidate so the comparator
                    // below doesn't re-iterate the aggro list. A spell with a
                    // CC'd mob in its splash returns -1 from CountAoeHostiles
                    // and is treated as non-clustered.
                    GameLiving castTarget = Body.TargetObject as GameLiving;
                    HashSet<Spell> clusteredAoe = null;

                    if (castTarget != null)
                    {
                        foreach (Spell s in spellsToCast)
                        {
                            if (!IsClusterBeneficialAoe(s))
                                continue;

                            int hostiles = CountAoeHostiles(s, castTarget);

                            if (ShouldUseDamageAoe(combatProfile, s, hostiles, hostiles < 0))
                            {
                                clusteredAoe ??= new HashSet<Spell>();
                                clusteredAoe.Add(s);
                            }
                        }
                    }

                    bool HasCluster(Spell s) => clusteredAoe != null && clusteredAoe.Contains(s);

                    // Priority sort: clustered AoE wins, then debuff-first, then nuke.
                    // Lower score = higher priority. AoE without a cluster takes a
                    // +2 score penalty so its higher base damage doesn't win against
                    // a single-target nuke when only one mob is being hit — solo AoE
                    // is a DPS loss and burns mana for nothing. Inside the same priority
                    // bracket we keep insertion order so class-specific tuning by
                    // spell-list order still matters.
                    int SortScore(Spell s)
                    {
                        int score = ScoreOffensivePriority(s);

                        if (s.Radius > 0 && !HasCluster(s))
                            score += 2;

                        return score;
                    }

                    // In-place sort with the same key precedence as the previous
                    // LINQ chain (OrderByDescending(HasCluster) ThenBy(SortScore)
                    // ThenByDescending(Damage)). Avoids the 3 enumerator + 1 List
                    // intermediate allocations the LINQ chain produced every tick.
                    spellsToCast.Sort((a, b) =>
                    {
                        int byCluster = HasCluster(b).CompareTo(HasCluster(a));
                        if (byCluster != 0) return byCluster;
                        int byScore = SortScore(a).CompareTo(SortScore(b));
                        if (byScore != 0) return byScore;
                        return b.Damage.CompareTo(a.Damage);
                    });

                    // Top of the priority list is normally the best pick. Add a small
                    // amount of variety (10% chance to pick second-best) so groups
                    // of mimics don't all cast the exact same spell on the same tick.
                    // We only swap when the two candidates share clustering tier —
                    // otherwise the variety could drop a clustered AoE for a
                    // single-target nuke and undo the AoE preference.
                    Spell spellToCast = spellsToCast[0];
                    if (spellsToCast.Count > 1 && Util.Chance(10)
                        && HasCluster(spellsToCast[0]) == HasCluster(spellsToCast[1]))
                        spellToCast = spellsToCast[1];

                    if (spellToCast.IsPBAoE)
                    {
                        GameLiving pbaoeTarget = PickPbaoeCastTarget(spellToCast, Body.TargetObject as GameLiving);
                        if (pbaoeTarget != null)
                            Body.TargetObject = pbaoeTarget;
                    }

                    if (spellToCast.Uninterruptible || !Body.IsBeingInterrupted)
                        casted = CheckOffensiveSpells(spellToCast);
                    else if (!spellToCast.Uninterruptible && Body.IsBeingInterrupted)
                    {
                        // Interrupt reaction: any caster archetype (not just pure
                        // ListCasters) tries to QuickCast through the interrupt so
                        // mana isn't burned for nothing.
                        if (TryQuickCastThroughInterrupt(spellToCast))
                            casted = CheckOffensiveSpells(spellToCast);
                    }
                }
            }

            return casted || Body.IsCasting;
        }

        protected bool CanCastOffensiveSpell(Spell spell)
        {
            if (Body.GetSkillDisabledDuration(spell) <= 0)
            {
                if (spell.CastTime > 0)
                {
                    if (spell.Target is eSpellTarget.ENEMY or eSpellTarget.AREA or eSpellTarget.CONE)
                    {
                        // Block pet summons when the bot is already at the pet cap.
                        // Without this guard the bot keeps trying to summon, the
                        // engine refuses ("too many controlled creatures"), and the
                        // bot dead-locks on the same useless cast instead of nuking.
                        if (IsAtPetCap(spell))
                            return false;
                        return true;
                    }
                }
            }

            return false;
        }

        /// <summary>
        /// Returns true if the spell is a pet-summon-in-combat (Theurgist/Animist
        /// turret style) and the caster is already at the configured pet cap.
        /// Permanent-pet summons (Cabalist commander, BD commander, etc.) are
        /// handled by the engine's own one-pet limit and never returned here.
        /// </summary>
        protected bool IsAtPetCap(Spell spell)
        {
            if (spell == null)
                return false;

            switch (spell.SpellType)
            {
                case eSpellType.SummonTheurgistPet:
                    return Body.PetCount >= DOL.GS.ServerProperties.Properties.THEURGIST_PET_CAP;
                case eSpellType.SummonAnimistPet:
                case eSpellType.SummonAnimistFnF:
                case eSpellType.SummonAnimistFnFCustom:
                case eSpellType.SummonAnimistAmbusher:
                    // Animist turrets are area-capped by the engine; we just defer
                    // to a generous heuristic to avoid stacking too many on one cast.
                    return Body.PetCount >= 8;
                default:
                    return false;
            }
        }

        /// <summary>
        /// Priority score for offensive spell selection. Lower = cast first.
        /// The order encodes a generic but proven DAoC opener pattern:
        ///   1. Snare / root  — keeps the mob at range, sets up follow-up
        ///   2. Disease       — applies the strength debuff before melee swings
        ///   3. Stat debuffs  — reduces incoming damage to the group
        ///   4. DoT           — front-loads damage that ticks during the fight
        ///   5. DD-with-debuff and DD-with-snare — efficient mixed casts
        ///   6. Bolts         — burst opener
        ///   7. Direct damage — pure nukes
        ///   8. Anything else
        /// </summary>
        protected static int ScoreOffensivePriority(Spell s)
        {
            if (s == null)
                return 99;

            switch (s.SpellType)
            {
                // Pet summons that are the class's core combat tool. Theurgists
                // and Animists fight by saturating the field with short-lived
                // pets/turrets; spamming DDs while ignoring summons crippled
                // them. Priority 0 ensures we cast them whenever the cap allows.
                case eSpellType.SummonTheurgistPet:
                case eSpellType.SummonAnimistPet:
                case eSpellType.SummonAnimistFnF:
                case eSpellType.SummonAnimistFnFCustom:
                case eSpellType.SummonAnimistAmbusher:
                    return 0;
                case eSpellType.SpeedDecrease: return 0;
                case eSpellType.Disease: return 1;
                case eSpellType.StrengthDebuff:
                case eSpellType.DexterityDebuff:
                case eSpellType.StrengthConstitutionDebuff:
                case eSpellType.DexterityQuicknessDebuff:
                case eSpellType.MeleeDamageDebuff:
                case eSpellType.CombatSpeedDebuff:
                case eSpellType.ArmorFactorDebuff:
                case eSpellType.AllStatsPercentDebuff:
                case eSpellType.CrushSlashThrustDebuff:
                case eSpellType.EffectivenessDebuff:
                    return 2;
                case eSpellType.DamageOverTime: return 3;
                case eSpellType.DirectDamageWithDebuff:
                case eSpellType.DamageSpeedDecrease:
                    return 4;
                case eSpellType.Bolt: return 5;
                case eSpellType.Lifedrain: return 6;
                case eSpellType.DirectDamage: return 7;
                default: return 10;
            }
        }

        // Below this hostile count an AoE damage spell is not worth the cast:
        // single-target spells almost always out-DPS a 1-target AoE because of
        // the variance/level penalty AoE damage takes per cap.
        private const int MIN_AOE_CLUSTER_HOSTILES = 2;

        // AoE spell types that scale with the number of mobs in the radius.
        // CC AoE (mez/stun) and debuff AoE are intentionally excluded — those
        // are routed through the dedicated CC path and would otherwise stomp
        // existing CC the group manages via [[MimicGroup.CCTargets]].
        private static bool IsClusterBeneficialAoe(Spell s)
        {
            if (s == null || s.Radius <= 0)
                return false;

            switch (s.SpellType)
            {
                case eSpellType.DirectDamage:
                case eSpellType.DirectDamageWithDebuff:
                case eSpellType.DamageOverTime:
                case eSpellType.DamageSpeedDecrease:
                case eSpellType.Lifedrain:
                    return true;
                default:
                    return false;
            }
        }

        private static bool ShouldUseDamageAoe(MimicCombatProfile combatProfile, Spell spell, int hostileCount, bool wouldBreakCrowdControl)
        {
            if (combatProfile == null || spell == null)
                return false;

            return spell.IsPBAoE
                ? combatProfile.ShouldUsePbaoe(hostileCount, wouldBreakCrowdControl)
                : combatProfile.ShouldUseAoe(hostileCount, wouldBreakCrowdControl, false);
        }

        /// <summary>
        /// PvP CC target priority: prefer enemy healers, then casters, then the
        /// rest. Excludes already-CC'd targets, dead targets, anyone outside the
        /// best CC spell's range, and the group's current focus (so we don't
        /// mez whatever the assist is killing). Returns null when no good
        /// candidate exists — caller falls back to TargetObject.
        /// </summary>
        private GameLiving PickPvpCcTarget()
        {
            if (!MimicBody.CanCastCrowdControlSpells)
                return null;

            int bestRange = 0;
            foreach (Spell s in MimicBody.CrowdControlSpells)
            {
                if (s != null && s.Range > bestRange)
                    bestRange = s.Range;
            }

            if (bestRange <= 0)
                return null;

            GameLiving focus = Body.Group?.MimicGroup?.MainAssist?.TargetObject as GameLiving;
            MimicCombatProfile ccProfile = MimicBody.CombatProfile;
            if (ccProfile == null)
                return null;

            GameLiving best = null;
            int bestScore = int.MaxValue;
            int bestDist = int.MaxValue;

            foreach (GamePlayer player in Body.GetPlayersInRadius((ushort)bestRange))
            {
                if (player == null || !player.IsAlive)
                    continue;
                if (!CanAggroTarget(player))
                    continue;
                if (player.IsCrowdControlled || player.IsStealthed)
                    continue;
                if (player == focus)
                    continue;

                int dist = Body.GetDistanceTo(player);
                int score = ccProfile.ScoreTarget(
                    MimicCombatProfileRegistry.GetForLiving(player),
                    eMimicCombatMode.PvP,
                    player == focus,
                    player.TargetObject == Body,
                    IsAttackingProtectedMember(player),
                    player.HealthPercent <= 35,
                    player.IsCrowdControlled,
                    dist);

                if (score < bestScore || (score == bestScore && dist < bestDist))
                {
                    best = player;
                    bestScore = score;
                    bestDist = dist;
                }
            }

            // Mimic bots (enemy bots running this same brain) — also IGamePlayer.
            foreach (GameNPC npc in Body.GetNPCsInRadius((ushort)bestRange))
            {
                if (npc is not MimicNPC mimic || !mimic.IsAlive)
                    continue;
                if (!CanAggroTarget(mimic))
                    continue;
                if (mimic.IsCrowdControlled)
                    continue;
                if (mimic == focus)
                    continue;

                int dist = Body.GetDistanceTo(mimic);
                int score = ccProfile.ScoreTarget(
                    MimicCombatProfileRegistry.GetForLiving(mimic),
                    eMimicCombatMode.PvP,
                    mimic == focus,
                    mimic.TargetObject == Body,
                    IsAttackingProtectedMember(mimic),
                    mimic.HealthPercent <= 35,
                    mimic.IsCrowdControlled,
                    dist);

                if (score < bestScore || (score == bestScore && dist < bestDist))
                {
                    best = mimic;
                    bestScore = score;
                    bestDist = dist;
                }
            }

            return best;
        }

        /// <summary>
        /// Counts active group hostiles caught inside the AoE footprint.
        /// Epicenter is the caster for PBAoE, otherwise the primary target.
        /// Mezzed/rooted mobs and queued CC targets veto damage AoE so we do not
        /// break the group's control plan.
        /// </summary>
        private int CountAoeHostiles(Spell spell, GameLiving primaryTarget)
        {
            if (spell == null || spell.Radius <= 0 || primaryTarget == null)
                return 0;

            // Veto AoE on epic / boss-class targets. AoE on a boss splits aggro
            // (a fresh hostile entering range gets flagged for the same dispatcher
            // tick) and wastes mana on a single high-HP enemy that single-target
            // nukes burn down faster. The -1 sentinel is the same one the CC
            // veto below uses, so the existing ShouldUseAoe path picks it up.
            if (primaryTarget is IGameEpicNpc)
                return -1;

            bool isPBAoE = spell.IsPBAoE;
            int radius = spell.Radius;
            // Snapshot once into a HashSet for O(1) Contains. The CCTargets
            // accessor on MimicGroup already returns a fresh copy under the
            // dedicated lock so this is race-free.
            HashSet<GameLiving> ccTargetSet = null;
            var ccTargetsSnap = Body.Group?.MimicGroup?.CCTargets;
            if (ccTargetsSnap != null && ccTargetsSnap.Count > 0)
            {
                ccTargetSet = new HashSet<GameLiving>(ccTargetsSnap);
            }

            HashSet<GameLiving> counted = new();
            int count = 0;

            bool TryCount(GameLiving hostile)
            {
                if (!IsValidAoeHostile(hostile, primaryTarget))
                    return true;

                bool inRange = isPBAoE
                    ? Body.IsWithinRadius(hostile, radius)
                    : primaryTarget.IsWithinRadius(hostile, radius);

                if (!inRange)
                    return true;

                bool protectedCrowdControl = (ccTargetSet != null && ccTargetSet.Contains(hostile))
                    || hostile.IsMezzed
                    || (hostile.IsRooted && hostile != primaryTarget && hostile != Body.TargetObject);

                if (protectedCrowdControl)
                    return false;

                if (counted.Add(hostile))
                    count++;

                return true;
            }

            if (!TryCount(primaryTarget))
                return -1;

            foreach (var kv in AggroList)
            {
                if (!TryCount(kv.Key))
                    return -1; // splash would break our own CC; veto this AoE
            }

            ushort scanRadius = (ushort)Math.Min(radius, ushort.MaxValue);
            GameObject epicenter = isPBAoE ? Body : primaryTarget;
            foreach (GameNPC npc in epicenter.GetNPCsInRadius(scanRadius))
            {
                if (!TryCount(npc))
                    return -1;
            }

            return count;
        }

        private bool IsValidAoeHostile(GameLiving hostile, GameLiving primaryTarget)
        {
            if (hostile == null || !hostile.IsAlive || hostile.ObjectState != GameObject.eObjectState.Active)
                return false;

            if (hostile == Body)
                return false;

            if (hostile is GameTaxi or GameTrainingDummy)
                return false;

            if (Body.Group != null && Body.Group.IsInTheGroup(hostile))
                return false;

            if (!CanAggroTarget(hostile))
                return false;

            if (hostile == primaryTarget || hostile == Body.TargetObject || AggroList.ContainsKey(hostile))
                return true;

            if (Body.Group == null)
                return false;

            if (hostile.TargetObject is GameLiving target && Body.Group.IsInTheGroup(target))
                return true;

            if (hostile is GameNPC npc && npc.Brain is StandardMobBrain brain && brain.HasAggro)
            {
                var ordered = brain.GetOrderedAggroList();
                if (ordered != null)
                {
                    for (int i = 0; i < ordered.Count; i++)
                    {
                        GameLiving aggroed = ordered[i].Living;
                        if (aggroed != null && Body.Group.IsInTheGroup(aggroed))
                            return true;
                    }
                }
            }

            return false;
        }

        private GameLiving PickPbaoeCastTarget(Spell spell, GameLiving currentTarget)
        {
            if (spell == null || !spell.IsPBAoE || spell.Radius <= 0)
                return currentTarget;

            if (currentTarget != null && Body.IsWithinRadius(currentTarget, spell.Radius) && IsValidAoeHostile(currentTarget, currentTarget))
                return currentTarget;

            GameLiving best = null;
            int bestDistance = int.MaxValue;

            foreach (var kv in AggroList)
            {
                GameLiving hostile = kv.Key;
                if (!IsValidAoeHostile(hostile, currentTarget))
                    continue;
                if (!Body.IsWithinRadius(hostile, spell.Radius))
                    continue;

                int distance = Body.GetDistanceTo(hostile);
                if (distance < bestDistance)
                {
                    best = hostile;
                    bestDistance = distance;
                }
            }

            ushort scanRadius = (ushort)Math.Min(spell.Radius, ushort.MaxValue);
            foreach (GameNPC npc in Body.GetNPCsInRadius(scanRadius))
            {
                if (!IsValidAoeHostile(npc, currentTarget))
                    continue;

                int distance = Body.GetDistanceTo(npc);
                if (distance < bestDistance)
                {
                    best = npc;
                    bestDistance = distance;
                }
            }

            return best ?? currentTarget;
        }

        /// <summary>
        /// Attempts to fire QuickCast so the next spell can land through an
        /// interrupt. Used by any caster archetype, not just pure ListCasters.
        /// Returns true if QuickCast was activated (so the caller can proceed
        /// with the spell cast), or if QuickCast isn't needed because the bot
        /// isn't being interrupted any more.
        /// </summary>
        private bool TryQuickCastThroughInterrupt(Spell spellToCast)
        {
            if (spellToCast == null || spellToCast.Uninterruptible)
                return true;

            if (!Body.IsBeingInterrupted)
                return true;

            Ability quickCast = Body.GetAbility(Abilities.Quickcast);

            if (quickCast == null || Body.GetSkillDisabledDuration(quickCast) > 0)
                return false;

            // Give mimics a small bump in duration, they don't use it as well as humans.
            new QuickCastECSGameEffect(new ECSGameEffectInitParams(Body, QuickCastECSGameEffect.DURATION + 1000, 1));
            Body.DisableSkill(quickCast, 180000);
            return true;
        }

        protected bool CanCastDefensiveSpell(Spell spell)
        {
            if (spell == null || spell.IsHarmful)
                return false;

            // Make sure we're currently able to cast the spell.
            if (spell.CastTime > 0 && Body.IsBeingInterrupted && !spell.Uninterruptible)
                return false;

            // Make sure the spell isn't disabled.
            if (Body.GetSkillDisabledDuration(spell) > 0)
                return false;

            return true;
        }


        protected virtual GameLiving FindTargetForDefensiveSpell(Spell spell)
        {
            GameLiving target = null;

            switch (spell.SpellType)
            {
                #region Pulse

                case eSpellType.SpeedEnhancement when spell.IsPulsing:

                if (!LivingHasEffect(Body, spell))
                    target = Body;

                break;

                case eSpellType.Bladeturn when spell.IsPulsing:
                break;

                // TODO: Fix damageshields with low duration.
                case eSpellType.DamageShield when spell.Duration == 60000:
                break;

                case eSpellType.MesmerizeDurationBuff when spell.IsPulsing:
                break;

                #endregion Pulse

                #region Buffs

                case eSpellType.SpeedEnhancement when spell.IsInstantCast:
                break;

                case eSpellType.SpeedEnhancement when spell.IsPulsing:
                case eSpellType.SpeedEnhancement when spell.Target == eSpellTarget.PET:
                case eSpellType.CombatSpeedBuff when spell.Duration > 20:
                case eSpellType.CombatSpeedBuff when spell.IsConcentration:
                case eSpellType.MesmerizeDurationBuff when !spell.IsPulsing:
                case eSpellType.Bladeturn when !spell.IsPulsing:

                case eSpellType.AcuityBuff:
                case eSpellType.AFHitsBuff:
                case eSpellType.AllMagicResistBuff:
                case eSpellType.ArmorAbsorptionBuff:
                case eSpellType.BaseArmorFactorBuff:
                case eSpellType.SpecArmorFactorBuff:
                case eSpellType.PaladinArmorFactorBuff:
                case eSpellType.BodyResistBuff:
                case eSpellType.BodySpiritEnergyBuff:
                case eSpellType.Buff:
                case eSpellType.CelerityBuff:
                case eSpellType.ColdResistBuff:
                case eSpellType.CombatSpeedBuff:
                case eSpellType.ConstitutionBuff:
                case eSpellType.CourageBuff:
                case eSpellType.CrushSlashTrustBuff:
                case eSpellType.DexterityBuff:
                case eSpellType.DexterityQuicknessBuff:
                case eSpellType.EffectivenessBuff:
                case eSpellType.EnduranceRegenBuff:
                case eSpellType.EnergyResistBuff:
                case eSpellType.FatigueConsumptionBuff:
                case eSpellType.FlexibleSkillBuff:
                case eSpellType.HasteBuff:
                case eSpellType.HealthRegenBuff:
                case eSpellType.HeatColdMatterBuff:
                case eSpellType.HeatResistBuff:
                case eSpellType.HeroismBuff:
                case eSpellType.KeepDamageBuff:
                case eSpellType.MagicResistBuff:
                case eSpellType.MatterResistBuff:
                case eSpellType.MeleeDamageBuff:
                case eSpellType.MesmerizeDurationBuff:
                case eSpellType.MLABSBuff:
                case eSpellType.ParryBuff:
                case eSpellType.PowerHealthEnduranceRegenBuff:
                case eSpellType.PowerRegenBuff:
                case eSpellType.SavageCombatSpeedBuff:
                case eSpellType.SavageCrushResistanceBuff:
                case eSpellType.SavageDPSBuff:
                case eSpellType.SavageParryBuff:
                case eSpellType.SavageSlashResistanceBuff:
                case eSpellType.SavageThrustResistanceBuff:
                case eSpellType.SpiritResistBuff:
                case eSpellType.StrengthBuff:
                case eSpellType.StrengthConstitutionBuff:
                case eSpellType.SuperiorCourageBuff:
                case eSpellType.ToHitBuff:
                case eSpellType.WeaponSkillBuff:
                case eSpellType.DamageAdd:
                case eSpellType.OffensiveProc:
                case eSpellType.DefensiveProc:
                case eSpellType.DamageShield:
                case eSpellType.Bladeturn:
                {
                    if (spell.IsConcentration)
                    {
                        if (spell.Concentration > Body.Concentration)
                            break;

                        if (Body.effectListComponent.GetConcentrationEffects().Count >= 20)
                            break;
                    }

                    if (!LivingHasEffect(Body, spell) && !Body.attackComponent.AttackState && spell.Target != eSpellTarget.PET)
                    {
                        target = Body;
                        break;
                    }

                    if (Body.ControlledBrain != null && Body.ControlledBrain.Body != null && Body.GetDistanceTo(Body.ControlledBrain.Body) <= spell.Range && !LivingHasEffect(Body.ControlledBrain.Body, spell) && spell.Target != eSpellTarget.SELF)
                    {
                        if (spell.SpellType == eSpellType.DamageShield)
                            break;

                        target = Body.ControlledBrain.Body;
                        break;
                    }

                    if (Body.Group != null)
                    {
                        if (spell.Target == eSpellTarget.REALM || spell.Target == eSpellTarget.GROUP)
                        {
                            foreach (GameLiving groupMember in Body.Group.GetMembersInTheGroup())
                            {
                                if (groupMember != Body)
                                {
                                    if (!LivingHasEffect(groupMember, spell) && !Body.attackComponent.AttackState && groupMember.IsAlive)
                                    {
                                        target = groupMember;
                                        break;
                                    }
                                }
                            }
                        }
                    }

                    break;
                }

                #endregion Buffs

                #region Disease Cure/Poison Cure/Summon

                case eSpellType.CureDisease:
                {
                    if (Body.IsDiseased)
                    {
                        target = Body;
                        break;
                    }

                    if (Body.ControlledBrain != null && Body.ControlledBrain.Body != null && Body.ControlledBrain.Body.IsDiseased
                        && Body.GetDistanceTo(Body.ControlledBrain.Body) <= spell.Range && spell.Target != eSpellTarget.SELF)
                    {
                        target = Body.ControlledBrain.Body;
                        break;
                    }

                    break;
                }

                case eSpellType.CurePoison:
                {
                    if (Body.IsPoisoned)
                    {
                        target = Body;
                        break;
                    }

                    if (Body.ControlledBrain != null &&
                        Body.ControlledBrain.Body != null &&
                        Body.ControlledBrain.Body.IsPoisoned &&
                        Body.GetDistanceTo(Body.ControlledBrain.Body) <= spell.Range && spell.Target != eSpellTarget.SELF)
                    {
                        target = Body.ControlledBrain.Body;
                        break;
                    }

                    break;
                }

                case eSpellType.Summon:
                {
                    target = Body;
                    break;
                }

                case eSpellType.SummonMinion:
                {
                    if (Body.ControlledBrain?.Body == null)
                        break;

                    IControlledBrain[] icb = Body.ControlledBrain.Body.ControlledNpcList;

                    int numberOfPets = 0;
                    for (int i = 0; i < icb.Length; i++)
                    {
                        if (icb[i] != null)
                            numberOfPets++;
                    }

                    if (numberOfPets >= icb.Length)
                        break;

                    int cumulativeLevel = 0;

                    foreach (IControlledBrain controlledBodyControlledBrain in icb)
                        cumulativeLevel += controlledBodyControlledBrain?.Body != null ? controlledBodyControlledBrain.Body.Level : 0;

                    byte newPetLevel = (byte)(Body.Level * spell.Damage * -0.01);

                    if (newPetLevel > spell.Value)
                        newPetLevel = (byte)spell.Value;

                    if (cumulativeLevel + newPetLevel > 75)
                        break;

                    target = Body;

                    break;
                }

                #endregion Disease Cure/Poison Cure/Summon

                #region Heals

                case eSpellType.CombatHeal:
                case eSpellType.Heal:
                case eSpellType.HealOverTime:
                case eSpellType.MercHeal:
                case eSpellType.OmniHeal:
                case eSpellType.PBAoEHeal:
                case eSpellType.SpreadHeal:
                {
                    if (Body.ControlledBrain != null && Body.ControlledBrain.Body != null
                        && Body.GetDistanceTo(Body.ControlledBrain.Body) <= spell.Range
                        && Body.ControlledBrain.Body.HealthPercent < Properties.NPC_HEAL_THRESHOLD
                        && spell.Target != eSpellTarget.SELF)
                    {
                        target = Body.ControlledBrain.Body;
                        break;
                    }

                    break;
                }

                #endregion

                case eSpellType.SummonCommander:
                case eSpellType.SummonDruidPet:
                case eSpellType.SummonHunterPet:
                case eSpellType.SummonNecroPet:
                case eSpellType.SummonUnderhill:
                case eSpellType.SummonSimulacrum:
                case eSpellType.SummonSpiritFighter:
                {
                    if (Body.ControlledBrain != null)
                        break;

                    target = Body;
                    break;
                }

                case eSpellType.Resurrect:
                {
                    // Previously: assigned to Body.TargetObject instead of the
                    // out parameter, so CanCastDefensiveSpell saw target == null
                    // and skipped the cast. Rez had never actually fired through
                    // this path. Caller (CheckDefensiveSpells) handles the
                    // TargetObject swap itself.
                    if (Body.Group != null)
                    {
                        foreach (GameLiving groupMember in Body.Group.GetMembersInTheGroup())
                        {
                            if (groupMember == null || groupMember == Body || groupMember.IsAlive)
                                continue;
                            if (!Body.IsWithinRadius(groupMember, spell.Range))
                                continue;
                            if (IsBeingRezzedByGroup(groupMember))
                                continue;

                            target = groupMember;
                            break;
                        }
                    }

                    break;
                }

                default:
                break;
            }

            return target;
        }

        /// <summary>
        /// Checks offensive spells.  Handles dds, debuffs, etc.
        /// </summary>
        protected virtual bool CheckOffensiveSpells(Spell spell, bool quickCast = false)
        {
            if (spell.NeedInstrument && Body.ActiveWeaponSlot != eActiveWeaponSlot.Distance)
                Body.SwitchWeapon(eActiveWeaponSlot.Distance);

            bool casted = false;

            if (Body.TargetObject is GameLiving living && (spell.Duration == 0 || !LivingHasEffect(living, spell) || spell.SpellType == eSpellType.DirectDamageWithDebuff || spell.SpellType == eSpellType.DamageSpeedDecrease))
            {
                if (Debug)
                    log.Info(Body.Name + " tried to cast " + spell.Name + " " + spell.SpellType.ToString() + " on " + Body.TargetObject.Name);

                casted = Body.CastSpell(spell, MimicBody.GetSpellLineForSpell(spell));
            }

            return casted;
        }

        protected virtual bool CheckInstantDefensiveSpells(Spell spell)
        {
            if (spell.HasRecastDelay && Body.GetSkillDisabledDuration(spell) > 0)
                return false;

            bool castSpell = false;

            switch (spell.SpellType)
            {
                // TODO: Stealth archer using speed to get away or attack
                //case eSpellType.SpeedEnhancement:

                case eSpellType.SavageCrushResistanceBuff:
                case eSpellType.SavageSlashResistanceBuff:
                case eSpellType.SavageThrustResistanceBuff:
                case eSpellType.SavageCombatSpeedBuff:
                case eSpellType.SavageDPSBuff:
                case eSpellType.SavageParryBuff:
                case eSpellType.SavageEvadeBuff:

                if (spell.SpellType == eSpellType.SavageCrushResistanceBuff ||
                    spell.SpellType == eSpellType.SavageSlashResistanceBuff ||
                    spell.SpellType == eSpellType.SavageThrustResistanceBuff &&
                    !CheckSavageResistSpell(spell.SpellType))
                    break;

                if (!LivingHasEffect(Body, spell))
                    castSpell = true;

                break;

                case eSpellType.BodySpiritEnergyBuff:
                case eSpellType.HeatColdMatterBuff:
                case eSpellType.SpiritResistBuff:
                case eSpellType.EnergyResistBuff:
                case eSpellType.HeatResistBuff:
                case eSpellType.ColdResistBuff:
                case eSpellType.BodyResistBuff:
                case eSpellType.MatterResistBuff:
                {
                    // Temp to stop Paladins/Skalds from spamming.
                    // TODO: Smarter use of resist chants.
                    if (spell.Pulse > 0)
                        break;

                    break;
                }

                case eSpellType.EnduranceRegenBuff:
                case eSpellType.Bladeturn:
                case eSpellType.AblativeArmor:
                case eSpellType.CombatHeal:
                case eSpellType.DamageAdd:
                case eSpellType.PaladinArmorFactorBuff:
                case eSpellType.DexterityQuicknessBuff:
                case eSpellType.CombatSpeedBuff:
                case eSpellType.OffensiveProc:
                case eSpellType.SummonHunterPet:

                if (spell.UsePulsePower)
                {
                    if (!Body.InCombat)
                        break;
                }

                if (spell.SpellType == eSpellType.CombatSpeedBuff)
                {
                    if (Body.TargetObject != null && !Body.IsWithinRadius(Body.TargetObject, Body.MeleeAttackRange))
                        break;
                }

                // Ablative absorbs the next incoming hits — only worth burning
                // mana on when we're actually about to take damage (in combat
                // with HP starting to drop, or PvP).
                if (spell.SpellType == eSpellType.AblativeArmor)
                {
                    if (!Body.InCombat && !PvPMode)
                        break;
                    if (Body.HealthPercent >= 95 && !PvPMode)
                        break;
                }

                // CombatHeal is a one-shot instant heal: LivingHasEffect always
                // returns false so without a gate we'd burn it on full HP.
                if (spell.SpellType == eSpellType.CombatHeal)
                {
                    if (!Body.InCombat)
                        break;
                    if (Body.HealthPercent >= 80)
                        break;
                }

                // Bladeturn is a single-charge absorb. Don't cast it out of
                // combat — the charge ticks down via interruptions/movement.
                if (spell.SpellType == eSpellType.Bladeturn)
                {
                    if (!Body.InCombat && !PvPMode)
                        break;
                }

                if (!LivingHasEffect(Body, spell))
                    castSpell = true;

                break;
            }

            if (castSpell)
                Body.CastSpell(spell, MimicBody.GetSpellLineForSpell(spell));

            return castSpell;
        }

        /// <summary>
        /// Checks Instant Spells.  Handles Taunts, shouts, stuns, etc.
        /// </summary>
        protected virtual bool CheckInstantOffensiveSpells(Spell spell)
        {
            if (spell.HasRecastDelay && Body.GetSkillDisabledDuration(spell) > 0)
                return false;

            bool castSpell = false;

            switch (spell.SpellType)
            {
                #region Enemy Spells

                case eSpellType.Taunt:

                if (Body.Group?.MimicGroup.MainTank == Body)
                    castSpell = true;

                break;

                case eSpellType.DirectDamage:
                case eSpellType.NightshadeNuke:
                case eSpellType.Lifedrain:
                case eSpellType.DexterityDebuff:
                case eSpellType.DexterityQuicknessDebuff:
                case eSpellType.StrengthDebuff:
                case eSpellType.StrengthConstitutionDebuff:
                case eSpellType.CombatSpeedDebuff:
                case eSpellType.DamageOverTime:
                case eSpellType.MeleeDamageDebuff:
                case eSpellType.AllStatsPercentDebuff:
                case eSpellType.CrushSlashThrustDebuff:
                case eSpellType.EffectivenessDebuff:
                case eSpellType.Disease:
                case eSpellType.Stun:
                case eSpellType.Mez:
                case eSpellType.Mesmerize:

                if (Body.TargetObject is not GameLiving instantTarget)
                    break;

                if (spell.IsPBAoE && !Body.IsWithinRadius(Body.TargetObject, spell.Radius))
                    break;

                if (spell.Radius > 0)
                {
                    int hostiles = CountAoeHostiles(spell, instantTarget);
                    bool crowdControlSpell = spell.SpellType is eSpellType.Stun or eSpellType.Mez or eSpellType.Mesmerize;
                    bool useAoe = crowdControlSpell
                        ? MimicBody?.CombatProfile?.ShouldUseAoe(hostiles, hostiles < 0, true) == true
                        : ShouldUseDamageAoe(MimicBody?.CombatProfile, spell, hostiles, hostiles < 0);

                    if (!useAoe)
                        break;
                }

                // Try to limit the debuffs cast to save mana and time spent doing so.
                if (MimicBody.CharacterClass.ClassType == eClassType.ListCaster)
                {
                    if (!Util.Chance(Math.Max(5, Body.ManaPercent - 75)))
                        break;
                }

                if (!LivingHasEffect(instantTarget, spell) && Body.IsWithinRadius(Body.TargetObject, spell.Range))
                    castSpell = true;

                break;

                #endregion Enemy Spells
            }

            ECSGameEffect pulseEffect = EffectListService.GetPulseEffectOnTarget(Body, spell);

            if (pulseEffect != null)
                return false;

            if (castSpell)
            {
                Body.CastSpell(spell, MimicBody.GetSpellLineForSpell(spell));
                return true;
            }

            return false;
        }

        protected virtual bool CheckSavageResistSpell(eSpellType spellType)
        {
            eDamageType damageType = eDamageType.Natural;

            switch (spellType)
            {
                case eSpellType.SavageCrushResistanceBuff:
                damageType = eDamageType.Crush;
                break;

                case eSpellType.SavageSlashResistanceBuff:
                damageType = eDamageType.Slash;
                break;

                case eSpellType.SavageThrustResistanceBuff:
                damageType = eDamageType.Thrust;
                break;
            }

            if (Body.attackComponent.AttackerTracker.Count > 0)
            {
                foreach (var attacker in Body.attackComponent.AttackerTracker.Attackers)
                {
                    if (attacker.ActiveWeapon != null)
                    {
                        if (attacker.ActiveWeapon.Type_Damage != 0 && (int)damageType == attacker.ActiveWeapon.Type_Damage)
                            return true;
                    }
                    else if (attacker is GameNPC npc)
                    {
                        if (npc.MeleeDamageType == damageType)
                            return true;
                    }
                }
            }

            return false;
        }

        /// <summary>
        /// Checks if the living target has a spell effect.
        /// Only to be used for spell casting purposes.
        /// </summary>
        /// <returns>True if the living has the effect of will receive it by our current spell.</returns>
        public bool LivingHasEffect(GameLiving target, Spell spell)
        {
            if (target == null)
                return true;

            eEffect spellEffect = EffectHelper.GetEffectFromSpell(spell);

            // Ignore effects that aren't actually effects (may be incomplete).
            if (spellEffect is eEffect.DirectDamage or eEffect.Pet or eEffect.Unknown)
                return false;

            SpellHandler spellHandler = Body.castingComponent.SpellHandler;

            // If we're currently casting 'spell' on 'target', assume it already has the effect.
            // This allows spell queuing while preventing casting on the same target more than once.
            if (spellHandler != null && spellHandler.Spell.ID == spell.ID && spellHandler.Target == target)
                return true;

            SpellHandler queuedSpellHandler = Body.castingComponent.QueuedSpellHandler;

            // Do the same for our queued up spell.
            // This can happen on charmed pets having two buffs that they're trying to cast on their owner.
            if (queuedSpellHandler != null && queuedSpellHandler.Spell.ID == spell.ID && queuedSpellHandler.Target == target)
                return true;

            // May not be the right place for that, but without that check NPCs with more than one offensive or defensive proc will only buff themselves once.
            if (spell.SpellType is eSpellType.OffensiveProc or eSpellType.DefensiveProc)
            {
                List<ECSGameSpellEffect> existingEffects = target.effectListComponent.GetSpellEffects(spellEffect);

                foreach (ECSGameSpellEffect effect in existingEffects)
                {
                    if (effect.SpellHandler.Spell.ID == spell.ID || (spell.EffectGroup > 0 && effect.SpellHandler.Spell.EffectGroup == spell.EffectGroup))
                        return true;
                }

                return false;
            }

            // True if the target has the effect, or the immunity effect for this effect.
            // Treat NPC immunity effects as full immunity effects.
            return EffectListService.GetEffectOnTarget(target, spellEffect) != null ||
                HasImmunityEffect(target, EffectHelper.GetImmunityEffectFromSpell(spell)) ||
                HasImmunityEffect(target, EffectHelper.GetNpcImmunityEffectFromSpell(spell));

            static bool HasImmunityEffect(GameLiving target, eEffect immunityEffect)
            {
                return immunityEffect is not eEffect.Unknown && EffectListService.GetEffectOnTarget(target, immunityEffect) != null;
            }
        }

        #endregion Spells

        public class OrderedAggroListElement
        {
            public GameLiving Living { get; }
            public long AggroAmount { get; }

            public OrderedAggroListElement(GameLiving living, long aggroAmount)
            {
                Living = living;
                AggroAmount = aggroAmount;
            }
        }
    }
}

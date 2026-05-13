using DOL.Database;
using DOL.GS;
using DOL.GS.API;
using DOL.GS.Effects;
using DOL.GS.Keeps;
using DOL.GS.PacketHandler;
using DOL.GS.RealmAbilities;
using DOL.GS.Scripts;
using DOL.GS.Scripts.AI.Strategies;
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
    public class MimicBrain : ABrain, IOldAggressiveBrain
    {
        protected static readonly Logging.Logger log = Logging.LoggerManager.Create(MethodBase.GetCurrentMethod().DeclaringType);

        public override bool IsActive => Body != null && Body.IsAlive && Body.ObjectState == GameObject.eObjectState.Active;

        public bool IsHealer = false;
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
                return _strategyManager;
            }
        }

        public override void Think()
        {
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
                if (!Body.IsWithinRadius(ad.Attacker, AggroRange))
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
        /// The interval for thinking, min 1.5 seconds
        /// 10 seconds for 0 aggro mobs
        /// </summary>
        public override int ThinkInterval
        {
            get
            {
                return 500;
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
                                    if (Body.IsWithinRadius(Body.TargetObject, Body.MeleeAttackRange) &&
                                        GameServer.ServerRules.IsAllowedToAttack(Body, target, true) || Body.HealthPercent < 75)
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

        public void CheckPuller()
        {
            if (IsPulling && Body.TargetObject != null && Body.TargetObject.ObjectState == GameObject.eObjectState.Active)
            {
                if (CheckResetPuller())
                {
                    Body.ReturnToSpawnPoint(Body.MaxSpeed);

                    if (MimicBody.CharacterClass.ID != (int)eCharacterClass.Hunter &&
                        MimicBody.CharacterClass.ID != (int)eCharacterClass.Ranger &&
                        MimicBody.CharacterClass.ID != (int)eCharacterClass.Scout &&
                        MimicBody.CharacterClass.ClassType != eClassType.ListCaster)
                    {
                        if (MimicBody.MimicSpec.Is2H)
                            Body.SwitchWeapon(eActiveWeaponSlot.TwoHanded);
                        else
                            Body.SwitchWeapon(eActiveWeaponSlot.Standard);
                    }

                    return;
                }
            }

            if (!Body.InCombat)
            {
                if (CheckDelayPull())
                {
                    Body.StopAttack();
                    Body.StopFollowing();
                }
                else
                {
                    GameLiving pullTarget = GetPullTarget();
                    PerformPull(pullTarget);
                }
            }
        }

        public bool CheckDelayPull()
        {
            if (LastTargetObject != null && LastTargetObject.ObjectState == GameObject.eObjectState.Active)
                return true;

            if (CheckSpells(eCheckSpellType.Defensive) || MimicBody.Sit(CheckStats(75)))
                return true;

            if (Body.Group != null &&
                Body.Group.GetMembersInTheGroup().Any(groupMember => groupMember.IsCasting || groupMember.IsSitting))
                return true;

            return false;
        }

        public GameLiving GetPullTarget()
        {
            if (!Body.IsAttacking && !Body.IsCasting && !Body.IsSitting)
            {
                if (Body.Group.MimicGroup.CCTargets.Count > 0)
                    return Body.Group.MimicGroup.CCTargets[Util.Random(Body.Group.MimicGroup.CCTargets.Count - 1)];

                CheckProximityAggro(3600);

                if (AggroList.Count > 0)
                {
                    GameLiving closestTarget;

                    if (Body.Group.MimicGroup.PullFromPoint != null)
                        closestTarget = AggroList.Where(pair => Body.GetConLevel(pair.Key) >= Body.Group.MimicGroup.ConLevelFilter).
                                                   OrderBy(pair => pair.Key.GetDistance(Body.Group.MimicGroup.PullFromPoint)).
                                                   ThenBy(pair => Body.GetDistanceTo(pair.Key)).First().Key;
                    else
                        closestTarget = AggroList.Where(pair => Body.GetConLevel(pair.Key) > Body.Group.MimicGroup.ConLevelFilter).
                                                   OrderBy(pair => Body.GetDistanceTo(pair.Key)).First().Key;

                    return closestTarget;
                }
            }

            return null;
        }

        private bool CheckResetPuller()
        {
            if (Body.TargetObject is GameNPC npcTarget && npcTarget.Brain is StandardMobBrain mobBrain && mobBrain.HasAggro)
            {
                LastTargetObject = Body.TargetObject;
                IsPulling = false;
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

            IsPulling = true;

            if (Body.Inventory.GetItem(eInventorySlot.DistanceWeapon) != null)
            {
                Body.SwitchWeapon(eActiveWeaponSlot.Distance);
                Body.StartAttack(target);
            }
            else
            {
                //if (Body.CanCastInstantHarmfulSpells)
                //{
                //    foreach(Spell spell in Body.InstantHarmfulSpells)

                //}
                //if (!Body.IsWithinRadius(Body.TargetObject, spell.Range))
                //{
                //    Body.Follow(Body.TargetObject, spell.Range - 100, 5000);
                //    QueuedOffensiveSpell = spell;
                //    return false;
                //}
            }
        }

        #endregion MainPuller

        #region MainLeader

        public bool CheckDelayRoam()
        {
            if (Body.IsCasting || CheckSpells(eCheckSpellType.Defensive) || MimicBody.Sit(CheckStats(75)))
                return true;

            if (Body.Group != null && Body.Group.GetMembersInTheGroup().Any(groupMember =>
                groupMember.IsCasting ||
                groupMember.IsSitting ||
                (groupMember is MimicNPC mimic &&
                !mimic.InCombat &&
                (mimic.MimicBrain.CheckStats(75) ||
                (mimic.MimicBrain.FSM.GetCurrentState() == mimic.MimicBrain.FSM.GetState(eFSMStateType.FOLLOW_THE_LEADER) &&
                !Body.IsWithinRadius(mimic, 1000))))))
            {
                return true;
            }

            return false;
        }

        #endregion MainLeader

        #region MainCC

        public void CheckMainCC()
        {
            if (Body.Group.MimicGroup.CCTargets.Count > 0)
            {
                if (CheckSpells(eCheckSpellType.CrowdControl))
                    return;
            }

            if (!Body.InCombat && Body.Group.MimicGroup.CCTargets.Count > 0)
            {
                Body.Group.MimicGroup.CCTargets = ValidateCCList(Body.Group.MimicGroup.CCTargets);
            }
        }

        // Test for bad lists. Might not be needed.
        private List<GameLiving> ValidateCCList(List<GameLiving> ccList)
        {
            List<GameLiving> validatedList = new List<GameLiving>();

            if (ccList.Count != 0)
            {
                foreach (GameLiving cc in ccList)
                {
                    if (cc is GameNPC npc && npc != null && npc.IsAlive && ((StandardMobBrain)npc.Brain).HasAggro)
                    {
                        validatedList.Add(cc);
                    }
                }
            }

            return validatedList;
        }

        #endregion MainCC

        #region MainTank

        public bool CheckMainTankTarget()
        {
            if (!IsMainTank)
                return false;

            GameLiving target = null;
            List<GameLiving> listOfTargets = null;

            if (AggroList.Count > 0)
            {
                listOfTargets = (AggroList.Keys.Where(key => key.TargetObject is GameLiving livingTarget && livingTarget != Body &&
                                                             !livingTarget.IsMezzed && !livingTarget.IsRooted).ToList());
            }

            if (listOfTargets != null && listOfTargets.Count > 0)
                target = listOfTargets[Util.Random(listOfTargets.Count - 1)];

            if (target != null)
            {
                Body.TargetObject = target;

                return true;
            }

            return false;
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
            }

            public long Base { get; set; }
            public long Effective { get; set; }
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
                return oldValue;
            }
        }

        public virtual void RemoveFromAggroList(GameLiving living)
        {
            AggroList.TryRemove(living, out _);
        }

        public long GetMaxAggro()
        {
            return AggroList.IsEmpty
                ? 0
                : AggroList.OrderByDescending(x => x.Value.Effective).Select(x => (x.Key, x.Value.Effective)).ToList().First().Effective;
        }

        public List<OrderedAggroListElement> GetOrderedAggroList()
        {
            // Potentially slow, so we cache the result.
            lock (_orderedAggroListLock)
            {
                if (OrderedAggroList.Count == 0)
                    OrderedAggroList = AggroList.OrderByDescending(x => x.Value.Effective).Select(x => new OrderedAggroListElement(x.Key, x.Value.Effective)).ToList();

                return OrderedAggroList.ToList();
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

                    // Solo casters flee, grouped casters rely on group to peel
                    if (Body.Group == null)
                    {
                        if (TryFlee())
                            return;

                        if (TryResumeAfterFlee())
                            return;
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

        private bool TryFlee()
        {
            if (TargetFleePosition != null || IsFleeing || !Body.IsBeingInterrupted)
                return false;

            int fleeDistance = 2000 - Body.GetDistance(Body.TargetObject);
            Flee(fleeDistance);

            return true;
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
            if (Body.Group != null && Body.Group.MimicGroup.MainAssist.InCombat)
            {
                GameObject assistTarget = Body.Group.MimicGroup.CurrentTarget;
                GameObject target = null;

                if (assistTarget != null && CanAggroTarget((GameLiving)assistTarget))
                    target = assistTarget;

                return target;
            }

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
            // Clear cached ordered aggro list.
            // It isn't built here because ordering all entities in the aggro list can be expensive, and we typically don't need it.
            // It's built on demand, when `GetOrderedAggroList` is called.
            OrderedAggroList.Clear();

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
        /// </summary>
        protected virtual GameLiving CalculateNextAttackTarget()
        {
            return CleanUpAggroListAndGetHighestModifiedThreat();
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

            // Healers should heal whether in combat or out of it.
            if (CheckHeals())
                return true;

            if (!casted && type == eCheckSpellType.CrowdControl)
            {
                GameLiving ccTarget = null;

                if (PvPMode)
                {
                    if (Body.TargetObject is GameLiving livingTarget && CanAggroTarget(livingTarget)
                        && !livingTarget.IsCrowdControlled)
                        ccTarget = livingTarget;
                }
                else if (MimicBody.CanCastCrowdControlSpells)
                    ccTarget = MimicBody.Group?.MimicGroup.CCTargets[Util.Random(MimicBody.Group.MimicGroup.CCTargets.Count - 1)] as GameLiving;

                if (ccTarget != null && MimicBody.CanCastCrowdControlSpells)
                {
                    Body.TargetObject = ccTarget;

                    foreach (Spell spell in MimicBody.CrowdControlSpells)
                    {
                        if (CanCastOffensiveSpell(spell) && !LivingHasEffect(ccTarget, spell))
                            spellsToCast.Add(spell);
                    }

                    if (spellsToCast.Count > 0)
                    {
                        Spell spell = spellsToCast[Util.Random(spellsToCast.Count - 1)];
                        casted = Body.CastSpell(spell, MimicBody.GetSpellLineForSpell(spell));

                        if (casted)
                        {
                            if (!PvPMode)
                                MimicBody.Group.MimicGroup.CCTargets.Remove(ccTarget);

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
                if (MimicBody.CharacterClass.ID == (int)eCharacterClass.Cleric)
                {
                    if (!Util.Chance(Math.Max(5, Body.ManaPercent - 50)))
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
                if ((MimicBody.CanUsePositionalStyles || MimicBody.CanUseAnytimeStyles) && (Body.IsWithinRadius(Body.TargetObject, 550) || Body.ManaPercent <= 10))
                    return false;

                if (MimicBody.CanCastCrowdControlSpells)
                {
                    int ccChance = 50;

                    GameLiving livingTarget = Body.TargetObject as GameLiving;

                    if (livingTarget?.TargetObject == Body && Body.IsWithinRadius(Body.TargetObject, 500))
                        ccChance = 95;

                    if (Body.Group?.MimicGroup.CurrentTarget == Body.TargetObject)
                        ccChance = 0;

                    if (Util.Chance(ccChance))
                    {
                        foreach (Spell spell in MimicBody.CrowdControlSpells)
                        {
                            if (CanCastOffensiveSpell(spell) && !LivingHasEffect((GameLiving)Body.TargetObject, spell))
                                spellsToCast.Add(spell);
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
                        foreach (Spell spell in Body.HarmfulSpells)
                        {
                            if (spell.SpellType == eSpellType.Charm ||
                                spell.SpellType == eSpellType.Amnesia ||
                                spell.SpellType == eSpellType.Confusion ||
                                spell.SpellType == eSpellType.Taunt)
                                continue;

                            if (CanCastOffensiveSpell(spell))
                                spellsToCast.Add(spell);
                        }
                    }
                }

                if (spellsToCast.Count > 0)
                {
                    Spell spellToCast = spellsToCast[Util.Random(spellsToCast.Count - 1)];

                    if (spellToCast.Uninterruptible || !Body.IsBeingInterrupted)
                        casted = CheckOffensiveSpells(spellToCast);
                    else if (!spellToCast.Uninterruptible && Body.IsBeingInterrupted)
                    {
                        if (MimicBody.CharacterClass.ClassType == eClassType.ListCaster)
                        {
                            Ability quickCast = Body.GetAbility(Abilities.Quickcast);

                            if (quickCast != null)
                            {
                                if (Body.GetSkillDisabledDuration(quickCast) <= 0)
                                {
                                    // Give mimics a small bump in duration, they don't use it as well as humans.
                                    new QuickCastECSGameEffect(new ECSGameEffectInitParams(Body, QuickCastECSGameEffect.DURATION + 1000, 1));
                                    Body.DisableSkill(quickCast, 180000);

                                    casted = CheckOffensiveSpells(spellToCast);
                                }
                            }
                        }
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
                        return true;
                }
            }

            return false;
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

        /// <summary>Have we already checked heals this loop?</summary>
        public bool AlreadyCheckedHeals;
        private long nextCureTime = 0;

        /// <summary>Check for healing and cure spells</summary>
        /// <returns>True if trying to heal, including moving to get into range</returns>
        public bool CheckHeals()
        {
            /* Summary of priorities:
                Below emergency threshold, heal as fast as possible:
                    Instant heal
                    Interrupt casting non healing spells
                    Group heal if doing so heals the group more than single target would
                    Cast our biggest heal
                    Cast our most efficient heal

                Cure Mez
                Cure disease on most injured
                Cure poison on most injured

                Below healing threshold, prioritize efficiency:
                    Let the current spell finish casting before healing        
                    Apply HoT
                    Group heal if doing so is more mana efficient than single target
                    Cast our biggest heal if we're above mana threshold and they've lost enough health to merit it
                    Cast our most efficient heal

            Notes:
                Dedicated healers will heal group members over threshold, and are more likely to use group heals efficiently
                Spread heals are not being considered
                The following spell types will only be cast by a single group member at a time:
                    Instant heal, HoT, health regen, cure mezz, cure disease, cure poison
                Cure disease and poison are on a shared timer so cure spam doesn't stop non-emergency healing,
                    and gives secondary healers a chance to cure
                Local functions are used extensively to minimize unnecessary spell checks and move complexity out of spell selection
            */

            const byte ManaThreshold = 90;
            const long CureDelay = 5000;

            if (AlreadyCheckedHeals || !Body.CanCastHealSpells || Body.IsStunned || Body.IsMezzed || Body.IsSilenced)
                return false;

            AlreadyCheckedHeals = true;

            #region Instant Spell Local Functions

            bool? m_canCastInstantHeal = null;
            bool CanCastInstantHeal() => m_canCastInstantHeal ??= CheckHealSpell(MimicBody.HealInstant);

            bool? m_canCastInstantGroupHeal = null;
            bool CanCastInstantGroupHeal() => m_canCastInstantGroupHeal ??= CheckHealSpell(MimicBody.HealInstantGroup);

            bool? m_canCastInstantHot = null;
            bool CanCastInstantHot() => m_canCastInstantHot ??= CheckHealSpell(MimicBody.HealOverTimeInstant);

            bool? m_canCastInstantGroupHot = null;
            bool CanCastInstantGroupHot() => m_canCastInstantGroupHot ??= CheckHealSpell(MimicBody.HealOverTimeInstantGroup);

            // Instant cure spells are incredibly rare, so it's faster to check if instant before the general spell check
            bool? m_canCastCureDisease = null;
            bool CanCastCureDisease() => m_canCastCureDisease ??= CheckHealSpell(MimicBody.CureDisease) 
                && (!MimicBody.IsBeingSelfInterrupted || MimicBody.CureDisease.IsInstantCast);
            bool CanCastCureDiseaseInstant() => MimicBody.CureDisease != null && MimicBody.CureDisease.IsInstantCast 
                && CanCastCureDisease();

            bool? m_canCastCureDiseaseGroup = null;
            bool CanCastCureDiseaseGroup() => m_canCastCureDiseaseGroup ??= CheckHealSpell(MimicBody.CureDiseaseGroup)
                && (!MimicBody.IsBeingSelfInterrupted || MimicBody.CureDiseaseGroup.IsInstantCast);
            bool CanCastCureDiseaseGroupInstant() => MimicBody.CureDiseaseGroup != null && MimicBody.CureDiseaseGroup.IsInstantCast
                && CanCastCureDiseaseGroup();

            bool? m_canCastCurePoison = null;
            bool CanCastCurePoison() => m_canCastCurePoison ??= CheckHealSpell(MimicBody.CurePoison)
                && (!MimicBody.IsBeingSelfInterrupted || MimicBody.CurePoison.IsInstantCast);
            bool CanCastCurePoisonInstant() => MimicBody.CurePoison != null && MimicBody.CurePoison.IsInstantCast
                && CanCastCurePoison();

            bool? m_canCastCurePoisonGroup = null;
            bool CanCastCurePoisonGroup() => m_canCastCurePoisonGroup ??= CheckHealSpell(MimicBody.CurePoisonGroup)
                && (!MimicBody.IsBeingSelfInterrupted || MimicBody.CurePoisonGroup.IsInstantCast);
            bool CanCastCurePoisonGroupInstant() => MimicBody.CurePoisonGroup != null && MimicBody.CurePoisonGroup.IsInstantCast
                && CanCastCurePoisonGroup();

            bool CanCastInstant() => CanCastInstantHeal() || CanCastInstantGroupHeal() 
                || CanCastInstantHot() || CanCastInstantGroupHot()
                || CanCastCureDiseaseInstant() || CanCastCureDiseaseGroupInstant()
                || CanCastCurePoisonInstant() || CanCastCurePoisonGroupInstant();

            #endregion

            if (MimicBody.IsBeingSelfInterrupted && !CanCastInstant())
                return false;

            bool isCastingHeal = MimicBody.IsCasting && MimicBody.castingComponent.SpellHandler.Spell.IsHealing;

            if (isCastingHeal && !CanCastInstant())
                return true;

            // Working variables
            int amountToHeal;
            int numEmergency = 0;
            int numNeedHealing = 0;
            Spell spellToCast = null;
            GameLiving spellTarget = null;
            GameObject oldTarget;
            bool startedCasting = false;

            #region Local Functions

            bool? m_canCastGroupHeal = null;
            bool CanCastGroupHeal() => m_canCastGroupHeal ??= CheckHealSpell(MimicBody.HealGroup);

            bool? m_canCastBigHeal = null;
            bool CanCastBigHeal() => m_canCastBigHeal ??= CheckHealSpell(MimicBody.HealBig);

            bool? m_canCastEfficientHeal = null;
            bool CanCastEfficientHeal() => m_canCastEfficientHeal ??= CheckHealSpell(MimicBody.HealEfficient);

            bool? m_canCastHot = null;
            bool CanCastHot() => m_canCastHot ??= CheckHealSpell(MimicBody.HealOverTime);

            bool? m_canCastHotGroup = null;
            bool CanCastHotGroup() => m_canCastHotGroup ??= CheckHealSpell(MimicBody.HealOverTimeGroup);

            bool CheckHealSpell(Spell spell, bool checkGroup = true)
            {
                return spell != null
                    && (!MimicBody.IsBeingSelfInterrupted || spell.IsInstantCast)
                    && (!spell.HasRecastDelay || MimicBody.GetSkillDisabledDuration(spell) <= 0)
                    && MimicBody.Mana >= MimicBody.PowerCost(spell);
            }

            double m_groupHealVal = double.MinValue;
            double GetGroupHealVal()
            {
                if (m_groupHealVal < 0)
                {
                    m_groupHealVal = MimicBody.HealGroup.Value >= 0
                        ? numNeedHealing * MimicBody.HealGroup.Value
                        : amountToHeal * MimicBody.HealGroup.Value * -0.01d;
                }
                return m_groupHealVal;
            }

            double m_effectHoT = double.MinValue;
            double m_effectRegen = double.MinValue;
            double GetHotEffect(Spell spell)
            {
                switch (spell.SpellType)
                {
                    case eSpellType.HealOverTime:
                        if (m_effectHoT < 0d)
                        {
                            List<ECSGameEffect> effects = spellTarget.effectListComponent.GetEffects(eEffect.HealOverTime);

                            if (effects != null)
                            {
                                foreach (ECSGameEffect effect in effects)
                                    if (effect is ECSGameSpellEffect)
                                    {
                                        double newHoT = MimicNPC.HealAmount(effect.SpellHandler.Spell, spellTarget);
                                        if (newHoT > m_effectHoT)
                                            m_effectHoT = newHoT;
                                    }
                            }
                            else
                                m_effectHoT = 0d;
                        }
                        return m_effectHoT;
                    case eSpellType.HealthRegenBuff:
                        if (m_effectRegen < 0d)
                        {
                            List<ECSGameEffect> effects = spellTarget.effectListComponent.GetEffects(eEffect.HealthRegenBuff);

                            if (effects != null)
                            {
                                foreach (ECSGameEffect effect in effects)
                                    if (effect is ECSGameSpellEffect)
                                    {
                                        double newRegen = MimicNPC.HealAmount(effect.SpellHandler.Spell, spellTarget);
                                        if (newRegen > m_effectRegen)
                                            m_effectRegen = newRegen;
                                    }
                            }
                            else
                                m_effectRegen = 0d;
                        }
                        return m_effectRegen;
                }

                return 0d;
            }

            #endregion

            MimicGroup mGroup = MimicBody.Group?.MimicGroup;

            lock (mGroup?.HealLock ?? new object())
            {
                #region Check Health

                if (mGroup == null)
                {
                    amountToHeal = MimicBody.MaxHealth - MimicBody.Health;

                    if (amountToHeal > 0)
                    {
                        spellTarget = MimicBody;

                        if (MimicBody.HealthPercent < MimicGroup.HealThreshold)
                        {
                            numNeedHealing = 1;

                            if (MimicBody.HealthPercent < MimicGroup.EmergencyThreshold)
                                numEmergency = 1;
                        }
                    }
                }
                else
                {
                    mGroup.CheckGroupHealth(MimicBody);

                    amountToHeal = mGroup.AmountToHeal;
                    numEmergency = mGroup.NumNeedEmergencyHealing;
                    numNeedHealing = IsHealer 
                        ? mGroup.NumInjured 
                        : mGroup.NumNeedHealing;
                    spellTarget = mGroup.MemberToHeal;

                    if (mGroup.AlreadyCastInstantHeal)
                        m_canCastInstantHeal = m_canCastInstantGroupHeal = false;

                    if (mGroup.AlreadyCastingHoT)
                    {
                        if (MimicBody.HealOverTimeInstant == null || MimicBody.HealOverTimeInstant.SpellType == eSpellType.HealOverTime)
                            m_canCastInstantHot = false;
                        if (MimicBody.HealOverTimeInstantGroup == null || MimicBody.HealOverTimeInstantGroup.SpellType == eSpellType.HealOverTime)
                            m_canCastInstantGroupHot = false;
                        if (MimicBody.HealOverTime == null || MimicBody.HealOverTime.SpellType == eSpellType.HealOverTime)
                            m_canCastHot = false;
                        if (MimicBody.HealOverTimeGroup == null || MimicBody.HealOverTimeGroup.SpellType == eSpellType.HealOverTime)
                            m_canCastHotGroup = false;
                    }

                    if (mGroup.AlreadyCastingRegen)
                    {
                        if (MimicBody.HealOverTimeInstant == null || MimicBody.HealOverTimeInstant.SpellType == eSpellType.HealthRegenBuff)
                            m_canCastInstantHot = false;
                        if (MimicBody.HealOverTimeInstantGroup == null || MimicBody.HealOverTimeInstantGroup.SpellType == eSpellType.HealthRegenBuff)
                            m_canCastInstantGroupHot = false;
                        if (MimicBody.HealOverTime == null || MimicBody.HealOverTime.SpellType == eSpellType.HealthRegenBuff)
                            m_canCastHot = false;
                        if (MimicBody.HealOverTimeGroup == null || MimicBody.HealOverTimeGroup.SpellType == eSpellType.HealthRegenBuff)
                            m_canCastHotGroup = false;
                    }

                    if (mGroup.AlreadyCastingCureDisease)
                        m_canCastCureDisease = m_canCastCureDiseaseGroup = false;

                    if (mGroup.AlreadyCastingCurePoison)
                        m_canCastCurePoison = m_canCastCurePoisonGroup = false;
                }

                #endregion
 
                #region Emergency Heal

                if (numEmergency > 0)
                {
                    if (numEmergency > 1)
                    {
                        if (CanCastInstantGroupHeal())
                            spellToCast = MimicBody.HealInstantGroup;
                        else if (CanCastInstantHeal())
                            spellToCast = MimicBody.HealInstant;
                        else if (!isCastingHeal && CanCastGroupHeal())
                        {
                            if (MimicNPC.HealAmount(MimicBody.HealBig, spellTarget) > GetGroupHealVal() && CanCastBigHeal())
                                spellToCast = MimicBody.HealBig;
                            else if (MimicNPC.HealAmount(MimicBody.HealEfficient, spellTarget) > GetGroupHealVal() && CanCastEfficientHeal())
                                spellToCast = MimicBody.HealEfficient;
                            else
                                spellToCast = MimicBody.HealGroup;
                        }
                    }

                    if (spellToCast == null)
                    {
                        if (CanCastInstantHeal())
                            spellToCast = MimicBody.HealInstant;
                        else if (CanCastInstantGroupHeal())
                            spellToCast = MimicBody.HealInstantGroup;
                        else if (!isCastingHeal)
                        {
                            if (CanCastBigHeal())
                                spellToCast = MimicBody.HealBig;
                            else if (CanCastEfficientHeal())
                                spellToCast = MimicBody.HealEfficient;
                        }
                    }
                }

                #endregion
 
                #region Cure Mess/Disease/Poison

                if (spellToCast == null)
                {
                    if (mGroup != null && mGroup.MemberToCureMezz != null && !mGroup.AlreadyCastingCureMezz
                        && !MimicBody.IsCasting && CheckHealSpell(MimicBody.CureMezz))
                    {
                        spellToCast = MimicBody.CureMezz;
                        spellTarget = mGroup.MemberToCureMezz;
                    }
                    else if (mGroup == null)
                    {
                        if (MimicBody.IsDiseased && nextCureTime < GameLoop.GameLoopTime)
                        {
                            if (CanCastCureDisease() && (!MimicBody.IsCasting || CanCastCureDiseaseInstant()))
                            {
                                spellToCast = MimicBody.CureDisease;
                                spellTarget = MimicBody;
                            }
                            else if (CanCastCureDiseaseGroup() && (!MimicBody.IsCasting) || CanCastCureDiseaseGroupInstant())
                            {
                                spellToCast = MimicBody.CureDiseaseGroup;
                                spellTarget = MimicBody;
                            }
                        }
                        else if (MimicBody.IsPoisoned && nextCureTime < GameLoop.GameLoopTime)
                        {
                            if (CanCastCurePoison() && (!MimicBody.IsCasting || CanCastCurePoisonInstant()))
                            {
                                spellToCast = MimicBody.CurePoison;
                                spellTarget = MimicBody;
                            }
                            else if (CanCastCurePoisonGroup() && (!MimicBody.IsCasting || CanCastCurePoisonGroupInstant()))
                            {
                                spellToCast = MimicBody.CurePoisonGroup;
                                spellTarget = MimicBody;
                            }
                        }
                    }
                    else
                    {
                        if (mGroup.MemberToCureDisease != null && nextCureTime < GameLoop.GameLoopTime)
                        {
                            if (CanCastCureDiseaseGroup()
                                && (mGroup.NumNeedCureDisease > 1 || !CanCastCureDisease())
                                && (!MimicBody.IsCasting || CanCastCureDiseaseGroupInstant()))
                            {
                                spellToCast = MimicBody.CureDiseaseGroup;
                                spellTarget = mGroup.MemberToCureDisease;
                            }
                            else if (CanCastCureDisease()
                                && (!MimicBody.IsCasting || CanCastCureDiseaseInstant()))
                            {
                                spellToCast = MimicBody.CureDisease;
                                spellTarget = mGroup.MemberToCureDisease;
                            }
                        }
                        else if (mGroup.MemberToCurePoison != null && nextCureTime < GameLoop.GameLoopTime)
                        {
                            if (CanCastCurePoisonGroup()
                                && (mGroup.NumNeedCurePoison > 1 || !CanCastCurePoison())
                                && (!MimicBody.IsCasting || CanCastCurePoisonGroupInstant()))
                            {
                                spellToCast = MimicBody.CurePoisonGroup;
                                spellTarget = mGroup.MemberToCurePoison;
                            }
                            else if (CanCastCurePoison()
                                && (!MimicBody.IsCasting || CanCastCurePoisonInstant()))
                            {
                                spellToCast = MimicBody.CurePoison;
                                spellTarget = mGroup.MemberToCurePoison;
                            }
                        }
                    }
                }

                #endregion
 
                #region Non-Emergency Heal

                if (spellToCast == null && numNeedHealing > 0)
                {
                    if (numNeedHealing > 1)
                    {
                        // Instant HoTs usually have low cooldowns, so spam them whenever possible
                        if (CanCastInstantGroupHot()
                            && MimicNPC.HealAmount(MimicBody.HealOverTimeInstantGroup, spellTarget) > GetHotEffect(MimicBody.HealOverTimeInstantGroup))
                                spellToCast = MimicBody.HealOverTimeInstantGroup;
                        else if (!MimicBody.IsCasting || (numEmergency > 0 && !isCastingHeal))
                        {
                            if (CanCastHotGroup()
                                && MimicNPC.HealAmount(MimicBody.HealOverTimeGroup, spellTarget) > GetHotEffect(MimicBody.HealOverTimeGroup))
                                    spellToCast = MimicBody.HealOverTimeGroup;
                            else if (CanCastGroupHeal())
                            {
                                if (!CanCastEfficientHeal()
                                    || (GetGroupHealVal() / MimicBody.PowerCost(MimicBody.HealGroup))
                                    > (MimicNPC.HealAmount(MimicBody.HealEfficient, spellTarget) / MimicBody.PowerCost(MimicBody.HealEfficient)))
                                        spellToCast = MimicBody.HealGroup;
                            }
                        }
                    }

                    if (spellToCast == null)
                    {
                        if (CanCastInstantHot()
                            && MimicNPC.HealAmount(MimicBody.HealOverTimeInstant, spellTarget) > GetHotEffect(MimicBody.HealOverTimeInstant))
                                spellToCast = MimicBody.HealOverTimeInstant;
                        else if (CanCastInstantGroupHot()
                            && MimicNPC.HealAmount(MimicBody.HealOverTimeInstantGroup, spellTarget) > GetHotEffect(MimicBody.HealOverTimeInstantGroup))
                                spellToCast = MimicBody.HealOverTimeInstantGroup;
                        else if (!MimicBody.IsCasting || (numEmergency > 0 && !isCastingHeal))
                        {
                            if (CanCastHot()
                                && MimicNPC.HealAmount(MimicBody.HealOverTime, spellTarget) > GetHotEffect(MimicBody.HealOverTime))
                                    spellToCast = MimicBody.HealOverTime;
                            else if (CanCastHotGroup()
                                && MimicNPC.HealAmount(MimicBody.HealOverTimeGroup, spellTarget) > GetHotEffect(MimicBody.HealOverTimeGroup))
                                    spellToCast = MimicBody.HealOverTimeGroup;
                            else if (MimicBody.ManaPercent >= ManaThreshold && CanCastBigHeal()
                                && (spellTarget.MaxHealth - spellTarget.Health) >= MimicNPC.HealAmount(MimicBody.HealBig, spellTarget))
                                    spellToCast = MimicBody.HealBig;
                            else if (CanCastEfficientHeal())
                                spellToCast = MimicBody.HealEfficient;
                            else if (CanCastGroupHeal())
                                // We don't have a single target heal, but we might have a CL group heal
                                spellToCast = MimicBody.HealGroup;
                        }
                    }
                }

                #endregion
 
                #region Cast Spell

                if (spellToCast != null)
                {
                    if (!MimicBody.IsWithinRadius(spellTarget, spellToCast.CalculateEffectiveRange(spellTarget)))
                    {
                        MimicBody.PathTo(new Point3D(spellTarget.X, spellTarget.Y, spellTarget.Z), MimicBody.MaxSpeed);
                        return true;
                    }

                    if (!spellToCast.IsInstantCast)
                    {
                        if (MimicBody.IsCasting)
                            MimicBody.StopCurrentSpellcast();
                        else if (MimicBody.IsAttacking)
                            MimicBody.StopAttack();
                    }

                    oldTarget = MimicBody.TargetObject;
                    MimicBody.TargetObject = spellTarget;
                    startedCasting = MimicBody.CastSpell(spellToCast, MimicBody.GetSpellLineForSpell(spellToCast), false);

                    if (!startedCasting)
                        MimicBody.TargetObject = oldTarget;
                    else
                    {
                        if (spellToCast.IsInstantCast)
                        {
                            MimicBody.TargetObject = oldTarget;
                            startedCasting = false;
                        }
                        else if (spellToCast.SpellType == eSpellType.CureDisease || spellToCast.SpellType == eSpellType.CurePoison)
                            nextCureTime = GameLoop.GameLoopTime + CureDelay;

                        if (mGroup != null)
                            switch (spellToCast.SpellType)
                            {
                                case eSpellType.Heal:
                                    if (spellToCast.IsInstantCast)
                                        mGroup.AlreadyCastInstantHeal = true;
                                    break;
                                case eSpellType.HealOverTime: mGroup.AlreadyCastingHoT = true; break;
                                case eSpellType.HealthRegenBuff: mGroup.AlreadyCastingRegen = true; break;
                                case eSpellType.CureMezz: mGroup.AlreadyCastingCureMezz = true; break;
                                case eSpellType.CureDisease: mGroup.AlreadyCastingCureDisease = true; break;
                                case eSpellType.CurePoison: mGroup.AlreadyCastingCurePoison = true; break;
                            }
                    }
                }
            } // lock

            #endregion

            return startedCasting || isCastingHeal;
        }

        bool CheckDefensiveSpells(List<Spell> spells)
        {
            // Contrary to offensive spells, we don't start with a valid target.
            // So the idea here is to find a target, switch before calling `CastDefensiveSpell`, then retrieve our previous target.
            List<(Spell, GameLiving)> spellsToCast = new(spells.Count);

            foreach (Spell spell in spells)
            {
                if (CanCastDefensiveSpell(spell, out GameLiving target))
                    spellsToCast.Add((spell, target));
            }

            if (spellsToCast.Count == 0)
                return false;

            GameObject oldTarget = Body.TargetObject;
            (Spell spell, GameLiving target) spellToCast = spellsToCast[0];
            Body.TargetObject = spellToCast.target;
            bool cast = Body.CastSpell(spellToCast.spell, MimicBody.GetSpellLineForSpell(spellToCast.spell));

            if (Debug)
            {
                if (cast)
                    log.Info(Body.Name + " tried to cast " + spellToCast.spell.Name + " on " + spellToCast.target.Name + " and cast == true");
                else
                    log.Info(Body.Name + " tried to cast " + spellToCast.spell.Name + " on " + spellToCast.target.Name + " and cast == false");

                if (LivingHasEffect(spellToCast.target, spellToCast.spell))
                    log.Info(spellToCast.target.Name + " has the effect already.");
            }

            Body.TargetObject = oldTarget;
            return cast;

            bool CanCastDefensiveSpell(Spell spell, out GameLiving target)
            {
                target = null;

                // TODO: Handle instrument spells
                if (spell.NeedInstrument || (!spell.Uninterruptible && Body.IsBeingInterrupted) ||
                    (spell.HasRecastDelay && Body.GetSkillDisabledDuration(spell) > 0))
                {
                    return false;
                }

                target = FindTargetForDefensiveSpell(spell);
                return target != null;
            }
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
                    if (Body.Group != null)
                    {
                        foreach (GameLiving groupMember in Body.Group.GetMembersInTheGroup())
                        {
                            if (!groupMember.IsAlive)
                            {
                                Body.TargetObject = groupMember;
                                break;
                            }
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

                if (spell.IsPBAoE && !Body.IsWithinRadius(Body.TargetObject, spell.Radius))
                    break;

                // Try to limit the debuffs cast to save mana and time spent doing so.
                if (MimicBody.CharacterClass.ClassType == eClassType.ListCaster)
                {
                    if (!Util.Chance(Math.Max(5, Body.ManaPercent - 75)))
                        break;
                }

                if (!LivingHasEffect((GameLiving)Body.TargetObject, spell) && Body.IsWithinRadius(Body.TargetObject, spell.Range))
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
using DOL.GS.PacketHandler;
using DOL.GS.ServerProperties;
using DOL.GS.Spells;
using DOL.Language;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace DOL.GS.Scripts
{
    public class MimicGroup
    {
        /// <summary>
        /// Broadcasts a localized message to every group member in their own language,
        /// formatted as "[Party] FromName: \"text\"".
        /// </summary>
        private static void SayToGroup(GameLiving from, string translationId)
        {
            if (from?.Group == null)
                return;

            string fromName = from.GetName(0, true);
            foreach (GamePlayer player in from.Group.GetPlayersInTheGroup())
            {
                string lang = player.Client?.Account?.Language;
                string text = LanguageMgr.GetTranslation(lang, translationId);
                player.Out.SendMessage($"[Party] {fromName}: \"{text}\"", eChatType.CT_Group, eChatLoc.CL_ChatWindow);
            }
        }
        public GameLiving MainLeader { get; private set; }
        public GameLiving MainAssist { get; private set; }
        public GameLiving MainTank { get; private set; }
        public GameLiving MainCC { get; private set; }
        public GameLiving MainPuller { get; private set; }
        public GameLiving Healer { get; private set; }
        public Point3D CampPoint { get; private set; }
        public Point2D PullFromPoint {get; private set; }

        public List<GameLiving> CCTargets { get; set; }

        // Default minimum con-level to pull: -1 (blue). The puller skips grey
        // (-3) and green (-2) by default so chain-pulling doesn't burn cycles
        // on no-xp / trivial mobs. Players can relax it via /mcamp.
        public int ConLevelFilter = -1;

        #region Camp Phase State Machine

        /// <summary>
        /// Tracks the high-level group activity inside a camp session so each bot
        /// can pick the correct sub-behaviour (regen, intercept, focus DPS, etc.)
        /// without each Think() having to re-derive it from scratch.
        /// </summary>
        public enum eCampPhase
        {
            /// <summary>No active camp / inactive. CampPoint is null.</summary>
            Inactive,
            /// <summary>Group is sitting, regening, buffing. Puller idle.</summary>
            Regen,
            /// <summary>Group is at full HP/mana, buffs up — ready for next pull.</summary>
            Ready,
            /// <summary>Puller is in flight bringing a mob.</summary>
            Pulling,
            /// <summary>Mob is incoming / first contact; tank should intercept, CC pre-mez adds.</summary>
            Engaging,
            /// <summary>Active combat on a brought-in mob/pack.</summary>
            Combat,
            /// <summary>Combat just ended; group recovers vitals, sits, before next pull.</summary>
            PostCombat,
        }

        public eCampPhase CampPhase { get; private set; } = eCampPhase.Inactive;

        /// <summary>
        /// Last GameLoopTime (ms) the camp phase changed. Used by camp-state
        /// timers (e.g. "have we been in Pulling for >12s without making
        /// contact? → recover the puller").
        /// </summary>
        public long CampPhaseSinceTick { get; private set; }

        /// <summary>
        /// The mob the puller currently has on its bow/spell, set when the
        /// pull is initiated. Cleared once the mob lands or the chain ends.
        /// Lets the rest of the camp anticipate the incoming target.
        /// </summary>
        public GameLiving IncomingPullTarget { get; set; }

        /// <summary>
        /// Updates the phase, recording the timestamp of the change. Idempotent
        /// when called with the current phase. Public so the brain / commands
        /// can drive transitions.
        /// </summary>
        public void SetCampPhase(eCampPhase phase)
        {
            if (CampPhase == phase)
                return;

            CampPhase = phase;
            CampPhaseSinceTick = GameLoop.GameLoopTime;

            if (phase == eCampPhase.Inactive || phase == eCampPhase.Regen)
                IncomingPullTarget = null;
        }

        /// <summary>
        /// True if the supplied bot is the squishy "highest vulnerability"
        /// member the tank should guard at camp. Returns the chosen target so
        /// the tank doesn't have to walk every member each tick.
        /// </summary>
        public GameLiving PickGuardTarget(GameLiving tank)
        {
            if (tank == null || tank.Group == null)
                return null;

            // Healer first; then any caster; then assist.
            GameLiving best = null;
            int bestScore = int.MaxValue;
            foreach (GameLiving gl in tank.Group.GetMembersInTheGroup())
            {
                if (gl == null || gl == tank || !gl.IsAlive)
                    continue;
                int s;
                if (gl is MimicNPC m && m.MimicBrain != null && m.MimicBrain.IsHealer)
                    s = 0;
                else if (gl is GamePlayer gp && (gp.CharacterClass.ClassType == eClassType.ListCaster))
                    s = 1;
                else if (gl is MimicNPC mc && mc.CharacterClass.ClassType == eClassType.ListCaster)
                    s = 1;
                else
                    s = 2;

                if (s < bestScore)
                {
                    bestScore = s;
                    best = gl;
                }
            }
            return best;
        }

        #endregion

        public GameObject CurrentTarget
        {
            get { return MainAssist?.TargetObject; }
        }

        public MimicGroup(GameLiving leader) 
        {
            MainLeader = leader;
            MainAssist = leader;
            MainTank = leader;
            MainCC = leader;
            MainPuller = leader;
            CCTargets = new List<GameLiving>();
        }

        public bool SetLeader(GameLiving living)
        {
            if (living == null)
                return false;

            MainLeader = living;
            SayToGroup(living, "Mimic.Group.LeaderSet");
            return true;
        }

        public bool SetMainAssist(GameLiving living)
        {
            if (living == null)
                return false;

            MainAssist = living;
            SayToGroup(living, "Mimic.Group.AssistSet");
            return true;
        }

        public bool SetMainTank(GameLiving living)
        {
            if (living == null)
                return false;

            MainTank = living;
            SayToGroup(living, "Mimic.Group.TankSet");
            return true;
        }

        public bool SetMainCC(GameLiving living)
        {
            if (living == null)
                return false;

            MainCC = living;
            SayToGroup(living, "Mimic.Group.CCSet");
            return true;
        }

        /// <summary>
        /// True if the living can act as puller: it must have either a distance
        /// weapon (archer-style pull) or harmful spells (caster-style pull).
        /// </summary>
        public static bool CanPull(GameLiving living)
        {
            if (living == null)
                return false;

            if (living.Inventory?.GetItem(eInventorySlot.DistanceWeapon) != null)
                return true;

            if (living is MimicNPC mimic && (mimic.CanCastHarmfulSpells || mimic.CanCastInstantHarmfulSpells))
                return true;

            return false;
        }

        /// <summary>
        /// Assigns <paramref name="living"/> as the group's puller. Idempotent
        /// — re-assigning the current puller is a no-op (no toggle, no clear).
        /// Use <see cref="ClearMainPuller"/> to explicitly remove the role.
        /// Returns false if the candidate can't pull at all.
        /// </summary>
        public bool SetMainPuller(GameLiving living)
        {
            if (!CanPull(living))
                return false;

            if (MainPuller == living)
                return true; // already set — don't toggle, don't spam chat

            MainPuller = living;
            SayToGroup(living, "Mimic.Group.PullerSet");
            return true;
        }

        /// <summary>
        /// Bypasses <see cref="CanPull"/> to install a body-pull puller —
        /// i.e. a pure-melee bot with no bow and no harmful spells. The
        /// brain's PerformPull handles the melee body-pull fallback so the
        /// role is still useful: run to target, hit, run back. Announces
        /// the change once like a normal Set.
        /// </summary>
        public void ForceSetMainPullerForBodyPull(GameLiving living)
        {
            if (living == null || MainPuller == living)
                return;

            MainPuller = living;
            SayToGroup(living, "Mimic.Group.PullerSet");
        }

        /// <summary>
        /// Clears the puller role explicitly. Falls back to the group leader
        /// so the role is never left "null" (legacy code paths assume non-null
        /// MainPuller). Announces the change once.
        /// </summary>
        public void ClearMainPuller()
        {
            if (MainPuller == null || MainPuller == MainLeader)
            {
                MainPuller = MainLeader;
                return;
            }

            GameLiving previous = MainPuller;
            MainPuller = MainLeader;
            SayToGroup(previous, "Mimic.Group.PullerUnset");
        }

        public bool SetHealer(GameLiving living)
        {
            if (living == null)
                return false;

            if (living is MimicNPC mimic)
            {
                if (!mimic.CanCastHealSpells && !mimic.CanCastInstantHealSpells)
                    SayToGroup(mimic, "Mimic.Group.CannotCastHeal");
                else
                {
                    mimic.MimicBrain.IsHealer = !mimic.MimicBrain.IsHealer;

                    if (mimic.MimicBrain.IsHealer)
                        SayToGroup(mimic, "Mimic.Group.HealerOn");
                    else
                        SayToGroup(mimic, "Mimic.Group.HealerOff");
                }
            }
            else
                return false;

            return true;
        }

        public void SetCampPoint(Point3D point)
        {
            if (point != null)
            {
                CampPoint = new Point3D(point);
                SetCampPhase(eCampPhase.Regen);
            }
            else
            {
                CampPoint = null;
                SetCampPhase(eCampPhase.Inactive);
            }
        }

        public void SetPullPoint(Point2D point)
        {
            if (point != null)
                PullFromPoint = new Point2D(point);
            else
                PullFromPoint = null;
        }

        #region Healing

        /// <summary>Lock before accessing CheckGroupHealth() or related members</summary>
        public object HealLock = new();
        /// <summary>How injured is the group as a whole?</summary>
        public int AmountToHeal { get; private set; }
        /// <summary>How many group members are below emergency threshold</summary>
        public int NumNeedEmergencyHealing { get; private set; }
        /// <summary>How many group members are below healing threshold</summary>
        public int NumNeedHealing { get; private set; }
        /// <summary>How many group members are below max health</summary>
        public int NumInjured { get; private set; }
        /// <summary>Most injured group member</summary>
        public GameLiving MemberToHeal { get; private set; }
        /// <summary>Mezzed group member</summary>
        public GameLiving MemberToCureMezz { get; private set; }
        /// <summary>How many group members are diseased?</summary>
        public int NumNeedCureDisease { get; private set; }
        /// <summary>Most injured diseased group member</summary>
        public GameLiving MemberToCureDisease { get; private set; }
        /// <summary>How many group members are poisoned?</summary>
        public int NumNeedCurePoison { get; private set; }  
        /// <summary>Most injured poisoned group member</summary>
        public GameLiving MemberToCurePoison { get; private set; }
        /// <summary>Is a group member already casting an instant heal spell?</summary>
        public bool AlreadyCastInstantHeal;
        /// <summary>Is a group member already casting a heal over time spell?  Set in MimicBrain.CheckHeals()</summary>
        public bool AlreadyCastingHoT;
        /// <summary>Is a group member already casting a health regen spell?</summary>
        public bool AlreadyCastingRegen;
        /// <summary>Is a group member already casting a cure mezz spell?</summary>
        public bool AlreadyCastingCureMezz;
        /// <summary>Is a group member already casting a cure disease spell?</summary>
        public bool AlreadyCastingCureDisease;
        /// <summary>Is a group member already casting a cure poison spell?</summary>
        public bool AlreadyCastingCurePoison;

        private int m_healthPercent;
        private int m_diseasePercent;
        private int m_poisonPercent;
        private int m_percentCurrent;

        // Heal thresholds — more aggressive than the generic NPC defaults so mimic
        // healers stay proactive. Default 85 / 50 (vs the global NPC_HEAL_THRESHOLD
        // of 75 / 37). Tunable via ServerProperty mimic_heal_threshold.
        public static int HealThreshold = MimicConfig.MIMIC_HEAL_THRESHOLD > 0
            ? MimicConfig.MIMIC_HEAL_THRESHOLD
            : 85;
        public static int EmergencyThreshold = MimicConfig.MIMIC_EMERGENCY_THRESHOLD > 0
            ? MimicConfig.MIMIC_EMERGENCY_THRESHOLD
            : 50;

        private long nextCheckTime = 0;
        const long checkTimeOffset = 51; // Think() can be called slightly before interval

        /// <summary>Retrieve health and mezz/disease/poison status for the group</summary>
        /// <param name="checker">Healer checking group status</param>
        public void CheckGroupHealth(MimicNPC checker)
        {
            // The checker may have been kicked from the group between the
            // last Think tick and the heal scan; bail rather than NRE on
            // the foreach below.
            if (checker?.Group == null)
                return;

            if (nextCheckTime < GameLoop.GameLoopTime)
            {
                nextCheckTime = GameLoop.GameLoopTime + checker.Brain.ThinkInterval - checkTimeOffset;

                AmountToHeal = 0;
                NumNeedEmergencyHealing = 0;
                NumNeedHealing = 0;
                NumInjured = 0;
                MemberToHeal = null;
                MemberToCureMezz = null;
                NumNeedCureDisease = 0;
                MemberToCureDisease = null;
                NumNeedCurePoison = 0;
                MemberToCurePoison = null;
                AlreadyCastInstantHeal = false;
                AlreadyCastingHoT = false;
                AlreadyCastingRegen = false;
                AlreadyCastingCureMezz = false;
                AlreadyCastingCureDisease = false;
                AlreadyCastingCurePoison = false;

                m_healthPercent = 100;
                m_diseasePercent = 100;
                m_poisonPercent = 100;

                foreach (GameLiving groupMember in checker.Group.GetMembersInTheGroup())
                {
                    if (groupMember != checker && !groupMember.IsWithinRadius(checker, WorldMgr.VISIBILITY_DISTANCE))
                    // We can only reuse results if everybody is in the same region and reasonably close together
                        nextCheckTime = 0;
                    else
                    {
                        m_percentCurrent = groupMember.HealthPercent;

                        if (m_percentCurrent < 100)
                        {
                            if (m_percentCurrent < EmergencyThreshold)
                                NumNeedEmergencyHealing++;
                            else if (m_percentCurrent < HealThreshold)
                                NumNeedHealing++;
                            else
                                NumInjured++;

                            AmountToHeal += groupMember.MaxHealth - groupMember.Health;
                        }

                        if (m_percentCurrent < m_healthPercent)
                        {
                            m_healthPercent = m_percentCurrent;
                            MemberToHeal = groupMember;
                        }

                        if (groupMember.IsMezzed)
                            MemberToCureMezz = groupMember;

                        if (groupMember.IsDiseased)
                        {
                            NumNeedCureDisease++;
                            if (MemberToCureDisease == null || m_percentCurrent < m_diseasePercent)
                            {
                                MemberToCureDisease = groupMember;
                                m_diseasePercent = m_percentCurrent;
                            }
                        }

                        if (groupMember.IsPoisoned)
                        {
                            NumNeedCurePoison++;
                            if (MemberToCurePoison == null || m_percentCurrent < m_poisonPercent)
                            {
                                MemberToCurePoison = groupMember;
                                m_poisonPercent = m_percentCurrent;
                            }
                        }

                        // Race-safety: IsCasting can be true while CurrentSpellHandler
                        // (or its Spell) has just been nulled out by another thread —
                        // snapshot the reference and validate before reading SpellType.
                        if (groupMember.IsCasting)
                        {
                            ISpellHandler handler = groupMember.CurrentSpellHandler;

                            if (handler?.Spell != null)
                            {
                                switch (handler.Spell.SpellType)
                                {
                                    case eSpellType.HealOverTime: AlreadyCastingHoT = true; break;
                                    case eSpellType.HealthRegenBuff: AlreadyCastingRegen = true; break;
                                    case eSpellType.CureMezz: AlreadyCastingCureMezz = true; break;
                                    case eSpellType.CureDisease: AlreadyCastingCureDisease = true; break;
                                    case eSpellType.CurePoison: AlreadyCastingCurePoison = true; break;
                                }
                            }
                        }
                    }
                }

                NumNeedHealing += NumNeedEmergencyHealing;
                NumInjured += NumNeedHealing;

                // Priority override: when the MainTank is below the heal threshold,
                // always target the tank first regardless of who is the most injured.
                // Keeping the tank up is more valuable for group survival than topping
                // a slightly more wounded DPS.
                if (MainTank != null && MainTank.IsAlive && MainTank.HealthPercent < HealThreshold)
                    MemberToHeal = MainTank;
            }
        }

        #endregion
    }
}

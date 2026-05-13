using DOL.GS.PacketHandler;
using DOL.GS.ServerProperties;
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

        public int ConLevelFilter = -2;

        public GameObject CurrentTarget
        {
            get { return MainAssist.TargetObject; }
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

        public bool SetMainPuller(GameLiving living)
        {
            if (!CanPull(living))
                return false;

            if (MainPuller == living)
            {
                MainPuller = MainLeader;
                SayToGroup(living, "Mimic.Group.PullerUnset");
            }
            else
            {
                MainPuller = living;
                SayToGroup(living, "Mimic.Group.PullerSet");
            }

            return true;
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
                CampPoint = new Point3D(point);
            else
                CampPoint = null;
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

        static readonly public int HealThreshold = Properties.NPC_HEAL_THRESHOLD;
        static readonly public int EmergencyThreshold = HealThreshold / 2;

        private long nextCheckTime = 0;
        const long checkTimeOffset = 51; // Think() can be called slightly before interval

        /// <summary>Retrieve health and mezz/disease/poison status for the group</summary>
        /// <param name="checker">Healer checking group status</param>
        public void CheckGroupHealth(MimicNPC checker)
        {
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

                        if (groupMember.IsMezzed && groupMember != null)
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

                        if (groupMember.IsCasting)
                            switch (groupMember.CurrentSpellHandler.Spell.SpellType)
                            {
                                case eSpellType.HealOverTime: AlreadyCastingHoT = true; break;
                                case eSpellType.HealthRegenBuff: AlreadyCastingRegen = true; break;
                                case eSpellType.CureMezz: AlreadyCastingCureMezz = true; break;
                                case eSpellType.CureDisease: AlreadyCastingCureDisease = true; break;
                                case eSpellType.CurePoison: AlreadyCastingCurePoison = true; break;
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

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

        // Thread-safe CC target list. Multiple bot brains tick on different
        // threads, and the previous raw List<GameLiving> was mutated without
        // any synchronisation (see TESTING.md). Callers now go through the
        // helpers below (AddCcTarget / RemoveCcTarget / GetCcTargetsSnapshot /
        // ClearCcTargets / CcTargetCount / ContainsCcTarget), which take the
        // dedicated lock. The public CCTargets property remains as a snapshot
        // accessor for legacy enumeration; never mutate the returned list.
        private readonly List<GameLiving> _ccTargets = new();
        private readonly Lock _ccTargetsLock = new();

        /// <summary>
        /// Snapshot of the current CC targets. Safe to enumerate / index but
        /// MUTATION on the returned list has no effect on the group state —
        /// use AddCcTarget / RemoveCcTarget / ClearCcTargets instead.
        /// </summary>
        public IReadOnlyList<GameLiving> CCTargets
        {
            get
            {
                lock (_ccTargetsLock)
                    return _ccTargets.Count == 0 ? System.Array.Empty<GameLiving>() : _ccTargets.ToArray();
            }
        }

        /// <summary>Number of currently tracked CC targets.</summary>
        public int CcTargetCount
        {
            get
            {
                lock (_ccTargetsLock)
                    return _ccTargets.Count;
            }
        }

        /// <summary>Adds a CC target idempotently. Returns true when it was newly added.</summary>
        public bool AddCcTarget(GameLiving target)
        {
            if (target == null)
                return false;
            lock (_ccTargetsLock)
            {
                if (_ccTargets.Contains(target))
                    return false;
                _ccTargets.Add(target);
                return true;
            }
        }

        /// <summary>Removes a CC target. Returns true when it was present.</summary>
        public bool RemoveCcTarget(GameLiving target)
        {
            if (target == null)
                return false;
            lock (_ccTargetsLock)
                return _ccTargets.Remove(target);
        }

        /// <summary>Drops every tracked CC target.</summary>
        public void ClearCcTargets()
        {
            lock (_ccTargetsLock)
                _ccTargets.Clear();
        }

        public bool ContainsCcTarget(GameLiving target)
        {
            if (target == null)
                return false;
            lock (_ccTargetsLock)
                return _ccTargets.Contains(target);
        }

        /// <summary>
        /// Re-validates the tracked CC targets, dropping any that the supplied
        /// predicate rejects (dead, despawned, etc.). Returns the new count.
        /// </summary>
        public int ValidateCcTargets(System.Func<GameLiving, bool> keepPredicate)
        {
            if (keepPredicate == null)
                return CcTargetCount;
            lock (_ccTargetsLock)
            {
                _ccTargets.RemoveAll(gl => !keepPredicate(gl));
                return _ccTargets.Count;
            }
        }

        /// <summary>
        /// Picks one of the tracked CC targets at random (or null if empty).
        /// Pull-target fallback used by the main puller when CC pre-mez is staged.
        /// </summary>
        public GameLiving PickRandomCcTarget()
        {
            lock (_ccTargetsLock)
            {
                if (_ccTargets.Count == 0)
                    return null;
                return _ccTargets[Util.Random(_ccTargets.Count - 1)];
            }
        }

        // Default minimum con-level to pull: -1 (blue). The puller skips grey
        // (-3) and green (-2) by default so chain-pulling doesn't burn cycles
        // on no-xp / trivial mobs. Players can relax it via /mcamp.
        public int ConLevelFilter = -1;

        // ---- Group-level chat dedup ----
        // A "topic" (e.g. "say-low-health") is staked by the first bot that
        // says it. Other bots in the group are silenced on that topic until
        // the cooldown elapses — prevents 8 bots all shouting "I'm low!" in
        // 2 seconds after a wipe. Per-topic timestamps, lock-protected.
        private readonly System.Collections.Generic.Dictionary<string, long> _chatTopicUntil
            = new System.Collections.Generic.Dictionary<string, long>(System.StringComparer.OrdinalIgnoreCase);
        private readonly Lock _chatTopicLock = new();

        /// <summary>
        /// Tries to claim the right to speak on <paramref name="topic"/> at
        /// the group level. Returns true (and arms the cooldown) when the
        /// caller is free to speak; false when another group member already
        /// covered that topic within the dedup window.
        ///
        /// <paramref name="cooldownMs"/> defaults to
        /// <see cref="MimicConfig.MIMIC_GROUP_CHAT_DEDUP_MS"/>. Pass 0 to
        /// bypass the dedup entirely.
        /// </summary>
        public bool TryClaimChatTopic(string topic, int cooldownMs = -1)
        {
            if (string.IsNullOrEmpty(topic))
                return true; // unscoped chat is always allowed

            int cd = cooldownMs < 0 ? MimicConfig.MIMIC_GROUP_CHAT_DEDUP_MS : cooldownMs;
            if (cd <= 0)
                return true;

            long now = GameLoop.GameLoopTime;

            lock (_chatTopicLock)
            {
                if (_chatTopicUntil.TryGetValue(topic, out long until) && until > now)
                    return false;

                _chatTopicUntil[topic] = now + cd;
                return true;
            }
        }

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

        /// <summary>
        /// Called when a group member dies. If the dying member was the MainTank,
        /// scan the rest of the group for the best surviving tank-capable mimic
        /// and promote them. Without this the dead tank stays MainTank, the
        /// healer keeps trying to top up a corpse, and DPS keep assisting on
        /// nothing — the group spirals.
        ///
        /// Returns true if a takeover happened.
        /// </summary>
        public bool TryPromoteSecondaryTank(GameLiving dying)
        {
            if (dying == null || MainTank != dying)
                return false;
            if (MainLeader?.Group == null)
                return false;

            GameLiving best = null;
            int bestScore = int.MinValue;
            foreach (GameLiving member in MainLeader.Group.GetMembersInTheGroup())
            {
                if (member == null || member == dying || !member.IsAlive)
                    continue;
                int score = ScoreTankCandidate(member);
                if (score > bestScore)
                {
                    bestScore = score;
                    best = member;
                }
            }

            // Even a non-ideal melee can hold aggro better than the corpse. Floor
            // is "any alive group member with a melee weapon" — score may be 0
            // but it beats a dead MainTank.
            if (best == null)
                return false;

            MainTank = best;
            SayToGroup(best, "Mimic.Group.TankSet");
            return true;
        }

        /// <summary>
        /// Mirror of <see cref="TryPromoteSecondaryTank"/> for the MainCC role.
        /// When the current MainCC dies, scan the rest of the group for the
        /// best surviving CC-capable mimic and promote them. Without this the
        /// dead bot stays MainCC, CcStrategy keeps dispatching mez/root casts
        /// on a corpse, and adds stay loose.
        /// </summary>
        public bool TryPromoteSecondaryCC(GameLiving dying)
        {
            if (dying == null || MainCC != dying)
                return false;
            if (MainLeader?.Group == null)
                return false;

            GameLiving best = null;
            foreach (GameLiving member in MainLeader.Group.GetMembersInTheGroup())
            {
                if (member == null || member == dying || !member.IsAlive)
                    continue;
                if (member is not MimicNPC m || !m.CanCastCrowdControlSpells)
                    continue;
                // First valid match wins — CC roster is small enough that a
                // class-tier scoring isn't worth the complexity.
                best = member;
                break;
            }
            if (best == null)
                return false;

            MainCC = best;
            SayToGroup(best, "Mimic.Group.CCSet");
            return true;
        }

        /// <summary>
        /// Backup healer flag promotion. When a flagged healer dies, ensure
        /// at least one other healer-capable mimic in the group has its
        /// IsHealer flag set. Without this, a group with one Cleric + one
        /// Druid loses ALL healing when the Cleric dies (the Druid keeps its
        /// IsHealer=false, so it stays in DPS mode).
        /// </summary>
        public bool TryPromoteHealer(GameLiving dying)
        {
            if (dying == null || MainLeader?.Group == null)
                return false;
            if (dying is not MimicNPC dm || dm.MimicBrain == null || !dm.MimicBrain.IsHealer)
                return false; // dying wasn't a flagged healer

            foreach (GameLiving member in MainLeader.Group.GetMembersInTheGroup())
            {
                if (member == null || member == dying || !member.IsAlive)
                    continue;
                if (member is not MimicNPC m || m.MimicBrain == null)
                    continue;
                if (!m.CanCastHealSpells && !m.CanCastInstantHealSpells)
                    continue;
                if (m.MimicBrain.IsHealer)
                    return false; // someone else already flagged — nothing to do
            }

            // No live flagged healer remains — promote first eligible candidate.
            foreach (GameLiving member in MainLeader.Group.GetMembersInTheGroup())
            {
                if (member == null || member == dying || !member.IsAlive)
                    continue;
                if (member is not MimicNPC m || m.MimicBrain == null)
                    continue;
                if (!m.CanCastHealSpells && !m.CanCastInstantHealSpells)
                    continue;
                m.MimicBrain.IsHealer = true;
                SayToGroup(m, "Mimic.Group.HealerOn");
                return true;
            }
            return false;
        }

        // Higher score = better tank candidate. Real tanks (shield + plate class)
        // outrank Reaver-ish hybrids, which outrank pure melee DPS, which outrank
        // casters/healers. Players are slightly preferred over mimics so a real
        // tank in the group always gets the role back.
        private static int ScoreTankCandidate(GameLiving member)
        {
            int score = 0;
            if (member is GamePlayer)
                score += 5;

            int classId = 0;
            if (member is GamePlayer gp)
                classId = gp.CharacterClass?.ID ?? 0;
            else if (member is MimicNPC mn)
                classId = (int) mn.CharacterClass.ID;

            switch ((eCharacterClass) classId)
            {
                // Plate / shield tanks — first choice
                case eCharacterClass.Paladin:
                case eCharacterClass.Armsman:
                case eCharacterClass.Hero:
                case eCharacterClass.Warrior:
                    score += 100;
                    break;
                // Chain / hybrid melee with shield spec — second choice
                case eCharacterClass.Reaver:
                case eCharacterClass.Champion:
                case eCharacterClass.Thane:
                case eCharacterClass.Warden:
                    score += 60;
                    break;
                // Off-tanks / heavy melee — last resort
                case eCharacterClass.Mercenary:
                case eCharacterClass.Berserker:
                case eCharacterClass.Blademaster:
                case eCharacterClass.Savage:
                case eCharacterClass.Valkyrie:
                case eCharacterClass.Friar:
                    score += 20;
                    break;
                default:
                    // Anything else is a bad tank but we still allow it as a
                    // floor over no tank at all.
                    score += 1;
                    break;
            }
            return score;
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
        /// <summary>Is a group member already casting a non-instant single-target heal?</summary>
        public bool AlreadyCastingSingleHeal { get; private set; }
        /// <summary>Group member currently covered by a non-instant single-target heal.</summary>
        public GameLiving MemberBeingSingleHealed { get; private set; }
        private readonly HashSet<GameLiving> _membersBeingSingleHealed = new();
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
                AlreadyCastingSingleHeal = false;
                MemberBeingSingleHealed = null;
                _membersBeingSingleHealed.Clear();
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
                                    case eSpellType.Heal:
                                    case eSpellType.CombatHeal:
                                    case eSpellType.MercHeal:
                                    case eSpellType.OmniHeal:
                                        if (!handler.Spell.IsInstantCast
                                            && handler.Target != null
                                            && checker.Group.IsInTheGroup(handler.Target))
                                            MarkSingleHealInProgress(handler.Target);
                                        break;
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

        public void MarkSingleHealInProgress(GameLiving target)
        {
            if (target == null)
                return;

            AlreadyCastingSingleHeal = true;
            MemberBeingSingleHealed ??= target;
            _membersBeingSingleHealed.Add(target);
        }

        public bool IsSingleHealReserved(GameLiving target)
        {
            return target != null && _membersBeingSingleHealed.Contains(target);
        }

        public GameLiving PickAlternateHealTarget(MimicNPC checker, GameLiving excluded, bool includeInjured, bool avoidSingleHealReservation)
        {
            if (checker?.Group == null)
                return null;

            GameLiving best = null;
            int bestScore = int.MaxValue;

            foreach (GameLiving groupMember in checker.Group.GetMembersInTheGroup())
            {
                if (groupMember == null || !groupMember.IsAlive || groupMember == excluded)
                    continue;

                if (groupMember != checker && !groupMember.IsWithinRadius(checker, WorldMgr.VISIBILITY_DISTANCE))
                    continue;

                if (groupMember.HealthPercent >= 100)
                    continue;

                bool needsHeal = groupMember.HealthPercent < HealThreshold
                    || (includeInjured && groupMember.HealthPercent < 100);

                if (!needsHeal)
                    continue;

                if (avoidSingleHealReservation
                    && AlreadyCastingSingleHeal
                    && IsSingleHealReserved(groupMember)
                    && groupMember.HealthPercent >= EmergencyThreshold)
                    continue;

                int score = groupMember.HealthPercent;
                if (groupMember == MainTank)
                    score -= 10;
                if (groupMember.HealthPercent < EmergencyThreshold)
                    score -= 25;

                if (score < bestScore)
                {
                    best = groupMember;
                    bestScore = score;
                }
            }

            return best;
        }

        #endregion
    }
}

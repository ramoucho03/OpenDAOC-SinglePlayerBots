using DOL.AI.Brain;
using DOL.Database;
using DOL.Events;
using DOL.GS.Realm;
using DOL.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;

namespace DOL.GS.Scripts
{
    #region Battlegrounds

    public static class MimicBattlegrounds
    {
        private static readonly Logger log = LoggerManager.Create(MethodBase.GetCurrentMethod().DeclaringType);

        // DB-driven map of every Battleground row → its running MimicBattleground.
        // Replaces the hard-coded trio (Thidranki 252 / Caledonia 249 / Molvik 165)
        // which referenced WRONG region IDs on OpenDAOC: 252 is not a valid
        // region, 249 is Darkness Falls (a dungeon, not a BG), and 165 is
        // Cathal Valley (not Molvik). The OpenDAOC BG layout uses regions
        // 234-242 + 165, and the canonical level/region mapping lives in the
        // Battleground DB table — so we read that at boot rather than guess.
        private static readonly Dictionary<ushort, MimicBattleground> _battlegrounds = new();

        // Legacy named handles kept for any external script that still
        // references them. Now resolved from the DB-driven map at boot time
        // — they may stay null on servers whose Battleground rows don't
        // match the historical level brackets.
        public static MimicBattleground ThidBattleground => _battlegrounds.TryGetValue(238, out var bg) ? bg : null;
        public static MimicBattleground MolvikBattleground => _battlegrounds.TryGetValue(241, out var bg) ? bg : null;
        public static MimicBattleground CathalValleyBattleground => _battlegrounds.TryGetValue(165, out var bg) ? bg : null;

        // Drives the player-presence check that auto-spawns / auto-clears the
        // BG mimic populations every minute.
        private static ECSGameTimer _bgPresenceTimer;
        private const int BG_PRESENCE_CHECK_MS = 60_000;
        private const int BG_BOTS_PER_REALM_WHEN_PLAYERS = 20;

        // Per-realm spawn anchors used by every BG. BGs share a similar
        // realm-corner layout (Alb SE, Hib SW, Mid NE), so a single set of
        // coordinates works as a sensible default. Servers with custom BG
        // layouts can override per-region by populating the DBspawn override
        // (TODO: add DB column) — for now these are the safe defaults that
        // landed bots in the right realm zone on stock OpenDAOC maps.
        private static readonly Point3D BG_ALB_SPAWN = new(37200, 51200, 3950);
        private static readonly Point3D BG_HIB_SPAWN = new(19820, 19305, 4050);
        private static readonly Point3D BG_MID_SPAWN = new(53300, 26100, 4270);

        public static void Initialize()
        {
            // Load every Battleground row from the DB and instantiate a
            // MimicBattleground per region. The DB row is the source of
            // truth for level brackets — no more hardcoded mismatches.
            foreach (DbBattleground bg in GameServer.Database.SelectAllObjects<DbBattleground>())
            {
                if (bg == null)
                    continue;

                // Skip non-BG entries that may live in the same table
                // (Darkness Falls, dungeon stub rows). Real BGs always have
                // a sane MinLevel/MaxLevel range.
                if (bg.MinLevel == 0 || bg.MaxLevel == 0 || bg.MaxLevel < bg.MinLevel)
                    continue;

                if (WorldMgr.GetRegion(bg.RegionID) == null)
                    continue;

                _battlegrounds[bg.RegionID] = new MimicBattleground(
                    bg.RegionID, BG_ALB_SPAWN, BG_HIB_SPAWN, BG_MID_SPAWN,
                    BG_BOTS_PER_REALM_WHEN_PLAYERS * 3,
                    BG_BOTS_PER_REALM_WHEN_PLAYERS * 3,
                    bg.MinLevel, bg.MaxLevel);
            }

            // Start the player-presence loop. Each minute we check each BG region:
            //   - if a player is present and the BG is dormant → Start (spawns 20/realm)
            //   - if no player and the BG is running         → Clear (delete all bots)
            _bgPresenceTimer = new ECSGameTimer(null, BgPresenceTick, BG_PRESENCE_CHECK_MS);
            _bgPresenceTimer.Start();

            log.Info($"MimicBattlegrounds initialized: {_battlegrounds.Count} BG(s) registered from DB with player-presence auto-spawn.");
        }

        private static int BgPresenceTick(ECSGameTimer timer)
        {
            try
            {
                foreach (var kv in _battlegrounds)
                    UpdateBgPresence(kv.Value, kv.Key);
            }
            catch (Exception e)
            {
                log.Error("BgPresenceTick failed", e);
            }
            return BG_PRESENCE_CHECK_MS;
        }

        private static void UpdateBgPresence(MimicBattleground bg, ushort regionId)
        {
            if (bg == null) return;

            Region region = WorldMgr.GetRegion(regionId);
            if (region == null) return;

            int humanPlayersInRegion = 0;
            foreach (GamePlayer p in ClientService.Instance.GetPlayersOfRegion(region))
            {
                if (p == null) continue;
                if (p.Client?.Account != null && p.Client.Account.PrivLevel > 1)
                    continue; // ignore GM/Admin invisible spectators
                humanPlayersInRegion++;
            }

            bool wantsRunning = humanPlayersInRegion > 0;

            if (wantsRunning && !bg.IsRunning)
            {
                bg.Start();
                log.Info($"BG region {regionId}: {humanPlayersInRegion} player(s) present, starting bot population.");
            }
            else if (!wantsRunning && bg.IsRunning)
            {
                bg.Clear();
                log.Info($"BG region {regionId}: no players, clearing bot population.");
            }
        }

        public class MimicBattleground
        {
            public MimicBattleground(ushort region, Point3D albSpawn, Point3D hibSpawn, Point3D midSpawn, int minMimics, int maxMimics, byte minLevel, byte maxLevel)
            {
                m_region = region;
                m_albSpawnPoint = albSpawn;
                m_hibSpawnPoint = hibSpawn;
                m_midSpawnPoint = midSpawn;
                m_minTotalMimics = minMimics;
                m_maxTotalMimics = maxMimics;
                m_minLevel = minLevel;
                m_maxLevel = maxLevel;
            }

            private ECSGameTimer m_masterTimer;

            private MimicSpawner m_albSpawner;
            private MimicSpawner m_hibSpawner;
            private MimicSpawner m_midSpawner;

            private int m_timerInterval = 600000; // 10 minutes
            private long m_resetMaxTime = 0;

            private readonly List<BattleStats> m_battleStats = new List<BattleStats>();

            private Point3D m_albSpawnPoint;
            private Point3D m_hibSpawnPoint;
            private Point3D m_midSpawnPoint;

            private ushort m_region;

            private byte m_minLevel;
            private byte m_maxLevel;

            private int m_minTotalMimics;
            private int m_maxTotalMimics;

            private int m_currentMaxTotalMimics;

            private int m_currentMaxAlb;
            private int m_currentMaxHib;
            private int m_currentMaxMid;

            public bool IsRunning => m_masterTimer != null && m_masterTimer.IsAlive;

            public void Start()
            {
                if (m_masterTimer != null)
                {
                    if (!m_masterTimer.IsAlive)
                        m_masterTimer.Start();

                    m_albSpawner.Start();
                    m_hibSpawner.Start();
                    m_midSpawner.Start();
                }
                else
                {
                    ResetMaxMimics();

                    m_masterTimer = new ECSGameTimer(null, new ECSGameTimer.ECSTimerCallback(MasterTimerCallback), m_timerInterval);

                    m_albSpawner = new MimicSpawner(eRealm.Albion, m_minLevel, m_maxLevel, m_currentMaxAlb, 65, m_albSpawnPoint, m_region, 0, true);
                    m_hibSpawner = new MimicSpawner(eRealm.Hibernia, m_minLevel, m_maxLevel, m_currentMaxHib, 65, m_hibSpawnPoint, m_region, 0, true);
                    m_midSpawner = new MimicSpawner(eRealm.Midgard, m_minLevel, m_maxLevel, m_currentMaxMid, 65, m_midSpawnPoint, m_region, 0, true);
                }
            }

            public void Stop()
            {
                m_masterTimer?.Stop();
                m_albSpawner?.Stop();
                m_hibSpawner?.Stop();
                m_midSpawner?.Stop();
            }

            public void Clear()
            {
                Stop();

                m_masterTimer = null;

                // Iterate snapshots so we don't race with the spawner's task
                // pool, which mutates _mimics under lock from CreateMimic.
                if (m_albSpawner != null)
                {
                    foreach (MimicNPC mimic in m_albSpawner.GetMimicsSnapshot())
                        mimic.Delete();

                    m_albSpawner.Delete();
                    m_albSpawner = null;
                }

                if (m_hibSpawner != null)
                {
                    foreach (MimicNPC mimic in m_hibSpawner.GetMimicsSnapshot())
                        mimic.Delete();

                    m_hibSpawner.Delete();
                    m_hibSpawner = null;
                }

                if (m_midSpawner != null)
                {
                    foreach (MimicNPC mimic in m_midSpawner.GetMimicsSnapshot())
                        mimic.Delete();

                    m_midSpawner.Delete();
                    m_midSpawner = null;
                }
            }

            private int MasterTimerCallback(ECSGameTimer timer)
            {
                if (GameLoop.GameLoopTime > m_resetMaxTime &&
                    !m_albSpawner.IsRunning &&
                    !m_hibSpawner.IsRunning &&
                    !m_midSpawner.IsRunning)
                {
                    ResetMaxMimics();
                }

                // Use the locked Count accessor — the raw .Count read can
                // tear when the spawn task pool is mid-Add.
                int albCount = m_albSpawner.MimicsCount;
                int hibCount = m_hibSpawner.MimicsCount;
                int midCount = m_midSpawner.MimicsCount;
                int totalMimics = albCount + hibCount + midCount;
                if (log.IsInfoEnabled)
                {
                    log.Info("Alb: " + albCount + "/" + m_currentMaxAlb);
                    log.Info("Hib: " + hibCount + "/" + m_currentMaxHib);
                    log.Info("Mid: " + midCount + "/" + m_currentMaxMid);
                    log.Info("Total Mimics: " + totalMimics + "/" + m_currentMaxTotalMimics);
                }

                return m_timerInterval + Util.Random(-300000, 300000); // 10 minutes + or - 5 minutes
            }

            /// <summary>
            /// Gets a new total maximum and minimum of mimics for each realm randomly.
            /// </summary>
            private void ResetMaxMimics()
            {
                m_currentMaxTotalMimics = Util.Random(m_minTotalMimics, m_maxTotalMimics);
                m_currentMaxAlb = 0;
                m_currentMaxHib = 0;
                m_currentMaxMid = 0;

                for (int i = 0; i < m_currentMaxTotalMimics; i++)
                {
                    int randomRealm = Util.Random(2);

                    if (randomRealm == 0)
                        m_currentMaxAlb++;
                    else if (randomRealm == 1)
                        m_currentMaxHib++;
                    else if (randomRealm == 2)
                        m_currentMaxMid++;
                }
            }

            public void UpdateBattleStats(MimicNPC mimic)
            {
                m_battleStats.Add(new BattleStats(mimic.Name, mimic.RaceName, mimic.CharacterClass.Name, mimic.Kills, true));
            }

            public void BattlegroundStats(GamePlayer player)
            {
                List<MimicNPC> currentMimics = GetMasterList();
                List<BattleStats> currentStats = new List<BattleStats>();

                if (currentMimics.Count != 0)
                {
                    foreach (MimicNPC mimic in currentMimics)
                        currentStats.Add(new BattleStats(mimic.Name, mimic.RaceName, mimic.CharacterClass.Name, mimic.Kills, false));
                }

                List<BattleStats> masterStatList = new List<BattleStats>();
                masterStatList.AddRange(currentStats);
                masterStatList.AddRange(m_battleStats);

                List<BattleStats> sortedList = masterStatList.OrderByDescending(obj => obj.TotalKills).ToList();

                string message = "----------------------------------------\n\n";
                int index = Math.Min(10, sortedList.Count);

                if (sortedList.Count != 0)
                {
                    for (int i = 0; i < index; i++)
                    {
                        string stats = string.Format("{0}. {1} - {2} - {3} - Kills: {4}",
                            i + 1,
                            sortedList[i].Name,
                            sortedList[i].Race,
                            sortedList[i].ClassName,
                            sortedList[i].TotalKills);

                        if (sortedList[i].IsDead)
                            stats += " - DEAD";

                        stats += "\n\n";

                        message += stats;
                    }
                }

                message += "Alb count: " + m_albSpawner.SpawnCount;
                message += "\nHib count: " + m_hibSpawner.SpawnCount;
                message += "\nMid count: " + m_midSpawner.SpawnCount;

                player.Out.SendMessage(message, PacketHandler.eChatType.CT_System, PacketHandler.eChatLoc.CL_PopupWindow);
            }

            public List<MimicNPC> GetMasterList()
            {
                List<MimicNPC> masterList = new List<MimicNPC>();

                // Snapshot each spawner under its own lock so the task-pool
                // spawn path (Add under lock) can't tear the iterator.
                foreach (MimicNPC mimic in m_albSpawner.GetMimicsSnapshot())
                {
                    if (mimic != null && mimic.ObjectState == GameObject.eObjectState.Active && mimic.ObjectState != GameObject.eObjectState.Deleted)
                        masterList.Add(mimic);
                }

                foreach (MimicNPC mimic in m_hibSpawner.GetMimicsSnapshot())
                {
                    if (mimic != null && mimic.ObjectState == GameObject.eObjectState.Active && mimic.ObjectState != GameObject.eObjectState.Deleted)
                        masterList.Add(mimic);
                }

                foreach (MimicNPC mimic in m_midSpawner.GetMimicsSnapshot())
                {
                    if (mimic != null && mimic.ObjectState == GameObject.eObjectState.Active && mimic.ObjectState != GameObject.eObjectState.Deleted)
                        masterList.Add(mimic);
                }

                return masterList;
            }
        }

        private struct BattleStats
        {
            public string Name;
            public string Race;
            public string ClassName;
            public int TotalKills;
            public bool IsDead;

            public BattleStats(string name, string race, string className, int totalKills, bool dead)
            {
                Name = name;
                Race = race;
                ClassName = className;
                TotalKills = totalKills;
                IsDead = dead;
            }
        }
    }

    #endregion Battlegrounds

    #region Spawning

    public static class MimicSpawning
    {
        public static List<MimicSpawner> MimicSpawners
        {
            get
            {
                return _mimicSpawners ?? (_mimicSpawners = new List<MimicSpawner>());
            }
        }

        private static List<MimicSpawner> _mimicSpawners;
    }

    #endregion Spawning

    public static class MimicManager
    {
        private static readonly Logger log = LoggerManager.Create(MethodBase.GetCurrentMethod().DeclaringType);

        public static List<MimicNPC> MimicNPCs = new List<MimicNPC>();

        public static bool Initialize()
        {
            log.Info("MimicManager Initializing...");

            // Fail-fast on missing chat translations: the previous behaviour
            // silently leaked the raw key into party chat when a translation
            // file was incomplete. Warns once per missing key in the boot log.
            MimicChatKeys.ValidateAll();

            MimicBattlegrounds.Initialize();
            RegisterPlayerLifecycleHandlers();
            PvPFrontierManager.Initialize();
            PvEPopulationManager.Initialize();

            return true;
        }

        public static bool AddMimicToWorld(MimicNPC mimic, Point3D position, ushort region)
        {
            if (mimic != null)
            {
                mimic.X = position.X;
                mimic.Y = position.Y;
                mimic.Z = position.Z;

                mimic.CurrentRegionID = region;

                if (mimic.AddToWorld())
                    return true;
            }

            return false;
        }

        public static MimicNPC GetMimic(eMimicClass charClass, byte level, string name = "", eGender gender = eGender.Neutral, eSpecType spec = eSpecType.None, bool preventCombat = false)
        {
            if (charClass == eMimicClass.None)
                return null;

            MimicNPC mimic = new MimicNPC(charClass, level, gender, spec);

            if (mimic != null)
            {
                if (name != "")
                    mimic.Name = name;

                if (gender != eGender.Neutral)
                {
                    mimic.Gender = gender;

                    foreach (PlayerRace race in PlayerRace.AllRaces)
                    {
                        if (race.ID == (eRace)mimic.Race)
                        {
                            mimic.Model = (ushort)race.GetModel(gender);
                            break;
                        }
                    }
                }

                if (preventCombat)
                {
                    MimicBrain mimicBrain = mimic.Brain as MimicBrain;

                    if (mimicBrain != null)
                        mimicBrain.PreventCombat = preventCombat;
                }

                return mimic;
            }

            return null;
        }

        public static eMimicClass GetRandomMimicClass(eRealm realm = eRealm.None)
        {
            List<eMimicClass> classes = new List<eMimicClass>();

            foreach (eMimicClass mimicClass in Enum.GetValues(typeof(eMimicClass)))
            {
                if (mimicClass == eMimicClass.None)
                    continue;

                if (realm != eRealm.None &&
                    !GlobalConstants.STARTING_CLASSES_DICT[realm].Contains((eCharacterClass)mimicClass))
                    continue;

                classes.Add(mimicClass);
            }

            if (classes.Count == 0)
                return eMimicClass.None;

            return classes[Util.Random(classes.Count - 1)];
        }

        public static eMimicClass GetRandomMeleeClass(eRealm realm = eRealm.None)
        {
            List<eMimicClass> meleeClasses = new List<eMimicClass>();

            foreach (eMimicClass mimicClass in Enum.GetValues(typeof(eMimicClass)))
            {
                switch (mimicClass)
                {
                    case eMimicClass.None:
                    case eMimicClass.Cabalist:
                    case eMimicClass.Sorcerer:
                    case eMimicClass.Theurgist:
                    case eMimicClass.Wizard:
                    case eMimicClass.Eldritch:
                    case eMimicClass.Enchanter:
                    case eMimicClass.Mentalist:
                    case eMimicClass.Bonedancer:
                    case eMimicClass.Runemaster:
                    case eMimicClass.Spiritmaster:
                    continue;

                    default:
                    if (realm != eRealm.None &&
                        !GlobalConstants.STARTING_CLASSES_DICT[realm].Contains((eCharacterClass)mimicClass))
                        continue;

                    meleeClasses.Add(mimicClass);
                    break;
                }
            }

            if (meleeClasses.Count == 0)
                return eMimicClass.None;

            return meleeClasses[Util.Random(meleeClasses.Count - 1)];
        }

        public static eMimicClass GetRandomCasterClass(eRealm realm = eRealm.None)
        {
            List<eMimicClass> casterClasses = new List<eMimicClass>();

            foreach (eMimicClass mimicClass in Enum.GetValues(typeof(eMimicClass)))
            {
                switch (mimicClass)
                {
                    case eMimicClass.Cabalist:
                    case eMimicClass.Sorcerer:
                    case eMimicClass.Theurgist:
                    case eMimicClass.Wizard:
                    case eMimicClass.Eldritch:
                    case eMimicClass.Enchanter:
                    case eMimicClass.Mentalist:
                    case eMimicClass.Bonedancer:
                    case eMimicClass.Runemaster:
                    case eMimicClass.Spiritmaster:

                    if (realm != eRealm.None &&
                        !GlobalConstants.STARTING_CLASSES_DICT[realm].Contains((eCharacterClass)mimicClass))
                        continue;

                    casterClasses.Add(mimicClass);
                    break;

                    default:
                    continue;
                }
            }

            if (casterClasses.Count == 0)
                return eMimicClass.None;

            return casterClasses[Util.Random(casterClasses.Count - 1)];
        }

        #region Ownership tracking

        // Live mimics tracked per owning account so we can find them on disconnect.
        // We delete owned mimics on disconnect; there is no hibernation/restore
        // path — the player can re-create their group with /mgroup or /mcreate
        // after reconnect.
        private static readonly Dictionary<string, List<MimicNPC>> _liveByOwner = new();
        private static readonly object _ownerLock = new();

        /// <summary>
        /// Stamps ownership on the mimic and starts tracking it. Safe to call
        /// multiple times; subsequent calls only update the owner field.
        /// </summary>
        public static void RegisterOwned(GamePlayer owner, MimicNPC mimic)
        {
            if (mimic == null)
                return;

            string acct = owner?.Client?.Account?.Name;

            if (string.IsNullOrEmpty(acct))
                return;

            mimic.OwnerAccount = acct;

            lock (_ownerLock)
            {
                if (!_liveByOwner.TryGetValue(acct, out List<MimicNPC> list))
                {
                    list = new List<MimicNPC>();
                    _liveByOwner[acct] = list;
                }

                if (!list.Contains(mimic))
                    list.Add(mimic);
            }
        }

        /// <summary>
        /// Stops tracking the mimic. Called from MimicNPC.Delete so we never
        /// hold references to dead objects.
        /// </summary>
        public static void UnregisterOwned(MimicNPC mimic)
        {
            if (mimic?.OwnerAccount == null)
                return;

            lock (_ownerLock)
            {
                if (_liveByOwner.TryGetValue(mimic.OwnerAccount, out List<MimicNPC> list))
                {
                    list.Remove(mimic);

                    if (list.Count == 0)
                        _liveByOwner.Remove(mimic.OwnerAccount);
                }
            }
        }

        public static IReadOnlyList<MimicNPC> GetLiveOwnedBy(string accountName)
        {
            if (string.IsNullOrEmpty(accountName))
                return Array.Empty<MimicNPC>();

            lock (_ownerLock)
            {
                if (_liveByOwner.TryGetValue(accountName, out List<MimicNPC> list))
                    return list.ToList(); // copy out under lock

                return Array.Empty<MimicNPC>();
            }
        }

        /// <summary>
        /// Player is leaving for good (Quit event, or the engine's
        /// SECONDS_TO_QUIT_ON_LINKDEATH timer expired). Every mimic the
        /// account owns is removed from the world.
        /// </summary>
        private static void OnPlayerQuit(DOLEvent e, object sender, EventArgs args)
        {
            DeleteOwnedBy(sender as GamePlayer, "quit");
        }

        /// <summary>
        /// Player just lost their connection. The engine already keeps the
        /// avatar in-world for SECONDS_TO_QUIT_ON_LINKDEATH (60s by default)
        /// before firing Quit — so mimics are safe during a short net blip.
        ///
        /// If <see cref="MimicConfig.MIMIC_LINKDEATH_GRACE_SECONDS"/> is zero,
        /// we revert to the legacy "delete immediately" behaviour for
        /// servers that want bots dropped the moment a link breaks.
        /// </summary>
        private static void OnPlayerLinkdeath(DOLEvent e, object sender, EventArgs args)
        {
            if (MimicConfig.MIMIC_LINKDEATH_GRACE_SECONDS <= 0)
            {
                DeleteOwnedBy(sender as GamePlayer, "linkdeath (grace=0)");
                return;
            }

            if (sender is GamePlayer player && player.Client?.Account != null && log.IsInfoEnabled)
            {
                int owned = GetLiveOwnedBy(player.Client.Account.Name).Count;
                if (owned > 0)
                    log.Info($"Mimic linkdeath grace active for {player.Client.Account.Name}: keeping {owned} bot(s) alive until Quit.");
            }
        }

        private static void DeleteOwnedBy(GamePlayer player, string reason)
        {
            if (player?.Client?.Account == null)
                return;

            IReadOnlyList<MimicNPC> owned = GetLiveOwnedBy(player.Client.Account.Name);
            if (owned.Count == 0)
                return;

            int deleted = 0;
            foreach (MimicNPC mimic in owned.ToList())
            {
                if (mimic != null && mimic.ObjectState == GameObject.eObjectState.Active)
                {
                    // Pre-flag this teardown as expected so the [MIMIC-FLICKER]
                    // diagnostic in MimicNPC.Delete doesn't log a stack trace —
                    // quit/linkdeath cleanup is a legitimate delete, not flicker.
                    mimic._beingDeleted = true;
                    mimic.Delete();
                    deleted++;
                }
            }

            if (log.IsInfoEnabled)
                log.Info($"Deleted {deleted} mimic(s) for account {player.Client.Account.Name} (reason: {reason})");
        }

        /// <summary>
        /// Drops every live mimic owned by the player from the world. Used by
        /// the /mclear command and as a manual reset hatch.
        /// </summary>
        public static int ClearOwned(GamePlayer player)
        {
            if (player?.Client?.Account == null)
                return 0;

            IReadOnlyList<MimicNPC> owned = GetLiveOwnedBy(player.Client.Account.Name);
            int count = 0;

            foreach (MimicNPC mimic in owned.ToList())
            {
                if (mimic != null && mimic.ObjectState == GameObject.eObjectState.Active)
                {
                    // Pre-flag this teardown as expected so the [MIMIC-FLICKER]
                    // diagnostic doesn't log a stack trace — /mclear is voluntary.
                    mimic._beingDeleted = true;
                    mimic.Delete();
                    count++;
                }
            }

            return count;
        }

        internal static void RegisterPlayerLifecycleHandlers()
        {
            GameEventMgr.AddHandler(GamePlayerEvent.Quit, OnPlayerQuit);
            GameEventMgr.AddHandler(GamePlayerEvent.Linkdeath, OnPlayerLinkdeath);
            GameEventMgr.AddHandler(GroupEvent.MemberDisbanded, OnGroupMemberDisbanded);
            // GroupEvent.MemberJoined is registered separately in the
            // OnScriptsCompiled bootstrap class because its handler
            // (OnGroupMemberJoined) lives in that other class, not here.
        }

        /// <summary>
        /// When a player kicks a mimic from their group (or leaves the group
        /// themself, leaving the bots alone), the bot is deleted cleanly.
        /// We only act on player-owned bots (OwnerAccount set) so auto-spawned
        /// frontier/battleground bots aren't affected.
        ///
        /// The MimicNPC._beingDeleted flag prevents re-entry when MimicNPC.Delete
        /// itself calls Group.RemoveMember (which triggers this same event).
        /// </summary>
        private static void OnGroupMemberDisbanded(DOLEvent e, object sender, EventArgs args)
        {
            if (args is not MemberDisbandedEventArgs disbandArgs)
                return;

            if (disbandArgs.Member is not MimicNPC mimic)
                return;

            if (mimic._beingDeleted)
                return; // already on its way out, don't double-delete

            if (string.IsNullOrEmpty(mimic.OwnerAccount))
                return; // auto-spawned bot (frontier / battleground), not owned by a player

            if (log.IsInfoEnabled)
                log.Info($"Mimic {mimic.Name} left group → deleting (owner={mimic.OwnerAccount})");

            mimic.Delete();
        }

        #endregion Ownership tracking
    }

    #region Equipment

    public static class MimicEquipment
    {
        private static readonly Logger log = LoggerManager.Create(MethodBase.GetCurrentMethod().DeclaringType);

        // ----------------------------------------------------------------
        // Endgame-aware ROG tuning.
        //
        // Level-50 mimics are the RvR roster; a real level-50 player runs
        // level-51 (item-cap) gear, so we itemise their ROG pieces at the
        // cap and with a utility floor scaled to level. This (a) unlocks the
        // >50 colour / model / quality tiers in GeneratedUniqueItem (mithril,
        // adamantium, black cloth, forest green, burgundy, navy blue, ...) for
        // a sharper end-game look, and (b) pushes class-appropriate stats
        // toward the utility ceiling instead of occasionally rolling near the
        // bare 15 minimum. Lower-level PvE mimics keep modest, level-matched
        // gear. Model, colour and bonus *selection* all stay random per piece,
        // so two mimics of the same class never look or itemise identically.
        // ----------------------------------------------------------------

        /// <summary>ROG item level to itemise a mimic of the given level at.</summary>
        public static byte GetRogItemLevel(int mimicLevel)
        {
            // 48+ is treated as "max level" and itemised at the live item cap
            // (51) so the top colour / model / quality tiers unlock.
            if (mimicLevel >= 48)
                return 51;

            return (byte)Math.Clamp(mimicLevel, 1, 51);
        }

        /// <summary>
        /// Minimum total item utility a mimic's ROG gear should reach, scaled
        /// by level. CapUtility ceilings a level-51 piece around ~32-47, so a
        /// floor in the mid-30s makes high-level gear reliably strong without
        /// fighting the cap. PvE lowbies stay near the 15 baseline.
        /// </summary>
        public static int GetRogUtilityFloor(int mimicLevel)
        {
            return Math.Clamp((int)(mimicLevel * 0.7), 15, 38);
        }

        /// <summary>
        /// For high-level mimics, narrow a DB item list to pieces at or near
        /// the level cap so a level-50 bot doesn't equip a level-44 weapon
        /// when a level-50/51 one exists. Returns the original list unchanged
        /// for low-level mimics or when nothing near-cap is in the table, so
        /// we never trade a real item for an empty selection.
        /// </summary>
        private static IList<DbItemTemplate> PreferNearCapLevel(IList<DbItemTemplate> list, int mimicLevel)
        {
            if (list == null || list.Count == 0 || mimicLevel < 48)
                return list;

            int floor = mimicLevel - 2;
            List<DbItemTemplate> nearCap = list.Where(t => t.Level >= floor).ToList();
            return nearCap.Count > 0 ? nearCap : list;
        }

        // Curated "nice look" armour dye palettes per realm. The ROG already
        // tints each piece independently, which left a bot wearing a bronze
        // helm, a mithril chest and green legs — a mismatched, clown-y set.
        // Instead we pick ONE colour per mimic and dye the whole armour set
        // with it, so each bot reads as a coherently-kitted player. The colour
        // still varies between mimics, so the roster stays visually diverse.
        // Each palette mixes the realm's signature hues (red / green / blue)
        // with a few premium neutrals (steel, fine alloy, mithril, adamantium,
        // black cloth, charcoal) shared across realms. All codes are taken
        // from the >40/>50 tiers already validated in GetRandomColorForRealm.
        private static readonly int[] _albArmorColors = { 27, 64, 65, 66, 67, 143, 18, 20, 21, 26, 43, 118 };
        private static readonly int[] _hibArmorColors = { 32, 33, 68, 70, 71, 142, 18, 20, 21, 26, 43, 118 };
        private static readonly int[] _midArmorColors = { 36, 51, 52, 54, 141, 18, 20, 21, 26, 43, 118 };

        private static int GetRandomArmorColorForRealm(eRealm realm)
        {
            int[] palette = realm switch
            {
                eRealm.Albion   => _albArmorColors,
                eRealm.Hibernia => _hibArmorColors,
                eRealm.Midgard  => _midArmorColors,
                _ => _albArmorColors
            };

            return palette[Util.Random(palette.Length - 1)];
        }

        public static void SetWeaponROG(GameLiving living, eRealm realm, eCharacterClass charClass, byte level, eObjectType objectType, eInventorySlot slot, eDamageType damageType, int utilityMinimum = 15)
        {
            DbItemTemplate itemToCreate = new GeneratedUniqueItem(realm, charClass, level, objectType, slot, damageType, utilityMinimum);

            GameInventoryItem item = GameInventoryItem.Create(itemToCreate);
            living.Inventory.AddItem(slot, item);
        }

        public static void SetArmorROG(GameLiving living, eRealm realm, eCharacterClass charClass, byte level, eObjectType objectType, int utilityMinimum = 15)
        {
            // One coherent dye for the whole set (see palette comment above),
            // rolled once per mimic so the bot looks deliberately kitted while
            // still differing from the next bot.
            int setColor = GetRandomArmorColorForRealm(realm);

            for (int i = Slot.HELM; i <= Slot.ARMS; i++)
            {
                if (i == Slot.JEWELRY || i == Slot.CLOAK)
                    continue;

                eInventorySlot slot = (eInventorySlot)i;
                DbItemTemplate itemToCreate = new GeneratedUniqueItem(realm, charClass, level, objectType, slot, utilityMinimum);

                GameInventoryItem item = GameInventoryItem.Create(itemToCreate);

                // Apply the per-set colour to the created item only — never to
                // the shared template (which GeneratedUniqueItem builds fresh
                // per piece, but the instance-only write keeps the existing
                // AddItem colour convention).
                if (item != null)
                    item.Color = setColor;

                living.Inventory.AddItem(slot, item);
            }
        }

        public static void SetJewelryROG(GameLiving living, eRealm realm, eCharacterClass charClass, byte level, eObjectType objectType, int utilityMinimum = 15)
        {
            for (int i = Slot.JEWELRY; i <= Slot.RIGHTRING; i++)
            {
                if (i is Slot.TORSO or Slot.LEGS or Slot.ARMS or Slot.FOREARMS or Slot.SHIELD)
                    continue;

                eInventorySlot slot = (eInventorySlot)i;
                DbItemTemplate itemToCreate = new GeneratedUniqueItem(realm, charClass, level, objectType, slot, utilityMinimum);

                GameInventoryItem item = GameInventoryItem.Create(itemToCreate);

                if (i == Slot.RIGHTRING || i == Slot.LEFTRING)
                    living.Inventory.AddItem(living.Inventory.FindFirstEmptySlot(eInventorySlot.LeftRing, eInventorySlot.RightRing), item);
                else if (i == Slot.LEFTWRIST || i == Slot.RIGHTWRIST)
                    living.Inventory.AddItem(living.Inventory.FindFirstEmptySlot(eInventorySlot.LeftBracer, eInventorySlot.RightBracer), item);
                else
                    living.Inventory.AddItem(slot, item);
            }
        }

        public static void SetInstrumentROG(GameLiving living, eRealm realm, eCharacterClass charClass, byte level, eObjectType objectType, eInventorySlot slot, eInstrumentType instrumentType)
        {
            // Upstream GeneratedUniqueItem no longer accepts eInstrumentType.
            // Build a base instrument and stamp the requested DPS/instrument
            // type on the resulting template so the mimic minstrel still gets
            // the right instrument family.
            DbItemTemplate itemToCreate = new GeneratedUniqueItem(false, realm, charClass, level, objectType, slot);
            itemToCreate.DPS_AF = (int) instrumentType;

            GameInventoryItem item = GameInventoryItem.Create(itemToCreate);
            living.Inventory.AddItem(slot, item);
        }

        // ----------------------------------------------------------------
        // ItemTemplate cache.
        //
        // Every MimicNPC constructor equips weapon/shield/ranged/armor/jewelry,
        // each running a SelectObjects<DbItemTemplate>. On this server those
        // queries resolve to a full `SELECT ... FROM ItemTemplate` table scan
        // (~134ms each, visible in the server log). A constructor therefore
        // cost ~1s of synchronous DB work; the PvE/PvP population managers
        // build batches of bots inside their 5s maintenance tick ON THE
        // GAMELOOP THREAD, so the whole server froze for 1-3s every 5s and
        // the client blinked every NPC out and back in ("all bots reset every
        // 3-5s" regression).
        //
        // Fix: load the ItemTemplate table ONCE, then filter it in memory.
        // First mimic build pays the 134ms; every build after is microseconds.
        // ----------------------------------------------------------------
        private static DbItemTemplate[] _itemTemplateCache;
        private static readonly object _itemTemplateCacheLock = new();

        private static DbItemTemplate[] GetItemTemplates()
        {
            DbItemTemplate[] cache = _itemTemplateCache;
            if (cache != null)
                return cache;

            lock (_itemTemplateCacheLock)
            {
                if (_itemTemplateCache == null)
                {
                    var all = GameServer.Database.SelectAllObjects<DbItemTemplate>();
                    _itemTemplateCache = all != null ? all.ToArray() : Array.Empty<DbItemTemplate>();

                    if (log.IsInfoEnabled)
                        log.Info($"MimicManager: cached {_itemTemplateCache.Length} ItemTemplate rows for bot equipment.");
                }

                return _itemTemplateCache;
            }
        }

        public static void SetMeleeWeapon(IGamePlayer player, eObjectType weapType, eHand hand, eWeaponDamageType damageType = 0)
        {
            // Class-adapted weapons: when WEAPON_ROG is on, every mimic gets a
            // Generated Unique weapon itemised for its class (stats matched to
            // the spec) instead of a random DB template whose bonuses may be
            // irrelevant to the build. Mirrors the ARMOR_ROG / jewelry path so
            // the whole loadout reads as "adapté à la spé".
            if (MimicConfig.WEAPON_ROG)
            {
                SetMeleeWeaponRog(player, weapType, hand, damageType);
                return;
            }

            int min = Math.Max(1, player.Level - 6);
            int max = Math.Min(51, player.Level + 4);

            IList<DbItemTemplate> itemList = GetItemTemplates()
                .Where(t => t.Level >= min && t.Level <= max
                            && t.Object_Type == (int)weapType
                            && t.Realm == (int)player.Realm
                            && t.IsPickable)
                .ToList();
            List<DbItemTemplate> itemsToKeep = new List<DbItemTemplate>();

            if (itemList.Count != 0)
            {
                foreach (DbItemTemplate item in itemList)
                {
                    bool shouldAddItem = false;

                    switch (hand)
                    {
                        case eHand.oneHand:
                        shouldAddItem = item.Item_Type == Slot.RIGHTHAND || item.Item_Type == Slot.LEFTHAND;
                        break;

                        case eHand.leftHand:
                        shouldAddItem = item.Item_Type == Slot.LEFTHAND;
                        break;

                        case eHand.twoHand:
                        shouldAddItem = item.Item_Type == Slot.TWOHAND && (damageType == 0 || item.Type_Damage == (int)damageType);
                        break;

                        default:
                        break;
                    }

                    if (shouldAddItem)
                        itemsToKeep.Add(item);
                }

                if (itemsToKeep.Count != 0)
                {
                    IList<DbItemTemplate> pick = PreferNearCapLevel(itemsToKeep, player.Level);
                    DbItemTemplate itemTemplate = pick[Util.Random(pick.Count - 1)];
                    AddItem(player, itemTemplate, hand);
                    return;
                }
            }

            // ROG fallback: no DB-templated weapon matched the realm + class
            // + level window. Without this the bot would spawn unarmed and
            // CheckPuller / AttackAction would silently no-op forever. Roll
            // a Generated Unique item in the right slot so the bot at least
            // has SOMETHING to swing. Log a warn so operators notice their
            // weapon table needs filling.
            if (log.IsWarnEnabled)
                log.Warn($"No melee weapon found in DB for {player.Name} ({weapType}/{hand} L{player.Level}/{player.Realm}); generating ROG fallback");

            SetMeleeWeaponRog(player, weapType, hand, damageType);
        }

        /// <summary>
        /// Generates a class-adapted Generated Unique melee weapon and equips
        /// it in the slot implied by <paramref name="hand"/>. Shared by the
        /// WEAPON_ROG path and the "no DB weapon found" fallback.
        /// </summary>
        private static void SetMeleeWeaponRog(IGamePlayer player, eObjectType weapType, eHand hand, eWeaponDamageType damageType)
        {
            eInventorySlot fallbackSlot = hand switch
            {
                eHand.twoHand => eInventorySlot.TwoHandWeapon,
                eHand.leftHand => eInventorySlot.LeftHandWeapon,
                _ => eInventorySlot.RightHandWeapon,
            };
            SetWeaponROG((GameLiving)player,
                player.Realm,
                (eCharacterClass)player.CharacterClass.ID,
                GetRogItemLevel(player.Level),
                weapType,
                fallbackSlot,
                (eDamageType)(byte)damageType,
                GetRogUtilityFloor(player.Level));
        }

        public static void SetRangedWeapon(IGamePlayer player, eObjectType weapType)
        {
            // Class-adapted ranged weapon (see SetMeleeWeapon for the rationale).
            if (MimicConfig.WEAPON_ROG)
            {
                SetWeaponROG((GameLiving)player,
                    player.Realm,
                    (eCharacterClass)player.CharacterClass.ID,
                    GetRogItemLevel(player.Level),
                    weapType,
                    eInventorySlot.DistanceWeapon,
                    eDamageType.Slash,
                    GetRogUtilityFloor(player.Level));
                return;
            }

            int min = Math.Max(1, player.Level - 6);
            int max = Math.Min(51, player.Level + 3);

            IList<DbItemTemplate> itemList = GetItemTemplates()
                .Where(t => t.Level >= min && t.Level <= max
                            && t.Object_Type == (int)weapType
                            && t.Item_Type == 13
                            && t.Realm == (int)player.Realm
                            && t.IsPickable)
                .ToList();

            if (itemList.Count != 0)
            {
                IList<DbItemTemplate> pick = PreferNearCapLevel(itemList, player.Level);
                DbItemTemplate itemTemplate = pick[Util.Random(pick.Count - 1)];
                AddItem(player, itemTemplate);

                return;
            }

            // ROG fallback for ranged: archer bots (Scout / Ranger / Hunter)
            // without a bow simply can't pull, which kills the whole camp
            // pipeline. Same Generated Unique fallback path as melee above.
            if (log.IsWarnEnabled)
                log.Warn($"No ranged weapon found in DB for {player.Name} ({weapType} L{player.Level}/{player.Realm}); generating ROG fallback");

            SetWeaponROG((GameLiving)player,
                player.Realm,
                (eCharacterClass)player.CharacterClass.ID,
                GetRogItemLevel(player.Level),
                weapType,
                eInventorySlot.DistanceWeapon,
                eDamageType.Slash,
                GetRogUtilityFloor(player.Level));
        }

        public static void SetShield(IGamePlayer player, int shieldSize)
        {
            if (shieldSize < 1)
                return;

            int min = Math.Max(1, player.Level - 6);
            int max = Math.Min(51, player.Level + 3);

            IList<DbItemTemplate> itemList = GetItemTemplates()
                .Where(t => t.Level >= min && t.Level <= max
                            && t.Object_Type == (int)eObjectType.Shield
                            && t.Realm == (int)player.Realm
                            && t.Type_Damage == shieldSize
                            && t.IsPickable)
                .ToList();

            if (itemList.Count != 0)
            {
                DbItemTemplate itemTemplate = itemList[Util.Random(itemList.Count - 1)];
                AddItem(player, itemTemplate);

                return;
            }
            else
                log.Info("No Shield found for " + player.Name);
        }

        public static void SetArmor(IGamePlayer player, eObjectType armorType)
        {
            int min = Math.Max(1, player.Level - 6);
            int max = Math.Min(51, player.Level + 3);

            IList<DbItemTemplate> itemList = GetItemTemplates()
                .Where(t => t.Level >= min && t.Level <= max
                            && t.Object_Type == (int)armorType
                            && t.Realm == (int)player.Realm
                            && t.IsPickable)
                .ToList();

            if (itemList.Count != 0)
            {
                Dictionary<int, List<DbItemTemplate>> armorSlots = new Dictionary<int, List<DbItemTemplate>>();

                foreach (DbItemTemplate template in itemList)
                {
                    if (!armorSlots.TryGetValue(template.Item_Type, out List<DbItemTemplate> slotList))
                    {
                        slotList = new List<DbItemTemplate>();
                        armorSlots[template.Item_Type] = slotList;
                    }

                    slotList.Add(template);
                }

                foreach (var pair in armorSlots)
                {
                    if (pair.Value.Count != 0)
                    {
                        DbItemTemplate itemTemplate = pair.Value[Util.Random(pair.Value.Count - 1)];
                        AddItem(player, itemTemplate);
                    }
                }
            }
            else
                log.Info("No armor found for " + player.Name);
        }

        public static void SetInstrument(IGamePlayer player, eObjectType weapType, eInventorySlot slot, eInstrumentType instrumentType)
        {
            int min = Math.Max(1, player.Level - 6);
            int max = Math.Min(51, player.Level + 3);

            IList<DbItemTemplate> itemList = GetItemTemplates()
                .Where(t => t.Level >= min && t.Level <= max
                            && t.Object_Type == (int)weapType
                            && t.DPS_AF == (int)instrumentType
                            && t.Realm == (int)player.Realm
                            && t.IsPickable)
                .ToList();

            if (itemList.Count != 0)
            {
                DbItemTemplate itemTemplate = itemList[Util.Random(itemList.Count - 1)];
                DbInventoryItem item = GameInventoryItem.Create(itemTemplate);
                player.Inventory.AddItem(slot, item);

                return;
            }
            else
                log.Info("No instrument found for " + player.Name);
        }

        public static void SetJewelry(IGamePlayer player)
        {
            int min = Math.Max(1, player.Level - 30);
            int max = Math.Min(51, player.Level + 3);

            List<DbItemTemplate> cloakList = new List<DbItemTemplate>();
            List<DbItemTemplate> jewelryList = new List<DbItemTemplate>();
            List<DbItemTemplate> ringList = new List<DbItemTemplate>();
            List<DbItemTemplate> wristList = new List<DbItemTemplate>();
            List<DbItemTemplate> neckList = new List<DbItemTemplate>();
            List<DbItemTemplate> waistList = new List<DbItemTemplate>();

            IList<DbItemTemplate> itemList = GetItemTemplates()
                .Where(t => t.Level >= min && t.Level <= max
                            && t.Object_Type == (int)eObjectType.Magical
                            && t.Realm == (int)player.Realm
                            && t.IsPickable)
                .ToList();
            if (itemList.Count != 0)
            {
                foreach (DbItemTemplate template in itemList)
                {
                    if (template.Item_Type == Slot.CLOAK)
                        cloakList.Add(template);
                    else if (template.Item_Type == Slot.JEWELRY)
                        jewelryList.Add(template);
                    else if (template.Item_Type == Slot.LEFTRING || template.Item_Type == Slot.RIGHTRING)
                        ringList.Add(template);
                    else if (template.Item_Type == Slot.LEFTWRIST || template.Item_Type == Slot.RIGHTWRIST)
                        wristList.Add(template);
                    else if (template.Item_Type == Slot.NECK)
                        neckList.Add(template);
                    else if (template.Item_Type == Slot.WAIST)
                        waistList.Add(template);
                }

                List<List<DbItemTemplate>> masterList = new List<List<DbItemTemplate>>
                {
                cloakList,
                jewelryList,
                neckList,
                waistList
                };

                foreach (List<DbItemTemplate> list in masterList)
                {
                    if (list.Count != 0)
                    {
                        DbItemTemplate itemTemplate = list[Util.Random(list.Count - 1)];
                        int color = list == cloakList
                            ? GetRandomArmorColorForRealm(player.Realm)
                            : -1;
                        AddItem(player, itemTemplate, color: color);
                    }
                }

                // Add two rings and bracelets
                for (int i = 0; i < 2; i++)
                {
                    if (ringList.Count != 0)
                    {
                        DbItemTemplate itemTemplate = ringList[Util.Random(ringList.Count - 1)];
                        AddItem(player, itemTemplate);
                    }

                    if (wristList.Count != 0)
                    {
                        DbItemTemplate itemTemplate = wristList[Util.Random(wristList.Count - 1)];
                        AddItem(player, itemTemplate);
                    }
                }

                // Not sure this is needed what were you thinking past self?
                if (player.Inventory.GetItem(eInventorySlot.Cloak) == null)
                {
                    DbItemTemplate cloak = GameServer.Database.FindObjectByKey<DbItemTemplate>("cloak");
                    AddItem(player, cloak, color: GetRandomArmorColorForRealm(player.Realm));
                }
            }
            else
                log.Info("No jewelry of any kind found for " + player.Name);
        }

        /// <summary>
        /// Whether the supplied bot's combat profile prefers an off-hand
        /// loadout that makes equipping a shield strictly worse (assassin
        /// dual-wield, archer bow, dual-wield berserker / savage, etc.).
        /// Centralises the previous hand-maintained class-ID list — Mercenary,
        /// Blademaster, Berserker and Savage are tank-melee classes whose
        /// profile alone doesn't say "never shield", so we keep a class-ID
        /// fallback for them. The role check covers Assassin and Archer.
        /// </summary>
        private static bool ShouldBackpackOffhandShield(IGamePlayer player)
        {
            if (player is MimicNPC m && m.CombatProfile != null)
            {
                if (m.CombatProfile.HasRole(eMimicCombatRole.Assassin)
                    || m.CombatProfile.HasRole(eMimicCombatRole.Archer))
                    return true;
            }

            int id = player?.CharacterClass?.ID ?? 0;
            return id == (int)eCharacterClass.Mercenary
                || id == (int)eCharacterClass.Blademaster
                || id == (int)eCharacterClass.Berserker
                || id == (int)eCharacterClass.Savage;
        }

        private static void AddItem(IGamePlayer player, DbItemTemplate itemTemplate, eHand hand = eHand.None, int color = -1)
        {
            if (itemTemplate == null)
            {
                log.Info("itemTemplate in AddItem is null");
                return;
            }

            DbInventoryItem item = GameInventoryItem.Create(itemTemplate);

            if (item != null)
            {
                // Apply a randomized colour to the created item — never to the
                // shared DB template, which is cached and would tint the item
                // for every other consumer.
                if (color >= 0)
                    item.Color = color;

                if (item.Item_Type == Slot.LEFTRING || item.Item_Type == Slot.RIGHTRING)
                {
                    player.Inventory.AddItem(player.Inventory.FindFirstEmptySlot(eInventorySlot.LeftRing, eInventorySlot.RightRing), item);
                    return;
                }
                else if (item.Item_Type == Slot.LEFTWRIST || item.Item_Type == Slot.RIGHTWRIST)
                {
                    player.Inventory.AddItem(player.Inventory.FindFirstEmptySlot(eInventorySlot.LeftBracer, eInventorySlot.RightBracer), item);
                    return;
                }
                else if (item.Item_Type == Slot.LEFTHAND && item.Object_Type != (int)eObjectType.Shield && hand == eHand.oneHand)
                {
                    player.Inventory.AddItem(eInventorySlot.RightHandWeapon, item);
                    return;
                }
                else
                {
                    // Off-hand shield is backpacked for classes whose combat
                    // profile actively prefers a dual-wield / 2H / ranged
                    // loadout — equipping the shield would just waste the
                    // off-hand. Driven by the combat profile (Assassin /
                    // Archer roles) rather than a static class-ID list so
                    // new specs slot in without a code patch.
                    if (item.Object_Type == (int)eObjectType.Shield && ShouldBackpackOffhandShield(player))
                    {
                        player.Inventory.AddItem(player.Inventory.FindFirstEmptySlot(eInventorySlot.FirstEmptyBackpack, eInventorySlot.LastEmptyBackpack), item);
                    }
                    else
                        player.Inventory.AddItem((eInventorySlot)item.Item_Type, item);
                }
            }
            else
                log.Info("Item failed to be created for " + player.Name);
        }
    }

    #endregion Equipment

    #region Spec

    public class MimicSpec
    {
        // Instance field (was incorrectly static — every Spec ctor wrote to
        // the same slot, so the last mimic constructed dictated SpecName for
        // every other mimic. Harmless today because nothing reads SpecName,
        // but the static was a foot-gun waiting for the first debug log.)
        public string SpecName;
        public eObjectType WeaponOneType;
        public eObjectType WeaponTwoType;
        public eWeaponDamageType DamageType = 0;
        public eSpecType SpecType;

        public bool Is2H;

        public List<SpecLine> SpecLines = new List<SpecLine>();

        public MimicSpec()
        { }

        protected void Add(string spec, uint cap, float ratio)
        {
            SpecLines.Add(new SpecLine(spec, cap, ratio));
        }

        protected string ObjToSpec(eObjectType obj)
        {
            string spec = SkillBase.ObjectTypeToSpec(obj);

            return spec;
        }

        public static MimicSpec GetSpec(eMimicClass charClass, eSpecType spec = eSpecType.None)
        {
            switch (charClass)
            {
                case eMimicClass.Armsman: return new ArmsmanSpec(spec);
                case eMimicClass.Cabalist: return new CabalistSpec(spec);
                case eMimicClass.Cleric: return new ClericSpec(spec);
                case eMimicClass.Friar: return new FriarSpec(spec);
                case eMimicClass.Infiltrator: return new InfiltratorSpec();
                case eMimicClass.Mercenary: return new MercenarySpec(spec);
                case eMimicClass.Minstrel: return new MinstrelSpec();
                case eMimicClass.Necromancer: return new NecromancerSpec(spec);
                case eMimicClass.Paladin: return new PaladinSpec(spec);
                case eMimicClass.Reaver: return new ReaverSpec();
                case eMimicClass.Scout: return new ScoutSpec();
                case eMimicClass.Sorcerer: return new SorcererSpec(spec);
                case eMimicClass.Theurgist: return new TheurgistSpec(spec);
                case eMimicClass.Wizard: return new WizardSpec(spec);
                case eMimicClass.Heretic: return new HereticSpec(spec);
                case eMimicClass.MaulerAlb: return new MaulerAlbSpec();

                case eMimicClass.Animist: return new AnimistSpec(spec);
                case eMimicClass.Bainshee: return new BainsheeSpec(spec);
                case eMimicClass.Bard: return new BardSpec();
                case eMimicClass.Blademaster: return new BlademasterSpec(spec);
                case eMimicClass.Champion: return new ChampionSpec(spec);
                case eMimicClass.Druid: return new DruidSpec(spec);
                case eMimicClass.Eldritch: return new EldritchSpec(spec);
                case eMimicClass.Enchanter: return new EnchanterSpec(spec);
                case eMimicClass.Hero: return new HeroSpec(spec);
                case eMimicClass.Mentalist: return new MentalistSpec(spec);
                case eMimicClass.Nightshade: return new NightshadeSpec();
                case eMimicClass.Ranger: return new RangerSpec();
                case eMimicClass.Valewalker: return new ValewalkerSpec();
                case eMimicClass.Vampiir: return new VampiirSpec();
                case eMimicClass.Warden: return new WardenSpec(spec);
                case eMimicClass.MaulerHib: return new MaulerHibSpec();

                case eMimicClass.Berserker: return new BerserkerSpec();
                case eMimicClass.Bonedancer: return new BonedancerSpec(spec);
                case eMimicClass.Healer: return new HealerSpec(spec);
                case eMimicClass.Hunter: return new HunterSpec();
                case eMimicClass.Runemaster: return new RunemasterSpec(spec);
                case eMimicClass.Savage: return new SavageSpec(spec);
                case eMimicClass.Shadowblade: return new ShadowbladeSpec(spec);
                case eMimicClass.Shaman: return new ShamanSpec(spec);
                case eMimicClass.Skald: return new SkaldSpec();
                case eMimicClass.Spiritmaster: return new SpiritmasterSpec(spec);
                case eMimicClass.Thane: return new ThaneSpec(spec);
                case eMimicClass.Valkyrie: return new ValkyrieSpec(spec);
                case eMimicClass.Warlock: return new WarlockSpec();
                case eMimicClass.Warrior: return new WarriorSpec();
                case eMimicClass.MaulerMid: return new MaulerMidSpec();
            }

            return null;
        }
    }

    public struct SpecLine
    {
        public string Spec;
        public uint SpecCap;
        public float levelRatio;

        public SpecLine(string spec, uint cap, float ratio)
        {
            Spec = spec;
            SpecCap = cap;
            levelRatio = ratio;
        }
    }

    #endregion Spec

    #region LFG

    public static class MimicLFGManager
    {
        private static readonly int _minRespawnTime = 60000;    // 1 minute
        private static readonly int _maxRespawnTime = 600000;   // 10 minutes

        private static readonly int _minEntryLifetime = 300000;  // 5 minutes
        private static readonly int _maxEntryLifetime = 3600000; // 1 hour

        private static readonly int _maxPoolSize = 60;
        private static readonly int _spawnChance = 100;

        private static readonly float _removeRandom = 0.25f;

        // Minimum entries the player should see when running /mlfg, after the
        // level filter has been applied. If the filtered pool is below this,
        // GetLFG synthesizes extra entries at the caller's level range.
        private const int _minDisplayedNearCaller = 30;

        // TODO: Maybe add class weighting
        //public static readonly Dictionary<eMimicClass, int> ClassWeights = new()
        //{
        //};

        private static readonly Dictionary<eRealm, RealmPool> _pools = new()
        {
            { eRealm.Albion,   new RealmPool(eRealm.Albion)   },
            { eRealm.Hibernia, new RealmPool(eRealm.Hibernia) },
            { eRealm.Midgard,  new RealmPool(eRealm.Midgard)  },
        };

        public static IReadOnlyList<MimicLFGEntry> GetLFG(eRealm realm, byte level)
        {
            if (!_pools.TryGetValue(realm, out RealmPool pool))
                return Array.Empty<MimicLFGEntry>();

            pool.Refresh(level);

            int min = Math.Max(1, level - 3);
            int max = Math.Min(50, level + 3);

            List<MimicLFGEntry> filtered = pool.GetCurrentPool()
                .Where(e => e.Level >= min && e.Level <= max)
                .ToList();

            if (filtered.Count < _minDisplayedNearCaller)
            {
                int missing = _minDisplayedNearCaller - filtered.Count;
                pool.TopUpForCaller(level, missing);

                filtered = pool.GetCurrentPool()
                    .Where(e => e.Level >= min && e.Level <= max)
                    .ToList();
            }

            return filtered.AsReadOnly();
        }

        public static void Remove(eRealm realm, MimicLFGEntry entry)
        {
            entry.RemoveTime = GameLoop.GameLoopTime - 1;
        }

        private sealed class RealmPool
        {
            private readonly eRealm _realm;
            private readonly object _lock = new();
            private IReadOnlyList<MimicLFGEntry> _currentPool = Array.Empty<MimicLFGEntry>();
            private long _nextRespawnTime = 0;

            public RealmPool(eRealm realm) => _realm = realm;

            public IReadOnlyList<MimicLFGEntry> GetCurrentPool()
            {
                lock (_lock)
                    return _currentPool;
            }

            public void Refresh(byte callerLevel)
            {
                long now = GameLoop.GameLoopTime;

                if (now < _nextRespawnTime)
                {
                    if (_currentPool.Any(e => now >= e.RemoveTime))
                        TryRebuild(callerLevel, addNew: false);

                    return;
                }

                TryRebuild(callerLevel, addNew: true);
            }

            private void TryRebuild(byte callerLevel, bool addNew)
            {
                if (!Monitor.TryEnter(_lock))
                    return;

                try
                {
                    long now = GameLoop.GameLoopTime;

                    List<MimicLFGEntry> currentEntries = _currentPool
                        .Where(e => now < e.RemoveTime)
                        .ToList();

                    if (addNew && currentEntries.Count > 0)
                    {
                        int removeCount = (int)Math.Floor(currentEntries.Count * _removeRandom);
                        currentEntries.RemoveRange(0, removeCount);
                    }

                    if (addNew)
                    {
                        int slots = _maxPoolSize - currentEntries.Count;

                        for (int i = 0; i < slots; i++)
                        {
                            if (!Util.Chance(_spawnChance))
                                continue;

                            byte level = (byte)Util.Random( Math.Max(1, callerLevel - 3), Math.Min(50, callerLevel + 3));
                            long removeTime = now + Util.Random( _minEntryLifetime, _maxEntryLifetime);

                            currentEntries.Add(new MimicLFGEntry(MimicManager.GetRandomMimicClass(_realm), level, _realm, removeTime));
                        }

                        _nextRespawnTime = now + Util.Random(_minRespawnTime, _maxRespawnTime);
                    }

                    _currentPool = currentEntries.AsReadOnly();
                }
                finally
                {
                    Monitor.Exit(_lock);
                }
            }

            /// <summary>
            /// Synchronously grows the pool with extra entries inside the caller's
            /// level window so /mlfg has at least <see cref="_minDisplayedNearCaller"/>
            /// matches to show. Bypasses the spawn-chance gate; the pool may temporarily
            /// exceed <see cref="_maxPoolSize"/> until the next periodic rebuild.
            /// </summary>
            public void TopUpForCaller(byte callerLevel, int missing)
            {
                if (missing <= 0)
                    return;

                if (!Monitor.TryEnter(_lock))
                    return;

                try
                {
                    long now = GameLoop.GameLoopTime;

                    List<MimicLFGEntry> entries = _currentPool
                        .Where(e => now < e.RemoveTime)
                        .ToList();

                    int minLevel = Math.Max(1, callerLevel - 3);
                    int maxLevel = Math.Min(50, callerLevel + 3);

                    for (int i = 0; i < missing; i++)
                    {
                        byte level = (byte)Util.Random(minLevel, maxLevel);
                        long removeTime = now + Util.Random(_minEntryLifetime, _maxEntryLifetime);
                        entries.Add(new MimicLFGEntry(MimicManager.GetRandomMimicClass(_realm), level, _realm, removeTime));
                    }

                    _currentPool = entries.AsReadOnly();
                }
                finally
                {
                    Monitor.Exit(_lock);
                }
            }
        }

        public sealed class MimicLFGEntry
        {
            public string Name { get; }
            public eGender Gender { get; }
            public eMimicClass MimicClass { get; }
            public byte Level { get; }
            public eRealm Realm { get; }
            public bool RefusedGroup { get; set; }

            public long RemoveTime;

            public MimicLFGEntry(eMimicClass mimicClass, byte level, eRealm realm, long removeTime)
            {
                Gender = Util.Random(1) > 0 ? eGender.Male : eGender.Female;
                Name = MimicNames.GetName(Gender, realm);
                MimicClass = mimicClass;
                Level = level;
                Realm = realm;
                RemoveTime = removeTime;
            }
        }
    }

    #endregion LFG

    public class SetupMimicsEvent
    {
        private static readonly Logger log = LoggerManager.Create(MethodBase.GetCurrentMethod().DeclaringType);

        [ScriptLoadedEvent]
        public static void OnScriptsCompiled(DOLEvent e, object sender, EventArgs args)
        {
            if (MimicManager.Initialize())
                log.Info("MimicNPCs Initialized.");
            else
                log.Error("MimicNPCs Failed to Initialize.");

            // When anyone joins a group, see if a better tank is now available
            // and promote them. Without this, MainTank stays whatever it was
            // initialized to (usually the human leader), and mimic tanks never
            // act as tank because IsMainTank is false for them. Global handler
            // — fires for player-led groups, full-bot groups, and LFG recruits
            // alike. Registered here (not in MimicManager.RegisterPlayer
            // LifecycleHandlers) because OnGroupMemberJoined is private to
            // this class.
            GameEventMgr.AddHandler(GroupEvent.MemberJoined, OnGroupMemberJoined);
        }

        private static void OnGroupMemberJoined(DOLEvent e, object sender, EventArgs args)
        {
            if (sender is not Group g || g.MimicGroup == null)
                return;
            if (args is not MemberJoinedEventArgs mja || mja.Member == null)
                return;

            // Evaluate the joiner — usually the only candidate that changed.
            g.MimicGroup.TryAutoPromoteTank(mja.Member);
        }
    }

    // Just a quick way to get names...
    public static class MimicNames
    {
        // Pre-split arrays, deduplicated, evenly capitalised. The previous
        // implementation kept the lists as one giant comma-separated string
        // and called Split(',') on every bot spawn, which both allocated a
        // fresh string[] each time and re-walked the list for duplicates we
        // never wanted in the first place. Build the arrays once at class
        // init time and pick at random from them.
        //
        // Naming theme per realm:
        //   Albion   — Arthurian + late-medieval English / Frankish
        //   Hibernia — classical Irish / Welsh / Gaelic
        //   Midgard  — Old Norse, Icelandic sagas, Viking-age
        // Female and male sets are kept separate so the gender check stays
        // truthful. Roughly 200+ candidates per realm/gender post-dedup.

        private static readonly string[] _albMale =
        {
            // Arthurian core
            "Gareth", "Lancelot", "Cedric", "Tristan", "Percival", "Gawain",
            "Arthur", "Merlin", "Galahad", "Ector", "Uther", "Mordred", "Bors",
            "Lionel", "Agravain", "Bedivere", "Kay", "Lamorak", "Erec",
            "Gaheris", "Pellinore", "Loholt", "Leodegrance", "Aglovale", "Tor",
            "Ywain", "Cador", "Tristram", "Cei", "Launcelot", "Dinadan",
            "Lucan", "Caradoc", "Segwarides", "Geraint", "Bohort", "Karados",
            "Palomides", "Palamedes", "Parzival", "Kaherdin", "Patrise",
            "Madouc", "Guivret", "Aelfric", "Rivalin",
            // Late-medieval English / Frankish names a 1.65-era Albion roster would carry
            "Aldred", "Aldous", "Alfred", "Alric", "Amalric", "Anselm",
            "Athelstan", "Baldwin", "Bertrand", "Beorn", "Beorhtric",
            "Cadell", "Cedric", "Charles", "Conrad", "Cuthbert", "Drogo",
            "Edmund", "Edward", "Edwin", "Egbert", "Eldric", "Eustace",
            "Everard", "Fulk", "Geoffrey", "Gilbert", "Godfrey", "Godwin",
            "Gregory", "Guy", "Harold", "Hartwin", "Henry", "Hubert", "Hugh",
            "Humphrey", "Ingram", "John", "Jordan", "Julian", "Lambert",
            "Leofric", "Leofwine", "Lionel", "Lucan", "Magnus", "Martin",
            "Matthew", "Maurice", "Nicholas", "Odo", "Osbert", "Osric",
            "Oswin", "Owain", "Peter", "Philip", "Ralph", "Randolph",
            "Raymond", "Reginald", "Richard", "Robert", "Roger", "Roland",
            "Rufus", "Simon", "Stephen", "Talbot", "Theobald", "Thomas",
            "Tobias", "Walter", "Wilbur", "Wilfred", "William", "Wulfric",
        };

        private static readonly string[] _albFemale =
        {
            // Arthurian core
            "Guinevere", "Isolde", "Morgana", "Elaine", "Vivienne", "Nimue",
            "Lynette", "Rhiannon", "Enid", "Iseult", "Bellicent", "Brangaine",
            "Blanchefleur", "Laudine", "Lisanor", "Brisen", "Linet", "Serene",
            "Ysabele", "Peronell", "Clarissant", "Igraine", "Yseult",
            "Lunete", "Dindrane", "Ragnelle", "Morgause", "Bellicent",
            "Cundrie", "Vivian", "Selene",
            // Period-appropriate Albion roster
            "Adela", "Adelaide", "Adelina", "Agatha", "Agnes", "Alice",
            "Aline", "Amice", "Amicia", "Anne", "Avice", "Avis", "Beatrice",
            "Belisent", "Cecily", "Clarice", "Constance", "Cristina",
            "Diota", "Dionisia", "Edith", "Edmonia", "Edyth", "Eleanor",
            "Elen", "Elfreda", "Eliduc", "Elysia", "Emelina", "Emma",
            "Estrid", "Felicia", "Flora", "Florence", "Geva", "Godgifu",
            "Goldwin", "Gunnora", "Helewise", "Helewysa", "Hilda", "Idonea",
            "Idonia", "Inga", "Isabel", "Isabella", "Jocosa", "Joan",
            "Juliana", "Katherine", "Lauretta", "Leofgifu", "Lettice",
            "Lucia", "Mabel", "Margery", "Matilda", "Maud", "Millicent",
            "Muriel", "Nesta", "Petronilla", "Phelippa", "Philippa", "Rohese",
            "Rosamund", "Sibyl", "Sigrid", "Theodora", "Walburga", "Wilfrida",
        };

        private static readonly string[] _hibMale =
        {
            "Aonghus", "Breandan", "Cian", "Dallan", "Eogan", "Fearghal",
            "Greagoir", "Iomhar", "Lorcan", "Mairtin", "Neachtan", "Odhran",
            "Paraic", "Ruairi", "Seosamh", "Aed", "Beircheart", "Colm",
            "Domhnall", "Eanna", "Fergus", "Goll", "Irial", "Liam", "MacCon",
            "Naoimhin", "Padraig", "Ronan", "Seanan", "Tadhgan", "Uilliam",
            "Ailill", "Bran", "Cairbre", "Daithi", "Eoghan", "Faolan", "Gorm",
            "Iollan", "Lughaidh", "Manannan", "Niall", "Oisin", "Seadna",
            "Tadhg", "Ultan", "Alastar", "Bairre", "Caoilte", "Daire", "Enna",
            "Fiachra", "Gairm", "Imleach", "Jarlath", "Kian", "Laoiseach",
            "Malachy", "Naoise", "Paidin", "Roibeard", "Seamus", "Turlough",
            "Uilleag",
            // Additional Irish / Scottish Gaelic and a sprinkle of Welsh
            "Ailbhe", "Ainmire", "Amhlaoibh", "Aodhan", "Aralt", "Ardan",
            "Art", "Bairrionn", "Bearach", "Brendan", "Brian", "Cadell",
            "Cahir", "Caoimhin", "Carbry", "Cathal", "Cathaoir", "Cearul",
            "Ciaran", "Cillian", "Coilin", "Conall", "Conan", "Conn",
            "Conor", "Cormac", "Cuan", "Cuchulain", "Dagda", "Dallas",
            "Darragh", "Declan", "Diarmuid", "Donn", "Donnchadh", "Dubhthach",
            "Eamonn", "Earnan", "Eoin", "Faelan", "Fechin", "Felim", "Finn",
            "Finnegan", "Flannan", "Garbhan", "Gilroy", "Glasny", "Iarfhlaith",
            "Iarlaith", "Kevin", "Kieran", "Lugh", "Maelmuire", "Maoilbhrid",
            "Maolmuire", "Murchadh", "Neamhain", "Nuadu", "Oghma", "Owen",
            "Pearce", "Phelan", "Riordan", "Senan", "Seosamh", "Sloan",
            "Sloane", "Tarlach", "Teague", "Tiernan", "Torin", "Uaithne",
            // Welsh / Brythonic
            "Aneurin", "Bedwyr", "Bran", "Caradog", "Cynan", "Dafydd",
            "Drystan", "Eifion", "Emrys", "Garan", "Gareth", "Gawain",
            "Geraint", "Idris", "Iestyn", "Iolo", "Llew", "Llywelyn",
            "Madog", "Mervyn", "Owain", "Pryderi", "Pwyll", "Rhodri",
            "Rhys", "Taliesin",
        };

        private static readonly string[] _hibFemale =
        {
            "Aibhlinn", "Brighid", "Caoilfhionn", "Deirdre", "Eabha",
            "Fionnuala", "Grainne", "Iseult", "Lean", "Maire", "Niamh",
            "Oonagh", "Padraigin", "Roisin", "Saoirse", "Teagan", "Una",
            "Aoife", "Aisling", "Blathnat", "Cliodhna", "Dymphna", "Eidin",
            "Fineachan", "Gormfhlaith", "Iomhar", "Laoise", "Maighread",
            "Noirin", "Orlaith", "Plurabelle", "Rioghnach", "Siobhan",
            "Treasa", "Ursula", "Ailbhe", "Bairrfhionn", "Caoilinn", "Dairine",
            "Eabhnat", "Fearchara", "Gormlaith", "Ite", "Laochlann", "Mairtin",
            "Nollaig", "Ornait", "Pala", "Roise", "Seaghdha", "Tomaltach",
            "Uinseann",
            // Additional Irish / Scottish Gaelic / Welsh
            "Aifric", "Ailis", "Aine", "Aislinn", "Anwen", "Aoibheann",
            "Aoibhgreine", "Aoibhin", "Arwen", "Banba", "Beibhinn", "Bevin",
            "Branwen", "Cadhla", "Caireann", "Caitlin", "Caitriona", "Cara",
            "Catriona", "Ceara", "Ciara", "Ciarrai", "Cinnia", "Clodagh",
            "Daireann", "Damhnait", "Dechtire", "Dervorgilla", "Eavan",
            "Eibhleann", "Eibhlin", "Eileen", "Eilis", "Eithne", "Etain",
            "Eveleen", "Faye", "Fianait", "Finola", "Gormla", "Gwen",
            "Gwendolyn", "Honora", "Ide", "Inan", "Ita", "Keelin", "Keira",
            "Kelda", "Kennedy", "Kyna", "Liadan", "Lyne", "Maeve", "Mairead",
            "Meara", "Meath", "Moira", "Mor", "Muireann", "Muirenn",
            "Nessa", "Nuala", "Olwen", "Oonagh", "Orna", "Owena", "Pegeen",
            "Rhonwen", "Ronat", "Sadhbh", "Saraid", "Seana", "Sile",
            "Sinead", "Sive", "Sorcha", "Tara", "Tegan", "Triona",
        };

        private static readonly string[] _midMale =
        {
            "Agnar", "Bjorn", "Dagur", "Eirik", "Fjolnir", "Geir", "Haldor",
            "Ivar", "Jarl", "Kjartan", "Leif", "Magnus", "Njall", "Orvar",
            "Ragnald", "Sigbjorn", "Thrain", "Ulf", "Vifil", "Arni", "Bardi",
            "Dain", "Einar", "Faldan", "Grettir", "Hogni", "Ingvar", "Jokul",
            "Koll", "Leiknir", "Mord", "Nikul", "Ornolf", "Ragnvald",
            "Sigmund", "Thorfinn", "Ulfar", "Vali", "Yngvar", "Asgeir",
            "Bolli", "Darri", "Egill", "Flosi", "Gisli", "Hjortur", "Ingolf",
            "Jokull", "Kolbeinn", "Leikur", "Mordur", "Nils", "Orri",
            "Sigurdur", "Thormundur", "Ulfur", "Valur", "Yngvi", "Arnstein",
            "Bardur", "David", "Eik", "Fridgeir", "Grimur", "Hafthor",
            "Jorundur", "Kari", "Ljotur", "Nokkvi", "Oddur", "Rafn",
            "Steinar", "Thorir", "Valgard", "Yngve", "Askur", "Baldur",
            "Dagr", "Eirikur", "Fridleif",
            // Additional Old Norse / Icelandic saga names
            "Alfgeir", "Alrek", "Arinbjorn", "Asbjorn", "Asbrand", "Asmund",
            "Atli", "Audun", "Bardi", "Bersi", "Bjarki", "Bjarni", "Bork",
            "Brand", "Brodir", "Eilif", "Eindrid", "Eyjolf", "Eystein",
            "Falgeir", "Finnbogi", "Finnr", "Floki", "Frodi", "Geirmund",
            "Glum", "Gizur", "Gorm", "Grimkel", "Gudbrand", "Gudmund",
            "Gudrod", "Gunnar", "Gunnlaug", "Gunnstein", "Hakon", "Halfdan",
            "Hallbjorn", "Hallgrim", "Hallstein", "Hallvard", "Harald",
            "Hauk", "Helgi", "Heming", "Hjalti", "Hrafn", "Hrolf", "Illugi",
            "Knut", "Kolbein", "Kolskegg", "Olaf", "Onund", "Osvif", "Ottar",
            "Ragnar", "Refr", "Rolf", "Runolf", "Sigvald", "Skapti", "Skarp",
            "Snorri", "Solmund", "Stein", "Steinar", "Sturla", "Styr",
            "Svein", "Thangbrand", "Thord", "Thorbjorn", "Thorgeir",
            "Thorgrim", "Thorhall", "Thorkel", "Thorleif", "Thormod",
            "Thorolf", "Thorstein", "Thorvald", "Thorvar", "Toki", "Tostig",
            "Trygg", "Trygvi", "Vali", "Vermund", "Vigfus", "Yngvald",
        };

        private static readonly string[] _midFemale =
        {
            "Aesa", "Bjorg", "Dalla", "Edda", "Fjola", "Gerd", "Halla",
            "Inga", "Jora", "Kari", "Lina", "Marna", "Njola", "Orna", "Ragna",
            "Sif", "Thora", "Ulfhild", "Vika", "Alva", "Bodil", "Dagny",
            "Eira", "Frida", "Gisla", "Hildur", "Ingibjorg", "Jofrid",
            "Kolfinna", "Mina", "Olina", "Ragnheid", "Sigrid", "Thordis",
            "Una", "Yrsa", "Asgerd", "Bergthora", "Flosa", "Gudrid", "Hjordis",
            "Ingimund", "Lidgerd", "Mjoll", "Oddny", "Ranveig", "Sigrun",
            "Thorhalla", "Valdis", "Alfhild", "Bardis", "Davida", "Eilika",
            "Fridleif", "Gudrun", "Jokulina", "Halfdana", "Aslaug",
            // Additional Old Norse / Icelandic saga names
            "Arnbjorg", "Arndis", "Arngerd", "Arnora", "Asa", "Asdis",
            "Asfrid", "Aslaug", "Astrid", "Audhild", "Audr", "Bera",
            "Birgit", "Borghild", "Brunhild", "Dagrun", "Dis", "Drifa",
            "Dyrleif", "Eilif", "Elin", "Erna", "Estrid", "Eyja", "Eyvor",
            "Fastrid", "Finna", "Folkvi", "Freydis", "Geirhild", "Gerda",
            "Gillaug", "Gjaflaug", "Gretha", "Grima", "Gudleif", "Gudny",
            "Gudve", "Gunnhild", "Gyda", "Hallbera", "Hallveig", "Hedvig",
            "Helga", "Herdis", "Hervor", "Hilda", "Hildigunn", "Hildr",
            "Hjordis", "Hrafnhild", "Idunn", "Ingegerd", "Ingiborg",
            "Ingrid", "Iona", "Iorunn", "Jodis", "Jofrid", "Jorunn",
            "Katla", "Kolfinna", "Kolgrima", "Kraka", "Liv", "Mjoll",
            "Nanna", "Olof", "Oluf", "Osk", "Rannveig", "Saga", "Salgerd",
            "Sigfrid", "Sigga", "Sigvor", "Skadi", "Snorra", "Solveig",
            "Steinunn", "Steingerd", "Svala", "Svana", "Svanhild", "Thordis",
            "Thora", "Thorbjorg", "Thorbera", "Thorgerd", "Thorgunn",
            "Thorlaug", "Thorny", "Thorunn", "Thorvor", "Thyra", "Tora",
            "Turid", "Unn", "Vald", "Vigdis",
        };

        // Gender-neutral "gamer handle" pseudonyms. Mimics are meant to blend
        // into a live PvP population, so ~half the time GetName hands out one
        // of these instead of a lore first name. Roughly half of each list is
        // realm-flavoured (Arthurian / Celtic / Norse cores dressed up with
        // gamer affixes) and half is generic modern gamer tags. ~700 unique
        // entries per realm (an original ~200 plus a 500-strong digit-free
        // batch appended below each array). Generated via gen_gamer_names.py.

        private static readonly string[] _albGamer =
        {
            "NobleSaxon", "Logres64", "Theurg7", "Haze101", "Saxon64", "BigPendrag",
            "xXReaverXx", "CamelotPro", "FrostPro", "Helix47", "Titan808", "xXMaelstromXx",
            "GalahadGod", "OnlyDrifter", "RealTemplar", "OnyxZ", "iGalahad", "iVortex",
            "GoldenBriton", "Theurg88", "Cobaltz", "Eclipse117", "xXFuryXx", "Nova360",
            "xXCrossXx", "Steel", "RogueReaper117", "VileHawk", "xXExcaliburXx", "GawainSlayer",
            "Toxic7", "SlayerOG", "Gawain13", "Edge117", "LilDragon", "xXSnareXx",
            "CamlannSlayer", "OnyxTTV", "NovaEdge42", "Steel13", "Pendragon7", "Camelotz",
            "Glitch101", "LilBlaze", "Crimson101", "Steel42", "Knight21", "DarkFriar",
            "Albion12", "xTemplar", "Camlann99", "Cross47", "xGalahad", "PercivalPro",
            "GlitchCobra", "GrimGareth", "TemplarOG", "Pendrag1337", "Briton808", "iCamelot",
            "CrownKing", "SacredSaxon", "AvalonX", "Ultra21", "JustCleric", "Cobalt13",
            "Gawain", "LethalReaper", "LogresGG", "TheurgHD", "PaladinGG", "Rapid9000",
            "VoidBeast7", "HolyOG", "Camelot", "Feral1337", "NumbFang360", "Knight",
            "Crown9000", "GawainGod", "Pellinore24", "MrKiller", "Crusader101", "Onyx777",
            "xXNinjaXx", "xXSaberXx", "NobleMordred", "KnightOG", "IronBedivere", "LilHaze",
            "Eclipse13", "Blaze99", "xXSorcererXx", "Ash1337", "Onyx23", "BigDusk",
            "xXSteelXx", "Lion", "Ember1337", "Crown23", "BaneHD", "OnlyKiller",
            "xXPercivalXx", "Iron1337", "PaladinLord", "Cobra24", "Quiet256", "TheHydra",
            "HolyX", "CrusaderX", "AshFury", "Cinder256", "Crusader24", "TheurgGod",
            "Rogue24", "LunarBolt", "SkullZ", "LethalWolf42", "Briton", "SorcererX",
            "OnlyThrone", "Wisp88", "GalahadSlayer", "CamelotZ", "Knight12", "Albion",
            "Crawler99", "xXLanceXx", "TalonPro", "Crimson9000", "EchoHD", "Grail256",
            "WildBreaker64", "RazorStriker12", "Striker23", "SableTalon", "Gareth7", "BreakerOP",
            "MadGolem", "ShieldOG", "PixelRogue", "Frost99", "Zenith23", "FatalWolf256",
            "Camlann13", "TalonZ", "xXFluxXx", "Vile360", "SavageHawk88", "Paladin",
            "MegaCobra64", "DaEclipse", "Serpentx", "EpicRage12", "Armsman24", "Theurg",
            "KnightZ", "VexGod", "Pellinore808", "AlbionZ", "MegaFang9000", "CrimsonHawk",
            "Gareth", "SorcererPro", "JustWarrior", "Shadow7", "Doom23", "Sorcerer777",
            "xXLogresXx", "xXShieldXx", "AlbionOG", "TheNyx", "BigSerpent", "GawainHD",
            "Surge23", "Mordred", "MrCrown", "MrHoly", "Saxon", "PixelWolf",
            "Flame42", "xXStrikerXx", "Logres256", "Galahad", "Bedivere808", "Hydra7",
            "Crusader", "Crawler64", "Percival", "VoidHydra1337", "WildCrawler12", "Ghost256",
            "Ozone99", "iLogres", "Lone9000", "ItsBriton", "xXTalonXx", "Sorcerer",
            "RealLion", "JustRage",
            // 500 additional digit-free gamer handles (gen_gamer_names.py, 2026-06)
            "LunarBedivere", "LethalViperOG", "MistyEclipse", "LethalPaladin", "LethalLion", "ShadowHawk",
            "BigEdge", "RabidSerpent", "SacredWispTTV", "SurgePro", "VileRazor", "UltraBriton",
            "VoidHelixOG", "MossyFlameGG", "FrozenArmsmanZ", "MegaAssassin", "FeralEclipseX", "SilentThrone",
            "SaxonPlasma", "MordredHunter", "VerdantWardenZ", "KnightSaber", "UltraBeastOG", "HyperNova",
            "FrozenFox", "CrimsonHawkPro", "xKnight", "UltraHunterHD", "ItsHelix", "FatalGolem",
            "TheHelixHD", "BigTiger", "IronFury", "SaxonCobra", "ObsidianTitanGG", "SolarPlasmaZ",
            "ExcaliburHD", "MistyBriton", "SableNovaKing", "GoldenWolfKing", "TristanOP", "iTigerPro",
            "SkullOP", "DaRazor", "AlbionCyber", "BigReaver", "xCamelot", "AbyssYT",
            "ArmsmanRiptide", "BigVortexTTV", "FriarRender", "ArthurGolem", "TheEmber", "GoldenFluxKing",
            "MrShark", "SavageTiger", "StormSerpentGG", "SniperX", "LogresCyber", "FatalBreakerGod",
            "CrusaderSurge", "AvalonGG", "FangPro", "SilentWardenPro", "OnyxAvalon", "ReaverTTV",
            "OnlyCyberPro", "VoidSaber", "MegaVortex", "xXCamlannXx", "RealBoltLord", "HolyTigerGod",
            "FrostSpecter", "MordredEdge", "RabidAssassin", "FangOP", "SilentFang", "SaxonWraith",
            "TheCamelot", "CamlannSkull", "AvalonGhost", "PaladinOP", "EpicCobraHD", "AlbionRiptide",
            "LionheartMauler", "CamelotWraith", "DaFox", "SorcererCyber", "BrutalTalonX", "LethalSaxon",
            "FatalPhoenixOP", "HelixOG", "ShadowCamlannX", "DaWraithOP", "VoidNinjaOP", "BedivereNova",
            "MerlinDragon", "LunarPlasma", "NumbKnightTTV", "TristanWraith", "CamelotGlitch", "SaxonTempest",
            "LionheartBlaze", "FrostBlaze", "OnyxHawkGG", "FeralTiger", "FluxX", "VoidTheurg",
            "SacredDoomLord", "ItsNinja", "NobleDrifter", "FatalLancelot", "SolarBreaker", "NumbLancer",
            "ToxicMaelstrom", "HyperLionheart", "IronTitan", "SolarPulseKing", "NumbMaulerGod", "xMerlin",
            "TristanRazor", "CrimsonEclipse", "CamlannCyber", "VileTigerGod", "FatalDrifter", "VoidBreakerGod",
            "WildHavocLord", "VoidViperKing", "FrostPhoenix", "BrutalViper", "VileRazorX", "SavagePhoenix",
            "BrutalMaulerGG", "FeralBriton", "SurgeKing", "SavageFox", "NobleSorcererGG", "GawainOP",
            "MegaMaulerYT", "BrutalCobra", "FrozenHydra", "ColdLogres", "EpicLancelotPro", "ToxicVortex",
            "HolyLogres", "NobleCometGod", "RabidPlasma", "MossyTitanX", "xXSpecterXx", "SavagePlasmaPro",
            "StrikerX", "ItsRogueLord", "MordredReaper", "AlbionPulse", "AshenShark", "PercivalLion",
            "IronCinderGod", "IronTheurg", "SavageHydraHD", "WildHydraOG", "RazorKing", "LethalGalahad",
            "SolarRazor", "FrostCyberZ", "FuryOG", "RapidRaven", "HyperDrifter", "OnyxSlayer",
            "AshenSaxon", "CometGG", "GoldenSlayer", "SavageWardenYT", "LilSpecter", "ShadowFoxOG",
            "NoblePulse", "WraithTTV", "SavageBedivere", "WardenGG", "FeralLogres", "TheCobraHD",
            "PercivalMauler", "ColdRogue", "LogresBane", "LancelotLion", "CrusaderHydra", "SolarAbyss",
            "VileGalahadTTV", "MistyReaverHD", "iStalkerOG", "NobleEclipse", "ItsRage", "TheFlameGG",
            "DarkVortexHD", "CyberOG", "DaAssassinX", "StormNinja", "FluxGG", "FeralReaper",
            "CrusaderTiger", "OnlyGawainYT", "GrimMauler", "FrozenTiger", "BrutalHydraOP", "LunarZenithKing",
            "AshenFlameHD", "RapidWisp", "LethalBedivere", "PellinoreEcho", "RabidWolf", "MordredSkull",
            "VoidGrailGod", "TristanEmber", "VileWolf", "OnlySniper", "ArthurBane", "ExcaliburVortex",
            "RapidAssassin", "MegaNovaPro", "SilentGrailZ", "WildCrusaderGG", "KnightCobra", "MossyRenderOP",
            "MaulerGod", "FriarZenith", "ReaverHelix", "IronStriker", "MrAvalon", "FriarPulse",
            "DarkCrawlerTTV", "FrostGlitch", "IronVortex", "OnyxBlazeZ", "PulsePro", "DarkFlux",
            "FeralPhantomTTV", "VoidSaberX", "RealSmasher", "PaladinHunter", "EpicMaelstrom", "DaTemplarOP",
            "StalkerKing", "VoidFlame", "FrostSorcerer", "BedivereLord", "NumbTempestKing", "GalahadPulse",
            "FriarSlayer", "MistyKraken", "VileArmsman", "VerdantCamlann", "RealCometZ", "OnyxMaelstromOP",
            "ToxicPercival", "HolyHawk", "SilentRazor", "ShadowSaber", "SacredNova", "NobleAvalonOG",
            "xTempest", "TemplarSmasher", "GawainPulse", "LancelotYT", "ShadowFoxGod", "SableKnightLord",
            "xXVortexXx", "CamelotKraken", "BrutalRenderHD", "PercivalFox", "MossyEcho", "OnyxBreakerX",
            "SilentMerlinYT", "LunarBriton", "SmasherPro", "RealPellinore", "GrimGlitchGG", "MossyRaven",
            "TheurgGolem", "FeralBaneZ", "TemplarGlitch", "MistyEclipseOP", "GoldenGolemKing", "FoxHD",
            "HolyEclipse", "RapidMaelstrom", "FeralFoxGG", "GrimCleric", "IronKnightOP", "ColdThroneOG",
            "SaxonAbyss", "NumbSorcerer", "FuryOP", "HyperLionheartZ", "MordredGG", "ColdAvalon",
            "CamelotMauler", "DarkRogueOG", "RabidStrikerTTV", "ViperGG", "JustBeastKing", "BigRazor",
            "HolyMordredYT", "FeralBlade", "FrostTheurg", "RiptideKing", "ColdCamlann", "ColdDoomGG",
            "ToxicRaven", "WildPaladin", "FrozenAbyssZ", "FrozenLionZ", "CamelotFang", "DarkBlazeKing",
            "DrifterOG", "ArmsmanTiger", "UltraRazor", "UltraViperZ", "FatalDoomTTV", "GrimReaver",
            "GrimMerlinZ", "xTheurgX", "HyperRazor", "GrailSurge", "CrimsonBolt", "AvalonHelix",
            "ItsSaber", "JustHelixGG", "RapidLancelot", "FeralSaxonHD", "SolarPellinore", "MossyPhoenix",
            "ShadowPercival", "TheGhost", "IronClericX", "ToxicEmber", "ObsidianMerlin", "RapidSmasher",
            "EmberTTV", "FatalSmasher", "SilentRaven", "RealReaper", "AshenSlayer", "HolyCinder",
            "ColdCrown", "ToxicWolfOG", "BigWardenYT", "HydraZ", "CrimsonFlameGG", "ColdCrusader",
            "SavageCamlannZ", "MegaBladeYT", "BritonMaelstrom", "GoldenSkullGod", "xFlameOP", "ClericPhoenix",
            "DarkBlaze", "VileFox", "LunarSorcerer", "MistySorcerer", "HawkX", "ReaperOP",
            "DarkCamlann", "SolarSpecter", "CrimsonTemplar", "iRazor", "HydraOP", "BritonFlame",
            "ItsBladeOG", "EclipseTTV", "TheSurge", "ColdPulse", "VileGrailPro", "ShadeZ",
            "GrimFlux", "FrostCyber", "RabidSaber", "BedivereRazor", "SorcererBlaze", "ClericBreaker",
            "iLancelot", "FrostTitanPro", "PendragonTiger", "CobraGG", "GrimSniperLord", "OnlyBlade",
            "FeralDrifterOP", "OnlyRavenOG", "FriarFang", "SaxonPulse", "UltraLogresZ", "VilePhantom",
            "EpicRage", "SorcererReaver", "ColdMaelstrom", "BigSmasher", "BigSniperX", "TristanTempest",
            "ArthurHelix", "VoidDrifter", "BritonStalker", "CrownHD", "WildStalkerTTV", "ArthurGG",
            "GalahadTitan", "SacredWraith", "RapidCamelot", "MossyPendragon", "SaberGod", "ItsAlbion",
            "RabidAlbion", "ClericFury", "GrimAlbion", "xBladeGG", "IronAlbion", "IronArthur",
            "EpicBriton", "SorcererSlayer", "OnyxPhoenixLord", "VileBaneKing", "xXAlbionXx", "LethalFriar",
            "ObsidianTitan", "OnyxGolemPro", "ShadowTigerOP", "OnlyHelixHD", "KnightFlux", "ClericHunter",
            "TheCleric", "GrimWardenX", "ThroneSlayer", "RealTitan", "EpicSurgeOP", "BrutalCyber",
            "FrozenShadeOP", "xStalker", "ArmsmanShark", "iSaber", "DarkWraithKing", "LancelotFang",
            "VoidWraith", "SolarBlade", "SaxonStriker", "HolyEmber", "TristanFlame", "PaladinGhost",
            "HelixLord", "MerlinWraith", "JustGolemGod", "AshenHawkZ", "MrGhostOG", "SacredZenith",
            "WildThrone", "TemplarSniper", "NobleCyberPro", "AvalonBeast", "CrimsonLion", "RabidCrawler",
            "PendragonShade", "ArmsmanEdge", "RapidBritonGG", "VerdantLancer", "ShadowCyberOG", "ItsTitan",
            "SolarTitan", "CrownBeast", "MrPhoenix", "OnlySurge", "GrailFlux", "MistySaxon",
            "GrimShade", "OnyxCobraKing", "ColdSurgeOG", "FeralCyber", "SacredSharkYT", "PaladinBeast",
            "SablePulseTTV", "JustCrownKing", "OnyxAlbionHD", "ItsSpecterPro", "AvalonHydra", "TheWolf",
            "TheurgEclipse", "RapidThrone", "DarkSmasherX", "RabidLion", "SacredSmasher", "JustSaberGod",
            "SavageSaxonKing", "RapidSmasherGG", "ReaverX", "TemplarLancer", "VortexTTV", "MistyCobra",
            "WildGalahad", "AshenComet", "MaulerYT", "LancelotOP", "DarkWispX", "GoldenStalker",
            "NumbDoomZ", "ToxicLionZ",
        };

        private static readonly string[] _hibGamer =
        {
            "DruidBane", "Grove777", "BansheePro", "iApex", "BigForest", "xXOakXx",
            "Sidhez", "Brehon", "Hydra9000", "xDagda", "xRaven", "DarkFang",
            "xGael", "xXTaraXx", "Razor99", "Eldritch23", "xXThornXx", "xXBardXx",
            "xXApexXx", "Stalker13", "Pulse101", "WildNiamh", "CometGG", "Druid",
            "DaFlux", "BardSlayer", "xXBrehonXx", "Stealth101", "MrJinx", "Briar64",
            "Lethal9000", "VerdantZ", "xXForestXx", "MrMist", "Animist47", "xXStagXx",
            "Sable24", "CobraOP", "Druid21", "Verdant", "Riff9000", "Mentalist256",
            "MirageGG", "xXSniperXx", "Tuatha7", "Bolt42", "OakTTV", "Fang13",
            "Thorn24", "ItsBolt", "DanuPro", "AbyssTTV", "VexGod", "Quill64",
            "Druid42", "Shamrock", "Blaze24", "FluxOP", "Drifter777", "Clover47",
            "xXTitanXx", "TheWisp", "SniperX", "RogueBolt", "xXAssassinXx", "Oak47",
            "GroveX", "Ember88", "LilTara", "Banshee64", "MaulerGG", "LilGrove",
            "Havoc777", "Quiet23", "MistyEmerald", "xXMentalistXx", "Iron7", "Saber117",
            "Niamh101", "ForestLord", "Ash64", "Riff12", "Willow88", "SilentFae",
            "Celt13", "Doom21", "JustStatic", "HydraPro", "Gael", "AbyssOP",
            "QuasarHD", "GaelBane", "DarkSerpent88", "KrakenHD", "BardGod", "GhostFox",
            "Nightshade", "Drifter88", "Eldritch", "Mentalist117", "Tiger21", "BigStrike",
            "TalonX", "Surge12", "SolarKraken101", "Mist12", "Faerie64", "EdgeHD",
            "EpicWolf", "LilEcho", "ZenithTTV", "ItsTuatha", "BigTempest", "Strikerz",
            "Forest1337", "Vile23", "PhantomViper", "xXBlazeXx", "xXLeafXx", "WildClover",
            "Champ12", "Tara", "Nova808", "xXGaelXx", "Cuchu13", "Tuatha",
            "Emerald99", "xXAnimistXx", "EldritchHD", "GolemZ", "StagX", "Wraithx",
            "ToxicReaper117", "xHunter", "JustKraken", "RealDusk", "Stag13", "OnlySaber",
            "TheSkull", "Banshee88", "Static42", "xXGroveXx", "xXDagdaXx", "xXBansheeXx",
            "xForest", "Tempest24", "Danu64", "xXSynthXx", "xXOzoneXx", "NovaStriker",
            "MossyMist", "OnlyClover", "Hero42", "DuskGod", "Sidhe", "DarkHero",
            "MrHelix", "TheTuatha", "CrimsonFury", "Razor64", "xXPulseXx", "ViperGod",
            "NightshadeGG", "DaFlame", "Champ101", "Specter21", "RiffGod", "Fianna",
            "Fang7", "Tuatha64", "WildErin", "VerdantBane", "xXEldritchXx", "Pulse24",
            "StrikeGG", "Mist", "Helix7", "xXMaulerXx", "StaticZ", "DuskGG",
            "xXBriarXx", "xXFiannaXx", "Cinder13", "HyperStrike", "MentalistZ", "Doom42",
            "DaBlade", "Celt", "Tuatha808", "xXDanuXx", "MistyLugh", "KillerZ",
            "DaBriar", "Lord1337", "Krypt13", "xXMirageXx", "Bard13", "xXNiamhXx",
            "Plasma256", "WildFaerie",
            // 500 additional digit-free gamer handles (gen_gamer_names.py, 2026-06)
            "ForestStalker", "HolyBeastLord", "MossyTiger", "FeralLughGG", "RageGod", "PlasmaZ",
            "IronLeaf", "LancerPro", "SacredRavenHD", "MistralMauler", "IronCyber", "ToxicTuathaKing",
            "MrTempest", "RavenOP", "AshenHawk", "MentalistKing", "VoidHawkTTV", "ShamrockTempest",
            "MistralStalker", "StormWarden", "DrifterZ", "ErinFlame", "GaelDrifter", "WildSurge",
            "DagdaWolf", "RealOak", "FaePro", "LethalViper", "ToxicDagda", "FatalWispGod",
            "BreakerOP", "GoldenDragonGG", "TheFangKing", "LilEmeraldOG", "ErinSerpent", "SolarCloverGG",
            "SilentZenithZ", "ReaperOP", "VerdantCobraX", "TheFluxTTV", "SidheRogue", "StormWisp",
            "DanuTempest", "ZenithHD", "HolyEldritch", "iFaerie", "UltraMistral", "OnlyEmerald",
            "xShark", "GhostOG", "FatalZenithLord", "SolarFlux", "PhantomKing", "DagdaAssassin",
            "JustHawkOG", "BigSaber", "IronViperOP", "TaraFlux", "ShamrockSmasher", "WillowStriker",
            "OnlySurge", "RabidLion", "BigClover", "FrostBansheeHD", "LethalBreaker", "EmberYT",
            "FaeRender", "UltraComet", "HyperSidhe", "SolarHawkZ", "StormBansheeX", "FeralCobraOG",
            "GroveStriker", "VoidFlux", "SolarWraithKing", "MistralFang", "StagYT", "OakZ",
            "SlayerKing", "ShadowCeltKing", "LunarCelt", "ColdWarden", "DaClover", "iGlitchGod",
            "LilCloverLord", "DarkShamrock", "BlazeLord", "CloverEdge", "DarkBane", "LunarGolemOG",
            "BardNova", "LilComet", "OakVortex", "FrostSniper", "VileMistralGod", "TuathaBeast",
            "ColdWraith", "OnyxFaerieLord", "ShadowFangTTV", "DaShade", "OnyxDagda", "ToxicEldritchOP",
            "FrozenGlitch", "TaraRaven", "BansheeSaber", "MistyLancer", "SablePulse", "MistralWarden",
            "EpicTempestTTV", "BreakerTTV", "VileVortex", "EpicOak", "VerdantBlaze", "TitanOG",
            "xXVortexXx", "AshenHydraGod", "CuchuHunter", "ItsTigerZ", "CloverZ", "SilentRenderTTV",
            "ThornKing", "UltraReaper", "RealBansheePro", "DanuReaver", "OakViper", "CrimsonSlayer",
            "FaerieHawk", "RabidFox", "LunarCuchuGod", "DaEmber", "NumbFae", "FrostBriar",
            "MegaSniper", "BansheeGhost", "LunarLionHD", "RapidDagdaGG", "IronFuryX", "FatalWolf",
            "GrimHydra", "SniperZ", "xXDoomXx", "IronMistral", "FrostDagda", "OakBolt",
            "DanuRaven", "FrozenCinderPro", "ForestHunter", "ShadowFiannaTTV", "IronBeast", "ItsHavoc",
            "SavageCyberX", "ForestCobra", "iMentalistPro", "ItsWarden", "HyperKraken", "CeltTTV",
            "RabidTempestTTV", "ForestViper", "iGrove", "RealSidhe", "xComet", "DruidWarden",
            "ToxicEclipse", "ShamrockVortex", "DruidRiptide", "RabidWolfGG", "RabidEclipse", "UltraSaber",
            "ErinGod", "GroveTitan", "DarkSniper", "AnimistFlame", "FaeGod", "xXHavocXx",
            "FiannaSlayer", "ShamrockTitan", "MentalistLion", "WispYT", "CrawlerHD", "LilDragon",
            "RapidSniperYT", "LethalTuathaPro", "LunarSmasher", "EpicNinjaOP", "BardAssassin", "ColdFlame",
            "xDragonGod", "DruidLion", "xXZenithXx", "BigBladeOG", "AshenDrifterOP", "NobleHydraHD",
            "BansheeGod", "xXCuchuXx", "VoidRiptide", "GroveHD", "IronEclipseX", "VerdantBriar",
            "xDoom", "ColdBansheeGG", "ErinComet", "AshenLeaf", "RageOG", "TitanTTV",
            "BrutalErinGod", "BardCinder", "RabidBolt", "VileHelix", "SharkHD", "LeafRazor",
            "IronWillow", "GrovePhantom", "IronRiptide", "OnlyErin", "StrikerLord", "iCrawler",
            "OakOG", "SacredSaberTTV", "ColdOak", "SilentEclipseGG", "LethalSmasherOG", "AshenStriker",
            "LethalMaulerOG", "WildGolem", "FaerieOP", "TheDrifterX", "SidheSpecter", "FiannaTTV",
            "FrostEclipse", "RogueOG", "UltraHavoc", "iBane", "OakShade", "NobleWardenTTV",
            "CuchuEclipse", "NightshadeRazor", "ItsPulseOG", "TheFang", "MegaBladeHD", "ThornComet",
            "WildCrawlerYT", "DarkGrove", "EmeraldLancer", "CrimsonSniper", "AnimistPulse", "LethalFaeOG",
            "GroveSlayer", "ForestYT", "OnlyFaerieX", "MistyHawkGG", "VileCobra", "StormMauler",
            "OnyxSharkX", "WildRazorGG", "LilSniper", "EpicTara", "DaFury", "OakBlade",
            "LughHD", "OnlyCometGG", "GoldenVerdant", "HunterOG", "TheGlitch", "OnyxEcho",
            "iBeastOP", "VerdantBolt", "MossyDrifterX", "TheFlame", "CloverRender", "EchoX",
            "TuathaPhantom", "SolarMaelstrom", "xSaberGG", "RealReaver", "UltraSidhe", "BardKraken",
            "RealHawk", "BardPhoenix", "SilentSkull", "SableHunter", "FrozenNova", "WildTara",
            "AshenBrehonZ", "ColdGlitchPro", "EmeraldSkull", "CloverShark", "ToxicCyber", "VileDoomLord",
            "ToxicTigerLord", "LethalViperGG", "BardX", "GoldenFaerie", "xTalon", "BrutalMistralX",
            "HolyMaelstrom", "HyperCinder", "EpicFaeGG", "ErinZ", "LilTiger", "FrozenRaven",
            "DarkCrawlerPro", "DanuSurge", "iWillow", "LunarGolem", "SolarCuchu", "ForestEdge",
            "SilentEmerald", "FrozenShark", "ItsSaberGG", "JustEcho", "VortexTTV", "UltraMaelstromX",
            "MrTalon", "ItsCuchu", "TheDoomOG", "BaneLord", "HolyWillow", "DarkSkull",
            "StormCuchuOG", "UltraCuchu", "CeltAbyss", "MentalistShade", "FaerieGhost", "SableSmasher",
            "PlasmaKing", "ReaverOG", "StagTiger", "AnimistWisp", "ObsidianFianna", "FatalDragonHD",
            "iBlaze", "CrimsonRazor", "SidheReaver", "RealSpecter", "JustPlasma", "AnimistHunter",
            "SableSerpent", "JustSlayer", "TaraStalker", "ObsidianRaven", "MrCelt", "BrutalReaverOP",
            "ForestEclipse", "MrGroveOP", "LethalLugh", "LughMaelstrom", "ShamrockBane", "FeralFaerie",
            "BigNinjaOG", "FrostCobraX", "SacredDanu", "MossyZenithOG", "ShadowRiptide", "iStagZ",
            "FatalWisp", "ShamrockBeast", "VileFang", "SolarPhoenix", "LunarEdge", "BansheeHelix",
            "NiamhRogue", "DarkBanshee", "VerdantTiger", "HyperStalkerYT", "LunarRage", "FiannaFox",
            "DanuTitan", "BaneX", "FrozenWolf", "DruidX", "VerdantFangGod", "ShadeKing",
            "FrozenSerpentOP", "BigStriker", "RageYT", "CloverSaber", "ColdSmasher", "ShadowOak",
            "LethalCobraGG", "MrVortexYT", "HelixOP", "CrimsonBlade", "OnlySidhePro", "DarkAssassin",
            "NobleHavocHD", "SaberLord", "MistyThornGod", "FrostComet", "VerdantWraith", "FrozenRageKing",
            "SidheCrawler", "MrWraith", "OnlyEdge", "GrimEclipseOP", "ShadowSurge", "RapidBane",
            "iLughOG", "MegaFlux", "ErinLord", "GaelFang", "HyperHunterKing", "JustFaerie",
            "SavageRiptide", "ObsidianHavocX", "StagStalker", "StagBreaker", "JustSidheYT", "AshenGaelX",
            "BrutalZenith", "GaelHawk", "SacredFoxKing", "SolarMistral", "SableStagX", "SilentWolfOG",
            "BigLancerYT", "WispLord", "ShadowDanu", "GoldenGrove", "BigDruid", "CinderGG",
            "LunarSpecter", "SavageFiannaHD", "BriarFlame", "xXLionXx", "RabidSniperGG", "xVortexX",
            "ColdBardPro", "DaComet", "BardMaelstrom", "SableDanu", "JustSharkX", "RabidDagdaGod",
            "StormAnimist", "SolarNiamh", "DarkHawk", "OnyxVerdantTTV", "DarkEmerald", "OnlyCelt",
            "VerdantCuchuGG", "FeralEcho", "ForestSniper", "LunarCrawler", "LughSerpent", "EpicBladeYT",
            "RabidWraith", "HolyNinja", "MrReaper", "EpicDrifter", "ShadowSharkGod", "OnlyKrakenGod",
            "MegaBrehonX", "BrutalGlitch", "SableEmberOP", "GrimBlade", "ErinCinder", "NumbBane",
            "BrehonTTV", "xHavoc", "ErinBane", "ThornCinder", "MegaTara", "LunarFlux",
            "EmberZ", "SilentSerpentGG", "OnlyWarden", "BriarAssassin", "LilTaraYT", "DagdaPhoenix",
            "ToxicWolfOP", "LilOakOG", "OnlyShamrock", "VoidNova", "LunarPhoenix", "BardFox",
            "ThePhoenixLord", "MegaHavocKing", "xXTuathaXx", "MegaShark", "ErinTitan", "ForestMauler",
            "RapidPlasmaYT", "HolyFuryGG", "DarkCelt", "OnlyEclipse", "iSidheGG", "MegaVortex",
            "MegaStriker", "SidheBane", "MistyTempestGG", "IronRogueZ", "GrimSurge", "StormTuatha",
            "LunarBreaker", "GoldenStalker", "VileEmberZ", "ShamrockSpecter", "EmeraldTempest", "ItsWispLord",
            "MegaKraken", "MegaFiannaOP",
        };

        private static readonly string[] _midGamer =
        {
            "Zenith99", "NovaHD", "LilStrike", "Fenrir1337", "xXSnareXx", "VidarGod",
            "Ghost23", "Warrior7", "Surtr12", "GrimGarm", "ValkyrPro", "MrAxe",
            "Bear", "OnlyBifrost", "KrakenOG", "xXSleipnirXx", "EmberLord101", "DemonOG",
            "Loki9000", "Jinx21", "UltraHunter13", "Valkyr", "RealViking", "Ash808",
            "xXValkyrXx", "Niflheim", "MjolnirGod", "HollowSniper", "Tyr", "GrimHammer",
            "Ragnar99", "DaKrypt", "Einherjar23", "xXRageXx", "Bonedancer", "LilRiptide",
            "FrostHealer", "BonedancerX", "AshDrifter256", "Blazez", "xXHammerXx", "Sniperz",
            "Onyx256", "Mjolnir12", "xXBreakerXx", "BloodEinherjar", "Talon777", "VileSlayer",
            "Wolf1337", "RunemasterGG", "Savage23", "OnlySlayer", "xThane", "DarkFrost",
            "xBane", "Sleipnirz", "Ymir", "Blood64", "VoidLord101", "Odin1337",
            "xXThorXx", "Toxic808", "JustBear", "ItsAbyss", "Ymirx", "ItsDrifter",
            "Rune47", "RuneX", "Maelstrom808", "DrifterGG", "RabidSniper47", "Serpent7",
            "RogueHD", "Synth808", "Bane7", "GhostSage99", "VikingBane", "FrostSkald",
            "IronSavage", "DaEclipse", "DaRaven", "ColdFenrir", "Rapid117", "ObsidianGod",
            "Savage", "Hunter99", "Eclipse117", "Axe99", "Ragnar117", "RogueTTV",
            "xXVidarXx", "Cinder88", "ObsidianGG", "VoidTitan", "AxePro", "SavageBane",
            "NumbWraith23", "OdinPro", "xXHawkXx", "SleipnirHD", "DaHunter", "Specter13",
            "Runemaster13", "LunarSkull42", "SavageSlayer", "Spiritmaster", "Wolf21", "BloodFrost",
            "iMaster", "NovaKnight1337", "Wisp88", "SteelNinja", "ItsDragon", "Fatal1337",
            "OdinYT", "Warrior23", "Axe", "RealShark", "Zephyr7", "GarmSlayer",
            "Assassinz", "BifrostGod", "Mjolnirz", "RabidViper12", "Garm808", "xZephyr",
            "GarmHD", "IronBifrost", "Echo360", "Ozone99", "HunterHD", "DaBolt",
            "GarmKing", "xXMjolnirXx", "SniperTTV", "Rune", "Draugr", "Specter777",
            "Frost", "MjolnirOG", "Skald256", "BladeGG", "Draugr9000", "Midgard",
            "MegaClaw", "xXStaticXx", "FluxTTV", "BonedancerBane", "FrozenHawk", "xXRelicXx",
            "CinderOG", "SniperX", "xXGarmXx", "CrimsonHawk101", "Jotun", "Synth12",
            "Cyber117", "FenrirGod", "LilBifrost", "Healer12", "OnyxSlayer9000", "DarkAxe",
            "Dark64", "Mjolnir9000", "Surgez", "Heimdall", "ValkyrX", "StormNiflheim",
            "Rage360", "Synth256", "LokiPro", "SavageFenrir", "RealNorse", "Savage99",
            "Warlock42", "FrostX", "Jinx256", "HunterTTV", "Tempest1337", "Vikingx",
            "RuneSlayer", "iSkald", "LilDemon", "IronStriker777", "Einherjar13", "Numb1337",
            "xValkyr", "xXVortexXx", "BrutalPhoenix", "SkullX", "xXBonedancerXx", "LilMidgard",
            "Odin21", "Sleipnir42", "RunemasterPro", "xXRagnarXx", "Haze9000", "HexKnight23",
            "xXVikingXx", "MrPulsar",
            // 500 additional digit-free gamer handles (gen_gamer_names.py, 2026-06)
            "DarkGhostYT", "BigValhalla", "FenrirBane", "NorseKraken", "DaMjolnirZ", "ColdDrifter",
            "MossyCyberGG", "ItsSlayerOP", "FeralFluxTTV", "SolarFlame", "NobleDragonZ", "RabidBane",
            "VoidThorZ", "RabidRageLord", "NobleSmasher", "GarmDragon", "TheNorseLord", "BerserkFox",
            "xShadeGod", "JustAssassinYT", "xWispGG", "RapidSaber", "MegaWardenZ", "BonedancerLord",
            "JustOdinGod", "FatalGarmGG", "SkaldFang", "StormSkald", "HolyFluxYT", "EpicMaulerGG",
            "DaSpecter", "ValkyrStriker", "ValkyrFox", "AshenTitan", "BifrostDoom", "VerdantPulse",
            "SavageViper", "CrimsonBane", "SavageGhost", "iZenithYT", "BigRogue", "LilStriker",
            "StormGolemGod", "SableSurtrX", "LokiGhost", "ItsDrifterGG", "SolarViper", "RapidWraith",
            "VerdantSharkGod", "VileHydra", "WildAssassin", "LilMjolnir", "HolyRazor", "EpicBolt",
            "OnyxHawk", "MossyGarmOG", "WarriorNova", "FangKing", "NumbViking", "MistyHawk",
            "RagnarBreaker", "UltraDrifter", "NumbStriker", "FatalFlameLord", "SkaldShark", "OnyxFlux",
            "SleipnirRage", "OdinFox", "BrutalSaberYT", "GoldenStalker", "ShadowDraugr", "FeralBeastGod",
            "LokiSlayer", "StormPulseGG", "SacredEchoOG", "FrozenFang", "StormGolem", "StormFrostbite",
            "TheNorseKing", "NumbSkull", "TitanZ", "RabidHavoc", "GrimCyberKing", "GrimEcho",
            "xCinder", "RealHunter", "FatalAbyss", "xStalkerGG", "SilentLancer", "IronLancer",
            "JustGlitch", "VileDragonGG", "VerdantZenithHD", "JustSpecterOP", "OnyxZenithGod", "TyrWraith",
            "MossyDrifter", "SilentEcho", "LilRage", "VidarBlade", "SolarFrostbite", "SavageCinderX",
            "EinherjarLancer", "RealLokiGG", "OnlyTitan", "RapidJotun", "VileFoxGod", "LancerPro",
            "ValhallaSlayer", "RealBladeLord", "SolarBolt", "FatalStalker", "UltraSurge", "OnyxAesir",
            "ColdAsgard", "BigBonedancerOG", "MegaEinherjar", "OnlySniperX", "OnlyAsgardKing", "NobleSkullGG",
            "VileAssassin", "DraugrSerpent", "BrutalBane", "HyperLionX", "xHelix", "DaSurgeZ",
            "ShadowNorse", "HyperValhalla", "VikingEclipse", "SniperGG", "JustSkaldOG", "ValhallaAbyss",
            "ObsidianEdge", "TheThor", "AbyssYT", "EclipseGG", "VerdantSaberGG", "SavageCrawler",
            "FrostAesir", "GarmEdge", "VoidTigerOP", "OnyxZenith", "NumbEclipseKing", "ShadowNorseOP",
            "RabidBonedancer", "FeralNiflheim", "SpecterLord", "ColdBreaker", "ThaneComet", "LunarEclipse",
            "BigRagnarKing", "iDoom", "MistyEclipse", "OnyxRogue", "WarriorCyber", "MossySurgeLord",
            "AshenLion", "UltraShade", "ThanePhantom", "MegaEdge", "WildStalkerOP", "RabidNinja",
            "OnyxPhoenix", "iOdinTTV", "BrutalThane", "MegaBaneKing", "ShadowHunter", "FatalHydraLord",
            "EpicSpecter", "RogueGod", "FeralFlame", "RapidDraugrTTV", "BrutalSkull", "OdinDragon",
            "NobleNorse", "GrimMaelstrom", "ThorSlayer", "MistyCobraX", "VileCyber", "RapidRage",
            "BigCyberHD", "FrostbiteTiger", "SleipnirGG", "OdinBlade", "FenrirVortex", "StalkerGod",
            "NobleHavoc", "DraugrKing", "IronEchoLord", "OnyxSlayer", "FrostWraith", "MjolnirOP",
            "FatalBoltHD", "LilMauler", "xOdinOP", "SolarAsgard", "TheCobra", "FatalStriker",
            "BigSkald", "FeralJotunOG", "SavageValhalla", "VidarWraith", "WildBerserk", "VileRagnarGod",
            "WardenPro", "GoldenCometGod", "VoidAsgardPro", "VileEclipse", "MossyAsgard", "RuneGG",
            "iGlitchHD", "FangZ", "SavageBeast", "VileCobra", "VerdantDoomPro", "MaulerHD",
            "ValkyrHawk", "ToxicThorOG", "UltraHydraX", "LilFrostbite", "CrimsonRogue", "DarkDrifter",
            "IronEdge", "GoldenPhoenix", "VoidTalonGod", "BonedancerHavoc", "BrutalCobra", "HyperEcho",
            "ColdZenith", "SacredSmasher", "VileSlayerOG", "RunemasterBeast", "HyperBifrost", "TalonPro",
            "SableEclipseTTV", "HeimdallOG", "BerserkOP", "OdinPulse", "VileWraithOP", "HunterX",
            "RealWolfZ", "xFoxHD", "FrostbiteDragon", "BerserkCobra", "NorseFlux", "FrostRogue",
            "HeimdallVortex", "WildLancer", "StormSaber", "RuneReaper", "AesirWarden", "StalkerHD",
            "EinherjarHelix", "iSleipnir", "NumbGarm", "HyperRogue", "DarkSurge", "ValhallaSurge",
            "BrutalAsgard", "RapidGolemKing", "WildFenrir", "SacredYmir", "FlameKing", "CrimsonRageTTV",
            "GrimEclipse", "BrutalTigerZ", "EpicMaulerTTV", "StormWardenZ", "ObsidianLionGod", "LunarViper",
            "DarkPulseX", "RabidBlade", "RabidRiptide", "SurtrBane", "HolyGhostGG", "VidarWolf",
            "LethalKrakenHD", "DarkWarden", "RabidFuryGG", "HolyZenith", "BanePro", "SableEmber",
            "MistyCrawlerZ", "VoidGlitch", "BrutalFangKing", "MrSpecter", "UltraDoomHD", "NumbComet",
            "EpicRagnar", "IronYmirGG", "iBifrost", "MossyEmber", "SilentWisp", "FrostRenderLord",
            "FrozenRaven", "SacredEcho", "TheJotun", "ItsRunemaster", "JustCobra", "ValkyrOP",
            "RapidSlayer", "iSaberLord", "UltraThaneTTV", "VileRender", "MegaWispGG", "iMauler",
            "FrostBlaze", "DaVortex", "TheRage", "HydraPro", "BerserkHelix", "ItsAbyssX",
            "SableSkald", "SolarSerpentYT", "MossyEchoGG", "LilOdinGG", "DraugrOP", "ObsidianWarden",
            "StormThorLord", "CrimsonViperYT", "SkaldSpecter", "ShadeHD", "LethalThaneOP", "UltraMaelstrom",
            "RapidBreakerYT", "BigWarrior", "BigShadeHD", "SacredEmberGod", "MjolnirBolt", "MegaReaverKing",
            "ItsSerpent", "VortexPro", "HeimdallLion", "AshenHeimdallGG", "TitanX", "EpicSpecterOG",
            "GrimPulse", "UltraRune", "JustMauler", "OnyxFenrir", "FrostLion", "VerdantFenrir",
            "HolyBaneKing", "FatalYmirX", "SurtrHawk", "HolyDoom", "FenrirShark", "SacredAesirTTV",
            "UltraFuryX", "EpicGarm", "xAbyss", "BigShade", "AshenAbyss", "NumbBladeKing",
            "RapidKrakenHD", "EinherjarEcho", "RunemasterFlame", "LilHydra", "VoidNiflheim", "HyperFenrirGod",
            "RapidRaven", "RabidAbyss", "WarriorRazor", "MrMidgard", "IronVidar", "CrimsonEdgeLord",
            "MegaGarm", "AshenTyrGod", "RogueOG", "EmberOP", "UltraDragonYT", "ValkyrDrifter",
            "HyperComet", "ItsFoxOG", "TyrShade", "BerserkMauler", "LunarRagnar", "SurgeYT",
            "ThorHunter", "StormFury", "RagnarX", "VoidBifrostZ", "AshenViking", "NobleAssassinHD",
            "ShadowFury", "UltraSharkPro", "EpicSurge", "SolarRenderTTV", "FeralLionGG", "JotunSlayer",
            "MrJotun", "IronSaber", "SleipnirGod", "MegaBreakerGod", "RabidCinderGG", "VoidWarriorZ",
            "RapidSurtr", "FatalAsgard", "FeralSpecter", "SacredNorse", "DarkDragon", "JustRage",
            "FrostLoki", "MegaAssassin", "SacredSaber", "SilentSleipnir", "ItsFuryPro", "MrBane",
            "UltraSleipnir", "DraugrEcho", "JustValhalla", "WildSurtrX", "FenrirOP", "ValkyrHavoc",
            "EpicDragon", "WarriorReaver", "GarmEmber", "HolyTyrPro", "SolarHawk", "xLion",
            "xXYmirXx", "VoidHawk", "SableReaper", "BonedancerOP", "FeralThor", "FeralTempest",
            "WildStrikerPro", "AesirRender", "JustSurge", "TyrWisp", "DraugrWolf", "VidarEmber",
            "LionZ", "SkaldFlux", "UltraDraugr", "ObsidianSkullOG", "SolarRunemaster", "BifrostGlitch",
            "DoomGod", "FenrirReaver", "iTigerZ", "NobleTempest", "MidgardShade", "FenrirAssassin",
            "EpicSharkGod", "MrCyberYT", "SavageRageTTV", "JustBlazeLord", "RealValkyr", "BigFuryOG",
            "ShadowEmber", "RagnarBeast", "LunarFluxLord", "HyperFluxKing", "ValhallaTempest", "HyperRaven",
            "EpicTyr", "LunarBifrostHD", "FrostbiteEdge", "RabidWraithGG", "IronRenderGod", "MossyStriker",
            "YmirLancer", "ValhallaPro", "FrozenSleipnir", "WolfX", "HyperValkyrLord", "AshenSlayer",
            "MrEmber", "JustSaberKing", "ObsidianThorZ", "CobraX", "ShadowWolf", "FenrirSlayer",
            "GoldenBlazeKing", "AesirFang", "TheCyber", "OnyxRagnar", "xViper", "DarkLionOG",
            "iBane", "RuneDoom", "EinherjarGG", "iComet", "GrimEclipseYT", "TyrAbyss",
            "BrutalWraith", "SavageHawk", "VidarGlitch", "IronAsgard", "RapidBane", "ToxicCrawler",
            "UltraHydra", "BrutalAssassin", "MegaSlayer", "MossyBaneYT", "SilentSkaldLord", "GoldenAesir",
            "VikingCyber", "SleipnirReaver",
        };

        public static string GetName(eGender gender, eRealm realm)
        {
            // Half the time, hand out a gender-neutral gamer handle so the
            // roster reads like a live server population rather than a wall of
            // lore first names. The other half keeps the period-appropriate,
            // gender-truthful first names below.
            if (Util.Chance(50))
            {
                string[] handles = realm switch
                {
                    eRealm.Albion   => _albGamer,
                    eRealm.Hibernia => _hibGamer,
                    eRealm.Midgard  => _midGamer,
                    _ => Array.Empty<string>()
                };

                if (handles.Length > 0)
                    return handles[Util.Random(handles.Length - 1)];
            }

            string[] names = realm switch
            {
                eRealm.Albion   => gender == eGender.Male ? _albMale : _albFemale,
                eRealm.Hibernia => gender == eGender.Male ? _hibMale : _hibFemale,
                eRealm.Midgard  => gender == eGender.Male ? _midMale : _midFemale,
                _ => Array.Empty<string>()
            };

            if (names.Length == 0)
                return "Mimic";

            return names[Util.Random(names.Length - 1)];
        }
    }
}
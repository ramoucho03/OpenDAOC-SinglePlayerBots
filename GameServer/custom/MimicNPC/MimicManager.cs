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

        // Thidranki: classic 20-24 battleground (region 252).
        public static MimicBattleground ThidBattleground;
        // Caledonia: 1-50 battleground (region 249).
        public static MimicBattleground CaledoniaBattleground;
        // Molvik: 35-39 battleground (region 165).
        public static MimicBattleground MolvikBattleground;

        // Drives the player-presence check that auto-spawns / auto-clears the
        // BG mimic populations every minute.
        private static ECSGameTimer _bgPresenceTimer;
        private const int BG_PRESENCE_CHECK_MS = 60_000;
        private const int BG_BOTS_PER_REALM_WHEN_PLAYERS = 20;

        public static void Initialize()
        {
            // We spawn 20 bots / realm with the existing fixed-pool MimicBattleground
            // engine — it requires min/max totals so we use 60 / 60 to lock the pop.
            ThidBattleground = new MimicBattleground(252,
                                                    new Point3D(37200, 51200, 3950),
                                                    new Point3D(19820, 19305, 4050),
                                                    new Point3D(53300, 26100, 4270),
                                                    60, 60, 20, 24);

            CaledoniaBattleground = new MimicBattleground(249,
                                                    new Point3D(37200, 51200, 3950),
                                                    new Point3D(19820, 19305, 4050),
                                                    new Point3D(53300, 26100, 4270),
                                                    60, 60, 45, 50);

            MolvikBattleground = new MimicBattleground(165,
                                                    new Point3D(37200, 51200, 3950),
                                                    new Point3D(19820, 19305, 4050),
                                                    new Point3D(53300, 26100, 4270),
                                                    60, 60, 35, 39);

            // Start the player-presence loop. Each minute we check each BG region:
            //   - if a player is present and the BG is dormant → Start (spawns 20/realm)
            //   - if no player and the BG is running         → Clear (delete all bots)
            _bgPresenceTimer = new ECSGameTimer(null, BgPresenceTick, BG_PRESENCE_CHECK_MS);
            _bgPresenceTimer.Start();

            log.Info("MimicBattlegrounds initialized with player-presence auto-spawn.");
        }

        private static int BgPresenceTick(ECSGameTimer timer)
        {
            try
            {
                UpdateBgPresence(ThidBattleground, 252);
                UpdateBgPresence(CaledoniaBattleground, 249);
                UpdateBgPresence(MolvikBattleground, 165);
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

                if (m_albSpawner != null)
                {
                    foreach (MimicNPC mimic in m_albSpawner.Mimics)
                        mimic.Delete();

                    m_albSpawner.Delete();
                    m_albSpawner = null;
                }

                if (m_hibSpawner != null)
                {
                    foreach (MimicNPC mimic in m_hibSpawner.Mimics)
                        mimic.Delete();

                    m_hibSpawner.Delete();
                    m_hibSpawner = null;
                }

                if (m_midSpawner != null)
                {
                    foreach (MimicNPC mimic in m_midSpawner.Mimics)
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

                int totalMimics = m_albSpawner.Mimics.Count + m_hibSpawner.Mimics.Count + m_midSpawner.Mimics.Count;
                log.Info("Alb: " + m_albSpawner.Mimics.Count + "/" + m_currentMaxAlb);
                log.Info("Hib: " + m_hibSpawner.Mimics.Count + "/" + m_currentMaxHib);
                log.Info("Mid: " + m_midSpawner.Mimics.Count + "/" + m_currentMaxMid);
                log.Info("Total Mimics: " + totalMimics + "/" + m_currentMaxTotalMimics);

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

                foreach (MimicNPC mimic in m_albSpawner.Mimics)
                {
                    if (mimic != null && mimic.ObjectState == GameObject.eObjectState.Active && mimic.ObjectState != GameObject.eObjectState.Deleted)
                        masterList.Add(mimic);
                }

                foreach (MimicNPC mimic in m_hibSpawner.Mimics)
                {
                    if (mimic != null && mimic.ObjectState == GameObject.eObjectState.Active && mimic.ObjectState != GameObject.eObjectState.Deleted)
                        masterList.Add(mimic);
                }

                foreach (MimicNPC mimic in m_midSpawner.Mimics)
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

        public static void SetWeaponROG(GameLiving living, eRealm realm, eCharacterClass charClass, byte level, eObjectType objectType, eInventorySlot slot, eDamageType damageType)
        {
            DbItemTemplate itemToCreate = new GeneratedUniqueItem(false, realm, charClass, level, objectType, slot, damageType);

            GameInventoryItem item = GameInventoryItem.Create(itemToCreate);
            living.Inventory.AddItem(slot, item);
        }

        public static void SetArmorROG(GameLiving living, eRealm realm, eCharacterClass charClass, byte level, eObjectType objectType)
        {
            for (int i = Slot.HELM; i <= Slot.ARMS; i++)
            {
                if (i == Slot.JEWELRY || i == Slot.CLOAK)
                    continue;

                eInventorySlot slot = (eInventorySlot)i;
                DbItemTemplate itemToCreate = new GeneratedUniqueItem(false, realm, charClass, level, objectType, slot);

                GameInventoryItem item = GameInventoryItem.Create(itemToCreate);

                living.Inventory.AddItem(slot, item);
            }
        }

        public static void SetJewelryROG(GameLiving living, eRealm realm, eCharacterClass charClass, byte level, eObjectType objectType)
        {
            for (int i = Slot.JEWELRY; i <= Slot.RIGHTRING; i++)
            {
                if (i is Slot.TORSO or Slot.LEGS or Slot.ARMS or Slot.FOREARMS or Slot.SHIELD)
                    continue;

                eInventorySlot slot = (eInventorySlot)i;
                DbItemTemplate itemToCreate = new GeneratedUniqueItem(false, realm, charClass, level, objectType, slot);

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
            DbItemTemplate itemToCreate = new GeneratedUniqueItem(false, realm, charClass, level, objectType, slot, instrumentType);

            GameInventoryItem item = GameInventoryItem.Create(itemToCreate);
            living.Inventory.AddItem(slot, item);
        }

        public static void SetMeleeWeapon(IGamePlayer player, eObjectType weapType, eHand hand, eWeaponDamageType damageType = 0)
        {
            int min = Math.Max(1, player.Level - 6);
            int max = Math.Min(51, player.Level + 4);

            IList<DbItemTemplate> itemList;

            itemList = GameServer.Database.SelectObjects<DbItemTemplate>(DB.Column("Level").IsGreaterOrEqualTo(min).And(
                                                                       DB.Column("Level").IsLessOrEqualTo(max).And(
                                                                       DB.Column("Object_Type").IsEqualTo((int)weapType).And(
                                                                       DB.Column("Realm").IsEqualTo((int)player.Realm)).And(
                                                                       DB.Column("IsPickable").IsEqualTo(1)))));
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
                    DbItemTemplate itemTemplate = itemsToKeep[Util.Random(itemsToKeep.Count - 1)];
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

            eInventorySlot fallbackSlot = hand switch
            {
                eHand.twoHand => eInventorySlot.TwoHandWeapon,
                eHand.leftHand => eInventorySlot.LeftHandWeapon,
                _ => eInventorySlot.RightHandWeapon,
            };
            SetWeaponROG((GameLiving)player,
                player.Realm,
                (eCharacterClass)player.CharacterClass.ID,
                (byte)player.Level,
                weapType,
                fallbackSlot,
                (eDamageType)(byte)damageType);
        }

        public static void SetRangedWeapon(IGamePlayer player, eObjectType weapType)
        {
            int min = Math.Max(1, player.Level - 6);
            int max = Math.Min(51, player.Level + 3);

            IList<DbItemTemplate> itemList;
            itemList = GameServer.Database.SelectObjects<DbItemTemplate>(DB.Column("Level").IsGreaterOrEqualTo(min).And(
                                                                       DB.Column("Level").IsLessOrEqualTo(max).And(
                                                                       DB.Column("Object_Type").IsEqualTo((int)weapType).And(
                                                                       DB.Column("Item_Type").IsEqualTo(13).And(
                                                                       DB.Column("Realm").IsEqualTo((int)player.Realm)).And(
                                                                       DB.Column("IsPickable").IsEqualTo(1))))));

            if (itemList.Count != 0)
            {
                DbItemTemplate itemTemplate = itemList[Util.Random(itemList.Count - 1)];
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
                (byte)player.Level,
                weapType,
                eInventorySlot.DistanceWeapon,
                eDamageType.Slash);
        }

        public static void SetShield(IGamePlayer player, int shieldSize)
        {
            if (shieldSize < 1)
                return;

            int min = Math.Max(1, player.Level - 6);
            int max = Math.Min(51, player.Level + 3);

            IList<DbItemTemplate> itemList;

            itemList = GameServer.Database.SelectObjects<DbItemTemplate>(DB.Column("Level").IsGreaterOrEqualTo(min).And(
                                                                       DB.Column("Level").IsLessOrEqualTo(max).And(
                                                                       DB.Column("Object_Type").IsEqualTo((int)eObjectType.Shield).And(
                                                                       DB.Column("Realm").IsEqualTo((int)player.Realm)).And(
                                                                       DB.Column("Type_Damage").IsEqualTo(shieldSize).And(
                                                                       DB.Column("IsPickable").IsEqualTo(1))))));

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

            IList<DbItemTemplate> itemList;

            itemList = GameServer.Database.SelectObjects<DbItemTemplate>(DB.Column("Level").IsGreaterOrEqualTo(min).And(
                                                                       DB.Column("Level").IsLessOrEqualTo(max).And(
                                                                       DB.Column("Object_Type").IsEqualTo((int)armorType).And(
                                                                       DB.Column("Realm").IsEqualTo((int)player.Realm)).And(
                                                                       DB.Column("IsPickable").IsEqualTo(1)))));

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

            IList<DbItemTemplate> itemList;
            itemList = GameServer.Database.SelectObjects<DbItemTemplate>(DB.Column("Level").IsGreaterOrEqualTo(min).And(
                                                                       DB.Column("Level").IsLessOrEqualTo(max).And(
                                                                       DB.Column("Object_Type").IsEqualTo((int)weapType).And(
                                                                       DB.Column("DPS_AF").IsEqualTo((int)instrumentType).And(
                                                                       DB.Column("Realm").IsEqualTo((int)player.Realm)).And(
                                                                       DB.Column("IsPickable").IsEqualTo(1))))));

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

            IList<DbItemTemplate> itemList;
            List<DbItemTemplate> cloakList = new List<DbItemTemplate>();
            List<DbItemTemplate> jewelryList = new List<DbItemTemplate>();
            List<DbItemTemplate> ringList = new List<DbItemTemplate>();
            List<DbItemTemplate> wristList = new List<DbItemTemplate>();
            List<DbItemTemplate> neckList = new List<DbItemTemplate>();
            List<DbItemTemplate> waistList = new List<DbItemTemplate>();

            itemList = GameServer.Database.SelectObjects<DbItemTemplate>(DB.Column("Level").IsGreaterOrEqualTo(min).And(
                                                                       DB.Column("Level").IsLessOrEqualTo(max).And(
                                                                       DB.Column("Object_Type").IsEqualTo((int)eObjectType.Magical).And(
                                                                       DB.Column("Realm").IsEqualTo((int)player.Realm)).And(
                                                                       DB.Column("IsPickable").IsEqualTo(1)))));
            if (itemList.Count != 0)
            {
                foreach (DbItemTemplate template in itemList)
                {
                    if (template.Item_Type == Slot.CLOAK)
                    {
                        template.Color = Util.Random((Enum.GetValues(typeof(eColor)).Length));
                        cloakList.Add(template);
                    }
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
                        AddItem(player, itemTemplate);
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
                    cloak.Color = Util.Random((Enum.GetValues(typeof(eColor)).Length));
                    AddItem(player, cloak);
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

        private static void AddItem(IGamePlayer player, DbItemTemplate itemTemplate, eHand hand = eHand.None)
        {
            if (itemTemplate == null)
                log.Info("itemTemplate in AddItem is null");

            DbInventoryItem item = GameInventoryItem.Create(itemTemplate);

            if (item != null)
            {
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
        public static string SpecName;
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
                case eMimicClass.Warden: return new WardenSpec(spec);

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
                case eMimicClass.Warrior: return new WarriorSpec();
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

        public static string GetName(eGender gender, eRealm realm)
        {
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
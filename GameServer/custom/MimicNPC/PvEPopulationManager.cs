using DOL.AI.Brain;
using DOL.GS.Commands;
using DOL.GS.PacketHandler;
using DOL.GS.ServerProperties;
using DOL.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace DOL.GS.Scripts
{
    // ============================================================================
    // PvE Population Manager
    // ----------------------------------------------------------------------------
    // Spawns and maintains a configurable PvE bot population to make the world
    // feel inhabited without overloading the server. Symmetric to
    // [[PvPFrontierManager]] but focused on capitals + leveling zones.
    //
    // Two spawn flavors:
    //   - CitySpawnPoint  : N solo idle bots in a capital. State = CITY_IDLE.
    //                       AggroLevel = 0 so they don't fight.
    //   - FieldSpawnPoint : a group of K combat-capable bots at a camp point.
    //                       Same brain as a player-built /mgroup, but autonomous.
    //
    // Hibernation (Phase 5): each spawn tracks the last tick a real player was
    // observed in its region. After PVEPOP_HIBERNATE_MIN minutes without anyone
    // in the region, all bots tied to that spawn are despawned. They respawn
    // automatically the moment a player re-enters.
    //
    // Activation: gated by ServerProperty `pvepop_enabled`. Default off — admin
    // turns it on once they're happy with the configuration.
    // ============================================================================

    #region Server properties

    public static class PvEPopulationProperties
    {
        [ServerProperty("pvepop", "pvepop_enabled",
            "Master switch for the PvE population system. When false, no bots are spawned/managed.", false)]
        public static bool PVEPOP_ENABLED;

        [ServerProperty("pvepop", "pvepop_autostart",
            "Auto-start the population manager on server boot when pvepop_enabled is true.", true)]
        public static bool PVEPOP_AUTOSTART;

        [ServerProperty("pvepop", "pvepop_city_bots_per_realm",
            "Idle bots maintained in each capital city (Camelot/Jordheim/Tir na Nog).", 30)]
        public static int PVEPOP_CITY_BOTS_PER_REALM;

        [ServerProperty("pvepop", "pvepop_field_group_size",
            "Size of each field camping group.", 6)]
        public static int PVEPOP_FIELD_GROUP_SIZE;

        [ServerProperty("pvepop", "pvepop_hibernate_min",
            "Minutes without any real player in the region before the spawn is hibernated (bots despawned, snapshot kept).", 15)]
        public static int PVEPOP_HIBERNATE_MIN;
    }

    #endregion

    #region Manager

    public static class PvEPopulationManager
    {
        private static readonly Logger log = LoggerManager.Create(MethodBase.GetCurrentMethod().DeclaringType);

        // Spawn descriptors. The defaults populate capitals plus a handful of
        // classic leveling zones. Admins can append more via SQL or RegisterX.
        public sealed class CitySpawnPoint
        {
            public eRealm Realm;
            public ushort Region;
            public Point3D Center;
            public int Radius;        // bots scatter in [-Radius, +Radius] from Center
            public int TargetBots;
            public List<MimicNPC> Bots = new();
            public long LastPlayerSeenTick;
            public bool Hibernated;
        }

        public sealed class FieldSpawnPoint
        {
            public eRealm Realm;
            public ushort Region;
            public Point3D Center;
            public byte LevelMin;
            public byte LevelMax;
            public int GroupSize;
            public List<MimicNPC> Bots = new();
            public Group BotGroup;
            public long LastPlayerSeenTick;
            public bool Hibernated;
        }

        private static readonly List<CitySpawnPoint> _cities = new();
        private static readonly List<FieldSpawnPoint> _fields = new();
        private static readonly object _lock = new();

        private static ECSGameTimer _tickTimer;
        private static bool _running;
        private const int TICK_MS = 5000;

        public static bool IsRunning => _running;
        public static int TotalLiveBots
        {
            get
            {
                int total = 0;
                lock (_lock)
                {
                    foreach (var c in _cities) total += c.Bots.Count;
                    foreach (var f in _fields) total += f.Bots.Count;
                }
                return total;
            }
        }

        public static void Initialize()
        {
            BuildDefaultConfig();

            if (PvEPopulationProperties.PVEPOP_ENABLED && PvEPopulationProperties.PVEPOP_AUTOSTART)
            {
                Start();
                log.Info("PvEPopulationManager auto-started.");
            }
            else
            {
                log.Info("PvEPopulationManager initialized but not started. Set pvepop_enabled=true and run /pvepop start.");
            }
        }

        /// <summary>
        /// Default spawn layout. Coordinates are approximate centers of each
        /// capital and a handful of leveling zones. The numbers add up to ~300
        /// total bots across 3 realms when defaults are kept.
        /// </summary>
        private static void BuildDefaultConfig()
        {
            int cityBots = PvEPopulationProperties.PVEPOP_CITY_BOTS_PER_REALM;
            int groupSize = PvEPopulationProperties.PVEPOP_FIELD_GROUP_SIZE;

            lock (_lock)
            {
                _cities.Clear();
                _fields.Clear();

                // ---- Capitales ----
                _cities.Add(new CitySpawnPoint
                {
                    Realm = eRealm.Albion,
                    Region = 10, // Camelot City
                    Center = new Point3D(33_000, 32_000, 5_000),
                    Radius = 1_500,
                    TargetBots = cityBots,
                });
                _cities.Add(new CitySpawnPoint
                {
                    Realm = eRealm.Midgard,
                    Region = 101, // Jordheim
                    Center = new Point3D(31_000, 30_000, 8_700),
                    Radius = 1_500,
                    TargetBots = cityBots,
                });
                _cities.Add(new CitySpawnPoint
                {
                    Realm = eRealm.Hibernia,
                    Region = 201, // Tir na Nog
                    Center = new Point3D(32_000, 27_000, 7_700),
                    Radius = 1_500,
                    TargetBots = cityBots,
                });

                // ---- Field camps (4 par royaume = 12 groupes × groupSize bots) ----
                // Albion mainland (region 1) — typical leveling tiers
                AddField(eRealm.Albion, 1, new Point3D(560_000, 530_000, 2_900), 6, 15, groupSize);   // ~Cotswold area
                AddField(eRealm.Albion, 1, new Point3D(420_000, 565_000, 2_500), 20, 30, groupSize);  // ~Black Mountains
                AddField(eRealm.Albion, 1, new Point3D(380_000, 380_000, 2_500), 35, 45, groupSize);  // ~Cornwall
                AddField(eRealm.Albion, 1, new Point3D(490_000, 290_000, 2_400), 45, 50, groupSize);  // ~Dartmoor

                // Midgard mainland (region 100)
                AddField(eRealm.Midgard, 100, new Point3D(800_000, 720_000, 4_500), 6, 15, groupSize);
                AddField(eRealm.Midgard, 100, new Point3D(700_000, 680_000, 4_500), 20, 30, groupSize);
                AddField(eRealm.Midgard, 100, new Point3D(550_000, 750_000, 4_300), 35, 45, groupSize);
                AddField(eRealm.Midgard, 100, new Point3D(640_000, 540_000, 4_200), 45, 50, groupSize);

                // Hibernia mainland (region 200)
                AddField(eRealm.Hibernia, 200, new Point3D(360_000, 330_000, 5_500), 6, 15, groupSize);
                AddField(eRealm.Hibernia, 200, new Point3D(400_000, 420_000, 5_500), 20, 30, groupSize);
                AddField(eRealm.Hibernia, 200, new Point3D(480_000, 500_000, 5_400), 35, 45, groupSize);
                AddField(eRealm.Hibernia, 200, new Point3D(520_000, 380_000, 5_400), 45, 50, groupSize);
            }
        }

        private static void AddField(eRealm realm, ushort region, Point3D center, byte minLvl, byte maxLvl, int groupSize)
        {
            _fields.Add(new FieldSpawnPoint
            {
                Realm = realm,
                Region = region,
                Center = center,
                LevelMin = minLvl,
                LevelMax = maxLvl,
                GroupSize = groupSize,
            });
        }

        public static bool Start()
        {
            if (_running)
                return false;

            if (!PvEPopulationProperties.PVEPOP_ENABLED)
            {
                log.Info("PvEPopulationManager: refusing to start, pvepop_enabled is false.");
                return false;
            }

            _running = true;

            // Seed the LastPlayerSeenTick so spawns start in 'awake' state for
            // the first hibernate window — they'll go to sleep naturally if no
            // one actually visits.
            long now = GameLoop.GameLoopTime;
            lock (_lock)
            {
                foreach (var c in _cities) c.LastPlayerSeenTick = now;
                foreach (var f in _fields) f.LastPlayerSeenTick = now;
            }

            _tickTimer = new ECSGameTimer(null, MaintenanceTick, TICK_MS);
            _tickTimer.Start();

            log.Info($"PvEPopulationManager started. {_cities.Count} city spawns, {_fields.Count} field spawns.");
            return true;
        }

        public static bool Stop()
        {
            if (!_running)
                return false;

            _running = false;
            _tickTimer?.Stop();
            _tickTimer = null;
            log.Info("PvEPopulationManager stopped. Bots already in world remain; /pvepop clear to remove them.");
            return true;
        }

        public static int ClearAll()
        {
            int removed = 0;
            lock (_lock)
            {
                foreach (var c in _cities)
                {
                    foreach (var b in c.Bots.ToList())
                        if (DespawnBot(b)) removed++;
                    c.Bots.Clear();
                }
                foreach (var f in _fields)
                {
                    foreach (var b in f.Bots.ToList())
                        if (DespawnBot(b)) removed++;
                    f.Bots.Clear();
                    f.BotGroup = null;
                }
            }
            return removed;
        }

        private static bool DespawnBot(MimicNPC bot)
        {
            if (bot == null) return false;
            try
            {
                if (bot.Group != null)
                    bot.Group.RemoveMember(bot);
                bot.Delete();
                return true;
            }
            catch (Exception e)
            {
                log.Error($"DespawnBot failed for {bot.Name}", e);
                return false;
            }
        }

        // -------- Maintenance tick --------

        private static int MaintenanceTick(ECSGameTimer timer)
        {
            if (!_running)
                return 0;

            try
            {
                long now = GameLoop.GameLoopTime;
                long hibernateMs = (long)PvEPopulationProperties.PVEPOP_HIBERNATE_MIN * 60_000L;

                lock (_lock)
                {
                    foreach (var city in _cities)
                        TickCity(city, now, hibernateMs);

                    foreach (var field in _fields)
                        TickField(field, now, hibernateMs);
                }
            }
            catch (Exception e)
            {
                log.Error("PvEPopulationManager tick failed", e);
            }

            return TICK_MS;
        }

        private static bool RegionHasPlayer(ushort regionId)
        {
            Region region = WorldMgr.GetRegion(regionId);
            if (region == null)
                return false;

            // ClientService keeps a fast list of connected players; scan only
            // entries in the target region. This avoids GetPlayersInRadius
            // which is O(N) per call.
            foreach (GameClient c in ClientService.Instance.GetClients())
            {
                if (c == null || c.Player == null) continue;
                if (c.Account != null && c.Account.PrivLevel > 1) continue; // GMs don't count
                if (c.Player.CurrentRegionID == regionId)
                    return true;
            }
            return false;
        }

        private static void TickCity(CitySpawnPoint city, long now, long hibernateMs)
        {
            // Player tracking + hibernation gate.
            if (RegionHasPlayer(city.Region))
            {
                city.LastPlayerSeenTick = now;

                if (city.Hibernated)
                {
                    city.Hibernated = false;
                    log.Info($"City {city.Region} woke up (player returned).");
                }
            }
            else if (!city.Hibernated && now - city.LastPlayerSeenTick > hibernateMs)
            {
                Hibernate(city);
                return;
            }

            if (city.Hibernated)
                return; // nothing to do, zone empty

            // Prune dead/stale bots.
            city.Bots.RemoveAll(b => b == null || !b.IsAlive || b.ObjectState != GameObject.eObjectState.Active);

            // Spawn up to N at a time per tick (avoid burst).
            int missing = city.TargetBots - city.Bots.Count;
            int spawnNow = Math.Min(missing, 3);
            for (int i = 0; i < spawnNow; i++)
            {
                MimicNPC bot = CreateCityIdleBot(city);
                if (bot != null)
                    city.Bots.Add(bot);
            }
        }

        private static void TickField(FieldSpawnPoint field, long now, long hibernateMs)
        {
            if (RegionHasPlayer(field.Region))
            {
                field.LastPlayerSeenTick = now;

                if (field.Hibernated)
                {
                    field.Hibernated = false;
                    log.Info($"Field spawn region={field.Region} lvl={field.LevelMin}-{field.LevelMax} woke up.");
                }
            }
            else if (!field.Hibernated && now - field.LastPlayerSeenTick > hibernateMs)
            {
                Hibernate(field);
                return;
            }

            if (field.Hibernated)
                return;

            // Prune dead bots first.
            field.Bots.RemoveAll(b => b == null || !b.IsAlive || b.ObjectState != GameObject.eObjectState.Active);

            // Spawn fresh group when below target.
            if (field.Bots.Count < field.GroupSize)
            {
                int missing = field.GroupSize - field.Bots.Count;

                // First time? Build a fresh group + composition.
                if (field.BotGroup == null || field.Bots.Count == 0)
                {
                    field.BotGroup = null;
                }

                int spawnNow = Math.Min(missing, 2); // gentle ramp
                for (int i = 0; i < spawnNow; i++)
                {
                    MimicNPC bot = CreateFieldBot(field);
                    if (bot == null) continue;

                    if (field.BotGroup == null)
                    {
                        field.BotGroup = new Group(bot);
                        GroupMgr.AddGroup(field.BotGroup);
                    }
                    else
                    {
                        field.BotGroup.AddMember(bot);
                    }
                    field.Bots.Add(bot);
                }

                // Set a camp point so the existing camp FSM (puller/CC/tank logic)
                // kicks in for this group.
                if (field.BotGroup != null && field.BotGroup.MimicGroup != null)
                    field.BotGroup.MimicGroup.SetCampPoint(new Point3D(field.Center));
            }
        }

        private static void Hibernate(CitySpawnPoint city)
        {
            city.Hibernated = true;
            int n = city.Bots.Count;
            foreach (var b in city.Bots.ToList())
                DespawnBot(b);
            city.Bots.Clear();
            log.Info($"City {city.Region} hibernated, {n} bots despawned.");
        }

        private static void Hibernate(FieldSpawnPoint field)
        {
            field.Hibernated = true;
            int n = field.Bots.Count;
            foreach (var b in field.Bots.ToList())
                DespawnBot(b);
            field.Bots.Clear();
            field.BotGroup = null;
            log.Info($"Field spawn region={field.Region} lvl={field.LevelMin}-{field.LevelMax} hibernated, {n} bots despawned.");
        }

        // -------- Class pickers (per-realm random) --------

        // City idle bots can be anything — variety matters more than role mix.
        private static readonly eMimicClass[] _albClasses = {
            eMimicClass.Armsman, eMimicClass.Paladin, eMimicClass.Mercenary, eMimicClass.Reaver,
            eMimicClass.Scout, eMimicClass.Minstrel, eMimicClass.Cleric, eMimicClass.Friar,
            eMimicClass.Cabalist, eMimicClass.Sorcerer, eMimicClass.Theurgist, eMimicClass.Wizard,
            eMimicClass.Infiltrator,
        };
        private static readonly eMimicClass[] _midClasses = {
            eMimicClass.Warrior, eMimicClass.Thane, eMimicClass.Berserker, eMimicClass.Savage,
            eMimicClass.Skald, eMimicClass.Hunter, eMimicClass.Shadowblade, eMimicClass.Healer,
            eMimicClass.Shaman, eMimicClass.Runemaster, eMimicClass.Spiritmaster, eMimicClass.Bonedancer,
        };
        private static readonly eMimicClass[] _hibClasses = {
            eMimicClass.Hero, eMimicClass.Champion, eMimicClass.Blademaster, eMimicClass.Warden,
            eMimicClass.Bard, eMimicClass.Druid, eMimicClass.Ranger, eMimicClass.Nightshade,
            eMimicClass.Eldritch, eMimicClass.Enchanter, eMimicClass.Mentalist, eMimicClass.Valewalker,
        };

        // Combat-oriented subset for field camps: drops support-only classes that
        // would camp poorly without a group composer pass.
        private static readonly eMimicClass[] _albCombatClasses = {
            eMimicClass.Armsman, eMimicClass.Paladin, eMimicClass.Mercenary, eMimicClass.Reaver,
            eMimicClass.Cleric, eMimicClass.Wizard, eMimicClass.Theurgist, eMimicClass.Cabalist,
            eMimicClass.Scout,
        };
        private static readonly eMimicClass[] _midCombatClasses = {
            eMimicClass.Warrior, eMimicClass.Thane, eMimicClass.Berserker, eMimicClass.Savage,
            eMimicClass.Healer, eMimicClass.Shaman, eMimicClass.Runemaster, eMimicClass.Spiritmaster,
            eMimicClass.Hunter,
        };
        private static readonly eMimicClass[] _hibCombatClasses = {
            eMimicClass.Hero, eMimicClass.Champion, eMimicClass.Blademaster, eMimicClass.Warden,
            eMimicClass.Druid, eMimicClass.Eldritch, eMimicClass.Enchanter, eMimicClass.Mentalist,
            eMimicClass.Ranger,
        };

        private static eMimicClass PickAnyClassForRealm(eRealm realm)
        {
            eMimicClass[] pool = realm switch
            {
                eRealm.Midgard  => _midClasses,
                eRealm.Hibernia => _hibClasses,
                _               => _albClasses,
            };
            return pool[Util.Random(0, pool.Length - 1)];
        }

        private static eMimicClass PickCombatClassForRealm(eRealm realm)
        {
            eMimicClass[] pool = realm switch
            {
                eRealm.Midgard  => _midCombatClasses,
                eRealm.Hibernia => _hibCombatClasses,
                _               => _albCombatClasses,
            };
            return pool[Util.Random(0, pool.Length - 1)];
        }

        // -------- Bot factories --------

        private static MimicNPC CreateCityIdleBot(CitySpawnPoint city)
        {
            // Random class for variety. Specs/equipment via the existing
            // MimicManager factory keeps the look consistent with /mcreate bots.
            eMimicClass cls = PickAnyClassForRealm(city.Realm);
            byte level = (byte)Util.Random(20, 50);

            MimicNPC bot = MimicManager.GetMimic(cls, level, "", eGender.Neutral, eSpecType.None, preventCombat: true);
            if (bot == null) return null;

            // Random offset inside city radius so they don't pile up at one point.
            int dx = Util.Random(-city.Radius, city.Radius);
            int dy = Util.Random(-city.Radius, city.Radius);
            Point3D pos = new(city.Center.X + dx, city.Center.Y + dy, city.Center.Z);

            if (!MimicManager.AddMimicToWorld(bot, pos, city.Region))
                return null;

            // Force the city idle state immediately so they don't spend a few
            // seconds in WAKING_UP wandering off.
            if (bot.MimicBrain != null)
            {
                bot.MimicBrain.AggroLevel = 0;
                bot.MimicBrain.AggroRange = 0;
                bot.MimicBrain.Roam = false;
                bot.MimicBrain.FSM.SetCurrentState(eFSMStateType.CITY_IDLE);
            }

            return bot;
        }

        private static MimicNPC CreateFieldBot(FieldSpawnPoint field)
        {
            eMimicClass cls = PickCombatClassForRealm(field.Realm);
            byte level = (byte)Util.Random(field.LevelMin, field.LevelMax);

            MimicNPC bot = MimicManager.GetMimic(cls, level);
            if (bot == null) return null;

            int dx = Util.Random(-300, 300);
            int dy = Util.Random(-300, 300);
            Point3D pos = new(field.Center.X + dx, field.Center.Y + dy, field.Center.Z);

            if (!MimicManager.AddMimicToWorld(bot, pos, field.Region))
                return null;

            return bot;
        }

        // -------- Status / introspection --------

        public static string BuildStatusReport()
        {
            System.Text.StringBuilder sb = new();
            sb.AppendLine("=== PvE Population status ===");
            sb.AppendLine($"Enabled: {PvEPopulationProperties.PVEPOP_ENABLED}");
            sb.AppendLine($"Running: {_running}");
            sb.AppendLine($"Hibernate after: {PvEPopulationProperties.PVEPOP_HIBERNATE_MIN} min");
            sb.AppendLine();
            sb.AppendLine("Cities:");
            lock (_lock)
            {
                foreach (var c in _cities)
                    sb.AppendLine($"  realm={c.Realm,-8} region={c.Region,-4} bots={c.Bots.Count}/{c.TargetBots,-3} hib={c.Hibernated}");
                sb.AppendLine("Fields:");
                foreach (var f in _fields)
                    sb.AppendLine($"  realm={f.Realm,-8} region={f.Region,-4} lvl={f.LevelMin}-{f.LevelMax} bots={f.Bots.Count}/{f.GroupSize} hib={f.Hibernated}");
            }
            sb.AppendLine();
            sb.AppendLine($"Total live bots: {TotalLiveBots}");
            return sb.ToString();
        }
    }

    #endregion

    #region Command

    [CmdAttribute("&pvepop",
        ePrivLevel.GM,
        "Manage the PvE population manager (capital + field bots).",
        "/pvepop start",
        "/pvepop stop",
        "/pvepop status",
        "/pvepop clear")]
    public class PvEPopulationCommand : AbstractCommandHandler, ICommandHandler
    {
        public void OnCommand(GameClient client, string[] args)
        {
            if (args.Length < 2)
            {
                DisplaySyntax(client);
                return;
            }

            switch (args[1].ToLowerInvariant())
            {
                case "start":
                    if (PvEPopulationManager.Start())
                        client.Out.SendMessage("PvE population started.", eChatType.CT_System, eChatLoc.CL_SystemWindow);
                    else
                        client.Out.SendMessage("Already running or pvepop_enabled is false.", eChatType.CT_System, eChatLoc.CL_SystemWindow);
                    break;
                case "stop":
                    PvEPopulationManager.Stop();
                    client.Out.SendMessage("PvE population stopped.", eChatType.CT_System, eChatLoc.CL_SystemWindow);
                    break;
                case "clear":
                    int n = PvEPopulationManager.ClearAll();
                    client.Out.SendMessage($"Despawned {n} population bots.", eChatType.CT_System, eChatLoc.CL_SystemWindow);
                    break;
                case "status":
                    client.Out.SendMessage(PvEPopulationManager.BuildStatusReport(), eChatType.CT_System, eChatLoc.CL_SystemWindow);
                    break;
                default:
                    DisplaySyntax(client);
                    break;
            }
        }
    }

    #endregion
}

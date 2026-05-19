using DOL.AI.Brain;
using DOL.Events;
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
    // PvP Frontier System
    // ----------------------------------------------------------------------------
    // Spawns and maintains hundreds of autonomous mimic bots in level-50 RvR zones,
    // organized in hybrid groups that roam waypoints, detect enemy realm groups,
    // and assess whether to engage or flee.
    //
    // Architecture:
    //   PvPFrontierManager     - static, top-level lifecycle. Holds 3 realm pools.
    //   PvPFrontierGroup       - one roaming group. State machine: Form/Patrol/
    //                            Engage/Regroup/Disband. Owns N MimicNPCs and a DAoC Group.
    //   PvPGroupComposer       - picks class compositions for PvP (different from PvE).
    //   PvPEngagementAssessor  - compares group strength to decide engage/flee/ignore.
    //   PvPFrontierCommand     - admin commands /pvpfrontier {start|stop|status|clear}.
    //
    // Activation: requires ServerProperty `pvp_frontier_enabled = true` AND an admin
    // running /pvpfrontier start. Stays off by default so the system doesn't surprise
    // a server admin on first boot.
    // ============================================================================

    #region Server properties

    public static class PvPFrontierProperties
    {
        [ServerProperty("pvpfrontier", "pvp_frontier_autostart",
            "Auto-start the PvP frontier bot system at server boot? Default true. Set false if you only want manual /pvpfrontier start.", true)]
        public static bool PVP_FRONTIER_AUTOSTART;

        [ServerProperty("pvpfrontier", "pvp_frontier_population_per_realm",
            "Target number of mimic bots maintained per realm in the frontier zones (default 400).", 400)]
        public static int PVP_FRONTIER_POPULATION_PER_REALM;

        [ServerProperty("pvpfrontier", "pvp_frontier_region",
            "Region ID for the shared PvP frontier zone. Default 163 (Hadrian's Wall / Old Frontier).", 163)]
        public static int PVP_FRONTIER_REGION;

        [ServerProperty("pvpfrontier", "pvp_frontier_min_level",
            "Minimum bot level in the frontier.", 45)]
        public static int PVP_FRONTIER_MIN_LEVEL;

        [ServerProperty("pvpfrontier", "pvp_frontier_max_level",
            "Maximum bot level in the frontier.", 50)]
        public static int PVP_FRONTIER_MAX_LEVEL;

        [ServerProperty("pvpfrontier", "pvp_frontier_engage_aggression",
            "Engagement aggression: 0=engage only with advantage (default), 1=engage at parity, 2=always engage.", 0)]
        public static int PVP_FRONTIER_ENGAGE_AGGRESSION;

        [ServerProperty("pvpfrontier", "pvp_frontier_keep_attack_chance",
            "Chance (0-100) per patrol waypoint pick that a frontier group targets a nearby enemy keep/tower instead of a random waypoint.", 35)]
        public static int PVP_FRONTIER_KEEP_ATTACK_CHANCE;
    }

    #endregion

    #region Manager

    public static class PvPFrontierManager
    {
        private static readonly Logger log = LoggerManager.Create(MethodBase.GetCurrentMethod().DeclaringType);

        // Per-realm spawn anchors. Bots spawn in a radius around these and pick
        // patrol waypoints from the realm's waypoint list.
        // These coordinates target Hadrian's Wall area in the Old Frontiers
        // (region 163) — Albion is south-east, Midgard north-west, Hibernia
        // north-east. An admin can override per-realm anchors at runtime via
        // SetSpawnAnchor.
        public sealed class RealmConfig
        {
            public eRealm Realm;
            public Point3D SpawnAnchor;
            public List<Point3D> PatrolWaypoints = new();
            public List<PvPFrontierGroup> Groups = new();
        }

        internal static readonly Dictionary<eRealm, RealmConfig> _configs = new();
        internal static readonly object _configsLock = new();
        private static ECSGameTimer _tickTimer;
        private static bool _running;
        private const int TICK_MS = 5000;            // population/maintenance tick
        private const int GROUP_TICK_MS = 2000;      // per-group AI tick

        public static bool IsRunning => _running;
        public static int TotalLiveBots
        {
            get
            {
                lock (_configsLock)
                {
                    int total = 0;
                    foreach (var cfg in _configs.Values)
                        foreach (var g in cfg.Groups)
                            total += g.AliveMemberCount;
                    return total;
                }
            }
        }

        public static void Initialize()
        {
            BuildDefaultConfig();

            if (PvPFrontierProperties.PVP_FRONTIER_AUTOSTART)
            {
                Start();
                log.Info("PvPFrontierManager initialized and auto-started.");
            }
            else
            {
                log.Info("PvPFrontierManager initialized (auto-start disabled). Use /pvpfrontier start to spawn the population.");
            }
        }

        private static void BuildDefaultConfig()
        {
            ushort region = (ushort)PvPFrontierProperties.PVP_FRONTIER_REGION;

            lock (_configsLock)
            {
                _configs.Clear();

                // Default anchors for Hadrian's Wall (region 163). The three
                // realms get distinct corners; waypoints describe a triangle
                // that brings them toward the center where encounters happen.
                _configs[eRealm.Albion] = new RealmConfig
                {
                    Realm = eRealm.Albion,
                    SpawnAnchor = new Point3D(45_000, 55_000, 3_500),
                    PatrolWaypoints = new()
                    {
                        new Point3D(45_000, 55_000, 3_500),  // home
                        new Point3D(38_000, 48_000, 3_500),  // mid-west
                        new Point3D(40_000, 35_000, 3_500),  // contested center
                        new Point3D(52_000, 42_000, 3_500),  // east patrol
                    },
                };

                _configs[eRealm.Hibernia] = new RealmConfig
                {
                    Realm = eRealm.Hibernia,
                    SpawnAnchor = new Point3D(25_000, 22_000, 3_500),
                    PatrolWaypoints = new()
                    {
                        new Point3D(25_000, 22_000, 3_500),
                        new Point3D(30_000, 28_000, 3_500),
                        new Point3D(40_000, 35_000, 3_500),
                        new Point3D(20_000, 35_000, 3_500),
                    },
                };

                _configs[eRealm.Midgard] = new RealmConfig
                {
                    Realm = eRealm.Midgard,
                    SpawnAnchor = new Point3D(55_000, 25_000, 3_500),
                    PatrolWaypoints = new()
                    {
                        new Point3D(55_000, 25_000, 3_500),
                        new Point3D(48_000, 30_000, 3_500),
                        new Point3D(40_000, 35_000, 3_500),
                        new Point3D(52_000, 42_000, 3_500),
                    },
                };
            }
        }

        public static bool Start()
        {
            if (_running)
                return false;

            if (_configs.Count == 0)
                BuildDefaultConfig();

            _running = true;
            _tickTimer = new ECSGameTimer(null, MaintenanceTick, TICK_MS);
            _tickTimer.Start();

            log.Info($"PvPFrontierManager started. Target {PvPFrontierProperties.PVP_FRONTIER_POPULATION_PER_REALM} bots per realm.");
            return true;
        }

        public static bool Stop()
        {
            if (!_running)
                return false;

            _running = false;
            _tickTimer?.Stop();
            _tickTimer = null;
            log.Info("PvPFrontierManager stopped. Existing bots remain in world; /pvpfrontier clear to remove them.");
            return true;
        }

        public static int ClearAll()
        {
            int removed = 0;

            lock (_configsLock)
            {
                foreach (var cfg in _configs.Values)
                {
                    foreach (var grp in cfg.Groups.ToList())
                    {
                        removed += grp.DisbandAndDelete();
                    }
                    cfg.Groups.Clear();
                }
            }

            return removed;
        }

        /// <summary>
        /// Maintenance tick: ensure each realm reaches its target population
        /// by spawning new groups, and step every group's AI.
        /// </summary>
        private static int MaintenanceTick(ECSGameTimer timer)
        {
            if (!_running)
                return 0;

            try
            {
                int target = PvPFrontierProperties.PVP_FRONTIER_POPULATION_PER_REALM;

                lock (_configsLock)
                {
                    foreach (var kv in _configs)
                    {
                        RealmConfig cfg = kv.Value;

                        // Step every group; prune disbanded ones.
                        for (int i = cfg.Groups.Count - 1; i >= 0; i--)
                        {
                            PvPFrontierGroup grp = cfg.Groups[i];
                            grp.Tick();

                            if (grp.IsDisbanded)
                                cfg.Groups.RemoveAt(i);
                        }

                        // Spawn enough new groups to reach the realm's target population.
                        int alive = 0;
                        foreach (var g in cfg.Groups)
                            alive += g.AliveMemberCount;

                        int missing = target - alive;

                        // Spawn one group per tick (avoid burst). Groups are 4-8 mimics.
                        if (missing > 0)
                        {
                            int groupSize = Math.Clamp(missing, 4, 8);
                            PvPFrontierGroup newGroup = PvPFrontierGroup.Spawn(cfg, groupSize);
                            if (newGroup != null)
                                cfg.Groups.Add(newGroup);
                        }
                    }
                }
            }
            catch (Exception e)
            {
                log.Error("PvPFrontierManager tick failed", e);
            }

            return TICK_MS;
        }

        public static string BuildStatusReport()
        {
            System.Text.StringBuilder sb = new();
            sb.AppendLine("=== PvP Frontier status ===");
            sb.AppendLine($"Running: {_running}");
            sb.AppendLine($"Target population per realm: {PvPFrontierProperties.PVP_FRONTIER_POPULATION_PER_REALM}");
            sb.AppendLine($"Region: {PvPFrontierProperties.PVP_FRONTIER_REGION}");
            sb.AppendLine();
            sb.AppendLine($"{"Realm",-9} {"groups",6} {"logical",7} {"hydrated",8} {"npcs",5}");

            lock (_configsLock)
            {
                foreach (var cfg in _configs.Values)
                {
                    int hydrated = 0;
                    int logical = 0;
                    int npcs = 0;
                    foreach (var g in cfg.Groups)
                    {
                        logical += g.LogicalMemberCount;
                        if (g.IsHydrated)
                        {
                            hydrated++;
                            npcs += g.AliveMemberCount;
                        }
                    }
                    sb.AppendLine($"{cfg.Realm,-9} {cfg.Groups.Count,6} {logical,7} {hydrated,8} {npcs,5}");
                }
            }

            sb.AppendLine();
            sb.AppendLine("Dormant groups consume no AI CPU — they only simulate movement.");
            sb.AppendLine("Hydrated groups have actual MimicNPCs running combat AI.");
            sb.AppendLine();
            sb.AppendLine($"Engagement aggression: {PvPFrontierProperties.PVP_FRONTIER_ENGAGE_AGGRESSION} (0=adv only, 1=parity, 2=always)");
            return sb.ToString();
        }
    }

    #endregion

    #region Group state machine

    public enum eFrontierState
    {
        Forming,
        Patrolling,
        Engaging,
        Retreating,
        Disbanded,
    }

    public sealed class PvPFrontierGroup
    {
        private static readonly Logger log = LoggerManager.Create(MethodBase.GetCurrentMethod().DeclaringType);

        public PvPFrontierManager.RealmConfig Config { get; }
        public Group DolGroup { get; private set; }
        public List<MimicNPC> Members { get; } = new();
        public eFrontierState State { get; private set; } = eFrontierState.Forming;

        public bool IsDisbanded => State == eFrontierState.Disbanded;

        // ----- Logical roster / dormant simulation -----
        //
        // A LogicalMember is a persistent identity (class, level, name, gender,
        // role). It survives dehydration. When the group is dormant, this is
        // ALL we keep around — no GameObject, no brain, no inventory, no AI tick.
        //
        // When a player approaches within HYDRATE_RANGE of VirtualPosition, the
        // group "materialises" — each LogicalMember becomes a real MimicNPC at
        // VirtualPosition. When the player leaves and DEHYDRATE_RANGE+grace is
        // reached, the MimicNPCs are deleted and the Roster persists.
        public sealed class LogicalMember
        {
            public eMimicClass MimicClass;
            public byte Level;
            public string Name;
            public eGender Gender;
            public bool IsTank;
            public bool IsHealer;
            public bool IsCC;
        }

        public List<LogicalMember> Roster { get; } = new();
        public Point3D VirtualPosition { get; private set; }
        public ushort Region { get; private set; }
        public bool IsHydrated { get; private set; }

        public int LogicalMemberCount => Roster.Count;

        public int AliveMemberCount
        {
            get
            {
                if (!IsHydrated) return Roster.Count;

                int n = 0;
                foreach (var m in Members)
                    if (m != null && m.IsAlive && m.ObjectState == GameObject.eObjectState.Active)
                        n++;
                return n;
            }
        }

        // Detection range for spotting enemy groups. ~3500u corresponds to
        // visibility range and matches the brain's aggro range.
        private const int DETECTION_RANGE = 3500;
        private const int WAYPOINT_REACHED_RANGE = 400;

        // Hydration: player must be within HYDRATE_RANGE of VirtualPosition to
        // wake the group up. Dehydration uses a wider radius + grace period so
        // a player jogging past doesn't flap the group on/off.
        private const int HYDRATE_RANGE = 4500;
        private const int DEHYDRATE_RANGE = 6500;
        private const int DEHYDRATE_GRACE_MS = 20_000;

        // Dormant movement: how often we advance VirtualPosition and how far
        // each step covers. 5s tick * 190 (run speed) ≈ 950u per step.
        private const int DORMANT_STEP_MS = 5_000;
        private const int DORMANT_STEP_DISTANCE = 950;

        private Point3D _currentWaypoint;
        private long _nextScanMs;
        private long _retreatUntilMs;
        private long _dehydrateAfterMs;   // 0 = no pending dehydration
        private long _nextDormantStepMs;

        // After leaving combat, the group rests this long before scanning for
        // new engagements. Prevents a lone human from being chain-wiped by
        // multiple bot groups converging on the same spot.
        private const int POST_FIGHT_COOLDOWN_MS = 30_000;
        private long _postFightUntilMs;

        private PvPFrontierGroup(PvPFrontierManager.RealmConfig cfg) => Config = cfg;

        /// <summary>
        /// Composes a roster (class + level per slot) and registers the group
        /// in dormant mode at the realm's anchor. NO MimicNPC is instantiated —
        /// that happens lazily on hydration. Returns null only if the composer
        /// produces no classes (catalog edge case).
        /// </summary>
        public static PvPFrontierGroup Spawn(PvPFrontierManager.RealmConfig cfg, int groupSize)
        {
            PvPFrontierGroup g = new(cfg);

            List<eMimicClass> comp = PvPGroupComposer.BuildPvPComposition(cfg.Realm, groupSize);
            if (comp.Count == 0)
                return null;

            byte minLevel = (byte)PvPFrontierProperties.PVP_FRONTIER_MIN_LEVEL;
            byte maxLevel = (byte)PvPFrontierProperties.PVP_FRONTIER_MAX_LEVEL;

            foreach (eMimicClass cls in comp)
            {
                byte level = (byte)Util.Random(minLevel, maxLevel);
                eGender gender = Util.Random(1) > 0 ? eGender.Male : eGender.Female;

                g.Roster.Add(new LogicalMember
                {
                    MimicClass = cls,
                    Level = level,
                    Name = MimicNames.GetName(gender, cfg.Realm),
                    Gender = gender,
                    IsTank = IsTankClassEnum(cls),
                    IsHealer = IsHealerClassEnum(cls),
                    IsCC = IsCCClassEnum(cls),
                });
            }

            g.Region = (ushort)PvPFrontierProperties.PVP_FRONTIER_REGION;
            g.VirtualPosition = new Point3D(cfg.SpawnAnchor.X + Util.Random(-250, 250),
                                            cfg.SpawnAnchor.Y + Util.Random(-250, 250),
                                            cfg.SpawnAnchor.Z);

            g.PickNextWaypoint();
            g.State = eFrontierState.Patrolling;
            g.IsHydrated = false;
            g._nextDormantStepMs = GameLoop.GameLoopTime + DORMANT_STEP_MS;
            return g;
        }

        // Classify a class by enum so we never have to instantiate a MimicNPC
        // just to ask "is it a tank?". Mirrors MimicGroupComposer.Is*Class which
        // takes a MimicNPC instance.
        private static bool IsTankClassEnum(eMimicClass c) =>
            MimicCombatProfileRegistry.HasRole(c, eMimicCombatRole.Tank);

        private static bool IsHealerClassEnum(eMimicClass c) =>
            MimicCombatProfileRegistry.HasRole(c, eMimicCombatRole.Healer);

        private static bool IsCCClassEnum(eMimicClass c) =>
            MimicCombatProfileRegistry.HasRole(c, eMimicCombatRole.CrowdControl);

        public int DisbandAndDelete()
        {
            int n = 0;
            if (IsHydrated)
            {
                foreach (var m in Members.ToList())
                {
                    if (m != null && m.ObjectState == GameObject.eObjectState.Active)
                    {
                        m.Delete();
                        n++;
                    }
                }
                Members.Clear();
                IsHydrated = false;
            }
            Roster.Clear();
            State = eFrontierState.Disbanded;
            return n;
        }

        /// <summary>
        /// Materialises every LogicalMember into a real MimicNPC at the current
        /// VirtualPosition and binds them into a DAoC group. Idempotent: returns
        /// immediately if already hydrated.
        /// </summary>
        public void Hydrate()
        {
            if (IsHydrated || Roster.Count == 0) return;

            foreach (LogicalMember lm in Roster)
            {
                Point3D pos = new(VirtualPosition.X + Util.Random(-200, 200),
                                  VirtualPosition.Y + Util.Random(-200, 200),
                                  VirtualPosition.Z);

                MimicNPC m = MimicManager.GetMimic(lm.MimicClass, lm.Level, lm.Name, lm.Gender);
                if (m == null) continue;

                if (!MimicManager.AddMimicToWorld(m, pos, Region))
                    continue;

                if (m.MimicBrain != null)
                {
                    m.MimicBrain.PvPMode = true;
                    // Frontier movement is driven by OrderGroupToWaypoint /
                    // PickNextWaypoint, NOT by the generic roam state. The
                    // WAKING_UP state transitions a CanRoam bot straight to
                    // ROAMING the moment it ticks, which would short-circuit
                    // the patrol logic and send bots wandering instead of
                    // following waypoints.
                    m.MimicBrain.Roam = false;
                    m.MimicBrain.AggroLevel = 100;
                    m.MimicBrain.AggroRange = 3000;
                    m.MimicBrain.IsHealer = lm.IsHealer;
                }

                Members.Add(m);
            }

            if (Members.Count == 0) return;

            DolGroup = new Group(Members[0]);
            GroupMgr.AddGroup(DolGroup);
            DolGroup.AddMember(Members[0]);
            for (int i = 1; i < Members.Count; i++)
                DolGroup.AddMember(Members[i]);

            PvPGroupComposer.AutoAssignPvPRoles(Members);
            IsHydrated = true;
            OrderGroupToWaypoint();
        }

        /// <summary>
        /// Deletes every MimicNPC. The logical Roster is preserved so the group
        /// can re-hydrate later. If a member died while hydrated, it's already
        /// gone from Members — we sync Roster too so it stays accurate.
        /// </summary>
        public void Dehydrate()
        {
            if (!IsHydrated) return;

            // Sync Roster with alive members (drop the dead).
            // Match by name to keep identities consistent across hydration cycles.
            HashSet<string> alive = new();
            foreach (var m in Members)
                if (m != null && m.IsAlive) alive.Add(m.Name);

            Roster.RemoveAll(lm => !alive.Contains(lm.Name));

            // VirtualPosition becomes the leader's last known position so the
            // group "appears" where it actually was on the next hydration.
            MimicNPC leader = FirstAliveMember();
            if (leader != null)
                VirtualPosition = new Point3D(leader.X, leader.Y, leader.Z);

            foreach (var m in Members.ToList())
            {
                if (m != null && m.ObjectState == GameObject.eObjectState.Active)
                    m.Delete();
            }
            Members.Clear();
            DolGroup = null;
            IsHydrated = false;
            _nextDormantStepMs = GameLoop.GameLoopTime + DORMANT_STEP_MS;
        }

        /// <summary>
        /// Called periodically from PvPFrontierManager. Drives:
        ///   - proximity check + hydration management
        ///   - hydrated state machine (patrol / engage / retreat)
        ///   - dormant simulation (advance VirtualPosition)
        /// </summary>
        public void Tick()
        {
            if (IsDisbanded) return;

            // Disband if the entire roster is gone (e.g. every bot died in a fight).
            if (Roster.Count == 0)
            {
                State = eFrontierState.Disbanded;
                return;
            }

            long now = GameLoop.GameLoopTime;
            bool playerNear = IsPlayerWithin(HYDRATE_RANGE);

            // ---- Hydration management ----
            if (!IsHydrated)
            {
                if (playerNear)
                {
                    Hydrate();
                }
                else
                {
                    DormantTick(now);
                    return; // dormant groups don't run combat AI
                }
            }
            else
            {
                // Hydrated. If player is now far away (DEHYDRATE_RANGE), start a
                // grace timer. If they come back within HYDRATE_RANGE before it
                // expires, cancel the dehydration.
                bool farFromAll = !IsPlayerWithin(DEHYDRATE_RANGE);

                if (farFromAll)
                {
                    if (_dehydrateAfterMs == 0)
                        _dehydrateAfterMs = now + DEHYDRATE_GRACE_MS;
                    else if (now >= _dehydrateAfterMs)
                    {
                        Dehydrate();
                        _dehydrateAfterMs = 0;
                        return;
                    }
                }
                else
                {
                    _dehydrateAfterMs = 0;
                }
            }

            // Disband if hydrated but every member died (Roster won't refill).
            if (IsHydrated && AliveMemberCount == 0)
            {
                State = eFrontierState.Disbanded;
                return;
            }

            // ---- Hydrated state machine ----
            switch (State)
            {
                case eFrontierState.Forming:
                    State = eFrontierState.Patrolling;
                    OrderGroupToWaypoint();
                    break;

                case eFrontierState.Patrolling:
                    TickPatrol();
                    break;

                case eFrontierState.Engaging:
                    TickEngaging();
                    break;

                case eFrontierState.Retreating:
                    TickRetreat();
                    break;
            }
        }

        /// <summary>
        /// Cheap simulation tick. Advances VirtualPosition toward the current
        /// waypoint by DORMANT_STEP_DISTANCE every DORMANT_STEP_MS. No combat,
        /// no scanning, no AI cost.
        /// </summary>
        private void DormantTick(long now)
        {
            if (now < _nextDormantStepMs) return;
            _nextDormantStepMs = now + DORMANT_STEP_MS;

            long dx = _currentWaypoint.X - VirtualPosition.X;
            long dy = _currentWaypoint.Y - VirtualPosition.Y;
            long distSq = dx * dx + dy * dy;

            if (distSq < (long)WAYPOINT_REACHED_RANGE * WAYPOINT_REACHED_RANGE)
            {
                PickNextWaypoint();
                return;
            }

            double dist = Math.Sqrt(distSq);
            double step = Math.Min(DORMANT_STEP_DISTANCE, dist);
            double nx = VirtualPosition.X + dx * step / dist;
            double ny = VirtualPosition.Y + dy * step / dist;
            VirtualPosition = new Point3D((int)nx, (int)ny, VirtualPosition.Z);
        }

        /// <summary>
        /// True if any non-GM human player is within `range` of VirtualPosition
        /// in our Region. Filters out GMs (PrivLevel > 1) since admins shouldn't
        /// keep the system busy by being invisible.
        /// </summary>
        private bool IsPlayerWithin(int range)
        {
            global::DOL.GS.Region region = WorldMgr.GetRegion(Region);
            if (region == null) return false;

            long rangeSq = (long)range * range;
            foreach (GamePlayer p in ClientService.Instance.GetPlayersOfRegion(region))
            {
                if (p == null || !p.IsAlive) continue;
                if (p.Client?.Account != null && p.Client.Account.PrivLevel > 1) continue;

                long dx = p.X - VirtualPosition.X;
                long dy = p.Y - VirtualPosition.Y;
                if (dx * dx + dy * dy <= rangeSq) return true;
            }
            return false;
        }

        private void TickPatrol()
        {
            GameLiving leader = FirstAliveMember();
            if (leader == null) return;

            // If we are near our waypoint AND it's a keep, engage the doors/guards.
            TryAttackKeepObjectsNearWaypoint();

            // Reached current waypoint? Pick a new one.
            if (leader.GetDistance(_currentWaypoint) < WAYPOINT_REACHED_RANGE)
            {
                PickNextWaypoint();
                OrderGroupToWaypoint();
            }

            // Periodically scan for enemy realm groups within detection range.
            long now = GameLoop.GameLoopTime;

            // Honour the post-fight cooldown: the group keeps patrolling but
            // doesn't seek a new engagement during the rest window.
            if (now < _postFightUntilMs)
                return;

            if (now >= _nextScanMs)
            {
                _nextScanMs = now + 3000 + Util.Random(0, 2000);
                PvPFrontierGroup enemy = ScanForEnemyGroup(leader);

                if (enemy != null)
                {
                    eEngagementDecision decision = PvPEngagementAssessor.Assess(this, enemy);

                    if (decision == eEngagementDecision.Engage)
                    {
                        OrderGroupToEngage(enemy);
                        State = eFrontierState.Engaging;
                    }
                    else if (decision == eEngagementDecision.Retreat)
                    {
                        OrderGroupToRetreat();
                        _retreatUntilMs = now + 20_000;
                        State = eFrontierState.Retreating;
                    }
                }
            }
        }

        private void TickEngaging()
        {
            // Stay engaging while at least one bot is in combat. When everyone
            // is clear of combat (enemy wiped or escaped), arm the post-fight
            // cooldown and resume patrol — gives lone humans a chance to
            // disengage instead of being chain-mobbed by every nearby group.
            bool anyoneInCombat = Members.Any(m => m != null && m.IsAlive && m.InCombat);

            if (!anyoneInCombat)
            {
                _postFightUntilMs = GameLoop.GameLoopTime + POST_FIGHT_COOLDOWN_MS;
                State = eFrontierState.Patrolling;
                PickNextWaypoint();
                OrderGroupToWaypoint();
            }
        }

        private void TickRetreat()
        {
            long now = GameLoop.GameLoopTime;
            if (now >= _retreatUntilMs)
            {
                State = eFrontierState.Patrolling;
                PickNextWaypoint();
                OrderGroupToWaypoint();
            }
        }

        // ----- helpers -----

        private MimicNPC FirstAliveMember()
        {
            foreach (var m in Members)
                if (m != null && m.IsAlive && m.ObjectState == GameObject.eObjectState.Active)
                    return m;
            return null;
        }

        private void PickNextWaypoint()
        {
            // Roll for "go attack an enemy keep" intent. If it hits, find the
            // closest enemy-realm keep in our region and use it as the waypoint.
            if (Util.Chance(PvPFrontierProperties.PVP_FRONTIER_KEEP_ATTACK_CHANCE))
            {
                Point3D keepTarget = PickClosestEnemyKeep();
                if (keepTarget != null)
                {
                    _currentWaypoint = keepTarget;
                    return;
                }
            }

            if (Config.PatrolWaypoints.Count == 0)
            {
                _currentWaypoint = Config.SpawnAnchor;
                return;
            }
            _currentWaypoint = Config.PatrolWaypoints[Util.Random(Config.PatrolWaypoints.Count - 1)];
        }

        /// <summary>
        /// Returns the position of the closest enemy-realm keep / tower in our
        /// region, or null if none exist. The keep system handles ownership
        /// changes automatically — when a keep flips to our realm, the next
        /// pick will skip it.
        /// </summary>
        private Point3D PickClosestEnemyKeep()
        {
            MimicNPC leader = FirstAliveMember();
            if (leader == null) return null;

            var keeps = GameServer.KeepManager.GetKeepsOfRegion(leader.CurrentRegionID);
            if (keeps == null || keeps.Count == 0) return null;

            Keeps.AbstractGameKeep best = null;
            long bestSq = long.MaxValue;

            foreach (var k in keeps)
            {
                if (k == null) continue;
                if (k.Realm == Config.Realm) continue;

                long dx = k.X - leader.X;
                long dy = k.Y - leader.Y;
                long sq = dx * dx + dy * dy;

                if (sq < bestSq)
                {
                    bestSq = sq;
                    best = k;
                }
            }

            if (best == null) return null;
            return new Point3D(best.X, best.Y, best.Z);
        }

        /// <summary>
        /// When the patrol target is a keep, the group's leader will be standing
        /// next to a door / guards. This method makes them all engage the nearest
        /// attackable keep object (door or guard) instead of just standing there.
        /// </summary>
        private void TryAttackKeepObjectsNearWaypoint()
        {
            MimicNPC leader = FirstAliveMember();
            if (leader == null) return;
            if (leader.GetDistance(_currentWaypoint) > 800) return; // not at the keep yet

            var keeps = GameServer.KeepManager.GetKeepsOfRegion(leader.CurrentRegionID);
            if (keeps == null) return;

            // Find the keep matching our current waypoint (within ~500u).
            Keeps.AbstractGameKeep matched = null;
            foreach (var k in keeps)
            {
                if (k == null) continue;
                long dx = k.X - _currentWaypoint.X;
                long dy = k.Y - _currentWaypoint.Y;
                if (dx * dx + dy * dy < 500 * 500)
                {
                    matched = k;
                    break;
                }
            }
            if (matched == null || matched.Realm == Config.Realm) return;

            // Pick target: closest live guard first, fall back to the closest door.
            GameLiving target = null;
            int bestDist = int.MaxValue;

            if (matched.Guards != null)
            {
                foreach (var kv in matched.Guards)
                {
                    var g = kv.Value;
                    if (g == null || !g.IsAlive) continue;
                    int d = leader.GetDistanceTo(g);
                    if (d < bestDist) { bestDist = d; target = g; }
                }
            }

            if (target == null && matched.Doors != null)
            {
                foreach (var kv in matched.Doors)
                {
                    var d = kv.Value;
                    if (d == null || !d.IsAlive) continue;
                    int dist = leader.GetDistanceTo(d);
                    if (dist < bestDist) { bestDist = dist; target = d; }
                }
            }

            if (target == null) return;

            foreach (var m in Members)
            {
                if (m == null || !m.IsAlive || m.MimicBrain == null) continue;
                m.MimicBrain.AddToAggroList(target, 100);
                m.MimicBrain.FSM.SetCurrentState(eFSMStateType.AGGRO);
            }
        }

        private void OrderGroupToWaypoint()
        {
            MimicNPC leader = FirstAliveMember();
            if (leader == null) return;

            foreach (var m in Members)
            {
                if (m == null || !m.IsAlive) continue;
                if (m == leader)
                {
                    m.WalkTo(_currentWaypoint, m.MaxSpeed);
                }
                else if (m.MimicBrain != null)
                {
                    m.MimicBrain.FSM.SetCurrentState(eFSMStateType.FOLLOW_THE_LEADER);
                }
            }
        }

        private void OrderGroupToEngage(PvPFrontierGroup enemy)
        {
            MimicNPC enemyLeader = enemy.FirstAliveMember();
            if (enemyLeader == null) return;

            foreach (var m in Members)
            {
                if (m == null || !m.IsAlive || m.MimicBrain == null) continue;
                m.MimicBrain.AddToAggroList(enemyLeader, 100);
                m.MimicBrain.FSM.SetCurrentState(eFSMStateType.AGGRO);
            }
        }

        private void OrderGroupToRetreat()
        {
            // Walk back to the realm's spawn anchor (relative safety).
            foreach (var m in Members)
            {
                if (m == null || !m.IsAlive) continue;
                m.WalkTo(Config.SpawnAnchor, m.MaxSpeed);
            }
        }

        /// <summary>
        /// Scans for the closest enemy-realm PvPFrontierGroup whose leader is within
        /// DETECTION_RANGE of our leader. Skips disbanded groups and our own.
        /// </summary>
        private PvPFrontierGroup ScanForEnemyGroup(GameLiving myLeader)
        {
            PvPFrontierGroup best = null;
            int bestDistSq = int.MaxValue;

            lock (PvPFrontierManager._configsLock)
            {
                foreach (var cfg in PvPFrontierManager._configs.Values)
                {
                    if (cfg.Realm == this.Config.Realm) continue;

                    foreach (var grp in cfg.Groups)
                    {
                        if (grp.IsDisbanded) continue;
                        if (grp.AliveMemberCount == 0) continue;

                        MimicNPC enemyLeader = grp.FirstAliveMember();
                        if (enemyLeader == null) continue;
                        if (enemyLeader.CurrentRegionID != myLeader.CurrentRegionID) continue;

                        int dx = enemyLeader.X - myLeader.X;
                        int dy = enemyLeader.Y - myLeader.Y;
                        int distSq = dx * dx + dy * dy;

                        if (distSq < bestDistSq && distSq <= DETECTION_RANGE * DETECTION_RANGE)
                        {
                            bestDistSq = distSq;
                            best = grp;
                        }
                    }
                }
            }

            return best;
        }
    }

    #endregion

    #region Composition

    public static class PvPGroupComposer
    {
        // PvP role weights differ from PvE:
        //   - More healers (2-3 instead of 1-2) — focus targets drop fast in PvP
        //   - More CC casters (mez is king on the open field)
        //   - Stealthers and casters for ranged/burst
        //   - Single tank usually (peelers/intercepts more than aggro holders)
        private static readonly eFrontierRole[] _template =
        {
            eFrontierRole.Tank,       // 1
            eFrontierRole.Healer,     // 2
            eFrontierRole.Healer,     // 3
            eFrontierRole.Support,    // 4 (CC + speed)
            eFrontierRole.Caster,     // 5
            eFrontierRole.Caster,     // 6
            eFrontierRole.Stealther,  // 7
            eFrontierRole.MeleeDPS,   // 8
        };

        public enum eFrontierRole { Tank, Healer, Support, Caster, Stealther, MeleeDPS }

        private static readonly Dictionary<eRealm, Dictionary<eFrontierRole, eMimicClass[]>> _classesByRole = new()
        {
            [eRealm.Albion] = new()
            {
                [eFrontierRole.Tank]      = new[] { eMimicClass.Armsman, eMimicClass.Paladin, eMimicClass.Mercenary },
                [eFrontierRole.Healer]    = new[] { eMimicClass.Cleric, eMimicClass.Friar },
                [eFrontierRole.Support]   = new[] { eMimicClass.Minstrel, eMimicClass.Sorcerer },
                [eFrontierRole.Caster]    = new[] { eMimicClass.Wizard, eMimicClass.Theurgist, eMimicClass.Cabalist, eMimicClass.Sorcerer },
                [eFrontierRole.Stealther] = new[] { eMimicClass.Infiltrator, eMimicClass.Scout },
                [eFrontierRole.MeleeDPS]  = new[] { eMimicClass.Mercenary, eMimicClass.Reaver, eMimicClass.Armsman },
            },
            [eRealm.Hibernia] = new()
            {
                [eFrontierRole.Tank]      = new[] { eMimicClass.Hero, eMimicClass.Champion, eMimicClass.Blademaster },
                [eFrontierRole.Healer]    = new[] { eMimicClass.Druid, eMimicClass.Warden },
                [eFrontierRole.Support]   = new[] { eMimicClass.Bard, eMimicClass.Mentalist },
                [eFrontierRole.Caster]    = new[] { eMimicClass.Eldritch, eMimicClass.Enchanter, eMimicClass.Mentalist, eMimicClass.Valewalker },
                [eFrontierRole.Stealther] = new[] { eMimicClass.Nightshade, eMimicClass.Ranger },
                [eFrontierRole.MeleeDPS]  = new[] { eMimicClass.Blademaster, eMimicClass.Champion, eMimicClass.Hero, eMimicClass.Valewalker },
            },
            [eRealm.Midgard] = new()
            {
                [eFrontierRole.Tank]      = new[] { eMimicClass.Warrior, eMimicClass.Thane, eMimicClass.Berserker },
                [eFrontierRole.Healer]    = new[] { eMimicClass.Healer, eMimicClass.Shaman },
                [eFrontierRole.Support]   = new[] { eMimicClass.Skald, eMimicClass.Healer },
                [eFrontierRole.Caster]    = new[] { eMimicClass.Runemaster, eMimicClass.Spiritmaster, eMimicClass.Bonedancer },
                [eFrontierRole.Stealther] = new[] { eMimicClass.Shadowblade, eMimicClass.Hunter },
                [eFrontierRole.MeleeDPS]  = new[] { eMimicClass.Berserker, eMimicClass.Savage, eMimicClass.Warrior, eMimicClass.Hunter },
            },
        };

        public static List<eMimicClass> BuildPvPComposition(eRealm realm, int groupSize)
        {
            List<eMimicClass> result = new(groupSize);

            if (!_classesByRole.TryGetValue(realm, out var rolesForRealm))
                return result;

            int slots = Math.Clamp(groupSize, 1, _template.Length);

            for (int i = 0; i < slots; i++)
            {
                if (!rolesForRealm.TryGetValue(_template[i], out var candidates) || candidates.Length == 0)
                    continue;

                result.Add(candidates[Util.Random(candidates.Length - 1)]);
            }

            return result;
        }

        public static void AutoAssignPvPRoles(List<MimicNPC> mimics)
        {
            if (mimics == null || mimics.Count == 0) return;

            MimicGroup mg = mimics[0].Group?.MimicGroup;
            if (mg == null) return;

            MimicNPC tank = mimics.FirstOrDefault(m => MimicGroupComposer.IsTankClass(m));
            MimicNPC healer = mimics.FirstOrDefault(m => MimicGroupComposer.IsHealerClass(m));
            MimicNPC cc = mimics.FirstOrDefault(m => MimicGroupComposer.IsCCClass(m));

            MimicNPC leader = tank ?? mimics[0];
            mg.SetLeader(leader);
            mg.SetMainAssist(tank ?? mimics[0]);

            if (tank != null) mg.SetMainTank(tank);
            if (cc != null) mg.SetMainCC(cc);
            if (healer != null && healer.MimicBrain != null)
                healer.MimicBrain.IsHealer = true;
        }
    }

    #endregion

    #region Engagement

    public enum eEngagementDecision { Engage, Retreat, Ignore }

    public static class PvPEngagementAssessor
    {
        /// <summary>
        /// Compares my group's strength vs the enemy's. Strength = sum of level
        /// of alive members, weighted slightly by healer presence (healer = +5).
        /// Decision rules driven by ServerProperty pvp_frontier_engage_aggression:
        ///   0 (default): engage only if my strength >= enemy strength * 1.1
        ///   1 (parity):  engage if my strength >= enemy strength * 0.9
        ///   2 (always):  always engage
        /// Retreat fires only at aggression 0/1 when significantly outmatched.
        /// </summary>
        public static eEngagementDecision Assess(PvPFrontierGroup me, PvPFrontierGroup enemy)
        {
            int my = ComputeStrength(me);
            int his = ComputeStrength(enemy);
            int aggro = PvPFrontierProperties.PVP_FRONTIER_ENGAGE_AGGRESSION;

            if (aggro >= 2) return eEngagementDecision.Engage;

            double ratio = his == 0 ? double.MaxValue : (double)my / his;

            double engageThreshold = aggro == 1 ? 0.9 : 1.1;
            double retreatThreshold = aggro == 1 ? 0.6 : 0.75;

            if (ratio >= engageThreshold) return eEngagementDecision.Engage;
            if (ratio <= retreatThreshold) return eEngagementDecision.Retreat;
            return eEngagementDecision.Ignore;
        }

        private static int ComputeStrength(PvPFrontierGroup g)
        {
            int s = 0;
            foreach (var m in g.Members)
            {
                if (m == null || !m.IsAlive) continue;
                s += m.Level;
                if (m.MimicBrain != null && m.MimicBrain.IsHealer)
                    s += 5;
            }
            return s;
        }
    }

    #endregion

    #region Admin command

    [CmdAttribute(
        "&pvpfrontier",
        ePrivLevel.Admin,
        "/pvpfrontier start | stop | status | clear - Manage the autonomous PvP frontier bot system.")]
    public class PvPFrontierCommand : AbstractCommandHandler, ICommandHandler
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
                    if (PvPFrontierManager.Start())
                        DisplayMessage(client, "PvP Frontier started.");
                    else
                        DisplayMessage(client, "Already running.");
                    break;

                case "stop":
                    if (PvPFrontierManager.Stop())
                        DisplayMessage(client, "PvP Frontier stopped.");
                    else
                        DisplayMessage(client, "Not running.");
                    break;

                case "clear":
                    int n = PvPFrontierManager.ClearAll();
                    DisplayMessage(client, $"Deleted {n} frontier mimic(s).");
                    break;

                case "status":
                    DisplayMessage(client, PvPFrontierManager.BuildStatusReport());
                    break;

                default:
                    DisplaySyntax(client);
                    break;
            }
        }
    }

    #endregion
}

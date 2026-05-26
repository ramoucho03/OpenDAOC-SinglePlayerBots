using DOL.AI.Brain;
using DOL.Database;
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
            "Region ID for the shared PvP frontier zone. Default 163 (New Frontiers — the unified NF region on OpenDAOC). All three realms run on this single region and converge on the contested center.", 163)]
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

        [ServerProperty("pvpfrontier", "pvp_frontier_min_group_size",
            "Minimum mimics per spawned frontier group. Set to 1 to allow solo roamers and small skirmish groups (1-3), or keep at 4 for full party rolls only.", 1)]
        public static int PVP_FRONTIER_MIN_GROUP_SIZE;

        [ServerProperty("pvpfrontier", "pvp_frontier_max_group_size",
            "Maximum mimics per spawned frontier group (hard cap = full DAoC group of 8).", 8)]
        public static int PVP_FRONTIER_MAX_GROUP_SIZE;

        [ServerProperty("pvpfrontier", "pvp_frontier_group_size_weights",
            "Weighted distribution for spawned group sizes 1..8, comma-separated. Higher weight = more groups of that size. Default biases strongly toward full 8-man parties, with occasional skirmish teams and rare solo roamers — mirrors what a populated frontier actually looks like.",
            "3,5,7,10,12,15,20,28")]
        public static string PVP_FRONTIER_GROUP_SIZE_WEIGHTS;

        [ServerProperty("pvpfrontier", "pvp_frontier_include_bgs",
            "Also spawn intelligent PvP frontier groups in every Battleground region from the DB (Thidranki, Molvik, Cathal Valley, etc.), scaled to each BG's level bracket. Default true — gives the frontier-style smart AI to all BGs, not just NF.",
            true)]
        public static bool PVP_FRONTIER_INCLUDE_BGS;

        [ServerProperty("pvpfrontier", "pvp_frontier_bg_population_per_realm",
            "Target number of mimic bots maintained per realm in each Battleground region (separate from the main frontier population). Default 30 — keeps BGs lively without saturating a low-pop server.", 30)]
        public static int PVP_FRONTIER_BG_POPULATION_PER_REALM;

        // ---- Dynamic spawn (spawn-near-player) ----
        // Spawns frontier groups around active players in the region instead of
        // at fixed realm anchors. Out-of-vision but close enough to be reached
        // by foot in a couple of minutes — the player feels "encounters happen"
        // without the server pinning hundreds of bots out in empty corners of
        // the map.

        [ServerProperty("pvpfrontier", "pvp_frontier_dynamic_spawn",
            "Enable dynamic spawn: new frontier groups materialise in a ring around real players in the region (out of visibility) instead of at fixed realm anchors. Default true — saves CPU/memory by not maintaining far-away bots.", true)]
        public static bool PVP_FRONTIER_DYNAMIC_SPAWN;

        [ServerProperty("pvpfrontier", "pvp_frontier_dynamic_spawn_inner",
            "Minimum distance (units) between a dynamic-spawn point and the anchor player. Must stay well above visibility range (~3500u) so groups never pop into view. Default 8000.", 8000)]
        public static int PVP_FRONTIER_DYNAMIC_SPAWN_INNER;

        [ServerProperty("pvpfrontier", "pvp_frontier_dynamic_spawn_outer",
            "Maximum distance (units) between a dynamic-spawn point and the anchor player. Wider = more dispersed groups, narrower = denser encounters. Default 13000 (~4 min walk).", 13000)]
        public static int PVP_FRONTIER_DYNAMIC_SPAWN_OUTER;

        [ServerProperty("pvpfrontier", "pvp_frontier_dynamic_spawn_skip_if_empty",
            "When true and dynamic spawn is on, skip spawning entirely in regions where no real player is present. Saves CPU when the frontier is deserted. Default true.", true)]
        public static bool PVP_FRONTIER_DYNAMIC_SPAWN_SKIP_IF_EMPTY;

        [ServerProperty("pvpfrontier", "pvp_frontier_dynamic_spawn_prefer_opposite",
            "When true, dynamic spawn prefers placing groups around players of an OPPOSING realm so encounters happen quickly. Falls back to any player if no opposing realm is in the region. Default true.", true)]
        public static bool PVP_FRONTIER_DYNAMIC_SPAWN_PREFER_OPPOSITE;

        [ServerProperty("pvpfrontier", "pvp_frontier_player_track_radius",
            "When picking the next patrol waypoint, frontier groups can bias toward the closest enemy-realm player within this range (units). Higher = bots follow players across larger swaths of the map. Default 12000 (~3 zones).", 12000)]
        public static int PVP_FRONTIER_PLAYER_TRACK_RADIUS;

        [ServerProperty("pvpfrontier", "pvp_frontier_player_track_chance",
            "Chance (0-100) per next-waypoint pick that a frontier group routes toward the closest enemy-realm player in the region instead of a random patrol waypoint. Default 50 — half the picks track players, half stay on the configured loop, so groups follow without locking onto a single target.", 50)]
        public static int PVP_FRONTIER_PLAYER_TRACK_CHANCE;
    }

    #endregion

    #region Manager

    public static class PvPFrontierManager
    {
        private static readonly Logger log = LoggerManager.Create(MethodBase.GetCurrentMethod().DeclaringType);

        // Per-realm spawn anchors. Bots spawn in a radius around these and pick
        // patrol waypoints from the realm's waypoint list.
        // These coordinates target the New Frontiers map (region 163). The
        // three realms have distinct corners that converge toward the contested
        // center where encounters happen. An admin can override per-realm
        // anchors at runtime via SetSpawnAnchor.
        public sealed class RealmConfig
        {
            public eRealm Realm;
            public Point3D SpawnAnchor;
            public List<Point3D> PatrolWaypoints = new();
            public List<PvPFrontierGroup> Groups = new();

            // Per-config region / level / population. Carried on the config so
            // every BG region runs its own population scaled to its bracket.
            // Defaults match the single-region NF behaviour when not set.
            public ushort Region;
            public byte MinLevel;
            public byte MaxLevel;
            public int TargetPopulation;

            // Friendly label for log lines and admin commands. Resolves from
            // the region description at config-build time so it survives
            // reloads without re-querying WorldMgr.
            public string ZoneLabel = "Frontier";
        }

        // Two-level storage: region → (realm → config). Lets the manager run
        // one independent population per BG region in parallel with the main
        // NF frontier. Lookups stay O(1) and the maintenance loop is just a
        // nested foreach.
        internal static readonly Dictionary<ushort, Dictionary<eRealm, RealmConfig>> _configs = new();
        internal static readonly object _configsLock = new();
        private static ECSGameTimer _tickTimer;
        private static bool _running;
        private const int TICK_MS = 5000;            // population/maintenance tick
        private const int GROUP_TICK_MS = 1000;      // per-group AI tick (was 2000 — halved for snappier reactions / waypoint advancement / scan cadence)

        // ----- New Frontiers (the active RvR region) -----
        // Region 163 (NF) is the unified frontier theatre — all three realms
        // share the same map and converge at the contested center. Keeps,
        // relics, and the mimic-frontier population all live here. The legacy
        // realm-home regions (1 Albion / 100 Midgard / 200 Hibernia) carry
        // their normal PvE leveling content and are NOT part of the RvR map.
        // The BG auto-discovery (see BuildBattlegroundConfigs) skips this
        // region so we don't duplicate the NF config.
        internal const ushort REGION_NEW_FRONTIERS = 163;

        // ----- GLOBAL hydration budget -----
        // MimicNPC construction (spec/skill/equipment/ROG resolution) is
        // CPU-heavy and runs synchronously on the GameLoop thread. We size
        // this budget so a FULL 8-man group can materialise in a single
        // maintenance tick — the user spec is "le groupe pop d'un coup,
        // complet et totalement pret a se battre". A staggered hydration
        // breaks the spec: half the bots arrive, the player engages them,
        // and the rest pop in mid-fight from the player's perspective.
        // Sized to 16 so two groups can co-spawn on the same tick when
        // multiple realms decide to converge on the player at once.
        // Worst-case stall is ~16 constructions / tick, ~300-500 ms —
        // a one-shot price the user explicitly accepted for the "pop d'un
        // coup" behaviour.
        private const int MAX_HYDRATIONS_PER_TICK = 16;
        internal static int HydrationBudgetRemaining;

        /// <summary>
        /// Consumes one unit of the global per-tick hydration budget.
        /// Returns false when the budget for this maintenance tick is spent —
        /// the caller must defer the remaining construction to a later tick.
        /// </summary>
        internal static bool TryConsumeHydrationBudget()
        {
            if (HydrationBudgetRemaining <= 0)
                return false;
            HydrationBudgetRemaining--;
            return true;
        }

        public static bool IsRunning => _running;
        public static int TotalLiveBots
        {
            get
            {
                lock (_configsLock)
                {
                    int total = 0;
                    foreach (var byRealm in _configs.Values)
                        foreach (var cfg in byRealm.Values)
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
            // New Frontiers (region 163). All three realms share one map and
            // converge on a contested center zone, so every realm sees both
            // others on a single config — exactly what makes a populated NF
            // feel populated. Each realm gets the full target population in
            // this region (no inter-region split, unlike the old per-realm
            // home-region setup), so 3 × PVP_FRONTIER_POPULATION_PER_REALM
            // bots roam the same map.
            ushort nfRegion = (ushort)PvPFrontierProperties.PVP_FRONTIER_REGION;
            int nfPop = Math.Max(0, PvPFrontierProperties.PVP_FRONTIER_POPULATION_PER_REALM);

            lock (_configsLock)
            {
                _configs.Clear();

                // ===== New Frontiers (region 163) =====
                //
                // Coordinate map (units, all at z=3500 on the open plain):
                //
                //                       (Mid: 55,25)           (Alb: 45,55)
                //                       /                       \
                //                      / mid mid (45,30)         \
                //                     /                  alb mid (45,42)
                //         contested center (40,35) <----- all three realms converge here
                //                     \                 hib mid (32,32)
                //                      \  hib raid (35,40)  /
                //                       (Hib: 25,22) ------/
                //
                // The shared "contested" waypoint at (40_000, 35_000) is the
                // canonical convergence point — every realm's loop includes
                // it. The deeper "raid" waypoints push one realm well past
                // center into enemy territory so bot groups regularly cross
                // patrols deep in the other realms' lanes (creates rolling
                // skirmishes instead of one giant deathball at center).

                Point3D contestedCenter = new(40_000, 35_000, 3_500);

                // Albion (NE corner)
                Point3D albHome      = new(45_000, 55_000, 3_500);
                Point3D albMid       = new(45_000, 42_000, 3_500);  // forward staging
                Point3D albMidRaid   = new(50_000, 30_000, 3_500);  // deep mid territory
                Point3D albHibRaid   = new(32_000, 30_000, 3_500);  // deep hib territory

                // Midgard (E corner)
                Point3D midHome      = new(55_000, 25_000, 3_500);
                Point3D midMid       = new(48_000, 30_000, 3_500);  // forward staging
                Point3D midAlbRaid   = new(45_000, 48_000, 3_500);  // deep alb territory
                Point3D midHibRaid   = new(28_000, 28_000, 3_500);  // deep hib territory

                // Hibernia (SW corner)
                Point3D hibHome      = new(25_000, 22_000, 3_500);
                Point3D hibMid       = new(32_000, 32_000, 3_500);  // forward staging
                Point3D hibAlbRaid   = new(45_000, 50_000, 3_500);  // deep alb territory
                Point3D hibMidRaid   = new(50_000, 28_000, 3_500);  // deep mid territory

                Dictionary<eRealm, RealmConfig> nfConfigs = new()
                {
                    [eRealm.Albion] = MakeFrontierConfig(eRealm.Albion, nfRegion, "New Frontiers",
                        albHome, new()
                        {
                            albHome,        // 1. regroup at home anchor
                            albMid,         // 2. forward staging
                            contestedCenter,// 3. converge at center
                            albMidRaid,     // 4. push into Mid territory
                            albMid,         // 5. swing back through staging
                            contestedCenter,// 6. back through center
                            albHibRaid,     // 7. push into Hib territory
                        }, nfPop),

                    [eRealm.Midgard] = MakeFrontierConfig(eRealm.Midgard, nfRegion, "New Frontiers",
                        midHome, new()
                        {
                            midHome,
                            midMid,
                            contestedCenter,
                            midAlbRaid,
                            midMid,
                            contestedCenter,
                            midHibRaid,
                        }, nfPop),

                    [eRealm.Hibernia] = MakeFrontierConfig(eRealm.Hibernia, nfRegion, "New Frontiers",
                        hibHome, new()
                        {
                            hibHome,
                            hibMid,
                            contestedCenter,
                            hibAlbRaid,
                            hibMid,
                            contestedCenter,
                            hibMidRaid,
                        }, nfPop),
                };

                _configs[nfRegion] = nfConfigs;

                // ----- Battlegrounds (auto-discovered from DB) -----
                if (PvPFrontierProperties.PVP_FRONTIER_INCLUDE_BGS)
                    BuildBattlegroundConfigs();
            }
        }

        /// <summary>
        /// Builds one realm's RvR config: spawn anchor, patrol route through
        /// the frontier zones, level bracket and target population. The level
        /// bracket comes from the global frontier min/max-level server
        /// properties.
        /// </summary>
        private static RealmConfig MakeFrontierConfig(eRealm realm, ushort region, string label,
            Point3D anchor, List<Point3D> waypoints, int targetPop)
        {
            byte minLvl = (byte)PvPFrontierProperties.PVP_FRONTIER_MIN_LEVEL;
            byte maxLvl = (byte)PvPFrontierProperties.PVP_FRONTIER_MAX_LEVEL;
            if (maxLvl < minLvl) maxLvl = minLvl;

            return new RealmConfig
            {
                Realm = realm,
                Region = region,
                MinLevel = minLvl,
                MaxLevel = maxLvl,
                TargetPopulation = targetPop,
                ZoneLabel = label,
                SpawnAnchor = anchor,
                PatrolWaypoints = waypoints,
            };
        }

        /// <summary>
        /// Scans the Battleground DB table and adds a per-realm RealmConfig
        /// for each BG region, so the frontier-style smart AI runs in every
        /// BG too. Skips the NF region so we don't duplicate the main config,
        /// and skips rows whose Region doesn't resolve (server map mismatch).
        /// </summary>
        private static void BuildBattlegroundConfigs()
        {
            int bgPop = Math.Max(0, PvPFrontierProperties.PVP_FRONTIER_BG_POPULATION_PER_REALM);

            // Shared per-realm anchor coordinates. BGs all use the same
            // realm-corner layout in OpenDAOC; the portal keep locations
            // resolved at hydration time pin bots into the right zone.
            Point3D albAnchor = new(37_200, 51_200, 3_950);
            Point3D hibAnchor = new(19_820, 19_305, 4_050);
            Point3D midAnchor = new(53_300, 26_100, 4_270);

            foreach (DbBattleground bg in GameServer.Database.SelectAllObjects<DbBattleground>())
            {
                if (bg == null) continue;
                if (bg.MinLevel == 0 || bg.MaxLevel == 0 || bg.MaxLevel < bg.MinLevel)
                    continue;
                if (bg.RegionID == REGION_NEW_FRONTIERS)
                    continue; // already configured as the main NF frontier
                if (WorldMgr.GetRegion(bg.RegionID) == null) continue;

                string label = WorldMgr.GetRegion(bg.RegionID)?.Description ?? $"BG L{bg.MinLevel}-{bg.MaxLevel}";

                Dictionary<eRealm, RealmConfig> byRealm = new()
                {
                    [eRealm.Albion] = MakeBgConfig(eRealm.Albion, bg, albAnchor, bgPop, label),
                    [eRealm.Hibernia] = MakeBgConfig(eRealm.Hibernia, bg, hibAnchor, bgPop, label),
                    [eRealm.Midgard] = MakeBgConfig(eRealm.Midgard, bg, midAnchor, bgPop, label),
                };
                _configs[bg.RegionID] = byRealm;
            }
        }

        private static RealmConfig MakeBgConfig(eRealm realm, DbBattleground bg, Point3D anchor, int targetPop, string label)
        {
            return new RealmConfig
            {
                Realm = realm,
                Region = bg.RegionID,
                MinLevel = bg.MinLevel,
                MaxLevel = bg.MaxLevel,
                TargetPopulation = targetPop,
                ZoneLabel = label,
                SpawnAnchor = anchor,
                // Single-waypoint patrol = bots roam around the anchor's
                // radius. BG maps are small so we don't need a full route;
                // the engagement loop drives chases from contact anyway.
                PatrolWaypoints = new() { anchor },
            };
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
                foreach (var byRealm in _configs.Values)
                {
                    foreach (var cfg in byRealm.Values)
                    {
                        foreach (var grp in cfg.Groups.ToList())
                        {
                            removed += grp.DisbandAndDelete();
                        }
                        cfg.Groups.Clear();
                    }
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
                // Reset the global hydration budget for this tick. No matter
                // how many groups want to materialise their bots, only
                // MAX_HYDRATIONS_PER_TICK MimicNPCs are constructed this
                // maintenance pass — the rest defer to subsequent ticks.
                // This is what bounds the GameLoop stall and stops the
                // "every NPC blinks every 5s" freeze.
                HydrationBudgetRemaining = MAX_HYDRATIONS_PER_TICK;

                // Snapshot player positions ONCE per tick. Every group's
                // IsPlayerWithin check then consults the snapshot instead of
                // re-walking ClientService for every group + every range
                // probe — O(groups × players) collapses to O(players) per
                // tick + O(groups) cache lookups.
                RefreshPlayerSnapshots();

                lock (_configsLock)
                {
                    foreach (var byRealm in _configs.Values)
                    {
                        foreach (RealmConfig cfg in byRealm.Values)
                        {
                            // Step every group; prune disbanded ones.
                            for (int i = cfg.Groups.Count - 1; i >= 0; i--)
                            {
                                PvPFrontierGroup grp = cfg.Groups[i];
                                grp.Tick();

                                // Defensive corpse sweep. Frontier mimics
                                // normally die through MimicNPC.ProcessDeath
                                // → base.ProcessDeath → Delete() (the rez-
                                // wait branch is gated on OwnerAccount which
                                // frontier bots never have). However at least
                                // one death path can leave the GameObject
                                // Active (visible as grey-name corpses that
                                // never despawn). Walk every hydrated group's
                                // roster here and force-delete anything that
                                // is simultaneously NOT alive and STILL active
                                // — that's the corpse signature.
                                if (grp.IsHydrated)
                                {
                                    foreach (var member in grp.Members)
                                    {
                                        if (member == null) continue;
                                        if (member.IsAlive) continue;
                                        if (member.ObjectState != GameObject.eObjectState.Active) continue;
                                        try { member.Delete(); } catch (Exception delEx)
                                        {
                                            log.Warn("PvPFrontier: corpse sweep failed to delete " + member.Name, delEx);
                                        }
                                    }
                                }

                                if (grp.IsDisbanded)
                                {
                                    // Tick()'s disband paths only flip the state;
                                    // any hydrated members are still live world
                                    // objects (corpses included). Delete them
                                    // before dropping the group reference.
                                    grp.DisbandAndDelete();
                                    cfg.Groups.RemoveAt(i);
                                }
                            }

                            // Each config carries its own target population
                            // (per-region, per-realm). BG configs use the
                            // smaller bg-population setting; the main NF
                            // config uses the full frontier target.
                            int target = cfg.TargetPopulation;
                            if (target <= 0)
                                continue;

                            int alive = 0;
                            foreach (var g in cfg.Groups)
                                alive += g.AliveMemberCount;

                            int missing = target - alive;
                            if (missing <= 0)
                                continue;

                            // Spawn one group per tick per config (avoid burst).
                            int minSize = Math.Clamp(PvPFrontierProperties.PVP_FRONTIER_MIN_GROUP_SIZE, 1, 8);
                            int maxSize = Math.Clamp(PvPFrontierProperties.PVP_FRONTIER_MAX_GROUP_SIZE, minSize, 8);
                            int rolledSize = RollWeightedGroupSize(minSize, maxSize);
                            int groupSize = Math.Min(rolledSize, Math.Max(1, missing));

                            // Dynamic-spawn pick. The picker is gated on the
                            // PVP_FRONTIER_DYNAMIC_SPAWN property and returns
                            // a ring-around-a-real-player position when at
                            // least one player is in the region's snapshot.
                            Point3D dynamicAnchor = null;
                            bool gotDynamic = TryPickDynamicSpawnPosition(cfg, out Point3D pickedPos);
                            if (gotDynamic)
                                dynamicAnchor = pickedPos;

                            // Resource-saver: when dynamic spawn is enabled,
                            // skip-if-empty is on, AND no player anchor was
                            // found, we DON'T fall back to the static realm
                            // anchor. The frontier is deserted in this region
                            // — there's nobody to discover the new group, so
                            // building bots only to have them dehydrate
                            // immediately wastes CPU and memory. The group
                            // slot stays "missing" in this config; the next
                            // tick re-tries when a player may have arrived.
                            if (PvPFrontierProperties.PVP_FRONTIER_DYNAMIC_SPAWN
                                && PvPFrontierProperties.PVP_FRONTIER_DYNAMIC_SPAWN_SKIP_IF_EMPTY
                                && !gotDynamic)
                                continue;

                            PvPFrontierGroup newGroup = PvPFrontierGroup.Spawn(cfg, groupSize, dynamicAnchor);
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

        // ----- Group size distribution -----
        // Real RvR has lots of full 8-man parties, fewer skirmish teams, and
        // rare solo roamers. Roll a weighted random size in [min..max] based
        // on the configured weights so the population looks like a populated
        // frontier instead of a uniform mix.
        private static int[] _parsedSizeWeights;
        private static string _parsedSizeWeightsSource;

        private static int[] GetGroupSizeWeights()
        {
            string raw = PvPFrontierProperties.PVP_FRONTIER_GROUP_SIZE_WEIGHTS ?? "3,5,7,10,12,15,20,28";
            if (_parsedSizeWeights != null && string.Equals(_parsedSizeWeightsSource, raw, StringComparison.Ordinal))
                return _parsedSizeWeights;

            int[] result = new int[8];
            // Defaults if parsing fails partway through.
            int[] defaults = { 3, 5, 7, 10, 12, 15, 20, 28 };
            Array.Copy(defaults, result, 8);

            try
            {
                string[] parts = raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                for (int i = 0; i < parts.Length && i < 8; i++)
                {
                    if (int.TryParse(parts[i], out int w) && w >= 0)
                        result[i] = w;
                }
            }
            catch { /* keep defaults */ }

            _parsedSizeWeights = result;
            _parsedSizeWeightsSource = raw;
            return result;
        }

        internal static int RollWeightedGroupSize(int minSize, int maxSize)
        {
            int[] weights = GetGroupSizeWeights();

            // Sum the weights of the sizes inside [minSize..maxSize].
            int total = 0;
            for (int s = minSize; s <= maxSize; s++)
                total += weights[s - 1];

            if (total <= 0)
                return Util.Random(minSize, maxSize); // pathological config, fall back to uniform

            int roll = Util.Random(1, total);
            int running = 0;
            for (int s = minSize; s <= maxSize; s++)
            {
                running += weights[s - 1];
                if (roll <= running)
                    return s;
            }

            return maxSize;
        }

        // ----- Per-tick player snapshot -----
        // IsPlayerWithin used to call ClientService.GetPlayersOfRegion on
        // every check. With 400 bots × 3 realms × N players per region that
        // becomes O(groups × players) per maintenance tick — measurable on
        // a populated server. Snapshot once per maintenance tick and let
        // each group consult the cache instead of re-walking the client list.
        internal sealed class PlayerSnapshot
        {
            public List<PlayerSampledPos> Sampled = new(16);
        }

        internal struct PlayerSampledPos
        {
            public int X;
            public int Y;
            public int Z;
            public eRealm Realm;
        }

        internal static readonly Dictionary<ushort, PlayerSnapshot> _playerSnapshotByRegion = new();
        internal static long _playerSnapshotTick;
        internal static readonly object _playerSnapshotLock = new();

        private static void RefreshPlayerSnapshots()
        {
            // Collect every region that has at least one group registered to
            // it. With multi-region BG support, this naturally covers NF +
            // every configured BG region in one pass.
            HashSet<ushort> regions = new();
            lock (_configsLock)
            {
                foreach (ushort regionId in _configs.Keys)
                    regions.Add(regionId);
                foreach (var byRealm in _configs.Values)
                    foreach (var cfg in byRealm.Values)
                        foreach (var g in cfg.Groups)
                            if (g.Region != 0)
                                regions.Add(g.Region);
            }
            if (regions.Count == 0)
                regions.Add((ushort)PvPFrontierProperties.PVP_FRONTIER_REGION);

            lock (_playerSnapshotLock)
            {
                _playerSnapshotByRegion.Clear();

                foreach (ushort regId in regions)
                {
                    global::DOL.GS.Region region = WorldMgr.GetRegion(regId);
                    if (region == null)
                        continue;

                    PlayerSnapshot snap = new();
                    foreach (GamePlayer p in ClientService.Instance.GetPlayersOfRegion(region))
                    {
                        if (p == null || !p.IsAlive)
                            continue;
                        // GMs / admins (PrivLevel > 1) are KEPT in the
                        // snapshot. They count as presence for hydration
                        // triggers and dynamic-spawn anchoring — without
                        // this an admin patrolling NF to verify the system
                        // never sees a single mimic group, because the
                        // skip-if-empty path treats the region as deserted
                        // (no PrivLevel-1 player anchor → no spawn → no
                        // encounters). Aggro / kill / RP paths have their
                        // own PrivLevel guards downstream so admins still
                        // can't be targeted or farmed for points by bots.
                        snap.Sampled.Add(new PlayerSampledPos { X = p.X, Y = p.Y, Z = p.Z, Realm = p.Realm });
                    }

                    _playerSnapshotByRegion[regId] = snap;
                }

                _playerSnapshotTick = GameLoop.GameLoopTime;
            }
        }

        internal static PlayerSnapshot GetPlayerSnapshot(ushort regionId)
        {
            lock (_playerSnapshotLock)
                return _playerSnapshotByRegion.TryGetValue(regionId, out PlayerSnapshot s) ? s : null;
        }

        /// <summary>
        /// Picks a spawn position near (but out of vision of) a real player in
        /// the config's region. The position sits on a ring around the chosen
        /// player: inner radius keeps the spawn invisible at materialisation
        /// time, outer radius keeps it close enough that the group will reach
        /// the player by walking in within a couple of minutes.
        ///
        /// Returns false (and `position` is undefined) when:
        ///   - dynamic spawn is disabled,
        ///   - no real player is registered in the region's snapshot,
        ///   - no candidate passes the opposing-realm filter (when enabled)
        ///     and no fallback candidate exists.
        ///
        /// Caller (<see cref="MaintenanceTick"/>) treats a `false` return as
        /// "skip spawning this group for now" — the static-anchor fallback is
        /// optional and gated by <see cref="PvPFrontierProperties.PVP_FRONTIER_DYNAMIC_SPAWN_SKIP_IF_EMPTY"/>.
        /// </summary>
        internal static bool TryPickDynamicSpawnPosition(RealmConfig cfg, out Point3D position)
        {
            position = default;
            if (cfg == null) return false;
            if (!PvPFrontierProperties.PVP_FRONTIER_DYNAMIC_SPAWN) return false;

            PlayerSnapshot snap = GetPlayerSnapshot(cfg.Region);
            if (snap == null || snap.Sampled.Count == 0) return false;

            int inner = Math.Max(500, PvPFrontierProperties.PVP_FRONTIER_DYNAMIC_SPAWN_INNER);
            int outer = Math.Max(inner + 100, PvPFrontierProperties.PVP_FRONTIER_DYNAMIC_SPAWN_OUTER);
            int ringWidth = outer - inner;

            // Candidate filter: prefer players whose realm is NOT this group's
            // realm so encounters happen quickly. Fall back to any player if
            // the opposing filter empties the pool (e.g. only home-realm
            // players around). Track the indices of the two pools so the random
            // pick stays O(1) instead of allocating two filtered lists.
            List<int> opposing = null;
            List<int> any = null;
            for (int i = 0; i < snap.Sampled.Count; i++)
            {
                PlayerSampledPos sp = snap.Sampled[i];
                (any ??= new List<int>(snap.Sampled.Count)).Add(i);
                if (PvPFrontierProperties.PVP_FRONTIER_DYNAMIC_SPAWN_PREFER_OPPOSITE
                    && sp.Realm != cfg.Realm && sp.Realm != eRealm.None)
                {
                    (opposing ??= new List<int>(snap.Sampled.Count)).Add(i);
                }
            }

            List<int> pool = opposing != null && opposing.Count > 0 ? opposing : any;
            if (pool == null || pool.Count == 0) return false;

            PlayerSampledPos anchor = snap.Sampled[pool[Util.Random(pool.Count - 1)]];

            // Random angle + random radius in [inner, outer]. Uniform on the
            // ring without bias toward the inner edge: sqrt() correction on the
            // 0..1 radius factor keeps the area-density flat.
            double angle = Util.Random(0, 359) * Math.PI / 180.0;
            double radiusFactor = Math.Sqrt(Util.RandomDouble());
            double radius = inner + radiusFactor * ringWidth;

            int sx = anchor.X + (int)Math.Round(Math.Cos(angle) * radius);
            int sy = anchor.Y + (int)Math.Round(Math.Sin(angle) * radius);

            // Reuse the anchor player's Z. The picked point is on the same map
            // sheet within ~6.5k units, so the Z is normally close enough; the
            // bot's first patrol-step path resolves the real terrain Z anyway.
            position = new Point3D(sx, sy, anchor.Z);
            return true;
        }

        // ----- Recent enemy hotspots -----
        // Whenever a group engages an enemy realm group, record the location.
        // Other friendly groups consult the hotspot list when picking a
        // waypoint, which makes them converge on contested zones the way
        // real players gravitate toward the action.
        internal struct EnemyHotspot
        {
            public ushort Region;
            public eRealm ObservingRealm;   // realm of the group that recorded it
            public int X;
            public int Y;
            public int Z;
            public long ExpireTick;
        }

        private static readonly List<EnemyHotspot> _hotspots = new(32);
        private static readonly object _hotspotsLock = new();
        private const int HOTSPOT_LIFETIME_MS = 90_000;     // 90s decay — long enough for a fight, short enough that the world moves on
        private const int HOTSPOT_MAX_ENTRIES = 64;

        public static void RegisterEnemyHotspot(ushort region, eRealm observingRealm, int x, int y, int z)
        {
            long now = GameLoop.GameLoopTime;
            lock (_hotspotsLock)
            {
                // GC expired entries while we're here.
                _hotspots.RemoveAll(h => h.ExpireTick <= now);

                _hotspots.Add(new EnemyHotspot
                {
                    Region = region,
                    ObservingRealm = observingRealm,
                    X = x,
                    Y = y,
                    Z = z,
                    ExpireTick = now + HOTSPOT_LIFETIME_MS,
                });

                // Bound the list so a long-running engagement doesn't leak.
                if (_hotspots.Count > HOTSPOT_MAX_ENTRIES)
                    _hotspots.RemoveRange(0, _hotspots.Count - HOTSPOT_MAX_ENTRIES);
            }
        }

        /// <summary>
        /// Pick a recent enemy hotspot from the perspective of `myRealm` in
        /// `regionId`. Hotspots logged by the SAME realm are skipped (we don't
        /// want a group of Albs to chase another Alb group's engagement —
        /// they'd just walk to a friendly fight). Returns null if no relevant
        /// hotspot exists.
        /// </summary>
        public static Point3D PickRecentEnemyHotspot(ushort regionId, eRealm myRealm)
        {
            long now = GameLoop.GameLoopTime;
            lock (_hotspotsLock)
            {
                List<EnemyHotspot> candidates = null;
                for (int i = 0; i < _hotspots.Count; i++)
                {
                    EnemyHotspot h = _hotspots[i];
                    if (h.Region != regionId) continue;
                    if (h.ExpireTick <= now) continue;
                    if (h.ObservingRealm == myRealm) continue;
                    candidates ??= new List<EnemyHotspot>(4);
                    candidates.Add(h);
                }

                if (candidates == null || candidates.Count == 0)
                    return null;

                EnemyHotspot pick = candidates[Util.Random(candidates.Count - 1)];
                return new Point3D(pick.X, pick.Y, pick.Z);
            }
        }

        public static string BuildStatusReport()
        {
            System.Text.StringBuilder sb = new();
            sb.AppendLine("=== PvP Frontier status ===");
            sb.AppendLine($"Running: {_running}");
            sb.AppendLine($"NF target population/realm: {PvPFrontierProperties.PVP_FRONTIER_POPULATION_PER_REALM} (single region 163, three realms share the map)");
            sb.AppendLine($"BG target population/realm: {PvPFrontierProperties.PVP_FRONTIER_BG_POPULATION_PER_REALM} (BGs auto-included: {PvPFrontierProperties.PVP_FRONTIER_INCLUDE_BGS})");
            sb.AppendLine();
            sb.AppendLine($"{"Zone",-20} {"Realm",-9} {"groups",6} {"logical",7} {"hydrated",8} {"npcs",5}");

            lock (_configsLock)
            {
                foreach (var kv in _configs)
                {
                    foreach (var cfg in kv.Value.Values)
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
                        sb.AppendLine($"{cfg.ZoneLabel,-20} {cfg.Realm,-9} {cfg.Groups.Count,6} {logical,7} {hydrated,8} {npcs,5}");
                    }
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

        // Minimum time a group stays materialised once hydrated, regardless of
        // player distance. Without this floor, a hydrated group that patrols
        // away from the player who woke it crosses DEHYDRATE_RANGE, dehydrates,
        // goes dormant, drifts back, and re-hydrates — the bots visibly
        // appear / disappear / reset on a loop. The floor guarantees a group
        // lives long enough to be a real encounter before it can be recycled.
        private const int MIN_HYDRATED_LIFETIME_MS = 60_000;

        // Dormant movement: how often we advance VirtualPosition and how far
        // each step covers. 5s tick * 190 (run speed) ≈ 950u per step.
        private const int DORMANT_STEP_MS = 5_000;
        private const int DORMANT_STEP_DISTANCE = 950;

        private Point3D _currentWaypoint;
        private long _nextScanMs;
        private long _retreatUntilMs;
        private long _dehydrateAfterMs;   // 0 = no pending dehydration
        private long _nextDormantStepMs;
        private long _hydratedSinceMs;    // GameLoopTime of the last Hydrate()

        // Stuck detection state — leader position sampled each patrol tick.
        // If the leader hasn't moved STUCK_MIN_DELTA² in STUCK_GRACE_MS we
        // force a fresh waypoint pick.
        private int _lastStuckX;
        private int _lastStuckY;
        private long _lastStuckSampleMs;
        private const int STUCK_MIN_DELTA_SQ = 200 * 200; // 200u of movement in the window
        private const int STUCK_GRACE_MS = 6000;

        // After leaving combat, the group rests this long before scanning for
        // new engagements. Cut from 30 s → 8 s so the frontier feels alive
        // — the original window made groups stand still post-fight long
        // enough that a roaming player got the same patrolling group three
        // times in a row without ever seeing it actually move.
        private const int POST_FIGHT_COOLDOWN_MS = 8_000;
        private long _postFightUntilMs;

        private PvPFrontierGroup(PvPFrontierManager.RealmConfig cfg) => Config = cfg;

        /// <summary>
        /// Composes a roster (class + level per slot) and registers the group
        /// in dormant mode at the realm's anchor. NO MimicNPC is instantiated —
        /// that happens lazily on hydration. Returns null only if the composer
        /// produces no classes (catalog edge case).
        /// </summary>
        public static PvPFrontierGroup Spawn(PvPFrontierManager.RealmConfig cfg, int groupSize, Point3D overrideAnchor = null)
        {
            PvPFrontierGroup g = new(cfg);

            List<eMimicClass> comp = PvPGroupComposer.BuildPvPComposition(cfg.Realm, groupSize);
            if (comp.Count == 0)
                return null;

            // Bot levels and target region come from the RealmConfig so each
            // BG config produces bots in its own bracket (Thidranki 20-24,
            // Molvik 35-39, etc.). The main NF config is special-cased to
            // ALWAYS spawn level-50 mimics regardless of the configured
            // min/max — NF is the end-game RvR theatre, and mixed-level
            // groups don't make sense there.
            bool isNfRegion = (cfg.Region == PvPFrontierManager.REGION_NEW_FRONTIERS)
                              || (cfg.Region == 0 && PvPFrontierProperties.PVP_FRONTIER_REGION == PvPFrontierManager.REGION_NEW_FRONTIERS);

            byte minLevel = cfg.MinLevel > 0 ? cfg.MinLevel : (byte)PvPFrontierProperties.PVP_FRONTIER_MIN_LEVEL;
            byte maxLevel = cfg.MaxLevel > 0 ? cfg.MaxLevel : (byte)PvPFrontierProperties.PVP_FRONTIER_MAX_LEVEL;
            if (maxLevel < minLevel) maxLevel = minLevel;

            foreach (eMimicClass cls in comp)
            {
                byte level = isNfRegion ? (byte)50 : (byte)Util.Random(minLevel, maxLevel);
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

            g.Region = cfg.Region != 0
                ? cfg.Region
                : (ushort)PvPFrontierProperties.PVP_FRONTIER_REGION;

            // Dynamic spawn: when the caller supplied an override anchor (a
            // point on a ring around a real player, picked by
            // TryPickDynamicSpawnPosition), use it directly with a tiny
            // jitter so consecutive spawns aren't stacked on the same tile.
            // Falls back to the static realm anchor when no override was
            // provided — keeps the original behaviour intact for any caller
            // that doesn't opt in.
            if (overrideAnchor != null)
            {
                g.VirtualPosition = new Point3D(overrideAnchor.X + Util.Random(-150, 150),
                                                overrideAnchor.Y + Util.Random(-150, 150),
                                                overrideAnchor.Z);
            }
            else
            {
                g.VirtualPosition = new Point3D(cfg.SpawnAnchor.X + Util.Random(-250, 250),
                                                cfg.SpawnAnchor.Y + Util.Random(-250, 250),
                                                cfg.SpawnAnchor.Z);
            }

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
            _hydrationCursor = 0;
            State = eFrontierState.Disbanded;
            return n;
        }

        /// <summary>
        /// Materialises every LogicalMember into a real MimicNPC at the current
        /// VirtualPosition and binds them into a DAoC group. Idempotent: returns
        /// immediately if already hydrated.
        /// </summary>
        // Max MimicNPC constructed per Hydrate() call. Set to a full 8-man
        // so the user-spec "le groupe pop d'un coup, complet et totalement
        // pret a se battre" actually happens — staggering meant the player
        // engaged half a group while the other half popped in mid-fight.
        // The global MAX_HYDRATIONS_PER_TICK on the manager (16) still
        // bounds total CPU per maintenance tick across all groups.
        private const int HYDRATE_BATCH_PER_TICK = 8;

        // Cursor into Roster for staggered hydration. Advances on every
        // attempt (success OR skip) so a roster class that fails to build
        // never wedges the cursor and re-tries forever.
        private int _hydrationCursor;

        public void Hydrate()
        {
            if (IsHydrated || Roster.Count == 0) return;

            int attemptsThisCall = 0;
            while (_hydrationCursor < Roster.Count && attemptsThisCall < HYDRATE_BATCH_PER_TICK)
            {
                // Global gate: stop the moment the shared per-tick budget is
                // spent, even if this group's own batch isn't full. The group
                // stays !IsHydrated and resumes from _hydrationCursor on a
                // later tick. This is the cap that prevents N groups × 4 bots
                // from freezing the GameLoop on a single maintenance pass.
                if (!PvPFrontierManager.TryConsumeHydrationBudget())
                    break;

                LogicalMember lm = Roster[_hydrationCursor];
                _hydrationCursor++;
                attemptsThisCall++;

                Point3D pos = new(VirtualPosition.X + Util.Random(-200, 200),
                                  VirtualPosition.Y + Util.Random(-200, 200),
                                  VirtualPosition.Z);

                // Per-member try/catch: a single roster class that fails to
                // construct (MimicNPC throws on bad EligibleRaces / missing
                // spec / missing combat profile) must NOT abort the whole
                // hydration.
                MimicNPC m;
                try
                {
                    m = MimicManager.GetMimic(lm.MimicClass, lm.Level, lm.Name, lm.Gender);
                }
                catch (Exception ex)
                {
                    log.Error($"PvPFrontier Hydrate: failed to build {lm.MimicClass} — skipping.", ex);
                    continue;
                }

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

                // Frontier "ready for fight" outfit. The default Mimic
                // constructor leaves the bot at base RR for its level and
                // un-buffed — a real player joining NF would already have
                // a realm rank from playing AND group buffs from their
                // healer. Match that here so frontier mimics aren't free
                // food on spawn:
                //   * Randomise RealmLevel inside [20, 110] (RR3L0..RR12L0).
                //   * Pre-apply level-50 buff stat bonuses so the bot opens
                //     combat at the same effective stats as a buffed player.
                //   * Top up Health / Mana / Endurance once the bonuses are
                //     applied (Max* changes with the buff stat bonus).
                ApplyFrontierRealmRank(m);
                ApplyFrontierPreBuffs(m);
                ApplyFrontierGearUpgrade(m);
                m.Health = m.MaxHealth;
                m.Mana = m.MaxMana;
                m.Endurance = m.MaxEndurance;

                Members.Add(m);
            }

            // Roster not fully processed yet — finish on the next Tick.
            // The group stays !IsHydrated so Tick() keeps calling Hydrate().
            if (_hydrationCursor < Roster.Count)
                return;

            if (Members.Count == 0) return;

            DolGroup = new Group(Members[0]);
            GroupMgr.AddGroup(DolGroup);
            DolGroup.AddMember(Members[0]);
            for (int i = 1; i < Members.Count; i++)
                DolGroup.AddMember(Members[i]);

            PvPGroupComposer.AutoAssignPvPRoles(Members);
            IsHydrated = true;
            _hydratedSinceMs = GameLoop.GameLoopTime;
            _dehydrateAfterMs = 0;
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
            // Reset the staggered-hydration cursor and the hydrated-since
            // stamp so the NEXT Hydrate() starts a clean batch from roster
            // index 0 and the MIN_HYDRATED_LIFETIME floor is measured from
            // the new materialisation, not the stale previous one.
            _hydrationCursor = 0;
            _hydratedSinceMs = 0;
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
                    // Settle for this tick: the disband check and the combat
                    // state machine below would otherwise run against bots
                    // that were materialised microseconds ago. Resume next
                    // tick once they're fully in the world.
                    return;
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

                // Minimum-lifetime floor: a group that hydrated less than
                // MIN_HYDRATED_LIFETIME_MS ago is NOT allowed to dehydrate,
                // even if it patrolled out of player range. This is the core
                // anti-flicker guard — without it a patrolling group hydrate/
                // dehydrate/re-hydrate loops and the bots visibly reset.
                bool tooYoungToDehydrate = now - _hydratedSinceMs < MIN_HYDRATED_LIFETIME_MS;

                if (farFromAll && !tooYoungToDehydrate)
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
        /// True if any non-GM human player is within `range` of the group's
        /// active position (leader while hydrated, VirtualPosition while
        /// dormant) in our Region. Reads from the per-tick player snapshot
        /// populated by PvPFrontierManager.RefreshPlayerSnapshots — the
        /// previous per-call iteration over ClientService blew up the tick
        /// budget on a populated server (O(groups × players)).
        /// </summary>
        private bool IsPlayerWithin(int range)
        {
            PvPFrontierManager.PlayerSnapshot snap = PvPFrontierManager.GetPlayerSnapshot(Region);
            if (snap == null || snap.Sampled.Count == 0)
                return false;

            // While the group is hydrated, its members can patrol far from
            // the original VirtualPosition. Sample the real leader's
            // position when we have one so the dehydrate gate tracks the
            // bots, not the stale spawn anchor.
            int refX = VirtualPosition.X;
            int refY = VirtualPosition.Y;
            if (IsHydrated)
            {
                GameLiving leader = FirstAliveMember();
                if (leader != null)
                {
                    refX = leader.X;
                    refY = leader.Y;
                }
            }

            long rangeSq = (long)range * range;
            List<PvPFrontierManager.PlayerSampledPos> list = snap.Sampled;
            for (int i = 0; i < list.Count; i++)
            {
                long dx = list[i].X - refX;
                long dy = list[i].Y - refY;
                if (dx * dx + dy * dy <= rangeSq)
                    return true;
            }
            return false;
        }

        private void TickPatrol()
        {
            GameLiving leader = FirstAliveMember();
            if (leader == null) return;

            // If we are near our waypoint AND it's a keep, engage the doors/guards.
            TryAttackKeepObjectsNearWaypoint();

            // Reached current waypoint? Pick the next one as soon as the
            // leader arrives — no more "wait for stragglers" gate. The
            // 75% cohesion gate stalled the entire group whenever a slow
            // caster fell behind, producing visibly immobile waypoints
            // for 5-10+ seconds at a time. The follower brains run their
            // own FOLLOW_THE_LEADER state and a stuck-detection nudge
            // (see below) catches anyone genuinely lost — the leader no
            // longer waits.
            if (leader.GetDistance(_currentWaypoint) < WAYPOINT_REACHED_RANGE)
            {
                PickNextWaypoint();
                OrderGroupToWaypoint();
            }

            // Stuck detection: if the leader hasn't meaningfully moved in
            // STUCK_GRACE_MS while supposedly walking to a waypoint, force
            // a fresh pick. Path failures (broken navmesh, line of fire
            // through a wall) otherwise leave the group standing on the
            // exact same tile for the whole patrol cycle. Cheap O(1)
            // position delta check — no path probing.
            CheckPatrolStuck(leader);

            // Periodically scan for enemy realm groups within detection range.
            long now = GameLoop.GameLoopTime;

            // Honour the post-fight cooldown: the group keeps patrolling but
            // doesn't seek a new engagement during the rest window.
            if (now < _postFightUntilMs)
                return;

            if (now >= _nextScanMs)
            {
                // Scan cadence halved (was 3-5 s, now 1-2 s) so groups
                // commit to a real player or enemy group within one tick
                // of detection instead of finishing their current waypoint
                // first. Combined with the 1 s group tick, max latency
                // from "player enters detection range" → "group engages"
                // is now ~2 s instead of ~5-8 s.
                _nextScanMs = now + 1000 + Util.Random(0, 1000);

                // Scan for the closest enemy player FIRST. Frontier mimic
                // groups exist to challenge real players — if one is in
                // detection range, divert the patrol toward them regardless
                // of whether an enemy mimic group is also nearby. Without
                // this, a lone player could walk past patrols at 3000u and
                // never be engaged because the scan only saw the other
                // mimic groups across the map.
                GameLiving enemyPlayer = ScanForEnemyPlayer(leader);
                if (enemyPlayer != null)
                {
                    OrderGroupToEngagePlayer(enemyPlayer);
                    State = eFrontierState.Engaging;
                    return;
                }

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

        /// <summary>
        /// Scans nearby for an enemy-realm GamePlayer within DETECTION_RANGE
        /// of the group's leader. Filters out admins (PrivLevel > 1) and
        /// stealthed targets so a GM patrolling NF doesn't drag every group
        /// into combat, and a stealther stays stealthed. Returns the closest
        /// match or null. Used by TickPatrol to drive group-level player
        /// engagement (the proactive "aggressif a distance" behaviour
        /// requested by the spec).
        /// </summary>
        private GameLiving ScanForEnemyPlayer(GameLiving myLeader)
        {
            GamePlayer best = null;
            int bestDistSq = int.MaxValue;
            ushort detect = (ushort)DETECTION_RANGE;

            foreach (GamePlayer p in myLeader.GetPlayersInRadius(detect))
            {
                if (p == null || !p.IsAlive) continue;
                if (p.Realm == Config.Realm || p.Realm == eRealm.None) continue;
                if (p.IsStealthed) continue;
                if (p.Client?.Account != null && p.Client.Account.PrivLevel > 1) continue;
                if (p.CurrentRegionID != myLeader.CurrentRegionID) continue;

                int dx = p.X - myLeader.X;
                int dy = p.Y - myLeader.Y;
                int distSq = dx * dx + dy * dy;
                if (distSq < bestDistSq)
                {
                    bestDistSq = distSq;
                    best = p;
                }
            }

            return best;
        }

        /// <summary>
        /// Issues an engagement order against a real player target. Sets the
        /// player as the priority target for every alive member's brain, then
        /// orders the group to converge on them — same shape as
        /// <see cref="OrderGroupToEngage"/> but with a player anchor instead
        /// of an enemy mimic group leader.
        /// </summary>
        private void OrderGroupToEngagePlayer(GameLiving target)
        {
            if (target == null) return;

            // Tactical focus-fire: if the spotted player is in a group with a
            // healer / caster, swap to the squishiest visible target. A real
            // RvR group does this instinctively — pick the back line off
            // before grinding through the tank. Falls back to the original
            // target if no better squishy is in detection range.
            GameLiving focus = PickBestFocusTarget(target);
            GameLiving primary = focus ?? target;

            // Per-role aggro assignment: melee + tank classes go straight on
            // the primary target; healers / supports / stealthers stay on
            // the primary as well but with a smaller aggro bump (so they
            // don't out-threat the tank's peel). This lets the brain's own
            // CC / heal cycles continue to make per-tick decisions while
            // still committing to combat.
            foreach (var m in Members)
            {
                if (m == null || !m.IsAlive) continue;
                if (m.MimicBrain == null) continue;

                m.TargetObject = primary;
                int aggroAmount = (m.MimicBrain.IsHealer || m.MimicBrain.IsMainCC) ? 1 : 5;
                m.MimicBrain.AddToAggroList(primary, aggroAmount);
            }

            // Movement: bias the convergence point so HEALERS end up at the
            // back of the formation. Without this, the whole group piles
            // onto the player's tile and the back-line healers get focus-
            // fired through the tank. We aim the convergence about ~600u
            // SHORT of the target so the tank engages while healers stay
            // safely back. The brain's per-bot aggro range (3000u) still
            // closes the final gap when needed.
            double dx = target.X - VirtualPosition.X;
            double dy = target.Y - VirtualPosition.Y;
            double len = Math.Sqrt(dx * dx + dy * dy);
            if (len > 100)
            {
                int backoff = 600;
                int approachX = target.X - (int)Math.Round(dx / len * backoff);
                int approachY = target.Y - (int)Math.Round(dy / len * backoff);
                _currentWaypoint = new Point3D(approachX, approachY, target.Z);
            }
            else
            {
                _currentWaypoint = new Point3D(target.X, target.Y, target.Z);
            }
            OrderGroupToWaypoint();
        }

        /// <summary>
        /// Inspects the target's group (if any) for a more valuable focus
        /// target — a healer, caster, or unbuffed light-armour class is a
        /// far better burst target than the front-line tank. Mimics already
        /// do this implicitly via aggro-list threat math, but locking the
        /// initial engage on a healer makes the first 5 seconds of combat
        /// feel like a real coordinated group push instead of a flat zerg
        /// on the tank.
        /// </summary>
        private GameLiving PickBestFocusTarget(GameLiving primaryTarget)
        {
            if (primaryTarget is not GamePlayer gp || gp.Group == null)
                return null;

            GameLiving best = null;
            int bestScore = int.MinValue;
            foreach (GameLiving member in gp.Group.GetMembersInTheGroup())
            {
                if (member is not GamePlayer mp || !mp.IsAlive) continue;
                if (mp.IsStealthed) continue;
                if (mp.Client?.Account?.PrivLevel > 1) continue;
                if (mp.Realm == Config.Realm) continue;

                // Score: healer / caster / light armour > tank. Bigger score
                // means juicier target. Distance penalty so we don't chase
                // someone behind the player.
                int score = 0;
                eCharacterClass cls = (eCharacterClass)mp.CharacterClass.ID;
                if (IsHealerPlayerClass(cls)) score += 100;
                else if (IsCasterPlayerClass(cls)) score += 60;
                else if (IsLightArmourClass(cls)) score += 25;
                else continue; // tank / heavy melee — don't divert

                if (mp == primaryTarget) score += 5; // small tiebreak to keep original
                MimicNPC leader = FirstAliveMember();
                if (leader != null)
                {
                    int dist = leader.GetDistanceTo(mp);
                    score -= dist / 200; // -1 per 200u of extra distance
                }

                if (score > bestScore)
                {
                    bestScore = score;
                    best = mp;
                }
            }
            return best;
        }

        private static bool IsHealerPlayerClass(eCharacterClass c)
        {
            return c == eCharacterClass.Cleric || c == eCharacterClass.Friar
                || c == eCharacterClass.Druid || c == eCharacterClass.Warden || c == eCharacterClass.Bard
                || c == eCharacterClass.Healer || c == eCharacterClass.Shaman;
        }

        private static bool IsCasterPlayerClass(eCharacterClass c)
        {
            return c == eCharacterClass.Wizard || c == eCharacterClass.Sorcerer || c == eCharacterClass.Theurgist
                || c == eCharacterClass.Cabalist || c == eCharacterClass.Necromancer
                || c == eCharacterClass.Eldritch || c == eCharacterClass.Enchanter || c == eCharacterClass.Mentalist || c == eCharacterClass.Animist
                || c == eCharacterClass.Runemaster || c == eCharacterClass.Spiritmaster || c == eCharacterClass.Bonedancer;
        }

        private static bool IsLightArmourClass(eCharacterClass c)
        {
            return c == eCharacterClass.Minstrel || c == eCharacterClass.Scout || c == eCharacterClass.Infiltrator
                || c == eCharacterClass.Ranger || c == eCharacterClass.Nightshade
                || c == eCharacterClass.Hunter || c == eCharacterClass.Shadowblade;
        }

        private void TickEngaging()
        {
            // Stay engaging while at least one bot is in combat. When everyone
            // is clear of combat (enemy wiped or escaped), arm the post-fight
            // cooldown and resume patrol — gives lone humans a chance to
            // disengage instead of being chain-mobbed by every nearby group.
            int aliveNow = 0;
            bool anyoneInCombat = false;
            foreach (var m in Members)
            {
                if (m != null && m.IsAlive && m.ObjectState == GameObject.eObjectState.Active)
                {
                    aliveNow++;
                    if (m.InCombat) anyoneInCombat = true;
                }
            }

            if (!anyoneInCombat)
            {
                // Detect a near-wipe: if we lost half or more of our roster
                // during the engagement, head to safety instead of right
                // back into the same hotspot. Player groups don't patrol on
                // a fresh 4/8 — they recover first.
                int rosterSize = Roster.Count;
                bool tookHeavyLosses = rosterSize > 0 && aliveNow * 2 <= rosterSize;

                long now = GameLoop.GameLoopTime;
                _postFightUntilMs = now + POST_FIGHT_COOLDOWN_MS;

                if (tookHeavyLosses)
                {
                    // Double-length retreat to a friendly safe point and an
                    // extended post-fight cooldown so we don't body-block
                    // chasers.
                    _retreatUntilMs = now + 45_000;
                    _postFightUntilMs = now + POST_FIGHT_COOLDOWN_MS * 2;
                    State = eFrontierState.Retreating;
                    OrderGroupToRetreat();
                    return;
                }

                State = eFrontierState.Patrolling;
                // Post-combat regroup: rally at the leader's CURRENT position
                // before picking the next patrol waypoint. Without this, the
                // group splinters after a fight — followers spread out chasing
                // their last aggro target, the leader picks a fresh waypoint
                // and sprints off, and stragglers either fall behind or get
                // picked off solo by chasers. A single intermediate waypoint
                // at the leader's location forces the FOLLOW_THE_LEADER state
                // to converge for ~5-10 s before the next patrol leg starts.
                MimicNPC postFightLeader = FirstAliveMember();
                if (postFightLeader != null)
                {
                    _currentWaypoint = new Point3D(postFightLeader.X, postFightLeader.Y, postFightLeader.Z);
                    OrderGroupToWaypoint();
                }
                else
                {
                    PickNextWaypoint();
                    OrderGroupToWaypoint();
                }
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

        /// <summary>
        /// Fraction of the alive roster currently within `range` of the given
        /// anchor (typically the leader). Used to gate "we've arrived" so the
        /// leader doesn't immediately leave on the next waypoint while the
        /// rest of the group is still strung out behind.
        /// </summary>
        private double GroupCohesionRatio(GameLiving anchor, int range)
        {
            if (anchor == null) return 1.0;
            int alive = 0;
            int near = 0;
            long rangeSq = (long)range * range;
            foreach (var m in Members)
            {
                if (m == null || !m.IsAlive || m.ObjectState != GameObject.eObjectState.Active)
                    continue;
                alive++;
                long dx = m.X - anchor.X;
                long dy = m.Y - anchor.Y;
                if (dx * dx + dy * dy <= rangeSq)
                    near++;
            }
            if (alive == 0) return 1.0;
            return (double)near / alive;
        }

        /// <summary>
        /// Tracks leader displacement between patrol ticks. If the leader
        /// hasn't moved meaningfully (≥200u total) within STUCK_GRACE_MS,
        /// force a fresh waypoint pick and re-issue the move order. Path
        /// failures (LoS blocked by a wall, navmesh hole, mob body block)
        /// otherwise wedge the entire group on one tile indefinitely —
        /// visible as "the patrol stopped and never moved again".
        /// Also teleport-snaps any straggler more than 3000u behind the
        /// leader to the leader's position so a slow caster doesn't fall
        /// off the world map after a hot pursuit / retreat / engagement.
        /// </summary>
        private void CheckPatrolStuck(GameLiving leader)
        {
            if (leader == null) return;

            long now = GameLoop.GameLoopTime;
            if (_lastStuckSampleMs == 0)
            {
                _lastStuckX = leader.X;
                _lastStuckY = leader.Y;
                _lastStuckSampleMs = now;
            }

            int dx = leader.X - _lastStuckX;
            int dy = leader.Y - _lastStuckY;
            int moved = dx * dx + dy * dy;

            if (moved >= STUCK_MIN_DELTA_SQ)
            {
                _lastStuckX = leader.X;
                _lastStuckY = leader.Y;
                _lastStuckSampleMs = now;
            }
            else if (now - _lastStuckSampleMs > STUCK_GRACE_MS)
            {
                // Leader didn't move enough — fresh waypoint to escape.
                _lastStuckSampleMs = now;
                _lastStuckX = leader.X;
                _lastStuckY = leader.Y;
                PickNextWaypoint();
                OrderGroupToWaypoint();
            }

            // Pull stragglers back. Anyone more than 3000 u from the leader
            // gets a path order; if they're absurdly far (>6000u) we just
            // teleport-snap them to the leader so the group reconstitutes
            // instead of dribbling members across half a zone.
            const int STRAGGLER_PATH_RANGE = 3000;
            const int STRAGGLER_TELEPORT_RANGE = 6000;
            foreach (var m in Members)
            {
                if (m == null || !m.IsAlive || m == leader) continue;
                if (m.ObjectState != GameObject.eObjectState.Active) continue;
                if (m.InCombat) continue; // never yank a bot out of an active fight

                int ddx = m.X - leader.X;
                int ddy = m.Y - leader.Y;
                long sq = (long)ddx * ddx + (long)ddy * ddy;
                if (sq < (long)STRAGGLER_PATH_RANGE * STRAGGLER_PATH_RANGE) continue;

                if (sq > (long)STRAGGLER_TELEPORT_RANGE * STRAGGLER_TELEPORT_RANGE)
                {
                    m.MoveTo(leader.CurrentRegionID,
                        leader.X + Util.Random(-100, 100),
                        leader.Y + Util.Random(-100, 100),
                        leader.Z,
                        leader.Heading);
                }
                else
                {
                    m.PathTo(new Point3D(leader.X, leader.Y, leader.Z), m.MaxSpeed);
                }
            }
        }

        private void PickNextWaypoint()
        {
            // Priority order (mirrors how a real RvR group picks its next
            // destination):
            //   1. A friendly keep currently under attack — defend it.
            //   2. NEW — bias toward the closest enemy-realm player in
            //      the region. Lets groups follow players across NF as
            //      they roam, instead of patrolling the same fixed loop
            //      while the player moves elsewhere.
            //   3. A recently active enemy hotspot — chase the fight.
            //   4. Roll for "attack an enemy keep" intent.
            //   5. Random patrol waypoint.
            //   6. Fall back to spawn anchor.

            // (1) Defend a friendly keep under siege.
            Point3D defendTarget = PickFriendlyKeepUnderAttack();
            if (defendTarget != null)
            {
                _currentWaypoint = defendTarget;
                return;
            }

            // (2) Player tracking. PvP frontier exists for players, so when
            // one is in the region, follow them. Range is wide (default
            // 12000u) and the bias is partial (default 50% chance) so the
            // entire frontier doesn't dogpile a single roamer — half the
            // groups still patrol their normal loop and meet the player at
            // the contested ground organically.
            if (Util.Chance(PvPFrontierProperties.PVP_FRONTIER_PLAYER_TRACK_CHANCE))
            {
                Point3D playerTarget = PickClosestEnemyPlayerWaypoint();
                if (playerTarget != null)
                {
                    _currentWaypoint = playerTarget;
                    return;
                }
            }

            // (3) Recent enemy activity bias: 30% chance to head to the last
            // place an enemy was spotted (if any). Keeps groups converging
            // on contested ground instead of patrolling deserted waypoints.
            if (Util.Chance(30))
            {
                Point3D hotspot = PvPFrontierManager.PickRecentEnemyHotspot(Region, Config.Realm);
                if (hotspot != null)
                {
                    _currentWaypoint = hotspot;
                    return;
                }
            }

            // (4) Offensive keep intent.
            if (Util.Chance(PvPFrontierProperties.PVP_FRONTIER_KEEP_ATTACK_CHANCE))
            {
                Point3D keepTarget = PickClosestEnemyKeep();
                if (keepTarget != null)
                {
                    _currentWaypoint = keepTarget;
                    return;
                }
            }

            // (5) Random patrol waypoint.
            if (Config.PatrolWaypoints.Count == 0)
            {
                // (6) No waypoints configured: hold position at spawn.
                _currentWaypoint = Config.SpawnAnchor;
                return;
            }
            _currentWaypoint = Config.PatrolWaypoints[Util.Random(Config.PatrolWaypoints.Count - 1)];
        }

        /// <summary>
        /// Picks a patrol target near the closest enemy-realm player within
        /// PVP_FRONTIER_PLAYER_TRACK_RADIUS of any group member. We offset
        /// the target by a small jitter so multiple groups tracking the same
        /// player don't all converge on the exact same tile — the player
        /// gets pressure from several angles instead of one tightly packed
        /// blob. Returns null when nobody is in range, no leader is alive,
        /// or the only candidates are filtered out (admins, stealthers,
        /// same-realm).
        /// </summary>
        private Point3D PickClosestEnemyPlayerWaypoint()
        {
            MimicNPC leader = FirstAliveMember();
            if (leader == null) return null;

            PvPFrontierManager.PlayerSnapshot snap = PvPFrontierManager.GetPlayerSnapshot(Region);
            if (snap == null || snap.Sampled.Count == 0) return null;

            int trackRadius = Math.Max(1500, PvPFrontierProperties.PVP_FRONTIER_PLAYER_TRACK_RADIUS);
            long trackRadiusSq = (long)trackRadius * trackRadius;

            PvPFrontierManager.PlayerSampledPos? bestPos = null;
            long bestSq = long.MaxValue;

            foreach (PvPFrontierManager.PlayerSampledPos p in snap.Sampled)
            {
                if (p.Realm == Config.Realm || p.Realm == eRealm.None)
                    continue;

                long dx = p.X - leader.X;
                long dy = p.Y - leader.Y;
                long sq = dx * dx + dy * dy;
                if (sq > trackRadiusSq) continue;
                if (sq < bestSq)
                {
                    bestSq = sq;
                    bestPos = p;
                }
            }

            if (bestPos == null) return null;

            // Angle-based spread instead of random ±400u jitter. Multiple
            // groups tracking the same player all rolled tiles within a
            // ~565u radius (sqrt(400²+400²)) — a tight pile that gave the
            // player a single rally point to AoE. Now each group picks an
            // angle derived from its own (deterministic) hash and stages
            // 1500-2500u out on a ring around the target, so 5 groups
            // attacking the same player approach from 5 different bearings.
            // The group still RUNS toward the player (waypoint pulls them
            // in via OrderGroupToWaypoint + per-mimic AggroRange); this
            // staging point just determines THE ANGLE of approach.
            int hash = (Roster.Count * 7919) ^ Region ^ (int)Config.Realm;
            double angle = (hash & 0xFFFF) / 65536.0 * Math.PI * 2;
            int offset = 1500 + Util.Random(0, 1000);
            int ox = (int)Math.Round(Math.Cos(angle) * offset);
            int oy = (int)Math.Round(Math.Sin(angle) * offset);
            return new Point3D(bestPos.Value.X + ox, bestPos.Value.Y + oy, bestPos.Value.Z);
        }

        /// <summary>
        /// Returns the position of a friendly keep / tower currently in combat
        /// (under attack by enemies). Closest match if several are under siege.
        /// </summary>
        private Point3D PickFriendlyKeepUnderAttack()
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
                if (k.Realm != Config.Realm) continue;     // friendly only
                if (!k.InCombat) continue;                 // under attack only

                long dx = k.X - leader.X;
                long dy = k.Y - leader.Y;
                long sq = dx * dx + dy * dy;

                if (sq < bestSq)
                {
                    bestSq = sq;
                    best = k;
                }
            }

            return best != null ? new Point3D(best.X, best.Y, best.Z) : null;
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

            // Record the engagement so other friendly groups know where the
            // fighting is happening and can rally instead of patrolling empty
            // waypoints. We record OUR realm as the observer so the same
            // realm's other groups don't all chase each other's engagements.
            PvPFrontierManager.RegisterEnemyHotspot(Region, Config.Realm,
                enemyLeader.X, enemyLeader.Y, enemyLeader.Z);

            foreach (var m in Members)
            {
                if (m == null || !m.IsAlive || m.MimicBrain == null) continue;
                m.MimicBrain.AddToAggroList(enemyLeader, 100);
                m.MimicBrain.FSM.SetCurrentState(eFSMStateType.AGGRO);
            }
        }

        private void OrderGroupToRetreat()
        {
            // Pick the closest "safe point": either a friendly keep we still
            // hold, or the realm's spawn anchor. Real RvR groups duck into
            // the nearest friendly perimeter, they don't always trek all the
            // way back to spawn. Falls back to the spawn anchor when no
            // friendly keep is in our region.
            Point3D safePoint = PickClosestSafePoint() ?? Config.SpawnAnchor;
            _retreatDestination = safePoint;

            MimicNPC leader = FirstAliveMember();
            if (leader == null)
                return;

            // Move the leader explicitly; the rest follow in formation. The
            // previous code sent every member to the same point with no
            // cohesion, so healers and casters got isolated and picked off
            // by chasers — same fix pattern as OrderGroupToWaypoint.
            leader.WalkTo(safePoint, leader.MaxSpeed);

            foreach (var m in Members)
            {
                if (m == null || !m.IsAlive || m == leader) continue;
                if (m.MimicBrain == null) continue;
                m.MimicBrain.FSM.SetCurrentState(eFSMStateType.FOLLOW_THE_LEADER);
            }
        }

        private Point3D _retreatDestination;

        /// <summary>
        /// Closest friendly keep / tower we still own (within the group's
        /// region). Returns null when no friendly keep exists in the region.
        /// </summary>
        private Point3D PickClosestSafePoint()
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
                if (k.Realm != Config.Realm) continue;     // only our own keeps
                if (k.InCombat) continue;                  // under attack — not safe

                long dx = k.X - leader.X;
                long dy = k.Y - leader.Y;
                long sq = dx * dx + dy * dy;

                if (sq < bestSq)
                {
                    bestSq = sq;
                    best = k;
                }
            }

            return best != null ? new Point3D(best.X, best.Y, best.Z) : null;
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
                foreach (var byRealm in PvPFrontierManager._configs.Values)
                foreach (var cfg in byRealm.Values)
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

        /// <summary>
        /// Randomises a frontier mimic's RealmRank inside [RR3L0, RR12L0]
        /// = RealmLevel 20..110. Sets RealmPoints to match — without that,
        /// the displayed RR title and the RP bar are out of sync and
        /// /who shows a level-50 1L0. Source of REALMPOINTS_FOR_LEVEL is
        /// MimicNPC.REALMPOINTS_FOR_LEVEL (same table as players).
        /// </summary>
        private static void ApplyFrontierRealmRank(MimicNPC m)
        {
            if (m == null) return;
            int maxIdx = MimicNPC.REALMPOINTS_FOR_LEVEL.Length - 1;
            int realmLevel = Util.Random(20, Math.Min(110, maxIdx));
            m.RealmLevel = realmLevel;
            m.RealmPoints = MimicNPC.REALMPOINTS_FOR_LEVEL[realmLevel];
        }

        /// <summary>
        /// Pre-applies "fully-buffed level-50 character" stat bonuses on
        /// a frontier mimic at hydration time, so the bot opens combat at
        /// the same effective stats a real player would have AFTER spending
        /// 30 s receiving buffs from their group. Skips the buff cast cycle
        /// entirely — the bot is just configured to read as if it had been
        /// buffed already.
        ///
        /// Values match typical lvl 50 buff caps (base + spec stat ~75,
        /// AF ~175 + 75 spec, +18 resists across all damage types, plus
        /// a damage add, regen and quickness bump). Real player groups
        /// average around these numbers; matching them here avoids the
        /// "fresh bot eaten by a buffed player" mismatch on engagement.
        /// </summary>
        private static void ApplyFrontierPreBuffs(MimicNPC m)
        {
            if (m == null) return;

            // Base buffs (Cleric / Druid / Shaman / Bard / Paladin level 50)
            const int BASE_STAT = 47;
            const int BASE_AF = 175;

            // Spec buffs (Spec line, capped lvl 50)
            const int SPEC_STAT = 25;
            const int SPEC_AF = 75;
            const int SPEC_RESIST = 18;
            const int SPEC_DAMAGE_ADD = 6;

            // Main stats — apply universally; a Wizard mimic also gets the
            // Strength buff (cheap, harmless) which keeps the helper simple
            // and avoids per-class stat-target branching.
            void BumpBase(eProperty p, int v) => m.BaseBuffBonusCategory[p] = v;
            void BumpSpec(eProperty p, int v) => m.SpecBuffBonusCategory[p] = v;

            BumpBase(eProperty.Strength, BASE_STAT);
            BumpBase(eProperty.Constitution, BASE_STAT);
            BumpBase(eProperty.Dexterity, BASE_STAT);
            BumpBase(eProperty.Quickness, BASE_STAT);
            BumpBase(eProperty.Acuity, BASE_STAT);
            BumpBase(eProperty.Intelligence, BASE_STAT);
            BumpBase(eProperty.Piety, BASE_STAT);
            BumpBase(eProperty.Empathy, BASE_STAT);
            BumpBase(eProperty.Charisma, BASE_STAT);

            BumpSpec(eProperty.Strength, SPEC_STAT);
            BumpSpec(eProperty.Constitution, SPEC_STAT);
            BumpSpec(eProperty.Dexterity, SPEC_STAT);
            BumpSpec(eProperty.Quickness, SPEC_STAT);
            BumpSpec(eProperty.Acuity, SPEC_STAT);

            // AF — Paladin chant + Cleric/Druid spec AF
            BumpBase(eProperty.ArmorFactor, BASE_AF);
            BumpSpec(eProperty.ArmorFactor, SPEC_AF);

            // Resist chants (Paladin) — flat across all damage types
            BumpSpec(eProperty.Resist_Body, SPEC_RESIST);
            BumpSpec(eProperty.Resist_Cold, SPEC_RESIST);
            BumpSpec(eProperty.Resist_Heat, SPEC_RESIST);
            BumpSpec(eProperty.Resist_Energy, SPEC_RESIST);
            BumpSpec(eProperty.Resist_Matter, SPEC_RESIST);
            BumpSpec(eProperty.Resist_Spirit, SPEC_RESIST);
            BumpSpec(eProperty.Resist_Crush, SPEC_RESIST);
            BumpSpec(eProperty.Resist_Slash, SPEC_RESIST);
            BumpSpec(eProperty.Resist_Thrust, SPEC_RESIST);

            // Damage add (Skald / Friar / Shaman) — flat damage on every swing
            m.AbilityBonus[eProperty.DPS] = SPEC_DAMAGE_ADD;
        }

        /// <summary>
        /// Bumps every equipped inventory item to 99 % quality and 100 %
        /// condition. ROG generation already rolls items in the 91-99 range
        /// for level 50 (see <c>GeneratedUniqueItem</c>), but we want every
        /// frontier mimic at the ceiling so combat outcomes don't randomly
        /// flap based on item rolls — and so the bot reads as "fully stuffé"
        /// in the inventory inspector. Items missing or unmodifiable are
        /// silently skipped.
        /// </summary>
        private static void ApplyFrontierGearUpgrade(MimicNPC m)
        {
            if (m?.Inventory == null) return;

            foreach (DbInventoryItem item in m.Inventory.AllItems)
            {
                if (item == null) continue;
                try
                {
                    if (item.Quality < 99) item.Quality = 99;
                    if (item.MaxCondition < 50000) item.MaxCondition = 50000;
                    item.Condition = item.MaxCondition;
                }
                catch
                {
                    // Some templates lock fields. Best-effort upgrade only.
                }
            }
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
                // Valewalker is a 2H Scythe melee hybrid (TwoHanded SpecType),
                // NOT a back-line caster — rolling it in the Caster slot put
                // it on the front line with no Light/Mana spec to back up
                // the comp. Animist (turret PetCaster) fits the Caster slot
                // better. Keep Valewalker on the MeleeDPS list only.
                [eFrontierRole.Caster]    = new[] { eMimicClass.Eldritch, eMimicClass.Enchanter, eMimicClass.Mentalist, eMimicClass.Animist },
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

            // Dedup: avoid stacking the same exact class on multiple role
            // slots (Sorcerer Support + Sorcerer Caster, Mentalist twice,
            // Healer in both Healer and Support, etc.). When a role's only
            // candidates are already present, fall through to the next role
            // and let the caller end up slightly smaller — better than a
            // 2-Sorcerer 8-man with no real second caster.
            HashSet<eMimicClass> taken = new();

            for (int i = 0; i < slots; i++)
            {
                if (!rolesForRealm.TryGetValue(_template[i], out var candidates) || candidates.Length == 0)
                    continue;

                eMimicClass pick = eMimicClass.None;
                // Walk a randomised order so we don't bias the first entry.
                int start = Util.Random(candidates.Length - 1);
                for (int k = 0; k < candidates.Length; k++)
                {
                    eMimicClass candidate = candidates[(start + k) % candidates.Length];
                    if (!taken.Contains(candidate))
                    {
                        pick = candidate;
                        break;
                    }
                }

                if (pick == eMimicClass.None)
                    continue;

                taken.Add(pick);
                result.Add(pick);
            }

            return result;
        }

        public static void AutoAssignPvPRoles(List<MimicNPC> mimics)
        {
            if (mimics == null || mimics.Count == 0) return;

            MimicGroup mg = mimics[0].Group?.MimicGroup;
            if (mg == null) return;

            MimicNPC tank = mimics.FirstOrDefault(m => MimicGroupComposer.IsTankClass(m));
            MimicNPC cc = mimics.FirstOrDefault(m => MimicGroupComposer.IsCCClass(m));

            MimicNPC leader = tank ?? mimics[0];
            mg.SetLeader(leader);
            mg.SetMainAssist(tank ?? mimics[0]);

            if (tank != null) mg.SetMainTank(tank);
            if (cc != null) mg.SetMainCC(cc);

            // Flag EVERY healer in the comp as a dedicated healer, not just
            // the first one found. With a 2- or 3-healer PvP roster, leaving
            // the secondary healers in DPS mode means they fight on the
            // front line instead of triaging — the focus targets melt fast
            // in RvR and we need every healer pumping.
            foreach (MimicNPC m in mimics)
            {
                if (m?.MimicBrain != null && MimicGroupComposer.IsHealerClass(m))
                    m.MimicBrain.IsHealer = true;
            }
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
            int aliveCount = 0;
            int healerCount = 0;
            int levelSum = 0;
            int rrSum = 0;
            foreach (var m in g.Members)
            {
                if (m == null || !m.IsAlive) continue;
                aliveCount++;
                levelSum += m.Level;
                rrSum += m.RealmLevel;
                if (m.MimicBrain != null && m.MimicBrain.IsHealer)
                    healerCount++;
            }

            if (aliveCount == 0) return 0;

            // Base strength: levels + realm-rank bonus (every 10 RR = level-1
            // bonus, capping the RR contribution at sane values).
            int strength = levelSum + (rrSum / 10);

            // Headcount weight: a 5-man group is much stronger than a level-
            // matched 1-man, but the previous formula treated them as equal.
            // Multiply by sqrt(aliveCount) so 8 mimics weigh ~2.8× a solo
            // mimic instead of 8× (too cliff-edged for tactical decisions).
            strength = (int)(strength * Math.Sqrt(aliveCount));

            // Healer ratio matters more than absolute healer count: 2 healers
            // in an 8-man (25 %) is the standard tank-friendly comp; 0 is a
            // suicidal zerg; 2 in a 4-man (50 %) is over-healed. Scale the
            // contribution by ratio, not raw count.
            double healerRatio = (double)healerCount / aliveCount;
            strength = (int)(strength * (1.0 + healerRatio * 0.6));

            return strength;
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

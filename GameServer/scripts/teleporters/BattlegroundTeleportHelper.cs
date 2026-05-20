using System;
using System.Collections.Generic;
using DOL.Database;
using DOL.GS.Keeps;

namespace DOL.GS.Scripts
{
    /// <summary>
    /// Shared battleground teleport logic, used by both the dedicated
    /// <see cref="BGTeleporter"/> and the realm master teleporters
    /// (<see cref="LiveTeleporter"/> — "Master Visur" / "Stor Gothi Annark" /
    /// "Channeler Glasny").
    ///
    /// All BG knowledge is read from the `Battleground` DB table at call time,
    /// so admin tuning (level brackets, new BG regions) takes effect live
    /// without a rebuild. Keeping the logic in one place guarantees both
    /// teleporters offer an identical, consistent set of destinations.
    /// </summary>
    public static class BattlegroundTeleportHelper
    {
        // Per-realm fallback spawn anchors, used only when a BG region has no
        // portal keep registered for the player's realm. These match the
        // realm-corner layout the mimic BG spawner uses (Alb SE, Hib SW,
        // Mid NE), so the player still lands inside their realm's section.
        private static readonly Dictionary<eRealm, Point3D> _fallbackSpawns = new()
        {
            [eRealm.Albion] = new Point3D(37200, 51200, 3950),
            [eRealm.Hibernia] = new Point3D(19820, 19305, 4050),
            [eRealm.Midgard] = new Point3D(53300, 26100, 4270),
        };

        /// <summary>
        /// Every Battleground row the player is currently eligible for,
        /// sorted by MinLevel ascending. Eligibility = level inside the
        /// bracket, realm level under the cap, and the region resolvable.
        /// </summary>
        public static List<DbBattleground> GetEligibleBattlegrounds(GamePlayer player)
        {
            List<DbBattleground> result = new();
            if (player == null)
                return result;

            foreach (DbBattleground bg in GameServer.Database.SelectAllObjects<DbBattleground>())
            {
                if (!IsValidBattleground(bg))
                    continue;
                if (player.Level < bg.MinLevel || player.Level > bg.MaxLevel)
                    continue;
                if (bg.MaxRealmLevel > 0 && player.RealmLevel >= bg.MaxRealmLevel)
                    continue;

                result.Add(bg);
            }

            result.Sort((a, b) => a.MinLevel.CompareTo(b.MinLevel));
            return result;
        }

        /// <summary>
        /// True when a Battleground row is a real, usable BG: sane level
        /// bracket and a region that exists on this server's map.
        /// </summary>
        public static bool IsValidBattleground(DbBattleground bg)
        {
            if (bg == null)
                return false;
            if (bg.MinLevel == 0 || bg.MaxLevel == 0 || bg.MaxLevel < bg.MinLevel)
                return false;
            return WorldMgr.GetRegion(bg.RegionID) != null;
        }

        /// <summary>
        /// Human-readable label for a BG, taken from the region description
        /// (e.g. "Thidranki", "Cathal Valley"). Falls back to a level-bracket
        /// label if the region has no proper name. Spaces are kept — the DAoC
        /// client transmits multi-word bracketed whisper keys verbatim.
        /// </summary>
        public static string GetBattlegroundLabel(DbBattleground bg)
        {
            Region region = WorldMgr.GetRegion(bg.RegionID);
            string name = region?.Description;
            if (string.IsNullOrEmpty(name) || name.StartsWith("Region", StringComparison.OrdinalIgnoreCase))
                name = $"BG L{bg.MinLevel}-{bg.MaxLevel}";
            return name;
        }

        /// <summary>
        /// Resolves the BG whose label matches <paramref name="label"/> among
        /// the player's eligible battlegrounds. Returns null when nothing
        /// matches (player whispered something else, or is out of level).
        /// </summary>
        public static DbBattleground FindEligibleByLabel(GamePlayer player, string label)
        {
            if (string.IsNullOrWhiteSpace(label))
                return null;

            foreach (DbBattleground bg in GetEligibleBattlegrounds(player))
            {
                if (string.Equals(GetBattlegroundLabel(bg), label, StringComparison.OrdinalIgnoreCase))
                    return bg;
            }
            return null;
        }

        /// <summary>
        /// Builds an in-memory <see cref="DbTeleport"/> destination for a BG,
        /// targeting the player's realm portal keep inside the BG region.
        /// Falls back to the realm corner anchor when no portal keep exists.
        /// Returns null if the destination cannot be resolved at all.
        ///
        /// The DbTeleport is NOT persisted — it only feeds the existing
        /// teleporter destination pipeline (UniPortal cast + region check).
        /// </summary>
        public static DbTeleport BuildBattlegroundDestination(GamePlayer player, DbBattleground bg)
        {
            if (player == null || !IsValidBattleground(bg))
                return null;

            ushort regionId = bg.RegionID;
            int x, y, z, heading = 0;

            // Prefer the realm's portal keep inside the BG region.
            AbstractGameKeep portalKeep = null;
            foreach (AbstractGameKeep keep in GameServer.KeepManager.GetKeepsOfRegion(regionId))
            {
                if (keep.IsPortalKeep && keep.Realm == player.Realm)
                {
                    portalKeep = keep;
                    break;
                }
            }

            if (portalKeep != null)
            {
                x = portalKeep.X;
                y = portalKeep.Y;
                z = portalKeep.Z;
                heading = portalKeep.Heading;
            }
            else if (_fallbackSpawns.TryGetValue(player.Realm, out Point3D anchor))
            {
                x = anchor.X;
                y = anchor.Y;
                z = anchor.Z;
            }
            else
            {
                return null;
            }

            // Validate the resolved spawn before handing it to the teleport
            // pipeline. If (x, y) falls outside every zone of the BG region,
            // the client reports zoneId 65535 on its first position update and
            // the server kicks the player to char-select — which also deletes
            // all their bots via the Quit event. The hardcoded per-realm anchors
            // don't fit every BG region's zone layout, so a missing portal keep
            // can leave us pointing into the void. Refuse the destination
            // instead of teleporting the player into an unknown zone.
            Region region = WorldMgr.GetRegion(regionId);
            if (region == null || region.GetZone(x, y) == null)
                return null;

            return new DbTeleport
            {
                TeleportID = GetBattlegroundLabel(bg),
                Realm = (int) player.Realm,
                RegionID = regionId,
                X = x,
                Y = y,
                Z = z,
                Heading = heading,
            };
        }
    }
}

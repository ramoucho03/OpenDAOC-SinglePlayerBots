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
        /// Builds an in-memory <see cref="DbTeleport"/> destination for a BG.
        /// Resolution order, most authoritative first:
        ///   1. A <c>Teleport</c> table row for the BG region and the player's
        ///      realm — coordinates authored to match the real DAoC client map.
        ///   2. The realm's portal keep inside the BG region.
        /// The resolved point is then validated against the region's zones: a
        /// point outside every zone makes the client report zoneId 65535, which
        /// the server answers by kicking the player to char-select (also deleting
        /// their bots via the Quit event).
        ///
        /// No hardcoded coordinate anchors are used. A coordinate that isn't
        /// authored against the real client map cannot be trusted — a wrong
        /// guess strands the player. Returns null when no safe destination can
        /// be resolved; callers MUST treat null as "refuse the teleport".
        ///
        /// The DbTeleport is NOT persisted — it only feeds the existing
        /// teleporter destination pipeline.
        /// </summary>
        public static DbTeleport BuildBattlegroundDestination(GamePlayer player, DbBattleground bg)
        {
            if (player == null || !IsValidBattleground(bg))
                return null;

            ushort regionId = bg.RegionID;
            Region region = WorldMgr.GetRegion(regionId);
            if (region == null)
                return null;

            int x = 0, y = 0, z = 0, heading = 0;
            bool resolved = false;

            // 1. Canonical source: a Teleport table row for this BG region and
            //    the player's realm. These are authored against the client map.
            DbTeleport teleportRow = FindBattlegroundTeleportRow(regionId, (int) player.Realm);
            if (teleportRow != null)
            {
                x = teleportRow.X;
                y = teleportRow.Y;
                z = teleportRow.Z;
                heading = teleportRow.Heading;
                resolved = true;
            }

            // 2. Fallback: the realm's portal keep inside the BG region.
            if (!resolved)
            {
                foreach (AbstractGameKeep keep in GameServer.KeepManager.GetKeepsOfRegion(regionId))
                {
                    if (keep.IsPortalKeep && keep.Realm == player.Realm)
                    {
                        x = keep.X;
                        y = keep.Y;
                        z = keep.Z;
                        heading = keep.Heading;
                        resolved = true;
                        break;
                    }
                }
            }

            if (!resolved)
                return null;

            // Final guard: never hand the teleport pipeline a point that sits
            // outside every zone of the region. The client would report zoneId
            // 65535 on its first position update and the server would kick the
            // player to char-select.
            if (region.GetZone(x, y) == null)
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

        /// <summary>
        /// Finds the best <c>Teleport</c> table row for a BG region: an exact
        /// realm match wins, otherwise a realm-agnostic (Realm 0) row is used.
        /// Rows with no coordinates (sub-menu placeholders) are ignored.
        /// </summary>
        private static DbTeleport FindBattlegroundTeleportRow(ushort regionId, int realm)
        {
            DbTeleport realmAgnostic = null;

            foreach (DbTeleport t in GameServer.Database.SelectAllObjects<DbTeleport>())
            {
                if (t == null || t.RegionID != regionId)
                    continue;
                if (t.X == 0 && t.Y == 0 && t.Z == 0)
                    continue; // sub-menu placeholder, not a real destination

                if (t.Realm == realm)
                    return t;
                if (t.Realm == 0)
                    realmAgnostic = t;
            }

            return realmAgnostic;
        }
    }
}

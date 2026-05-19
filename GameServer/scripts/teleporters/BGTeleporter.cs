using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using DOL.Database;
using DOL.Events;
using DOL.GS.PacketHandler;

namespace DOL.GS.Scripts
{
    /// <summary>
    /// Battleground teleporter NPC.
    ///
    /// DB-driven rewrite of the original BGTeleporter, which used zone IDs
    /// (250-253) as if they were region IDs and offered hardcoded "Caledonia /
    /// Murdaigean / Abermenai" destinations that don't exist as separate regions
    /// in OpenDAOC. The OpenDAOC BG layout reuses regions 234-242 + 165 with the
    /// canonical level brackets stored in the `Battleground` DB table; this
    /// teleporter reads that table on each interact so any DB tuning is picked
    /// up live (no rebuild needed).
    ///
    /// Behaviour:
    ///   1. Read every Battleground row from the DB.
    ///   2. Filter to the rows the player's Level and RealmLevel are eligible for.
    ///   3. Group by region name (from Region description) for the popup list.
    ///   4. On whisper, port the player to the realm's portal keep inside the
    ///      chosen BG region — picked dynamically so map changes don't break
    ///      the teleporter.
    /// </summary>
    public class BGTeleporter : GameNPC
    {
        // Hides GameNPC.log on purpose so log entries from this teleporter
        // identify as BGTeleporter rather than the parent's category.
        private static new readonly Logging.Logger log =
            Logging.LoggerManager.Create(MethodBase.GetCurrentMethod().DeclaringType);

        public override bool AddToWorld()
        {
            Model = 2026;
            Name = "BG TELEPORTER";
            Level = 50;
            Size = 60;
            Flags |= GameNPC.eFlags.PEACE;
            return base.AddToWorld();
        }

        public override bool Interact(GamePlayer player)
        {
            if (!base.Interact(player))
                return false;

            TurnTo(player.X, player.Y);

            List<DbBattleground> eligible = GetEligibleBattlegrounds(player);

            if (eligible.Count == 0)
            {
                player.Out.SendMessage(
                    "Hello " + player.Name + "! There are no battlegrounds available for your level.",
                    eChatType.CT_Say, eChatLoc.CL_PopupWindow);
                return true;
            }

            StringBuilder sb = new();
            sb.Append("Hello ").Append(player.Name).AppendLine("! I can teleport you to the following battlegrounds (filtered to your level):");
            sb.AppendLine();

            foreach (DbBattleground bg in eligible)
            {
                string label = GetBattlegroundLabel(bg);
                sb.Append('[').Append(label).Append("]  ");
                sb.Append("level ").Append(bg.MinLevel).Append('-').Append(bg.MaxLevel);
                if (bg.MaxRealmLevel > 0)
                    sb.Append(", up to RL").Append(bg.MaxRealmLevel);
                sb.AppendLine();
            }

            player.Out.SendMessage(sb.ToString(), eChatType.CT_Say, eChatLoc.CL_PopupWindow);
            return true;
        }

        public override bool WhisperReceive(GameLiving source, string str)
        {
            if (!base.WhisperReceive(source, str))
                return false;
            if (source is not GamePlayer player)
                return false;

            TurnTo(player.X, player.Y);

            if (player.InCombat)
            {
                player.Client.Out.SendMessage(
                    "You can't port while in combat.",
                    eChatType.CT_Say, eChatLoc.CL_PopupWindow);
                return true;
            }

            // Resolve the chosen BG by label match. The popup labels are
            // generated above so this stays consistent with the interact
            // path even if region descriptions change.
            DbBattleground chosen = null;
            foreach (DbBattleground bg in GetEligibleBattlegrounds(player))
            {
                if (string.Equals(GetBattlegroundLabel(bg), str, StringComparison.OrdinalIgnoreCase))
                {
                    chosen = bg;
                    break;
                }
            }

            if (chosen == null)
            {
                // Unknown label — fall through silently to keep the NPC
                // quiet when a player whispers something irrelevant.
                return true;
            }

            if (!TryTeleportToBattleground(player, chosen))
            {
                player.Client.Out.SendMessage(
                    "Could not resolve a portal keep for that battleground.",
                    eChatType.CT_Say, eChatLoc.CL_PopupWindow);
            }
            return true;
        }

        /// <summary>
        /// Returns every Battleground row this player can enter, sorted by
        /// MinLevel ascending. We re-query the DB on every interact so live
        /// edits to the Battleground table land without a restart.
        /// </summary>
        private static List<DbBattleground> GetEligibleBattlegrounds(GamePlayer player)
        {
            List<DbBattleground> all = new();
            foreach (DbBattleground bg in GameServer.Database.SelectAllObjects<DbBattleground>())
            {
                if (bg == null)
                    continue;
                if (bg.MinLevel == 0 || bg.MaxLevel == 0 || bg.MaxLevel < bg.MinLevel)
                    continue;
                if (player.Level < bg.MinLevel || player.Level > bg.MaxLevel)
                    continue;
                if (bg.MaxRealmLevel > 0 && player.RealmLevel >= bg.MaxRealmLevel)
                    continue;
                if (WorldMgr.GetRegion(bg.RegionID) == null)
                    continue;

                all.Add(bg);
            }

            all.Sort((a, b) => a.MinLevel.CompareTo(b.MinLevel));
            return all;
        }

        /// <summary>
        /// Human-readable label for a BG. Uses the region's Description when
        /// available, falling back to "BG L{min}-{max}" so an admin-renamed
        /// region still produces a usable popup entry.
        /// </summary>
        private static string GetBattlegroundLabel(DbBattleground bg)
        {
            Region region = WorldMgr.GetRegion(bg.RegionID);
            string name = region?.Description;
            if (string.IsNullOrEmpty(name) || name.StartsWith("Region", StringComparison.OrdinalIgnoreCase))
                name = $"BG L{bg.MinLevel}-{bg.MaxLevel}";
            // Strip spaces so the whisper key matches what the player types.
            return name.Replace(" ", string.Empty);
        }

        /// <summary>
        /// Drops the player at the realm-appropriate portal keep inside the
        /// target BG region. Each BG has 3 portal keeps (one per realm); we
        /// pick the one matching player.Realm. If the portal keep can't be
        /// resolved (data missing), fall back to the BG's central spawn so
        /// the teleport still lands somewhere safe.
        /// </summary>
        private static bool TryTeleportToBattleground(GamePlayer player, DbBattleground bg)
        {
            ushort regionId = bg.RegionID;

            // Look for the player's realm portal keep in this BG region.
            foreach (Keeps.AbstractGameKeep keep in GameServer.KeepManager.GetKeepsOfRegion(regionId))
            {
                if (keep.IsPortalKeep && keep.Realm == player.Realm)
                {
                    player.MoveTo(regionId, keep.X, keep.Y, keep.Z, (ushort) keep.Heading);
                    return true;
                }
            }

            // Fallback: use the global BG spawn anchors that the mimic BG
            // spawner already relies on. Same layout convention (Alb SE,
            // Hib SW, Mid NE) so the player lands inside their realm's
            // section of the BG.
            Point3D spawn = player.Realm switch
            {
                eRealm.Albion => new Point3D(37200, 51200, 3950),
                eRealm.Hibernia => new Point3D(19820, 19305, 4050),
                eRealm.Midgard => new Point3D(53300, 26100, 4270),
                _ => null,
            };

            if (spawn == null)
                return false;

            player.MoveTo(regionId, spawn.X, spawn.Y, spawn.Z, 0);
            return true;
        }

        [ScriptLoadedEvent]
        public static void OnScriptCompiled(DOLEvent e, object sender, EventArgs args)
        {
            log.Info("BG Teleporter initialized (DB-driven, level-filtered).");
        }
    }
}

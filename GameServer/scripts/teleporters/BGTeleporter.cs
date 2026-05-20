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
    /// DB-driven: every destination comes from the `Battleground` DB table via
    /// <see cref="BattlegroundTeleportHelper"/>, filtered to the player's level
    /// and realm level. The original implementation used zone IDs (250-253) as
    /// region IDs and hardcoded "Caledonia / Murdaigean / Abermenai" — none of
    /// which are valid regions in OpenDAOC. This version shares its logic with
    /// the realm master teleporters so both stay consistent.
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

            List<DbBattleground> eligible = BattlegroundTeleportHelper.GetEligibleBattlegrounds(player);

            if (eligible.Count == 0)
            {
                player.Out.SendMessage(
                    "Hello " + player.Name + "! There are no battlegrounds available for your level.",
                    eChatType.CT_Say, eChatLoc.CL_PopupWindow);
                return true;
            }

            StringBuilder sb = new();
            sb.Append("Hello ").Append(player.Name)
              .AppendLine("! I can teleport you to the following battlegrounds (filtered to your level):");
            sb.AppendLine();

            foreach (DbBattleground bg in eligible)
            {
                sb.Append('[').Append(BattlegroundTeleportHelper.GetBattlegroundLabel(bg)).Append("]  ");
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

            DbBattleground chosen = BattlegroundTeleportHelper.FindEligibleByLabel(player, str);
            if (chosen == null)
            {
                // Unknown / ineligible label — stay quiet so the NPC doesn't
                // spam when a player whispers something irrelevant.
                return true;
            }

            if (player.InCombat)
            {
                player.Out.SendMessage("You can't port while in combat.",
                    eChatType.CT_Say, eChatLoc.CL_PopupWindow);
                return true;
            }

            DbTeleport destination = BattlegroundTeleportHelper.BuildBattlegroundDestination(player, chosen);
            if (destination == null)
            {
                player.Out.SendMessage("Could not resolve a destination for that battleground.",
                    eChatType.CT_Say, eChatLoc.CL_PopupWindow);
                return true;
            }

            player.Out.SendMessage(
                $"{Name} says, \"I'm now teleporting you to {destination.TeleportID}.\"",
                eChatType.CT_Say, eChatLoc.CL_ChatWindow);
            player.MoveTo((ushort) destination.RegionID, destination.X, destination.Y,
                destination.Z, (ushort) destination.Heading);
            return true;
        }

        [ScriptLoadedEvent]
        public static void OnScriptCompiled(DOLEvent e, object sender, EventArgs args)
        {
            log.Info("BG Teleporter initialized (DB-driven, level-filtered).");
        }
    }
}

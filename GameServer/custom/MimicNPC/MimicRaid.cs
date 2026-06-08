using DOL.AI.Brain;
using DOL.GS.PacketHandler;
using DOL.GS.ServerProperties;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace DOL.GS.Scripts
{
    /// <summary>
    /// Orchestrates player-led mimic raids — a "battlegroup" sized force for
    /// large PvE content that a single 8-man group can't clear (dragons, ML /
    /// epic encounters, big dungeon pulls).
    ///
    /// A raid is:
    ///   * a real <see cref="BattleGroup"/> with the human player as leader
    ///     (so /bc battle-chat, loot treasurer and rolls all work like a normal
    ///     raid), plus
    ///   * the player's own <see cref="Group"/> (player + up to 7 mimics), plus
    ///   * one or more pure-mimic groups, each with its own mimic leader.
    ///
    /// Mimics can't be members of a DAoC BattleGroup (it is GamePlayer-only),
    /// so raid cohesion is achieved instead through ownership tracking
    /// (<see cref="MimicManager"/>) and the per-bot <see cref="MimicBrain.RaidAnchor"/>:
    /// every secondary-group leader trails the human anchor and drags its whole
    /// group along, so the entire raid travels and (auto-)fights as one body.
    ///
    /// Raids are PvE-only by design: <see cref="MimicBrain.IsRaidMember"/> bots
    /// never auto-enable PvP mode, and creation is refused in RvR regions/zones.
    /// </summary>
    public static class MimicRaid
    {
        /// <summary>Live raid bots owned by the player (across all raid groups).</summary>
        private static List<MimicNPC> OwnedRaidBots(GamePlayer player)
        {
            List<MimicNPC> list = new();

            string account = player?.Client?.Account?.Name;
            if (string.IsNullOrEmpty(account))
                return list;

            foreach (MimicNPC m in MimicManager.GetLiveOwnedBy(account))
            {
                if (m != null && m.MimicBrain != null && m.MimicBrain.IsRaidMember
                    && m.ObjectState == GameObject.eObjectState.Active)
                    list.Add(m);
            }

            return list;
        }

        /// <summary>True when the player currently has any live raid bots.</summary>
        public static bool HasRaid(GamePlayer player) => OwnedRaidBots(player).Count > 0;

        /// <summary>
        /// Builds a full raid for <paramref name="player"/>. Returns false with
        /// a human-readable <paramref name="error"/> on a guard failure (RvR
        /// zone, raid already active, etc.).
        /// </summary>
        public static bool CreateRaid(GamePlayer player, eRealm realm, int numGroups, byte level, bool preventCombat, out string error)
        {
            error = null;

            if (player == null)
            {
                error = "Joueur introuvable.";
                return false;
            }

            // PvE-only: a mimic battlegroup is a PvE tool. Refuse in RvR.
            if ((player.CurrentRegion != null && player.CurrentRegion.IsRvR)
                || (player.CurrentZone != null && player.CurrentZone.IsRvR))
            {
                error = "Les raids de mimics sont reserves au PvE : impossible d'en creer en zone RvR.";
                return false;
            }

            if (HasRaid(player))
            {
                error = "Vous avez deja un raid actif. Utilisez /mraid disband d'abord.";
                return false;
            }

            int maxGroups = MimicConfig.MIMIC_RAID_MAX_GROUPS > 0 ? MimicConfig.MIMIC_RAID_MAX_GROUPS : 8;
            numGroups = Math.Clamp(numGroups, 1, maxGroups);

            Point3D origin = new(player.X, player.Y, player.Z);

            // Real battlegroup with the player as leader, so the human raid
            // leader gets the full raid toolkit (battle chat, rolls, treasurer).
            EnsureBattleGroup(player);

            int groupsBuilt = 0;

            // First group: the player's own group (player + up to 7 mimics).
            Group playerGroup = player.Group;
            if (playerGroup == null)
            {
                playerGroup = new Group(player);
                GroupMgr.AddGroup(playerGroup);
                playerGroup.AddMember(player);
            }

            int playerGroupFree = Properties.GROUP_MAX_MEMBER - playerGroup.MemberCount;
            if (playerGroupFree > 0)
            {
                List<MimicNPC> firstGroupBots = BuildGroupBots(player, realm, playerGroupFree, level, preventCombat, origin);

                foreach (MimicNPC m in firstGroupBots)
                    playerGroup.AddMember(m);

                if (firstGroupBots.Count > 0)
                {
                    MimicGroupComposer.AutoAssignRoles(firstGroupBots);
                    AnchorBots(firstGroupBots, player);
                    groupsBuilt = 1;
                }
            }

            // Secondary groups: pure-mimic 8-man groups, each with its own
            // mimic leader who trails the human anchor.
            for (int g = groupsBuilt; g < numGroups; g++)
            {
                List<MimicNPC> bots = BuildGroupBots(player, realm, Properties.GROUP_MAX_MEMBER, level, preventCombat, origin);
                if (bots.Count == 0)
                    continue;

                Group grp = new(bots[0]);
                GroupMgr.AddGroup(grp);

                foreach (MimicNPC m in bots)
                    grp.AddMember(m);

                MimicGroupComposer.AutoAssignRoles(bots);
                AnchorBots(bots, player);
            }

            return HasRaid(player);
        }

        /// <summary>Teleports every live raid bot to the raid leader's side.</summary>
        public static int Summon(GamePlayer player)
        {
            int n = 0;

            foreach (MimicNPC m in OwnedRaidBots(player))
            {
                // Skip dead bots awaiting a rez — see /msummon for the rationale.
                if (!m.IsAlive || m.InRezWait)
                    continue;

                int x = player.X + Util.Random(-150, 150);
                int y = player.Y + Util.Random(-150, 150);

                if (player.CurrentRegionID == m.CurrentRegionID)
                {
                    m.MoveInRegion(player.CurrentRegionID, x, y, player.Z + 10, player.Heading, true);
                }
                else
                {
                    m.MoveTo(player.CurrentRegionID, x, y, player.Z + 10, player.Heading);
                    m.MimicBrain.FSM.SetCurrentState(eFSMStateType.WAKING_UP);
                    m.Group?.UpdateMember(m, true, false);
                    m.Group?.UpdateGroupWindow();
                }

                n++;
            }

            return n;
        }

        /// <summary>
        /// Clears any camp/pull point and re-anchors every raid bot to the
        /// player so the whole raid resumes travelling as one body.
        /// </summary>
        public static int Follow(GamePlayer player)
        {
            HashSet<Group> groups = new();
            int n = 0;

            foreach (MimicNPC m in OwnedRaidBots(player))
            {
                m.MimicBrain.RaidAnchor = player;
                if (m.Group != null)
                    groups.Add(m.Group);
                m.MimicBrain.FSM.SetCurrentState(eFSMStateType.WAKING_UP);
                n++;
            }

            foreach (Group g in groups)
            {
                g.MimicGroup?.SetCampPoint(null);
                g.MimicGroup?.SetPullPoint(null);
            }

            return n;
        }

        /// <summary>Sends every combat-capable raid bot onto the player's target.</summary>
        public static int AttackTarget(GamePlayer player, GameLiving target)
        {
            if (target == null)
                return 0;

            int n = 0;

            foreach (MimicNPC m in OwnedRaidBots(player))
            {
                if (m.MimicBrain is not MimicBrain brain)
                    continue;
                if (brain.PreventCombat || brain.IsHealer || !m.IsAlive)
                    continue;
                if (!GameServer.ServerRules.IsAllowedToAttack(m, target, true))
                    continue;

                brain.AddToAggroList(target, brain.GetMaxAggro() + 1);
                brain.AttackMostWanted();
                n++;
            }

            return n;
        }

        /// <summary>
        /// Parks the raid at a stable camp anchored on the player's position.
        /// Each group gets its own camp point + camp roles, exactly like
        /// /mcamp does for a single group.
        /// </summary>
        public static int Camp(GamePlayer player)
        {
            HashSet<Group> groups = new();

            foreach (MimicNPC m in OwnedRaidBots(player))
                if (m.Group != null)
                    groups.Add(m.Group);

            if (groups.Count == 0)
                return 0;

            Point3D camp = new(player.X, player.Y, player.Z);

            foreach (Group g in groups)
            {
                g.MimicGroup.SetCampPoint(camp);
                MimicGroupComposer.EnsureCampRoles(g);
            }

            int n = 0;
            foreach (MimicNPC m in OwnedRaidBots(player))
            {
                m.MimicBrain.FSM.SetCurrentState(eFSMStateType.CAMP);
                n++;
            }

            return n;
        }

        /// <summary>Deletes every raid bot and tears down the lone-leader battlegroup.</summary>
        public static int Disband(GamePlayer player)
        {
            int removed = 0;

            foreach (MimicNPC m in OwnedRaidBots(player))
            {
                if (m.ObjectState == GameObject.eObjectState.Active)
                {
                    // Flag as an expected teardown so the MIMIC-FLICKER
                    // diagnostic doesn't log a stack trace — disband is voluntary.
                    m._beingDeleted = true;
                    m.Delete();
                    removed++;
                }
            }

            // Remove the player from the battlegroup only when they're its sole
            // member (the bots were never in it). If other humans joined, leave
            // their battlegroup untouched.
            BattleGroup bg = player.TempProperties.GetProperty<BattleGroup>(BattleGroup.BATTLEGROUP_PROPERTY);
            if (bg != null && bg.PlayerCount <= 1)
                bg.RemoveBattlePlayer(player);

            return removed;
        }

        /// <summary>Sends the player a popup summarising their raid.</summary>
        public static void Status(GamePlayer player)
        {
            List<MimicNPC> bots = OwnedRaidBots(player);

            StringBuilder sb = new();
            sb.AppendLine("------- Votre raid -------");

            if (bots.Count == 0)
            {
                sb.AppendLine("Aucun raid actif. Tapez /mraid pour en creer un.");
                player.Out.SendMessage(sb.ToString(), eChatType.CT_System, eChatLoc.CL_PopupWindow);
                return;
            }

            // Group bots by their Group, listing the player's group first.
            var byGroup = bots.Where(m => m.Group != null).GroupBy(m => m.Group).ToList();

            int alive = bots.Count(m => m.IsAlive && !m.InRezWait);
            sb.AppendLine($"{bots.Count} mimic(s) sur {byGroup.Count} groupe(s) — {alive} en vie.");
            sb.AppendLine();

            int idx = 1;
            foreach (var grp in byGroup)
            {
                bool isPlayerGroup = grp.Key == player.Group;
                sb.Append("Groupe ").Append(idx++);
                if (isPlayerGroup)
                    sb.Append(" (le votre)");
                sb.AppendLine(" :");

                foreach (MimicNPC m in grp)
                {
                    string state = !m.IsAlive || m.InRezWait ? " [mort]" : string.Empty;
                    sb.Append("   ").Append(m.Name)
                      .Append(" - ").Append(m.CharacterClass?.Name ?? "?")
                      .Append(" L").Append(m.Level).AppendLine(state);
                }
            }

            player.Out.SendMessage(sb.ToString(), eChatType.CT_System, eChatLoc.CL_PopupWindow);
        }

        // ----------------------------------------------------------------
        // Helpers
        // ----------------------------------------------------------------

        private static void EnsureBattleGroup(GamePlayer player)
        {
            BattleGroup bg = player.TempProperties.GetProperty<BattleGroup>(BattleGroup.BATTLEGROUP_PROPERTY);
            if (bg != null)
                return;

            bg = new BattleGroup();
            bg.SetBGLeader(player);
            bg.AddBattlePlayer(player, true);
        }

        /// <summary>
        /// Spawns a balanced composition of <paramref name="count"/> mimics of
        /// the given realm/level around <paramref name="origin"/> and registers
        /// them as owned by the player. Returns the created (in-world) bots.
        /// </summary>
        private static List<MimicNPC> BuildGroupBots(GamePlayer player, eRealm realm, int count, byte level, bool preventCombat, Point3D origin)
        {
            List<MimicNPC> created = new();
            if (count <= 0)
                return created;

            List<eMimicClass> composition = MimicGroupComposer.BuildComposition(realm, count);

            foreach (eMimicClass cls in composition)
            {
                // Disperse around the origin so they don't stack on one tile.
                Point3D pos = new(origin.X + Util.Random(-150, 150), origin.Y + Util.Random(-150, 150), origin.Z);

                MimicNPC mimic = MimicManager.GetMimic(cls, level, preventCombat: preventCombat);
                if (mimic == null)
                    continue;

                if (!MimicManager.AddMimicToWorld(mimic, pos, player.CurrentRegionID))
                    continue;

                MimicManager.RegisterOwned(player, mimic);
                created.Add(mimic);
            }

            return created;
        }

        /// <summary>
        /// Stamps the raid anchor on each bot, forces PvE mode (raids never
        /// PvP), and kicks the FSM so they evaluate their follow target.
        /// </summary>
        private static void AnchorBots(List<MimicNPC> bots, GamePlayer anchor)
        {
            foreach (MimicNPC m in bots)
            {
                if (m?.MimicBrain == null)
                    continue;

                m.MimicBrain.RaidAnchor = anchor;
                m.MimicBrain.PvPMode = false; // raids are PvE-only
                m.MimicBrain.FSM.SetCurrentState(eFSMStateType.WAKING_UP);
            }
        }
    }
}

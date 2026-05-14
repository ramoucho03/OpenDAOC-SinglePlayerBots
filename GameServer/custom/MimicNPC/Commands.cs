using DOL.AI;
using DOL.AI.Brain;
using DOL.GS.Commands;
using DOL.GS.PacketHandler;
using DOL.GS.Scripts.AI.Strategies;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace DOL.GS.Scripts
{
    /// <summary>
    /// Helpers shared by clickable-popup commands (/mmenu, /mlfg). The DAoC
    /// client only treats popup `[bracketed]` text as clickable when the
    /// player has an NPC targeted — clicks become a /whisper to that NPC.
    /// To make our popup links clickable we make sure a mimic owned by or
    /// grouped with the caller is targeted before we send the popup, then
    /// MimicNPC.WhisperReceive forwards `/cmd` whispers to ScriptMgr.HandleCommand.
    /// </summary>
    internal static class MimicPopupHelper
    {
        /// <summary>
        /// Returns a mimic to be targeted by the player so popup brackets are
        /// clickable. If the player already has a mimic targeted, that one is
        /// returned. Otherwise we pick the closest owned/grouped mimic in the
        /// same region and send a target-change packet so the client retargets.
        /// Returns null if no mimic is available.
        /// </summary>
        public static MimicNPC EnsureMimicTargeted(GamePlayer player)
        {
            if (player == null)
                return null;

            // Already targeting a mimic? Use it.
            if (player.TargetObject is MimicNPC current
                && current.IsAlive
                && current.ObjectState == GameObject.eObjectState.Active)
                return current;

            MimicNPC chosen = null;
            int bestDistSq = int.MaxValue;

            // Prefer mimics owned by this player (account-scoped).
            string accountName = player.Client?.Account?.Name;
            if (!string.IsNullOrEmpty(accountName))
            {
                var owned = MimicManager.GetLiveOwnedBy(accountName);
                foreach (MimicNPC m in owned)
                {
                    if (m == null || !m.IsAlive || m.ObjectState != GameObject.eObjectState.Active)
                        continue;
                    if (m.CurrentRegionID != player.CurrentRegionID)
                        continue;

                    int dx = m.X - player.X;
                    int dy = m.Y - player.Y;
                    int d2 = dx * dx + dy * dy;
                    if (d2 < bestDistSq)
                    {
                        bestDistSq = d2;
                        chosen = m;
                    }
                }
            }

            // Fall back to any grouped mimic if no owned one is in range.
            if (chosen == null && player.Group != null)
            {
                foreach (GameLiving gl in player.Group.GetMembersInTheGroup())
                {
                    if (gl is MimicNPC m
                        && m.IsAlive
                        && m.ObjectState == GameObject.eObjectState.Active
                        && m.CurrentRegionID == player.CurrentRegionID)
                    {
                        int dx = m.X - player.X;
                        int dy = m.Y - player.Y;
                        int d2 = dx * dx + dy * dy;
                        if (d2 < bestDistSq)
                        {
                            bestDistSq = d2;
                            chosen = m;
                        }
                    }
                }
            }

            if (chosen != null)
            {
                player.TargetObject = chosen;
                player.Out.SendChangeTarget(chosen);
            }

            return chosen;
        }

        /// <summary>
        /// Sends a popup whose [bracketed] links are clickable on 1.127+ clients.
        /// The trick is to route the message through GameNPC.SayTo so it ships
        /// as `"&lt;NpcName&gt; says, \"...\""` (CT_System + CL_PopupWindow). The
        /// client recognises the NPC source and turns every `[token]` into a
        /// `/whisper &lt;NpcName&gt; token` shortcut. If no mimic is available we
        /// fall back to a plain popup with a hint to create a mimic first.
        /// </summary>
        public static void SendClickablePopup(GamePlayer player, MimicNPC contextMimic, string body)
        {
            if (player?.Out == null || string.IsNullOrEmpty(body))
                return;

            if (contextMimic != null)
            {
                // SayTo wraps the body as "<Name> says, \"<body>\"" and sends
                // it as CT_System/CL_PopupWindow — exactly the format the
                // 1.127 client expects for bracket clicks to whisper back.
                // announce=false skips the "X speaks to Y" area broadcast.
                contextMimic.SayTo(player, eChatLoc.CL_PopupWindow, body, false);
                return;
            }

            string fallback = body
                + "\n\n(Astuce : cree d'abord un mimic via /mcreate ou /mgroup "
                + "puis relance la commande pour rendre les liens cliquables.)";
            player.Out.SendMessage(fallback, eChatType.CT_System, eChatLoc.CL_PopupWindow);
        }
    }

    #region Admin/GM/Debug/Cheats

    [CmdAttribute(
    "&mcreate",
    ePrivLevel.Player,
    "/mcreate classe (niveau) (spec) (inv) - Crée un mimic d'une classe, d'un niveau et d'une spécialisation données à votre position ou sur le ground target, et l'invite dans votre groupe si 'inv' est précisé.")]
    public class MimicCreateCommandHandler : AbstractCommandHandler, ICommandHandler
    {
        public void OnCommand(GameClient client, string[] args)
        {
            if (args.Length > 0)
            {
                GamePlayer player = client.Player;
                eMimicClass mclass;
                byte level = player.Level;
                eSpecType mimicSpec = eSpecType.None;
                bool invite = false;

                if (args.Length > 1)
                {
                    if (!Enum.TryParse<eMimicClass>(args[1], true, out mclass))
                    {
                        player.Out.SendMessage("'" + args[1] + "' n'est pas une classe valide.", eChatType.CT_Say, eChatLoc.CL_ChatWindow);
                        return;
                    }
                }
                else
                {
                    player.Out.SendMessage("Vous devez indiquer une classe.", eChatType.CT_Say, eChatLoc.CL_ChatWindow);
                    return;
                }

                for (int i = 2; i < args.Length; i++)
                {
                    if (args[i].StartsWith("inv", StringComparison.OrdinalIgnoreCase))
                        invite = true;
                    else if (byte.TryParse(args[i], out byte newLevel))
                    {
                        if (newLevel < 1 || newLevel > GamePlayer.MAX_LEVEL)
                        {
                            player.Out.SendMessage("Le niveau doit être compris entre 1 et " + GamePlayer.MAX_LEVEL + ".", eChatType.CT_Say, eChatLoc.CL_ChatWindow);
                            return;
                        }
                        level = newLevel; // TryParse écrase la valeur de sortie, d'où la variable intermédiaire.
                    }
                    else if (!Enum.TryParse<eSpecType>(args[i], true, out mimicSpec) || mimicSpec == eSpecType.None)
                    {
                        player.Out.SendMessage("Argument non reconnu : " + args[i], eChatType.CT_Say, eChatLoc.CL_ChatWindow);
                        return;
                    }
                }

                Point3D position = new Point3D(player.X, player.Y, player.Z);

                if (player.GroundTarget != null)
                {
                    Point2D playerPos = new Point2D(player.X, player.Y);

                    if (client.Player.GroundTarget.GetDistance(playerPos) < WorldMgr.VISIBILITY_DISTANCE)
                        position = new Point3D(player.GroundTarget);
                }

                MimicNPC mimic = MimicManager.GetMimic(mclass, level, spec: mimicSpec);
                MimicManager.AddMimicToWorld(mimic, position, player.CurrentRegionID);
                MimicManager.RegisterOwned(player, mimic);

                if (invite && GameServer.ServerRules.IsSameRealm(player, mimic, true))
                {
                    if (player.Group == null)
                    {
                        Group group = new Group(player);
                        GroupMgr.AddGroup(group);
                        group.AddMember(player);
                    }

                    if (!player.Group.AddMember(mimic))
                        player.Out.SendMessage("Impossible d'ajouter le mimic au groupe.", eChatType.CT_Say, eChatLoc.CL_ChatWindow);
                }
            }
        }
    }

    [CmdAttribute(
       "&mgroup",
       ePrivLevel.Player,
       "/mgroup royaume [taille] [niveau] [preventCombat] - Invoque un groupe equilibre de mimics du royaume choisi (tank/heal/cc/dps).",
       "Si vous etes en groupe, ils vous y rejoignent ; sinon un nouveau groupe est cree autour de vous.")]
    public class MimicSummonMimicGroupCommandHandler : AbstractCommandHandler, ICommandHandler
    {
        public void OnCommand(GameClient client, string[] args)
        {
            if (args.Length < 2)
                return;

            args[1] = args[1].ToLower();

            eRealm realm = args[1] switch
            {
                "alb" or "albion"   => eRealm.Albion,
                "hib" or "hibernia" => eRealm.Hibernia,
                "mid" or "midgard"  => eRealm.Midgard,
                _ => eRealm.None,
            };

            if (realm == eRealm.None)
            {
                client.Player.Out.SendMessage("Royaume invalide. Utilisez alb, hib ou mid.", eChatType.CT_Say, eChatLoc.CL_ChatWindow);
                return;
            }

            byte groupSize = 8;
            if (args.Length >= 3 && (!byte.TryParse(args[2], out groupSize) || groupSize < 1 || groupSize > 8))
                groupSize = 8;

            byte level;
            if (args.Length >= 4)
            {
                if (!byte.TryParse(args[3], out level) || level < 1 || level > 50)
                    level = 1;
            }
            else
                level = client.Player.Level;

            bool preventCombat = false;
            if (args.Length >= 5)
                bool.TryParse(args[4], out preventCombat);

            Point3D origin = new(client.Player.X, client.Player.Y, client.Player.Z);

            if (client.Player.GroundTarget != null)
            {
                Point2D playerPos = new(client.Player.X, client.Player.Y);

                if (client.Player.GroundTarget.GetDistance(playerPos) < 5000)
                    origin = new Point3D(client.Player.GroundTarget);
            }

            // Build a balanced composition: tank, healer, support/CC, then fill with DPS/casters.
            // The template is sized to groupSize and respects realm class availability.
            List<eMimicClass> composition = MimicGroupComposer.BuildComposition(realm, groupSize);

            List<MimicNPC> created = new();

            foreach (eMimicClass cls in composition)
            {
                // Disperse around the origin so they don't stack on the same tile.
                Point3D pos = new(origin.X + Util.Random(-120, 120), origin.Y + Util.Random(-120, 120), origin.Z);

                MimicNPC mimic = MimicManager.GetMimic(cls, level, preventCombat: preventCombat);

                if (mimic == null)
                    continue;

                if (!MimicManager.AddMimicToWorld(mimic, pos, client.Player.CurrentRegionID))
                    continue;

                MimicManager.RegisterOwned(client.Player, mimic);
                created.Add(mimic);
            }

            if (created.Count == 0)
                return;

            // Join the player's group if they have one and there's room; otherwise create a fresh bot group.
            if (client.Player.Group != null && client.Player.Group.MemberCount + created.Count <= ServerProperties.Properties.GROUP_MAX_MEMBER)
            {
                foreach (MimicNPC m in created)
                    client.Player.Group.AddMember(m);
            }
            else
            {
                created[0].Group = new Group(created[0]);
                GroupMgr.AddGroup(created[0].Group);
                created[0].Group.AddMember(created[0]);

                for (int i = 1; i < created.Count; i++)
                    created[0].Group.AddMember(created[i]);
            }

            // Auto-assign roles based on the class roles we just selected.
            MimicGroupComposer.AutoAssignRoles(created);

            foreach (MimicNPC m in created)
                m.MimicBrain?.FSM.SetCurrentState(eFSMStateType.WAKING_UP);
        }
    }

    [CmdAttribute(
    "&mspawner",
    ePrivLevel.Player,
    "/mspawner royaume niveauMin niveauMax maxAmount - Spawn périodique de mimics à votre position ou au ground target.")]
    public class MimicSpawnerCommandHandler : AbstractCommandHandler, ICommandHandler
    {
        public void OnCommand(GameClient client, string[] args)
        {
            if (args.Length < 5)
            {
                client.Player.Out.SendMessage("Usage : /mspawner <royaume> <niveauMin> <niveauMax> <max>", eChatType.CT_System, eChatLoc.CL_SystemWindow);
                return;
            }

            Point3D position = new Point3D(client.Player.X, client.Player.Y, client.Player.Z);

            if (client.Player.GroundTarget != null && client.Player.GroundTarget.IsWithinRadius(position, WorldMgr.VISIBILITY_DISTANCE))
                position = new Point3D(client.Player.GroundTarget);

            args[1] = args[1].ToLower();

            if (!int.TryParse(args[2], out int levelMin) ||
                !int.TryParse(args[3], out int levelMax) ||
                !int.TryParse(args[4], out int maxAmount))
            {
                client.Player.Out.SendMessage("niveauMin, niveauMax et max doivent être des entiers.", eChatType.CT_System, eChatLoc.CL_SystemWindow);
                return;
            }

            levelMin = Math.Max(1, levelMin);
            levelMax = Math.Min(levelMax, 50);

            if (levelMin > levelMax)
                (levelMin, levelMax) = (levelMax, levelMin);

            if (maxAmount > 500 || maxAmount < 0)
                maxAmount = 1;

            eRealm? realm = args[1] switch
            {
                "alb" or "albion" => eRealm.Albion,
                "mid" or "midgard" => eRealm.Midgard,
                "hib" or "hibernia" => eRealm.Hibernia,
                _ => null
            };

            if (realm == null)
            {
                client.Player.Out.SendMessage("Royaume inconnu : '" + args[1] + "'. Utilisez alb / mid / hib.", eChatType.CT_System, eChatLoc.CL_SystemWindow);
                return;
            }

            // The MimicSpawner constructor already calls AddToWorld() and registers
            // itself into MimicSpawning.MimicSpawners, so we just instantiate.
            _ = new MimicSpawner(realm.Value, levelMin, levelMax, maxAmount, 50, position, client.Player.CurrentRegionID);
        }
    }

    [CmdAttribute(
       "&mpvp",
       ePrivLevel.Player,
       "/mpvp (true/false) - Active / désactive le mode PvP sur le mimic ciblé, ou sur tous les mimics de votre groupe sans cible.")]
    public class MimicPvPModeCommandHandler : AbstractCommandHandler, ICommandHandler
    {
        public void OnCommand(GameClient client, string[] args)
        {
            if (client.Player == null)
                return;

            string message = string.Empty;
            MimicNPC mimic = client.Player.TargetObject as MimicNPC;

            if (args.Length > 1)
            {
                args[1] = args[1].ToLower();

                bool toggle = false;

                switch (args[1])
                {
                    case "true":
                    toggle = true;
                    break;

                    case "false":
                    toggle = false;
                    break;
                }

                if (mimic != null)
                {
                    mimic.MimicBrain.PvPMode = toggle;
                    message = "Mode PvP de " + mimic.Name + " : " + (toggle ? "activé" : "désactivé") + ".";
                }
                else if (client.Player.Group != null)
                {
                    foreach (GameLiving groupMember in client.Player.Group.GetMembersInTheGroup())
                    {
                        if (groupMember is MimicNPC mimicNPC)
                            mimicNPC.MimicBrain.PvPMode = toggle;
                    }

                    message = "Mode PvP pour les mimics de votre groupe : " + (toggle ? "activé" : "désactivé") + ".";
                }

                client.Player.Out.SendMessage(message, eChatType.CT_Say, eChatLoc.CL_ChatWindow);
            }
        }
    }

    [CmdAttribute(
   "&mpc",
   ePrivLevel.Player,
   "/mpc (true/false) [group] - Active / désactive PreventCombat sur le mimic ciblé ou son groupe, ou sur votre groupe sans cible.")]
    public class MimicCombatPreventCommandHandler : AbstractCommandHandler, ICommandHandler
    {
        public void OnCommand(GameClient client, string[] args)
        {
            if (client.Player == null)
                return;

            string message = string.Empty;
            MimicNPC mimic = client.Player.TargetObject as MimicNPC;

            if (args.Length > 1)
            {
                args[1] = args[1].ToLower();

                bool toggle = false;

                switch (args[1])
                {
                    case "true":
                    toggle = true;
                    break;

                    case "false":
                    toggle = false;
                    break;
                }

                if (mimic != null)
                {
                    if (args.Length > 2 && args[2].Equals("group", StringComparison.OrdinalIgnoreCase)
                        && mimic.Group != null)
                    {
                        foreach (GameLiving groupMember in mimic.Group.GetMembersInTheGroup())
                        {
                            if (groupMember is MimicNPC mimicNPC)
                            {
                                mimicNPC.MimicBrain.PreventCombat = toggle;
                                message = "PreventCombat pour le groupe de " + mimicNPC.Name + " : " + (toggle ? "activé" : "désactivé") + ".";
                            }
                        }
                    }
                    else
                    {
                        mimic.MimicBrain.PreventCombat = toggle;
                        message = "PreventCombat pour " + mimic.Name + " : " + (toggle ? "activé" : "désactivé") + ".";
                    }
                }
                else if (client.Player.Group != null)
                {
                    foreach (GameLiving groupMember in client.Player.Group.GetMembersInTheGroup())
                    {
                        if (groupMember is MimicNPC mimicNPC)
                            mimicNPC.MimicBrain.PreventCombat = toggle;
                    }

                    message = "PreventCombat pour les mimics de votre groupe : " + (toggle ? "activé" : "désactivé") + ".";
                }

                client.Player.Out.SendMessage(message, eChatType.CT_Say, eChatLoc.CL_ChatWindow);
            }
        }
    }

    [CmdAttribute(
    "&mheal",
    ePrivLevel.Player,
    "/mheal - Bascule un mimic soigneur entre 'combat actif' et 'reste en retrait et soigne'.")]
    public class MimicHealCommandHandler : AbstractCommandHandler, ICommandHandler
    {
        public void OnCommand(GameClient client, string[] args)
        {
            if (client?.Player == null)
                return;

            if (client.Player.TargetObject is not MimicNPC mimic)
            {
                client.Player.Out.SendMessage("Vous devez cibler un mimic soigneur.", eChatType.CT_System, eChatLoc.CL_SystemWindow);
                return;
            }

            if (mimic.Group == null)
                mimic.Whisper(client.Player, "Je dois être dans un groupe.");
            else
                mimic.Group.MimicGroup.SetHealer(mimic);
        }
    }

    [CmdAttribute(
    "&mbattle",
    ePrivLevel.Player,
    "/mbattle [Région] (Start/Stop/Clear)",
    "Régions : Thid. Start - démarre le spawn. Stop - arrête le spawn. Clear - arrête et retire les mimics.")]
    public class MimicBattleCommandHandler : AbstractCommandHandler, ICommandHandler
    {
        public void OnCommand(GameClient client, string[] args)
        {
            if (args.Length > 2)
            {
                args[1] = args[1].ToLower();
                args[2] = args[2].ToLower();

                switch (args[1])
                {
                    case "thid":
                    switch (args[2])
                    {
                        case "start": MimicBattlegrounds.ThidBattleground.Start(); break;
                        case "stop": MimicBattlegrounds.ThidBattleground.Stop(); break;
                        case "clear": MimicBattlegrounds.ThidBattleground.Clear(); break;
                    }
                    break;
                }
            }
        }
    }

    [CmdAttribute(
      "&msummon",
      ePrivLevel.Player,
      "/msummon - Téléporte tous les mimics de votre groupe à votre position.")]
    public class MimimcSummonCommandHandler : AbstractCommandHandler, ICommandHandler
    {
        public void OnCommand(GameClient client, string[] args)
        {
            if (client.Player.Group == null)
                return;

            int X = client.Player.X;
            int Y = client.Player.Y;
            int Z = client.Player.Z;
            ushort heading = client.Player.Heading;

            foreach (GameLiving groupMember in client.Player.Group.GetMembersInTheGroup())
            {
                if (groupMember is MimicNPC mimicNPC)
                {
                    bool movePet = false;

                    if (mimicNPC.ControlledBrain != null)
                    {
                        if (mimicNPC.CharacterClass.ID is not ((int)eCharacterClass.Theurgist) and not ((int)eCharacterClass.Animist))
                            movePet = true;
                    }

                    if (client.Player.CurrentRegionID == mimicNPC.CurrentRegionID)
                    {
                        mimicNPC.MoveInRegion(client.Player.CurrentRegionID, X, Y, Z + 10, heading, true);

                        if (movePet)
                        {
                            Point2D point = mimicNPC.GetPointFromHeading(mimicNPC.Heading, 64);
                            IControlledBrain npc = mimicNPC.ControlledBrain;

                            if (npc != null)
                            {
                                GameNPC petBody = npc.Body;
                                petBody.MoveInRegion(mimicNPC.CurrentRegionID, point.X, point.Y, Z + 10, (ushort)((mimicNPC.Heading + 2048) % 4096), true);

                                if (petBody != null && petBody.ControlledNpcList != null)
                                {
                                    foreach (IControlledBrain controlledBrain in petBody.ControlledNpcList)
                                    {
                                        if (controlledBrain != null && controlledBrain.Body != null)
                                        {
                                            GameNPC petBody2 = controlledBrain.Body;

                                            if (petBody2 != null)
                                                petBody2.MoveInRegion(mimicNPC.CurrentRegionID, point.X, point.Y, Z + 10, (ushort)((mimicNPC.Heading + 2048) % 4096), false);
                                        }
                                    }
                                }
                            }
                        }
                    }
                    else
                    {
                        mimicNPC.MoveTo(client.Player.CurrentRegionID, X, Y, Z + 10, heading);
                        mimicNPC.MimicBrain.FSM.SetCurrentState(eFSMStateType.WAKING_UP);

                        groupMember.Group.UpdateMember(mimicNPC, true, false);
                        groupMember.Group.UpdateGroupWindow();
                    }
                }
            }
        }
    }

    #endregion Admin/GM/Debug/Cheats

    #region MimicGroup

    [CmdAttribute(
       "&mlfg",
       ePrivLevel.Player,
       "/mlfg [index] - Liste les mimics cherchant un groupe, ou recrute celui correspondant à l'index donné.")]
    public class MimicLfgCommandHandler : AbstractCommandHandler, ICommandHandler
    {
        public void OnCommand(GameClient client, string[] args)
        {
            GamePlayer player = client.Player;

            if (player == null)
                return;

            var entries = MimicLFGManager.GetLFG(player.Realm, player.Level);
            string message;
            int page = 1;

            if (args.Length < 2)
            {
                message = BuildMessage(entries, page);
            }
            else if (string.Equals(args[1], "page", StringComparison.OrdinalIgnoreCase))
            {
                // `/mlfg page N` — pagination only, no recruit
                if (args.Length < 3 || !int.TryParse(args[2], out int requestedPage) || requestedPage < 1)
                    requestedPage = 1;

                page = requestedPage;
                message = BuildMessage(entries, page);
            }
            else
            {
                if (!int.TryParse(args[1], out int parsed))
                {
                    player.Out.SendMessage("L'index doit être un nombre.", eChatType.CT_System, eChatLoc.CL_SystemWindow);
                    return;
                }

                int index = parsed - 1;

                if (index < 0 || index > entries.Count - 1)
                    message = BuildMessage(entries, page, true);
                else
                {
                    MimicLFGManager.MimicLFGEntry entry = entries[index];

                    int baseChance = 90;

                    if (MimicConfig.LFG_LEVEL_BIAS)
                    {
                        int biasAmount = 5;
                        int levelDifference = player.Level - entry.Level;

                        if (Math.Abs(levelDifference) > 1)
                            baseChance += levelDifference * biasAmount;

                        baseChance = Math.Clamp(baseChance, 5, 95);
                    }

                    if (Util.Chance(baseChance) && !entry.RefusedGroup)
                    {
                        if (player.Group == null)
                        {
                            Group group = new Group(player);
                            GroupMgr.AddGroup(group);
                            group.AddMember(player);
                        }

                        if (player.Group.GetMembersInTheGroup().Count < ServerProperties.Properties.GROUP_MAX_MEMBER)
                        {
                            MimicNPC mimic = MimicManager.GetMimic(entry.MimicClass, entry.Level, entry.Name, entry.Gender);
                            MimicManager.AddMimicToWorld(mimic, new Point3D(player.X, player.Y, player.Z), player.CurrentRegionID);
                            MimicManager.RegisterOwned(player, mimic);

                            player.Group.AddMember(mimic);

                            MimicLFGManager.Remove(player.Realm, entry);

                            // Send a refreshed list with new indexes to avoid using wrong indexes while leaving the dialogue open
                            entries = MimicLFGManager.GetLFG(player.Realm, player.Level);

                            // Stay on the same page after recruit so the player can keep picking from the same view.
                            page = Math.Max(1, (index / MAX_DISPLAYED) + 1);
                            message = BuildMessage(entries, page);
                        }
                        else
                            message = BuildMessage(entries, page, true);
                    }
                    else
                    {
                        if (entry.RefusedGroup)
                            player.Out.SendMessage(entry.Name + " vous envoie : \"Désolé, j'ai déjà refusé.\"", eChatType.CT_Send, eChatLoc.CL_SystemWindow);
                        else
                            player.Out.SendMessage(entry.Name + " vous envoie : \"Non merci, je cherche un autre groupe !\"", eChatType.CT_Send, eChatLoc.CL_SystemWindow);

                        entry.RefusedGroup = true;
                        return;
                    }
                }
            }

            // 1.127+ client only treats popup brackets as clickable when the
            // message is wrapped as "<NpcName> says, '...'" by NPC.SayTo — the
            // client uses the embedded NPC name to /whisper back. Without that
            // wrapping the brackets are inert. We target a mimic, then route the
            // popup through its SayTo so the brackets become whisper-clickable.
            MimicNPC contextMimic = MimicPopupHelper.EnsureMimicTargeted(player);
            MimicPopupHelper.SendClickablePopup(player, contextMimic, message);
        }

        // Cap the rendered list to stay under the 2048-byte popup packet limit.
        // Each line is ~45 chars, header/footer ~180, so ~30 entries is safe.
        private const int MAX_DISPLAYED = 30;

        private string BuildMessage(IReadOnlyList<MimicLFGManager.MimicLFGEntry> entries, int page, bool invalid = false)
        {
            // Each entry is rendered as a clickable popup link:  "[mlfg N] Name Class Level".
            // No leading slash — see MimicNPC.TryRouteAsCommand for the routing.
            System.Text.StringBuilder sb = new();
            sb.AppendLine("------- Mimics LFG -------");

            if (invalid)
            {
                sb.AppendLine("Index invalide ou groupe complet.");
                return sb.ToString();
            }

            if (!entries.Any())
            {
                sb.AppendLine("Aucun mimic disponible.");
                return sb.ToString();
            }

            int totalPages = Math.Max(1, (entries.Count + MAX_DISPLAYED - 1) / MAX_DISPLAYED);
            if (page < 1) page = 1;
            if (page > totalPages) page = totalPages;

            int startIdx = (page - 1) * MAX_DISPLAYED;       // 0-based start
            int endIdx = Math.Min(startIdx + MAX_DISPLAYED, entries.Count);

            sb.AppendLine($"Page {page} / {totalPages}  —  {entries.Count} mimics au total. Clique [mlfg N] pour recruter.");
            sb.AppendLine();

            for (int i = startIdx; i < endIdx; i++)
            {
                var entry = entries[i];
                string cls = Enum.GetName(typeof(eMimicClass), entry.MimicClass);
                int displayIndex = i + 1; // 1-based, global across pages
                sb.AppendLine($"[mlfg {displayIndex}]  {entry.Name,-20} {cls,-14} lvl {entry.Level}");
            }

            if (totalPages > 1)
            {
                sb.AppendLine();
                System.Text.StringBuilder nav = new();
                if (page > 1)
                    nav.Append($"[mlfg page {page - 1}]  <- Precedent   ");
                if (page < totalPages)
                    nav.Append($"[mlfg page {page + 1}]  Suivant ->");
                sb.AppendLine(nav.ToString());
            }

            return sb.ToString();
        }
    }

    [CmdAttribute(
        "&mrole",
        ePrivLevel.Player,
        "/mrole (leader/tank/assist/cc/puller) - Assigne un rôle à un membre du groupe.")]
    public class MimicRoleCommandHandler : AbstractCommandHandler, ICommandHandler
    {
        public void OnCommand(GameClient client, string[] args)
        {
            GamePlayer player = client.Player;
            GameLiving target = player.TargetObject as GameLiving;

            if (player.Group == null || target == null)
                return;

            if (args.Length > 1)
            {
                args[1] = args[1].ToLower();

                bool success = false;

                switch (args[1])
                {
                    case "leader": success = player.Group.MimicGroup.SetLeader(target); break;
                    case "tank": success = player.Group.MimicGroup.SetMainTank(target); break;
                    case "assist": success = player.Group.MimicGroup.SetMainAssist(target); break;
                    case "cc": success = player.Group.MimicGroup.SetMainCC(target); break;
                    case "puller": success = player.Group.MimicGroup.SetMainPuller(target); break;
                }

                if (!success)
                    player.Out.SendMessage("Impossible d'attribuer le rôle '" + args[1] + "'.", eChatType.CT_Say, eChatLoc.CL_SystemWindow);
            }
        }
    }

    [CmdAttribute(
        "&mcamp",
        ePrivLevel.Player,
        "/mcamp (here/set/remove/aggrorange/filter) - Définit le point de camp du groupe, son rayon d'aggro, et le niveau de con que le puller acceptera.")]
    public class MimicCampCommandHandler : AbstractCommandHandler, ICommandHandler
    {
        public void OnCommand(GameClient client, string[] args)
        {
            GamePlayer player = client.Player;
            Point3D target = client.Player.GroundTarget;

            if (player.Group == null)
                return;

            if (args.Length > 1)
            {
                args[1] = args[1].ToLower();

                switch (args[1])
                {
                    case "here":
                        player.Group.MimicGroup.SetCampPoint(new Point3D(player.X, player.Y, player.Z));
                        player.Out.SendMessage("Point de camp défini à votre position.", eChatType.CT_Say, eChatLoc.CL_SystemWindow);

                        foreach (GameLiving groupMember in player.Group.GetMembersInTheGroup())
                            if (groupMember is MimicNPC mimic)
                                mimic.Brain.FSM.SetCurrentState(eFSMStateType.CAMP);
                        break;
                    case "set":
                    {
                        if (target == null || player.GetDistance(player.GroundTarget) > 2000)
                        {
                            player.Out.SendMessage("Le ground target est trop loin.", eChatType.CT_Say, eChatLoc.CL_SystemWindow);
                            return;
                        }

                        player.Group.MimicGroup.SetCampPoint(target);

                        player.Out.SendMessage("Point de camp enregistré.", eChatType.CT_Say, eChatLoc.CL_SystemWindow);

                        foreach (GameLiving groupMember in player.Group.GetMembersInTheGroup())
                            if (groupMember is MimicNPC mimic)
                                mimic.Brain.FSM.SetCurrentState(eFSMStateType.CAMP);
                    }
                    break;

                    case "remove":
                    {
                        if (player.Group.MimicGroup.CampPoint != null)
                        {
                            player.Group.MimicGroup.SetCampPoint(null);
                            player.Out.SendMessage("Point de camp retiré.", eChatType.CT_Say, eChatLoc.CL_SystemWindow);
                        }
                        else
                            player.Out.SendMessage("Aucun point de camp à retirer.", eChatType.CT_Say, eChatLoc.CL_SystemWindow);

                        foreach (GameLiving groupMember in player.Group.GetMembersInTheGroup())
                        {
                            if (groupMember is MimicNPC mimic)
                            {
                                mimic.Brain.FSM.SetCurrentState(eFSMStateType.FOLLOW_THE_LEADER);
                                mimic.MimicBrain.AggroRange = 3600;
                            }
                        }
                    }
                    break;

                    case "aggrorange":
                    {
                        if (args.Length > 2)
                        {
                            if (!int.TryParse(args[2], out int range) || range < 0)
                                range = 550;

                            foreach (GameLiving groupMember in player.Group.GetMembersInTheGroup())
                            {
                                if (groupMember is MimicNPC mimic)
                                {
                                    FSMState mimicState = mimic.Brain.FSM.GetState(eFSMStateType.CAMP);

                                    ((MimicState_Camp)mimicState).AggroRange = range;
                                }
                            }

                            player.Out.SendMessage("Rayon d'aggro du camp : " + range, eChatType.CT_System, eChatLoc.CL_SystemWindow);
                        }
                    }
                    break;

                    case "filter":
                    {
                        if (args.Length > 2)
                        {
                            args[2] = args[2].ToLower();

                            switch (args[2])
                            {
                                case "purple": player.Group.MimicGroup.ConLevelFilter = 3; break;
                                case "red": player.Group.MimicGroup.ConLevelFilter = 2; break;
                                case "orange": player.Group.MimicGroup.ConLevelFilter = 1; break;
                                case "yellow": player.Group.MimicGroup.ConLevelFilter = 0; break;
                                case "blue": player.Group.MimicGroup.ConLevelFilter = -1; break;
                                case "green": player.Group.MimicGroup.ConLevelFilter = -2; break;
                            }
                        }
                    }
                    break;
                }
            }
        }
    }

    [CmdAttribute(
     "&mpull",
     ePrivLevel.Player,
     "/mpull - Fixe le camp et le point de pull à votre position, et fait pull votre cible par le puller.")]
    public class MimicPullCommandHandler : AbstractCommandHandler, ICommandHandler
    {

        public void OnCommand(GameClient client, string[] args)
        {
            var player = client.Player;

            if (player.TargetObject is not GameNPC target || !GameServer.ServerRules.IsAllowedToAttack(player, target, true))
                player.Out.SendMessage("Votre cible ne peut pas être pull.", eChatType.CT_System, eChatLoc.CL_SystemWindow);
            else if (player.Group?.MimicGroup is not MimicGroup mGroup)
                player.Out.SendMessage("Vous devez être groupé avec un mimic pour utiliser /mpull.", eChatType.CT_System, eChatLoc.CL_SystemWindow);
            else if (mGroup.MainPuller is not MimicNPC puller || puller.Brain is not MimicBrain brainPuller)
                player.Out.SendMessage("Vous devez désigner un puller pour utiliser /mpull.", eChatType.CT_System, eChatLoc.CL_SystemWindow);
            else if (puller.Inventory.GetItem(eInventorySlot.DistanceWeapon) == null)
                puller.Whisper(player, "Je n'ai pas d'arme à distance équipée.");
            else
            {
                mGroup.SetCampPoint(new Point3D(player.X, player.Y, player.Z));
                mGroup.SetPullPoint(new Point2D(player.X, player.Y));
                    
                foreach (GameLiving groupMember in player.Group.GetMembersInTheGroup())
                    if (groupMember is MimicNPC mimic)
                        mimic.Brain.FSM.SetCurrentState(eFSMStateType.CAMP);

                mGroup.ConLevelFilter = puller.GetConLevel(target);
                puller.TargetObject = target;
                brainPuller.LastTargetObject = null;
                brainPuller.PerformPull(target);
            }
        }
    }

    [CmdAttribute(
        "&mpullfrom",
        ePrivLevel.Player,
        "/mpullfrom (here/set/remove) - Définit le point depuis lequel le puller doit chercher à pull.")]
    public class MimicPullFromCommandHandler : AbstractCommandHandler, ICommandHandler
    {
        public void OnCommand(GameClient client, string[] args)
        {
            GamePlayer player = client.Player;
            Point3D target = client.Player.GroundTarget;

            if (player.Group == null)
                return;

            if (args.Length > 1)
            {
                args[1] = args[1].ToLower();

                switch (args[1])
                {
                    case "here":
                        player.Group.MimicGroup.SetPullPoint(new Point2D(player.X, player.Y));
                        player.Out.SendMessage("Point de pull défini à votre position.", eChatType.CT_Say, eChatLoc.CL_SystemWindow);
                        break;
                    case "set":
                    {
                        if (target == null || !player.GroundTargetInView)
                            return;

                        player.Group.MimicGroup.SetPullPoint(target);
                        player.Out.SendMessage("Position de pull enregistrée.", eChatType.CT_Say, eChatLoc.CL_SystemWindow);
                    }
                    break;

                    case "remove":
                    {
                        player.Group.MimicGroup.SetPullPoint(null);
                        player.Out.SendMessage("Position de pull retirée.", eChatType.CT_Say, eChatLoc.CL_SystemWindow);
                    }
                    break;
                }
            }
        }
    }

    [CmdAttribute(
    "&mfollow",
    ePrivLevel.Player,
    "/mfollow - Retire les points de camp et de pull, et fait suivre tous les mimics groupés.")]
    public class MimicFollowCommandHandler : AbstractCommandHandler, ICommandHandler
    {
        public void OnCommand(GameClient client, string[] args)
        {
            if (client.Player.Group != null)
            {
                client.Player.Group.MimicGroup.SetCampPoint(null);
                client.Player.Group.MimicGroup.SetPullPoint(null);

                foreach (GameLiving groupMember in client.Player.Group.GetMembersInTheGroup())
                    if (groupMember is MimicNPC mimic)
                        mimic.Brain.FSM.SetCurrentState(eFSMStateType.FOLLOW_THE_LEADER);
            }
        }
    }

    [CmdAttribute(
    "&mattack",
    ePrivLevel.Player,
    "/mattack - Fait attaquer votre cible par tous les mimics groupés.")]
    public class MimicAttackCommandHandler : AbstractCommandHandler, ICommandHandler
    {
        public void OnCommand(GameClient client, string[] args)
        {
            if (client.Player.Group != null && client.Player.TargetObject is GameLiving target)
                foreach (GameLiving groupMember in client.Player.Group.GetMembersInTheGroup())
                    if (groupMember is MimicNPC mimic && mimic.Brain is MimicBrain brain 
                        && !brain.PreventCombat && !brain.IsHealer)
                    {
                        brain.AddToAggroList(target, brain.GetMaxAggro() + 1);
                        brain.AttackMostWanted();
                     }
        }
    }

    [CmdAttribute(
   "&mintercept",
   ePrivLevel.Player,
   "/mintercept [nom/classe] - Désigne une cible que le mimic ciblé doit intercepter.")]
    public class MimicInterceptCommandHandler : AbstractCommandHandler, ICommandHandler
    {
        public void OnCommand(GameClient client, string[] args)
        {
            GamePlayer player = client.Player;
            MimicNPC target = player.TargetObject as MimicNPC;

            if (target == null || player.Group == null || (player.Group != null && !player.Group.IsInTheGroup(target)))
                return;

            if (!target.HasAbility(Abilities.Intercept))
            {
                target.Whisper(player, "Je n'ai pas cette capacité.");
                return;
            }

            GameLiving targetGroupMember = null;

            if (args.Length > 1)
            {
                args[1] = args[1].ToLower();

                eCharacterClass charClass = eCharacterClass.Unknown;
                Enum.TryParse<eCharacterClass>(args[1], true, out charClass);

                foreach (GameLiving groupMember in player.Group.GetMembersInTheGroup())
                {
                    if (groupMember != target &&
                        ((groupMember.Name.Equals(args[1], StringComparison.OrdinalIgnoreCase))
                        || (groupMember is MimicNPC mimic && mimic.CharacterClass.ID == (int)charClass)
                        || (groupMember is GamePlayer play && play.CharacterClass.ID == (int)charClass)))
                    {
                        targetGroupMember = groupMember;
                        break;
                    }
                }

                if (targetGroupMember != null)
                {
                    if (target.MimicBrain.SetIntercept(targetGroupMember, out bool ourEffect))
                        target.Group.SendMessageToGroupMembers(target, "J'intercepterai pour " + targetGroupMember.Name + ".", eChatType.CT_Group, eChatLoc.CL_ChatWindow);
                    else
                    {
                        if (ourEffect)
                            target.Group.SendMessageToGroupMembers(target, "Je n'intercepterai plus pour " + targetGroupMember.Name + ".", eChatType.CT_Group, eChatLoc.CL_ChatWindow);
                        else
                            target.Group.SendMessageToGroupMembers(targetGroupMember.Name + " est déjà intercepté.", eChatType.CT_Group, eChatLoc.CL_ChatWindow);
                    }
                }
                else
                    target.Whisper(player, "Je ne trouve pas " + args[1] + ".");
            }
        }
    }

    [CmdAttribute(
    "&mguard",
    ePrivLevel.Player,
    "/mguard [nom/classe] - Désigne une cible à protéger via la capacité Garde.")]
    public class MimicGuardCommandHandler : AbstractCommandHandler, ICommandHandler
    {
        public void OnCommand(GameClient client, string[] args)
        {
            GamePlayer player = client.Player;
            MimicNPC target = player.TargetObject as MimicNPC;

            if (target == null || player.Group == null || (player.Group != null && !player.Group.IsInTheGroup(target)))
                return;

            if (!target.HasAbility(Abilities.Guard))
            {
                target.Whisper(player, "Je n'ai pas la capacité Garde.");
                return;
            }

            GameLiving targetGroupMember = null;

            if (args.Length > 1)
            {
                args[1] = args[1].ToLower();

                eCharacterClass charClass = eCharacterClass.Unknown;
                Enum.TryParse<eCharacterClass>(args[1], true, out charClass);

                foreach (GameLiving groupMember in player.Group.GetMembersInTheGroup())
                {
                    if (groupMember != target &&
                        ((groupMember.Name.Equals(args[1], StringComparison.OrdinalIgnoreCase))
                        || (groupMember is MimicNPC mimic && mimic.CharacterClass.ID == (int)charClass)
                        || (groupMember is GamePlayer play && play.CharacterClass.ID == (int)charClass)))
                    {
                        targetGroupMember = groupMember;
                        break;
                    }
                }

                if (targetGroupMember != null)
                {
                    if (target.MimicBrain.SetGuard(targetGroupMember, out bool ourEffect))
                        target.Group.SendMessageToGroupMembers(target, "Je garde " + targetGroupMember.Name + ".", eChatType.CT_Group, eChatLoc.CL_ChatWindow);
                    else
                    {
                        if (ourEffect)
                            target.Group.SendMessageToGroupMembers(target, "Je ne garde plus " + targetGroupMember.Name + ".", eChatType.CT_Group, eChatLoc.CL_ChatWindow);
                        else
                            target.Group.SendMessageToGroupMembers(targetGroupMember.Name + " est déjà gardé.", eChatType.CT_Group, eChatLoc.CL_ChatWindow);
                    }
                }
                else
                    target.Whisper(player, "Je ne trouve pas " + args[1] + ".");
            }
        }
    }

    [CmdAttribute(
    "&mprotect",
    ePrivLevel.Player,
    "/mprotect [nom/classe] - Désigne une cible à protéger via la capacité Protection.")]
    public class MimicProtectCommandHandler : AbstractCommandHandler, ICommandHandler
    {
        public void OnCommand(GameClient client, string[] args)
        {
            GamePlayer player = client.Player;
            MimicNPC target = player.TargetObject as MimicNPC;

            if (target == null || player.Group == null || (player.Group != null && !player.Group.IsInTheGroup(target)))
                return;

            if (!target.HasAbility(Abilities.Protect))
            {
                target.Whisper(player, "Je n'ai pas la capacité Protection.");
                return;
            }

            GameLiving targetGroupMember = null;

            if (args.Length > 1)
            {
                eCharacterClass charClass = eCharacterClass.Unknown;
                Enum.TryParse<eCharacterClass>(args[1], true, out charClass);

                foreach (GameLiving groupMember in player.Group.GetMembersInTheGroup())
                {
                    if (groupMember != target &&
                        ((groupMember.Name.Equals(args[1], StringComparison.OrdinalIgnoreCase))
                        || (groupMember is MimicNPC mimic && mimic.CharacterClass.ID == (int)charClass)
                        || (groupMember is GamePlayer play && play.CharacterClass.ID == (int)charClass)))
                    {
                        targetGroupMember = groupMember;
                        break;
                    }
                }

                if (targetGroupMember != null)
                {
                    if (target.MimicBrain.SetProtect(targetGroupMember, out bool ourEffect))
                        target.Group.SendMessageToGroupMembers(target, "Je protège " + targetGroupMember.Name + ".", eChatType.CT_Group, eChatLoc.CL_ChatWindow);
                    else
                    {
                        if (ourEffect)
                            target.Group.SendMessageToGroupMembers(target, "Je ne protège plus " + targetGroupMember.Name + ".", eChatType.CT_Group, eChatLoc.CL_ChatWindow);
                        else
                            target.Group.SendMessageToGroupMembers(target, targetGroupMember.Name + " est déjà protégé.", eChatType.CT_Group, eChatLoc.CL_ChatWindow);
                    }
                }
                else
                    target.Whisper(player, "Je ne trouve pas " + args[1] + ".");
            }
        }
    }

    #endregion MimicGroup

    [CmdAttribute(
      "&mbstats",
      ePrivLevel.Player,
      "/mbstats [Battleground] - Affiche les stats d'un battleground.",
      "[Battleground] - Thid")]
    public class MimicBattleStatsCommandHandler : AbstractCommandHandler, ICommandHandler
    {
        public void OnCommand(GameClient client, string[] args)
        {
            if (args.Length > 1)
            {
                args[1] = args[1].ToLower();

                switch (args[1])
                {
                    case "thid": MimicBattlegrounds.ThidBattleground.BattlegroundStats(client.Player); break;
                }
            }
        }
    }

    [CmdAttribute(
      "&mstrategy",
      ePrivLevel.Player,
      "/mstrategy list - Liste les stratégies enregistrées et actives sur le mimic ciblé (ou sur tous les mimics groupés).",
      "/mstrategy add <clé> - Active une stratégie sur la cible ou le groupe.",
      "/mstrategy remove <clé> - Désactive une stratégie sur la cible ou le groupe.",
      "/mstrategy clear - Désactive toutes les stratégies sur la cible ou le groupe.")]
    public class MimicStrategyCommandHandler : AbstractCommandHandler, ICommandHandler
    {
        public void OnCommand(GameClient client, string[] args)
        {
            GamePlayer player = client.Player;

            if (player == null)
                return;

            if (args.Length < 2)
            {
                player.Out.SendMessage("Usage : /mstrategy list|add|remove|clear [clé]", eChatType.CT_System, eChatLoc.CL_SystemWindow);
                return;
            }

            string sub = args[1].ToLowerInvariant();
            List<MimicNPC> targets = ResolveTargets(player);

            if (targets.Count == 0)
            {
                player.Out.SendMessage("Aucun mimic ciblé ni dans votre groupe.", eChatType.CT_System, eChatLoc.CL_SystemWindow);
                return;
            }

            switch (sub)
            {
                case "list":
                {
                    System.Text.StringBuilder sb = new();
                    sb.AppendLine("Stratégies enregistrées : " + string.Join(", ", BotStrategyRegistry.ListKeys()));

                    foreach (MimicNPC m in targets)
                    {
                        var manager = m.MimicBrain?.StrategyManager;
                        var active = manager == null ? new List<string>() : manager.ActiveStrategies.ToList();
                        sb.AppendLine(m.Name + " : " + (active.Count == 0 ? "(aucune)" : string.Join(", ", active)));
                    }

                    player.Out.SendMessage(sb.ToString(), eChatType.CT_System, eChatLoc.CL_PopupWindow);
                    break;
                }

                case "add":
                {
                    if (args.Length < 3)
                    {
                        player.Out.SendMessage("Usage : /mstrategy add <clé>", eChatType.CT_System, eChatLoc.CL_SystemWindow);
                        return;
                    }

                    int ok = 0;

                    foreach (MimicNPC m in targets)
                    {
                        if (m.MimicBrain?.StrategyManager?.Enable(args[2]) == true)
                            ok++;
                    }

                    player.Out.SendMessage("Stratégie '" + args[2] + "' activée sur " + ok + "/" + targets.Count + " mimic(s).", eChatType.CT_System, eChatLoc.CL_SystemWindow);
                    break;
                }

                case "remove":
                {
                    if (args.Length < 3)
                    {
                        player.Out.SendMessage("Usage : /mstrategy remove <clé>", eChatType.CT_System, eChatLoc.CL_SystemWindow);
                        return;
                    }

                    int ok = 0;

                    foreach (MimicNPC m in targets)
                    {
                        if (m.MimicBrain?.StrategyManager?.Disable(args[2]) == true)
                            ok++;
                    }

                    player.Out.SendMessage("Stratégie '" + args[2] + "' désactivée sur " + ok + "/" + targets.Count + " mimic(s).", eChatType.CT_System, eChatLoc.CL_SystemWindow);
                    break;
                }

                case "clear":
                {
                    foreach (MimicNPC m in targets)
                        m.MimicBrain?.StrategyManager?.Clear();

                    player.Out.SendMessage("Stratégies effacées sur " + targets.Count + " mimic(s).", eChatType.CT_System, eChatLoc.CL_SystemWindow);
                    break;
                }

                default:
                    player.Out.SendMessage("Sous-commande inconnue. Utilisez list|add|remove|clear.", eChatType.CT_System, eChatLoc.CL_SystemWindow);
                    break;
            }
        }

        private static List<MimicNPC> ResolveTargets(GamePlayer player)
        {
            List<MimicNPC> list = new();

            if (player.TargetObject is MimicNPC targeted)
            {
                list.Add(targeted);
                return list;
            }

            if (player.Group != null)
            {
                foreach (GameLiving member in player.Group.GetMembersInTheGroup())
                {
                    if (member is MimicNPC mimic)
                        list.Add(mimic);
                }
            }

            return list;
        }
    }

    [CmdAttribute(
        "&mclear",
        ePrivLevel.Player,
        "/mclear - Supprime tous les mimics que vous possedez.")]
    public class MimicClearCommandHandler : AbstractCommandHandler, ICommandHandler
    {
        public void OnCommand(GameClient client, string[] args)
        {
            GamePlayer player = client.Player;

            if (player == null)
                return;

            int removed = MimicManager.ClearOwned(player);

            player.Out.SendMessage($"Mimics supprimes : {removed}.", eChatType.CT_System, eChatLoc.CL_SystemWindow);
        }
    }

    /// <summary>
    /// Quick-action menu for the mimic bot system. Every line is a clickable
    /// `[/cmd args]` shortcut — DAoC popup windows type the bracket contents
    /// into chat when clicked, so the command fires directly.
    ///
    /// The popup packet (0xAF) has a 2048-byte limit so the menu is split into
    /// a hub view + focused category sub-menus, each well under the limit.
    /// </summary>
    [CmdAttribute(
        "&mmenu",
        ePrivLevel.Player,
        "/mmenu - Ouvre le menu cliquable des bots.",
        "/mmenu <categorie> - Ouvre une categorie : create, orders, camp, roles, modes, strat, bg, info, admin.")]
    public class MimicMenuCommandHandler : AbstractCommandHandler, ICommandHandler
    {
        public void OnCommand(GameClient client, string[] args)
        {
            GamePlayer player = client?.Player;
            if (player == null) return;

            string category = args.Length >= 2 ? args[1].ToLowerInvariant() : null;
            bool isAdmin = client.Account.PrivLevel >= (uint)ePrivLevel.Admin;

            System.Text.StringBuilder sb = new();

            switch (category)
            {
                case "create":   BuildCreate(sb); break;
                case "orders":   BuildOrders(sb); break;
                case "camp":     BuildCamp(sb); break;
                case "roles":    BuildRoles(sb); break;
                case "modes":    BuildModes(sb); break;
                case "strat":    BuildStrat(sb); break;
                case "bg":       BuildBg(sb); break;
                case "info":     BuildInfo(sb); break;
                case "admin":
                    if (isAdmin) BuildAdmin(sb);
                    else { BuildHub(sb, isAdmin); }
                    break;
                default:
                    BuildHub(sb, isAdmin);
                    break;
            }

            // 1.127 client makes popup brackets clickable ONLY after a true
            // right-click interaction (server can't fake the interaction context
            // for a brand-new popup). The bullet-proof clickable path is the
            // mimic's own right-click menu, which now embeds all global commands.
            // If the player has a mimic, fire its Interact() — that puts the
            // mimic into interaction state with the client, and the resulting
            // popup is fully clickable. /mmenu <category> still uses the old
            // popup as a textual reference.
            if (args.Length < 2)
            {
                MimicNPC contextMimic = MimicPopupHelper.EnsureMimicTargeted(player);
                if (contextMimic != null)
                {
                    contextMimic.Interact(player);
                    return;
                }

                player.Out.SendMessage(
                    "Cree un mimic d'abord (/mcreate ou /mgroup), puis relance /mmenu : "
                    + "clic droit sur ton mimic ouvre le menu cliquable complet (roles, "
                    + "camp, BG, /mlfg, /mgroup, /mcamp, /mpvp...).",
                    eChatType.CT_System, eChatLoc.CL_SystemWindow);
                return;
            }

            // Category subviews still go out as a plain popup — non-clickable,
            // but the textual reference is useful for power users who already
            // know the syntax.
            player.Out.SendMessage(sb.ToString(), eChatType.CT_System, eChatLoc.CL_PopupWindow);
        }

        // ----- Hub -----
        private static void BuildHub(System.Text.StringBuilder sb, bool isAdmin)
        {
            sb.AppendLine("=== MENU BOTS — choisis une categorie ===");
            sb.AppendLine();
            sb.AppendLine("[mmenu create]    Creation (groupes, lfg, clear, spawner)");
            sb.AppendLine("[mmenu orders]    Ordres (summon, follow, attack, pull)");
            sb.AppendLine("[mmenu camp]      Camp & rayon d'aggro");
            sb.AppendLine("[mmenu roles]     Roles (tank, heal, cc, guard, protect...)");
            sb.AppendLine("[mmenu modes]     PvP / PreventCombat");
            sb.AppendLine("[mmenu strat]     Strategies");
            sb.AppendLine("[mmenu bg]        Battlegrounds (Thidranki)");
            sb.AppendLine("[mmenu info]      Aide & stats");
            if (isAdmin)
                sb.AppendLine("[mmenu admin]     Admin (PvP frontier)");
            sb.AppendLine();
            sb.AppendLine("[mhelp]           Aide textuelle detaillee");
            sb.AppendLine();
            sb.AppendLine("Astuce : clic droit sur un mimic = menu interaction (roles, equipement, etat).");
        }

        // ----- Categories -----
        private static void BuildCreate(System.Text.StringBuilder sb)
        {
            sb.AppendLine("=== MENU > CREATION ===");
            sb.AppendLine("[mmenu]                 Retour au menu");
            sb.AppendLine();
            sb.AppendLine("[mgroup alb]            Groupe Albion equilibre (8 mimics)");
            sb.AppendLine("[mgroup hib]            Groupe Hibernia");
            sb.AppendLine("[mgroup mid]            Groupe Midgard");
            sb.AppendLine("[mlfg]                  Liste cliquable des mimics LFG");
            sb.AppendLine("[mcreate <classe>]      Cree un mimic (ex: /mcreate armsman 50 inv)");
            sb.AppendLine("[mspawner <args>]       Spawn periodique de mimics");
            sb.AppendLine("[mclear]                Supprime TOUS tes mimics");
        }

        private static void BuildOrders(System.Text.StringBuilder sb)
        {
            sb.AppendLine("=== MENU > ORDRES ===");
            sb.AppendLine("[mmenu]                 Retour au menu");
            sb.AppendLine();
            sb.AppendLine("[msummon]               Teleporte tes mimics groupes a toi");
            sb.AppendLine("[mfollow]               Annule camp/pull, suit le leader");
            sb.AppendLine("[mattack]               Attaque ta cible avec tous les mimics");
            sb.AppendLine("[mpull]                 Camp ici + pull ta cible");
            sb.AppendLine("[mpullfrom here]        Definit le point de pull ici");
            sb.AppendLine("[mpullfrom remove]      Retire le point de pull");
        }

        private static void BuildCamp(System.Text.StringBuilder sb)
        {
            sb.AppendLine("=== MENU > CAMP ===");
            sb.AppendLine("[mmenu]                 Retour au menu");
            sb.AppendLine();
            sb.AppendLine("[mcamp set]             Camp a ton ground target (sinon ta position)");
            sb.AppendLine("[mcamp here]            Camp a ta position");
            sb.AppendLine("[mcamp remove]          Annule le camp");
            sb.AppendLine();
            sb.AppendLine("[mcamp aggrorange 550]  Rayon d'aggro (def 250 dungeon, 550 dehors)");
            sb.AppendLine("[mcamp aggrorange 1500] Aggro elargi");
            sb.AppendLine();
            sb.AppendLine("[mcamp filter blue]     Pull a partir de blue con");
            sb.AppendLine("[mcamp filter yellow]   Pull a partir de yellow");
            sb.AppendLine("[mcamp filter orange]   Pull a partir de orange");
        }

        private static void BuildRoles(System.Text.StringBuilder sb)
        {
            sb.AppendLine("=== MENU > ROLES (cible un mimic d'abord) ===");
            sb.AppendLine("[mmenu]                 Retour au menu");
            sb.AppendLine();
            sb.AppendLine("[mrole leader]          Designe leader");
            sb.AppendLine("[mrole tank]            Designe MainTank");
            sb.AppendLine("[mrole assist]          Designe MainAssist");
            sb.AppendLine("[mrole cc]              Designe MainCC");
            sb.AppendLine("[mrole puller]          Designe MainPuller");
            sb.AppendLine("[mheal]                 Bascule mode soigneur");
            sb.AppendLine();
            sb.AppendLine("[mguard <nom>]          Garde la cible");
            sb.AppendLine("[mprotect <nom>]        Protege la cible");
            sb.AppendLine("[mintercept <nom>]      Intercepte pour la cible");
        }

        private static void BuildModes(System.Text.StringBuilder sb)
        {
            sb.AppendLine("=== MENU > MODES ===");
            sb.AppendLine("[mmenu]                 Retour au menu");
            sb.AppendLine();
            sb.AppendLine("[mpvp true]             Active PvP (cible ou groupe)");
            sb.AppendLine("[mpvp false]            Desactive PvP");
            sb.AppendLine("[mpc true]              PreventCombat ON (le bot n'engagera plus)");
            sb.AppendLine("[mpc false]             PreventCombat OFF");
        }

        private static void BuildStrat(System.Text.StringBuilder sb)
        {
            sb.AppendLine("=== MENU > STRATEGIES ===");
            sb.AppendLine("[mmenu]                 Retour au menu");
            sb.AppendLine();
            sb.AppendLine("[mstrategy list]        Liste strategies actives");
            sb.AppendLine("[mstrategy add <cle>]   Ajoute une strategie");
            sb.AppendLine("[mstrategy remove <cle>] Retire une strategie");
        }

        private static void BuildBg(System.Text.StringBuilder sb)
        {
            sb.AppendLine("=== MENU > BATTLEGROUNDS ===");
            sb.AppendLine("[mmenu]                 Retour au menu");
            sb.AppendLine();
            sb.AppendLine("[mbattle thid start]    Demarre les spawns Thidranki");
            sb.AppendLine("[mbattle thid stop]     Arrete les spawns");
            sb.AppendLine("[mbattle thid clear]    Stop + supprime tous les bots");
            sb.AppendLine();
            sb.AppendLine("[mbstats]               Stats des battlegrounds");
        }

        private static void BuildInfo(System.Text.StringBuilder sb)
        {
            sb.AppendLine("=== MENU > INFO & AIDE ===");
            sb.AppendLine("[mmenu]                 Retour au menu");
            sb.AppendLine();
            sb.AppendLine("[mhelp]                 Aide detaillee (catalogue complet)");
            sb.AppendLine("[mhelp mgroup]          Detail d'une commande");
            sb.AppendLine("[mbstats]               Stats des battlegrounds");
        }

        private static void BuildAdmin(System.Text.StringBuilder sb)
        {
            sb.AppendLine("=== MENU > ADMIN (PvP frontier) ===");
            sb.AppendLine("[mmenu]                 Retour au menu");
            sb.AppendLine();
            sb.AppendLine("[pvpfrontier status]    Statut du systeme");
            sb.AppendLine("[pvpfrontier start]     Demarre le systeme");
            sb.AppendLine("[pvpfrontier stop]      Arrete le systeme");
            sb.AppendLine("[pvpfrontier clear]     Supprime tous les bots frontier");
        }
    }

    /// <summary>
    /// Master help command. Lists every Mimic / PvP-frontier command grouped
    /// by category, with usage and one-line description. `/mhelp <name>` shows
    /// a single command's full help.
    /// </summary>
    [CmdAttribute(
        "&mhelp",
        new[] { "&mimichelp" },
        ePrivLevel.Player,
        "/mhelp - Affiche l'aide complete sur toutes les commandes des bots.",
        "/mhelp <commande> - Affiche le detail d'une commande (ex: /mhelp mgroup).")]
    public class MimicHelpCommandHandler : AbstractCommandHandler, ICommandHandler
    {
        // Catalog of every Mimic command exposed to the player. Grouped so the
        // help output stays readable. Description is the short one-liner; full
        // syntax/details come from the command's [CmdAttribute] when queried
        // individually via `/mhelp <name>`.
        private sealed class Entry
        {
            public string Cmd;
            public string Category;
            public string Short;
            public string Usage;
            public Entry(string cmd, string cat, string s, string u)
            { Cmd = cmd; Category = cat; Short = s; Usage = u; }
        }

        private static readonly Entry[] _entries = new[]
        {
            // --- Creation ---
            new Entry("/mcreate",  "Creation",  "Cree un mimic d'une classe et niveau donnes.",
                "/mcreate <classe> [niveau] [spec] [inv]\nEx: /mcreate armsman 50 inv"),
            new Entry("/mgroup",   "Creation",  "Invoque un groupe equilibre (tank/heal/cc/dps).",
                "/mgroup <royaume> [taille=8] [niveau=ton lvl] [preventCombat=false]\nEx: /mgroup alb 8 50"),
            new Entry("/mclear",   "Creation",  "Supprime tous tes mimics.", "/mclear"),
            new Entry("/mlfg",     "Creation",  "Liste les mimics LFG ou en recrute un.",
                "/mlfg          (liste)\n/mlfg <index>  (recrute le numero affiche)"),
            new Entry("/mspawner", "Creation",  "Spawn periodique de mimics a une position.",
                "/mspawner <royaume> <lvlMin> <lvlMax> <maxAmount>"),

            // --- Orders ---
            new Entry("/msummon",  "Ordres",    "Teleporte tes mimics groupes a ta position.", "/msummon"),
            new Entry("/mfollow",  "Ordres",    "Retire camp/pull et fait suivre tous les mimics.", "/mfollow"),
            new Entry("/mattack",  "Ordres",    "Fait attaquer ta cible par tous les mimics.", "/mattack"),
            new Entry("/mpull",    "Ordres",    "Fixe camp+pull a ta position et pull ta cible.", "/mpull"),
            new Entry("/mpullfrom","Ordres",    "Definit le point depuis lequel puller.",
                "/mpullfrom here | set <x y z> | remove"),
            new Entry("/mcamp",    "Ordres",    "Definit camp / aggrorange / filtre con.",
                "/mcamp here | set | remove | aggrorange <n> | filter <con>"),

            // --- Roles ---
            new Entry("/mrole",    "Roles",     "Assigne un role a un mimic.",
                "/mrole leader | tank | assist | cc | puller"),
            new Entry("/mheal",    "Roles",     "Bascule le soigneur entre 'combat' et 'soin pur'.", "/mheal"),
            new Entry("/mguard",   "Roles",     "Designe une cible a garder (capacite Garde).",
                "/mguard [nom ou classe]"),
            new Entry("/mprotect", "Roles",     "Designe une cible a proteger (Protection).",
                "/mprotect [nom ou classe]"),
            new Entry("/mintercept","Roles",    "Designe une cible a intercepter.",
                "/mintercept [nom ou classe]"),

            // --- Modes ---
            new Entry("/mpvp",     "Modes",     "Active/desactive le mode PvP sur la cible ou le groupe.",
                "/mpvp true | false"),
            new Entry("/mpc",      "Modes",     "Active/desactive PreventCombat (le mimic n'engagera plus).",
                "/mpc true | false [group]"),

            // --- Strategies / debug ---
            new Entry("/mstrategy","Strategie", "Liste / ajoute / retire des strategies sur la cible.",
                "/mstrategy list | add <cle> | remove <cle>"),
            new Entry("/mbattle",  "Battleground","Controle un battleground (Thid/Caledonia/Molvik).",
                "/mbattle <Region> Start | Stop | Clear"),
            new Entry("/mbstats",  "Battleground","Affiche les stats d'un battleground.",
                "/mbstats <Battleground>"),

            // --- PvP Frontier ---
            new Entry("/pvpfrontier", "PvP Frontier (admin)",
                "Manage l'IA frontiere autonome (auto-start au boot).",
                "/pvpfrontier start | stop | status | clear"),

            // --- Aide ---
            new Entry("/mmenu", "Aide",
                "Ouvre un menu cliquable avec toutes les actions courantes.",
                "/mmenu"),
            new Entry("/mhelp", "Aide",
                "Affiche cette aide (catalogue complet).",
                "/mhelp [commande]"),
        };

        public void OnCommand(GameClient client, string[] args)
        {
            GamePlayer player = client.Player;
            if (player == null) return;

            if (args.Length >= 2)
            {
                ShowOne(player, args[1]);
                return;
            }

            ShowAll(player);
        }

        private void ShowAll(GamePlayer player)
        {
            System.Text.StringBuilder sb = new();
            sb.AppendLine("--- AIDE DES COMMANDES BOTS ---");
            sb.AppendLine("Tape /mhelp <commande> pour le detail (ex: /mhelp mgroup).");
            sb.AppendLine();

            foreach (var grp in _entries.GroupBy(e => e.Category))
            {
                sb.Append("[").Append(grp.Key).AppendLine("]");
                foreach (var e in grp)
                    sb.Append("  ").Append(e.Cmd.PadRight(14)).Append(" - ").AppendLine(e.Short);
                sb.AppendLine();
            }

            player.Out.SendMessage(sb.ToString(), eChatType.CT_System, eChatLoc.CL_PopupWindow);
        }

        private void ShowOne(GamePlayer player, string arg)
        {
            string q = "/" + arg.TrimStart('/').ToLowerInvariant();
            Entry hit = _entries.FirstOrDefault(e => string.Equals(e.Cmd, q, StringComparison.OrdinalIgnoreCase));
            if (hit == null)
            {
                player.Out.SendMessage(
                    $"Commande inconnue : {arg}. Tape /mhelp pour la liste.",
                    eChatType.CT_System, eChatLoc.CL_SystemWindow);
                return;
            }

            string msg =
                $"=== {hit.Cmd} ({hit.Category}) ===\n" +
                hit.Short + "\n\n" +
                "Utilisation :\n" +
                hit.Usage;
            player.Out.SendMessage(msg, eChatType.CT_System, eChatLoc.CL_PopupWindow);
        }
    }
}
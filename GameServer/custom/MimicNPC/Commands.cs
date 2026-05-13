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
       "/mgroup royaume taille niveau preventCombat - Invoque un groupe de mimics d'un royaume donné.")]
    public class MimicSummonMimicGroupCommandHandler : AbstractCommandHandler, ICommandHandler
    {
        public void OnCommand(GameClient client, string[] args)
        {
            if (args.Length >= 2)
            {
                args[1] = args[1].ToLower();

                byte groupSize = 8;
                if (args.Length >= 3)
                {
                    if (!byte.TryParse(args[2], out groupSize) || groupSize < 1 || groupSize > 8)
                        groupSize = 8;
                }

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

                Point3D position = new Point3D(client.Player.X, client.Player.Y, client.Player.Z);

                if (client.Player.GroundTarget != null)
                {
                    Point2D playerPos = new Point2D(client.Player.X, client.Player.Y);

                    if (client.Player.GroundTarget.GetDistance(playerPos) < 5000)
                        position = new Point3D(client.Player.GroundTarget);
                }

                if (position != null)
                {
                    List<GameLiving> groupMembers = new List<GameLiving>();
                    MimicNPC mimic;

                    switch (args[1])
                    {
                        case "alb":
                        case "albion":
                        {
                            for (int i = 0; i < groupSize; i++)
                            {
                                int randomX = Util.Random(-100, 100);
                                int randomY = Util.Random(-100, 100);

                                position.X += randomX;
                                position.Y += randomY;

                                mimic = MimicManager.GetMimic(MimicManager.GetRandomMimicClass(eRealm.Albion), level, preventCombat: preventCombat);
                                MimicManager.AddMimicToWorld(mimic, position, client.Player.CurrentRegionID);

                                if (mimic != null)
                                    groupMembers.Add(mimic);
                            }

                            break;
                        }

                        case "hib":
                        case "hibernia":
                        {
                            for (int i = 0; i < groupSize; i++)
                            {
                                int randomX = Util.Random(-100, 100);
                                int randomY = Util.Random(-100, 100);

                                position.X += randomX;
                                position.Y += randomY;

                                mimic = MimicManager.GetMimic(MimicManager.GetRandomMimicClass(eRealm.Hibernia), level, preventCombat: preventCombat);
                                MimicManager.AddMimicToWorld(mimic, position, client.Player.CurrentRegionID);

                                if (mimic != null)
                                    groupMembers.Add(mimic);
                            }

                            break;
                        }

                        case "mid":
                        case "midgard":
                        {
                            for (int i = 0; i < groupSize; i++)
                            {
                                int randomX = Util.Random(-100, 100);
                                int randomY = Util.Random(-100, 100);

                                position.X += randomX;
                                position.Y += randomY;

                                mimic = MimicManager.GetMimic(MimicManager.GetRandomMimicClass(eRealm.Midgard), level, preventCombat: preventCombat);
                                MimicManager.AddMimicToWorld(mimic, position, client.Player.CurrentRegionID);

                                if (mimic != null)
                                    groupMembers.Add(mimic);
                            }

                            break;
                        }

                        default: break;
                    }

                    if (groupMembers.Count > 0)
                    {
                        if (groupMembers[0].Group == null)
                        {
                            groupMembers[0].Group = new Group(groupMembers[0]);
                            groupMembers[0].Group.AddMember(groupMembers[0]);
                        }

                        foreach (GameLiving living in groupMembers)
                        {
                            if (living.Group == null)
                            {
                                groupMembers[0].Group.AddMember(living);

                                MimicBrain brain = ((MimicNPC)living).Brain as MimicBrain;
                                brain.FSM.SetCurrentState(eFSMStateType.WAKING_UP);
                            }
                        }
                    }
                }
            }
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
            if (client.Player.TargetObject is MimicNPC mimic)
            {
                if (mimic.Group == null)
                    mimic.Whisper(client.Player, "Je dois être dans un groupe.");
                else
                    mimic.Group.MimicGroup.SetHealer(mimic);
            }
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

            if (args.Length < 2)
            {
                message = BuildMessage(entries);
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
                    message = BuildMessage(entries, true);
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

                            player.Group.AddMember(mimic);

                            MimicLFGManager.Remove(player.Realm, entry);

                            // Send a refreshed list with new indexes to avoid using wrong indexes while leaving the dialogue open
                            entries = MimicLFGManager.GetLFG(player.Realm, player.Level);

                            message = BuildMessage(entries);
                        }
                        else
                            message = BuildMessage(entries, true);
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

            player.Out.SendMessage(message, eChatType.CT_System, eChatLoc.CL_PopupWindow);
        }

        private string BuildMessage(IReadOnlyList<MimicLFGManager.MimicLFGEntry> entries, bool invalid = false)
        {
            string message = "--------------------------------\n";

            if (invalid)
                message += "Index invalide ou groupe complet.\n";
            else if (entries.Any())
            {
                int index = 1;
                foreach (var entry in entries)
                    message += index++.ToString() + ". " + entry.Name + " " + Enum.GetName(typeof(eMimicClass), entry.MimicClass) + " " + entry.Level + "\n";
            }
            else
                message += "Aucun mimic disponible.\n";

            return message;
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
                            target.Group.SendMessageToGroupMembers("Je ne protège plus " + targetGroupMember.Name + ".", eChatType.CT_Group, eChatLoc.CL_ChatWindow);
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
}
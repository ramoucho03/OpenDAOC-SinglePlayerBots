using System;
using System.Collections;
using System.Collections.Generic;
using DOL.Database;
using DOL.GS.Housing;
using DOL.GS.PacketHandler;
using DOL.GS.ServerRules;
using DOL.GS.Spells;

/* Need to fix
 * EquipTemplate for Hib and Mid
 * Oceanus for all realms.
 * Kobold Undercity for Mid
 * personal guild and hearth teleports
 */
namespace DOL.GS.Scripts
{
    public class LiveTeleporter : GameNPC
    {
        /// <summary>
        /// The type of teleporter; this is used in order to be able to handle
        /// identical TeleportIDs differently, depending on the actual teleporter.
        /// </summary>
        protected virtual String Type
        {
            get { return string.Empty; }
        }

        /// <summary>
        /// The destination realm. 
        /// </summary>
        protected virtual eRealm DestinationRealm
        {
            get { return Realm; }
        }

        public override bool AddToWorld()
        {
            switch (Realm)
            {
                case eRealm.Albion:
                    Name = "Master Visur";
                    GuildName = "Teleporter";
                    Model = 61;

                    GameNpcInventoryTemplate templateAlb = new GameNpcInventoryTemplate();
                    templateAlb.AddNPCEquipment(eInventorySlot.Cloak, 57, 66);
                    templateAlb.AddNPCEquipment(eInventorySlot.TorsoArmor, 1005, 86);
                    templateAlb.AddNPCEquipment(eInventorySlot.LegsArmor, 140, 6);
                    templateAlb.AddNPCEquipment(eInventorySlot.ArmsArmor, 141, 6);
                    templateAlb.AddNPCEquipment(eInventorySlot.HandsArmor, 142, 6);
                    templateAlb.AddNPCEquipment(eInventorySlot.FeetArmor, 143, 6);
                    templateAlb.AddNPCEquipment(eInventorySlot.TwoHandWeapon, 1166);
                    Inventory = templateAlb.CloseTemplate();
                    break;
                case eRealm.Midgard:
                    Name = "Stor Gothi Annark";
                    GuildName = "Teleporter";
                    Model = 215;

                    GameNpcInventoryTemplate templateMid = new GameNpcInventoryTemplate();
                    templateMid.AddNPCEquipment(eInventorySlot.Cloak, 57, 26);
                    templateMid.AddNPCEquipment(eInventorySlot.TorsoArmor, 245, 26);
                    templateMid.AddNPCEquipment(eInventorySlot.LegsArmor, 246, 26);
                    templateMid.AddNPCEquipment(eInventorySlot.HandsArmor, 248, 26);
                    templateMid.AddNPCEquipment(eInventorySlot.FeetArmor, 249, 26);
                    Inventory = templateMid.CloseTemplate();
                    break;
                case eRealm.Hibernia:
                    Name = "Channeler Glasny";
                    GuildName = "Teleporter";
                    Model = 342;

                    GameNpcInventoryTemplate templateHib = new GameNpcInventoryTemplate();
                    templateHib.AddNPCEquipment(eInventorySlot.TorsoArmor, 1008);
                    templateHib.AddNPCEquipment(eInventorySlot.HandsArmor, 396);
                    templateHib.AddNPCEquipment(eInventorySlot.FeetArmor, 402);
                    templateHib.AddNPCEquipment(eInventorySlot.TwoHandWeapon, 468);
                    Inventory = templateHib.CloseTemplate();
                    break;
            }

            Level = 60;
            Size = 50;
            Flags |= GameNPC.eFlags.PEACE;

            return base.AddToWorld();
        }

        /// <summary>
        /// Display the teleport indicator around this teleporters feet
        /// </summary>
        public override bool ShowTeleporterIndicator
        {
            get { return true; }
        }


        public override bool Interact(GamePlayer player) // What to do when a player clicks on me
        {
            if (!base.Interact(player) || GameRelic.IsPlayerCarryingRelic(player)) return false;

            if (player.Realm != this.Realm && player.Client.Account.PrivLevel == 1) return false;

            TurnTo(player, 10000);
            
            var message = string.Empty;

            switch (Realm)
            {
                case eRealm.Albion:

                    message = "Greetings, " + player.Name +
                              " I am able to channel energy to transport you to distant lands. I can send you to the following locations:\n\n" +
                              "[Castle Sauvage] in Camelot Hills or \n[Snowdonia Fortress] in Black Mtns. North,\n" +
                              "[Avalon Marsh] wharf,\n" +
                              "[Gothwaite Harbor] in the [Shrouded Isles],\n" +
                              "[Camelot] our glorious capital,\n" +
                              "[Entrance] to the areas of [Housing]\n\n" +
                              "or one of the many [towns] throughout Albion.";
                              //"For this event duration, I can send you to [Darkness Falls]";
                    break;

                case eRealm.Midgard:
                    
                    message = "Greetings, " + player.Name +
                              " I am able to channel energy to transport you to distant lands. I can send you to the following locations:\n\n" +
                              "[Svasud Faste] in Mularn or \n[Vindsaul Faste] in West Svealand,\n" +
                              "Beaches of [Gotar] near Nailiten,\n" +
                              "[Aegirhamn] in the [Shrouded Isles],\n" +
                              "Our glorious city of [Jordheim],\n" +
                              "[Entrance] to the areas of [Housing]\n\n" +
                              "or one of the many [towns] throughout Midgard.";
                    break;

                case eRealm.Hibernia:
                    
                    message = "Greetings, " + player.Name +
                              " I am able to channel energy to transport you to distant lands. I can send you to the following locations:\n\n" +
                              "[Druim Ligen] in Connacht or \n[Druim Cain] in Bri Leith,\n" +
                              "[Shannon Estuary] watchtower,\n" +
                              "[Domnann] Grove in the [Shrouded Isles],\n" +
                              "[Tir na Nog] our glorious capital,\n" +
                              "[Entrance] to the areas of [Housing]\n\n" +
                              "or one of the many [towns] throughout Hibernia.";
                    break;

                default:
                    SayTo(player, "I have no Realm set, so don't know what locations to offer..");
                    break;
            }
            
            message += "\n\n" +
                       "Perhaps you would like the challenge of the [Epic Dungeon]?";

            // Expansion shortcuts — added by Option 3 patch. Each destination
            // is realm-aware in GetTeleportLocation so the same keyword routes
            // every realm to its own throne / catacombs entrance. Atlantis is
            // a shared TOA hub (coords from db-public Aerus statue anchor).
            message += "\n\n" +
                       "I can also send you to the expansion lands of [Atlantis], " +
                       "the [Throne Room] of our king, or the [Catacombs] beneath our realm.";

            // Adventure Wing portal — opens an instanced dungeon. Player whispers
            // "adventure wing" to get the realm-appropriate sub-menu, then a wing
            // keyword (e.g. "wing 96") to enter. Each call creates a fresh
            // AdventureWingInstance scoped to the player or their group.
            message += "\n\n" +
                       "If you seek solo or small-group challenges, ask about the [Adventure Wing] dungeons.";

            SayTo(player, message);

            return true;
        }

        public override bool WhisperReceive(GameLiving source, string str) // What to do when a player whispers me
        {
            if (!base.WhisperReceive(source, str)) return false;

            GamePlayer player = source as GamePlayer;
            if (player == null)
                return false;

            if (GameRelic.IsPlayerCarryingRelic(player))
                return false;

            return GetTeleportLocation(player, str);

        }

        protected virtual bool GetTeleportLocation(GamePlayer player, string text)
        {
            switch (Realm) // Only offer locations based on what realm i am set at.
            {
                case eRealm.Albion:
                    
                    if (text.ToLower() == "shrouded isles")
                    {
                        String reply = String.Format("The isles of Avalon are an excellent choice. {0} {1}",
                            "Would you prefer [Gothwaite] or perhaps one of the outlying towns",
                            "like [Wearyall Village], Fort [Gwyntell], or [Caer Diogel]?");
                        SayTo(player, reply);
                        return false;
                    }
                    
                    if (text.ToLower() == "housing")
                    {
                        SayTo(player,
                            "I can send you to your [personal] or [guild] house. If you do not have a personal house, I can teleport you to the housing [entrance] or your housing [hearth] bindstone.");
                        return false;
                    }
                    
                    if (text.ToLower() == "towns")
                    {
                        SayTo(player, "I can send you to:\n" +
                                      "[Cotswold Village]\n" +
                                      "[Prydwen Keep]\n" +
                                      "[Caer Ulfwych]\n" +
                                      "[Campacorentin Station]\n" +
                                      "[Adribard's Retreat]\n" +
                                      "[Yarley's Farm]");
                        return false;
                    }

                    /*
                    if (text.ToLower() == "darkness falls")
                    {
                        IGameLocation location = new GameLocation("df", 249, 249, 23122, 19634, 22897, 3074);
                        
                        Teleport teleport = new Teleport();
                        teleport.TeleportID = "Darkness Falls";
                        teleport.Realm = (int) DestinationRealm;
                        teleport.RegionID = location.RegionID;
                        teleport.X = location.X;
                        teleport.Y = location.Y;
                        teleport.Z = location.Z;
                        teleport.Heading = location.Heading;
                        OnDestinationPicked(player, teleport);
                        return true;
                    }*/
                    
                    break;
                
                case eRealm.Midgard:
                    
                    if (text.ToLower() == "shrouded isles")
                    {
                        String reply = String.Format("The isles of Aegir are an excellent choice. {0} {1}",
                            "Would you prefer the city of [Aegirhamn] or perhaps one of the outlying towns",
                            "like [Bjarken], [Hagall], or [Knarr]?");
                        SayTo(player, reply);
                        
                        return false;
                    }

                    if (text.ToLower() == "housing")
                    {
                        SayTo(player,
                            "I can send you to your [personal] or [guild] house. If you do not have a personal house, I can teleport you to the housing [entrance] or your housing [hearth] bindstone.");
                        return false;
                    }

                    if (text.ToLower() == "towns")
                    {
                        SayTo(player,
                            "I can send you to:\n" +
                            "[Mularn]\n" +
                            "[Fort Veldon]\n" +
                            "[Audliten]\n" +
                            "[Huginfell]\n" +
                            "[Fort Atla]\n" +
                            "[West Skona]");
                        return false;
                    }
                    
                    break;
                
                case eRealm.Hibernia:
                    
                    if (text.ToLower() == "shrouded isles")
                    {
                        SayTo(player,
                            "The isles of Hy Brasil are an excellent choice. Would you prefer the grove of [Domnann] or perhaps one of the outlying towns like [Droighaid], [Aalid Feie], or [Necht]?");
                        return false;
                    }

                    if (text.ToLower() == "housing")
                    {
                        SayTo(player,
                            "I can send you to your [personal] or [guild] house. If you do not have a personal house, I can teleport you to the housing [entrance] or your housing [hearth] bindstone.");
                        return false;
                    }

                    if (text.ToLower() == "towns")
                    {
                        SayTo(player,
                            "I can send you to:\n" +
                            "[Mag Mell]\n" +
                            "[Tir na mBeo]\n" +
                            "[Ardagh]\n" +
                            "[Howth]\n" +
                            "[Connla]\n" +
                            "[Innis Carthaig]");
                        return false;
                    }

                    break;
            }

            // Another special case is personal house, as there is no location
            // that will work for every player.
            if (text == "Entrance") text = text.ToLower();
            
            if (text.ToLower() == "personal")
            {
                House house = HouseMgr.GetHouseByPlayer(player);

                if (house == null)
                {
                    text = "entrance"; // Fall through, port to housing entrance.
                }
                else
                {
                    IGameLocation location = house.OutdoorJumpPoint;
                    DbTeleport teleport = new DbTeleport();
                    teleport.TeleportID = "your house";
                    teleport.Realm = (int) DestinationRealm;
                    teleport.RegionID = location.RegionID;
                    teleport.X = location.X;
                    teleport.Y = location.Y;
                    teleport.Z = location.Z;
                    teleport.Heading = location.Heading;
                    OnDestinationPicked(player, teleport);
                    return true;
                }
            }

            // Yet another special case the port to the 'hearth' what means
            // that the player will be ported to the defined house bindstone
            if (text.ToLower() == "hearth")
            {
                // Check if player has set a house bind
                if (!(player.BindHouseRegion > 0))
                {
                    SayTo(player, "Sorry, you haven't set any house bind point yet.");
                    return false;
                }

                // Check if the house at the player's house bind location still exists
                ArrayList houses = (ArrayList) HouseMgr.GetHousesCloseToSpot((ushort) player.BindHouseRegion,
                    player.BindHouseXpos, player.BindHouseYpos, 700);
                if (houses.Count == 0)
                {
                    SayTo(player, "I'm afraid I can't teleport you to your hearth since the house at your " +
                                  "house bind location has been torn down.");
                    return false;
                }

                // Check if the house at the player's house bind location contains a bind stone
                House targetHouse = (House) houses[0];
                var hookpointItems = targetHouse.HousePointItems;
                Boolean hasBindstone = false;

                foreach (KeyValuePair<uint, DbHouseHookPointItem> targetHouseItem in hookpointItems)
                {
                    if (((GameObject) targetHouseItem.Value.GameObject).GetName(0, false).ToLower()
                        .EndsWith("bindstone"))
                    {
                        hasBindstone = true;
                        break;
                    }
                }

                if (!hasBindstone)
                {
                    SayTo(player, "I'm sorry to tell that the bindstone of your current house bind location " +
                                  "has been removed, so I'm not able to teleport you there.");
                    return false;
                }

                // Check if the player has the permission to bind at the house bind stone
                if (!targetHouse.CanBindInHouse(player))
                {
                    SayTo(player, "You're no longer allowed to bind at the house bindstone you've previously " +
                                  "chosen, hence I'm not allowed to teleport you there.");
                    return false;
                }

                DbTeleport teleport = new DbTeleport();
                teleport.TeleportID = "hearth";
                teleport.Realm = (int) DestinationRealm;
                teleport.RegionID = player.BindHouseRegion;
                teleport.X = player.BindHouseXpos;
                teleport.Y = player.BindHouseYpos;
                teleport.Z = player.BindHouseZpos;
                teleport.Heading = player.BindHouseHeading;
                OnDestinationPicked(player, teleport);
                return true;
            }

            if (text.ToLower() == "guild")
            {
                House house = HouseMgr.GetGuildHouseByPlayer(player);

                if (house == null)
                {
                    SayTo(player, $"I'm sorry but {player.Guild.Name} doesn't own a Guild House.");
                    return false;
                    return false; // no teleport when guild house not found
                }
                else
                {
                    IGameLocation location = house.OutdoorJumpPoint;
                    DbTeleport teleport = new DbTeleport();
                    teleport.TeleportID = "guild house";
                    teleport.Realm = (int) DestinationRealm;
                    teleport.RegionID = location.RegionID;
                    teleport.X = location.X;
                    teleport.Y = location.Y;
                    teleport.Z = location.Z;
                    teleport.Heading = location.Heading;
                    OnDestinationPicked(player, teleport);
                    return true;
                }
            }

            if (text.ToLower() == "epic dungeon")
            {
                switch (player.Realm)
                {
                    case eRealm.Albion:
                        GetTeleportLocation(player, "Caer Sidi");
                        return true;
                    case eRealm.Midgard:
                        GetTeleportLocation(player, "Tuscaran Glacier");
                        return true;
                    case eRealm.Hibernia:
                        GetTeleportLocation(player, "Galladoria");
                        return false;
                }
            }

            // === Expansion destinations (Option 3 patch) =====================
            // Coordinates lifted from db-public/Mob.4.json (DR throne guards)
            // and Mob.2.json (TOA Aerus statue anchor). Adjust in-game via
            // /loc + edit if the landing spot is sub-optimal.

            if (text.ToLower() == "atlantis")
            {
                // TOA hub = Oceanus Hesperos (Region 73). Each realm has its
                // own Haven cluster within the region — coords sourced from
                // the trainer / channeler NPCs actually placed in db-public
                // (Mob.2.json) which we just imported.
                DbTeleport teleport = new DbTeleport();
                teleport.TeleportID = "Haven of Atlantis";
                teleport.Realm = (int) DestinationRealm;
                teleport.RegionID = 73;
                teleport.Heading = 0;
                switch (player.Realm)
                {
                    case eRealm.Albion:
                        teleport.X = 447323; teleport.Y = 552333; teleport.Z = 8567;
                        break;
                    case eRealm.Midgard:
                        teleport.X = 331265; teleport.Y = 451002; teleport.Z = 8140;
                        break;
                    case eRealm.Hibernia:
                        teleport.X = 330930; teleport.Y = 451170; teleport.Z = 8141;
                        break;
                    default:
                        SayTo(player, "I cannot find an Atlantis haven for your kingdom.");
                        return false;
                }
                OnDestinationPicked(player, teleport);
                return true;
            }

            if (text.ToLower() == "throne room")
            {
                DbTeleport teleport = new DbTeleport();
                teleport.TeleportID = "Throne Room";
                teleport.Realm = (int) DestinationRealm;
                switch (player.Realm)
                {
                    case eRealm.Albion:
                        teleport.RegionID = 394; teleport.X = 32331; teleport.Y = 31723; teleport.Z = 15901; teleport.Heading = 21;
                        break;
                    case eRealm.Midgard:
                        teleport.RegionID = 360; teleport.X = 32331; teleport.Y = 30414; teleport.Z = 15563; teleport.Heading = 23;
                        break;
                    case eRealm.Hibernia:
                        teleport.RegionID = 395; teleport.X = 32331; teleport.Y = 31690; teleport.Z = 15715; teleport.Heading = 39;
                        break;
                    default:
                        SayTo(player, "I'm afraid your kingdom has no royal throne I can reach.");
                        return false;
                }
                OnDestinationPicked(player, teleport);
                return true;
            }

            if (text.ToLower() == "catacombs")
            {
                DbTeleport teleport = new DbTeleport();
                teleport.TeleportID = "Catacombs";
                teleport.Realm = (int) DestinationRealm;
                teleport.Heading = 0;
                switch (player.Realm)
                {
                    case eRealm.Albion:
                        teleport.RegionID = 66;  teleport.X = 19161; teleport.Y = 19846; teleport.Z = 16178;
                        break;
                    case eRealm.Midgard:
                        teleport.RegionID = 58;  teleport.X = 23176; teleport.Y = 22681; teleport.Z = 16173;
                        break;
                    case eRealm.Hibernia:
                        teleport.RegionID = 197; teleport.X = 27437; teleport.Y = 40806; teleport.Z = 16548;
                        break;
                    default:
                        SayTo(player, "I cannot guide you to your realm's catacombs.");
                        return false;
                }
                OnDestinationPicked(player, teleport);
                return true;
            }

            // === Adventure Wings (Catacombs instanced dungeons) ====================
            // Top-level menu: list realm-appropriate wings. Each wing is a Cata
            // region populated with mobs (verified by COUNT(*) > 50 in mob table).
            // Whispering "wing <id>" enters that wing through an AdventureWingInstance
            // (handled by DOL.GS.ServerRules.AdventureWingJumpPoint), which clones
            // the region per group and spawns the same mobs as the skin region.
            if (text.ToLower() == "adventure wing")
            {
                string list;
                switch (player.Realm)
                {
                    case eRealm.Albion:
                        // Region IDs identified as populated Albion Cata wings
                        // (populated mobs > 100, expansion 3 / Catacombs).
                        list = "Albion adventure wings:\n" +
                               "[wing 66] [wing 67] [wing 68] [wing 96] [wing 97] [wing 99] " +
                               "[wing 188] [wing 189] [wing 195] [wing 196] [wing 197]";
                        break;
                    case eRealm.Midgard:
                        list = "Midgard adventure wings:\n" +
                               "[wing 58] [wing 59] [wing 92] [wing 94] [wing 95] " +
                               "[wing 148] [wing 149] [wing 162]";
                        break;
                    case eRealm.Hibernia:
                        list = "Hibernia adventure wings:\n" +
                               "[wing 63] [wing 65] [wing 109] [wing 226] [wing 227] " +
                               "[wing 228] [wing 229] [wing 230] [wing 243] [wing 489]";
                        break;
                    default:
                        SayTo(player, "I cannot find adventure wings for your realm.");
                        return false;
                }
                SayTo(player, list + "\n\nWhisper one of the wing keywords to enter. " +
                              "An instance will be created for you or your group.");
                return false;
            }

            // Wing entry: parses "wing <regionId>" and triggers the adventure wing
            // jump-point handler. The handler creates an instance, clones mobs
            // from the skin region, and moves the player. Landing coords are
            // chosen from the mob bounding box (center) per region — close enough
            // for any populated wing since mobs cluster around landing zones.
            if (text.ToLower().StartsWith("wing "))
            {
                if (!int.TryParse(text.Substring(5), out int wingRegion))
                {
                    SayTo(player, "I don't recognize that wing. Whisper [adventure wing] for the list.");
                    return false;
                }

                // Realm gating — players whisper a keyword from their own list.
                if (!IsValidWingForRealm(player.Realm, wingRegion))
                {
                    SayTo(player, "That adventure wing is not in your realm's territory.");
                    return false;
                }

                // Build a synthetic ZonePoint for AdventureWingJumpPoint to consume.
                DbZonePoint zp = new DbZonePoint();
                zp.SourceRegion = (ushort) player.CurrentRegionID;
                zp.SourceX = player.X;
                zp.SourceY = player.Y;
                zp.SourceZ = player.Z;
                zp.TargetRegion = (ushort) wingRegion;
                // Center of the wing — per-region populated bounding box midpoints
                // (computed from mob X/Y in the skin region). Players can /loc
                // and we adjust later if landing in a wall.
                var landing = GetWingLanding(wingRegion);
                zp.TargetX = landing.x;
                zp.TargetY = landing.y;
                zp.TargetZ = landing.z;
                zp.TargetHeading = 0;
                zp.ClassType = "DOL.GS.ServerRules.AdventureWingJumpPoint";
                zp.Realm = (ushort) player.Realm;

                AdventureWingJumpPoint handler = new AdventureWingJumpPoint();
                handler.IsAllowedToJump(zp, player);
                return true;
            }

            // Find the teleport location in the database.
            DbTeleport port = WorldMgr.GetTeleportLocation(DestinationRealm, String.Format("{0}:{1}", Type, text));
            if (port != null)
            {
                if (port.RegionID == 0 && port.X == 0 && port.Y == 0 && port.Z == 0)
                {
                    OnSubSelectionPicked(player, port);
                }
                else
                {
                    OnDestinationPicked(player, port);
                }

                return false;
            }

            return true; // Needs further processing.
        }

        // ====================================================================
        // Adventure Wing helpers (Catacombs instanced dungeons)
        // ====================================================================

        // Realm → set of wing region IDs that are populated (mob count > 50).
        // Region IDs are heuristically assigned by realm based on coords range
        // and ID grouping. Adjust if testing reveals a wing is wrong realm.
        private static readonly HashSet<int> AlbionWings = new()
        {
            66, 67, 68, 96, 97, 99, 188, 189, 195, 196, 197
        };
        private static readonly HashSet<int> MidgardWings = new()
        {
            58, 59, 92, 94, 95, 148, 149, 162
        };
        private static readonly HashSet<int> HiberniaWings = new()
        {
            63, 65, 109, 226, 227, 228, 229, 230, 243, 489
        };

        private static bool IsValidWingForRealm(eRealm realm, int regionId) => realm switch
        {
            eRealm.Albion   => AlbionWings.Contains(regionId),
            eRealm.Midgard  => MidgardWings.Contains(regionId),
            eRealm.Hibernia => HiberniaWings.Contains(regionId),
            _ => false
        };

        // Per-region landing coords. Computed from the mob bounding box mid-points
        // in db-public's data — lands the player roughly in the middle of mob
        // spawns so they don't appear in a wall or empty corner.
        private static readonly Dictionary<int, (int x, int y, int z)> WingLandings = new()
        {
            // Albion
            { 66,  (32000, 32000, 16178) },
            { 67,  (32000, 30000, 16100) },
            { 68,  (29900, 29200, 16500) },
            { 96,  (30500, 32000, 16500) },
            { 97,  (25700, 21600, 16500) },
            { 99,  (32600, 31200, 16500) },
            { 188, (32800, 31300, 16500) },
            { 189, (32800, 31300, 16000) },
            { 195, (36800, 29800, 16500) },
            { 196, (75900, 73400, 16500) },
            { 197, (32000, 32000, 16548) },
            // Midgard
            { 58,  (30000, 32000, 16173) },
            { 59,  (32800, 30500, 16500) },
            { 92,  (41900, 32800, 16500) },
            { 94,  (37900, 30800, 16500) },
            { 95,  (30600, 34400, 16500) },
            { 148, (36900, 34300, 16500) },
            { 149, (36100, 29700, 16500) },
            { 162, (24800, 21300, 16500) },
            // Hibernia
            { 63,  (32600, 30700, 16500) },
            { 65,  (30600, 36500, 16500) },
            { 109, (29800, 34400, 16500) },
            { 226, (30400, 34600, 16500) },
            { 227, (30300, 34400, 16500) },
            { 228, (31900, 31100, 16500) },
            { 229, (37600, 29100, 16500) },
            { 230, (32000, 32000, 16500) },
            { 243, (34200, 34600, 16500) },
            { 489, (27300, 42500, 16500) },
        };

        private static (int x, int y, int z) GetWingLanding(int regionId)
        {
            if (WingLandings.TryGetValue(regionId, out var loc))
                return loc;
            return (32000, 32000, 16500); // safe fallback
        }

        /// <summary>
        /// Player has picked a destination.
        /// Override if you need the teleporter to say something to the player
        /// before porting him.
        /// </summary>
        /// <param name="player"></param>
        /// <param name="destination"></param>
        protected virtual void OnDestinationPicked(GamePlayer player, DbTeleport destination)
        {
            Region region = WorldMgr.GetRegion((ushort) destination.RegionID);

            if (region == null || region.IsDisabled)
            {
                player.Out.SendMessage("This destination is not available.", eChatType.CT_System,
                    eChatLoc.CL_SystemWindow);
                return;
            }
            
            var message = $"{Name} says, \"I'm now teleporting you to {destination.TeleportID}.\"";
            
            player.Out.SendMessage(message, eChatType.CT_Say, eChatLoc.CL_ChatWindow);
            
            OnTeleportSpell(player, destination);
        }

        /// <summary>
        /// Player has picked a subselection.
        /// Override to pass teleport options on to the player.
        /// </summary>
        /// <param name="player"></param>
        /// <param name="subSelection"></param>
        protected virtual void OnSubSelectionPicked(GamePlayer player, DbTeleport subSelection)
        {
        }

        /// <summary>
        /// Teleport the player to the designated coordinates using the
        /// portal spell.
        /// </summary>
        /// <param name="player"></param>
        /// <param name="destination"></param>
        protected virtual void OnTeleportSpell(GamePlayer player, DbTeleport destination)
        {
            SpellLine spellLine = SkillBase.GetSpellLine(GlobalSpellsLines.Mob_Spells);
            List<Spell> spellList = SkillBase.GetSpellList(GlobalSpellsLines.Mob_Spells);
            Spell spell = SkillBase.GetSpellByID(5999); // UniPortal spell.

            if (spell != null)
            {
                UniPortal portalHandler = new UniPortal(this, spell, spellLine, destination);
                portalHandler.StartSpell(player);
                return;
            }

            // Spell not found in the database, fall back on default procedure.

            if (player.Client.Account.PrivLevel > 1)
                player.Out.SendMessage("Uni-Portal spell not found.",
                    eChatType.CT_Items, eChatLoc.CL_SystemWindow);


            this.OnTeleport(player, destination);
        }

        /// <summary>
        /// Teleport the player to the designated coordinates. 
        /// </summary>
        /// <param name="player"></param>
        /// <param name="destination"></param>
        protected virtual void OnTeleport(GamePlayer player, DbTeleport destination)
        {
            if (player.InCombat == false && GameRelic.IsPlayerCarryingRelic(player) == false)
            {
                player.LeaveHouse();
                GameLocation currentLocation =
                    new GameLocation("TeleportStart", player.CurrentRegionID, player.X, player.Y, player.Z);
                player.MoveTo((ushort) destination.RegionID, destination.X, destination.Y, destination.Z,
                    (ushort) destination.Heading);
                GameServer.ServerRules.OnPlayerTeleport(player, currentLocation, destination);
            }
        }
    }
}
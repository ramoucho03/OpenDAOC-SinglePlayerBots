using DOL.Database;
using DOL.GS.Keeps;
using DOL.GS.ServerProperties;
using DOL.Language;

namespace DOL.GS.PacketHandler.Client.v168
{
    [PacketHandlerAttribute(PacketHandlerType.TCP, eClientPackets.DoorRequest, "Door Interact Request Handler", eClientStatus.PlayerInGame)]
    public class DoorRequestHandler : PacketHandler
    {
        public static int HandlerDoorId { get; private set; }

        /// <summary>
        /// door index which is unique
        /// </summary>
        protected override void HandlePacketInternal(GameClient client, GSPacketIn packet)
        {
            int doorId = (int) packet.ReadInt();
            HandlerDoorId = doorId;
            byte doorState = (byte) packet.ReadByte();
            int doorType = doorId / 100000000;

            // Interaction distance. The base is a dedicated, generous, tunable
            // distance (WORLD_DOOR_INTERACT_DISTANCE) rather than a small multiple
            // of the loot pickup range: hand-imported door rows — the NF / Agramon
            // realm grilles ("porte grilles", which the user calls Ellan Vannin) —
            // store coordinates that don't exactly line up with the client-rendered
            // fixture, so the old 512u radius reported "too far" even when the player
            // was standing in the grille. Border-keep doors get +50%. Falls back to
            // the legacy WORLD_PICKUP_DISTANCE*2/*3 if the property is unset (0).
            int doorBaseDistance = Properties.WORLD_DOOR_INTERACT_DISTANCE > 0
                ? Properties.WORLD_DOOR_INTERACT_DISTANCE
                : Properties.WORLD_PICKUP_DISTANCE * 2;
            int radius = GameDoor.IsBorderKeepDoor(doorId)
                ? doorBaseDistance * 3 / 2
                : doorBaseDistance;
            int zoneDoor = doorId / 1000000;
            string debugText = string.Empty;

            // For ToA, the client always sends the same ID, so we need to construct an ID using the current zone.
            if ((eClientExpansion) client.Player.CurrentRegion.Expansion is eClientExpansion.TrialsOfAtlantis)
            {
                debugText = $"ToA DoorID:{doorId} ";
                doorId -= zoneDoor * 1000000;
                zoneDoor = client.Player.CurrentZone.ID;
                doorId += zoneDoor * 1000000;
                HandlerDoorId = doorId;

                // Experimental to handle a few odd TOA door issues.
                if (client.Player.CurrentRegion.IsDungeon)
                    radius *= 4;
            }

            // Debug text.
            if (client.Account.PrivLevel > 1 || Properties.ENABLE_DEBUG)
            {
                if (doorType == 7)
                {
                    int ownerKeepId = doorId / 100000 % 1000;
                    int towerNum = doorId / 10000 % 10;
                    int keepID = ownerKeepId + towerNum * 256;
                    int componentID = doorId / 100 % 100;
                    int doorIndex = doorId % 10;
                    client.Out.SendDebugMessage($"Keep Door ID:{doorId} state:{doorState} (Owner Keep:{ownerKeepId} KeepID:{keepID} ComponentID:{componentID} DoorIndex:{doorIndex} TowerNumber:{towerNum})");

                    if (keepID > 255 && ownerKeepId < 10)
                        ChatUtil.SendDebugMessage(client, "Warning: Towers with an Owner Keep ID < 10 will have untargetable doors!");
                }
                else if (doorType == 9)
                {
                    int doorIndex = doorId - doorType * 10000000;
                    client.Out.SendDebugMessage($"House DoorID:{doorId} state:{doorState} (doorType:{doorType} doorIndex:{doorIndex})");
                }
                else
                {
                    int fixture = doorId - zoneDoor * 1000000;
                    int fixturePiece = fixture;
                    fixture /= 100;
                    fixturePiece -= fixture * 100;
                    client.Out.SendDebugMessage($"{debugText}DoorID:{doorId} state:{doorState} zone:{zoneDoor} fixture:{fixture} fixturePiece:{fixturePiece} Type:{doorType}");
                }
            }

            GameDoorBase door = DoorMgr.GetDoorByID(doorId);

            if (door != null)
            {
                // Don't use TargetObject. DoorRequest is sent before PlayerTarget.
                // Gate on HORIZONTAL distance only (ignoreZ: true). A door object's
                // Z sits above the ground the player stands on (hand-placed door
                // rows and component-built keep/border doors store a Z offset from
                // the walkable floor, made worse by sloped terrain). The 3D check
                // then wrongly reports "too far" at the very foot of the door even
                // when the player is glued to it — the Ellan Vannin realm-entrance
                // barrier bug, which also hit any door not in the hard-coded
                // border-keep id list. Ignoring Z fixes every door at once.
                if (!client.Player.IsWithinRadius(door, radius, true))
                {
                    client.Player.Out.SendMessage(LanguageMgr.GetTranslation(client.Account.Language, "DoorRequestHandler.OnTick.TooFarAway", door.Name), eChatType.CT_System, eChatLoc.CL_SystemWindow);
                    return;
                }

                if (doorType is 7 or 9)
                {
                    UseDoor();
                    return;
                }

                if (client.Account.PrivLevel == 1)
                {
                    if (!door.Locked)
                    {
                        if (door.Health == 0)
                        {
                            UseDoor();
                            return;
                        }

                        if (GameServer.Instance.Configuration.ServerType is EGameServerType.GST_PvP or EGameServerType.GST_PvE)
                        {
                            if (door.Realm != 0)
                            {
                                UseDoor();
                                return;
                            }
                        }
                        else
                        {
                            if (client.Player.Realm == door.Realm || door.Realm is eRealm.Door)
                            {
                                UseDoor();
                                return;
                            }
                        }
                    }
                }
                else
                {
                    client.Out.SendDebugMessage($"GM: Forcing locked door open. (PosternDoor: {door.IsPostern})");
                    UseDoor();
                    return;
                }
            }
            else
            {
                if (doorType != 9 && client.Account.PrivLevel > 1 && !client.Player.CurrentRegion.IsInstance)
                {
                    if (client.Player.TempProperties.GetProperty<bool>(DoorMgr.WANT_TO_ADD_DOORS))
                        client.Player.Out.SendCustomDialog("This door is not in the database. Place yourself nearest to this door and click Accept to add it.", AddDoor);
                    else
                        client.Player.Out.SendMessage("This door is not in the database. Use '/door show' to enable the add door dialog when targeting doors.", eChatType.CT_Important, eChatLoc.CL_SystemWindow);
                }

                UseDoor();
                return;
            }

            void UseDoor()
            {
                GamePlayer player = client.Player;
                GameDoorBase door = DoorMgr.GetDoorByID(doorId);

                if (door != null)
                {
                    if (door is GameKeepDoor)
                    {
                        GameKeepDoor keepDoor = door as GameKeepDoor;

                        if (keepDoor.Component.Keep is GameKeepTower && keepDoor.Component.Keep.KeepComponents.Count > 1)
                            keepDoor.Interact(player);
                    }
                    else
                    {
                        // Horizontal-only check (ignoreZ: true) — same rationale as
                        // the gate above, so the open/close actually fires once the
                        // player is horizontally at the door.
                        if (player.IsWithinRadius(door, radius, true))
                        {
                            if (doorState == 0x01)
                                door.Open(player);
                            else
                                door.Close(player);
                        }
                    }
                }
                else
                {
                    // New frontiers. We don't want this (relic gates, etc).
                    if (player.CurrentRegionID == 163 && player.Client.Account.PrivLevel == 1)
                        return;

                    player.Out.SendDebugMessage($"Door {doorId} not found in door list, opening via GM door hack.");

                    GameDoor dummyDoor = new()
                    {
                        DoorId = doorId,
                        X = player.X,
                        Y = player.Y,
                        Z = player.Z,
                        Realm = eRealm.Door,
                        CurrentRegion = player.CurrentRegion
                    };

                    dummyDoor.Open(player);
                }
            }
        }

        public static void AddDoor(GamePlayer player, byte response)
        {
            if (response != 0x01)
                return;

            int doorType = HandlerDoorId / 100000000;

            if (doorType == 7)
                PositionMgr.CreateDoor(HandlerDoorId, player);
            else
            {
                DbDoor door = new()
                {
                    ObjectId = null,
                    InternalID = HandlerDoorId,
                    Name = "door",
                    Type = HandlerDoorId / 100000000,
                    Level = 20,
                    Realm = 6,
                    X = player.X,
                    Y = player.Y,
                    Z = player.Z,
                    Heading = player.Heading
                };

                GameServer.Database.AddObject(door);
                player.Out.SendMessage($"Added door {HandlerDoorId} to the database!", eChatType.CT_Important,eChatLoc.CL_SystemWindow);
                GameServer.Database.SaveObject(door);
                DoorMgr.Init();
            }
        }
    }
}

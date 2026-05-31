using System.Reflection;
using DOL.GS.Keeps;
using DOL.Logging;

namespace DOL.GS.PacketHandler.Client.v168
{
	[PacketHandlerAttribute(PacketHandlerType.TCP, eClientPackets.ShowWarmapRequest, "Show Warmap", eClientStatus.PlayerInGame)]
	public class WarmapShowRequestHandler : PacketHandler
	{
		private static readonly Logger log = LoggerManager.Create(MethodBase.GetCurrentMethod().DeclaringType);

		/// <summary>
		/// Re-encodes a keep's warmap identifier byte EXACTLY as the matching
		/// SendWarmapUpdate (PacketLib) emitted it for this client version. The
		/// teleport-click packet echoes this packed byte, not the real KeepID, so
		/// re-encoding and matching is the version-proof way to recover the keep.
		/// PacketLib170 (clients &lt; 1115) writes (map&lt;&lt;6)|(index&lt;&lt;3)|tower;
		/// PacketLib1115 (&gt;= 1115) writes ((map-1)&lt;&lt;6)|(index&lt;&lt;3)|tower with
		/// the Agramon (&gt;150) index special-case. Kept byte-identical to those
		/// encoders so the match is exact even for the quirky cases.
		/// </summary>
		private static byte EncodeWarmapKeepByte(AbstractGameKeep keep, GameClient.eClientVersion version)
		{
			int id = keep.KeepID & 0xFF;
			int tower = keep.KeepID >> 8;

			if ((int)version >= (int)GameClient.eClientVersion.Version1115)
			{
				int map = (id / 25) - 1;
				int index = id - (map * 25 + 25);
				if ((keep.KeepID & 0xFF) > 150)
					index = keep.KeepID - 151;
				return (byte)(((map - 1) << 6) | (index << 3) | tower);
			}
			else
			{
				int map = (id - 25) / 25;
				int index = id - (map * 25 + 25);
				return (byte)((map << 6) | (index << 3) | tower);
			}
		}

		/// <summary>
		/// Resolves the keep a player clicked on the warmap from the packed byte
		/// the client sent, by matching it against every frontier keep re-encoded
		/// with this client's PacketLib formula. The packed byte is unique across
		/// the frontier keep set, so the match is exact. Returns null if nothing
		/// matches (e.g. an unrecognised client encoding).
		/// </summary>
		private static AbstractGameKeep ResolveWarmapKeepByPackedByte(int rawByte, GameClient.eClientVersion version)
		{
			byte target = (byte)rawByte;

			foreach (AbstractGameKeep keep in GameServer.KeepManager.GetFrontierKeeps())
			{
				if (keep != null && EncodeWarmapKeepByte(keep, version) == target)
					return keep;
			}

			return null;
		}

		protected override void HandlePacketInternal(GameClient client, GSPacketIn packet)
		{
			int code = packet.ReadByte();
			int RealmMap = packet.ReadByte();
			int keepId = packet.ReadByte();
			int rawKeepId = keepId;

			if (client == null || client.Player == null)
				return;

			//hack fix new keep ids
			else if ((int)client.Version >= (int)GameClient.eClientVersion.Version190 && (int)client.Version < (int)GameClient.eClientVersion.Version1115)
			{
				if (keepId >= 82)
					keepId -= 7;
				else if (keepId >= 62)
					keepId -= 12;
			}

			switch (code)
			{
				//warmap open
				//warmap update
				case 0:
				{
					client.Player.WarMapPage = (byte)RealmMap;
					break;
				}
				case 1:
				{
					client.Out.SendWarmapUpdate(GameServer.KeepManager.GetKeepsByRealmMap(client.Player.WarMapPage));
					WarMapMgr.SendFightInfo(client);
					break;
				}
				//teleport
				case 2:
					{
						client.Out.SendWarmapUpdate(GameServer.KeepManager.GetKeepsByRealmMap(client.Player.WarMapPage));
						WarMapMgr.SendFightInfo(client);

						// Diagnostic: warmap teleport attempts are rare (player
						// click) and have historically failed silently. Log the
						// raw inputs plus what the keep id WOULD resolve to if the
						// client is echoing the PacketLib1115 packed byte, so the
						// real wire format can be confirmed from the server log.
						if (log.IsInfoEnabled)
						{
							AbstractGameKeep rawLookup = GameServer.KeepManager.GetKeepByID(keepId);
							AbstractGameKeep packedMatch = ResolveWarmapKeepByPackedByte(rawKeepId, client.Version);
							log.Info($"[WarmapTP] {client.Player.Name} v{client.Version} region={client.Player.CurrentRegionID} page={RealmMap} "
								+ $"rawKeepId={rawKeepId} remappedKeepId={keepId} -> GetKeepByID={(rawLookup != null ? rawLookup.KeepID + "/" + rawLookup.Name : "null")} | "
								+ $"packed-byte match -> {(packedMatch != null ? packedMatch.KeepID + "/" + packedMatch.Name : "null")}");
						}

						if (client.Account.PrivLevel == (int)ePrivLevel.Player &&
							(client.Player.InCombat || client.Player.CurrentRegionID != 163 || GameRelic.IsPlayerCarryingRelic(client.Player)))
						{
							if (log.IsInfoEnabled)
								log.Info($"[WarmapTP] {client.Player.Name} refused (gate A): InCombat={client.Player.InCombat} region={client.Player.CurrentRegionID} carryingRelic={GameRelic.IsPlayerCarryingRelic(client.Player)}");
							return;
						}

						AbstractGameKeep keep = null;

						if (keepId > 6)
						{
							keep = GameServer.KeepManager.GetKeepByID(keepId);

							// THE REPAIR. Older clients send a near-real KeepID
							// (handled above + the legacy 190-1114 remap), but the
							// modern client (>= 1115) echoes the PACKED warmap byte
							// the server sent — which is not a real KeepID and had
							// no decode here, so GetKeepByID returned null and the
							// teleport silently failed. Resolve it deterministically
							// by re-encoding every frontier keep exactly as our own
							// SendWarmapUpdate did and matching the received byte.
							if (keep == null)
							{
								keep = ResolveWarmapKeepByPackedByte(rawKeepId, client.Version);
								if (keep != null && log.IsInfoEnabled)
									log.Info($"[WarmapTP] {client.Player.Name} resolved clicked keep via packed-byte match: rawKeepId={rawKeepId} -> {keep.KeepID}/{keep.Name}");
							}
						}

						if (keep == null && keepId > 6)
						{
							if (log.IsInfoEnabled)
								log.Info($"[WarmapTP] {client.Player.Name} refused (gate B): keepId did not resolve to any keep (rawKeepId={rawKeepId}, remapped={keepId}, v{client.Version}). Client may use an unrecognised warmap id encoding.");
							return;
						}

						if (client.Account.PrivLevel == (int)ePrivLevel.Player)
						{
							bool found = false;

							if (keep != null)
							{
								// if we are requesting to teleport to a keep we need to check that keeps requirements first

								if (keep.Realm != client.Player.Realm)
								{
									if (log.IsInfoEnabled)
										log.Info($"[WarmapTP] {client.Player.Name} refused (gate C): keep {keep.KeepID}/{keep.Name} realm={keep.Realm} != player realm={client.Player.Realm}.");
									return;
								}

								if (keep is GameKeep && ((keep as GameKeep).OwnsAllTowers == false || keep.InCombat))
								{
									if (log.IsInfoEnabled)
										log.Info($"[WarmapTP] {client.Player.Name} refused (gate D): keep {keep.KeepID}/{keep.Name} OwnsAllTowers={(keep as GameKeep).OwnsAllTowers} InCombat={keep.InCombat}.");
									return;
								}

								// Missing: Supply line check
							}

							if (client.Player.CurrentRegionID == 163)
							{
								// We are in the frontiers and all keep requirements are met or we are not near a keep
								// this may be a portal stone in the RvR village, for example

								foreach (GameStaticItem item in client.Player.GetItemsInRadius(WorldMgr.INTERACT_DISTANCE))
								{
									if (item is FrontiersPortalStone)
									{
										found = true;
										break;
									}
								}
							}

							if (!found)
							{
								if (log.IsInfoEnabled)
									log.Info($"[WarmapTP] {client.Player.Name} refused (gate E): no FrontiersPortalStone within {WorldMgr.INTERACT_DISTANCE}u.");
								client.Player.Out.SendMessage("You cannot teleport unless you are near a valid portal stone.", eChatType.CT_System, eChatLoc.CL_SystemWindow);
								return;
							}
						}

						int x = 0;
						int y = 0;
						int z = 0;
						ushort heading = 0;
						switch (keepId)
						{
							//sauvage
							case 1:
							//snowdonia
							case 2:
							//svas
							case 3:
							//vind
							case 4:
							//ligen
							case 5:
							//cain
							case 6:
								{
									GameServer.KeepManager.GetBorderKeepLocation(keepId, out x, out y, out z, out heading);
									break;
								}
							default:
								{
									if (keep != null && keep is GameKeep)
									{
										FrontiersPortalStone stone = keep.TeleportStone;
										if (stone != null) 
										{
											heading = stone.Heading;
											z = stone.Z;
											stone.GetTeleportLocation(out x, out y);
										}
										else
										{
											x = keep.X;
											y = keep.Y;
											z = keep.Z+150;
											heading = keep.Heading;
										}
									}
									break;
								}
						}

						if (x != 0)
						{
							if (log.IsInfoEnabled)
								log.Info($"[WarmapTP] {client.Player.Name} teleporting to keepId={keepId} dest=({x},{y},{z}) heading={heading}.");
							client.Player.MoveTo(163, x, y, z, heading);
						}
						else if (log.IsInfoEnabled)
						{
							log.Info($"[WarmapTP] {client.Player.Name} refused (gate F): destination resolved to x=0 (keepId={keepId}, TeleportStone null and no border-keep match).");
						}

						break;
					}
			}
		}
	}

}

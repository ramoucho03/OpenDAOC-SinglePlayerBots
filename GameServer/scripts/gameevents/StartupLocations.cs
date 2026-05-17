using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using DOL.Database;
using DOL.Events;

namespace DOL.GS.GameEvents
{
	/// <summary>
	/// Moves new created Characters to the starting location based on region, class and race
	/// </summary>
	public static class StartupLocations
	{
		/// <summary>
		/// Declare a logger for this class.
		/// </summary>
		private static readonly Logging.Logger log = Logging.LoggerManager.Create(MethodBase.GetCurrentMethod().DeclaringType);

		/// <summary>
		/// Cached DB Startup Location
		/// </summary>
		private static readonly List<StartupLocation> m_cachedLocations = new List<StartupLocation>();

		/// <summary>
		/// Current Game Request Tutorial Region ID.
		/// </summary>
		private const int TUTORIAL_REGIONID = 27;
		
		[ScriptLoadedEvent]
		public static void OnScriptLoaded(DOLEvent e, object sender, EventArgs args)
		{
			GameEventMgr.AddHandler(DatabaseEvent.CharacterCreated, new DOLEventHandler(CharacterCreation));
			GameEventMgr.AddHandler(DatabaseEvent.CharacterSelected, new DOLEventHandler(CharacterSelection));
			
			InitStartupLocation();
			
			if (log.IsInfoEnabled)
				log.Info("StartupLocations initialized");
		}

		[ScriptUnloadedEvent]
		public static void OnScriptUnloaded(DOLEvent e, object sender, EventArgs args)
		{
			GameEventMgr.RemoveHandler(DatabaseEvent.CharacterCreated, new DOLEventHandler(CharacterCreation));
			GameEventMgr.RemoveHandler(DatabaseEvent.CharacterSelected, new DOLEventHandler(CharacterSelection));
		}
		
		/// <summary>
		/// Init Startup Location Static Cache
		/// </summary>
		[RefreshCommand]
		public static void InitStartupLocation()
		{
			m_cachedLocations.Clear();
			
			foreach (var obj in GameServer.Database.SelectAllObjects<StartupLocation>())
				m_cachedLocations.Add(obj);
		}

		/// <summary>
		/// Change location on character creation
		/// </summary>
		public static void CharacterCreation(DOLEvent ev, object sender, EventArgs args)
		{
			// Check Args
			var chArgs = args as CharacterEventArgs;
			
			if (chArgs == null)
				return;
			
			DbCoreCharacter ch = chArgs.Character;
			
			try
			{
				
				var availableLocation = GetAllStartupLocationForCharacter(ch, chArgs.GameClient.Version);

				StartupLocation dbStartupLocation = null;
				
				// get the first entry according to Tutorial Enabling.
				foreach (var location in availableLocation)
				{
					dbStartupLocation = location;
					break;
				}

				if (dbStartupLocation == null)
				{
					log.WarnFormat("startup location not found: account={0}; char name={1}; region={2}; realm={3}; class={4} ({5}); race={6} ({7}); version={8}",
						ch.AccountName, ch.Name, ch.Region, ch.Realm, ch.Class, (eCharacterClass) ch.Class, ch.Race, (eRace)ch.Race, chArgs.GameClient.Version);

					// Fallback: rather than leave the char with Xpos=Ypos=Zpos=Region=0
					// (which spawns them in the void → client closes the
					// connection → "0 bytes received" disconnect, see Heretic
					// regression), use a generic realm-capital spawn so they
					// can at least enter the world. Operator can /jump them
					// later. Coords match Camelot / Jordheim / Tir na Nog.
					ApplyRealmCapitalFallback(ch);
					BindCharacter(ch);
				}
				else
				{
					ch.Xpos = dbStartupLocation.XPos;
					ch.Ypos = dbStartupLocation.YPos;
					ch.Zpos = dbStartupLocation.ZPos;
					ch.Region = dbStartupLocation.Region;
					ch.Direction = dbStartupLocation.Heading;
					BindCharacter(ch);
				}
			}
			catch (Exception e)
			{
				if (log.IsErrorEnabled)
					log.ErrorFormat("StartupLocations script: error changing location. account={0}; char name={1}; region={2}; realm={3}; class={4} ({5}); race={6} ({7}); version={8}; {9}",
						ch.AccountName, ch.Name, ch.Region, ch.Realm, ch.Class, (eCharacterClass) ch.Class, ch.Race, (eRace)ch.Race, chArgs.GameClient.Version, e);
			}
		}

		/// <summary>
		/// Change location on character selection if it has any wrong values...
		/// </summary>
		public static void CharacterSelection(DOLEvent ev, object sender, EventArgs args)
		{
			// Check Args
			var chArgs = args as CharacterEventArgs;
			
			if (chArgs == null)
				return;
			
			DbCoreCharacter ch = chArgs.Character;
			
			// check if location looks ok.
			if (ch.Xpos == 0 && ch.Ypos == 0 && ch.Zpos == 0)
			{
				// This character needs to be fixed !
				CharacterCreation(ev, sender, args);
				GameServer.Database.SaveObject(ch);
				return;
			}
			
			// check if bind looks ok.
			if (ch.BindXpos == 0 && ch.BindYpos == 0 && ch.BindZpos == 0)
			{
				// This Bind needs to be fixed !
				BindCharacter(ch);
				GameServer.Database.SaveObject(ch);
			}
		}
		
		public static IList<StartupLocation> GetAllStartupLocationForCharacter(DbCoreCharacter ch, GameClient.eClientVersion cli)
		{
			return m_cachedLocations.Where(sl => sl.MinVersion <= (int)cli)
				.Where(sl => sl.ClassID == 0 || sl.ClassID == ch.Class)
				.Where(sl => sl.RaceID == 0 || sl.RaceID == ch.Race)
				.Where(sl => sl.RealmID == 0 || sl.RealmID == ch.Realm)
				.Where(sl => sl.ClientRegionID == 0 || sl.ClientRegionID == ch.Region)
				.OrderByDescending(sl => sl.MinVersion).ThenByDescending(sl => sl.ClientRegionID)
				.ThenByDescending(sl => sl.RealmID).ThenByDescending(sl => sl.ClassID)
				.ThenByDescending(sl => sl.RaceID).ToList();
		}
		
		public static StartupLocation GetNonTutorialLocation(GamePlayer player)
		{
			try
			{
				return GetAllStartupLocationForCharacter(player.Client.Account.Characters[player.Client.ActiveCharIndex], player.Client.Version).First(sl => sl.ClientRegionID != TUTORIAL_REGIONID);
			}
			catch
			{
				return null;
			}
				
		}

		/// <summary>
		/// Places the character at the realm capital when no specific startup
		/// location matches. Saves a brand-new character from spawning at
		/// (0,0,0) region 0, which makes the client immediately disconnect
		/// once it receives the position packet. The operator can still
		/// /jump them to a more accurate spot afterwards, but at least the
		/// player can log in.
		/// </summary>
		private static void ApplyRealmCapitalFallback(DbCoreCharacter ch)
		{
			switch ((eRealm)ch.Realm)
			{
				case eRealm.Albion:    // Camelot — King's Crossing area
					ch.Region = 10;
					ch.Xpos = 33000; ch.Ypos = 32000; ch.Zpos = 5000;
					ch.Direction = 0;
					break;
				case eRealm.Midgard:   // Jordheim — Castle entrance
					ch.Region = 101;
					ch.Xpos = 31000; ch.Ypos = 30000; ch.Zpos = 8700;
					ch.Direction = 0;
					break;
				case eRealm.Hibernia:  // Tir na Nog — central square
					ch.Region = 201;
					ch.Xpos = 32000; ch.Ypos = 27000; ch.Zpos = 7700;
					ch.Direction = 0;
					break;
				default:               // No matching realm — drop them in Camelot
					ch.Region = 10;
					ch.Xpos = 33000; ch.Ypos = 32000; ch.Zpos = 5000;
					ch.Direction = 0;
					break;
			}

			if (log.IsInfoEnabled)
				log.Info($"StartupLocations: applied realm-capital fallback for {ch.Name} (realm={ch.Realm}) → region={ch.Region} ({ch.Xpos},{ch.Ypos},{ch.Zpos}). Add a startuplocation row to fix.");
		}

		/// <summary>
		/// Binds character to current location
		/// </summary>
		/// <param name="ch"></param>
		public static void BindCharacter(DbCoreCharacter ch)
		{
			ch.BindRegion = ch.Region;
			ch.BindHeading = ch.Direction;
			ch.BindXpos = ch.Xpos;
			ch.BindYpos = ch.Ypos;
			ch.BindZpos = ch.Zpos;
		}
	}
}

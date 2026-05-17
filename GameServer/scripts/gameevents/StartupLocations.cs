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
					// Hard fallback so the character ALWAYS has a valid spawn.
					// Without this, a missing row (e.g. extension class on a
					// world DB that hasn't applied the per-class XML rows) left
					// Xpos/Ypos/Zpos/Region = 0, putting the player in "no zone"
					// at the next login and forcing the operator to fix the row
					// manually.
					ApplyRealmCapitalFallback(ch);
					BindCharacter(ch);

					if (log.IsWarnEnabled)
						log.WarnFormat("StartupLocation not found, applied realm-capital fallback: account={0}; char name={1}; region={2}; realm={3}; class={4} ({5}); race={6} ({7}); version={8}",
							ch.AccountName, ch.Name, ch.Region, ch.Realm, ch.Class, (eCharacterClass) ch.Class, ch.Race, (eRace)ch.Race, chArgs.GameClient.Version);
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

				// Even on exception (e.g. corrupt DB row) make sure the character has
				// SOMETHING usable rather than being stranded at (0,0,0).
				if (ch.Xpos == 0 && ch.Ypos == 0 && ch.Zpos == 0)
				{
					try
					{
						ApplyRealmCapitalFallback(ch);
						BindCharacter(ch);
					}
					catch { /* last-resort */ }
				}
			}
		}

		/// <summary>
		/// Sets the character to a known-valid spawn near the realm's level-1 town.
		/// Coordinates mirror the realm-only fallback rows shipped in
		/// StartupLocation.xml; they are duplicated here so creation never relies
		/// on a DB row being present (which is exactly what was missing for new
		/// extension classes on fresh DB imports).
		/// </summary>
		public static void ApplyRealmCapitalFallback(DbCoreCharacter ch)
		{
			switch ((eRealm)ch.Realm)
			{
				case eRealm.Albion:
					// Cotswold area (Black Mountains South, region 1).
					ch.Xpos = 560217;
					ch.Ypos = 510635;
					ch.Zpos = 2392;
					ch.Direction = 2980;
					ch.Region = 1;
					break;
				case eRealm.Midgard:
					// Mularn (Vale of Mularn, region 100).
					ch.Xpos = 802869;
					ch.Ypos = 726016;
					ch.Zpos = 4699;
					ch.Direction = 1399;
					ch.Region = 100;
					break;
				case eRealm.Hibernia:
					// Mag Mell (Lough Derg, region 200).
					ch.Xpos = 347279;
					ch.Ypos = 489090;
					ch.Zpos = 5286;
					ch.Direction = 2332;
					ch.Region = 200;
					break;
				default:
					// Unknown realm — default to Albion so the player at least
					// loads somewhere instead of being stuck in nowhere.
					ch.Xpos = 560217;
					ch.Ypos = 510635;
					ch.Zpos = 2392;
					ch.Direction = 2980;
					ch.Region = 1;
					break;
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
				// Try the normal CharacterCreation lookup first; if that still
				// returns no row (the original bug — extension class with no
				// matching StartupLocation), the realm-capital fallback inside
				// CharacterCreation guarantees valid coords this time.
				CharacterCreation(ev, sender, args);

				// Last-resort defense: if for any reason CharacterCreation
				// failed to populate coords, apply the realm-capital fallback
				// directly here. Without this a buggy creation event handler
				// could leave the character permanently stuck.
				if (ch.Xpos == 0 && ch.Ypos == 0 && ch.Zpos == 0)
				{
					ApplyRealmCapitalFallback(ch);
					BindCharacter(ch);
				}

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

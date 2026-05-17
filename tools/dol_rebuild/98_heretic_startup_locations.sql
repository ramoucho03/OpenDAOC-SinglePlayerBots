-- Heretic startup locations (class 33, Albion).
-- DOLSharp doesn't ship startup rows for Catacombs+ classes, so a brand
-- new Heretic created on a stock OpenDAoC server ends up at (0,0,0)
-- region 0 and the client closes the connection the moment it gets the
-- malformed position packet.
--
-- StartupLocations.cs also has a realm-capital fallback as a safety net,
-- but inserting the proper rows here makes the player spawn at the
-- canonical Cleric / Friar coords (Camelot / Avalon Marsh / Cornwall)
-- instead of being dropped at the generic capital center.
--
-- Mirror this file for any other extension class you wire into
-- STARTING_CLASSES_DICT (Warlock = 59 / Mid, Vampiir = 58 / Hib,
-- Maulers = 60/61/62) and add their rows here.

INSERT IGNORE INTO startuplocation
  (XPos, YPos, ZPos, Heading, Region, MinVersion, RealmID, RaceID, ClassID, ClientRegionID)
VALUES
  -- Briton (1)   -> Camelot area (reuses Cleric coords)
  (574315, 529639, 2906, 2852, 1, 0, 1,  1, 33, 0),
  -- Avalonian (2) -> Avalon Marsh dock (reuses Cleric coords)
  (473048, 628255, 2048,  491, 1, 0, 1,  2, 33, 0),
  -- Inconnu (13) -> Camelot fallback (no dedicated Cleric coords)
  (574315, 529639, 2906, 2852, 1, 0, 1, 13, 33, 0),
  -- Korazh (19, LotM Minotaur) -> Camelot fallback
  (574315, 529639, 2906, 2852, 1, 0, 1, 19, 33, 0);

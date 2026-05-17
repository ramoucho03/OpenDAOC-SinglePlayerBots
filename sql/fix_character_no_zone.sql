-- Fix characters stuck at Xpos/Ypos/Zpos/Region = 0
--
-- Characters created BEFORE the StartupLocations.cs realm-capital
-- fallback fix (commit on or after this file) may have been saved with
-- no spawn coordinates. They will refuse to log in cleanly or land in
-- "no zone".
--
-- This script repairs them in place to their realm's level-1 town
-- (Cotswold / Mularn / Mag Mell), matching the C# fallback. Bind point
-- is also set so a /release routes to the same town.
--
-- Apply on the server:
--   docker exec -i opendaoc-db mariadb -uroot -p<root_pwd> opendaoc \
--     < sql/fix_character_no_zone.sql
--
-- The new code-side fallback (CharacterSelection in StartupLocations.cs)
-- will also catch these on next login, so running this script is just
-- a way to repair them upfront without waiting for the player to log in.

-- Albion (realm 1) -> Cotswold
UPDATE `Character`
SET    Xpos = 560217, Ypos = 510635, Zpos = 2392, Direction = 2980, Region = 1,
       BindXpos = 560217, BindYpos = 510635, BindZpos = 2392, BindHeading = 2980, BindRegion = 1
WHERE  Realm = 1 AND Xpos = 0 AND Ypos = 0 AND Zpos = 0;

-- Midgard (realm 2) -> Mularn
UPDATE `Character`
SET    Xpos = 802869, Ypos = 726016, Zpos = 4699, Direction = 1399, Region = 100,
       BindXpos = 802869, BindYpos = 726016, BindZpos = 4699, BindHeading = 1399, BindRegion = 100
WHERE  Realm = 2 AND Xpos = 0 AND Ypos = 0 AND Zpos = 0;

-- Hibernia (realm 3) -> Mag Mell
UPDATE `Character`
SET    Xpos = 347279, Ypos = 489090, Zpos = 5286, Direction = 2332, Region = 200,
       BindXpos = 347279, BindYpos = 489090, BindZpos = 5286, BindHeading = 2332, BindRegion = 200
WHERE  Realm = 3 AND Xpos = 0 AND Ypos = 0 AND Zpos = 0;

SELECT CONCAT('Repaired ', ROW_COUNT(), ' Hibernia character(s).') AS Hibernia;

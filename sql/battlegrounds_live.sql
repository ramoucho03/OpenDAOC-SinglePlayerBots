-- =====================================================================
-- Battlegrounds Live - canonical Teleport rows for every BG region/realm
-- =====================================================================
-- Idempotent (REPLACE INTO). Applied by the gameserver entrypoint on every
-- start, alongside heretic_live.sql.
--
-- WHY THIS FILE EXISTS
-- --------------------
-- Upstream OpenDAoC-Database ships:
--   * 5 Battleground rows (regions 253/252/251/250/165 -> level brackets
--     15-19 / 20-24 / 25-29 / 30-34 / 45-49)
--   * Portal keeps for every realm in each of those regions (keep.sql)
--   * BUT zero Teleport rows for any of those BG regions
--
-- GameServer/scripts/teleporters/BattlegroundTeleportHelper.cs documents
-- two resolution paths:
--   1. (canonical)  a Teleport table row matching the BG region + player's
--                   realm -- "coordinates authored to match the real DAoC
--                   client map"
--   2. (fallback)   the realm's portal keep object inside the BG region
--
-- Without (1), every BG teleport relies on (2) and inherits its failure
-- modes: portal-keep object not yet loaded, IsPortalKeep flag wrong,
-- KeepManager not yet initialised when the helper is called, etc. When
-- (2) fails the helper returns null and the player gets "Could not
-- resolve a destination for that battleground." even though the data is
-- right there in the DB.
--
-- This file fills path (1) with the same coordinates the portal keeps
-- use, which are already validated to sit inside the BG zone (the
-- helper's final zone-bounds guard would otherwise refuse them).
--
-- COORDINATE SOURCE
-- -----------------
-- Each row's (X, Y, Z, Heading) is copied verbatim from keep.sql's
-- `<Realm> Portal Keep` row for the same RegionID. Realm IDs:
--   1 = Albion, 2 = Midgard, 3 = Hibernia.
-- The four "Atlas-style" small BG regions (253/252/251/250) share the
-- same zone layout, so all three realm keeps are at the same coords
-- across those four regions -- copied as-is, intentionally.
--
-- ZONE-BOUNDS SANITY CHECK
-- ------------------------
-- WorldMgr scales `zones.OffsetX/OffsetY/Width/Height` by 8192 at load
-- time, so the runtime bounds for each BG zone are:
--   165 (Cathal Valley):  X,Y in [524288 , 589824)  (Off=64, W/H=8)
--   250 (Caledon):        X,Y in [  8192 ,  73728)  (Off=1,  W/H=8)
--   251 (Murdaigean):     X,Y in [  8192 ,  73728)
--   252 (Thidranki):      X,Y in [  8192 ,  73728)
--   253 (Abermenai):      X,Y in [  8192 ,  73728)
-- Every (X, Y) inserted below sits comfortably inside those bounds.
--
-- VERIFICATION QUERY
-- ------------------
--   SELECT RegionID, Realm, X, Y, Z FROM teleport
--     WHERE Teleport_ID LIKE 'bg_live_%' ORDER BY RegionID, Realm;
--   -- expect 15 rows (5 regions x 3 realms).
-- =====================================================================

-- Cleanup any earlier attempt under the same naming scheme so re-runs
-- never leave stale rows behind even if the row shape changes later.
DELETE FROM `teleport` WHERE `Teleport_ID` LIKE 'bg_live_%';

REPLACE INTO `teleport`
    (`Type`, `TeleportID`, `Realm`, `RegionID`, `X`, `Y`, `Z`, `Heading`, `LastTimeRowUpdated`, `Teleport_ID`)
VALUES
    -- Region 253 -- Abermenai (BG 15-19) ---------------------------------
    ('', 'Abermenai',     1, 253, 37301, 52362, 3944, 2035, NOW(), 'bg_live_253_alb'),
    ('', 'Abermenai',     2, 253, 54053, 24680, 4320,  346, NOW(), 'bg_live_253_mid'),
    ('', 'Abermenai',     3, 253, 18362, 18257, 4320, 3517, NOW(), 'bg_live_253_hib'),

    -- Region 252 -- Thidranki (BG 20-24) ---------------------------------
    ('', 'Thidranki',     1, 252, 37301, 52362, 3944, 2035, NOW(), 'bg_live_252_alb'),
    ('', 'Thidranki',     2, 252, 54053, 24680, 4320,  346, NOW(), 'bg_live_252_mid'),
    ('', 'Thidranki',     3, 252, 18362, 18257, 4320, 3517, NOW(), 'bg_live_252_hib'),

    -- Region 251 -- Murdaigean (BG 25-29) --------------------------------
    ('', 'Murdaigean',    1, 251, 37301, 52362, 3944, 2035, NOW(), 'bg_live_251_alb'),
    ('', 'Murdaigean',    2, 251, 54053, 24680, 4320,  346, NOW(), 'bg_live_251_mid'),
    ('', 'Murdaigean',    3, 251, 18362, 18257, 4320, 3517, NOW(), 'bg_live_251_hib'),

    -- Region 250 -- Caledon / Caledonia (BG 30-34) -----------------------
    ('', 'Caledon',       1, 250, 37301, 52362, 3944, 2035, NOW(), 'bg_live_250_alb'),
    ('', 'Caledon',       2, 250, 54053, 24680, 4320,  346, NOW(), 'bg_live_250_mid'),
    ('', 'Caledon',       3, 250, 18362, 18257, 4320, 3517, NOW(), 'bg_live_250_hib'),

    -- Region 165 -- Cathal Valley (BG 45-49) -----------------------------
    ('', 'Cathal Valley', 1, 165, 583575, 584548, 4896, 1988, NOW(), 'bg_live_165_alb'),
    ('', 'Cathal Valley', 2, 165, 574386, 538221, 4832,  571, NOW(), 'bg_live_165_mid'),
    ('', 'Cathal Valley', 3, 165, 536186, 584836, 5800, 1791, NOW(), 'bg_live_165_hib');

-- ---- Battleground label / bracket clean-up ---------------------------
-- The upstream Battleground row for region 250 carries the misleading
-- ID "Caledonia (Level 34-39 - RR3L5)" while its actual MinLevel/MaxLevel
-- is 30/34. Rename it so admin dumps and the helper's eligibility math
-- agree. Idempotent: matches on the upstream PK string verbatim.
UPDATE `battleground`
   SET `Battleground_ID` = 'Caledonia (Level 30-34 - RR3L5)'
 WHERE `Battleground_ID` = 'Caledonia (Level 34-39 - RR3L5)';

-- =====================================================================
-- End.
-- =====================================================================

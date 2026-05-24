-- =====================================================================
-- Strategy 4: Larogoth -> functional loot wiring
-- =====================================================================
-- Converts the ItemLootSource table (populated by 30_larogoth_loot.sql)
-- into rows that the gameserver's loot generators actually consume:
--
--   monster_normal_drop   -> LootTemplate (Chance=8%) + MobXLootTemplate
--   monster_one_time_drop -> LootOTD (one-shot named-mob drop)
--
-- Why this is needed: upstream OpenDAoC ships ~28 MB of mobs and ~13 MB of
-- items, but the LINK between them (loot wiring) is starved:
--   loottemplate.sql        190 KB   -> ~5k rows  (covers <10% of mobs)
--   mobxloottemplate.sql    259 KB   -> ~6k rows
--   loototd.sql             7 KB
--   mobdroptemplate.sql     956 B    -> empty
--   quest.sql               869 B    -> empty
-- So most mobs fall back to LootGeneratorMoney (money only). Larogoth has
-- ~22k items each pointing to 1-3 droppers; the join (Mob.Name = Source)
-- typically resolves ~70-80% of those into actual functional drops.
--
-- Idempotency: NOT EXISTS subqueries (no unique constraint on these tables,
-- so INSERT IGNORE would NOT dedupe). Safe to re-run by hand.
-- Apply order: must run AFTER 30_larogoth_loot.sql (depends on
-- ItemLootSource). The entrypoint orchestrates this via checksum tracking.
--
-- Rollback (manual, item-level — no PackageID on these tables):
--   DELETE lt FROM LootTemplate lt
--     INNER JOIN MobXLootTemplate mxt ON mxt.LootTemplateName = lt.TemplateName
--     INNER JOIN Mob m ON m.Name = mxt.MobName
--     WHERE EXISTS (SELECT 1 FROM ItemLootSource ils
--                   WHERE ils.Id_nb = lt.ItemTemplateID
--                     AND ils.SourceName = mxt.MobName);
--   (Repeat with DELETE FROM MobXLootTemplate / LootOTD using the same join.)
-- =====================================================================

-- ---- 1. MobXLootTemplate: one row per (mob, mob-as-template) -----------
-- TemplateName == MobName for simplicity (each mob has its own template).
-- Multiple LootGenerators can coexist; LootGeneratorTemplate looks up by
-- MobName.ToLower() so collation doesn't matter (already utf8mb3_general_ci).
INSERT INTO MobXLootTemplate (MobName, LootTemplateName, DropCount)
SELECT DISTINCT
    m.Name           AS MobName,
    m.Name           AS LootTemplateName,
    1                AS DropCount
FROM ItemLootSource ils
INNER JOIN Mob m ON m.Name = ils.SourceName
WHERE ils.SourceType IN ('monster_normal_drop', 'monster_one_time_drop')
  AND NOT EXISTS (
      SELECT 1 FROM MobXLootTemplate x
      WHERE x.MobName = m.Name AND x.LootTemplateName = m.Name
  );

-- ---- 2. LootTemplate: one row per (mob-template, item) at 8% chance ----
-- "monster_normal_drop" is the common case in Larogoth — multiple items
-- listed per mob, each presumed to roll independently. 8% is the classic
-- DAoC named-mob drop rate. One_time_drop is excluded (handled by LootOTD).
INSERT INTO LootTemplate (TemplateName, ItemTemplateID, Chance, Count)
SELECT DISTINCT
    m.Name           AS TemplateName,
    ils.Id_nb        AS ItemTemplateID,
    8                AS Chance,
    1                AS Count
FROM ItemLootSource ils
INNER JOIN Mob m            ON m.Name   = ils.SourceName
INNER JOIN ItemTemplate it  ON it.Id_nb = ils.Id_nb
WHERE ils.SourceType = 'monster_normal_drop'
  AND NOT EXISTS (
      SELECT 1 FROM LootTemplate x
      WHERE x.TemplateName = m.Name AND x.ItemTemplateID = ils.Id_nb
  );

-- ---- 3. LootOTD: one-shot drops for named mobs -------------------------
-- MinLevel = max(0, mob.Level - 5) so under-leveled killers can't farm
-- elites for their OTD before being able to engage them fairly.
INSERT INTO LootOTD (MobName, ItemTemplateID, MinLevel)
SELECT DISTINCT
    m.Name                                       AS MobName,
    ils.Id_nb                                    AS ItemTemplateID,
    GREATEST(0, CAST(m.Level AS SIGNED) - 5)     AS MinLevel
FROM ItemLootSource ils
INNER JOIN Mob m            ON m.Name   = ils.SourceName
INNER JOIN ItemTemplate it  ON it.Id_nb = ils.Id_nb
WHERE ils.SourceType = 'monster_one_time_drop'
  AND NOT EXISTS (
      SELECT 1 FROM LootOTD x
      WHERE x.MobName = m.Name AND x.ItemTemplateID = ils.Id_nb
  );

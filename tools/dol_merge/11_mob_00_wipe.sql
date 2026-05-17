-- Wipe disabled. Earlier run already performed DELETE FROM mob;
-- the INSERT IGNORE chunks below add any missing rows without
-- duplicating what's already in the table.
SELECT 'mob wipe skipped (already ran earlier)' AS notice;

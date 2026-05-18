-- =====================================================================
-- Heretic Live - Fix #2: lower LevelAcquired so any Heretic level can train
-- =====================================================================

-- Allow Heretic to learn Arawn's Fire and Cthonic Accretion at level 1
-- (was 5, which gates training until character is L5+)
UPDATE classxspecialization
   SET LevelAcquired = 1
 WHERE ClassID = 33
   AND SpecKeyName IN ('Arawn\'s Fire', 'Cthonic Accretion');

-- Verify
SELECT ClassID, SpecKeyName, LevelAcquired FROM classxspecialization WHERE ClassID = 33;

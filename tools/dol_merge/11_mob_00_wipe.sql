-- Full wipe of mob table before re-importing DOLSharp content.
-- Custom runtime spawns (PvE/PvP frontier managers) are not stored
-- here, so this wipe is safe for runtime-managed populations.

SET FOREIGN_KEY_CHECKS=0;
START TRANSACTION;
DELETE FROM mob;
COMMIT;
SET FOREIGN_KEY_CHECKS=1;

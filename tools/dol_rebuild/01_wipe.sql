-- Wipe all world-content tables before re-importing from DOLSharp.
-- Player/progress tables are deliberately preserved.
-- See KEEP_TABLES in gen_full_rebuild.py for the list.

SET FOREIGN_KEY_CHECKS=0;

TRUNCATE TABLE `ability`;
TRUNCATE TABLE `area`;
TRUNCATE TABLE `artifact`;
TRUNCATE TABLE `artifactbonus`;
TRUNCATE TABLE `artifactxitem`;
TRUNCATE TABLE `battleground`;
TRUNCATE TABLE `bindpoint`;
-- classxrealmability skipped: doesn't exist on this server (only the
-- OpenDAoC variant `classxrealmability_atlas` is used, and that one is
-- deliberately preserved to keep characters' assigned RAs intact).
TRUNCATE TABLE `classxspecialization`;
TRUNCATE TABLE `crafteditem`;
TRUNCATE TABLE `craftedxitem`;
TRUNCATE TABLE `dataquest`;
TRUNCATE TABLE `door`;
TRUNCATE TABLE `faction`;
TRUNCATE TABLE `factionaggrolevel`;
TRUNCATE TABLE `househookpointitem`;
TRUNCATE TABLE `househookpointoffset`;
TRUNCATE TABLE `instancexelement`;
TRUNCATE TABLE `itemtemplate`;
TRUNCATE TABLE `keep`;
TRUNCATE TABLE `keepcomponent`;
TRUNCATE TABLE `keephookpoint`;
TRUNCATE TABLE `keepposition`;
TRUNCATE TABLE `languagesystem`;
TRUNCATE TABLE `linexspell`;
TRUNCATE TABLE `linkedfaction`;
TRUNCATE TABLE `lootgenerator`;
TRUNCATE TABLE `loototd`;
TRUNCATE TABLE `loottemplate`;
TRUNCATE TABLE `merchantitem`;
TRUNCATE TABLE `mob`;
TRUNCATE TABLE `mobxloottemplate`;
TRUNCATE TABLE `npcequipment`;
TRUNCATE TABLE `npctemplate`;
TRUNCATE TABLE `regions`;
TRUNCATE TABLE `specialization`;
TRUNCATE TABLE `specxability`;
TRUNCATE TABLE `spell`;
TRUNCATE TABLE `spellline`;
TRUNCATE TABLE `style`;
TRUNCATE TABLE `stylexspell`;
TRUNCATE TABLE `worldobject`;
TRUNCATE TABLE `zonepoint`;

SET FOREIGN_KEY_CHECKS=1;

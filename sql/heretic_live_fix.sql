-- =====================================================================
-- Heretic Live - Runtime fixes (apply AFTER heretic_live_integration.sql)
-- =====================================================================
-- Two distinct fixes:
--   A) Remove the broken 'Heretic Enhancement' baseline SpellLine which
--      has 0 spells in linexspell and blocks the fallback to the generic
--      'Enhancement' baseline (26 spells). This is an UPSTREAM data bug.
--   B) Lower linexspell.Level for Arawn's Fire / Cthonic Accretion so a
--      Heretic with even 1 spec point sees at least one spell.
-- =====================================================================

-- ---- Fix A: remove broken Heretic Enhancement baseline ---------------
-- The upstream `Heretic Enhancement` baseline SpellLine (ClassIDHint=33,
-- IsBaseLine=1, KeyName='Heretic Enhancement') exists but is not seeded
-- with any spells in linexspell. The Specialization.GetSpellLinesForLiving
-- code prefers class-hinted baseline over generic fallback, so this
-- empty row leaves Heretic Enhancement effectively empty in-game.
-- Removing it lets the generic 'Enhancement' baseline (26 spells, ClassIDHint=0)
-- be used by the Heretic class.
DELETE FROM spellline WHERE KeyName = 'Heretic Enhancement' AND IsBaseLine = 1;

-- Same potential issue for Rejuvenation - check & remove if found broken
DELETE FROM spellline WHERE KeyName = 'Heretic Rejuvenation' AND IsBaseLine = 1;

-- ---- Fix B: lower Arawn's Fire spell acquisition levels -------------
-- Make the lowest tier of each Arawn's Fire spell family acquire at
-- level 1-2 so a fresh Heretic with 1 spec point gets immediate feedback.
UPDATE linexspell SET Level = 1 WHERE LineXSpell_ID = 'lxs_af_100000'; -- Arawn's Singe (was 4)
UPDATE linexspell SET Level = 1 WHERE LineXSpell_ID = 'lxs_af_100020'; -- Blazing Flow (was 3)
UPDATE linexspell SET Level = 1 WHERE LineXSpell_ID = 'lxs_af_100050'; -- Flickering Embers (was 5)
UPDATE linexspell SET Level = 2 WHERE LineXSpell_ID = 'lxs_af_100040'; -- Fiery Grasp (was 6)
UPDATE linexspell SET Level = 3 WHERE LineXSpell_ID = 'lxs_af_100010'; -- Lava Spate (was 9)
UPDATE linexspell SET Level = 5 WHERE LineXSpell_ID = 'lxs_af_100030'; -- Glistening Blaze (was 36) — high tier, push to 30 instead of 36

-- Tier 2 of each family - keep them reachable at moderate spec
UPDATE linexspell SET Level = 5  WHERE LineXSpell_ID = 'lxs_af_100001'; -- Arawn's Torch (was 12)
UPDATE linexspell SET Level = 5  WHERE LineXSpell_ID = 'lxs_af_100021'; -- Blazing Stream (was 11)
UPDATE linexspell SET Level = 7  WHERE LineXSpell_ID = 'lxs_af_100051'; -- Glowing Embers (was 13)
UPDATE linexspell SET Level = 8  WHERE LineXSpell_ID = 'lxs_af_100041'; -- Fiery Clutch (was 14)
UPDATE linexspell SET Level = 7  WHERE LineXSpell_ID = 'lxs_af_100060'; -- Arawn's Grip insta-snare (was 27)

-- ---- Fix B': same for Cthonic Accretion --------------------------------
UPDATE linexspell SET Level = 1  WHERE LineXSpell_ID = 'lxs_ca_100100'; -- Chthonic Vigor (was 5)
UPDATE linexspell SET Level = 1  WHERE LineXSpell_ID = 'lxs_ca_100120'; -- Diabolic Thorns (was 6)
UPDATE linexspell SET Level = 2  WHERE LineXSpell_ID = 'lxs_ca_100110'; -- Kindled Shield (was 7)
UPDATE linexspell SET Level = 3  WHERE LineXSpell_ID = 'lxs_ca_100140'; -- Infernal Carve (was 11)
UPDATE linexspell SET Level = 5  WHERE LineXSpell_ID = 'lxs_ca_100150'; -- Arawn's Precision (was 10)
UPDATE linexspell SET Level = 10 WHERE LineXSpell_ID = 'lxs_ca_100130'; -- Buffer of Steam (was 25)

-- Tier 2
UPDATE linexspell SET Level = 6  WHERE LineXSpell_ID = 'lxs_ca_100101'; -- Chthonic Strength (was 10)
UPDATE linexspell SET Level = 8  WHERE LineXSpell_ID = 'lxs_ca_100121'; -- Diabolic Needles (was 12)
UPDATE linexspell SET Level = 8  WHERE LineXSpell_ID = 'lxs_ca_100111'; -- Kindled Aegis (was 14)

-- =====================================================================
-- Verify with:
--   SELECT LineXSpell_ID, LineName, Level FROM linexspell
--     WHERE PackageID = 'Heretic_Live' AND Level <= 5 ORDER BY LineName, Level;
--   SELECT KeyName, IsBaseLine, ClassIDHint FROM spellline
--     WHERE KeyName LIKE 'Heretic %';
-- Expected:
--   - 'Heretic Enhancement' baseline GONE (only 'Heretic Enhancement Spec' remains)
--   - Linexspell shows ~10 spells at level 1-5 for both Arawn's Fire and Cthonic Accretion
-- AFTER applying this SQL, RESTART the gameserver to reload SkillBase caches.
-- =====================================================================

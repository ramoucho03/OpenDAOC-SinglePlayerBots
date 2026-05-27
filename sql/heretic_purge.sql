-- =====================================================================
-- Heretic data purge — clears the runway for EoD's version
-- =====================================================================
-- Why this file exists:
--   Upstream OpenDAoC ships an incomplete Heretic class (136 LineXSpell
--   rows, 0 SpecXAbility, no realm abilities) — we used to patch over it
--   with heretic_live.sql (custom REPLACE INTO / INSERT INTO with
--   PackageID='Heretic_Live'). Eve-of-Darkness/db-public ships a complete
--   Heretic (309 LineXSpell, 287 Spell, 5 SpecXAbility, 31 ClassXRealmAbility
--   incl. Fanaticism RR5).
--
--   To let EoD's data win, we must DELETE the existing rows first — the
--   50_eveofdarkness_fill.sql migration runs INSERT IGNORE which would
--   otherwise silently skip every Heretic row whose PK already exists.
--
-- Apply order: between Larogoth (10-40) and EoD fill (50). Strictly
-- between — never before combined.sql has loaded.
--
-- Idempotency: every DELETE is idempotent by definition. Re-runs are
-- harmless (rows already gone). The migration is wrapped in the same
-- checksum tracking as the others via the entrypoint's marker table.
-- =====================================================================

SET FOREIGN_KEY_CHECKS = 0;

-- ---- 1. Our custom heretic_live.sql rows (PackageID tagged) ----------
-- Cleans up anything we added via the old heretic_live.sql so EoD has a
-- pristine slate. PackageID = 'Heretic_Live' identifies them precisely.
DELETE FROM Spell      WHERE PackageID = 'Heretic_Live';
DELETE FROM LineXSpell WHERE PackageID = 'Heretic_Live';

-- ---- 2. Heretic-specific SpellLines ---------------------------------
-- 'Heretic Rejuvenation Spec' / 'Heretic Enhancement Spec' are the two
-- Heretic-unique spec lines (the bare 'Rejuvenation' / 'Enhancement'
-- lines are SHARED with Cleric/Friar — we DO NOT delete those).
-- 'Heretic Rejuvenation' / 'Heretic Enhancement' are upstream's broken
-- baselines (0 spells in LineXSpell) that the old patch worked around.
DELETE FROM LineXSpell WHERE LineName IN (
    'Heretic Rejuvenation Spec',
    'Heretic Enhancement Spec',
    'Heretic Rejuvenation',
    'Heretic Enhancement'
);
DELETE FROM SpellLine WHERE KeyName IN (
    'Heretic Rejuvenation Spec',
    'Heretic Enhancement Spec',
    'Heretic Rejuvenation',
    'Heretic Enhancement'
);

-- ---- 3. Heretic-only career spec + training abilities ----------------
-- ClassID 33 is Heretic. ClassXSpecialization rows for ClassID=33 are
-- entirely safe to drop — they wire which speclines the class trains in,
-- and EoD reseeds the same list (cross-checked: 10 rows both sides).
DELETE FROM ClassXSpecialization WHERE ClassID = 33;
DELETE FROM SpecXAbility WHERE Spec = 'HereticCareer';
DELETE FROM Specialization WHERE KeyName = 'HereticCareer';

-- ---- 4. Heretic realm abilities --------------------------------------
-- OpenDAoC ships 0 rows here for Heretic; EoD ships 31 (RR5 Fanaticism
-- + augmented stats + the standard hybrid-melee RA tree). Cleared just
-- in case the EoD fill re-runs on a DB where someone hand-edited rows.
DELETE FROM ClassXRealmAbility_Atlas WHERE CharClass = 33;

-- ---- 5. Specific custom alias specs we created earlier ----------------
-- The old heretic_live.sql tried two strategies before settling on the
-- rebranching approach; both left orphan 'Arawn's Fire' / 'Cthonic
-- Accretion' rows in some DBs. Belt-and-suspenders cleanup.
DELETE FROM ClassXSpecialization WHERE SpecKeyName IN ('Arawn\'s Fire', 'Cthonic Accretion');
DELETE FROM SpellLine            WHERE KeyName     IN ('Arawn\'s Fire', 'Cthonic Accretion');
DELETE FROM Specialization       WHERE KeyName     IN ('Arawn\'s Fire', 'Cthonic Accretion');

SET FOREIGN_KEY_CHECKS = 1;

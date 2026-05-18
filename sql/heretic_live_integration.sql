-- =====================================================================
-- Heretic Live integration — Arawn's Fire + Cthonic Accretion speclines
-- =====================================================================
-- Adds the two specialization lines that the Heretic class has on the
-- live DAoC servers:
--   * Arawn's Fire (offensive DD/snare/DoT, specline of Rejuvenation)
--   * Cthonic Accretion (self buffs/dmg-add/absorb, specline of Enhancement)
--
-- Required matching C# code changes (already in place):
--   - SkillConstants.cs  : Specs.Arawns_Fire, Specs.Cthonic_Accretion
--   - eProperty.cs       : Skill_Arawns_Fire = 116, Skill_Cthonic_Accretion = 117
--   - SkillBase.cs       : m_specToSkill mapping for both
--
-- *** CRITICAL: after running this SQL, the gameserver MUST be RESTARTED ***
-- The SkillBase caches all class specs at startup (m_specsByClass).
-- DB-only changes are invisible until the server reloads the cache.
--
-- Uses REPLACE INTO with full column structure matching upstream
-- /tmp/opendaoc-database/opendaoc-db-core/*.sql so columns map correctly.
-- SpellID range: 100000-100199 (PackageID = 'Heretic_Live')
--
-- ClientEffect/Icon IDs reused from known-working spells in upstream:
--   Arawn's Inferno line  (mono ramping DD)   : 309   (Fiery Maelstrom)
--   Lava line             (AoE DD+snare)      : 308   (Fiery Maelstrom Minor)
--   Blazing line          (mono DD+snare)     : 9644  (95 Heat DD)
--   Channeled Blaze       (uninterruptible)   : 310   (Fiery Maelstrom Major)
--   Fiery Grasp           (instant DD)        : 312   (Fiery Bolt)
--   Flickering Embers     (DoT)               : 11263 (Flickering Flame)
--   Insta-snare           (Arawn's Grip)      : 9211  (Crippling Pain snare)
--   Chthonic Str/Con      (self buff)         : 10041 (Strengthen Pack)
--   Kindled Shield        (AF buff)           : 1     (Amethyst Shield)
--   Diabolic Thorns       (Damage Add)        : 10200 (Bone Spurs)
--   Buffer of Steam       (Ablative absorb)   : 10087 (Bone Skin)
--   Infernal Carve        (Melee DPS buff)    : 1566  (Refiner's Strength)
--   Arawn's Precision     (Piercing Magic)    : 9221  (Facilitate Painworking)
-- =====================================================================

-- ---- Idempotent cleanup (safe to re-run) -----------------------------
DELETE FROM linexspell WHERE LineName IN ('Arawn\'s Fire', 'Cthonic Accretion');
DELETE FROM spellline WHERE Spec IN ('Arawn\'s Fire', 'Cthonic Accretion');
DELETE FROM specialization WHERE KeyName IN ('Arawn\'s Fire', 'Cthonic Accretion');
DELETE FROM classxspecialization WHERE ClassID = 33 AND SpecKeyName IN ('Arawn\'s Fire', 'Cthonic Accretion');
DELETE FROM spell WHERE PackageID = 'Heretic_Live';

-- ---- specialization (full column list per upstream schema) -----------
REPLACE INTO `specialization` (`Specialization_ID`, `KeyName`, `Name`, `Icon`, `Description`, `Implementation`, `LastTimeRowUpdated`) VALUES
('', 'Arawn\'s Fire',      'Arawn\'s Fire',      0, 'Heretic offensive chants and focus damage.', NULL, NOW()),
('', 'Cthonic Accretion',  'Cthonic Accretion',  0, 'Heretic self buffs, damage-add and absorb.', NULL, NOW());

-- ---- spellline (full column list per upstream schema) ----------------
REPLACE INTO `spellline` (`KeyName`, `Name`, `Spec`, `IsBaseLine`, `PackageID`, `ClassIDHint`, `LastTimeRowUpdated`) VALUES
('Arawn\'s Fire',      'Arawn\'s Fire',      'Arawn\'s Fire',      0, 'Heretic_Live', 33, NOW()),
('Cthonic Accretion',  'Cthonic Accretion',  'Cthonic Accretion',  0, 'Heretic_Live', 33, NOW());

-- ---- classxspecialization (Heretic = 33) -----------------------------
REPLACE INTO `classxspecialization` (`ClassID`, `SpecKeyName`, `LevelAcquired`, `LastTimeRowUpdated`) VALUES
(33, 'Arawn\'s Fire',     5, NOW()),
(33, 'Cthonic Accretion', 5, NOW());

-- ====================================================================
-- ARAWN'S FIRE SPELLS  (SpellID 100000-100099)
-- Full column list per upstream spell schema (NOTE: TooltipId not TooltipID)
-- ====================================================================

-- ---- Arawn's Inferno line: channeled mono DD with ramping ---------------
REPLACE INTO `spell` (`Spell_ID`, `SpellID`, `ClientEffect`, `Icon`, `Name`, `Description`, `Target`, `Range`, `Power`, `CastTime`, `Damage`, `DamageType`, `Type`, `Duration`, `Frequency`, `Pulse`, `PulsePower`, `Radius`, `RecastDelay`, `ResurrectHealth`, `ResurrectMana`, `Value`, `Concentration`, `LifeDrainReturn`, `AmnesiaChance`, `Message1`, `Message2`, `Message3`, `Message4`, `InstrumentRequirement`, `SpellGroup`, `EffectGroup`, `SubSpellID`, `MoveCast`, `Uninterruptible`, `IsPrimary`, `IsSecondary`, `AllowBolt`, `SharedTimerGroup`, `PackageID`, `IsFocus`, `TooltipId`, `LastTimeRowUpdated`) VALUES
('arawns_singe_l4',     100000, 309, 309, 'Arawn\'s Singe',     'Channeled fire damage that ramps up over time.', 'Enemy', 1500, 5,  3.0,  10, 13, 'RampingDamageFocus', 16000, 1500, 1, 4, 0, 0, 0, 0, 0, 0, 50, 250, '', '', '', '', 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 'Heretic_Live', 1, 0, NOW()),
('arawns_torch_l12',    100001, 309, 309, 'Arawn\'s Torch',     'Channeled fire damage that ramps up over time.', 'Enemy', 1500, 12, 3.0,  20, 13, 'RampingDamageFocus', 16000, 1500, 1, 7, 0, 0, 0, 0, 0, 0, 50, 250, '', '', '', '', 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 'Heretic_Live', 1, 0, NOW()),
('arawns_flame_l20',    100002, 309, 309, 'Arawn\'s Flame',     'Channeled fire damage that ramps up over time.', 'Enemy', 1500, 20, 3.0,  32, 13, 'RampingDamageFocus', 16000, 1500, 1, 10, 0, 0, 0, 0, 0, 0, 50, 250, '', '', '', '', 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 'Heretic_Live', 1, 0, NOW()),
('arawns_blaze_l28',    100003, 309, 309, 'Arawn\'s Blaze',     'Channeled fire damage that ramps up over time.', 'Enemy', 1500, 28, 3.0,  48, 13, 'RampingDamageFocus', 16000, 1500, 1, 14, 0, 0, 0, 0, 0, 0, 50, 250, '', '', '', '', 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 'Heretic_Live', 1, 0, NOW()),
('arawns_pyre_l36',     100004, 309, 309, 'Arawn\'s Pyre',      'Channeled fire damage that ramps up over time.', 'Enemy', 1500, 36, 3.0,  68, 13, 'RampingDamageFocus', 16000, 1500, 1, 19, 0, 0, 0, 0, 0, 0, 50, 250, '', '', '', '', 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 'Heretic_Live', 1, 0, NOW()),
('arawns_inferno_l44',  100005, 309, 309, 'Arawn\'s Inferno',   'Channeled fire damage that ramps up over time.', 'Enemy', 1500, 44, 3.0,  92, 13, 'RampingDamageFocus', 16000, 1500, 1, 24, 0, 0, 0, 0, 0, 0, 50, 250, '', '', '', '', 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 'Heretic_Live', 1, 0, NOW());

-- ---- Lava line: AoE channeled DD + snare ---------------
REPLACE INTO `spell` (`Spell_ID`, `SpellID`, `ClientEffect`, `Icon`, `Name`, `Description`, `Target`, `Range`, `Power`, `CastTime`, `Damage`, `DamageType`, `Type`, `Duration`, `Frequency`, `Pulse`, `PulsePower`, `Radius`, `RecastDelay`, `ResurrectHealth`, `ResurrectMana`, `Value`, `Concentration`, `LifeDrainReturn`, `AmnesiaChance`, `Message1`, `Message2`, `Message3`, `Message4`, `InstrumentRequirement`, `SpellGroup`, `EffectGroup`, `SubSpellID`, `MoveCast`, `Uninterruptible`, `IsPrimary`, `IsSecondary`, `AllowBolt`, `SharedTimerGroup`, `PackageID`, `IsFocus`, `TooltipId`, `LastTimeRowUpdated`) VALUES
('lava_spate_l9',       100010, 308, 308, 'Lava Spate',        'Channeled fire AoE around the target with a slow.', 'Enemy', 1500, 9,  3.0,  6,  13, 'RampingDamageFocus', 16000, 1500, 1, 5, 350, 0, 0, 0, 50, 0, 30, 150, '', '', '', '', 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 'Heretic_Live', 1, 0, NOW()),
('lava_torrent_l16',    100011, 308, 308, 'Lava Torrent',      'Channeled fire AoE around the target with a slow.', 'Enemy', 1500, 16, 3.0,  10, 13, 'RampingDamageFocus', 16000, 1500, 1, 8, 350, 0, 0, 0, 50, 0, 30, 150, '', '', '', '', 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 'Heretic_Live', 1, 0, NOW()),
('lava_river_l23',      100012, 308, 308, 'Lava River',        'Channeled fire AoE around the target with a slow.', 'Enemy', 1500, 23, 3.0,  14, 13, 'RampingDamageFocus', 16000, 1500, 1, 11, 350, 0, 0, 0, 50, 0, 30, 150, '', '', '', '', 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 'Heretic_Live', 1, 0, NOW()),
('lava_flood_l31',      100013, 308, 308, 'Lava Flood',        'Channeled fire AoE around the target with a slow.', 'Enemy', 1500, 31, 3.0,  18, 13, 'RampingDamageFocus', 16000, 1500, 1, 14, 350, 0, 0, 0, 50, 0, 30, 150, '', '', '', '', 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 'Heretic_Live', 1, 0, NOW()),
('lava_deluge_l39',     100014, 308, 308, 'Lava Deluge',       'Channeled fire AoE around the target with a slow.', 'Enemy', 1500, 39, 3.0,  24, 13, 'RampingDamageFocus', 16000, 1500, 1, 18, 350, 0, 0, 0, 50, 0, 30, 150, '', '', '', '', 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 'Heretic_Live', 1, 0, NOW()),
('lava_avalanche_l47',  100015, 308, 308, 'Lava Avalanche',    'Channeled fire AoE around the target with a slow.', 'Enemy', 1500, 47, 3.0,  30, 13, 'RampingDamageFocus', 16000, 1500, 1, 22, 350, 0, 0, 0, 50, 0, 30, 150, '', '', '', '', 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 'Heretic_Live', 1, 0, NOW());

-- ---- Blazing line: mono focus DD + snare ---------------
REPLACE INTO `spell` (`Spell_ID`, `SpellID`, `ClientEffect`, `Icon`, `Name`, `Description`, `Target`, `Range`, `Power`, `CastTime`, `Damage`, `DamageType`, `Type`, `Duration`, `Frequency`, `Pulse`, `PulsePower`, `Radius`, `RecastDelay`, `ResurrectHealth`, `ResurrectMana`, `Value`, `Concentration`, `LifeDrainReturn`, `AmnesiaChance`, `Message1`, `Message2`, `Message3`, `Message4`, `InstrumentRequirement`, `SpellGroup`, `EffectGroup`, `SubSpellID`, `MoveCast`, `Uninterruptible`, `IsPrimary`, `IsSecondary`, `AllowBolt`, `SharedTimerGroup`, `PackageID`, `IsFocus`, `TooltipId`, `LastTimeRowUpdated`) VALUES
('blazing_flow_l3',     100020, 9644, 9644, 'Blazing Flow',      'Slows the target while burning it. Focus.',     'Enemy', 1500, 3,  3.0,  8,  13, 'HereticDamageSpeedDecrease', 8000, 2000, 1, 3, 0, 0, 0, 0, 50, 0, 0, 0, '', '', '', '', 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 'Heretic_Live', 1, 0, NOW()),
('blazing_stream_l11',  100021, 9644, 9644, 'Blazing Stream',    'Slows the target while burning it. Focus.',     'Enemy', 1500, 11, 3.0,  14, 13, 'HereticDamageSpeedDecrease', 8000, 2000, 1, 6, 0, 0, 0, 0, 50, 0, 0, 0, '', '', '', '', 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 'Heretic_Live', 1, 0, NOW()),
('blazing_torrent_l18', 100022, 9644, 9644, 'Blazing Torrent',   'Slows the target while burning it. Focus.',     'Enemy', 1500, 18, 3.0,  20, 13, 'HereticDamageSpeedDecrease', 8000, 2000, 1, 9, 0, 0, 0, 0, 50, 0, 0, 0, '', '', '', '', 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 'Heretic_Live', 1, 0, NOW()),
('blazing_river_l25',   100023, 9644, 9644, 'Blazing River',     'Slows the target while burning it. Focus.',     'Enemy', 1500, 25, 3.0,  28, 13, 'HereticDamageSpeedDecrease', 8000, 2000, 1, 12, 0, 0, 0, 0, 50, 0, 0, 0, '', '', '', '', 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 'Heretic_Live', 1, 0, NOW()),
('blazing_surge_l33',   100024, 9644, 9644, 'Blazing Surge',     'Slows the target while burning it. Focus.',     'Enemy', 1500, 33, 3.0,  38, 13, 'HereticDamageSpeedDecrease', 8000, 2000, 1, 16, 0, 0, 0, 0, 50, 0, 0, 0, '', '', '', '', 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 'Heretic_Live', 1, 0, NOW()),
('blazing_flood_l41',   100025, 9644, 9644, 'Blazing Flood',     'Slows the target while burning it. Focus.',     'Enemy', 1500, 41, 3.0,  50, 13, 'HereticDamageSpeedDecrease', 8000, 2000, 1, 20, 0, 0, 0, 0, 50, 0, 0, 0, '', '', '', '', 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 'Heretic_Live', 1, 0, NOW());

-- ---- Channeled Blaze: uninterruptible mono DD ---------------------------
REPLACE INTO `spell` (`Spell_ID`, `SpellID`, `ClientEffect`, `Icon`, `Name`, `Description`, `Target`, `Range`, `Power`, `CastTime`, `Damage`, `DamageType`, `Type`, `Duration`, `Frequency`, `Pulse`, `PulsePower`, `Radius`, `RecastDelay`, `ResurrectHealth`, `ResurrectMana`, `Value`, `Concentration`, `LifeDrainReturn`, `AmnesiaChance`, `Message1`, `Message2`, `Message3`, `Message4`, `InstrumentRequirement`, `SpellGroup`, `EffectGroup`, `SubSpellID`, `MoveCast`, `Uninterruptible`, `IsPrimary`, `IsSecondary`, `AllowBolt`, `SharedTimerGroup`, `PackageID`, `IsFocus`, `TooltipId`, `LastTimeRowUpdated`) VALUES
('glistening_blaze_l36', 100030, 310, 310, 'Glistening Blaze',  'Uninterruptible channeled fire damage.', 'Enemy', 1500, 32, 3.0,  90,  13, 'RampingDamageFocus', 33000, 1500, 1, 16, 0, 0, 0, 0, 0, 0, 0, 0, '', '', '', '', 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 'Heretic_Live', 1, 0, NOW()),
('whirling_blaze_l42',   100031, 310, 310, 'Whirling Blaze',    'Uninterruptible channeled fire damage.', 'Enemy', 1500, 38, 3.0,  97,  13, 'RampingDamageFocus', 33000, 1500, 1, 19, 0, 0, 0, 0, 0, 0, 0, 0, '', '', '', '', 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 'Heretic_Live', 1, 0, NOW()),
('torrential_blaze_l48', 100032, 310, 310, 'Torrential Blaze',  'Uninterruptible channeled fire damage.', 'Enemy', 1500, 44, 3.0,  120, 13, 'RampingDamageFocus', 33000, 1500, 1, 22, 0, 0, 0, 0, 0, 0, 0, 0, '', '', '', '', 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 'Heretic_Live', 1, 0, NOW());

-- ---- Fiery Grasp line: instant DD ---------------------------------------
REPLACE INTO `spell` (`Spell_ID`, `SpellID`, `ClientEffect`, `Icon`, `Name`, `Description`, `Target`, `Range`, `Power`, `CastTime`, `Damage`, `DamageType`, `Type`, `Duration`, `Frequency`, `Pulse`, `PulsePower`, `Radius`, `RecastDelay`, `ResurrectHealth`, `ResurrectMana`, `Value`, `Concentration`, `LifeDrainReturn`, `AmnesiaChance`, `Message1`, `Message2`, `Message3`, `Message4`, `InstrumentRequirement`, `SpellGroup`, `EffectGroup`, `SubSpellID`, `MoveCast`, `Uninterruptible`, `IsPrimary`, `IsSecondary`, `AllowBolt`, `SharedTimerGroup`, `PackageID`, `IsFocus`, `TooltipId`, `LastTimeRowUpdated`) VALUES
('fiery_grasp_l6',         100040, 312, 312, 'Fiery Grasp',         'Instant fire damage.', 'Enemy', 1500, 8,  2.8,  18,  13, 'DirectDamage', 0, 0, 0, 0, 0, 12000, 0, 0, 0, 0, 0, 0, '', '', '', '', 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 'Heretic_Live', 0, 0, NOW()),
('fiery_clutch_l14',       100041, 312, 312, 'Fiery Clutch',        'Instant fire damage.', 'Enemy', 1500, 14, 2.8,  34,  13, 'DirectDamage', 0, 0, 0, 0, 0, 12000, 0, 0, 0, 0, 0, 0, '', '', '', '', 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 'Heretic_Live', 0, 0, NOW()),
('fiery_grip_l22',         100042, 312, 312, 'Fiery Grip',          'Instant fire damage.', 'Enemy', 1500, 22, 2.8,  56,  13, 'DirectDamage', 0, 0, 0, 0, 0, 12000, 0, 0, 0, 0, 0, 0, '', '', '', '', 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 'Heretic_Live', 0, 0, NOW()),
('fiery_seize_l30',        100043, 312, 312, 'Fiery Seize',         'Instant fire damage.', 'Enemy', 1500, 30, 2.8,  84,  13, 'DirectDamage', 0, 0, 0, 0, 0, 12000, 0, 0, 0, 0, 0, 0, '', '', '', '', 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 'Heretic_Live', 0, 0, NOW()),
('fiery_hold_l38',         100044, 312, 312, 'Fiery Hold',          'Instant fire damage.', 'Enemy', 1500, 38, 2.8,  108, 13, 'DirectDamage', 0, 0, 0, 0, 0, 12000, 0, 0, 0, 0, 0, 0, '', '', '', '', 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 'Heretic_Live', 0, 0, NOW()),
('fiery_stranglehold_l46', 100045, 312, 312, 'Fiery Stranglehold',  'Instant fire damage.', 'Enemy', 1500, 46, 2.8,  140, 13, 'DirectDamage', 0, 0, 0, 0, 0, 12000, 0, 0, 0, 0, 0, 0, '', '', '', '', 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 'Heretic_Live', 0, 0, NOW());

-- ---- Flickering Embers line: DoT ----------------------------------------
REPLACE INTO `spell` (`Spell_ID`, `SpellID`, `ClientEffect`, `Icon`, `Name`, `Description`, `Target`, `Range`, `Power`, `CastTime`, `Damage`, `DamageType`, `Type`, `Duration`, `Frequency`, `Pulse`, `PulsePower`, `Radius`, `RecastDelay`, `ResurrectHealth`, `ResurrectMana`, `Value`, `Concentration`, `LifeDrainReturn`, `AmnesiaChance`, `Message1`, `Message2`, `Message3`, `Message4`, `InstrumentRequirement`, `SpellGroup`, `EffectGroup`, `SubSpellID`, `MoveCast`, `Uninterruptible`, `IsPrimary`, `IsSecondary`, `AllowBolt`, `SharedTimerGroup`, `PackageID`, `IsFocus`, `TooltipId`, `LastTimeRowUpdated`) VALUES
('flickering_embers_l5',    100050, 11263, 11263, 'Flickering Embers',    'Damage over time, fire.', 'Enemy', 700, 5,  3.0,  8,  13, 'HereticDamageOverTime', 20000, 4000, 1, 1, 0, 0, 0, 0, 0, 0, 0, 0, '', '', '', '', 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 'Heretic_Live', 0, 0, NOW()),
('glowing_embers_l13',      100051, 11263, 11263, 'Glowing Embers',       'Damage over time, fire.', 'Enemy', 700, 13, 3.0,  16, 13, 'HereticDamageOverTime', 20000, 4000, 1, 2, 0, 0, 0, 0, 0, 0, 0, 0, '', '', '', '', 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 'Heretic_Live', 0, 0, NOW()),
('burning_embers_l21',      100052, 11263, 11263, 'Burning Embers',       'Damage over time, fire.', 'Enemy', 700, 21, 3.0,  26, 13, 'HereticDamageOverTime', 20000, 4000, 1, 4, 0, 0, 0, 0, 0, 0, 0, 0, '', '', '', '', 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 'Heretic_Live', 0, 0, NOW()),
('scorching_embers_l30',    100053, 11263, 11263, 'Scorching Embers',     'Damage over time, fire.', 'Enemy', 700, 30, 3.0,  38, 13, 'HereticDamageOverTime', 20000, 4000, 1, 6, 0, 0, 0, 0, 0, 0, 0, 0, '', '', '', '', 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 'Heretic_Live', 0, 0, NOW()),
('searing_embers_l39',      100054, 11263, 11263, 'Searing Embers',       'Damage over time, fire.', 'Enemy', 700, 39, 3.0,  52, 13, 'HereticDamageOverTime', 20000, 4000, 1, 9, 0, 0, 0, 0, 0, 0, 0, 0, '', '', '', '', 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 'Heretic_Live', 0, 0, NOW()),
('incinerating_embers_l48', 100055, 11263, 11263, 'Incinerating Embers',  'Damage over time, fire.', 'Enemy', 700, 48, 3.0,  70, 13, 'HereticDamageOverTime', 20000, 4000, 1, 12, 0, 0, 0, 0, 0, 0, 0, 0, '', '', '', '', 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 'Heretic_Live', 0, 0, NOW());

-- ---- Insta-snare --------------------------------------------------------
REPLACE INTO `spell` (`Spell_ID`, `SpellID`, `ClientEffect`, `Icon`, `Name`, `Description`, `Target`, `Range`, `Power`, `CastTime`, `Damage`, `DamageType`, `Type`, `Duration`, `Frequency`, `Pulse`, `PulsePower`, `Radius`, `RecastDelay`, `ResurrectHealth`, `ResurrectMana`, `Value`, `Concentration`, `LifeDrainReturn`, `AmnesiaChance`, `Message1`, `Message2`, `Message3`, `Message4`, `InstrumentRequirement`, `SpellGroup`, `EffectGroup`, `SubSpellID`, `MoveCast`, `Uninterruptible`, `IsPrimary`, `IsSecondary`, `AllowBolt`, `SharedTimerGroup`, `PackageID`, `IsFocus`, `TooltipId`, `LastTimeRowUpdated`) VALUES
('arawns_grip_l27', 100060, 9211, 9211, 'Arawn\'s Grip', 'Instant snare. Slows the target for 5 seconds.', 'Enemy', 1500, 14, 0, 0, 13, 'HereticSpeedDecrease', 5000, 0, 0, 0, 0, 30000, 0, 0, 50, 0, 0, 0, '', '', '', '', 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 'Heretic_Live', 0, 0, NOW());

-- ====================================================================
-- CTHONIC ACCRETION SPELLS  (SpellID 100100-100199)
-- ====================================================================

-- ---- Chthonic Vigor → Might: Self Str/Con buff -------------------------
REPLACE INTO `spell` (`Spell_ID`, `SpellID`, `ClientEffect`, `Icon`, `Name`, `Description`, `Target`, `Range`, `Power`, `CastTime`, `Damage`, `DamageType`, `Type`, `Duration`, `Frequency`, `Pulse`, `PulsePower`, `Radius`, `RecastDelay`, `ResurrectHealth`, `ResurrectMana`, `Value`, `Concentration`, `LifeDrainReturn`, `AmnesiaChance`, `Message1`, `Message2`, `Message3`, `Message4`, `InstrumentRequirement`, `SpellGroup`, `EffectGroup`, `SubSpellID`, `MoveCast`, `Uninterruptible`, `IsPrimary`, `IsSecondary`, `AllowBolt`, `SharedTimerGroup`, `PackageID`, `IsFocus`, `TooltipId`, `LastTimeRowUpdated`) VALUES
('chthonic_vigor_l5',          100100, 10041, 10041, 'Chthonic Vigor',         'Self Strength and Constitution buff.', 'Self', 0, 4,  3.0, 0, 0, 'StrengthConstitutionBuff', 1200000, 0, 0, 0, 0, 0, 0, 0, 9,  0, 0, 0, '', '', '', '', 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 'Heretic_Live', 0, 0, NOW()),
('chthonic_strength_l10',      100101, 10041, 10041, 'Chthonic Strength',      'Self Strength and Constitution buff.', 'Self', 0, 8,  3.0, 0, 0, 'StrengthConstitutionBuff', 1200000, 0, 0, 0, 0, 0, 0, 0, 21, 0, 0, 0, '', '', '', '', 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 'Heretic_Live', 0, 0, NOW()),
('chthonic_fortification_l16', 100102, 10041, 10041, 'Chthonic Fortification', 'Self Strength and Constitution buff.', 'Self', 0, 12, 3.0, 0, 0, 'StrengthConstitutionBuff', 1200000, 0, 0, 0, 0, 0, 0, 0, 33, 0, 0, 0, '', '', '', '', 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 'Heretic_Live', 0, 0, NOW()),
('chthonic_focus_l23',         100103, 10041, 10041, 'Chthonic Focus',         'Self Strength and Constitution buff.', 'Self', 0, 16, 3.0, 0, 0, 'StrengthConstitutionBuff', 1200000, 0, 0, 0, 0, 0, 0, 0, 45, 0, 0, 0, '', '', '', '', 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 'Heretic_Live', 0, 0, NOW()),
('chthonic_power_l32',         100104, 10041, 10041, 'Chthonic Power',         'Self Strength and Constitution buff.', 'Self', 0, 20, 3.0, 0, 0, 'StrengthConstitutionBuff', 1200000, 0, 0, 0, 0, 0, 0, 0, 57, 0, 0, 0, '', '', '', '', 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 'Heretic_Live', 0, 0, NOW()),
('chthonic_force_l40',         100105, 10041, 10041, 'Chthonic Force',         'Self Strength and Constitution buff.', 'Self', 0, 24, 3.0, 0, 0, 'StrengthConstitutionBuff', 1200000, 0, 0, 0, 0, 0, 0, 0, 69, 0, 0, 0, '', '', '', '', 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 'Heretic_Live', 0, 0, NOW()),
('chthonic_might_l50',         100106, 10041, 10041, 'Chthonic Might',         'Self Strength and Constitution buff.', 'Self', 0, 28, 3.0, 0, 0, 'StrengthConstitutionBuff', 1200000, 0, 0, 0, 0, 0, 0, 0, 75, 0, 0, 0, '', '', '', '', 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 'Heretic_Live', 0, 0, NOW());

-- ---- Kindled Shield → Molten Barricade: Self AF buff -------------------
REPLACE INTO `spell` (`Spell_ID`, `SpellID`, `ClientEffect`, `Icon`, `Name`, `Description`, `Target`, `Range`, `Power`, `CastTime`, `Damage`, `DamageType`, `Type`, `Duration`, `Frequency`, `Pulse`, `PulsePower`, `Radius`, `RecastDelay`, `ResurrectHealth`, `ResurrectMana`, `Value`, `Concentration`, `LifeDrainReturn`, `AmnesiaChance`, `Message1`, `Message2`, `Message3`, `Message4`, `InstrumentRequirement`, `SpellGroup`, `EffectGroup`, `SubSpellID`, `MoveCast`, `Uninterruptible`, `IsPrimary`, `IsSecondary`, `AllowBolt`, `SharedTimerGroup`, `PackageID`, `IsFocus`, `TooltipId`, `LastTimeRowUpdated`) VALUES
('kindled_shield_l7',     100110, 1, 1, 'Kindled Shield',     'Self Armor Factor buff.', 'Self', 0, 5,  3.0, 0, 0, 'ArmorFactorBuff', 1200000, 0, 0, 0, 0, 0, 0, 0, 36,  0, 0, 0, '', '', '', '', 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 'Heretic_Live', 0, 0, NOW()),
('kindled_aegis_l14',     100111, 1, 1, 'Kindled Aegis',      'Self Armor Factor buff.', 'Self', 0, 10, 3.0, 0, 0, 'ArmorFactorBuff', 1200000, 0, 0, 0, 0, 0, 0, 0, 84,  0, 0, 0, '', '', '', '', 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 'Heretic_Live', 0, 0, NOW()),
('molten_shield_l21',     100112, 1, 1, 'Molten Shield',      'Self Armor Factor buff.', 'Self', 0, 14, 3.0, 0, 0, 'ArmorFactorBuff', 1200000, 0, 0, 0, 0, 0, 0, 0, 132, 0, 0, 0, '', '', '', '', 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 'Heretic_Live', 0, 0, NOW()),
('molten_aegis_l29',      100113, 1, 1, 'Molten Aegis',       'Self Armor Factor buff.', 'Self', 0, 18, 3.0, 0, 0, 'ArmorFactorBuff', 1200000, 0, 0, 0, 0, 0, 0, 0, 180, 0, 0, 0, '', '', '', '', 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 'Heretic_Live', 0, 0, NOW()),
('molten_bulwark_l36',    100114, 1, 1, 'Molten Bulwark',     'Self Armor Factor buff.', 'Self', 0, 22, 3.0, 0, 0, 'ArmorFactorBuff', 1200000, 0, 0, 0, 0, 0, 0, 0, 240, 0, 0, 0, '', '', '', '', 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 'Heretic_Live', 0, 0, NOW()),
('molten_rampart_l43',    100115, 1, 1, 'Molten Rampart',     'Self Armor Factor buff.', 'Self', 0, 26, 3.0, 0, 0, 'ArmorFactorBuff', 1200000, 0, 0, 0, 0, 0, 0, 0, 300, 0, 0, 0, '', '', '', '', 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 'Heretic_Live', 0, 0, NOW()),
('molten_barricade_l50',  100116, 1, 1, 'Molten Barricade',   'Self Armor Factor buff.', 'Self', 0, 30, 3.0, 0, 0, 'ArmorFactorBuff', 1200000, 0, 0, 0, 0, 0, 0, 0, 360, 0, 0, 0, '', '', '', '', 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 'Heretic_Live', 0, 0, NOW());

-- ---- Diabolic Thorns → Stakes: Self Damage Add -------------------------
REPLACE INTO `spell` (`Spell_ID`, `SpellID`, `ClientEffect`, `Icon`, `Name`, `Description`, `Target`, `Range`, `Power`, `CastTime`, `Damage`, `DamageType`, `Type`, `Duration`, `Frequency`, `Pulse`, `PulsePower`, `Radius`, `RecastDelay`, `ResurrectHealth`, `ResurrectMana`, `Value`, `Concentration`, `LifeDrainReturn`, `AmnesiaChance`, `Message1`, `Message2`, `Message3`, `Message4`, `InstrumentRequirement`, `SpellGroup`, `EffectGroup`, `SubSpellID`, `MoveCast`, `Uninterruptible`, `IsPrimary`, `IsSecondary`, `AllowBolt`, `SharedTimerGroup`, `PackageID`, `IsFocus`, `TooltipId`, `LastTimeRowUpdated`) VALUES
('diabolic_thorns_l6',    100120, 10200, 10200, 'Diabolic Thorns',    'Self damage add (melee).', 'Self', 0, 5,  3.0, 0, 13, 'DamageAdd', 1200000, 0, 0, 0, 0, 0, 0, 0, 1.2, 0, 0, 0, '', '', '', '', 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 'Heretic_Live', 0, 0, NOW()),
('diabolic_needles_l12',  100121, 10200, 10200, 'Diabolic Needles',   'Self damage add (melee).', 'Self', 0, 9,  3.0, 0, 13, 'DamageAdd', 1200000, 0, 0, 0, 0, 0, 0, 0, 2.0, 0, 0, 0, '', '', '', '', 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 'Heretic_Live', 0, 0, NOW()),
('diabolic_barbs_l19',    100122, 10200, 10200, 'Diabolic Barbs',     'Self damage add (melee).', 'Self', 0, 13, 3.0, 0, 13, 'DamageAdd', 1200000, 0, 0, 0, 0, 0, 0, 0, 2.8, 0, 0, 0, '', '', '', '', 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 'Heretic_Live', 0, 0, NOW()),
('diabolic_spikes_l26',   100123, 10200, 10200, 'Diabolic Spikes',    'Self damage add (melee).', 'Self', 0, 17, 3.0, 0, 13, 'DamageAdd', 1200000, 0, 0, 0, 0, 0, 0, 0, 3.4, 0, 0, 0, '', '', '', '', 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 'Heretic_Live', 0, 0, NOW()),
('diabolic_lances_l35',   100124, 10200, 10200, 'Diabolic Lances',    'Self damage add (melee).', 'Self', 0, 22, 3.0, 0, 13, 'DamageAdd', 1200000, 0, 0, 0, 0, 0, 0, 0, 4.0, 0, 0, 0, '', '', '', '', 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 'Heretic_Live', 0, 0, NOW()),
('diabolic_stakes_l44',   100125, 10200, 10200, 'Diabolic Stakes',    'Self damage add (melee).', 'Self', 0, 27, 3.0, 0, 13, 'DamageAdd', 1200000, 0, 0, 0, 0, 0, 0, 0, 4.6, 0, 0, 0, '', '', '', '', 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 'Heretic_Live', 0, 0, NOW());

-- ---- Buffer of Steam → Lava: Self Ablative absorb ----------------------
REPLACE INTO `spell` (`Spell_ID`, `SpellID`, `ClientEffect`, `Icon`, `Name`, `Description`, `Target`, `Range`, `Power`, `CastTime`, `Damage`, `DamageType`, `Type`, `Duration`, `Frequency`, `Pulse`, `PulsePower`, `Radius`, `RecastDelay`, `ResurrectHealth`, `ResurrectMana`, `Value`, `Concentration`, `LifeDrainReturn`, `AmnesiaChance`, `Message1`, `Message2`, `Message3`, `Message4`, `InstrumentRequirement`, `SpellGroup`, `EffectGroup`, `SubSpellID`, `MoveCast`, `Uninterruptible`, `IsPrimary`, `IsSecondary`, `AllowBolt`, `SharedTimerGroup`, `PackageID`, `IsFocus`, `TooltipId`, `LastTimeRowUpdated`) VALUES
('buffer_of_steam_l25',  100130, 10087, 10087, 'Buffer of Steam',   'Self absorb shield (15-30%).', 'Self', 0, 16, 3.0, 0, 0, 'AblativeArmor', 1200000, 0, 0, 0, 0, 0, 0, 0, 15, 0, 0, 0, '', '', '', '', 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 'Heretic_Live', 0, 0, NOW()),
('buffer_of_smoke_l30',  100131, 10087, 10087, 'Buffer of Smoke',   'Self absorb shield (15-30%).', 'Self', 0, 19, 3.0, 0, 0, 'AblativeArmor', 1200000, 0, 0, 0, 0, 0, 0, 0, 18, 0, 0, 0, '', '', '', '', 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 'Heretic_Live', 0, 0, NOW()),
('buffer_of_ash_l36',    100132, 10087, 10087, 'Buffer of Ash',     'Self absorb shield (15-30%).', 'Self', 0, 22, 3.0, 0, 0, 'AblativeArmor', 1200000, 0, 0, 0, 0, 0, 0, 0, 22, 0, 0, 0, '', '', '', '', 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 'Heretic_Live', 0, 0, NOW()),
('buffer_of_cinder_l42', 100133, 10087, 10087, 'Buffer of Cinder',  'Self absorb shield (15-30%).', 'Self', 0, 26, 3.0, 0, 0, 'AblativeArmor', 1200000, 0, 0, 0, 0, 0, 0, 0, 26, 0, 0, 0, '', '', '', '', 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 'Heretic_Live', 0, 0, NOW()),
('buffer_of_lava_l49',   100134, 10087, 10087, 'Buffer of Lava',    'Self absorb shield (15-30%).', 'Self', 0, 30, 3.0, 0, 0, 'AblativeArmor', 1200000, 0, 0, 0, 0, 0, 0, 0, 30, 0, 0, 0, '', '', '', '', 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 'Heretic_Live', 0, 0, NOW());

-- ---- Infernal Carve → Slice: Self Melee Damage buff --------------------
REPLACE INTO `spell` (`Spell_ID`, `SpellID`, `ClientEffect`, `Icon`, `Name`, `Description`, `Target`, `Range`, `Power`, `CastTime`, `Damage`, `DamageType`, `Type`, `Duration`, `Frequency`, `Pulse`, `PulsePower`, `Radius`, `RecastDelay`, `ResurrectHealth`, `ResurrectMana`, `Value`, `Concentration`, `LifeDrainReturn`, `AmnesiaChance`, `Message1`, `Message2`, `Message3`, `Message4`, `InstrumentRequirement`, `SpellGroup`, `EffectGroup`, `SubSpellID`, `MoveCast`, `Uninterruptible`, `IsPrimary`, `IsSecondary`, `AllowBolt`, `SharedTimerGroup`, `PackageID`, `IsFocus`, `TooltipId`, `LastTimeRowUpdated`) VALUES
('infernal_carve_l11',  100140, 1566, 1566, 'Infernal Carve',   'Self combat damage buff.', 'Self', 0, 8,  3.0, 0, 0, 'MeleeDamageBuff', 1200000, 0, 0, 0, 0, 0, 0, 0, 2.1, 0, 0, 0, '', '', '', '', 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 'Heretic_Live', 0, 0, NOW()),
('infernal_cleave_l17', 100141, 1566, 1566, 'Infernal Cleave',  'Self combat damage buff.', 'Self', 0, 12, 3.0, 0, 0, 'MeleeDamageBuff', 1200000, 0, 0, 0, 0, 0, 0, 0, 3.6, 0, 0, 0, '', '', '', '', 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 'Heretic_Live', 0, 0, NOW()),
('infernal_rend_l24',   100142, 1566, 1566, 'Infernal Rend',    'Self combat damage buff.', 'Self', 0, 16, 3.0, 0, 0, 'MeleeDamageBuff', 1200000, 0, 0, 0, 0, 0, 0, 0, 5.1, 0, 0, 0, '', '', '', '', 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 'Heretic_Live', 0, 0, NOW()),
('infernal_sever_l31',  100143, 1566, 1566, 'Infernal Sever',   'Self combat damage buff.', 'Self', 0, 20, 3.0, 0, 0, 'MeleeDamageBuff', 1200000, 0, 0, 0, 0, 0, 0, 0, 6.6, 0, 0, 0, '', '', '', '', 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 'Heretic_Live', 0, 0, NOW()),
('infernal_shear_l39',  100144, 1566, 1566, 'Infernal Shear',   'Self combat damage buff.', 'Self', 0, 24, 3.0, 0, 0, 'MeleeDamageBuff', 1200000, 0, 0, 0, 0, 0, 0, 0, 8.0, 0, 0, 0, '', '', '', '', 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 'Heretic_Live', 0, 0, NOW()),
('infernal_slice_l46',  100145, 1566, 1566, 'Infernal Slice',   'Self combat damage buff.', 'Self', 0, 28, 3.0, 0, 0, 'MeleeDamageBuff', 1200000, 0, 0, 0, 0, 0, 0, 0, 9.4, 0, 0, 0, '', '', '', '', 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 'Heretic_Live', 0, 0, NOW());

-- ---- Arawn's Precision → Cunning: Self Piercing Magic buff ------------
REPLACE INTO `spell` (`Spell_ID`, `SpellID`, `ClientEffect`, `Icon`, `Name`, `Description`, `Target`, `Range`, `Power`, `CastTime`, `Damage`, `DamageType`, `Type`, `Duration`, `Frequency`, `Pulse`, `PulsePower`, `Radius`, `RecastDelay`, `ResurrectHealth`, `ResurrectMana`, `Value`, `Concentration`, `LifeDrainReturn`, `AmnesiaChance`, `Message1`, `Message2`, `Message3`, `Message4`, `InstrumentRequirement`, `SpellGroup`, `EffectGroup`, `SubSpellID`, `MoveCast`, `Uninterruptible`, `IsPrimary`, `IsSecondary`, `AllowBolt`, `SharedTimerGroup`, `PackageID`, `IsFocus`, `TooltipId`, `LastTimeRowUpdated`) VALUES
('arawns_precision_l10',  100150, 9221, 9221, 'Arawn\'s Precision',  'Self piercing magic buff.', 'Self', 0, 8,  3.0, 0, 0, 'HereticPiercingMagic', 1200000, 0, 0, 0, 0, 0, 0, 0, 1,  0, 0, 0, '', '', '', '', 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 'Heretic_Live', 0, 0, NOW()),
('arawns_acumen_l18',     100151, 9221, 9221, 'Arawn\'s Acumen',     'Self piercing magic buff.', 'Self', 0, 12, 3.0, 0, 0, 'HereticPiercingMagic', 1200000, 0, 0, 0, 0, 0, 0, 0, 2,  0, 0, 0, '', '', '', '', 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 'Heretic_Live', 0, 0, NOW()),
('arawns_insight_l26',    100152, 9221, 9221, 'Arawn\'s Insight',    'Self piercing magic buff.', 'Self', 0, 16, 3.0, 0, 0, 'HereticPiercingMagic', 1200000, 0, 0, 0, 0, 0, 0, 0, 4,  0, 0, 0, '', '', '', '', 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 'Heretic_Live', 0, 0, NOW()),
('arawns_clarity_l34',    100153, 9221, 9221, 'Arawn\'s Clarity',    'Self piercing magic buff.', 'Self', 0, 20, 3.0, 0, 0, 'HereticPiercingMagic', 1200000, 0, 0, 0, 0, 0, 0, 0, 6,  0, 0, 0, '', '', '', '', 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 'Heretic_Live', 0, 0, NOW()),
('arawns_wisdom_l42',     100154, 9221, 9221, 'Arawn\'s Wisdom',     'Self piercing magic buff.', 'Self', 0, 24, 3.0, 0, 0, 'HereticPiercingMagic', 1200000, 0, 0, 0, 0, 0, 0, 0, 8,  0, 0, 0, '', '', '', '', 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 'Heretic_Live', 0, 0, NOW()),
('arawns_cunning_l50',    100155, 9221, 9221, 'Arawn\'s Cunning',    'Self piercing magic buff.', 'Self', 0, 28, 3.0, 0, 0, 'HereticPiercingMagic', 1200000, 0, 0, 0, 0, 0, 0, 0, 10, 0, 0, 0, '', '', '', '', 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 'Heretic_Live', 0, 0, NOW());

-- ====================================================================
-- LINEXSPELL: link spells to their specialization line at acquisition
-- ====================================================================
REPLACE INTO `linexspell` (`LineXSpell_ID`, `LineName`, `SpellID`, `Level`, `PackageID`, `LastTimeRowUpdated`) VALUES
-- Arawn's Fire
('lxs_af_100000', 'Arawn\'s Fire', 100000, 4,  'Heretic_Live', NOW()),
('lxs_af_100001', 'Arawn\'s Fire', 100001, 12, 'Heretic_Live', NOW()),
('lxs_af_100002', 'Arawn\'s Fire', 100002, 20, 'Heretic_Live', NOW()),
('lxs_af_100003', 'Arawn\'s Fire', 100003, 28, 'Heretic_Live', NOW()),
('lxs_af_100004', 'Arawn\'s Fire', 100004, 36, 'Heretic_Live', NOW()),
('lxs_af_100005', 'Arawn\'s Fire', 100005, 44, 'Heretic_Live', NOW()),
('lxs_af_100010', 'Arawn\'s Fire', 100010, 9,  'Heretic_Live', NOW()),
('lxs_af_100011', 'Arawn\'s Fire', 100011, 16, 'Heretic_Live', NOW()),
('lxs_af_100012', 'Arawn\'s Fire', 100012, 23, 'Heretic_Live', NOW()),
('lxs_af_100013', 'Arawn\'s Fire', 100013, 31, 'Heretic_Live', NOW()),
('lxs_af_100014', 'Arawn\'s Fire', 100014, 39, 'Heretic_Live', NOW()),
('lxs_af_100015', 'Arawn\'s Fire', 100015, 47, 'Heretic_Live', NOW()),
('lxs_af_100020', 'Arawn\'s Fire', 100020, 3,  'Heretic_Live', NOW()),
('lxs_af_100021', 'Arawn\'s Fire', 100021, 11, 'Heretic_Live', NOW()),
('lxs_af_100022', 'Arawn\'s Fire', 100022, 18, 'Heretic_Live', NOW()),
('lxs_af_100023', 'Arawn\'s Fire', 100023, 25, 'Heretic_Live', NOW()),
('lxs_af_100024', 'Arawn\'s Fire', 100024, 33, 'Heretic_Live', NOW()),
('lxs_af_100025', 'Arawn\'s Fire', 100025, 41, 'Heretic_Live', NOW()),
('lxs_af_100030', 'Arawn\'s Fire', 100030, 36, 'Heretic_Live', NOW()),
('lxs_af_100031', 'Arawn\'s Fire', 100031, 42, 'Heretic_Live', NOW()),
('lxs_af_100032', 'Arawn\'s Fire', 100032, 48, 'Heretic_Live', NOW()),
('lxs_af_100040', 'Arawn\'s Fire', 100040, 6,  'Heretic_Live', NOW()),
('lxs_af_100041', 'Arawn\'s Fire', 100041, 14, 'Heretic_Live', NOW()),
('lxs_af_100042', 'Arawn\'s Fire', 100042, 22, 'Heretic_Live', NOW()),
('lxs_af_100043', 'Arawn\'s Fire', 100043, 30, 'Heretic_Live', NOW()),
('lxs_af_100044', 'Arawn\'s Fire', 100044, 38, 'Heretic_Live', NOW()),
('lxs_af_100045', 'Arawn\'s Fire', 100045, 46, 'Heretic_Live', NOW()),
('lxs_af_100050', 'Arawn\'s Fire', 100050, 5,  'Heretic_Live', NOW()),
('lxs_af_100051', 'Arawn\'s Fire', 100051, 13, 'Heretic_Live', NOW()),
('lxs_af_100052', 'Arawn\'s Fire', 100052, 21, 'Heretic_Live', NOW()),
('lxs_af_100053', 'Arawn\'s Fire', 100053, 30, 'Heretic_Live', NOW()),
('lxs_af_100054', 'Arawn\'s Fire', 100054, 39, 'Heretic_Live', NOW()),
('lxs_af_100055', 'Arawn\'s Fire', 100055, 48, 'Heretic_Live', NOW()),
('lxs_af_100060', 'Arawn\'s Fire', 100060, 27, 'Heretic_Live', NOW()),
-- Cthonic Accretion
('lxs_ca_100100', 'Cthonic Accretion', 100100, 5,  'Heretic_Live', NOW()),
('lxs_ca_100101', 'Cthonic Accretion', 100101, 10, 'Heretic_Live', NOW()),
('lxs_ca_100102', 'Cthonic Accretion', 100102, 16, 'Heretic_Live', NOW()),
('lxs_ca_100103', 'Cthonic Accretion', 100103, 23, 'Heretic_Live', NOW()),
('lxs_ca_100104', 'Cthonic Accretion', 100104, 32, 'Heretic_Live', NOW()),
('lxs_ca_100105', 'Cthonic Accretion', 100105, 40, 'Heretic_Live', NOW()),
('lxs_ca_100106', 'Cthonic Accretion', 100106, 50, 'Heretic_Live', NOW()),
('lxs_ca_100110', 'Cthonic Accretion', 100110, 7,  'Heretic_Live', NOW()),
('lxs_ca_100111', 'Cthonic Accretion', 100111, 14, 'Heretic_Live', NOW()),
('lxs_ca_100112', 'Cthonic Accretion', 100112, 21, 'Heretic_Live', NOW()),
('lxs_ca_100113', 'Cthonic Accretion', 100113, 29, 'Heretic_Live', NOW()),
('lxs_ca_100114', 'Cthonic Accretion', 100114, 36, 'Heretic_Live', NOW()),
('lxs_ca_100115', 'Cthonic Accretion', 100115, 43, 'Heretic_Live', NOW()),
('lxs_ca_100116', 'Cthonic Accretion', 100116, 50, 'Heretic_Live', NOW()),
('lxs_ca_100120', 'Cthonic Accretion', 100120, 6,  'Heretic_Live', NOW()),
('lxs_ca_100121', 'Cthonic Accretion', 100121, 12, 'Heretic_Live', NOW()),
('lxs_ca_100122', 'Cthonic Accretion', 100122, 19, 'Heretic_Live', NOW()),
('lxs_ca_100123', 'Cthonic Accretion', 100123, 26, 'Heretic_Live', NOW()),
('lxs_ca_100124', 'Cthonic Accretion', 100124, 35, 'Heretic_Live', NOW()),
('lxs_ca_100125', 'Cthonic Accretion', 100125, 44, 'Heretic_Live', NOW()),
('lxs_ca_100130', 'Cthonic Accretion', 100130, 25, 'Heretic_Live', NOW()),
('lxs_ca_100131', 'Cthonic Accretion', 100131, 30, 'Heretic_Live', NOW()),
('lxs_ca_100132', 'Cthonic Accretion', 100132, 36, 'Heretic_Live', NOW()),
('lxs_ca_100133', 'Cthonic Accretion', 100133, 42, 'Heretic_Live', NOW()),
('lxs_ca_100134', 'Cthonic Accretion', 100134, 49, 'Heretic_Live', NOW()),
('lxs_ca_100140', 'Cthonic Accretion', 100140, 11, 'Heretic_Live', NOW()),
('lxs_ca_100141', 'Cthonic Accretion', 100141, 17, 'Heretic_Live', NOW()),
('lxs_ca_100142', 'Cthonic Accretion', 100142, 24, 'Heretic_Live', NOW()),
('lxs_ca_100143', 'Cthonic Accretion', 100143, 31, 'Heretic_Live', NOW()),
('lxs_ca_100144', 'Cthonic Accretion', 100144, 39, 'Heretic_Live', NOW()),
('lxs_ca_100145', 'Cthonic Accretion', 100145, 46, 'Heretic_Live', NOW()),
('lxs_ca_100150', 'Cthonic Accretion', 100150, 10, 'Heretic_Live', NOW()),
('lxs_ca_100151', 'Cthonic Accretion', 100151, 18, 'Heretic_Live', NOW()),
('lxs_ca_100152', 'Cthonic Accretion', 100152, 26, 'Heretic_Live', NOW()),
('lxs_ca_100153', 'Cthonic Accretion', 100153, 34, 'Heretic_Live', NOW()),
('lxs_ca_100154', 'Cthonic Accretion', 100154, 42, 'Heretic_Live', NOW()),
('lxs_ca_100155', 'Cthonic Accretion', 100155, 50, 'Heretic_Live', NOW());

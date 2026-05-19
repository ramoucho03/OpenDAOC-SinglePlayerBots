-- =====================================================================
-- Heretic Live - Consolidated, idempotent integration
-- =====================================================================
-- One-shot SQL applied by the gameserver entrypoint on every start.
-- Safe to re-run; uses DELETE then REPLACE INTO / UPDATE.
--
-- Strategy: REBRANCH onto upstream SpellLines that the C# code already
-- expects. We populate the existing 'Heretic Rejuvenation Spec' and
-- 'Heretic Enhancement Spec' speclines (empty in upstream) instead of
-- creating new specs. Result: exactly the Live DAoC layout —
--   Rejuvenation spec → heals (baseline) + Arawn's Fire DD/snare/DoT (specline)
--   Enhancement spec  → buffs (baseline) + Cthonic Accretion self-buffs (specline)
--
-- Required matching C# (already in place):
--   - SkillConstants.cs (Specs.Arawns_Fire / Cthonic_Accretion kept as aliases)
--   - eProperty.cs (Skill_Arawns_Fire=116, Skill_Cthonic_Accretion=117)
--   - SkillBase.cs (m_specToSkill mappings)
--   - MimicNPC/ClassSpecs/Albion/Heretic.cs (uses Rejuvenation/Enhancement)
--
-- Per spell category icons (re-used from upstream working spells):
--   Arawn's mono ramping DD : 309 (Fiery Maelstrom)
--   Lava AoE DD+snare       : 308 (Fiery Maelstrom Minor)
--   Blazing mono DD+snare   : 9644 (95 Heat DD)
--   Channeled Blaze         : 310 (Fiery Maelstrom Major)
--   Fiery Grasp instant DD  : 312 (Fiery Bolt)
--   Flickering Embers DoT   : 11263 (Flickering Flame)
--   Insta-snare             : 9211 (Crippling Pain)
--   Chthonic Str/Con        : 10041 (Strengthen Pack)
--   Kindled Shield AF       : 1 (Amethyst Shield)
--   Diabolic Thorns DmgAdd  : 10200 (Bone Spurs)
--   Buffer of Steam Absorb  : 10087 (Bone Skin)
--   Infernal Carve MeleeDmg : 1566 (Refiner's Strength)
--   Arawn's Precision       : 9221 (Facilitate Painworking)
-- =====================================================================

-- ---- Step 1: Idempotent cleanup --------------------------------------
-- Remove any prior attempt at custom specs (from old strategy)
DELETE FROM classxspecialization WHERE ClassID = 33 AND SpecKeyName IN ('Arawn\'s Fire', 'Cthonic Accretion');
DELETE FROM spellline WHERE KeyName IN ('Arawn\'s Fire', 'Cthonic Accretion');
DELETE FROM specialization WHERE KeyName IN ('Arawn\'s Fire', 'Cthonic Accretion');

-- Remove the broken 'Heretic Enhancement' baseline SpellLine which has
-- 0 spells in linexspell and blocks the fallback to the generic
-- 'Enhancement' baseline (26 spells). Upstream data bug.
DELETE FROM spellline WHERE KeyName = 'Heretic Enhancement' AND IsBaseLine = 1;
DELETE FROM spellline WHERE KeyName = 'Heretic Rejuvenation' AND IsBaseLine = 1;

-- Cleanup our own seed for fresh re-apply
DELETE FROM linexspell WHERE PackageID = 'Heretic_Live';
DELETE FROM spell WHERE PackageID = 'Heretic_Live';

-- ---- Step 2: Spells (71 rows, SpellID 100000-100199) ---------------
-- Arawn's Inferno: channeled mono DD ramping
REPLACE INTO `spell` (`Spell_ID`, `SpellID`, `ClientEffect`, `Icon`, `Name`, `Description`, `Target`, `Range`, `Power`, `CastTime`, `Damage`, `DamageType`, `Type`, `Duration`, `Frequency`, `Pulse`, `PulsePower`, `Radius`, `RecastDelay`, `ResurrectHealth`, `ResurrectMana`, `Value`, `Concentration`, `LifeDrainReturn`, `AmnesiaChance`, `Message1`, `Message2`, `Message3`, `Message4`, `InstrumentRequirement`, `SpellGroup`, `EffectGroup`, `SubSpellID`, `MoveCast`, `Uninterruptible`, `IsPrimary`, `IsSecondary`, `AllowBolt`, `SharedTimerGroup`, `PackageID`, `IsFocus`, `TooltipId`, `LastTimeRowUpdated`) VALUES
('arawns_singe_l4',     100000, 309, 309, 'Arawn\'s Singe',     'Channeled fire damage that ramps up over time.', 'Enemy', 1500, 5,  3.0,  10, 13, 'RampingDamageFocus', 16000, 1500, 1, 4, 0, 0, 0, 0, 0, 0, 50, 250, '', '', '', '', 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 'Heretic_Live', 1, 0, NOW()),
('arawns_torch_l12',    100001, 309, 309, 'Arawn\'s Torch',     'Channeled fire damage that ramps up over time.', 'Enemy', 1500, 12, 3.0,  20, 13, 'RampingDamageFocus', 16000, 1500, 1, 7, 0, 0, 0, 0, 0, 0, 50, 250, '', '', '', '', 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 'Heretic_Live', 1, 0, NOW()),
('arawns_flame_l20',    100002, 309, 309, 'Arawn\'s Flame',     'Channeled fire damage that ramps up over time.', 'Enemy', 1500, 20, 3.0,  32, 13, 'RampingDamageFocus', 16000, 1500, 1, 10, 0, 0, 0, 0, 0, 0, 50, 250, '', '', '', '', 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 'Heretic_Live', 1, 0, NOW()),
('arawns_blaze_l28',    100003, 309, 309, 'Arawn\'s Blaze',     'Channeled fire damage that ramps up over time.', 'Enemy', 1500, 28, 3.0,  48, 13, 'RampingDamageFocus', 16000, 1500, 1, 14, 0, 0, 0, 0, 0, 0, 50, 250, '', '', '', '', 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 'Heretic_Live', 1, 0, NOW()),
('arawns_pyre_l36',     100004, 309, 309, 'Arawn\'s Pyre',      'Channeled fire damage that ramps up over time.', 'Enemy', 1500, 36, 3.0,  68, 13, 'RampingDamageFocus', 16000, 1500, 1, 19, 0, 0, 0, 0, 0, 0, 50, 250, '', '', '', '', 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 'Heretic_Live', 1, 0, NOW()),
('arawns_inferno_l44',  100005, 309, 309, 'Arawn\'s Inferno',   'Channeled fire damage that ramps up over time.', 'Enemy', 1500, 44, 3.0,  92, 13, 'RampingDamageFocus', 16000, 1500, 1, 24, 0, 0, 0, 0, 0, 0, 50, 250, '', '', '', '', 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 'Heretic_Live', 1, 0, NOW());

-- Lava line: AoE channeled DD + snare
REPLACE INTO `spell` (`Spell_ID`, `SpellID`, `ClientEffect`, `Icon`, `Name`, `Description`, `Target`, `Range`, `Power`, `CastTime`, `Damage`, `DamageType`, `Type`, `Duration`, `Frequency`, `Pulse`, `PulsePower`, `Radius`, `RecastDelay`, `ResurrectHealth`, `ResurrectMana`, `Value`, `Concentration`, `LifeDrainReturn`, `AmnesiaChance`, `Message1`, `Message2`, `Message3`, `Message4`, `InstrumentRequirement`, `SpellGroup`, `EffectGroup`, `SubSpellID`, `MoveCast`, `Uninterruptible`, `IsPrimary`, `IsSecondary`, `AllowBolt`, `SharedTimerGroup`, `PackageID`, `IsFocus`, `TooltipId`, `LastTimeRowUpdated`) VALUES
('lava_spate_l9',       100010, 308, 308, 'Lava Spate',        'Channeled fire AoE around the target with a slow.', 'Enemy', 1500, 9,  3.0,  6,  13, 'RampingDamageFocus', 16000, 1500, 1, 5, 350, 0, 0, 0, 50, 0, 30, 150, '', '', '', '', 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 'Heretic_Live', 1, 0, NOW()),
('lava_torrent_l16',    100011, 308, 308, 'Lava Torrent',      'Channeled fire AoE around the target with a slow.', 'Enemy', 1500, 16, 3.0,  10, 13, 'RampingDamageFocus', 16000, 1500, 1, 8, 350, 0, 0, 0, 50, 0, 30, 150, '', '', '', '', 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 'Heretic_Live', 1, 0, NOW()),
('lava_river_l23',      100012, 308, 308, 'Lava River',        'Channeled fire AoE around the target with a slow.', 'Enemy', 1500, 23, 3.0,  14, 13, 'RampingDamageFocus', 16000, 1500, 1, 11, 350, 0, 0, 0, 50, 0, 30, 150, '', '', '', '', 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 'Heretic_Live', 1, 0, NOW()),
('lava_flood_l31',      100013, 308, 308, 'Lava Flood',        'Channeled fire AoE around the target with a slow.', 'Enemy', 1500, 31, 3.0,  18, 13, 'RampingDamageFocus', 16000, 1500, 1, 14, 350, 0, 0, 0, 50, 0, 30, 150, '', '', '', '', 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 'Heretic_Live', 1, 0, NOW()),
('lava_deluge_l39',     100014, 308, 308, 'Lava Deluge',       'Channeled fire AoE around the target with a slow.', 'Enemy', 1500, 39, 3.0,  24, 13, 'RampingDamageFocus', 16000, 1500, 1, 18, 350, 0, 0, 0, 50, 0, 30, 150, '', '', '', '', 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 'Heretic_Live', 1, 0, NOW()),
('lava_avalanche_l47',  100015, 308, 308, 'Lava Avalanche',    'Channeled fire AoE around the target with a slow.', 'Enemy', 1500, 47, 3.0,  30, 13, 'RampingDamageFocus', 16000, 1500, 1, 22, 350, 0, 0, 0, 50, 0, 30, 150, '', '', '', '', 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 'Heretic_Live', 1, 0, NOW());

-- Blazing line: mono focus DD + snare
REPLACE INTO `spell` (`Spell_ID`, `SpellID`, `ClientEffect`, `Icon`, `Name`, `Description`, `Target`, `Range`, `Power`, `CastTime`, `Damage`, `DamageType`, `Type`, `Duration`, `Frequency`, `Pulse`, `PulsePower`, `Radius`, `RecastDelay`, `ResurrectHealth`, `ResurrectMana`, `Value`, `Concentration`, `LifeDrainReturn`, `AmnesiaChance`, `Message1`, `Message2`, `Message3`, `Message4`, `InstrumentRequirement`, `SpellGroup`, `EffectGroup`, `SubSpellID`, `MoveCast`, `Uninterruptible`, `IsPrimary`, `IsSecondary`, `AllowBolt`, `SharedTimerGroup`, `PackageID`, `IsFocus`, `TooltipId`, `LastTimeRowUpdated`) VALUES
('blazing_flow_l3',     100020, 9644, 9644, 'Blazing Flow',      'Slows the target while burning it. Focus.',     'Enemy', 1500, 3,  3.0,  8,  13, 'HereticDamageSpeedDecrease', 8000, 2000, 1, 3, 0, 0, 0, 0, 50, 0, 0, 0, '', '', '', '', 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 'Heretic_Live', 1, 0, NOW()),
('blazing_stream_l11',  100021, 9644, 9644, 'Blazing Stream',    'Slows the target while burning it. Focus.',     'Enemy', 1500, 11, 3.0,  14, 13, 'HereticDamageSpeedDecrease', 8000, 2000, 1, 6, 0, 0, 0, 0, 50, 0, 0, 0, '', '', '', '', 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 'Heretic_Live', 1, 0, NOW()),
('blazing_torrent_l18', 100022, 9644, 9644, 'Blazing Torrent',   'Slows the target while burning it. Focus.',     'Enemy', 1500, 18, 3.0,  20, 13, 'HereticDamageSpeedDecrease', 8000, 2000, 1, 9, 0, 0, 0, 0, 50, 0, 0, 0, '', '', '', '', 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 'Heretic_Live', 1, 0, NOW()),
('blazing_river_l25',   100023, 9644, 9644, 'Blazing River',     'Slows the target while burning it. Focus.',     'Enemy', 1500, 25, 3.0,  28, 13, 'HereticDamageSpeedDecrease', 8000, 2000, 1, 12, 0, 0, 0, 0, 50, 0, 0, 0, '', '', '', '', 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 'Heretic_Live', 1, 0, NOW()),
('blazing_surge_l33',   100024, 9644, 9644, 'Blazing Surge',     'Slows the target while burning it. Focus.',     'Enemy', 1500, 33, 3.0,  38, 13, 'HereticDamageSpeedDecrease', 8000, 2000, 1, 16, 0, 0, 0, 0, 50, 0, 0, 0, '', '', '', '', 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 'Heretic_Live', 1, 0, NOW()),
('blazing_flood_l41',   100025, 9644, 9644, 'Blazing Flood',     'Slows the target while burning it. Focus.',     'Enemy', 1500, 41, 3.0,  50, 13, 'HereticDamageSpeedDecrease', 8000, 2000, 1, 20, 0, 0, 0, 0, 50, 0, 0, 0, '', '', '', '', 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 'Heretic_Live', 1, 0, NOW());

-- Channeled Blaze: uninterruptible mono DD
REPLACE INTO `spell` (`Spell_ID`, `SpellID`, `ClientEffect`, `Icon`, `Name`, `Description`, `Target`, `Range`, `Power`, `CastTime`, `Damage`, `DamageType`, `Type`, `Duration`, `Frequency`, `Pulse`, `PulsePower`, `Radius`, `RecastDelay`, `ResurrectHealth`, `ResurrectMana`, `Value`, `Concentration`, `LifeDrainReturn`, `AmnesiaChance`, `Message1`, `Message2`, `Message3`, `Message4`, `InstrumentRequirement`, `SpellGroup`, `EffectGroup`, `SubSpellID`, `MoveCast`, `Uninterruptible`, `IsPrimary`, `IsSecondary`, `AllowBolt`, `SharedTimerGroup`, `PackageID`, `IsFocus`, `TooltipId`, `LastTimeRowUpdated`) VALUES
('glistening_blaze_l36', 100030, 310, 310, 'Glistening Blaze',  'Uninterruptible channeled fire damage.', 'Enemy', 1500, 32, 3.0,  90,  13, 'RampingDamageFocus', 33000, 1500, 1, 16, 0, 0, 0, 0, 0, 0, 0, 0, '', '', '', '', 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 'Heretic_Live', 1, 0, NOW()),
('whirling_blaze_l42',   100031, 310, 310, 'Whirling Blaze',    'Uninterruptible channeled fire damage.', 'Enemy', 1500, 38, 3.0,  97,  13, 'RampingDamageFocus', 33000, 1500, 1, 19, 0, 0, 0, 0, 0, 0, 0, 0, '', '', '', '', 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 'Heretic_Live', 1, 0, NOW()),
('torrential_blaze_l48', 100032, 310, 310, 'Torrential Blaze',  'Uninterruptible channeled fire damage.', 'Enemy', 1500, 44, 3.0,  120, 13, 'RampingDamageFocus', 33000, 1500, 1, 22, 0, 0, 0, 0, 0, 0, 0, 0, '', '', '', '', 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 'Heretic_Live', 1, 0, NOW());

-- Fiery Grasp line: instant DD
REPLACE INTO `spell` (`Spell_ID`, `SpellID`, `ClientEffect`, `Icon`, `Name`, `Description`, `Target`, `Range`, `Power`, `CastTime`, `Damage`, `DamageType`, `Type`, `Duration`, `Frequency`, `Pulse`, `PulsePower`, `Radius`, `RecastDelay`, `ResurrectHealth`, `ResurrectMana`, `Value`, `Concentration`, `LifeDrainReturn`, `AmnesiaChance`, `Message1`, `Message2`, `Message3`, `Message4`, `InstrumentRequirement`, `SpellGroup`, `EffectGroup`, `SubSpellID`, `MoveCast`, `Uninterruptible`, `IsPrimary`, `IsSecondary`, `AllowBolt`, `SharedTimerGroup`, `PackageID`, `IsFocus`, `TooltipId`, `LastTimeRowUpdated`) VALUES
('fiery_grasp_l6',         100040, 312, 312, 'Fiery Grasp',         'Instant fire damage.', 'Enemy', 1500, 8,  2.8,  18,  13, 'DirectDamage', 0, 0, 0, 0, 0, 12000, 0, 0, 0, 0, 0, 0, '', '', '', '', 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 'Heretic_Live', 0, 0, NOW()),
('fiery_clutch_l14',       100041, 312, 312, 'Fiery Clutch',        'Instant fire damage.', 'Enemy', 1500, 14, 2.8,  34,  13, 'DirectDamage', 0, 0, 0, 0, 0, 12000, 0, 0, 0, 0, 0, 0, '', '', '', '', 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 'Heretic_Live', 0, 0, NOW()),
('fiery_grip_l22',         100042, 312, 312, 'Fiery Grip',          'Instant fire damage.', 'Enemy', 1500, 22, 2.8,  56,  13, 'DirectDamage', 0, 0, 0, 0, 0, 12000, 0, 0, 0, 0, 0, 0, '', '', '', '', 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 'Heretic_Live', 0, 0, NOW()),
('fiery_seize_l30',        100043, 312, 312, 'Fiery Seize',         'Instant fire damage.', 'Enemy', 1500, 30, 2.8,  84,  13, 'DirectDamage', 0, 0, 0, 0, 0, 12000, 0, 0, 0, 0, 0, 0, '', '', '', '', 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 'Heretic_Live', 0, 0, NOW()),
('fiery_hold_l38',         100044, 312, 312, 'Fiery Hold',          'Instant fire damage.', 'Enemy', 1500, 38, 2.8,  108, 13, 'DirectDamage', 0, 0, 0, 0, 0, 12000, 0, 0, 0, 0, 0, 0, '', '', '', '', 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 'Heretic_Live', 0, 0, NOW()),
('fiery_stranglehold_l46', 100045, 312, 312, 'Fiery Stranglehold',  'Instant fire damage.', 'Enemy', 1500, 46, 2.8,  140, 13, 'DirectDamage', 0, 0, 0, 0, 0, 12000, 0, 0, 0, 0, 0, 0, '', '', '', '', 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 'Heretic_Live', 0, 0, NOW());

-- Flickering Embers: DoT
REPLACE INTO `spell` (`Spell_ID`, `SpellID`, `ClientEffect`, `Icon`, `Name`, `Description`, `Target`, `Range`, `Power`, `CastTime`, `Damage`, `DamageType`, `Type`, `Duration`, `Frequency`, `Pulse`, `PulsePower`, `Radius`, `RecastDelay`, `ResurrectHealth`, `ResurrectMana`, `Value`, `Concentration`, `LifeDrainReturn`, `AmnesiaChance`, `Message1`, `Message2`, `Message3`, `Message4`, `InstrumentRequirement`, `SpellGroup`, `EffectGroup`, `SubSpellID`, `MoveCast`, `Uninterruptible`, `IsPrimary`, `IsSecondary`, `AllowBolt`, `SharedTimerGroup`, `PackageID`, `IsFocus`, `TooltipId`, `LastTimeRowUpdated`) VALUES
('flickering_embers_l5',    100050, 11263, 11263, 'Flickering Embers',    'Damage over time, fire.', 'Enemy', 700, 5,  3.0,  8,  13, 'HereticDamageOverTime', 20000, 4000, 1, 1, 0, 0, 0, 0, 0, 0, 0, 0, '', '', '', '', 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 'Heretic_Live', 0, 0, NOW()),
('glowing_embers_l13',      100051, 11263, 11263, 'Glowing Embers',       'Damage over time, fire.', 'Enemy', 700, 13, 3.0,  16, 13, 'HereticDamageOverTime', 20000, 4000, 1, 2, 0, 0, 0, 0, 0, 0, 0, 0, '', '', '', '', 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 'Heretic_Live', 0, 0, NOW()),
('burning_embers_l21',      100052, 11263, 11263, 'Burning Embers',       'Damage over time, fire.', 'Enemy', 700, 21, 3.0,  26, 13, 'HereticDamageOverTime', 20000, 4000, 1, 4, 0, 0, 0, 0, 0, 0, 0, 0, '', '', '', '', 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 'Heretic_Live', 0, 0, NOW()),
('scorching_embers_l30',    100053, 11263, 11263, 'Scorching Embers',     'Damage over time, fire.', 'Enemy', 700, 30, 3.0,  38, 13, 'HereticDamageOverTime', 20000, 4000, 1, 6, 0, 0, 0, 0, 0, 0, 0, 0, '', '', '', '', 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 'Heretic_Live', 0, 0, NOW()),
('searing_embers_l39',      100054, 11263, 11263, 'Searing Embers',       'Damage over time, fire.', 'Enemy', 700, 39, 3.0,  52, 13, 'HereticDamageOverTime', 20000, 4000, 1, 9, 0, 0, 0, 0, 0, 0, 0, 0, '', '', '', '', 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 'Heretic_Live', 0, 0, NOW()),
('incinerating_embers_l48', 100055, 11263, 11263, 'Incinerating Embers',  'Damage over time, fire.', 'Enemy', 700, 48, 3.0,  70, 13, 'HereticDamageOverTime', 20000, 4000, 1, 12, 0, 0, 0, 0, 0, 0, 0, 0, '', '', '', '', 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 'Heretic_Live', 0, 0, NOW());

-- Insta-snare
REPLACE INTO `spell` (`Spell_ID`, `SpellID`, `ClientEffect`, `Icon`, `Name`, `Description`, `Target`, `Range`, `Power`, `CastTime`, `Damage`, `DamageType`, `Type`, `Duration`, `Frequency`, `Pulse`, `PulsePower`, `Radius`, `RecastDelay`, `ResurrectHealth`, `ResurrectMana`, `Value`, `Concentration`, `LifeDrainReturn`, `AmnesiaChance`, `Message1`, `Message2`, `Message3`, `Message4`, `InstrumentRequirement`, `SpellGroup`, `EffectGroup`, `SubSpellID`, `MoveCast`, `Uninterruptible`, `IsPrimary`, `IsSecondary`, `AllowBolt`, `SharedTimerGroup`, `PackageID`, `IsFocus`, `TooltipId`, `LastTimeRowUpdated`) VALUES
('arawns_grip_l27', 100060, 9211, 9211, 'Arawn\'s Grip', 'Instant snare. Slows the target for 5 seconds.', 'Enemy', 1500, 14, 0, 0, 13, 'HereticSpeedDecrease', 5000, 0, 0, 0, 0, 30000, 0, 0, 50, 0, 0, 0, '', '', '', '', 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 'Heretic_Live', 0, 0, NOW());

-- Chthonic Vigor → Might: Str/Con buff
REPLACE INTO `spell` (`Spell_ID`, `SpellID`, `ClientEffect`, `Icon`, `Name`, `Description`, `Target`, `Range`, `Power`, `CastTime`, `Damage`, `DamageType`, `Type`, `Duration`, `Frequency`, `Pulse`, `PulsePower`, `Radius`, `RecastDelay`, `ResurrectHealth`, `ResurrectMana`, `Value`, `Concentration`, `LifeDrainReturn`, `AmnesiaChance`, `Message1`, `Message2`, `Message3`, `Message4`, `InstrumentRequirement`, `SpellGroup`, `EffectGroup`, `SubSpellID`, `MoveCast`, `Uninterruptible`, `IsPrimary`, `IsSecondary`, `AllowBolt`, `SharedTimerGroup`, `PackageID`, `IsFocus`, `TooltipId`, `LastTimeRowUpdated`) VALUES
('chthonic_vigor_l5',          100100, 10041, 10041, 'Chthonic Vigor',         'Self Strength and Constitution buff.', 'Self', 0, 4,  3.0, 0, 0, 'StrengthConstitutionBuff', 1200000, 0, 0, 0, 0, 0, 0, 0, 9,  0, 0, 0, '', '', '', '', 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 'Heretic_Live', 0, 0, NOW()),
('chthonic_strength_l10',      100101, 10041, 10041, 'Chthonic Strength',      'Self Strength and Constitution buff.', 'Self', 0, 8,  3.0, 0, 0, 'StrengthConstitutionBuff', 1200000, 0, 0, 0, 0, 0, 0, 0, 21, 0, 0, 0, '', '', '', '', 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 'Heretic_Live', 0, 0, NOW()),
('chthonic_fortification_l16', 100102, 10041, 10041, 'Chthonic Fortification', 'Self Strength and Constitution buff.', 'Self', 0, 12, 3.0, 0, 0, 'StrengthConstitutionBuff', 1200000, 0, 0, 0, 0, 0, 0, 0, 33, 0, 0, 0, '', '', '', '', 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 'Heretic_Live', 0, 0, NOW()),
('chthonic_focus_l23',         100103, 10041, 10041, 'Chthonic Focus',         'Self Strength and Constitution buff.', 'Self', 0, 16, 3.0, 0, 0, 'StrengthConstitutionBuff', 1200000, 0, 0, 0, 0, 0, 0, 0, 45, 0, 0, 0, '', '', '', '', 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 'Heretic_Live', 0, 0, NOW()),
('chthonic_power_l32',         100104, 10041, 10041, 'Chthonic Power',         'Self Strength and Constitution buff.', 'Self', 0, 20, 3.0, 0, 0, 'StrengthConstitutionBuff', 1200000, 0, 0, 0, 0, 0, 0, 0, 57, 0, 0, 0, '', '', '', '', 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 'Heretic_Live', 0, 0, NOW()),
('chthonic_force_l40',         100105, 10041, 10041, 'Chthonic Force',         'Self Strength and Constitution buff.', 'Self', 0, 24, 3.0, 0, 0, 'StrengthConstitutionBuff', 1200000, 0, 0, 0, 0, 0, 0, 0, 69, 0, 0, 0, '', '', '', '', 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 'Heretic_Live', 0, 0, NOW()),
('chthonic_might_l50',         100106, 10041, 10041, 'Chthonic Might',         'Self Strength and Constitution buff.', 'Self', 0, 28, 3.0, 0, 0, 'StrengthConstitutionBuff', 1200000, 0, 0, 0, 0, 0, 0, 0, 75, 0, 0, 0, '', '', '', '', 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 'Heretic_Live', 0, 0, NOW());

-- Kindled Shield → Molten Barricade: AF buff
REPLACE INTO `spell` (`Spell_ID`, `SpellID`, `ClientEffect`, `Icon`, `Name`, `Description`, `Target`, `Range`, `Power`, `CastTime`, `Damage`, `DamageType`, `Type`, `Duration`, `Frequency`, `Pulse`, `PulsePower`, `Radius`, `RecastDelay`, `ResurrectHealth`, `ResurrectMana`, `Value`, `Concentration`, `LifeDrainReturn`, `AmnesiaChance`, `Message1`, `Message2`, `Message3`, `Message4`, `InstrumentRequirement`, `SpellGroup`, `EffectGroup`, `SubSpellID`, `MoveCast`, `Uninterruptible`, `IsPrimary`, `IsSecondary`, `AllowBolt`, `SharedTimerGroup`, `PackageID`, `IsFocus`, `TooltipId`, `LastTimeRowUpdated`) VALUES
('kindled_shield_l7',     100110, 1, 1, 'Kindled Shield',     'Self Armor Factor buff.', 'Self', 0, 5,  3.0, 0, 0, 'ArmorFactorBuff', 1200000, 0, 0, 0, 0, 0, 0, 0, 36,  0, 0, 0, '', '', '', '', 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 'Heretic_Live', 0, 0, NOW()),
('kindled_aegis_l14',     100111, 1, 1, 'Kindled Aegis',      'Self Armor Factor buff.', 'Self', 0, 10, 3.0, 0, 0, 'ArmorFactorBuff', 1200000, 0, 0, 0, 0, 0, 0, 0, 84,  0, 0, 0, '', '', '', '', 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 'Heretic_Live', 0, 0, NOW()),
('molten_shield_l21',     100112, 1, 1, 'Molten Shield',      'Self Armor Factor buff.', 'Self', 0, 14, 3.0, 0, 0, 'ArmorFactorBuff', 1200000, 0, 0, 0, 0, 0, 0, 0, 132, 0, 0, 0, '', '', '', '', 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 'Heretic_Live', 0, 0, NOW()),
('molten_aegis_l29',      100113, 1, 1, 'Molten Aegis',       'Self Armor Factor buff.', 'Self', 0, 18, 3.0, 0, 0, 'ArmorFactorBuff', 1200000, 0, 0, 0, 0, 0, 0, 0, 180, 0, 0, 0, '', '', '', '', 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 'Heretic_Live', 0, 0, NOW()),
('molten_bulwark_l36',    100114, 1, 1, 'Molten Bulwark',     'Self Armor Factor buff.', 'Self', 0, 22, 3.0, 0, 0, 'ArmorFactorBuff', 1200000, 0, 0, 0, 0, 0, 0, 0, 240, 0, 0, 0, '', '', '', '', 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 'Heretic_Live', 0, 0, NOW()),
('molten_rampart_l43',    100115, 1, 1, 'Molten Rampart',     'Self Armor Factor buff.', 'Self', 0, 26, 3.0, 0, 0, 'ArmorFactorBuff', 1200000, 0, 0, 0, 0, 0, 0, 0, 300, 0, 0, 0, '', '', '', '', 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 'Heretic_Live', 0, 0, NOW()),
('molten_barricade_l50',  100116, 1, 1, 'Molten Barricade',   'Self Armor Factor buff.', 'Self', 0, 30, 3.0, 0, 0, 'ArmorFactorBuff', 1200000, 0, 0, 0, 0, 0, 0, 0, 360, 0, 0, 0, '', '', '', '', 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 'Heretic_Live', 0, 0, NOW());

-- Diabolic Thorns → Stakes: Damage Add
REPLACE INTO `spell` (`Spell_ID`, `SpellID`, `ClientEffect`, `Icon`, `Name`, `Description`, `Target`, `Range`, `Power`, `CastTime`, `Damage`, `DamageType`, `Type`, `Duration`, `Frequency`, `Pulse`, `PulsePower`, `Radius`, `RecastDelay`, `ResurrectHealth`, `ResurrectMana`, `Value`, `Concentration`, `LifeDrainReturn`, `AmnesiaChance`, `Message1`, `Message2`, `Message3`, `Message4`, `InstrumentRequirement`, `SpellGroup`, `EffectGroup`, `SubSpellID`, `MoveCast`, `Uninterruptible`, `IsPrimary`, `IsSecondary`, `AllowBolt`, `SharedTimerGroup`, `PackageID`, `IsFocus`, `TooltipId`, `LastTimeRowUpdated`) VALUES
('diabolic_thorns_l6',    100120, 10200, 10200, 'Diabolic Thorns',    'Self damage add (melee).', 'Self', 0, 5,  3.0, 0, 13, 'DamageAdd', 1200000, 0, 0, 0, 0, 0, 0, 0, 1.2, 0, 0, 0, '', '', '', '', 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 'Heretic_Live', 0, 0, NOW()),
('diabolic_needles_l12',  100121, 10200, 10200, 'Diabolic Needles',   'Self damage add (melee).', 'Self', 0, 9,  3.0, 0, 13, 'DamageAdd', 1200000, 0, 0, 0, 0, 0, 0, 0, 2.0, 0, 0, 0, '', '', '', '', 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 'Heretic_Live', 0, 0, NOW()),
('diabolic_barbs_l19',    100122, 10200, 10200, 'Diabolic Barbs',     'Self damage add (melee).', 'Self', 0, 13, 3.0, 0, 13, 'DamageAdd', 1200000, 0, 0, 0, 0, 0, 0, 0, 2.8, 0, 0, 0, '', '', '', '', 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 'Heretic_Live', 0, 0, NOW()),
('diabolic_spikes_l26',   100123, 10200, 10200, 'Diabolic Spikes',    'Self damage add (melee).', 'Self', 0, 17, 3.0, 0, 13, 'DamageAdd', 1200000, 0, 0, 0, 0, 0, 0, 0, 3.4, 0, 0, 0, '', '', '', '', 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 'Heretic_Live', 0, 0, NOW()),
('diabolic_lances_l35',   100124, 10200, 10200, 'Diabolic Lances',    'Self damage add (melee).', 'Self', 0, 22, 3.0, 0, 13, 'DamageAdd', 1200000, 0, 0, 0, 0, 0, 0, 0, 4.0, 0, 0, 0, '', '', '', '', 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 'Heretic_Live', 0, 0, NOW()),
('diabolic_stakes_l44',   100125, 10200, 10200, 'Diabolic Stakes',    'Self damage add (melee).', 'Self', 0, 27, 3.0, 0, 13, 'DamageAdd', 1200000, 0, 0, 0, 0, 0, 0, 0, 4.6, 0, 0, 0, '', '', '', '', 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 'Heretic_Live', 0, 0, NOW());

-- Buffer of Steam → Lava: Ablative absorb
REPLACE INTO `spell` (`Spell_ID`, `SpellID`, `ClientEffect`, `Icon`, `Name`, `Description`, `Target`, `Range`, `Power`, `CastTime`, `Damage`, `DamageType`, `Type`, `Duration`, `Frequency`, `Pulse`, `PulsePower`, `Radius`, `RecastDelay`, `ResurrectHealth`, `ResurrectMana`, `Value`, `Concentration`, `LifeDrainReturn`, `AmnesiaChance`, `Message1`, `Message2`, `Message3`, `Message4`, `InstrumentRequirement`, `SpellGroup`, `EffectGroup`, `SubSpellID`, `MoveCast`, `Uninterruptible`, `IsPrimary`, `IsSecondary`, `AllowBolt`, `SharedTimerGroup`, `PackageID`, `IsFocus`, `TooltipId`, `LastTimeRowUpdated`) VALUES
('buffer_of_steam_l25',  100130, 10087, 10087, 'Buffer of Steam',   'Self absorb shield (15-30%).', 'Self', 0, 16, 3.0, 0, 0, 'AblativeArmor', 1200000, 0, 0, 0, 0, 0, 0, 0, 15, 0, 0, 0, '', '', '', '', 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 'Heretic_Live', 0, 0, NOW()),
('buffer_of_smoke_l30',  100131, 10087, 10087, 'Buffer of Smoke',   'Self absorb shield (15-30%).', 'Self', 0, 19, 3.0, 0, 0, 'AblativeArmor', 1200000, 0, 0, 0, 0, 0, 0, 0, 18, 0, 0, 0, '', '', '', '', 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 'Heretic_Live', 0, 0, NOW()),
('buffer_of_ash_l36',    100132, 10087, 10087, 'Buffer of Ash',     'Self absorb shield (15-30%).', 'Self', 0, 22, 3.0, 0, 0, 'AblativeArmor', 1200000, 0, 0, 0, 0, 0, 0, 0, 22, 0, 0, 0, '', '', '', '', 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 'Heretic_Live', 0, 0, NOW()),
('buffer_of_cinder_l42', 100133, 10087, 10087, 'Buffer of Cinder',  'Self absorb shield (15-30%).', 'Self', 0, 26, 3.0, 0, 0, 'AblativeArmor', 1200000, 0, 0, 0, 0, 0, 0, 0, 26, 0, 0, 0, '', '', '', '', 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 'Heretic_Live', 0, 0, NOW()),
('buffer_of_lava_l49',   100134, 10087, 10087, 'Buffer of Lava',    'Self absorb shield (15-30%).', 'Self', 0, 30, 3.0, 0, 0, 'AblativeArmor', 1200000, 0, 0, 0, 0, 0, 0, 0, 30, 0, 0, 0, '', '', '', '', 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 'Heretic_Live', 0, 0, NOW());

-- Infernal Carve → Slice: Melee Damage buff
REPLACE INTO `spell` (`Spell_ID`, `SpellID`, `ClientEffect`, `Icon`, `Name`, `Description`, `Target`, `Range`, `Power`, `CastTime`, `Damage`, `DamageType`, `Type`, `Duration`, `Frequency`, `Pulse`, `PulsePower`, `Radius`, `RecastDelay`, `ResurrectHealth`, `ResurrectMana`, `Value`, `Concentration`, `LifeDrainReturn`, `AmnesiaChance`, `Message1`, `Message2`, `Message3`, `Message4`, `InstrumentRequirement`, `SpellGroup`, `EffectGroup`, `SubSpellID`, `MoveCast`, `Uninterruptible`, `IsPrimary`, `IsSecondary`, `AllowBolt`, `SharedTimerGroup`, `PackageID`, `IsFocus`, `TooltipId`, `LastTimeRowUpdated`) VALUES
('infernal_carve_l11',  100140, 1566, 1566, 'Infernal Carve',   'Self melee damage buff.', 'Self', 0, 8,  3.0, 0, 0, 'MeleeDamageBuff', 1200000, 0, 0, 0, 0, 0, 0, 0, 2.1, 0, 0, 0, '', '', '', '', 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 'Heretic_Live', 0, 0, NOW()),
('infernal_cleave_l17', 100141, 1566, 1566, 'Infernal Cleave',  'Self melee damage buff.', 'Self', 0, 12, 3.0, 0, 0, 'MeleeDamageBuff', 1200000, 0, 0, 0, 0, 0, 0, 0, 3.6, 0, 0, 0, '', '', '', '', 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 'Heretic_Live', 0, 0, NOW()),
('infernal_rend_l24',   100142, 1566, 1566, 'Infernal Rend',    'Self melee damage buff.', 'Self', 0, 16, 3.0, 0, 0, 'MeleeDamageBuff', 1200000, 0, 0, 0, 0, 0, 0, 0, 5.1, 0, 0, 0, '', '', '', '', 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 'Heretic_Live', 0, 0, NOW()),
('infernal_sever_l31',  100143, 1566, 1566, 'Infernal Sever',   'Self melee damage buff.', 'Self', 0, 20, 3.0, 0, 0, 'MeleeDamageBuff', 1200000, 0, 0, 0, 0, 0, 0, 0, 6.6, 0, 0, 0, '', '', '', '', 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 'Heretic_Live', 0, 0, NOW()),
('infernal_shear_l39',  100144, 1566, 1566, 'Infernal Shear',   'Self melee damage buff.', 'Self', 0, 24, 3.0, 0, 0, 'MeleeDamageBuff', 1200000, 0, 0, 0, 0, 0, 0, 0, 8.0, 0, 0, 0, '', '', '', '', 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 'Heretic_Live', 0, 0, NOW()),
('infernal_slice_l46',  100145, 1566, 1566, 'Infernal Slice',   'Self melee damage buff.', 'Self', 0, 28, 3.0, 0, 0, 'MeleeDamageBuff', 1200000, 0, 0, 0, 0, 0, 0, 0, 9.4, 0, 0, 0, '', '', '', '', 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 'Heretic_Live', 0, 0, NOW());

-- Arawn's Precision → Cunning: Piercing Magic buff
REPLACE INTO `spell` (`Spell_ID`, `SpellID`, `ClientEffect`, `Icon`, `Name`, `Description`, `Target`, `Range`, `Power`, `CastTime`, `Damage`, `DamageType`, `Type`, `Duration`, `Frequency`, `Pulse`, `PulsePower`, `Radius`, `RecastDelay`, `ResurrectHealth`, `ResurrectMana`, `Value`, `Concentration`, `LifeDrainReturn`, `AmnesiaChance`, `Message1`, `Message2`, `Message3`, `Message4`, `InstrumentRequirement`, `SpellGroup`, `EffectGroup`, `SubSpellID`, `MoveCast`, `Uninterruptible`, `IsPrimary`, `IsSecondary`, `AllowBolt`, `SharedTimerGroup`, `PackageID`, `IsFocus`, `TooltipId`, `LastTimeRowUpdated`) VALUES
('arawns_precision_l10',  100150, 9221, 9221, 'Arawn\'s Precision',  'Self piercing magic buff.', 'Self', 0, 8,  3.0, 0, 0, 'HereticPiercingMagic', 1200000, 0, 0, 0, 0, 0, 0, 0, 1,  0, 0, 0, '', '', '', '', 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 'Heretic_Live', 0, 0, NOW()),
('arawns_acumen_l18',     100151, 9221, 9221, 'Arawn\'s Acumen',     'Self piercing magic buff.', 'Self', 0, 12, 3.0, 0, 0, 'HereticPiercingMagic', 1200000, 0, 0, 0, 0, 0, 0, 0, 2,  0, 0, 0, '', '', '', '', 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 'Heretic_Live', 0, 0, NOW()),
('arawns_insight_l26',    100152, 9221, 9221, 'Arawn\'s Insight',    'Self piercing magic buff.', 'Self', 0, 16, 3.0, 0, 0, 'HereticPiercingMagic', 1200000, 0, 0, 0, 0, 0, 0, 0, 4,  0, 0, 0, '', '', '', '', 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 'Heretic_Live', 0, 0, NOW()),
('arawns_clarity_l34',    100153, 9221, 9221, 'Arawn\'s Clarity',    'Self piercing magic buff.', 'Self', 0, 20, 3.0, 0, 0, 'HereticPiercingMagic', 1200000, 0, 0, 0, 0, 0, 0, 0, 6,  0, 0, 0, '', '', '', '', 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 'Heretic_Live', 0, 0, NOW()),
('arawns_wisdom_l42',     100154, 9221, 9221, 'Arawn\'s Wisdom',     'Self piercing magic buff.', 'Self', 0, 24, 3.0, 0, 0, 'HereticPiercingMagic', 1200000, 0, 0, 0, 0, 0, 0, 0, 8,  0, 0, 0, '', '', '', '', 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 'Heretic_Live', 0, 0, NOW()),
('arawns_cunning_l50',    100155, 9221, 9221, 'Arawn\'s Cunning',    'Self piercing magic buff.', 'Self', 0, 28, 3.0, 0, 0, 'HereticPiercingMagic', 1200000, 0, 0, 0, 0, 0, 0, 0, 10, 0, 0, 0, '', '', '', '', 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 'Heretic_Live', 0, 0, NOW());

-- ---- Step 3: linexspell — link to upstream speclines ---------------
-- Arawn's Fire DD/snare/DoT → Heretic Rejuvenation Spec specline
-- Cthonic Accretion buffs   → Heretic Enhancement Spec  specline
REPLACE INTO `linexspell` (`LineXSpell_ID`, `LineName`, `SpellID`, `Level`, `PackageID`, `LastTimeRowUpdated`) VALUES
-- Arawn's Fire → Heretic Rejuvenation Spec (specline of Rejuvenation)
('lxs_af_100000', 'Heretic Rejuvenation Spec', 100000, 1,  'Heretic_Live', NOW()),
('lxs_af_100001', 'Heretic Rejuvenation Spec', 100001, 5,  'Heretic_Live', NOW()),
('lxs_af_100002', 'Heretic Rejuvenation Spec', 100002, 10, 'Heretic_Live', NOW()),
('lxs_af_100003', 'Heretic Rejuvenation Spec', 100003, 18, 'Heretic_Live', NOW()),
('lxs_af_100004', 'Heretic Rejuvenation Spec', 100004, 28, 'Heretic_Live', NOW()),
('lxs_af_100005', 'Heretic Rejuvenation Spec', 100005, 38, 'Heretic_Live', NOW()),
('lxs_af_100010', 'Heretic Rejuvenation Spec', 100010, 3,  'Heretic_Live', NOW()),
('lxs_af_100011', 'Heretic Rejuvenation Spec', 100011, 9,  'Heretic_Live', NOW()),
('lxs_af_100012', 'Heretic Rejuvenation Spec', 100012, 16, 'Heretic_Live', NOW()),
('lxs_af_100013', 'Heretic Rejuvenation Spec', 100013, 25, 'Heretic_Live', NOW()),
('lxs_af_100014', 'Heretic Rejuvenation Spec', 100014, 35, 'Heretic_Live', NOW()),
('lxs_af_100015', 'Heretic Rejuvenation Spec', 100015, 47, 'Heretic_Live', NOW()),
('lxs_af_100020', 'Heretic Rejuvenation Spec', 100020, 1,  'Heretic_Live', NOW()),
('lxs_af_100021', 'Heretic Rejuvenation Spec', 100021, 5,  'Heretic_Live', NOW()),
('lxs_af_100022', 'Heretic Rejuvenation Spec', 100022, 11, 'Heretic_Live', NOW()),
('lxs_af_100023', 'Heretic Rejuvenation Spec', 100023, 18, 'Heretic_Live', NOW()),
('lxs_af_100024', 'Heretic Rejuvenation Spec', 100024, 28, 'Heretic_Live', NOW()),
('lxs_af_100025', 'Heretic Rejuvenation Spec', 100025, 41, 'Heretic_Live', NOW()),
('lxs_af_100030', 'Heretic Rejuvenation Spec', 100030, 30, 'Heretic_Live', NOW()),
('lxs_af_100031', 'Heretic Rejuvenation Spec', 100031, 40, 'Heretic_Live', NOW()),
('lxs_af_100032', 'Heretic Rejuvenation Spec', 100032, 48, 'Heretic_Live', NOW()),
('lxs_af_100040', 'Heretic Rejuvenation Spec', 100040, 2,  'Heretic_Live', NOW()),
('lxs_af_100041', 'Heretic Rejuvenation Spec', 100041, 8,  'Heretic_Live', NOW()),
('lxs_af_100042', 'Heretic Rejuvenation Spec', 100042, 16, 'Heretic_Live', NOW()),
('lxs_af_100043', 'Heretic Rejuvenation Spec', 100043, 25, 'Heretic_Live', NOW()),
('lxs_af_100044', 'Heretic Rejuvenation Spec', 100044, 35, 'Heretic_Live', NOW()),
('lxs_af_100045', 'Heretic Rejuvenation Spec', 100045, 46, 'Heretic_Live', NOW()),
('lxs_af_100050', 'Heretic Rejuvenation Spec', 100050, 1,  'Heretic_Live', NOW()),
('lxs_af_100051', 'Heretic Rejuvenation Spec', 100051, 7,  'Heretic_Live', NOW()),
('lxs_af_100052', 'Heretic Rejuvenation Spec', 100052, 15, 'Heretic_Live', NOW()),
('lxs_af_100053', 'Heretic Rejuvenation Spec', 100053, 25, 'Heretic_Live', NOW()),
('lxs_af_100054', 'Heretic Rejuvenation Spec', 100054, 35, 'Heretic_Live', NOW()),
('lxs_af_100055', 'Heretic Rejuvenation Spec', 100055, 48, 'Heretic_Live', NOW()),
('lxs_af_100060', 'Heretic Rejuvenation Spec', 100060, 7,  'Heretic_Live', NOW()),
-- Cthonic Accretion → Heretic Enhancement Spec (specline of Enhancement)
('lxs_ca_100100', 'Heretic Enhancement Spec', 100100, 1,  'Heretic_Live', NOW()),
('lxs_ca_100101', 'Heretic Enhancement Spec', 100101, 6,  'Heretic_Live', NOW()),
('lxs_ca_100102', 'Heretic Enhancement Spec', 100102, 12, 'Heretic_Live', NOW()),
('lxs_ca_100103', 'Heretic Enhancement Spec', 100103, 20, 'Heretic_Live', NOW()),
('lxs_ca_100104', 'Heretic Enhancement Spec', 100104, 30, 'Heretic_Live', NOW()),
('lxs_ca_100105', 'Heretic Enhancement Spec', 100105, 40, 'Heretic_Live', NOW()),
('lxs_ca_100106', 'Heretic Enhancement Spec', 100106, 50, 'Heretic_Live', NOW()),
('lxs_ca_100110', 'Heretic Enhancement Spec', 100110, 2,  'Heretic_Live', NOW()),
('lxs_ca_100111', 'Heretic Enhancement Spec', 100111, 8,  'Heretic_Live', NOW()),
('lxs_ca_100112', 'Heretic Enhancement Spec', 100112, 16, 'Heretic_Live', NOW()),
('lxs_ca_100113', 'Heretic Enhancement Spec', 100113, 25, 'Heretic_Live', NOW()),
('lxs_ca_100114', 'Heretic Enhancement Spec', 100114, 35, 'Heretic_Live', NOW()),
('lxs_ca_100115', 'Heretic Enhancement Spec', 100115, 43, 'Heretic_Live', NOW()),
('lxs_ca_100116', 'Heretic Enhancement Spec', 100116, 50, 'Heretic_Live', NOW()),
('lxs_ca_100120', 'Heretic Enhancement Spec', 100120, 1,  'Heretic_Live', NOW()),
('lxs_ca_100121', 'Heretic Enhancement Spec', 100121, 8,  'Heretic_Live', NOW()),
('lxs_ca_100122', 'Heretic Enhancement Spec', 100122, 16, 'Heretic_Live', NOW()),
('lxs_ca_100123', 'Heretic Enhancement Spec', 100123, 24, 'Heretic_Live', NOW()),
('lxs_ca_100124', 'Heretic Enhancement Spec', 100124, 33, 'Heretic_Live', NOW()),
('lxs_ca_100125', 'Heretic Enhancement Spec', 100125, 44, 'Heretic_Live', NOW()),
('lxs_ca_100130', 'Heretic Enhancement Spec', 100130, 10, 'Heretic_Live', NOW()),
('lxs_ca_100131', 'Heretic Enhancement Spec', 100131, 18, 'Heretic_Live', NOW()),
('lxs_ca_100132', 'Heretic Enhancement Spec', 100132, 28, 'Heretic_Live', NOW()),
('lxs_ca_100133', 'Heretic Enhancement Spec', 100133, 38, 'Heretic_Live', NOW()),
('lxs_ca_100134', 'Heretic Enhancement Spec', 100134, 49, 'Heretic_Live', NOW()),
('lxs_ca_100140', 'Heretic Enhancement Spec', 100140, 3,  'Heretic_Live', NOW()),
('lxs_ca_100141', 'Heretic Enhancement Spec', 100141, 11, 'Heretic_Live', NOW()),
('lxs_ca_100142', 'Heretic Enhancement Spec', 100142, 19, 'Heretic_Live', NOW()),
('lxs_ca_100143', 'Heretic Enhancement Spec', 100143, 28, 'Heretic_Live', NOW()),
('lxs_ca_100144', 'Heretic Enhancement Spec', 100144, 38, 'Heretic_Live', NOW()),
('lxs_ca_100145', 'Heretic Enhancement Spec', 100145, 46, 'Heretic_Live', NOW()),
('lxs_ca_100150', 'Heretic Enhancement Spec', 100150, 5,  'Heretic_Live', NOW()),
('lxs_ca_100151', 'Heretic Enhancement Spec', 100151, 15, 'Heretic_Live', NOW()),
('lxs_ca_100152', 'Heretic Enhancement Spec', 100152, 25, 'Heretic_Live', NOW()),
('lxs_ca_100153', 'Heretic Enhancement Spec', 100153, 34, 'Heretic_Live', NOW()),
('lxs_ca_100154', 'Heretic Enhancement Spec', 100154, 42, 'Heretic_Live', NOW()),
('lxs_ca_100155', 'Heretic Enhancement Spec', 100155, 50, 'Heretic_Live', NOW());

-- =====================================================================
-- End. Verify with:
--   SELECT LineName, COUNT(*) FROM linexspell WHERE PackageID='Heretic_Live' GROUP BY LineName;
--   Expected: 'Heretic Rejuvenation Spec' → 34, 'Heretic Enhancement Spec' → 37
-- =====================================================================

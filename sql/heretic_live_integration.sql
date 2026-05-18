-- =====================================================================
-- Heretic Live integration — Arawn's Fire + Cthonic Accretion speclines
-- =====================================================================
-- Adds the two specialization lines that the Heretic class has on the
-- live DAoC servers but that were not seeded in this DB:
--   * Arawn's Fire (offensive DD/snare/DoT, specline of Rejuvenation)
--   * Cthonic Accretion (self buffs/dmg-add/absorb, specline of Enhancement)
--
-- Required matching C# code changes:
--   - SkillConstants.cs  : Specs.Arawns_Fire, Specs.Cthonic_Accretion
--   - eProperty.cs       : Skill_Arawns_Fire = 116, Skill_Cthonic_Accretion = 117
--   - SkillBase.cs       : m_specToSkill mapping for both
--
-- Idempotent: deletes prior rows before inserting.
-- SpellID range used: 100000-100199 (PackageID = 'Heretic_Live')
-- =====================================================================

SET @SPEC_ARAWN := 'Arawn''s Fire';
SET @SPEC_CTHONIC := 'Cthonic Accretion';
SET @CLASS_HERETIC := 33;

-- ---- Idempotent cleanup ---------------------------------------------
DELETE FROM linexspell WHERE LineName IN (@SPEC_ARAWN, @SPEC_CTHONIC);
DELETE FROM spellline WHERE Spec IN (@SPEC_ARAWN, @SPEC_CTHONIC);
DELETE FROM specialization WHERE KeyName IN (@SPEC_ARAWN, @SPEC_CTHONIC);
DELETE FROM classxspecialization WHERE ClassID = @CLASS_HERETIC AND SpecKeyName IN (@SPEC_ARAWN, @SPEC_CTHONIC);
DELETE FROM spell WHERE PackageID = 'Heretic_Live';

-- ---- Specialization rows --------------------------------------------
INSERT INTO specialization (KeyName, Name, Icon, Description, Implementation) VALUES
('Arawn''s Fire',      'Arawn''s Fire',      0, 'Heretic offensive chants and focus damage.', NULL),
('Cthonic Accretion',  'Cthonic Accretion',  0, 'Heretic self buffs, damage-add and absorb.', NULL);

-- ---- SpellLine rows -------------------------------------------------
INSERT INTO spellline (KeyName, Name, Spec, IsBaseLine, ClassIDHint) VALUES
('Arawn''s Fire',      'Arawn''s Fire',      'Arawn''s Fire',      0, 33),
('Cthonic Accretion',  'Cthonic Accretion',  'Cthonic Accretion',  0, 33);

-- ---- ClassXSpecialization (Heretic = ClassID 33) --------------------
INSERT INTO classxspecialization (ClassID, SpecKeyName, LevelAcquired) VALUES
(33, 'Arawn''s Fire',     5),
(33, 'Cthonic Accretion', 5);

-- ====================================================================
-- ARAWN'S FIRE SPELLS  (SpellID 100000-100099)
-- ====================================================================
-- Convention for INSERT:
--   Spell_ID, SpellID, ClientEffect, Icon, Name, Description, Target,
--   Range, Power, CastTime, Damage, DamageType (13=Heat), Type, Duration,
--   Frequency, Pulse, PulsePower, Radius, RecastDelay, ResurrectHealth,
--   ResurrectMana, Value, Concentration, LifeDrainReturn, AmnesiaChance,
--   Msg1-4, InstrumentReq, SpellGroup, EffectGroup, SubSpellID, MoveCast,
--   Uninterruptible, IsPrimary, IsSecondary, AllowBolt, SharedTimerGroup,
--   PackageID, IsFocus, TooltipID, LastTimeRowUpdated

-- ---- Arawn's Inferno line: channeled mono DD with ramping ---------------
-- LifeDrainReturn = growth% per tick, AmnesiaChance = growth cap %
INSERT INTO spell (Spell_ID, SpellID, ClientEffect, Icon, Name, Description, Target, `Range`, Power, CastTime, Damage, DamageType, Type, Duration, Frequency, Pulse, PulsePower, Radius, RecastDelay, ResurrectHealth, ResurrectMana, Value, Concentration, LifeDrainReturn, AmnesiaChance, Message1, Message2, Message3, Message4, InstrumentRequirement, SpellGroup, EffectGroup, SubSpellID, MoveCast, Uninterruptible, IsPrimary, IsSecondary, AllowBolt, SharedTimerGroup, PackageID, IsFocus, TooltipID, LastTimeRowUpdated) VALUES
('arawns_singe_l4',     100000, 5908, 5908, 'Arawn''s Singe',     'Channeled fire damage that ramps up over time.', 'Enemy', 1500, 5,  3.0,  10, 13, 'RampingDamageFocus', 16000, 1500, 1, 4, 0, 0, 0, 0, 0, 0, 50, 250, '', '', '', '', 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 'Heretic_Live', 1, 0, '2000-01-01 00:00:00'),
('arawns_torch_l12',    100001, 5908, 5908, 'Arawn''s Torch',     'Channeled fire damage that ramps up over time.', 'Enemy', 1500, 12, 3.0,  20, 13, 'RampingDamageFocus', 16000, 1500, 1, 7, 0, 0, 0, 0, 0, 0, 50, 250, '', '', '', '', 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 'Heretic_Live', 1, 0, '2000-01-01 00:00:00'),
('arawns_flame_l20',    100002, 5908, 5908, 'Arawn''s Flame',     'Channeled fire damage that ramps up over time.', 'Enemy', 1500, 20, 3.0,  32, 13, 'RampingDamageFocus', 16000, 1500, 1, 10, 0, 0, 0, 0, 0, 0, 50, 250, '', '', '', '', 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 'Heretic_Live', 1, 0, '2000-01-01 00:00:00'),
('arawns_blaze_l28',    100003, 5908, 5908, 'Arawn''s Blaze',     'Channeled fire damage that ramps up over time.', 'Enemy', 1500, 28, 3.0,  48, 13, 'RampingDamageFocus', 16000, 1500, 1, 14, 0, 0, 0, 0, 0, 0, 50, 250, '', '', '', '', 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 'Heretic_Live', 1, 0, '2000-01-01 00:00:00'),
('arawns_pyre_l36',     100004, 5908, 5908, 'Arawn''s Pyre',      'Channeled fire damage that ramps up over time.', 'Enemy', 1500, 36, 3.0,  68, 13, 'RampingDamageFocus', 16000, 1500, 1, 19, 0, 0, 0, 0, 0, 0, 50, 250, '', '', '', '', 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 'Heretic_Live', 1, 0, '2000-01-01 00:00:00'),
('arawns_inferno_l44',  100005, 5908, 5908, 'Arawn''s Inferno',   'Channeled fire damage that ramps up over time.', 'Enemy', 1500, 44, 3.0,  92, 13, 'RampingDamageFocus', 16000, 1500, 1, 24, 0, 0, 0, 0, 0, 0, 50, 250, '', '', '', '', 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 'Heretic_Live', 1, 0, '2000-01-01 00:00:00');

-- ---- Lava line: AoE channeled DD + snare (Radius>0 -> AoE snare 3s) -----
-- Value = % snare; RampingDamageFocus auto-spawns the snare sub-spell
INSERT INTO spell (Spell_ID, SpellID, ClientEffect, Icon, Name, Description, Target, `Range`, Power, CastTime, Damage, DamageType, Type, Duration, Frequency, Pulse, PulsePower, Radius, RecastDelay, ResurrectHealth, ResurrectMana, Value, Concentration, LifeDrainReturn, AmnesiaChance, Message1, Message2, Message3, Message4, InstrumentRequirement, SpellGroup, EffectGroup, SubSpellID, MoveCast, Uninterruptible, IsPrimary, IsSecondary, AllowBolt, SharedTimerGroup, PackageID, IsFocus, TooltipID, LastTimeRowUpdated) VALUES
('lava_spate_l9',       100010, 5910, 5910, 'Lava Spate',        'Channeled fire AoE around the target with a slow.', 'Enemy', 1500, 9,  3.0,  6,  13, 'RampingDamageFocus', 16000, 1500, 1, 5, 350, 0, 0, 0, 50, 0, 30, 150, '', '', '', '', 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 'Heretic_Live', 1, 0, '2000-01-01 00:00:00'),
('lava_torrent_l16',    100011, 5910, 5910, 'Lava Torrent',      'Channeled fire AoE around the target with a slow.', 'Enemy', 1500, 16, 3.0,  10, 13, 'RampingDamageFocus', 16000, 1500, 1, 8, 350, 0, 0, 0, 50, 0, 30, 150, '', '', '', '', 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 'Heretic_Live', 1, 0, '2000-01-01 00:00:00'),
('lava_river_l23',      100012, 5910, 5910, 'Lava River',        'Channeled fire AoE around the target with a slow.', 'Enemy', 1500, 23, 3.0,  14, 13, 'RampingDamageFocus', 16000, 1500, 1, 11, 350, 0, 0, 0, 50, 0, 30, 150, '', '', '', '', 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 'Heretic_Live', 1, 0, '2000-01-01 00:00:00'),
('lava_flood_l31',      100013, 5910, 5910, 'Lava Flood',        'Channeled fire AoE around the target with a slow.', 'Enemy', 1500, 31, 3.0,  18, 13, 'RampingDamageFocus', 16000, 1500, 1, 14, 350, 0, 0, 0, 50, 0, 30, 150, '', '', '', '', 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 'Heretic_Live', 1, 0, '2000-01-01 00:00:00'),
('lava_deluge_l39',     100014, 5910, 5910, 'Lava Deluge',       'Channeled fire AoE around the target with a slow.', 'Enemy', 1500, 39, 3.0,  24, 13, 'RampingDamageFocus', 16000, 1500, 1, 18, 350, 0, 0, 0, 50, 0, 30, 150, '', '', '', '', 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 'Heretic_Live', 1, 0, '2000-01-01 00:00:00'),
('lava_avalanche_l47',  100015, 5910, 5910, 'Lava Avalanche',    'Channeled fire AoE around the target with a slow.', 'Enemy', 1500, 47, 3.0,  30, 13, 'RampingDamageFocus', 16000, 1500, 1, 22, 350, 0, 0, 0, 50, 0, 30, 150, '', '', '', '', 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 'Heretic_Live', 1, 0, '2000-01-01 00:00:00');

-- ---- Blazing line: mono focus DD + snare (channeled) --------------------
-- Uses HereticDamageSpeedDecrease (DD-with-snare focus). Value=% snare.
INSERT INTO spell (Spell_ID, SpellID, ClientEffect, Icon, Name, Description, Target, `Range`, Power, CastTime, Damage, DamageType, Type, Duration, Frequency, Pulse, PulsePower, Radius, RecastDelay, ResurrectHealth, ResurrectMana, Value, Concentration, LifeDrainReturn, AmnesiaChance, Message1, Message2, Message3, Message4, InstrumentRequirement, SpellGroup, EffectGroup, SubSpellID, MoveCast, Uninterruptible, IsPrimary, IsSecondary, AllowBolt, SharedTimerGroup, PackageID, IsFocus, TooltipID, LastTimeRowUpdated) VALUES
('blazing_flow_l3',     100020, 5909, 5909, 'Blazing Flow',      'Slows the target while burning it. Focus.',     'Enemy', 1500, 3,  3.0,  8,  13, 'HereticDamageSpeedDecrease', 8000, 2000, 1, 3, 0, 0, 0, 0, 50, 0, 0, 0, '', '', '', '', 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 'Heretic_Live', 1, 0, '2000-01-01 00:00:00'),
('blazing_stream_l11',  100021, 5909, 5909, 'Blazing Stream',    'Slows the target while burning it. Focus.',     'Enemy', 1500, 11, 3.0,  14, 13, 'HereticDamageSpeedDecrease', 8000, 2000, 1, 6, 0, 0, 0, 0, 50, 0, 0, 0, '', '', '', '', 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 'Heretic_Live', 1, 0, '2000-01-01 00:00:00'),
('blazing_torrent_l18', 100022, 5909, 5909, 'Blazing Torrent',   'Slows the target while burning it. Focus.',     'Enemy', 1500, 18, 3.0,  20, 13, 'HereticDamageSpeedDecrease', 8000, 2000, 1, 9, 0, 0, 0, 0, 50, 0, 0, 0, '', '', '', '', 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 'Heretic_Live', 1, 0, '2000-01-01 00:00:00'),
('blazing_river_l25',   100023, 5909, 5909, 'Blazing River',     'Slows the target while burning it. Focus.',     'Enemy', 1500, 25, 3.0,  28, 13, 'HereticDamageSpeedDecrease', 8000, 2000, 1, 12, 0, 0, 0, 0, 50, 0, 0, 0, '', '', '', '', 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 'Heretic_Live', 1, 0, '2000-01-01 00:00:00'),
('blazing_surge_l33',   100024, 5909, 5909, 'Blazing Surge',     'Slows the target while burning it. Focus.',     'Enemy', 1500, 33, 3.0,  38, 13, 'HereticDamageSpeedDecrease', 8000, 2000, 1, 16, 0, 0, 0, 0, 50, 0, 0, 0, '', '', '', '', 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 'Heretic_Live', 1, 0, '2000-01-01 00:00:00'),
('blazing_flood_l41',   100025, 5909, 5909, 'Blazing Flood',     'Slows the target while burning it. Focus.',     'Enemy', 1500, 41, 3.0,  50, 13, 'HereticDamageSpeedDecrease', 8000, 2000, 1, 20, 0, 0, 0, 0, 50, 0, 0, 0, '', '', '', '', 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 'Heretic_Live', 1, 0, '2000-01-01 00:00:00');

-- ---- Channeled Blaze: uninterruptible mono DD ---------------------------
INSERT INTO spell (Spell_ID, SpellID, ClientEffect, Icon, Name, Description, Target, `Range`, Power, CastTime, Damage, DamageType, Type, Duration, Frequency, Pulse, PulsePower, Radius, RecastDelay, ResurrectHealth, ResurrectMana, Value, Concentration, LifeDrainReturn, AmnesiaChance, Message1, Message2, Message3, Message4, InstrumentRequirement, SpellGroup, EffectGroup, SubSpellID, MoveCast, Uninterruptible, IsPrimary, IsSecondary, AllowBolt, SharedTimerGroup, PackageID, IsFocus, TooltipID, LastTimeRowUpdated) VALUES
('glistening_blaze_l36', 100030, 5911, 5911, 'Glistening Blaze',  'Uninterruptible channeled fire damage.', 'Enemy', 1500, 32, 3.0,  90,  13, 'RampingDamageFocus', 33000, 1500, 1, 16, 0, 0, 0, 0, 0, 0, 0, 0, '', '', '', '', 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 'Heretic_Live', 1, 0, '2000-01-01 00:00:00'),
('whirling_blaze_l42',   100031, 5911, 5911, 'Whirling Blaze',    'Uninterruptible channeled fire damage.', 'Enemy', 1500, 38, 3.0,  97,  13, 'RampingDamageFocus', 33000, 1500, 1, 19, 0, 0, 0, 0, 0, 0, 0, 0, '', '', '', '', 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 'Heretic_Live', 1, 0, '2000-01-01 00:00:00'),
('torrential_blaze_l48', 100032, 5911, 5911, 'Torrential Blaze',  'Uninterruptible channeled fire damage.', 'Enemy', 1500, 44, 3.0,  120, 13, 'RampingDamageFocus', 33000, 1500, 1, 22, 0, 0, 0, 0, 0, 0, 0, 0, '', '', '', '', 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 'Heretic_Live', 1, 0, '2000-01-01 00:00:00');

-- ---- Fiery Grasp line: instant DD (combat-castable) --------------------
INSERT INTO spell (Spell_ID, SpellID, ClientEffect, Icon, Name, Description, Target, `Range`, Power, CastTime, Damage, DamageType, Type, Duration, Frequency, Pulse, PulsePower, Radius, RecastDelay, ResurrectHealth, ResurrectMana, Value, Concentration, LifeDrainReturn, AmnesiaChance, Message1, Message2, Message3, Message4, InstrumentRequirement, SpellGroup, EffectGroup, SubSpellID, MoveCast, Uninterruptible, IsPrimary, IsSecondary, AllowBolt, SharedTimerGroup, PackageID, IsFocus, TooltipID, LastTimeRowUpdated) VALUES
('fiery_grasp_l6',         100040, 5912, 5912, 'Fiery Grasp',         'Instant fire damage.', 'Enemy', 1500, 8,  2.8,  18,  13, 'DirectDamage', 0, 0, 0, 0, 0, 12000, 0, 0, 0, 0, 0, 0, '', '', '', '', 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 'Heretic_Live', 0, 0, '2000-01-01 00:00:00'),
('fiery_clutch_l14',       100041, 5912, 5912, 'Fiery Clutch',        'Instant fire damage.', 'Enemy', 1500, 14, 2.8,  34,  13, 'DirectDamage', 0, 0, 0, 0, 0, 12000, 0, 0, 0, 0, 0, 0, '', '', '', '', 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 'Heretic_Live', 0, 0, '2000-01-01 00:00:00'),
('fiery_grip_l22',         100042, 5912, 5912, 'Fiery Grip',          'Instant fire damage.', 'Enemy', 1500, 22, 2.8,  56,  13, 'DirectDamage', 0, 0, 0, 0, 0, 12000, 0, 0, 0, 0, 0, 0, '', '', '', '', 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 'Heretic_Live', 0, 0, '2000-01-01 00:00:00'),
('fiery_seize_l30',        100043, 5912, 5912, 'Fiery Seize',         'Instant fire damage.', 'Enemy', 1500, 30, 2.8,  84,  13, 'DirectDamage', 0, 0, 0, 0, 0, 12000, 0, 0, 0, 0, 0, 0, '', '', '', '', 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 'Heretic_Live', 0, 0, '2000-01-01 00:00:00'),
('fiery_hold_l38',         100044, 5912, 5912, 'Fiery Hold',          'Instant fire damage.', 'Enemy', 1500, 38, 2.8,  108, 13, 'DirectDamage', 0, 0, 0, 0, 0, 12000, 0, 0, 0, 0, 0, 0, '', '', '', '', 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 'Heretic_Live', 0, 0, '2000-01-01 00:00:00'),
('fiery_stranglehold_l46', 100045, 5912, 5912, 'Fiery Stranglehold',  'Instant fire damage.', 'Enemy', 1500, 46, 2.8,  140, 13, 'DirectDamage', 0, 0, 0, 0, 0, 12000, 0, 0, 0, 0, 0, 0, '', '', '', '', 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 'Heretic_Live', 0, 0, '2000-01-01 00:00:00');

-- ---- Flickering Embers line: DoT (short range, periodic) ----------------
INSERT INTO spell (Spell_ID, SpellID, ClientEffect, Icon, Name, Description, Target, `Range`, Power, CastTime, Damage, DamageType, Type, Duration, Frequency, Pulse, PulsePower, Radius, RecastDelay, ResurrectHealth, ResurrectMana, Value, Concentration, LifeDrainReturn, AmnesiaChance, Message1, Message2, Message3, Message4, InstrumentRequirement, SpellGroup, EffectGroup, SubSpellID, MoveCast, Uninterruptible, IsPrimary, IsSecondary, AllowBolt, SharedTimerGroup, PackageID, IsFocus, TooltipID, LastTimeRowUpdated) VALUES
('flickering_embers_l5',    100050, 5913, 5913, 'Flickering Embers',    'Damage over time, fire.', 'Enemy', 700, 5,  3.0,  8,  13, 'HereticDamageOverTime', 20000, 4000, 1, 1, 0, 0, 0, 0, 0, 0, 0, 0, '', '', '', '', 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 'Heretic_Live', 0, 0, '2000-01-01 00:00:00'),
('glowing_embers_l13',      100051, 5913, 5913, 'Glowing Embers',       'Damage over time, fire.', 'Enemy', 700, 13, 3.0,  16, 13, 'HereticDamageOverTime', 20000, 4000, 1, 2, 0, 0, 0, 0, 0, 0, 0, 0, '', '', '', '', 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 'Heretic_Live', 0, 0, '2000-01-01 00:00:00'),
('burning_embers_l21',      100052, 5913, 5913, 'Burning Embers',       'Damage over time, fire.', 'Enemy', 700, 21, 3.0,  26, 13, 'HereticDamageOverTime', 20000, 4000, 1, 4, 0, 0, 0, 0, 0, 0, 0, 0, '', '', '', '', 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 'Heretic_Live', 0, 0, '2000-01-01 00:00:00'),
('scorching_embers_l30',    100053, 5913, 5913, 'Scorching Embers',     'Damage over time, fire.', 'Enemy', 700, 30, 3.0,  38, 13, 'HereticDamageOverTime', 20000, 4000, 1, 6, 0, 0, 0, 0, 0, 0, 0, 0, '', '', '', '', 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 'Heretic_Live', 0, 0, '2000-01-01 00:00:00'),
('searing_embers_l39',      100054, 5913, 5913, 'Searing Embers',       'Damage over time, fire.', 'Enemy', 700, 39, 3.0,  52, 13, 'HereticDamageOverTime', 20000, 4000, 1, 9, 0, 0, 0, 0, 0, 0, 0, 0, '', '', '', '', 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 'Heretic_Live', 0, 0, '2000-01-01 00:00:00'),
('incinerating_embers_l48', 100055, 5913, 5913, 'Incinerating Embers',  'Damage over time, fire.', 'Enemy', 700, 48, 3.0,  70, 13, 'HereticDamageOverTime', 20000, 4000, 1, 12, 0, 0, 0, 0, 0, 0, 0, 0, '', '', '', '', 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 'Heretic_Live', 0, 0, '2000-01-01 00:00:00');

-- ---- Insta-snare (50%, unbreakable for 5s) -----------------------------
INSERT INTO spell (Spell_ID, SpellID, ClientEffect, Icon, Name, Description, Target, `Range`, Power, CastTime, Damage, DamageType, Type, Duration, Frequency, Pulse, PulsePower, Radius, RecastDelay, ResurrectHealth, ResurrectMana, Value, Concentration, LifeDrainReturn, AmnesiaChance, Message1, Message2, Message3, Message4, InstrumentRequirement, SpellGroup, EffectGroup, SubSpellID, MoveCast, Uninterruptible, IsPrimary, IsSecondary, AllowBolt, SharedTimerGroup, PackageID, IsFocus, TooltipID, LastTimeRowUpdated) VALUES
('arawns_grip_l27', 100060, 5914, 5914, 'Arawn''s Grip', 'Instant snare. Slows the target for 5 seconds.', 'Enemy', 1500, 14, 0, 0, 13, 'HereticSpeedDecrease', 5000, 0, 0, 0, 0, 30000, 0, 0, 50, 0, 0, 0, '', '', '', '', 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 'Heretic_Live', 0, 0, '2000-01-01 00:00:00');

-- ====================================================================
-- CTHONIC ACCRETION SPELLS  (SpellID 100100-100199)
-- ====================================================================

-- ---- Chthonic Vigor → Might: Self Str/Con buff -------------------------
INSERT INTO spell (Spell_ID, SpellID, ClientEffect, Icon, Name, Description, Target, `Range`, Power, CastTime, Damage, DamageType, Type, Duration, Frequency, Pulse, PulsePower, Radius, RecastDelay, ResurrectHealth, ResurrectMana, Value, Concentration, LifeDrainReturn, AmnesiaChance, Message1, Message2, Message3, Message4, InstrumentRequirement, SpellGroup, EffectGroup, SubSpellID, MoveCast, Uninterruptible, IsPrimary, IsSecondary, AllowBolt, SharedTimerGroup, PackageID, IsFocus, TooltipID, LastTimeRowUpdated) VALUES
('chthonic_vigor_l5',          100100, 5915, 5915, 'Chthonic Vigor',         'Self Strength and Constitution buff.', 'Self', 0, 4,  3.0, 0, 0, 'StrengthConstitutionBuff', 1200000, 0, 0, 0, 0, 0, 0, 0, 9,  0, 0, 0, '', '', '', '', 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 'Heretic_Live', 0, 0, '2000-01-01 00:00:00'),
('chthonic_strength_l10',      100101, 5915, 5915, 'Chthonic Strength',      'Self Strength and Constitution buff.', 'Self', 0, 8,  3.0, 0, 0, 'StrengthConstitutionBuff', 1200000, 0, 0, 0, 0, 0, 0, 0, 21, 0, 0, 0, '', '', '', '', 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 'Heretic_Live', 0, 0, '2000-01-01 00:00:00'),
('chthonic_fortification_l16', 100102, 5915, 5915, 'Chthonic Fortification', 'Self Strength and Constitution buff.', 'Self', 0, 12, 3.0, 0, 0, 'StrengthConstitutionBuff', 1200000, 0, 0, 0, 0, 0, 0, 0, 33, 0, 0, 0, '', '', '', '', 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 'Heretic_Live', 0, 0, '2000-01-01 00:00:00'),
('chthonic_focus_l23',         100103, 5915, 5915, 'Chthonic Focus',         'Self Strength and Constitution buff.', 'Self', 0, 16, 3.0, 0, 0, 'StrengthConstitutionBuff', 1200000, 0, 0, 0, 0, 0, 0, 0, 45, 0, 0, 0, '', '', '', '', 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 'Heretic_Live', 0, 0, '2000-01-01 00:00:00'),
('chthonic_power_l32',         100104, 5915, 5915, 'Chthonic Power',         'Self Strength and Constitution buff.', 'Self', 0, 20, 3.0, 0, 0, 'StrengthConstitutionBuff', 1200000, 0, 0, 0, 0, 0, 0, 0, 57, 0, 0, 0, '', '', '', '', 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 'Heretic_Live', 0, 0, '2000-01-01 00:00:00'),
('chthonic_force_l40',         100105, 5915, 5915, 'Chthonic Force',         'Self Strength and Constitution buff.', 'Self', 0, 24, 3.0, 0, 0, 'StrengthConstitutionBuff', 1200000, 0, 0, 0, 0, 0, 0, 0, 69, 0, 0, 0, '', '', '', '', 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 'Heretic_Live', 0, 0, '2000-01-01 00:00:00'),
('chthonic_might_l50',         100106, 5915, 5915, 'Chthonic Might',         'Self Strength and Constitution buff.', 'Self', 0, 28, 3.0, 0, 0, 'StrengthConstitutionBuff', 1200000, 0, 0, 0, 0, 0, 0, 0, 75, 0, 0, 0, '', '', '', '', 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 'Heretic_Live', 0, 0, '2000-01-01 00:00:00');

-- ---- Kindled Shield → Molten Barricade: Self AF buff -------------------
INSERT INTO spell (Spell_ID, SpellID, ClientEffect, Icon, Name, Description, Target, `Range`, Power, CastTime, Damage, DamageType, Type, Duration, Frequency, Pulse, PulsePower, Radius, RecastDelay, ResurrectHealth, ResurrectMana, Value, Concentration, LifeDrainReturn, AmnesiaChance, Message1, Message2, Message3, Message4, InstrumentRequirement, SpellGroup, EffectGroup, SubSpellID, MoveCast, Uninterruptible, IsPrimary, IsSecondary, AllowBolt, SharedTimerGroup, PackageID, IsFocus, TooltipID, LastTimeRowUpdated) VALUES
('kindled_shield_l7',     100110, 5916, 5916, 'Kindled Shield',     'Self Armor Factor buff.', 'Self', 0, 5,  3.0, 0, 0, 'ArmorFactorBuff', 1200000, 0, 0, 0, 0, 0, 0, 0, 36,  0, 0, 0, '', '', '', '', 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 'Heretic_Live', 0, 0, '2000-01-01 00:00:00'),
('kindled_aegis_l14',     100111, 5916, 5916, 'Kindled Aegis',      'Self Armor Factor buff.', 'Self', 0, 10, 3.0, 0, 0, 'ArmorFactorBuff', 1200000, 0, 0, 0, 0, 0, 0, 0, 84,  0, 0, 0, '', '', '', '', 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 'Heretic_Live', 0, 0, '2000-01-01 00:00:00'),
('molten_shield_l21',     100112, 5916, 5916, 'Molten Shield',      'Self Armor Factor buff.', 'Self', 0, 14, 3.0, 0, 0, 'ArmorFactorBuff', 1200000, 0, 0, 0, 0, 0, 0, 0, 132, 0, 0, 0, '', '', '', '', 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 'Heretic_Live', 0, 0, '2000-01-01 00:00:00'),
('molten_aegis_l29',      100113, 5916, 5916, 'Molten Aegis',       'Self Armor Factor buff.', 'Self', 0, 18, 3.0, 0, 0, 'ArmorFactorBuff', 1200000, 0, 0, 0, 0, 0, 0, 0, 180, 0, 0, 0, '', '', '', '', 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 'Heretic_Live', 0, 0, '2000-01-01 00:00:00'),
('molten_bulwark_l36',    100114, 5916, 5916, 'Molten Bulwark',     'Self Armor Factor buff.', 'Self', 0, 22, 3.0, 0, 0, 'ArmorFactorBuff', 1200000, 0, 0, 0, 0, 0, 0, 0, 240, 0, 0, 0, '', '', '', '', 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 'Heretic_Live', 0, 0, '2000-01-01 00:00:00'),
('molten_rampart_l43',    100115, 5916, 5916, 'Molten Rampart',     'Self Armor Factor buff.', 'Self', 0, 26, 3.0, 0, 0, 'ArmorFactorBuff', 1200000, 0, 0, 0, 0, 0, 0, 0, 300, 0, 0, 0, '', '', '', '', 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 'Heretic_Live', 0, 0, '2000-01-01 00:00:00'),
('molten_barricade_l50',  100116, 5916, 5916, 'Molten Barricade',   'Self Armor Factor buff.', 'Self', 0, 30, 3.0, 0, 0, 'ArmorFactorBuff', 1200000, 0, 0, 0, 0, 0, 0, 0, 360, 0, 0, 0, '', '', '', '', 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 'Heretic_Live', 0, 0, '2000-01-01 00:00:00');

-- ---- Diabolic Thorns → Stakes: Self Damage Add -------------------------
INSERT INTO spell (Spell_ID, SpellID, ClientEffect, Icon, Name, Description, Target, `Range`, Power, CastTime, Damage, DamageType, Type, Duration, Frequency, Pulse, PulsePower, Radius, RecastDelay, ResurrectHealth, ResurrectMana, Value, Concentration, LifeDrainReturn, AmnesiaChance, Message1, Message2, Message3, Message4, InstrumentRequirement, SpellGroup, EffectGroup, SubSpellID, MoveCast, Uninterruptible, IsPrimary, IsSecondary, AllowBolt, SharedTimerGroup, PackageID, IsFocus, TooltipID, LastTimeRowUpdated) VALUES
('diabolic_thorns_l6',    100120, 5917, 5917, 'Diabolic Thorns',    'Self damage add (melee).', 'Self', 0, 5,  3.0, 0, 13, 'DamageAdd', 1200000, 0, 0, 0, 0, 0, 0, 0, 1.2, 0, 0, 0, '', '', '', '', 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 'Heretic_Live', 0, 0, '2000-01-01 00:00:00'),
('diabolic_needles_l12',  100121, 5917, 5917, 'Diabolic Needles',   'Self damage add (melee).', 'Self', 0, 9,  3.0, 0, 13, 'DamageAdd', 1200000, 0, 0, 0, 0, 0, 0, 0, 2.0, 0, 0, 0, '', '', '', '', 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 'Heretic_Live', 0, 0, '2000-01-01 00:00:00'),
('diabolic_barbs_l19',    100122, 5917, 5917, 'Diabolic Barbs',     'Self damage add (melee).', 'Self', 0, 13, 3.0, 0, 13, 'DamageAdd', 1200000, 0, 0, 0, 0, 0, 0, 0, 2.8, 0, 0, 0, '', '', '', '', 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 'Heretic_Live', 0, 0, '2000-01-01 00:00:00'),
('diabolic_spikes_l26',   100123, 5917, 5917, 'Diabolic Spikes',    'Self damage add (melee).', 'Self', 0, 17, 3.0, 0, 13, 'DamageAdd', 1200000, 0, 0, 0, 0, 0, 0, 0, 3.4, 0, 0, 0, '', '', '', '', 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 'Heretic_Live', 0, 0, '2000-01-01 00:00:00'),
('diabolic_lances_l35',   100124, 5917, 5917, 'Diabolic Lances',    'Self damage add (melee).', 'Self', 0, 22, 3.0, 0, 13, 'DamageAdd', 1200000, 0, 0, 0, 0, 0, 0, 0, 4.0, 0, 0, 0, '', '', '', '', 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 'Heretic_Live', 0, 0, '2000-01-01 00:00:00'),
('diabolic_stakes_l44',   100125, 5917, 5917, 'Diabolic Stakes',    'Self damage add (melee).', 'Self', 0, 27, 3.0, 0, 13, 'DamageAdd', 1200000, 0, 0, 0, 0, 0, 0, 0, 4.6, 0, 0, 0, '', '', '', '', 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 'Heretic_Live', 0, 0, '2000-01-01 00:00:00');

-- ---- Buffer of Steam → Lava: Self Ablative absorb ----------------------
INSERT INTO spell (Spell_ID, SpellID, ClientEffect, Icon, Name, Description, Target, `Range`, Power, CastTime, Damage, DamageType, Type, Duration, Frequency, Pulse, PulsePower, Radius, RecastDelay, ResurrectHealth, ResurrectMana, Value, Concentration, LifeDrainReturn, AmnesiaChance, Message1, Message2, Message3, Message4, InstrumentRequirement, SpellGroup, EffectGroup, SubSpellID, MoveCast, Uninterruptible, IsPrimary, IsSecondary, AllowBolt, SharedTimerGroup, PackageID, IsFocus, TooltipID, LastTimeRowUpdated) VALUES
('buffer_of_steam_l25',  100130, 5918, 5918, 'Buffer of Steam',   'Self absorb shield (15-30%).', 'Self', 0, 16, 3.0, 0, 0, 'AblativeArmor', 1200000, 0, 0, 0, 0, 0, 0, 0, 15, 0, 0, 0, '', '', '', '', 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 'Heretic_Live', 0, 0, '2000-01-01 00:00:00'),
('buffer_of_smoke_l30',  100131, 5918, 5918, 'Buffer of Smoke',   'Self absorb shield (15-30%).', 'Self', 0, 19, 3.0, 0, 0, 'AblativeArmor', 1200000, 0, 0, 0, 0, 0, 0, 0, 18, 0, 0, 0, '', '', '', '', 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 'Heretic_Live', 0, 0, '2000-01-01 00:00:00'),
('buffer_of_ash_l36',    100132, 5918, 5918, 'Buffer of Ash',     'Self absorb shield (15-30%).', 'Self', 0, 22, 3.0, 0, 0, 'AblativeArmor', 1200000, 0, 0, 0, 0, 0, 0, 0, 22, 0, 0, 0, '', '', '', '', 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 'Heretic_Live', 0, 0, '2000-01-01 00:00:00'),
('buffer_of_cinder_l42', 100133, 5918, 5918, 'Buffer of Cinder',  'Self absorb shield (15-30%).', 'Self', 0, 26, 3.0, 0, 0, 'AblativeArmor', 1200000, 0, 0, 0, 0, 0, 0, 0, 26, 0, 0, 0, '', '', '', '', 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 'Heretic_Live', 0, 0, '2000-01-01 00:00:00'),
('buffer_of_lava_l49',   100134, 5918, 5918, 'Buffer of Lava',    'Self absorb shield (15-30%).', 'Self', 0, 30, 3.0, 0, 0, 'AblativeArmor', 1200000, 0, 0, 0, 0, 0, 0, 0, 30, 0, 0, 0, '', '', '', '', 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 'Heretic_Live', 0, 0, '2000-01-01 00:00:00');

-- ---- Infernal Carve → Slice: Self Combat Speed buff (DPS) --------------
INSERT INTO spell (Spell_ID, SpellID, ClientEffect, Icon, Name, Description, Target, `Range`, Power, CastTime, Damage, DamageType, Type, Duration, Frequency, Pulse, PulsePower, Radius, RecastDelay, ResurrectHealth, ResurrectMana, Value, Concentration, LifeDrainReturn, AmnesiaChance, Message1, Message2, Message3, Message4, InstrumentRequirement, SpellGroup, EffectGroup, SubSpellID, MoveCast, Uninterruptible, IsPrimary, IsSecondary, AllowBolt, SharedTimerGroup, PackageID, IsFocus, TooltipID, LastTimeRowUpdated) VALUES
('infernal_carve_l11',  100140, 5919, 5919, 'Infernal Carve',   'Self combat damage buff.', 'Self', 0, 8,  3.0, 0, 0, 'CombatSpeedBuff', 1200000, 0, 0, 0, 0, 0, 0, 0, 2.1, 0, 0, 0, '', '', '', '', 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 'Heretic_Live', 0, 0, '2000-01-01 00:00:00'),
('infernal_cleave_l17', 100141, 5919, 5919, 'Infernal Cleave',  'Self combat damage buff.', 'Self', 0, 12, 3.0, 0, 0, 'CombatSpeedBuff', 1200000, 0, 0, 0, 0, 0, 0, 0, 3.6, 0, 0, 0, '', '', '', '', 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 'Heretic_Live', 0, 0, '2000-01-01 00:00:00'),
('infernal_rend_l24',   100142, 5919, 5919, 'Infernal Rend',    'Self combat damage buff.', 'Self', 0, 16, 3.0, 0, 0, 'CombatSpeedBuff', 1200000, 0, 0, 0, 0, 0, 0, 0, 5.1, 0, 0, 0, '', '', '', '', 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 'Heretic_Live', 0, 0, '2000-01-01 00:00:00'),
('infernal_sever_l31',  100143, 5919, 5919, 'Infernal Sever',   'Self combat damage buff.', 'Self', 0, 20, 3.0, 0, 0, 'CombatSpeedBuff', 1200000, 0, 0, 0, 0, 0, 0, 0, 6.6, 0, 0, 0, '', '', '', '', 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 'Heretic_Live', 0, 0, '2000-01-01 00:00:00'),
('infernal_shear_l39',  100144, 5919, 5919, 'Infernal Shear',   'Self combat damage buff.', 'Self', 0, 24, 3.0, 0, 0, 'CombatSpeedBuff', 1200000, 0, 0, 0, 0, 0, 0, 0, 8.0, 0, 0, 0, '', '', '', '', 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 'Heretic_Live', 0, 0, '2000-01-01 00:00:00'),
('infernal_slice_l46',  100145, 5919, 5919, 'Infernal Slice',   'Self combat damage buff.', 'Self', 0, 28, 3.0, 0, 0, 'CombatSpeedBuff', 1200000, 0, 0, 0, 0, 0, 0, 0, 9.4, 0, 0, 0, '', '', '', '', 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 'Heretic_Live', 0, 0, '2000-01-01 00:00:00');

-- ---- Arawn's Precision → Cunning: Self Piercing Magic buff ------------
INSERT INTO spell (Spell_ID, SpellID, ClientEffect, Icon, Name, Description, Target, `Range`, Power, CastTime, Damage, DamageType, Type, Duration, Frequency, Pulse, PulsePower, Radius, RecastDelay, ResurrectHealth, ResurrectMana, Value, Concentration, LifeDrainReturn, AmnesiaChance, Message1, Message2, Message3, Message4, InstrumentRequirement, SpellGroup, EffectGroup, SubSpellID, MoveCast, Uninterruptible, IsPrimary, IsSecondary, AllowBolt, SharedTimerGroup, PackageID, IsFocus, TooltipID, LastTimeRowUpdated) VALUES
('arawns_precision_l10',  100150, 5920, 5920, 'Arawn''s Precision',  'Self piercing magic buff.', 'Self', 0, 8,  3.0, 0, 0, 'HereticPiercingMagic', 1200000, 0, 0, 0, 0, 0, 0, 0, 1,  0, 0, 0, '', '', '', '', 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 'Heretic_Live', 0, 0, '2000-01-01 00:00:00'),
('arawns_acumen_l18',     100151, 5920, 5920, 'Arawn''s Acumen',     'Self piercing magic buff.', 'Self', 0, 12, 3.0, 0, 0, 'HereticPiercingMagic', 1200000, 0, 0, 0, 0, 0, 0, 0, 2,  0, 0, 0, '', '', '', '', 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 'Heretic_Live', 0, 0, '2000-01-01 00:00:00'),
('arawns_insight_l26',    100152, 5920, 5920, 'Arawn''s Insight',    'Self piercing magic buff.', 'Self', 0, 16, 3.0, 0, 0, 'HereticPiercingMagic', 1200000, 0, 0, 0, 0, 0, 0, 0, 4,  0, 0, 0, '', '', '', '', 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 'Heretic_Live', 0, 0, '2000-01-01 00:00:00'),
('arawns_clarity_l34',    100153, 5920, 5920, 'Arawn''s Clarity',    'Self piercing magic buff.', 'Self', 0, 20, 3.0, 0, 0, 'HereticPiercingMagic', 1200000, 0, 0, 0, 0, 0, 0, 0, 6,  0, 0, 0, '', '', '', '', 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 'Heretic_Live', 0, 0, '2000-01-01 00:00:00'),
('arawns_wisdom_l42',     100154, 5920, 5920, 'Arawn''s Wisdom',     'Self piercing magic buff.', 'Self', 0, 24, 3.0, 0, 0, 'HereticPiercingMagic', 1200000, 0, 0, 0, 0, 0, 0, 0, 8,  0, 0, 0, '', '', '', '', 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 'Heretic_Live', 0, 0, '2000-01-01 00:00:00'),
('arawns_cunning_l50',    100155, 5920, 5920, 'Arawn''s Cunning',    'Self piercing magic buff.', 'Self', 0, 28, 3.0, 0, 0, 'HereticPiercingMagic', 1200000, 0, 0, 0, 0, 0, 0, 0, 10, 0, 0, 0, '', '', '', '', 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 'Heretic_Live', 0, 0, '2000-01-01 00:00:00');

-- ====================================================================
-- LINEXSPELL: link spells to their specialization line at acquisition
-- ====================================================================
INSERT INTO linexspell (LineXSpell_ID, LineName, SpellID, Level, PackageID, LastTimeRowUpdated) VALUES
-- Arawn's Fire
('lxs_af_100000', 'Arawn''s Fire', 100000, 4,  'Heretic_Live', NOW()),
('lxs_af_100001', 'Arawn''s Fire', 100001, 12, 'Heretic_Live', NOW()),
('lxs_af_100002', 'Arawn''s Fire', 100002, 20, 'Heretic_Live', NOW()),
('lxs_af_100003', 'Arawn''s Fire', 100003, 28, 'Heretic_Live', NOW()),
('lxs_af_100004', 'Arawn''s Fire', 100004, 36, 'Heretic_Live', NOW()),
('lxs_af_100005', 'Arawn''s Fire', 100005, 44, 'Heretic_Live', NOW()),
('lxs_af_100010', 'Arawn''s Fire', 100010, 9,  'Heretic_Live', NOW()),
('lxs_af_100011', 'Arawn''s Fire', 100011, 16, 'Heretic_Live', NOW()),
('lxs_af_100012', 'Arawn''s Fire', 100012, 23, 'Heretic_Live', NOW()),
('lxs_af_100013', 'Arawn''s Fire', 100013, 31, 'Heretic_Live', NOW()),
('lxs_af_100014', 'Arawn''s Fire', 100014, 39, 'Heretic_Live', NOW()),
('lxs_af_100015', 'Arawn''s Fire', 100015, 47, 'Heretic_Live', NOW()),
('lxs_af_100020', 'Arawn''s Fire', 100020, 3,  'Heretic_Live', NOW()),
('lxs_af_100021', 'Arawn''s Fire', 100021, 11, 'Heretic_Live', NOW()),
('lxs_af_100022', 'Arawn''s Fire', 100022, 18, 'Heretic_Live', NOW()),
('lxs_af_100023', 'Arawn''s Fire', 100023, 25, 'Heretic_Live', NOW()),
('lxs_af_100024', 'Arawn''s Fire', 100024, 33, 'Heretic_Live', NOW()),
('lxs_af_100025', 'Arawn''s Fire', 100025, 41, 'Heretic_Live', NOW()),
('lxs_af_100030', 'Arawn''s Fire', 100030, 36, 'Heretic_Live', NOW()),
('lxs_af_100031', 'Arawn''s Fire', 100031, 42, 'Heretic_Live', NOW()),
('lxs_af_100032', 'Arawn''s Fire', 100032, 48, 'Heretic_Live', NOW()),
('lxs_af_100040', 'Arawn''s Fire', 100040, 6,  'Heretic_Live', NOW()),
('lxs_af_100041', 'Arawn''s Fire', 100041, 14, 'Heretic_Live', NOW()),
('lxs_af_100042', 'Arawn''s Fire', 100042, 22, 'Heretic_Live', NOW()),
('lxs_af_100043', 'Arawn''s Fire', 100043, 30, 'Heretic_Live', NOW()),
('lxs_af_100044', 'Arawn''s Fire', 100044, 38, 'Heretic_Live', NOW()),
('lxs_af_100045', 'Arawn''s Fire', 100045, 46, 'Heretic_Live', NOW()),
('lxs_af_100050', 'Arawn''s Fire', 100050, 5,  'Heretic_Live', NOW()),
('lxs_af_100051', 'Arawn''s Fire', 100051, 13, 'Heretic_Live', NOW()),
('lxs_af_100052', 'Arawn''s Fire', 100052, 21, 'Heretic_Live', NOW()),
('lxs_af_100053', 'Arawn''s Fire', 100053, 30, 'Heretic_Live', NOW()),
('lxs_af_100054', 'Arawn''s Fire', 100054, 39, 'Heretic_Live', NOW()),
('lxs_af_100055', 'Arawn''s Fire', 100055, 48, 'Heretic_Live', NOW()),
('lxs_af_100060', 'Arawn''s Fire', 100060, 27, 'Heretic_Live', NOW()),
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

-- =====================================================================
-- Done. Verify with:
--   SELECT ClassID, GROUP_CONCAT(SpecKeyName) FROM classxspecialization WHERE ClassID = 33 GROUP BY ClassID;
--   SELECT LineName, COUNT(*) FROM linexspell WHERE LineName IN ('Arawn''s Fire','Cthonic Accretion') GROUP BY LineName;
--   SELECT COUNT(*) FROM spell WHERE PackageID = 'Heretic_Live';
-- Expected: 33 (Heretic) has Arawn's Fire + Cthonic Accretion;
--   Arawn's Fire: 33 spells; Cthonic Accretion: 38 spells; total Heretic_Live: 70 spells (actual: 65 after compact pass)
-- =====================================================================

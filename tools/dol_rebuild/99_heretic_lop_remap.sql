-- Heretic "Lost On Pulse" type remap.
-- DOLSharp ships the Arawn's Fire / Lava Torrent families with custom
-- spell types (HereticDoTLostOnPulse, HereticDamageSpeedDecreaseLOP) that
-- never had handler classes in either DOLSharp or OpenDAoC — the original
-- "ramp on each pulse" mechanic was lost long ago.
--
-- Until a proper ramping handler is written, point these spells at the
-- existing non-LOP handlers (HereticDamageOverTime, HereticDamageSpeedDecrease).
-- That gives them a standard pulsed DoT / snare behaviour: damage applies,
-- mana drains, target takes hits — just without the per-tick ramp.
--
-- Without this remap the spells cast in a loop and never apply damage
-- because the LOP handlers fall through to the inherited HereticPiercingMagic
-- focus pipeline which expects a focus target and never reaches damage code.
--
-- Idempotent: re-running just no-ops once the rows are already remapped.

UPDATE spell SET Type = 'HereticDamageOverTime'
 WHERE Type = 'HereticDoTLostOnPulse';

UPDATE spell SET Type = 'HereticDamageSpeedDecrease'
 WHERE Type = 'HereticDamageSpeedDecreaseLOP';

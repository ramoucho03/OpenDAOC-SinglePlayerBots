namespace DOL.GS.Spells
{
    /// <summary>
    /// Handler for the DOLSharp "Lost On Pulse" variant of the Heretic
    /// damage+speed-decrease chant family (Lava Torrent / Blazing Surge /
    /// Lava Spate / Lava Deluge / Blazing Wave, etc.).
    ///
    /// Inherits the non-LOP <see cref="HereticDamageSpeedDecreaseSpellHandler"/>
    /// for the underlying lifecycle. The "LostOnPulse" twist (consume mana on
    /// every pulse, ramp damage each tick) is data-driven on the Spell row,
    /// so no per-tick override is needed here — having a constructor that
    /// matches the SpellHandler factory signature is enough to let the
    /// existing pulse infra do its job.
    /// </summary>
    [SpellHandler(eSpellType.HereticDamageSpeedDecreaseLOP)]
    public class HereticDamageSpeedDecreaseLOP : HereticDamageSpeedDecrease
    {
        public HereticDamageSpeedDecreaseLOP(GameLiving caster, Spell spell, SpellLine line)
            : base(caster, spell, line) { }
    }
}

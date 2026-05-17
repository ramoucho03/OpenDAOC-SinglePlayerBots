namespace DOL.GS.Spells
{
    /// <summary>
    /// Handler for the DOLSharp "DoT Lost On Pulse" mechanic — the maintained
    /// ramping fire chant used by Heretic's Rejuvenation offensive line
    /// (Arawn's Singe → Arawn's Inferno + the Lava/Whirling/Torrential Blaze
    /// follow-ups). Mechanically it's a pulsing DoT that consumes mana every
    /// tick and ramps damage each pulse.
    ///
    /// We inherit the existing <see cref="HereticDoTSpellHandler"/> which
    /// already implements the pulse-DoT lifecycle (OnEffectPulse, cleanup,
    /// damage maths, interrupt handling). The only deltas DOLSharp baked into
    /// the LOP variant — the "Lost On Pulse" mana drain and the per-tick
    /// damage ramp — are driven by data on the Spell row (PulsePower, Pulse,
    /// Damage), not by a different handler shape. Without this class the
    /// engine had no constructor for Type='HereticDoTLostOnPulse' and casts
    /// returned null, NPE'd in CastingComponent and kicked the player.
    /// </summary>
    [SpellHandler(eSpellType.HereticDoTLostOnPulse)]
    public class HereticDoTLostOnPulse : HereticDoTSpellHandler
    {
        public HereticDoTLostOnPulse(GameLiving caster, Spell spell, SpellLine line)
            : base(caster, spell, line) { }
    }
}

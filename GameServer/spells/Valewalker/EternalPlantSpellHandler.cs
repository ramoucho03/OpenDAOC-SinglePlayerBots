using DOL.GS.Effects;
using DOL.GS.PacketHandler;

namespace DOL.GS.Spells
{
    /// <summary>
    /// Valewalker Arboreal Path "Eternal Plant" line.
    ///
    /// On live this spell behaves like a DoT that snares the target for the spell's
    /// duration while pulsing nature damage. Implemented here as a damage-speed-decrease
    /// variant so it benefits from the existing DoT pulse + snare framework while
    /// staying registered under the dedicated <see cref="eSpellType.EternalPlant"/>
    /// type the database uses.
    /// </summary>
    [SpellHandler(eSpellType.EternalPlant)]
    public class EternalPlantSpellHandler : DamageSpeedDecreaseSpellHandler
    {
        public override string ShortDescription =>
            $"Roots {TargetPronoun} in twisting vines, slowing it by {Spell.Value}% and inflicting {Spell.Damage} {Spell.DamageTypeToString()} damage every {Spell.Frequency / 1000.0:0.0} seconds.";

        public EternalPlantSpellHandler(GameLiving caster, Spell spell, SpellLine line) : base(caster, spell, line) { }

        public override void OnEffectStart(GameSpellEffect effect)
        {
            base.OnEffectStart(effect);

            if (effect.Owner is null)
                return;

            // "Twisting vines wrap around you!"
            if (!string.IsNullOrEmpty(Spell.Message1))
                MessageToLiving(effect.Owner, Spell.Message1, eChatType.CT_Spell);
            else
                MessageToLiving(effect.Owner, "Twisting vines wrap around you!", eChatType.CT_Spell);
        }

        public override void OnEffectPulse(GameSpellEffect effect)
        {
            base.OnEffectPulse(effect);

            if (effect.Owner == null || !effect.Owner.IsAlive)
                return;

            // Apply periodic damage on each pulse in addition to the snare.
            OnDirectEffect(effect.Owner);
        }

        protected override GameSpellEffect CreateSpellEffect(GameLiving target, double effectiveness)
        {
            // Pulse based on Frequency just like a DoT so vines tick over time.
            int frequency = Spell.Frequency > 0 ? Spell.Frequency : Spell.Duration;
            return new GameSpellEffect(this, CalculateEffectDuration(target), frequency, effectiveness);
        }
    }
}

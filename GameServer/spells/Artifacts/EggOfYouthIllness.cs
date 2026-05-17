using DOL.GS.Effects;
using DOL.GS.PacketHandler;

namespace DOL.GS.Spells
{
    /// <summary>
    /// Egg of Youth - illness debuff applied after a free rez/heal from the Egg.
    /// Reduces resurrected players' melee and spell damage to compensate for the free rez.
    /// Cannot be purged.
    /// </summary>
    [SpellHandler(eSpellType.YouthIllness)]
    public class YouthIllness : SpellHandler
    {
        public override bool HasPositiveEffect { get { return false; } }
        public override bool IsUnPurgeAble { get { return true; } }

        public override double CalculateSpellResistChance(GameLiving target)
        {
            return 0;
        }

        public override void OnEffectStart(GameSpellEffect effect)
        {
            base.OnEffectStart(effect);

            int value = (int) m_spell.Value;
            if (value <= 0)
                value = 50; // sensible default if seed doesn't specify

            effect.Owner.DebuffCategory[eProperty.MeleeDamage] += value;
            effect.Owner.DebuffCategory[eProperty.SpellDamage]  += value;
            effect.Owner.DebuffCategory[eProperty.MeleeSpeed]   += value / 2;
            effect.Owner.DebuffCategory[eProperty.CastingSpeed]  += value / 2;
            effect.Owner.DebuffCategory[eProperty.StyleDamage]  += value;
            effect.Owner.DebuffCategory[eProperty.RangedDamage] += value;

            if (effect.Owner is GamePlayer player)
            {
                player.Out.SendCharStatsUpdate();
                player.UpdatePlayerStatus();
                player.Out.SendMessage("You feel weakened by the Egg of Youth's life-giving energies.",
                    eChatType.CT_Spell, eChatLoc.CL_SystemWindow);
            }
        }

        public override int OnEffectExpires(GameSpellEffect effect, bool noMessages)
        {
            int value = (int) m_spell.Value;
            if (value <= 0)
                value = 50;

            effect.Owner.DebuffCategory[eProperty.MeleeDamage] -= value;
            effect.Owner.DebuffCategory[eProperty.SpellDamage]  -= value;
            effect.Owner.DebuffCategory[eProperty.MeleeSpeed]   -= value / 2;
            effect.Owner.DebuffCategory[eProperty.CastingSpeed]  -= value / 2;
            effect.Owner.DebuffCategory[eProperty.StyleDamage]  -= value;
            effect.Owner.DebuffCategory[eProperty.RangedDamage] -= value;

            if (effect.Owner is GamePlayer player)
            {
                player.Out.SendCharStatsUpdate();
                player.UpdatePlayerStatus();
                if (!noMessages)
                    player.Out.SendMessage("The Egg of Youth's weakness fades from you.",
                        eChatType.CT_SpellExpires, eChatLoc.CL_SystemWindow);
            }

            return base.OnEffectExpires(effect, noMessages);
        }

        public YouthIllness(GameLiving caster, Spell spell, SpellLine line) : base(caster, spell, line) { }
    }
}

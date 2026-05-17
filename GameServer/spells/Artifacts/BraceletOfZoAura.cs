namespace DOL.GS.Spells
{
    /// <summary>
    /// Bracelet of Zo'arkat group aura.
    /// Boosts melee/ranged damage and armor factor of the group target.
    /// Items effects line: not affected by spec.
    /// </summary>
    [SpellHandler(eSpellType.ZoAura)]
    public class ZoAuraSpellHandler : DualStatBuff
    {
        public override bool HasPositiveEffect { get { return true; } }

        /// <summary>
        /// Use base (item) bonus category so it stacks with class buffs.
        /// </summary>
        public override eBuffBonusCategory BonusCategory1 { get { return eBuffBonusCategory.BaseBuff; } }
        public override eBuffBonusCategory BonusCategory2 { get { return eBuffBonusCategory.BaseBuff; } }

        public override eProperty Property1 { get { return eProperty.MeleeDamage; } }
        public override eProperty Property2 { get { return eProperty.ArmorFactor; } }

        public ZoAuraSpellHandler(GameLiving caster, Spell spell, SpellLine line) : base(caster, spell, line) { }
    }
}

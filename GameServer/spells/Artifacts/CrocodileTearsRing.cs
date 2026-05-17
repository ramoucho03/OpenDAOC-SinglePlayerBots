using DOL.GS.PacketHandler;

namespace DOL.GS.Spells
{
    /// <summary>
    /// Crocodile Tear Ring - clickable instant resurrect.
    /// Behaves like a standard resurrect but with no power cost and uses the
    /// spell's ResurrectHealth / ResurrectMana percentages.
    /// </summary>
    [SpellHandler(eSpellType.CrocodileTearsProc)]
    public class CrocodileTearsProc : ResurrectSpellHandler
    {
        public override bool HasPositiveEffect { get { return true; } }

        /// <summary>
        /// Artifact charge - no power cost.
        /// </summary>
        public override int PowerCost(GameLiving target)
        {
            return 0;
        }

        public override double CalculateSpellResistChance(GameLiving target)
        {
            return 0;
        }

        public CrocodileTearsProc(GameLiving caster, Spell spell, SpellLine line) : base(caster, spell, line)
        {
        }
    }
}

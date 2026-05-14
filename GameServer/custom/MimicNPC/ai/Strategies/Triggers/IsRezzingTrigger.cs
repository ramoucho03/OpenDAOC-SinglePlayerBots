using DOL.GS.Spells;

namespace DOL.GS.Scripts.AI.Strategies.Triggers
{
    /// <summary>
    /// Fires the tick a healer starts casting a resurrection. Lets the group
    /// hear "je rez X, protégez-moi" exactly once per cast (the binding's
    /// cooldown prevents repeats while the cast is in flight).
    /// </summary>
    public sealed class IsRezzingTrigger : IBotTrigger
    {
        public string Name => "is-rezzing";

        public bool Check(BotContext ctx)
        {
            if (!ctx.Bot.IsCasting)
                return false;

            ISpellHandler h = ctx.Bot.castingComponent?.SpellHandler;
            if (h?.Spell == null)
                return false;

            return h.Spell.SpellType == eSpellType.Resurrect;
        }
    }
}

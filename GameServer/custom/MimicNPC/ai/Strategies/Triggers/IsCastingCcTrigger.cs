using DOL.GS.Spells;

namespace DOL.GS.Scripts.AI.Strategies.Triggers
{
    /// <summary>
    /// Fires when this bot is the main CC and just started casting a mez/root/
    /// stun. Used to announce "mezz posé, ne touchez pas !" so the rest of
    /// the group avoids breaking the lock with AoE/cleave.
    /// </summary>
    public sealed class IsCastingCcTrigger : IBotTrigger
    {
        public string Name => "is-casting-cc";

        public bool Check(BotContext ctx)
        {
            if (!ctx.Brain.IsMainCC)
                return false;

            if (!ctx.Bot.IsCasting)
                return false;

            ISpellHandler h = ctx.Bot.castingComponent?.SpellHandler;
            if (h?.Spell == null)
                return false;

            switch (h.Spell.SpellType)
            {
                case eSpellType.Mesmerize:
                case eSpellType.MesmerizeDurationBuff:
                case eSpellType.PetMesmerize:
                    return true;
                default:
                    return false;
            }
        }
    }
}

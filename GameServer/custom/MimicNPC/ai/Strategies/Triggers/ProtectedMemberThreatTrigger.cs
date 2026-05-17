namespace DOL.GS.Scripts.AI.Strategies.Triggers
{
    /// <summary>
    /// Fires when a hostile in the bot's aggro list is actively targeting a
    /// vulnerable group member and the main tank has not already controlled it.
    /// </summary>
    public sealed class ProtectedMemberThreatTrigger : IBotTrigger
    {
        public string Name => "protected-member-threat";

        public bool Check(BotContext ctx)
        {
            if (ctx?.Brain == null || ctx.Bot == null)
                return false;

            if (ctx.Brain.PreventCombat || ctx.Brain.IsHealer)
                return false;

            if (!ctx.Bot.IsAlive || ctx.Bot.IsStunned || ctx.Bot.IsMezzed)
                return false;

            return ctx.Brain.SelectProtectedMemberThreat() != null;
        }
    }
}

namespace DOL.GS.Scripts.AI.Strategies.Actions
{
    /// <summary>
    /// Retargets the bot onto an urgent hostile that is pressuring a healer,
    /// CC/support, or caster. The brain keeps the actual combat execution
    /// centralized in AttackMostWanted/CheckSpells.
    /// </summary>
    public sealed class PeelProtectedMemberAction : IBotAction
    {
        public string Name => "peel-protected-member";

        public bool IsPossible(BotContext ctx)
        {
            if (ctx?.Brain == null || ctx.Bot == null)
                return false;

            if (ctx.Brain.PreventCombat || ctx.Brain.IsHealer)
                return false;

            if (!ctx.Bot.IsAlive || ctx.Bot.IsStunned || ctx.Bot.IsMezzed)
                return false;

            return true;
        }

        public bool Execute(BotContext ctx)
        {
            return ctx.Brain.TryPeelProtectedMemberThreat();
        }
    }
}

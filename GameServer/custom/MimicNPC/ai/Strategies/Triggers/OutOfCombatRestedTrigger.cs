namespace DOL.GS.Scripts.AI.Strategies.Triggers
{
    /// <summary>
    /// Fires when the bot is out of combat, sitting (regen) and reasonably full
    /// on resources. Used to fire occasional banter so the group feels alive
    /// during downtime.
    /// </summary>
    public sealed class OutOfCombatRestedTrigger : IBotTrigger
    {
        public string Name => "ooc-rested";

        public bool Check(BotContext ctx)
        {
            if (ctx.Bot.InCombat)
                return false;

            // Only banter when reasonably topped up — feels weird if a bot
            // jokes at 30% HP.
            return ctx.Bot.HealthPercent >= 90
                && (ctx.Bot.MaxMana == 0 || ctx.Bot.ManaPercent >= 80)
                && ctx.Bot.EndurancePercent >= 80;
        }
    }
}

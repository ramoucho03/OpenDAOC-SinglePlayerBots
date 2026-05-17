namespace DOL.GS.Scripts.AI.Strategies.Actions
{
    /// <summary>
    /// Sits the bot down out of combat to regenerate health/mana/endurance
    /// faster. Refuses when in combat, casting, attacking or already sitting.
    /// </summary>
    public sealed class SitDownAction : IBotAction
    {
        public string Name => "sit";

        public bool IsPossible(BotContext ctx)
        {
            MimicNPC bot = ctx.Bot;

            if (bot.IsSitting)
                return false;

            if (bot.InCombat || bot.IsCasting || bot.IsAttacking)
                return false;

            return !bot.IsMoving;
        }

        public bool Execute(BotContext ctx) => ctx.Bot.Sit(true);
    }
}

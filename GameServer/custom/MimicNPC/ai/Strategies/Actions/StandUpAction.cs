namespace DOL.GS.Scripts.AI.Strategies.Actions
{
    /// <summary>Gets the bot back on its feet — used when combat starts or when fully rested.</summary>
    public sealed class StandUpAction : IBotAction
    {
        public string Name => "stand";

        public bool IsPossible(BotContext ctx) => ctx.Bot != null && ctx.Bot.IsSitting;

        public bool Execute(BotContext ctx) => ctx.Bot.Sit(false);
    }
}

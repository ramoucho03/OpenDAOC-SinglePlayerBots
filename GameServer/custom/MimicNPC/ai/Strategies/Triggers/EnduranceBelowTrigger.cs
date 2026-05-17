namespace DOL.GS.Scripts.AI.Strategies.Triggers
{
    public sealed class EnduranceBelowTrigger : IBotTrigger
    {
        private readonly int _threshold;

        public string Name { get; }

        public EnduranceBelowTrigger(int percent)
        {
            _threshold = percent;
            Name = "endurance<" + percent;
        }

        public bool Check(BotContext ctx)
        {
            if (ctx.Bot.MaxEndurance <= 0)
                return false;

            return ctx.Bot.EndurancePercent < _threshold;
        }
    }
}

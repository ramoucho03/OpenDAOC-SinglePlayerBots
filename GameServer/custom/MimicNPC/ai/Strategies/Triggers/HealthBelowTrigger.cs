namespace DOL.GS.Scripts.AI.Strategies.Triggers
{
    public sealed class HealthBelowTrigger : IBotTrigger
    {
        private readonly int _threshold;

        public string Name { get; }

        public HealthBelowTrigger(int percent)
        {
            _threshold = percent;
            Name = "health<" + percent;
        }

        public bool Check(BotContext ctx) => ctx.Bot.HealthPercent < _threshold;
    }
}

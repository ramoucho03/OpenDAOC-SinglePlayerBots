namespace DOL.GS.Scripts.AI.Strategies.Triggers
{
    public sealed class ManaBelowTrigger : IBotTrigger
    {
        private readonly int _threshold;

        public string Name { get; }

        public ManaBelowTrigger(int percent)
        {
            _threshold = percent;
            Name = "mana<" + percent;
        }

        public bool Check(BotContext ctx)
        {
            if (ctx.Bot.MaxMana <= 0)
                return false;

            return ctx.Bot.ManaPercent < _threshold;
        }
    }
}

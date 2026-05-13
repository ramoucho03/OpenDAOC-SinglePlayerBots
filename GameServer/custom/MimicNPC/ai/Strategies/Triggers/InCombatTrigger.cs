namespace DOL.GS.Scripts.AI.Strategies.Triggers
{
    public sealed class InCombatTrigger : IBotTrigger
    {
        private readonly bool _expected;

        public string Name => _expected ? "in-combat" : "out-of-combat";

        public InCombatTrigger(bool expected = true) { _expected = expected; }

        public bool Check(BotContext ctx) => ctx.Bot.InCombat == _expected;
    }
}

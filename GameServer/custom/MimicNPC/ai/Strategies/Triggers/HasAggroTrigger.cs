namespace DOL.GS.Scripts.AI.Strategies.Triggers
{
    /// <summary>
    /// Fires when the brain has any active aggro, even if the bot itself has
    /// not been tagged into combat yet. This lets casters/tanks react to group
    /// pressure during the run-in window of a pull.
    /// </summary>
    public sealed class HasAggroTrigger : IBotTrigger
    {
        private readonly bool _expected;

        public string Name => _expected ? "has-aggro" : "no-aggro";

        public HasAggroTrigger(bool expected = true)
        {
            _expected = expected;
        }

        public bool Check(BotContext ctx)
        {
            return ctx?.Brain != null && ctx.Brain.HasAggro == _expected;
        }
    }
}

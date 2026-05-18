namespace DOL.GS.Scripts.AI.Strategies.Triggers
{
    /// <summary>
    /// Fires when the tank is being mobbed: 3+ hostiles in the aggro list
    /// and in combat. Used to favour AoE/encompassing styles that hit the
    /// whole pack rather than chasing single-target taunts one by one.
    ///
    /// Membership count is a cheap O(N) AggroList scan; we don't bother
    /// with range checking here because the bound action re-validates each
    /// candidate against melee reach anyway.
    /// </summary>
    public sealed class ManyMobsAggroTrigger : IBotTrigger
    {
        private readonly int _threshold;

        public ManyMobsAggroTrigger(int threshold = 3)
        {
            _threshold = threshold < 2 ? 2 : threshold;
        }

        public string Name => $"many-mobs-aggro>={_threshold}";

        public bool Check(BotContext ctx)
        {
            if (ctx?.Brain == null || ctx.Bot == null)
                return false;

            if (!ctx.Bot.IsAlive
                || ctx.Bot.IsSitting
                || ctx.Bot.IsStunned
                || ctx.Bot.IsMezzed)
                return false;

            if (!ctx.Brain.IsActingAsTank || !ctx.Bot.InCombat)
                return false;

            return ctx.Brain.CountAggroListAlive() >= _threshold;
        }
    }
}

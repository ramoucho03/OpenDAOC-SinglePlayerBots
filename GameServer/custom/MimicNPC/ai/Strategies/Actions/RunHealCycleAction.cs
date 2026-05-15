namespace DOL.GS.Scripts.AI.Strategies.Actions
{
    /// <summary>
    /// Delegates to <see cref="DOL.AI.Brain.MimicBrain.CheckHeals"/>, which
    /// already orchestrates the full heal/cure decision tree (emergency
    /// instant heals, group HoT, single-target big heal, cure mezz, cure
    /// disease/poison) using the cached <see cref="MimicGroup"/> state.
    ///
    /// The strategy layer adds three things on top of FSM-driven calls:
    /// 1. Explicit activation per class (a paladin won't run the heal cycle,
    ///    a cleric will), wired via <c>BOT_AI_V2_CLASSES</c>.
    /// 2. A short cooldown between two evaluations so we don't recompute
    ///    the priority tree at full game-loop frequency.
    /// 3. A diagnostic surface — <c>/mstrategy</c> can disable the heal
    ///    cycle live to debug what the bot would do without it.
    ///
    /// When CheckHeals fires a real cast it returns true; we propagate that
    /// so the binding cooldown actually applies. A bot already in the
    /// middle of a cast is reported as "still working" so we don't try to
    /// re-enter the dispatcher mid-cast.
    /// </summary>
    public sealed class RunHealCycleAction : IBotAction
    {
        public string Name => "run-heal-cycle";

        public bool IsPossible(BotContext ctx)
        {
            // Heals require a live caster with mana. Out-of-mana situations
            // are still allowed through so CheckHeals can log/skip itself —
            // we don't want to bake the mana threshold into two places.
            if (ctx.Bot == null || !ctx.Bot.IsAlive)
                return false;

            if (ctx.Bot.IsStunned || ctx.Bot.IsMezzed)
                return false;

            return true;
        }

        public bool Execute(BotContext ctx)
        {
            // Already mid-cast on a heal: treat as a productive tick so the
            // binding cooldown applies and we don't pile up overlapping
            // attempts. CheckHeals itself short-circuits when IsCasting.
            if (ctx.Bot.IsCasting)
                return true;

            return ctx.Brain.CheckHeals();
        }
    }
}

namespace DOL.GS.Scripts.AI.Strategies.Triggers
{
    /// <summary>
    /// Fires when at least one hostile currently casting a spell sits within
    /// the bot's melee reach. The expensive scan lives in
    /// <see cref="DOL.AI.Brain.MimicBrain.TryInterruptCastingEnemyWithSlam"/>;
    /// this trigger is a cheap pre-filter (alive + tank role + in combat).
    /// The action itself re-runs the scan only when the cooldown allows, so
    /// we don't pay the AggroList walk twice.
    /// </summary>
    public sealed class EnemyCastingInMeleeTrigger : IBotTrigger
    {
        public string Name => "enemy-casting-in-melee";

        public bool Check(BotContext ctx)
        {
            if (ctx?.Brain == null || ctx.Bot == null)
                return false;

            if (!ctx.Bot.IsAlive
                || ctx.Bot.IsSitting
                || ctx.Bot.IsStunned
                || ctx.Bot.IsMezzed)
                return false;

            // Only proper tanks should waste a stun style on interrupting —
            // a DPS bot would lose more damage than they gain.
            return ctx.Brain.IsActingAsTank && ctx.Bot.InCombat;
        }
    }
}

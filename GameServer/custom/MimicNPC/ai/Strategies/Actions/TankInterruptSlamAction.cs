namespace DOL.GS.Scripts.AI.Strategies.Actions
{
    /// <summary>
    /// Asks the tank brain to find a casting enemy in melee range and queue
    /// a stun style (Slam first) onto the next swing. All the lookup work
    /// happens in <see cref="DOL.AI.Brain.MimicBrain.TryInterruptCastingEnemyWithSlam"/>;
    /// this is a thin shim so it can be bound at strategy level with a
    /// dedicated cooldown.
    /// </summary>
    public sealed class TankInterruptSlamAction : IBotAction
    {
        public string Name => "tank-interrupt-slam";

        public bool IsPossible(BotContext ctx)
        {
            return ctx?.Brain != null
                && ctx.Bot != null
                && ctx.Bot.IsAlive
                && ctx.Brain.IsActingAsTank;
        }

        public bool Execute(BotContext ctx) => ctx.Brain.TryInterruptCastingEnemyWithSlam();
    }
}

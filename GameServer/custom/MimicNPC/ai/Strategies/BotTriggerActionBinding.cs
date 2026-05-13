namespace DOL.GS.Scripts.AI.Strategies
{
    /// <summary>
    /// Wires a trigger to an action with an effective priority and an
    /// optional minimum cooldown between two successful executions.
    /// Higher Priority means evaluated earlier each tick.
    /// </summary>
    public sealed class BotTriggerActionBinding
    {
        public IBotTrigger Trigger { get; }
        public IBotAction Action { get; }
        public int Priority { get; }
        public long CooldownMs { get; }

        /// <summary>
        /// When true, a successful Execute() ends this tick — no further
        /// bindings will be evaluated. Use it for actions that physically
        /// take over the bot (casts, switches, etc.).
        /// </summary>
        public bool Exclusive { get; }

        /// <summary>The strategy that owns this binding (for diagnostics).</summary>
        public string OwnerStrategy { get; internal set; }

        public long NextAllowedTick { get; internal set; }

        public BotTriggerActionBinding(IBotTrigger trigger, IBotAction action,
            int priority = 50, long cooldownMs = 0, bool exclusive = false)
        {
            Trigger = trigger;
            Action = action;
            Priority = priority;
            CooldownMs = cooldownMs;
            Exclusive = exclusive;
        }
    }
}

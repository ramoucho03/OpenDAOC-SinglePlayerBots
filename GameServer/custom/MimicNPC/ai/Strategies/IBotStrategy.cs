using System.Collections.Generic;

namespace DOL.GS.Scripts.AI.Strategies
{
    /// <summary>
    /// A bot strategy is a named module that contributes one or more
    /// trigger/action bindings. Strategies can be enabled or disabled at
    /// runtime by the player via /mstrategy.
    /// </summary>
    public interface IBotStrategy
    {
        string Name { get; }
        string Description { get; }

        /// <summary>
        /// Returns the bindings this strategy contributes. Called once when
        /// the strategy is enabled. Implementations should not retain
        /// per-tick state in the bindings (use action/trigger instances).
        /// </summary>
        IEnumerable<BotTriggerActionBinding> GetBindings(BotContext ctx);

        /// <summary>Optional hook fired when added to the manager.</summary>
        void OnEnable(BotContext ctx) { }
        /// <summary>Optional hook fired when removed from the manager.</summary>
        void OnDisable(BotContext ctx) { }
    }
}

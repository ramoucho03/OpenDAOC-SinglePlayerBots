using System.Collections.Generic;
using System.Linq;

namespace DOL.GS.Scripts.AI.Strategies.Builtin
{
    /// <summary>Explicit no-op strategy. Acts as a sane default slot when the player wants the FSM in full control.</summary>
    public sealed class PassiveStrategy : IBotStrategy
    {
        public const string Key = "passive";

        public string Name => Key;
        public string Description => "Aucune action. Laisse la FSM existante piloter le bot.";

        public IEnumerable<BotTriggerActionBinding> GetBindings(BotContext ctx) => Enumerable.Empty<BotTriggerActionBinding>();
    }
}

using DOL.GS.Scripts.AI.Strategies.Actions;
using DOL.GS.Scripts.AI.Strategies.Triggers;
using System.Collections.Generic;

namespace DOL.GS.Scripts.AI.Strategies.Builtin
{
    /// <summary>
    /// Bot AI v2 - proactive peel layer. When a hostile in the bot's aggro list
    /// is targeting a healer, CC/support, or caster and the main tank has not
    /// already controlled it, the bot briefly prioritizes that hostile over the
    /// normal assist target.
    ///
    /// Priority 84 keeps dedicated CC at 85 in charge of queued mez/root work,
    /// but beats the generic assist bindings so DPS do not tunnel while a
    /// protected member is being pressured.
    /// </summary>
    public sealed class PeelStrategy : IBotStrategy
    {
        public const string Key = "peel";

        public string Name => Key;
        public string Description => "Protege proactivement les healers/casters/supports attaques avant de reprendre l'assist.";

        public IEnumerable<BotTriggerActionBinding> GetBindings(BotContext ctx)
        {
            yield return new BotTriggerActionBinding(
                new ProtectedMemberThreatTrigger(),
                new PeelProtectedMemberAction(),
                priority: 84,
                cooldownMs: 750,
                exclusive: true);
        }
    }
}

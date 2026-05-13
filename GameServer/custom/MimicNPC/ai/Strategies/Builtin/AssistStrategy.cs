using DOL.GS.Scripts.AI.Strategies.Actions;
using DOL.GS.Scripts.AI.Strategies.Triggers;
using System.Collections.Generic;

namespace DOL.GS.Scripts.AI.Strategies.Builtin
{
    /// <summary>
    /// Calque la cible du bot sur celle de l'assist principal dès qu'il
    /// n'a plus de cible vivante. Cette stratégie évite qu'un DPS reste
    /// à l'arrêt après avoir tué sa cible alors que le groupe enchaîne
    /// déjà sur un autre mob.
    /// </summary>
    public sealed class AssistStrategy : IBotStrategy
    {
        public const string Key = "assist";

        public string Name => Key;
        public string Description => "Calque la cible sur celle de l'assist principal quand le bot n'a plus de cible vivante.";

        public IEnumerable<BotTriggerActionBinding> GetBindings(BotContext ctx)
        {
            yield return new BotTriggerActionBinding(
                new CompositeAndTrigger(new NoLiveTargetTrigger(), new MainAssistHasTargetTrigger()),
                new FocusMainAssistTargetAction(),
                priority: 80,
                cooldownMs: 500,
                exclusive: false);
        }
    }
}

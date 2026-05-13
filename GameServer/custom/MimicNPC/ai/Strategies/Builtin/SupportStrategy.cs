using DOL.GS.Scripts.AI.Strategies.Actions;
using DOL.GS.Scripts.AI.Strategies.Triggers;
using System.Collections.Generic;

namespace DOL.GS.Scripts.AI.Strategies.Builtin
{
    /// <summary>
    /// Stratégie "voix du groupe". Annonce les CC en attente, les
    /// dissipations à effectuer et les membres en HP critique. Conçue
    /// pour être activée sur un seul bot — typiquement le leader ou le
    /// main assist — pour éviter le spam de chat.
    /// </summary>
    public sealed class SupportStrategy : IBotStrategy
    {
        public const string Key = "support";

        public string Name => Key;
        public string Description => "Coordonne le groupe : annonce CC, mezz à dissiper et membres mourants.";

        public IEnumerable<BotTriggerActionBinding> GetBindings(BotContext ctx)
        {
            yield return new BotTriggerActionBinding(
                new GroupMemberCriticalTrigger(25),
                new AnnounceCriticalMemberAction(25),
                priority: 95,
                cooldownMs: 10_000,
                exclusive: false);

            yield return new BotTriggerActionBinding(
                new GroupMemberMezzedTrigger(),
                new AnnounceMezzedMemberAction(),
                priority: 80,
                cooldownMs: 15_000,
                exclusive: false);

            yield return new BotTriggerActionBinding(
                new GroupHasCcTargetsTrigger(),
                new AnnounceCcTargetAction(),
                priority: 60,
                cooldownMs: 12_000,
                exclusive: false);
        }
    }
}

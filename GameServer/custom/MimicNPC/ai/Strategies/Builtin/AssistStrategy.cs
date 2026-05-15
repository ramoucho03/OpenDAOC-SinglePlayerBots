using DOL.GS.Scripts.AI.Strategies.Actions;
using DOL.GS.Scripts.AI.Strategies.Triggers;
using System.Collections.Generic;

namespace DOL.GS.Scripts.AI.Strategies.Builtin
{
    /// <summary>
    /// Two complementary bindings keep the bot's focus on the main assist:
    ///
    /// 1. Re-acquire the assist target as soon as the bot has no live
    ///    target (default since Phase A — solves "DPS standing idle after
    ///    finishing its mob").
    /// 2. Phase E: actively switch off the current target the moment the
    ///    main assist switches to a new mob (e.g. broken mez add). Without
    ///    this, DPS bots keep pounding their old target while the rest of
    ///    the group has already moved on.
    ///
    /// The switch binding has a slightly lower priority and a longer
    /// cooldown than the no-target one, so a free DPS still re-acquires
    /// instantly while a busy DPS only switches every 1.5 s — fast enough
    /// to look coordinated, slow enough to avoid mid-style flips.
    /// </summary>
    public sealed class AssistStrategy : IBotStrategy
    {
        public const string Key = "assist";

        public string Name => Key;
        public string Description => "Calque la cible sur celle de l'assist principal et bascule quand l'assist change de cible.";

        public IEnumerable<BotTriggerActionBinding> GetBindings(BotContext ctx)
        {
            yield return new BotTriggerActionBinding(
                new CompositeAndTrigger(new NoLiveTargetTrigger(), new MainAssistHasTargetTrigger()),
                new FocusMainAssistTargetAction(),
                priority: 80,
                cooldownMs: 500,
                exclusive: false);

            yield return new BotTriggerActionBinding(
                new BotTargetDiffersFromAssistTrigger(),
                new FocusMainAssistTargetAction(),
                priority: 70,
                cooldownMs: 1500,
                exclusive: false);
        }
    }
}

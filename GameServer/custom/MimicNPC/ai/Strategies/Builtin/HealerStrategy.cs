using DOL.GS.Scripts.AI.Strategies.Actions;
using DOL.GS.Scripts.AI.Strategies.Triggers;
using System.Collections.Generic;

namespace DOL.GS.Scripts.AI.Strategies.Builtin
{
    /// <summary>
    /// Bot AI v2 — role strategy for dedicated healers (Cleric, Friar, Druid,
    /// Healer, Shaman). Acts as the explicit, configurable entry point into
    /// <see cref="DOL.AI.Brain.MimicBrain.CheckHeals"/>:
    ///
    /// - The trigger checks the cached group health snapshot every tick and
    ///   fires as soon as someone is critical, low or mezzed.
    /// - The action delegates the actual decision (which spell on whom) to
    ///   the existing CheckHeals dispatcher — no logic duplication.
    /// - High priority (90) and Exclusive=true so that, once a heal fires,
    ///   no lower-priority binding (chatter, follow assist) runs the same
    ///   tick. Healers heal first, talk second.
    /// - 250 ms cooldown to keep the dispatcher light without making the bot
    ///   visibly slower than the legacy FSM path.
    ///
    /// This strategy is enabled automatically at bot creation when the
    /// MimicNPC's class appears in <c>MimicConfig.BOT_AI_V2_CLASSES</c>,
    /// and can be toggled live with <c>/mstrategy enable/disable healer</c>.
    /// </summary>
    public sealed class HealerStrategy : IBotStrategy
    {
        public const string Key = "healer";

        public string Name => Key;
        public string Description => "Bot AI v2 — healer role: drives CheckHeals on any group heal need.";

        public IEnumerable<BotTriggerActionBinding> GetBindings(BotContext ctx)
        {
            int low = MimicConfig.MIMIC_HEAL_THRESHOLD > 0 ? MimicConfig.MIMIC_HEAL_THRESHOLD : 85;
            int crit = MimicConfig.MIMIC_EMERGENCY_THRESHOLD > 0 ? MimicConfig.MIMIC_EMERGENCY_THRESHOLD : 50;

            yield return new BotTriggerActionBinding(
                new AnyHealNeedTrigger(low, crit),
                new RunHealCycleAction(),
                priority: 90,
                cooldownMs: 250,
                exclusive: true);
        }
    }
}

using DOL.GS.Scripts.AI.Strategies.Actions;
using DOL.GS.Scripts.AI.Strategies.Triggers;
using System.Collections.Generic;

namespace DOL.GS.Scripts.AI.Strategies.Builtin
{
    /// <summary>
    /// Bot AI v2 — role strategy for dedicated healers (Cleric, Friar, Druid,
    /// Bard, Healer, Shaman). Acts as the explicit, configurable entry
    /// point into <see cref="DOL.AI.Brain.MimicBrain.CheckHeals"/>.
    ///
    /// Phase C: the single composite binding from Phase A is split into
    /// five priority-ordered bindings, one per heal reason. They all share
    /// the same action — <see cref="RunHealCycleAction"/> still delegates
    /// to CheckHeals which keeps its own priority logic. The split adds:
    ///
    /// 1. <b>Diagnostic visibility</b>: /mstrategy list shows which
    ///    reason fired this tick, not just "healer ran".
    /// 2. <b>Per-reason cooldowns</b>: a critical heal is rate-limited
    ///    differently from a normal heal or a cure.
    /// 3. <b>Granular toggle</b>: future work can attach distinct actions
    ///    to each binding (e.g. an emergency-only action that skips
    ///    CheckHeals' slower paths) without changing the strategy shape.
    ///
    /// Priorities (higher fires first when multiple triggers match):
    ///   critical(100) > mezz(95) > poison(92) > disease(90) > low(85)
    ///
    /// All bindings are exclusive so the first heal-side action wins the
    /// tick. CheckHeals itself dedupes via MimicGroup.AlreadyCasting*
    /// flags, so multiple bindings firing in successive ticks remain
    /// safe.
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

            // Critical health: highest priority, very short cooldown so we
            // keep re-evaluating until the target is out of the red.
            yield return new BotTriggerActionBinding(
                new GroupMemberCriticalTrigger(crit),
                new RunHealCycleAction(),
                priority: 100,
                cooldownMs: 200,
                exclusive: true);

            // Mezz: a mezzed tank is a dead tank, fire fast.
            yield return new BotTriggerActionBinding(
                new GroupMemberMezzedTrigger(),
                new RunHealCycleAction(),
                priority: 95,
                cooldownMs: 500,
                exclusive: true);

            // Poison / Disease: CheckHeals throttles cures internally to
            // 5 s — match that with a 1.5 s strategy cooldown so we don't
            // hammer the dispatcher.
            yield return new BotTriggerActionBinding(
                new GroupMemberPoisonedTrigger(),
                new RunHealCycleAction(),
                priority: 92,
                cooldownMs: 1500,
                exclusive: true);

            yield return new BotTriggerActionBinding(
                new GroupMemberDiseasedTrigger(),
                new RunHealCycleAction(),
                priority: 90,
                cooldownMs: 1500,
                exclusive: true);

            // Below-threshold heals: the bulk of heal traffic. Cadence
            // 200ms (was 400) so the healer stays on top of incoming damage
            // — groups were dying on plain mobs because the cycle waited
            // 400ms between heal attempts, letting the tank tick down two
            // big swings between any two heal cycles.
            yield return new BotTriggerActionBinding(
                new GroupMemberHealthLowTrigger(low),
                new RunHealCycleAction(),
                priority: 85,
                cooldownMs: 200,
                exclusive: true);

            // Combat maintenance: this is what makes proactive tank HoT/regen
            // actually happen while the tank is still healthy. CheckHeals is
            // cheap when no spell is needed and still owns all dedupe logic.
            // Cadence 300ms (was 500) so HoT refresh / mana monitoring stays
            // tighter when nobody is in immediate trouble yet.
            yield return new BotTriggerActionBinding(
                new HasAggroTrigger(true),
                new RunHealCycleAction(),
                priority: 82,
                cooldownMs: 300,
                exclusive: true);
        }
    }
}

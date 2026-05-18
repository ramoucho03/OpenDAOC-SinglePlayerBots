using DOL.AI.Brain;
using DOL.GS.Scripts.AI.Strategies.Actions;
using DOL.GS.Scripts.AI.Strategies.Triggers;
using System.Collections.Generic;

namespace DOL.GS.Scripts.AI.Strategies.Builtin
{
    /// <summary>
    /// Bot AI v2 — role strategy for tanks (Armsman, Paladin, Reaver, Hero,
    /// Warden, Champion, Warrior, Thane).
    ///
    /// Drives the defensive spell/style dispatcher of <see cref="MimicBrain"/>
    /// while the bot is in combat. The dispatcher already chooses taunts,
    /// group-shielding ticks, and HoT regen styles appropriately — this
    /// strategy makes the call explicit (so it can be toggled per bot via
    /// /mstrategy) and rate-limited (500 ms between attempts so we don't
    /// burn through endurance trying to chain taunt styles every tick).
    ///
    /// Lower priority than HealerStrategy (70 vs 90): no stock DAoC tank
    /// also heals, but operators are free to opt a class into both lists
    /// — when that happens, heals always preempt the defensive cycle in
    /// the same tick.
    ///
    /// Phase E adds a lost-aggro callout: when the tank's current target
    /// is hitting another group member, the tank publicly says so. The
    /// callout is informational — the taunt rotation already lives in
    /// CheckSpells(Defensive) and recovers aggro on its own; this just
    /// makes the situation visible to the rest of the group.
    ///
    /// Phase F additions:
    ///  - Engage activation: when a melee mob is swinging at the tank we
    ///    enable Engage to lift block chance on that target. Gated by a
    ///    long cooldown so we don't churn the effect every swing window.
    ///  - Defensive cooldown: pops Ignore Pain at low HP (50% / 35% steps).
    ///    The RA's own 15/30-min reuse keeps it economical — the binding
    ///    cooldown is only there to avoid the trigger re-firing during
    ///    the same critical-HP window.
    /// </summary>
    public sealed class TankStrategy : IBotStrategy
    {
        public const string Key = "tank";

        public string Name => Key;
        public string Description => "Bot AI v2 — tank role: drives the defensive cycle (taunts, peels) while engaged.";

        public IEnumerable<BotTriggerActionBinding> GetBindings(BotContext ctx)
        {
            // Emergency cooldown: critical HP, fire Ignore Pain *now*.
            // Priority is the highest in this strategy (85) so it preempts
            // the taunt rotation in the same tick — taunting a corpse is
            // useless and the RA self-heal is a literal save.
            yield return new BotTriggerActionBinding(
                new HealthBelowTrigger(35),
                new FireDefensiveCooldownAction(35, "tank-ignore-pain-emergency"),
                priority: 85,
                cooldownMs: 5_000,
                exclusive: false);

            // Highest-priority recovery cycle: fires the defensive cycle
            // (which picks the best Taunt spell + Taunt style + shield slam)
            // immediately when the tank notices they've lost aggro. Cooldown
            // 800ms — short enough to recover aggro within ~2s, long enough
            // that we don't gut endurance regen.
            yield return new BotTriggerActionBinding(
                new TankLostAggroTrigger(),
                new DelegateCheckSpellsAction(MimicBrain.eCheckSpellType.Defensive, "tank-lost-aggro-recovery"),
                priority: 78,
                cooldownMs: 800,
                exclusive: true);

            // Pressure cycle: tank already has aggro on at least one mob.
            // Cadence 250ms (was 350) so the tank keeps threat ramping faster
            // than a focused-DPS train can take it back. The defensive picker
            // still gates on actual Taunt availability; we just stop sleeping
            // between attempts.
            yield return new BotTriggerActionBinding(
                new HasAggroTrigger(true),
                new DelegateCheckSpellsAction(MimicBrain.eCheckSpellType.Defensive, "tank-pressure-cycle"),
                priority: 72,
                cooldownMs: 250,
                exclusive: true);

            yield return new BotTriggerActionBinding(
                new InCombatTrigger(true),
                new DelegateCheckSpellsAction(MimicBrain.eCheckSpellType.Defensive, "tank-defensive-cycle"),
                priority: 70,
                cooldownMs: 500,
                exclusive: true);

            // Engage activation: only when the current target is actively
            // melee-swinging at us. Non-exclusive so it can co-tick with
            // the pressure cycle in the same Think — Engage just sets a
            // defensive effect and doesn't conflict with the taunt path.
            // 4s binding cooldown handles re-arming after a target swap
            // without re-firing every tick (the effect itself is sticky).
            yield return new BotTriggerActionBinding(
                new InCombatTrigger(true),
                new ActivateTankEngageAction(),
                priority: 68,
                cooldownMs: 4_000,
                exclusive: false);

            // Lost-aggro callout: long-ish cooldown so the tank doesn't
            // cry wolf every tick a mob fluctuates between two members.
            // The actual recovery is handled by the priority-78 binding above.
            yield return new BotTriggerActionBinding(
                new TankLostAggroTrigger(),
                new LocalizedGroupSayAction("say-lost-aggro",
                    "Mimic.Chat.LostAggro.1",
                    "Mimic.Chat.LostAggro.2",
                    "Mimic.Chat.LostAggro.3"),
                priority: 65,
                cooldownMs: 12_000,
                exclusive: false);

            // Mid-HP cooldown sweep: catches the case where the tank slid
            // under 50% without ever hitting the 35% emergency trigger
            // (slow damage stream). Lower priority so the emergency
            // binding wins when both are eligible — the RA fires once
            // either way thanks to its own 15-min reuse.
            yield return new BotTriggerActionBinding(
                new HealthBelowTrigger(50),
                new FireDefensiveCooldownAction(50, "tank-ignore-pain-mid"),
                priority: 55,
                cooldownMs: 20_000,
                exclusive: false);
        }
    }

    /// <summary>
    /// Action wrapper that fires <see cref="MimicBrain.TryActivateTankEngage"/>.
    /// All the engage gating (alive, weapon, target is melee-swinging at us,
    /// 5s post-attack lockout) lives in the brain method so this stays a
    /// thin shim.
    /// </summary>
    internal sealed class ActivateTankEngageAction : IBotAction
    {
        public string Name => "tank-engage";

        public bool IsPossible(BotContext ctx)
        {
            return ctx?.Brain != null
                && ctx.Bot != null
                && ctx.Bot.IsAlive
                && ctx.Brain.IsActingAsTank;
        }

        public bool Execute(BotContext ctx) => ctx.Brain.TryActivateTankEngage();
    }

    /// <summary>
    /// Action wrapper that fires <see cref="MimicBrain.TryFireDefensiveCooldown"/>
    /// at the configured HP threshold. The brain method validates the actual
    /// RA availability and the engine-side reuse timer — this just plumbs the
    /// threshold so the binding can be tiered (emergency vs mid).
    /// </summary>
    internal sealed class FireDefensiveCooldownAction : IBotAction
    {
        private readonly int _threshold;
        public string Name { get; }

        public FireDefensiveCooldownAction(int threshold, string name)
        {
            _threshold = threshold;
            Name = name;
        }

        public bool IsPossible(BotContext ctx)
        {
            return ctx?.Brain != null
                && ctx.Bot != null
                && ctx.Bot.IsAlive
                && ctx.Brain.IsActingAsTank;
        }

        public bool Execute(BotContext ctx) => ctx.Brain.TryFireDefensiveCooldown(_threshold);
    }
}

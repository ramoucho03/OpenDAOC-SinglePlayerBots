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
    /// </summary>
    public sealed class TankStrategy : IBotStrategy
    {
        public const string Key = "tank";

        public string Name => Key;
        public string Description => "Bot AI v2 — tank role: drives the defensive cycle (taunts, peels) while engaged.";

        public IEnumerable<BotTriggerActionBinding> GetBindings(BotContext ctx)
        {
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
        }
    }
}

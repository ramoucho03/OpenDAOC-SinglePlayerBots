using DOL.AI.Brain;
using DOL.GS.Scripts.AI.Strategies.Actions;
using DOL.GS.Scripts.AI.Strategies.Triggers;
using System.Collections.Generic;

namespace DOL.GS.Scripts.AI.Strategies.Builtin
{
    /// <summary>
    /// Bot AI v2 — role strategy for tanks (Armsman, Paladin, Mercenary,
    /// Reaver, Hero, Warden, Champion, Warrior, Thane, Skald).
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
    /// </summary>
    public sealed class TankStrategy : IBotStrategy
    {
        public const string Key = "tank";

        public string Name => Key;
        public string Description => "Bot AI v2 — tank role: drives the defensive cycle (taunts, peels) while engaged.";

        public IEnumerable<BotTriggerActionBinding> GetBindings(BotContext ctx)
        {
            yield return new BotTriggerActionBinding(
                new InCombatTrigger(true),
                new DelegateCheckSpellsAction(MimicBrain.eCheckSpellType.Defensive, "tank-defensive-cycle"),
                priority: 70,
                cooldownMs: 500,
                exclusive: true);
        }
    }
}

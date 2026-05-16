using DOL.AI.Brain;
using DOL.GS.Scripts.AI.Strategies.Actions;
using DOL.GS.Scripts.AI.Strategies.Triggers;
using System.Collections.Generic;

namespace DOL.GS.Scripts.AI.Strategies.Builtin
{
    /// <summary>
    /// Bot AI v2 — role strategy for archer classes (Scout, Ranger, Hunter).
    ///
    /// Routes the offensive cycle (procs, archery-related spells) at a
    /// modest cadence. Weapon switching to/from the bow stays inside the
    /// FSM/AttackComponent — the strategy layer doesn't try to second-guess
    /// the ranged-engagement state machine, it only owns the spell side.
    ///
    /// Same priority/cooldown as MeleeDpsStrategy (500 ms, matching the
    /// combat-mode brain tick): archers and melee DPS are functionally
    /// equivalent from CheckSpells' point of view.
    /// </summary>
    public sealed class RangedDpsStrategy : IBotStrategy
    {
        public const string Key = "ranged_dps";

        public string Name => Key;
        public string Description => "Bot AI v2 — ranged DPS role: drives archer offensive procs while engaged.";

        public IEnumerable<BotTriggerActionBinding> GetBindings(BotContext ctx)
        {
            yield return new BotTriggerActionBinding(
                new HasAggroTrigger(true),
                new DelegateCheckSpellsAction(MimicBrain.eCheckSpellType.Offensive, "ranged-pressure-cycle"),
                priority: 62,
                cooldownMs: 400,
                exclusive: false);

            yield return new BotTriggerActionBinding(
                new InCombatTrigger(true),
                new DelegateCheckSpellsAction(MimicBrain.eCheckSpellType.Offensive, "ranged-offensive-cycle"),
                priority: 60,
                cooldownMs: 500,
                exclusive: false);
        }
    }
}

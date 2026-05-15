using DOL.AI.Brain;
using DOL.GS.Scripts.AI.Strategies.Actions;
using DOL.GS.Scripts.AI.Strategies.Triggers;
using System.Collections.Generic;

namespace DOL.GS.Scripts.AI.Strategies.Builtin
{
    /// <summary>
    /// Bot AI v2 — role strategy for melee DPS classes (Infiltrator,
    /// Mercenary, Reaver, Heretic, Blademaster, Nightshade, Vampiir,
    /// Valewalker, Berserker, Savage, Shadowblade).
    ///
    /// Pure melee classes don't have an offensive spell rotation, but
    /// they do get hybrid procs, lifetap returns, and a few self-buff
    /// triggers routed through CheckSpells(Offensive). The dispatcher
    /// short-circuits when nothing applies, so a one-second cooldown
    /// keeps this cheap.
    ///
    /// Focus-target propagation is already owned by AssistStrategy
    /// (priority 50 there); this strategy only handles the spell side.
    /// </summary>
    public sealed class MeleeDpsStrategy : IBotStrategy
    {
        public const string Key = "melee_dps";

        public string Name => Key;
        public string Description => "Bot AI v2 — melee DPS role: routes melee-class spell procs through CheckSpells.";

        public IEnumerable<BotTriggerActionBinding> GetBindings(BotContext ctx)
        {
            yield return new BotTriggerActionBinding(
                new InCombatTrigger(true),
                new DelegateCheckSpellsAction(MimicBrain.eCheckSpellType.Offensive, "melee-offensive-cycle"),
                priority: 60,
                cooldownMs: 1000,
                exclusive: false);
        }
    }
}

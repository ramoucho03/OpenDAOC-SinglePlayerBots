using DOL.AI.Brain;
using DOL.GS.Scripts.AI.Strategies.Actions;
using DOL.GS.Scripts.AI.Strategies.Triggers;
using System.Collections.Generic;

namespace DOL.GS.Scripts.AI.Strategies.Builtin
{
    /// <summary>
    /// Bot AI v2 — role strategy for offensive casters (Wizard, Theurgist,
    /// Cabalist, Sorcerer, Necromancer, Eldritch, Enchanter, Mentalist,
    /// Animist, Bainshee, Runemaster, Spiritmaster, Bonedancer, Warlock,
    /// and any healer/hybrid that the operator opts into the
    /// <c>bot_ai_v2_caster_dps_classes</c> CSV).
    ///
    /// Drives the offensive spell dispatcher every 600 ms while the bot
    /// is engaged. Exclusive so that a successful nuke ends the tick —
    /// we don't want to immediately stack a melee-style action behind it.
    ///
    /// Priority 75 sits between TankStrategy (70) and HealerStrategy (90):
    /// a Druid hybrid heals first, nukes second, never melees a tick that
    /// could have nuked.
    /// </summary>
    public sealed class CasterDpsStrategy : IBotStrategy
    {
        public const string Key = "caster_dps";

        public string Name => Key;
        public string Description => "Bot AI v2 — caster DPS role: drives the offensive spell rotation while engaged.";

        public IEnumerable<BotTriggerActionBinding> GetBindings(BotContext ctx)
        {
            yield return new BotTriggerActionBinding(
                new InCombatTrigger(true),
                new DelegateCheckSpellsAction(MimicBrain.eCheckSpellType.Offensive, "caster-offensive-cycle"),
                priority: 75,
                cooldownMs: 600,
                exclusive: true);
        }
    }
}

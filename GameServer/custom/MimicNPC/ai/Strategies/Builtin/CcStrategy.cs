using DOL.AI.Brain;
using DOL.GS.Scripts.AI.Strategies.Actions;
using DOL.GS.Scripts.AI.Strategies.Triggers;
using System.Collections.Generic;

namespace DOL.GS.Scripts.AI.Strategies.Builtin
{
    /// <summary>
    /// Bot AI v2 — role strategy for crowd-control specialists (Sorcerer,
    /// Minstrel, Enchanter, Bard, Mentalist, Eldritch, Runemaster,
    /// Spiritmaster, Skald).
    ///
    /// Fires <see cref="MimicBrain.CheckSpells"/> with the CrowdControl
    /// type whenever the group has tracked CC targets. Priority 85 sits
    /// just under HealerStrategy (90) and above all DPS/Tank strategies:
    /// keeping incoming adds CC'd is more valuable than landing one extra
    /// nuke or taunt, but never more valuable than a heal.
    ///
    /// Exclusive to prevent a CC mez from being immediately undone by a
    /// queued offensive nuke on the same target later in the tick.
    /// </summary>
    public sealed class CcStrategy : IBotStrategy
    {
        public const string Key = "cc";

        public string Name => Key;
        public string Description => "Bot AI v2 — crowd-control role: drives CheckSpells(CrowdControl) when adds are tracked.";

        public IEnumerable<BotTriggerActionBinding> GetBindings(BotContext ctx)
        {
            yield return new BotTriggerActionBinding(
                new GroupHasCcTargetsTrigger(),
                new DelegateCheckSpellsAction(MimicBrain.eCheckSpellType.CrowdControl, "cc-cycle"),
                priority: 85,
                cooldownMs: 750,
                exclusive: true);
        }
    }
}

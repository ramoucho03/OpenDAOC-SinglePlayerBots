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
            // PvE / camp adds: drive a CC cycle whenever the group has tracked
            // CC targets (queued by the puller / BAF of StandardMobBrain).
            yield return new BotTriggerActionBinding(
                new GroupHasCcTargetsTrigger(),
                new DelegateCheckSpellsAction(MimicBrain.eCheckSpellType.CrowdControl, "cc-cycle"),
                priority: 85,
                cooldownMs: 750,
                exclusive: true);

            // RvR group fight: the PvE add-queue is never populated with enemy
            // players, so this dedicated PvP trigger pivots the mezzer onto the
            // enemy group instead. Same CrowdControl action — its PvP path
            // (PickPvpCcTarget) locks down loose healers/casters first and skips
            // the group's focus kill-target plus already-mezzed/immune enemies,
            // spreading a clean chain-mez across the enemy line. Priority 86 sits
            // one above the PvE CC binding and still below HealerStrategy (90).
            yield return new BotTriggerActionBinding(
                new PvpCcOpportunityTrigger(),
                new DelegateCheckSpellsAction(MimicBrain.eCheckSpellType.CrowdControl, "cc-pvp-cycle"),
                priority: 86,
                cooldownMs: 1000,
                exclusive: true);
        }
    }
}

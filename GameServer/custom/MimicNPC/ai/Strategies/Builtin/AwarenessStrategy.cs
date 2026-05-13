using DOL.GS.Scripts.AI.Strategies.Actions;
using DOL.GS.Scripts.AI.Strategies.Triggers;
using System.Collections.Generic;

namespace DOL.GS.Scripts.AI.Strategies.Builtin
{
    /// <summary>
    /// Self-reports critical state to the group: low health, low mana for
    /// casters/healers. Uses long cooldowns to avoid chat spam.
    /// </summary>
    public sealed class AwarenessStrategy : IBotStrategy
    {
        public const string Key = "awareness";

        public string Name => Key;
        public string Description => "Annonce périodiquement au groupe les baisses de vie / mana / endurance.";

        public IEnumerable<BotTriggerActionBinding> GetBindings(BotContext ctx)
        {
            yield return new BotTriggerActionBinding(
                new HealthBelowTrigger(15),
                new GroupSayAction("Je suis critique, j'ai besoin d'un soin maintenant !", "say-low-health-crit"),
                priority: 90,
                cooldownMs: 15_000,
                exclusive: false);

            yield return new BotTriggerActionBinding(
                new HealthBelowTrigger(30),
                new GroupSayAction("Ma vie est basse !", "say-low-health"),
                priority: 70,
                cooldownMs: 30_000,
                exclusive: false);

            yield return new BotTriggerActionBinding(
                new ManaBelowTrigger(20),
                new GroupSayAction("Je n'ai presque plus de mana.", "say-low-mana"),
                priority: 40,
                cooldownMs: 60_000,
                exclusive: false);

            yield return new BotTriggerActionBinding(
                new EnduranceBelowTrigger(20),
                new GroupSayAction("Je suis à bout de souffle.", "say-low-endurance"),
                priority: 30,
                cooldownMs: 60_000,
                exclusive: false);
        }
    }
}

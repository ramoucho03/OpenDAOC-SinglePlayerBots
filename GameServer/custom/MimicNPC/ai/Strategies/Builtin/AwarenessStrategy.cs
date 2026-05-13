using DOL.GS.Scripts.AI.Strategies.Actions;
using DOL.GS.Scripts.AI.Strategies.Triggers;
using System.Collections.Generic;

namespace DOL.GS.Scripts.AI.Strategies.Builtin
{
    /// <summary>
    /// Self-reports critical state to the group (low health, mana, endurance)
    /// and adds occasional banter when OOC and rested. All messages are
    /// localized per recipient and pick one of several variants at random
    /// so bots don't sound robotic.
    /// </summary>
    public sealed class AwarenessStrategy : IBotStrategy
    {
        public const string Key = "awareness";

        public string Name => Key;
        public string Description => "Annonce vie/mana/endurance basses et glisse parfois une replique en repos.";

        public IEnumerable<BotTriggerActionBinding> GetBindings(BotContext ctx)
        {
            yield return new BotTriggerActionBinding(
                new HealthBelowTrigger(15),
                new LocalizedGroupSayAction("say-low-health-crit",
                    "Mimic.Chat.LowHealthCrit.1",
                    "Mimic.Chat.LowHealthCrit.2",
                    "Mimic.Chat.LowHealthCrit.3"),
                priority: 90,
                cooldownMs: 15_000,
                exclusive: false);

            yield return new BotTriggerActionBinding(
                new HealthBelowTrigger(30),
                new LocalizedGroupSayAction("say-low-health",
                    "Mimic.Chat.LowHealth.1",
                    "Mimic.Chat.LowHealth.2",
                    "Mimic.Chat.LowHealth.3"),
                priority: 70,
                cooldownMs: 30_000,
                exclusive: false);

            yield return new BotTriggerActionBinding(
                new ManaBelowTrigger(20),
                new LocalizedGroupSayAction("say-low-mana",
                    "Mimic.Chat.LowMana.1",
                    "Mimic.Chat.LowMana.2",
                    "Mimic.Chat.LowMana.3"),
                priority: 40,
                cooldownMs: 60_000,
                exclusive: false);

            yield return new BotTriggerActionBinding(
                new EnduranceBelowTrigger(20),
                new LocalizedGroupSayAction("say-low-endurance",
                    "Mimic.Chat.LowEnd.1",
                    "Mimic.Chat.LowEnd.2",
                    "Mimic.Chat.LowEnd.3"),
                priority: 30,
                cooldownMs: 60_000,
                exclusive: false);

            // Idle banter: long cooldown (5 min) so it stays charming, not noisy.
            yield return new BotTriggerActionBinding(
                new OutOfCombatRestedTrigger(),
                new LocalizedGroupSayAction("say-banter",
                    "Mimic.Chat.Banter.1",
                    "Mimic.Chat.Banter.2",
                    "Mimic.Chat.Banter.3",
                    "Mimic.Chat.Banter.4",
                    "Mimic.Chat.Banter.5",
                    "Mimic.Chat.Banter.6"),
                priority: 10,
                cooldownMs: 300_000,
                exclusive: false);

            // Puller announcement: tied to MainPuller + IsPulling. Long cooldown
            // means a single line per pull even if the brain ticks many times.
            yield return new BotTriggerActionBinding(
                new IsPullingTrigger(),
                new LocalizedGroupSayAction("say-pulling",
                    "Mimic.Chat.Pulling.1",
                    "Mimic.Chat.Pulling.2",
                    "Mimic.Chat.Pulling.3"),
                priority: 60,
                cooldownMs: 30_000,
                exclusive: false);

            // Tank engage callout — fires when the main tank takes a hit and
            // settles into combat. Cooldown matches a typical fight length.
            yield return new BotTriggerActionBinding(
                new TankEngagedTrigger(),
                new LocalizedGroupSayAction("say-tank-engage",
                    "Mimic.Chat.TankEngage.1",
                    "Mimic.Chat.TankEngage.2",
                    "Mimic.Chat.TankEngage.3"),
                priority: 50,
                cooldownMs: 45_000,
                exclusive: false);
        }
    }
}

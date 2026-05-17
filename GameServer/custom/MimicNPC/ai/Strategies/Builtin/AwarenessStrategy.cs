using DOL.GS.PacketHandler;
using DOL.GS.Scripts.AI.Strategies.Actions;
using DOL.GS.Scripts.AI.Strategies.Triggers;
using System.Collections.Generic;

namespace DOL.GS.Scripts.AI.Strategies.Builtin
{
    /// <summary>
    /// Self-reports critical state to the group (low health, mana, endurance,
    /// self-afflicted) and adds occasional banter when OOC and rested. All
    /// chat lines are localized per recipient and pick a random variant so
    /// bots don't sound robotic. Phase D adds two pure-emote bindings on
    /// top so the bot is also visually expressive.
    /// </summary>
    public sealed class AwarenessStrategy : IBotStrategy
    {
        public const string Key = "awareness";

        public string Name => Key;
        public string Description => "Annonce vie/mana/endurance basses, demande un cure si afflige, glisse parfois une replique en repos.";

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

            // LowMana / LowEndurance bindings removed: they're personal-state
            // chatter with no actionable group implication (the affected bot
            // already throttles its own casts/styles). Translation keys are
            // kept in the language files for /msay manual use.

            // Self-afflicted: bot is mezzed/diseased/poisoned and wants
            // a cure. High-ish priority because a CC'd bot is dead weight
            // until cured. Cooldown matches CheckHeals' cure throttle so
            // we don't spam the chat while waiting.
            yield return new BotTriggerActionBinding(
                new SelfAfflictedTrigger(),
                new LocalizedGroupSayAction("say-need-cure",
                    "Mimic.Chat.NeedCure.1",
                    "Mimic.Chat.NeedCure.2",
                    "Mimic.Chat.NeedCure.3"),
                priority: 75,
                cooldownMs: 8_000,
                exclusive: false);

            // Idle banter: long cooldown (10 min) so it stays charming, not noisy.
            // Was 5 min — at scale (12 bots × 1 banter / 5 min) the group chat
            // got too saturated with flavour lines and drowned out actionable
            // callouts. Doubling the floor makes it rare-but-present.
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
                cooldownMs: 600_000,
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

            // Chain-pull announcement: fires when the puller stacks a
            // second mob on the same cycle (ChainPullCount > 0). Wires
            // the existing Mimic.Chat.ChainPull.* translations into a
            // real binding — they used to ship in the language files
            // but no strategy ever called them. Shorter cooldown than
            // the first-pull callout because chain pulls happen back
            // to back and we want the group to brace each time.
            yield return new BotTriggerActionBinding(
                new IsChainPullingTrigger(),
                new LocalizedGroupSayAction("say-chain-pulling",
                    "Mimic.Chat.ChainPull.1",
                    "Mimic.Chat.ChainPull.2",
                    "Mimic.Chat.ChainPull.3"),
                priority: 60,
                cooldownMs: 12_000,
                exclusive: false);

            // Tank engage chat callout removed: redundant with LeaderStrategy's
            // say-leader-engage on the same encounter trigger — keeping both
            // produced two near-simultaneous "I'm in combat" lines per pull,
            // a noticeable noise multiplier in chat with no extra information.
            // The visual emote below stays (it's silent).

            // Visual immersion: bang on shield when settling into a tank
            // engage. Silent, no chat noise.
            yield return new BotTriggerActionBinding(
                new TankEngagedTrigger(),
                new EmoteAction("emote-tank-engage", eEmote.BangOnShield),
                priority: 49,
                cooldownMs: 30_000,
                exclusive: false);

            // Camp ready: the group is rested, buffs up, the puller is
            // about to leave. A salute from anyone (Any role) reads as
            // "we're set". Long cooldown so we get one per camp cycle.
            yield return new BotTriggerActionBinding(
                new CampPhaseTrigger(MimicGroup.eCampPhase.Ready),
                new EmoteAction("emote-camp-ready", eEmote.Salute),
                priority: 20,
                cooldownMs: 90_000,
                exclusive: false);
        }
    }
}

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
    ///
    /// Immersion pass (2026-05): the strategy now also fires (a) a rare,
    /// realm-flavoured idle emote when the bot has been out of combat for
    /// a while, (b) a victory "cheer" emote shortly after a fight ends, and
    /// (c) a stealth gate that mutes every chat/emote bark when the bot is
    /// stealthed so PvP scouts/SBs/NSs don't blow their own cover.
    /// </summary>
    public sealed class AwarenessStrategy : IBotStrategy
    {
        public const string Key = "awareness";

        public string Name => Key;
        public string Description => "Annonce vie/mana/endurance basses, demande un cure si afflige, glisse parfois une replique en repos.";

        public IEnumerable<BotTriggerActionBinding> GetBindings(BotContext ctx)
        {
            yield return new BotTriggerActionBinding(
                new NotStealthedAndTrigger(new HealthBelowTrigger(15)),
                new LocalizedGroupSayAction("say-low-health-crit",
                    "Mimic.Chat.LowHealthCrit.1",
                    "Mimic.Chat.LowHealthCrit.2",
                    "Mimic.Chat.LowHealthCrit.3"),
                priority: 90,
                cooldownMs: 15_000,
                exclusive: false);

            yield return new BotTriggerActionBinding(
                new NotStealthedAndTrigger(new HealthBelowTrigger(30)),
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
                new NotStealthedAndTrigger(new SelfAfflictedTrigger()),
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
                new NotStealthedAndTrigger(new OutOfCombatRestedTrigger()),
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
                new NotStealthedAndTrigger(new IsPullingTrigger()),
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
                new NotStealthedAndTrigger(new IsChainPullingTrigger()),
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
                new NotStealthedAndTrigger(new TankEngagedTrigger()),
                new EmoteAction("emote-tank-engage", eEmote.BangOnShield),
                priority: 49,
                cooldownMs: 30_000,
                exclusive: false);

            // Camp ready: the group is rested, buffs up, the puller is
            // about to leave. A salute from anyone (Any role) reads as
            // "we're set". Long cooldown so we get one per camp cycle.
            yield return new BotTriggerActionBinding(
                new NotStealthedAndTrigger(new CampPhaseTrigger(MimicGroup.eCampPhase.Ready)),
                new EmoteAction("emote-camp-ready", eEmote.Salute),
                priority: 20,
                cooldownMs: 90_000,
                exclusive: false);

            // ---------------------------------------------------------------
            // Immersion bindings (silent visual flavour, no chat impact).
            // ---------------------------------------------------------------

            // Idle flavour emote: bot has been out of combat for a while and
            // is reasonably rested. Picks a random realm-appropriate emote
            // (yawn / stretch / look around / drink) with an internal 25%
            // chance gate so even after the 3-min cooldown clears, the bot
            // only animates ~once per ~12 min on average per individual bot
            // — visible but never spammy. Per-binding cooldown (180 s) lives
            // on the binding, so it's per-bot (not global). Stealthed bots
            // are excluded by the trigger wrapper.
            yield return new BotTriggerActionBinding(
                new NotStealthedAndTrigger(new OutOfCombatRestedTrigger()),
                new IdleFlavorEmoteAction("emote-idle", chancePct: 25),
                priority: 5,
                cooldownMs: 180_000,
                exclusive: false);

            // Post-combat relief: fires shortly after a fight wraps up
            // (combat went quiet within the last 8 s and the bot is no
            // longer InCombat). 30% chance per eligible tick + a 90 s
            // per-bot cooldown means roughly one cheer/flex per couple of
            // fights, never two on the same fight. Reads as "phew, we
            // made it" from melee and as "victory" from casters.
            yield return new BotTriggerActionBinding(
                new NotStealthedAndTrigger(new JustLeftCombatTrigger(windowMs: 8000)),
                new IdleFlavorEmoteAction("emote-victory", chancePct: 30, victory: true),
                priority: 8,
                cooldownMs: 90_000,
                exclusive: false);
        }

        // -------------------------------------------------------------------
        // Inline helper triggers / actions kept private to the strategy.
        // They are too narrow to live in their own file and rely on no state
        // beyond the BotContext.
        // -------------------------------------------------------------------

        /// <summary>
        /// Composite gate: passes only when the inner trigger fires AND the
        /// bot is not stealthed. Wrapped around every chat/emote binding so
        /// a Scout or Shadowblade in stealth doesn't blow its cover with a
        /// "[Party] Foo: 'Heal me!'" bubble. Stealth check is cheap (one
        /// effect-list lookup) so applying it broadly is fine.
        /// </summary>
        private sealed class NotStealthedAndTrigger : IBotTrigger
        {
            private readonly IBotTrigger _inner;

            public string Name { get; }

            public NotStealthedAndTrigger(IBotTrigger inner)
            {
                _inner = inner;
                Name = "not-stealthed&" + (inner?.Name ?? "null");
            }

            public bool Check(BotContext ctx)
            {
                if (ctx?.Bot == null)
                    return false;

                if (ctx.Bot.IsStealthed)
                    return false;

                return _inner != null && _inner.Check(ctx);
            }
        }

        /// <summary>
        /// Trigger fired when the bot is currently out of combat but was in
        /// combat very recently (within <c>_windowMs</c>). Used to bind a
        /// "we just won" emote without needing an explicit kill event hook.
        /// </summary>
        private sealed class JustLeftCombatTrigger : IBotTrigger
        {
            private readonly long _windowMs;

            public string Name => "just-left-combat";

            public JustLeftCombatTrigger(long windowMs)
            {
                _windowMs = windowMs;
            }

            public bool Check(BotContext ctx)
            {
                MimicNPC bot = ctx?.Bot;
                if (bot == null || !bot.IsAlive)
                    return false;

                if (bot.InCombat)
                    return false;

                long last = bot.LastAttackTick;
                if (last <= 0)
                    return false;

                long delta = ctx.NowMs - last;
                return delta >= 0 && delta <= _windowMs;
            }
        }

        /// <summary>
        /// Silent emote action that picks a random animation from a
        /// realm-flavoured pool. An internal <c>_chancePct</c> gate lets
        /// the caller throttle the effective firing rate below what the
        /// binding cooldown alone allows. <c>victory=true</c> swaps the
        /// pool for celebratory animations (cheer / flex / laugh / dance).
        /// </summary>
        private sealed class IdleFlavorEmoteAction : IBotAction
        {
            // Realm-flavoured idle pools. Kept short on purpose: bots cycle
            // through a few signature emotes per realm so players can read
            // the realm at a glance, not a 20-emote variety show. No /sit
            // here — sitting is already driven by SitDownAction in resting
            // contexts and triggering it from a fire-and-forget emote would
            // strand the bot mid-pull.
            private static readonly eEmote[] _idleAlb = { eEmote.Yawn, eEmote.Ponder, eEmote.Salute, eEmote.Drink };
            private static readonly eEmote[] _idleMid = { eEmote.Yawn, eEmote.Roar, eEmote.Flex, eEmote.Drink };
            private static readonly eEmote[] _idleHib = { eEmote.Yawn, eEmote.Smile, eEmote.Worship, eEmote.Drink };
            private static readonly eEmote[] _idleNeutral = { eEmote.Yawn, eEmote.Ponder, eEmote.Drink };

            private static readonly eEmote[] _victoryAlb = { eEmote.Cheer, eEmote.Salute, eEmote.Clap };
            private static readonly eEmote[] _victoryMid = { eEmote.Roar, eEmote.Flex, eEmote.Cheer };
            private static readonly eEmote[] _victoryHib = { eEmote.Cheer, eEmote.Laugh, eEmote.Clap };
            private static readonly eEmote[] _victoryNeutral = { eEmote.Cheer };

            private readonly int _chancePct;
            private readonly bool _victory;

            public string Name { get; }

            public IdleFlavorEmoteAction(string actionName, int chancePct, bool victory = false)
            {
                _chancePct = chancePct < 1 ? 1 : (chancePct > 100 ? 100 : chancePct);
                _victory = victory;
                Name = actionName;
            }

            public bool IsPossible(BotContext ctx)
            {
                MimicNPC bot = ctx?.Bot;
                if (bot == null || !bot.IsAlive || bot.IsCasting || bot.IsStunned || bot.IsMezzed)
                    return false;

                // Stealth is already filtered upstream by NotStealthedAndTrigger
                // but re-check defensively: actions can in principle be reused
                // outside the strategy and we don't want a leaky bark.
                if (bot.IsStealthed)
                    return false;

                // Per-tick random gate. The binding cooldown sets the FLOOR
                // between attempts; this gate decides whether an eligible
                // attempt actually plays. Net effect: fewer "robotic, every
                // cooldown" emotes, more "occasionally the bot does a thing"
                // feel — which is exactly the design intent.
                return Util.Chance(_chancePct);
            }

            public bool Execute(BotContext ctx)
            {
                MimicNPC bot = ctx.Bot;
                eEmote[] pool = SelectPool(bot.Realm);
                if (pool == null || pool.Length == 0)
                    return false;

                eEmote e = pool[Util.Random(pool.Length - 1)];
                bot.Emote(e);
                return true;
            }

            private eEmote[] SelectPool(eRealm realm)
            {
                if (_victory)
                {
                    return realm switch
                    {
                        eRealm.Albion   => _victoryAlb,
                        eRealm.Midgard  => _victoryMid,
                        eRealm.Hibernia => _victoryHib,
                        _               => _victoryNeutral,
                    };
                }

                return realm switch
                {
                    eRealm.Albion   => _idleAlb,
                    eRealm.Midgard  => _idleMid,
                    eRealm.Hibernia => _idleHib,
                    _               => _idleNeutral,
                };
            }
        }
    }
}

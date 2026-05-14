using DOL.GS.Scripts.AI.Strategies.Actions;
using DOL.GS.Scripts.AI.Strategies.Triggers;
using System.Collections.Generic;

namespace DOL.GS.Scripts.AI.Strategies.Builtin
{
    /// <summary>
    /// Group-dynamics layer specific to camp farming. Wires camp-phase
    /// transitions, incoming-add detection, CC announces, rez calls and the
    /// retreat horn to localized group-chat lines so a /mcamp session feels
    /// like a real coordinated party.
    ///
    /// Each binding is role-gated and cooldown-throttled so we never have the
    /// whole camp shouting the same line on the same tick. Designed to be
    /// enabled on every mimic in the camp — the role filters take care of
    /// picking the right speaker for each callout.
    /// </summary>
    public sealed class CampStrategy : IBotStrategy
    {
        public const string Key = "camp";

        public string Name => Key;
        public string Description => "Dynamique de camp: annonces phases, adds, CC, rez, retraite.";

        public IEnumerable<BotTriggerActionBinding> GetBindings(BotContext ctx)
        {
            // ---- Camp Ready ------------------------------------------------
            // Spoken by the puller when the group's vitals are back up. One
            // line per regen cycle (long cooldown).
            yield return new BotTriggerActionBinding(
                new CampPhaseTrigger(MimicGroup.eCampPhase.Ready, CampPhaseTrigger.eRoleFilter.Puller),
                new LocalizedGroupSayAction("say-camp-ready",
                    "Mimic.Chat.CampReady.1",
                    "Mimic.Chat.CampReady.2",
                    "Mimic.Chat.CampReady.3"),
                priority: 40,
                cooldownMs: 60_000,
                exclusive: false);

            // ---- Incoming Adds --------------------------------------------
            // Puller spots BAF friends near its target after the shot lands.
            yield return new BotTriggerActionBinding(
                new IncomingAddsTrigger(minAdds: 1, pullerOnly: true),
                new LocalizedGroupSayAction("say-incoming-adds",
                    "Mimic.Chat.IncomingAdds.1",
                    "Mimic.Chat.IncomingAdds.2",
                    "Mimic.Chat.IncomingAdds.3"),
                priority: 75,
                cooldownMs: 20_000,
                exclusive: false);

            // ---- CC posed (mez/root) --------------------------------------
            // Wires the long-dormant Mimic.Chat.CcCast.* keys. Fires on the
            // main CC bot during the cast of a mez spell; the binding's
            // cooldown keeps it to a single line per CC chain.
            yield return new BotTriggerActionBinding(
                new IsCastingCcTrigger(),
                new LocalizedGroupSayAction("say-cc-cast",
                    "Mimic.Chat.CcCast.1",
                    "Mimic.Chat.CcCast.2",
                    "Mimic.Chat.CcCast.3"),
                priority: 70,
                cooldownMs: 8_000,
                exclusive: false);

            // ---- Rez in progress ------------------------------------------
            // Healer announces while casting a resurrection so the rest of
            // the group knows not to break it.
            yield return new BotTriggerActionBinding(
                new IsRezzingTrigger(),
                new LocalizedGroupSayAction("say-rezzing",
                    "Mimic.Chat.Rezzing.1",
                    "Mimic.Chat.Rezzing.2",
                    "Mimic.Chat.Rezzing.3"),
                priority: 80,
                cooldownMs: 15_000,
                exclusive: false);

            // ---- Retreat horn ---------------------------------------------
            // Leader/main-assist yells when active hostiles exceed budget by
            // at least 2. Very long cooldown — once per wipe attempt.
            yield return new BotTriggerActionBinding(
                new GroupOverwhelmedTrigger(overflow: 2),
                new LocalizedGroupSayAction("say-retreat",
                    "Mimic.Chat.Retreat.1",
                    "Mimic.Chat.Retreat.2",
                    "Mimic.Chat.Retreat.3"),
                priority: 95,
                cooldownMs: 30_000,
                exclusive: false);

            // ---- Assist call ----------------------------------------------
            // Leader/main-assist names the focus target when combat starts.
            yield return new BotTriggerActionBinding(
                new CompositeAndTrigger(
                    new CampPhaseTrigger(MimicGroup.eCampPhase.Combat, CampPhaseTrigger.eRoleFilter.Leader),
                    new MainAssistHasTargetTrigger()),
                new LocalizedGroupSayAction("say-assist-call",
                    "Mimic.Chat.AssistCall.1",
                    "Mimic.Chat.AssistCall.2",
                    "Mimic.Chat.AssistCall.3"),
                priority: 55,
                cooldownMs: 25_000,
                exclusive: false);
        }
    }
}

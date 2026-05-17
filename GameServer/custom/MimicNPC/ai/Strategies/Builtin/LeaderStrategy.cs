using DOL.GS.PacketHandler;
using DOL.GS.Scripts.AI.Strategies.Actions;
using DOL.GS.Scripts.AI.Strategies.Triggers;
using System.Collections.Generic;

namespace DOL.GS.Scripts.AI.Strategies.Builtin
{
    /// <summary>
    /// "Tactical voice" of the group, scoped to whichever bot currently
    /// holds the MainLeader role. Filling out a use case for the leader
    /// flag that was previously only consulted by GroupOverwhelmedTrigger
    /// to deduplicate the retreat callout.
    ///
    /// Two bindings, both tied to camp-phase transitions:
    ///
    /// 1) Pulling -> Engaging: the puller's mob just acquired aggro on
    ///    the group. The leader yells "Engage!" and does a Beckon emote
    ///    so the players know it is time to commit DPS / break CC for
    ///    the focus target.
    /// 2) Combat -> PostCombat: the fight is clean. The leader signals
    ///    "Good fight, regen" so everyone knows it's safe to sit. Pairs
    ///    with the existing SurvivalStrategy sit-to-recover binding.
    ///
    /// Both bindings are filtered by CampPhaseTrigger to the Leader role
    /// so only one bot per group fires them — no duplicate yells.
    /// Long cooldowns (45-60 s) keep the leader from being noisy if the
    /// camp transitions through a phase rapidly.
    /// </summary>
    public sealed class LeaderStrategy : IBotStrategy
    {
        public const string Key = "leader";

        public string Name => Key;
        public string Description => "Voix tactique du MainLeader : engage / postcombat callouts sur transitions de camp.";

        public IEnumerable<BotTriggerActionBinding> GetBindings(BotContext ctx)
        {
            // Engage call: fires the tick the camp enters Engaging phase
            // (mob has aggro on a group member). Beckon emote pairs the
            // chat line with a visible signal for players still loading
            // the area or in another window.
            yield return new BotTriggerActionBinding(
                new CampPhaseTrigger(MimicGroup.eCampPhase.Engaging, CampPhaseTrigger.eRoleFilter.Leader),
                new LocalizedGroupSayAction("say-leader-engage",
                    "Mimic.Chat.LeaderEngage.1",
                    "Mimic.Chat.LeaderEngage.2",
                    "Mimic.Chat.LeaderEngage.3"),
                priority: 70,
                cooldownMs: 45_000,
                exclusive: false);

            yield return new BotTriggerActionBinding(
                new CampPhaseTrigger(MimicGroup.eCampPhase.Engaging, CampPhaseTrigger.eRoleFilter.Leader),
                new EmoteAction("emote-leader-engage", eEmote.Beckon),
                priority: 69,
                cooldownMs: 45_000,
                exclusive: false);

            // Post-combat call: fires when the group enters PostCombat
            // cleanly (everyone alive, no incoming aggro). 60 s cooldown
            // matches a typical camp dwell time so we don't repeat on a
            // back-to-back pull cycle.
            yield return new BotTriggerActionBinding(
                new CampPhaseTrigger(MimicGroup.eCampPhase.PostCombat, CampPhaseTrigger.eRoleFilter.Leader),
                new LocalizedGroupSayAction("say-leader-postcombat",
                    "Mimic.Chat.LeaderPostCombat.1",
                    "Mimic.Chat.LeaderPostCombat.2",
                    "Mimic.Chat.LeaderPostCombat.3"),
                priority: 20,
                cooldownMs: 60_000,
                exclusive: false);
        }
    }
}

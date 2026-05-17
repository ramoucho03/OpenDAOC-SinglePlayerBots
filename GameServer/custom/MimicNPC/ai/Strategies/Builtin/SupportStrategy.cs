using DOL.GS.Scripts.AI.Strategies.Actions;
using DOL.GS.Scripts.AI.Strategies.Triggers;
using System.Collections.Generic;

namespace DOL.GS.Scripts.AI.Strategies.Builtin
{
    /// <summary>
    /// "Voix du groupe" strategy. Announces CC pending, members in
    /// critical health, and members that need a cure-mez. Designed to be
    /// active on a single bot — typically the leader or main assist —
    /// to avoid chat spam.
    ///
    /// Phase D: all callouts now go through localized translation keys
    /// (Mimic.Chat.MemberCrit.* / MemberMezzed.* / CcCast.*) so each
    /// recipient sees the line in their account language. The previous
    /// inline French-only Announce* actions have been removed.
    /// </summary>
    public sealed class SupportStrategy : IBotStrategy
    {
        public const string Key = "support";

        public string Name => Key;
        public string Description => "Coordonne le groupe : annonce CC, mezz à dissiper et membres mourants.";

        // Critical-health threshold reused across the trigger and the
        // target getter so they always reference the same snapshot.
        private const int CriticalThreshold = 25;

        public IEnumerable<BotTriggerActionBinding> GetBindings(BotContext ctx)
        {
            yield return new BotTriggerActionBinding(
                new GroupMemberCriticalTrigger(CriticalThreshold),
                new LocalizedGroupSayWithTargetAction(
                    "announce-crit",
                    static c => GetCriticalMember(c, CriticalThreshold),
                    "Mimic.Chat.MemberCrit.1",
                    "Mimic.Chat.MemberCrit.2",
                    "Mimic.Chat.MemberCrit.3"),
                priority: 95,
                cooldownMs: 10_000,
                exclusive: false);

            yield return new BotTriggerActionBinding(
                new GroupMemberMezzedTrigger(),
                new LocalizedGroupSayWithTargetAction(
                    "announce-mezzed",
                    static c => c.MimicGroup?.MemberToCureMezz,
                    "Mimic.Chat.MemberMezzed.1",
                    "Mimic.Chat.MemberMezzed.2",
                    "Mimic.Chat.MemberMezzed.3"),
                priority: 80,
                cooldownMs: 15_000,
                exclusive: false);

            yield return new BotTriggerActionBinding(
                new GroupHasCcTargetsTrigger(),
                new LocalizedGroupSayAction(
                    "announce-cc",
                    "Mimic.Chat.CcCast.1",
                    "Mimic.Chat.CcCast.2"),
                priority: 60,
                cooldownMs: 12_000,
                exclusive: false);
        }

        private static GameLiving GetCriticalMember(BotContext ctx, int threshold)
        {
            MimicGroup mg = ctx.MimicGroup;

            if (mg == null)
                return null;

            lock (mg.HealLock)
            {
                GameLiving who = mg.MemberToHeal;
                return who != null && who != ctx.Bot && who.HealthPercent < threshold ? who : null;
            }
        }
    }
}

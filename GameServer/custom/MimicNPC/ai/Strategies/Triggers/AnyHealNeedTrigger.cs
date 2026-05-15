using DOL.GS.Scripts.AI.Strategies.Triggers;

namespace DOL.GS.Scripts.AI.Strategies.Triggers
{
    /// <summary>
    /// Composite predicate that fires as soon as the bot's group needs any
    /// kind of healing or curing attention: someone critical, someone below
    /// the configured low-health threshold, or someone currently mezzed.
    ///
    /// This trigger is intentionally coarse — the actual heal selection is
    /// done inside <see cref="DOL.AI.Brain.MimicBrain.CheckHeals"/>, which
    /// already picks the right spell (emergency vs normal, cure mezz vs
    /// cure disease/poison) based on full group state.
    ///
    /// Splitting this into per-spell triggers would require exposing
    /// dedicated entry points in MimicBrain; that lives in a follow-up
    /// refactor once a real need shows up.
    /// </summary>
    public sealed class AnyHealNeedTrigger : IBotTrigger
    {
        private readonly GroupMemberCriticalTrigger _critical;
        private readonly GroupMemberHealthLowTrigger _low;
        private readonly GroupMemberMezzedTrigger _mezzed;

        public string Name { get; }

        public AnyHealNeedTrigger(int lowThreshold, int criticalThreshold)
        {
            _critical = new GroupMemberCriticalTrigger(criticalThreshold);
            _low = new GroupMemberHealthLowTrigger(lowThreshold);
            _mezzed = new GroupMemberMezzedTrigger();
            Name = $"heal-need(low<{lowThreshold},crit<{criticalThreshold})";
        }

        public bool Check(BotContext ctx)
        {
            // Order matters: critical first because a single critical member
            // short-circuits the rest and is the cheapest condition to test
            // on a cached MimicGroup.MemberToHeal snapshot.
            return _critical.Check(ctx)
                || _mezzed.Check(ctx)
                || _low.Check(ctx);
        }
    }
}

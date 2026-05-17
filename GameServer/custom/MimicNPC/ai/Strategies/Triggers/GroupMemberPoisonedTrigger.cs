namespace DOL.GS.Scripts.AI.Strategies.Triggers
{
    /// <summary>
    /// True when at least one alive group member is currently poisoned and
    /// the <see cref="MimicGroup"/> cache flags them as this snapshot's
    /// preferred cure target.
    ///
    /// Cure Poison shares a 5-second window with Cure Disease inside
    /// MimicBrain.CheckHeals (to leave room for secondary healers and to
    /// throttle the dispatcher). The trigger fires regardless — the
    /// strategy layer adds its own cooldown on top.
    /// </summary>
    public sealed class GroupMemberPoisonedTrigger : IBotTrigger
    {
        public string Name => "group-member-poisoned";

        public bool Check(BotContext ctx)
        {
            MimicGroup mg = ctx.MimicGroup;

            if (mg == null)
                return false;

            lock (mg.HealLock)
            {
                return mg.MemberToCurePoison != null && mg.MemberToCurePoison.IsPoisoned;
            }
        }
    }
}

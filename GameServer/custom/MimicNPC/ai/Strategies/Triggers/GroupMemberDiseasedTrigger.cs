namespace DOL.GS.Scripts.AI.Strategies.Triggers
{
    /// <summary>
    /// True when at least one alive group member is currently diseased and
    /// the <see cref="MimicGroup"/> cache has identified them as the
    /// preferred cure target this snapshot.
    ///
    /// Disease is a slow plus stat debuff, far less lethal than mezz but
    /// still worth a binding of its own so a healer can apply Cure Disease
    /// while combat continues. The MimicGroup.HealLock acquisition stays
    /// inside Check; the trigger contract requires this lock to be cheap.
    /// </summary>
    public sealed class GroupMemberDiseasedTrigger : IBotTrigger
    {
        public string Name => "group-member-diseased";

        public bool Check(BotContext ctx)
        {
            MimicGroup mg = ctx.MimicGroup;

            if (mg == null)
                return false;

            lock (mg.HealLock)
            {
                return mg.MemberToCureDisease != null && mg.MemberToCureDisease.IsDiseased;
            }
        }
    }
}

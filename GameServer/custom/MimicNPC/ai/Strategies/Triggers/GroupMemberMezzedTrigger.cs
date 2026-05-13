namespace DOL.GS.Scripts.AI.Strategies.Triggers
{
    /// <summary>Vrai quand un membre du groupe est mezz et n'a pas encore reçu de dissipation.</summary>
    public sealed class GroupMemberMezzedTrigger : IBotTrigger
    {
        public string Name => "group-member-mezzed";

        public bool Check(BotContext ctx)
        {
            MimicGroup mg = ctx.MimicGroup;

            if (mg == null)
                return false;

            lock (mg.HealLock)
            {
                return mg.MemberToCureMezz != null && mg.MemberToCureMezz.IsMezzed;
            }
        }
    }
}

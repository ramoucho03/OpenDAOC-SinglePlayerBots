namespace DOL.GS.Scripts.AI.Strategies.Triggers
{
    /// <summary>
    /// Vrai quand au moins un membre du groupe est en HP critique (sous le
    /// seuil donné). Plus précis que `GroupMemberHealthLowTrigger` pour le
    /// rôle "alerte" : on ne tire que sur de la vraie urgence.
    /// </summary>
    public sealed class GroupMemberCriticalTrigger : IBotTrigger
    {
        private readonly int _threshold;

        public string Name { get; }

        public GroupMemberCriticalTrigger(int percent)
        {
            _threshold = percent;
            Name = "group-member-crit<" + percent;
        }

        public bool Check(BotContext ctx)
        {
            if (ctx.Group == null)
                return false;

            MimicGroup mg = ctx.MimicGroup;

            if (mg != null)
            {
                lock (mg.HealLock)
                {
                    if (mg.MemberToHeal != null && mg.MemberToHeal != ctx.Bot && mg.MemberToHeal.HealthPercent < _threshold)
                        return true;
                }
            }

            foreach (GameLiving member in ctx.Group.GetMembersInTheGroup())
            {
                if (member == null || member == ctx.Bot || !member.IsAlive)
                    continue;

                if (member.HealthPercent < _threshold)
                    return true;
            }

            return false;
        }
    }
}

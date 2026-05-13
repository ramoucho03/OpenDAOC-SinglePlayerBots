namespace DOL.GS.Scripts.AI.Strategies.Triggers
{
    /// <summary>
    /// Fires when any group member (the bot included) drops below the
    /// configured health percent. Reads the cached MimicGroup heal data
    /// when available to avoid scanning the group every tick.
    /// </summary>
    public sealed class GroupMemberHealthLowTrigger : IBotTrigger
    {
        private readonly int _threshold;

        public string Name { get; }

        public GroupMemberHealthLowTrigger(int percent)
        {
            _threshold = percent;
            Name = "group-member<" + percent;
        }

        public bool Check(BotContext ctx)
        {
            if (ctx.Group == null)
                return ctx.Bot.HealthPercent < _threshold;

            // Fast path: refresh group health snapshot via the bot's brain.
            MimicGroup mg = ctx.MimicGroup;

            if (mg != null)
            {
                lock (mg.HealLock)
                {
                    if (mg.MemberToHeal != null && mg.MemberToHeal.HealthPercent < _threshold)
                        return true;
                }
            }

            foreach (GameLiving member in ctx.Group.GetMembersInTheGroup())
            {
                if (member == null || !member.IsAlive)
                    continue;

                if (member.HealthPercent < _threshold)
                    return true;
            }

            return false;
        }
    }
}

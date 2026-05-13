using DOL.GS.PacketHandler;

namespace DOL.GS.Scripts.AI.Strategies.Actions
{
    /// <summary>
    /// Annonce qu'un membre du groupe est sur le point de mourir. Utilisé
    /// par la stratégie de support pour pousser les heals d'urgence.
    /// </summary>
    public sealed class AnnounceCriticalMemberAction : IBotAction
    {
        private readonly int _threshold;
        public string Name { get; }

        public AnnounceCriticalMemberAction(int threshold = 25)
        {
            _threshold = threshold;
            Name = "announce-crit<" + threshold;
        }

        public bool IsPossible(BotContext ctx)
        {
            if (ctx.Group == null)
                return false;

            MimicGroup mg = ctx.MimicGroup;

            if (mg == null)
                return false;

            lock (mg.HealLock)
            {
                return mg.MemberToHeal != null
                    && mg.MemberToHeal != ctx.Bot
                    && mg.MemberToHeal.HealthPercent < _threshold;
            }
        }

        public bool Execute(BotContext ctx)
        {
            MimicGroup mg = ctx.MimicGroup;
            GameLiving who;

            lock (mg.HealLock)
                who = mg.MemberToHeal;

            if (who == null)
                return false;

            ctx.Group.SendMessageToGroupMembers(ctx.Bot,
                who.Name + " est en train de mourir !",
                eChatType.CT_Group, eChatLoc.CL_ChatWindow);
            return true;
        }
    }
}

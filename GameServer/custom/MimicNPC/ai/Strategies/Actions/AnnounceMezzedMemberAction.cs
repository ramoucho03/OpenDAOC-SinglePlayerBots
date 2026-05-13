using DOL.GS.PacketHandler;

namespace DOL.GS.Scripts.AI.Strategies.Actions
{
    /// <summary>
    /// Annonce qu'un membre du groupe est mezz et a besoin d'une dissipation.
    /// Pensée pour les leaders et casters de support.
    /// </summary>
    public sealed class AnnounceMezzedMemberAction : IBotAction
    {
        public string Name => "announce-mezzed";

        public bool IsPossible(BotContext ctx) => ctx.MimicGroup?.MemberToCureMezz?.IsMezzed == true && ctx.Group != null;

        public bool Execute(BotContext ctx)
        {
            GameLiving mezzed = ctx.MimicGroup.MemberToCureMezz;
            ctx.Group.SendMessageToGroupMembers(ctx.Bot,
                mezzed.Name + " est mezz, j'ai besoin d'une dissipation !",
                eChatType.CT_Group, eChatLoc.CL_ChatWindow);
            return true;
        }
    }
}

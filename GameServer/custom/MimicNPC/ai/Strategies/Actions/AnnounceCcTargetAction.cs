using DOL.GS.PacketHandler;

namespace DOL.GS.Scripts.AI.Strategies.Actions
{
    /// <summary>
    /// Annonce au groupe une cible CC en attente. Sert au leader / main CC
    /// pour signaler à tout le monde quelle add doit être mez.
    /// </summary>
    public sealed class AnnounceCcTargetAction : IBotAction
    {
        public string Name => "announce-cc-target";

        public bool IsPossible(BotContext ctx)
        {
            MimicGroup mg = ctx.MimicGroup;
            return mg?.CCTargets != null && mg.CCTargets.Count > 0 && ctx.Group != null;
        }

        public bool Execute(BotContext ctx)
        {
            MimicGroup mg = ctx.MimicGroup;
            GameLiving cc = mg.CCTargets[0];

            string msg = cc != null
                ? "Mez sur " + cc.Name + " !"
                : "Mez la prochaine add !";

            ctx.Group.SendMessageToGroupMembers(ctx.Bot, msg, eChatType.CT_Group, eChatLoc.CL_ChatWindow);
            return true;
        }
    }
}

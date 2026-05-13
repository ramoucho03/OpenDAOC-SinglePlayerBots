using DOL.GS.PacketHandler;

namespace DOL.GS.Scripts.AI.Strategies.Actions
{
    /// <summary>
    /// Sends a fixed line to the group chat. Used by strategies that want
    /// to signal a state change (low health, retreating, etc.). Falls back
    /// to a /say bubble when the bot is not grouped.
    /// </summary>
    public sealed class GroupSayAction : IBotAction
    {
        private readonly string _message;
        public string Name { get; }

        public GroupSayAction(string message, string actionName = "group-say")
        {
            _message = message ?? string.Empty;
            Name = actionName;
        }

        public bool IsPossible(BotContext ctx) => !string.IsNullOrEmpty(_message);

        public bool Execute(BotContext ctx)
        {
            MimicNPC bot = ctx.Bot;

            if (ctx.Group != null)
                ctx.Group.SendMessageToGroupMembers(bot, _message, eChatType.CT_Group, eChatLoc.CL_ChatWindow);
            else
                bot.Say(_message);

            return true;
        }
    }
}

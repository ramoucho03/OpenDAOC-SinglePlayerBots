using DOL.GS.PacketHandler;

namespace DOL.GS.Scripts.AI.Strategies.Actions
{
    /// <summary>
    /// Plays a configured emote animation on the bot. Visible to every
    /// player in <see cref="WorldMgr.VISIBILITY_DISTANCE"/>, no chat
    /// message — purely visual.
    ///
    /// Used by the immersion layer to react to fight outcomes (cheer on
    /// kill, salute on summon, etc.) without spamming the chat. Cooldown
    /// belongs to the binding, not to this action.
    /// </summary>
    public sealed class EmoteAction : IBotAction
    {
        private readonly eEmote _emote;
        public string Name { get; }

        public EmoteAction(string actionName, eEmote emote)
        {
            _emote = emote;
            Name = actionName;
        }

        public bool IsPossible(BotContext ctx)
        {
            MimicNPC bot = ctx.Bot;
            return bot != null && bot.IsAlive && !bot.IsCasting && !bot.IsStunned && !bot.IsMezzed;
        }

        public bool Execute(BotContext ctx)
        {
            ctx.Bot.Emote(_emote);
            return true;
        }
    }
}

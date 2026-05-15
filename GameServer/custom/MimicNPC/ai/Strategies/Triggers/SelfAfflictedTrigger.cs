namespace DOL.GS.Scripts.AI.Strategies.Triggers
{
    /// <summary>
    /// True when the bot itself is currently mezzed, diseased or poisoned.
    /// Used by the immersion layer so a CC'd or DoT'd bot publicly asks
    /// for a cure instead of silently waiting for the healer's snapshot
    /// to notice them.
    ///
    /// Backed by the same state the heal dispatcher reads — no extra
    /// scan, just three property reads on GameLiving.
    /// </summary>
    public sealed class SelfAfflictedTrigger : IBotTrigger
    {
        public string Name => "self-afflicted";

        public bool Check(BotContext ctx)
        {
            MimicNPC bot = ctx.Bot;

            if (bot == null || !bot.IsAlive)
                return false;

            return bot.IsMezzed || bot.IsDiseased || bot.IsPoisoned;
        }
    }
}

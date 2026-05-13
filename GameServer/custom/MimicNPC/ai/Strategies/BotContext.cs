using DOL.AI.Brain;

namespace DOL.GS.Scripts.AI.Strategies
{
    /// <summary>
    /// Lightweight per-tick context handed to triggers and actions of the bot
    /// strategy system. Avoids re-fetching the same references in every check.
    /// </summary>
    public sealed class BotContext
    {
        public MimicNPC Bot { get; }
        public MimicBrain Brain { get; }
        public long NowMs { get; private set; }

        public GameObject Target => Bot.TargetObject;
        public Group Group => Bot.Group;
        public MimicGroup MimicGroup => Bot.Group?.MimicGroup;
        public bool InCombat => Bot.InCombat;

        internal BotContext(MimicNPC bot, MimicBrain brain)
        {
            Bot = bot;
            Brain = brain;
            NowMs = GameLoop.GameLoopTime;
        }

        internal void Refresh()
        {
            NowMs = GameLoop.GameLoopTime;
        }
    }
}

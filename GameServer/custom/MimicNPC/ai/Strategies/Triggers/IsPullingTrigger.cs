namespace DOL.GS.Scripts.AI.Strategies.Triggers
{
    /// <summary>
    /// Fires when this bot is the group's MainPuller AND has actually started a pull
    /// (IsPulling). Used to announce "I'm pulling now" exactly once per pull, with
    /// the binding's cooldown preventing spam if the pull is delayed.
    /// </summary>
    public sealed class IsPullingTrigger : IBotTrigger
    {
        public string Name => "is-pulling";

        public bool Check(BotContext ctx)
        {
            return ctx.Brain != null
                && ctx.Brain.IsMainPuller
                && ctx.Brain.IsPulling;
        }
    }
}

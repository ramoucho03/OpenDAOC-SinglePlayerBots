namespace DOL.GS.Scripts.AI.Strategies
{
    /// <summary>
    /// A trigger observes the bot/world state and decides whether the
    /// associated action should be considered this tick. Triggers are
    /// expected to be cheap, side-effect-free predicates.
    /// </summary>
    public interface IBotTrigger
    {
        string Name { get; }
        bool Check(BotContext ctx);
    }
}

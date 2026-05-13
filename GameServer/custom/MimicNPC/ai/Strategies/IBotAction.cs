namespace DOL.GS.Scripts.AI.Strategies
{
    /// <summary>
    /// An action is the executable side of a strategy binding. It MAY refuse
    /// to run by returning false from IsPossible (e.g. resource not ready,
    /// target invalid). Execute returns true if it actually performed an
    /// effect this tick — the manager uses that to apply the binding's cooldown.
    /// </summary>
    public interface IBotAction
    {
        string Name { get; }
        bool IsPossible(BotContext ctx);
        bool Execute(BotContext ctx);
    }
}

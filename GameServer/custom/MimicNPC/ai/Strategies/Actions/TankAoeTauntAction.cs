namespace DOL.GS.Scripts.AI.Strategies.Actions
{
    /// <summary>
    /// Queues an AoE/encompassing style for the next swing when the tank is
    /// surrounded by 3+ mobs. The heavy lifting (range counting, style book
    /// scan, NextCombatStyle assignment) lives in
    /// <see cref="DOL.AI.Brain.MimicBrain.TryFireAoeTauntStyle"/>.
    /// </summary>
    public sealed class TankAoeTauntAction : IBotAction
    {
        public string Name => "tank-aoe-taunt";

        public bool IsPossible(BotContext ctx)
        {
            return ctx?.Brain != null
                && ctx.Bot != null
                && ctx.Bot.IsAlive
                && ctx.Brain.IsActingAsTank;
        }

        public bool Execute(BotContext ctx) => ctx.Brain.TryFireAoeTauntStyle();
    }
}

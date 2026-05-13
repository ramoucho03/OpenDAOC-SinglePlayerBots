namespace DOL.GS.Scripts.AI.Strategies.Triggers
{
    /// <summary>
    /// Fires when this bot is the group's MainTank and just engaged a target.
    /// Combined with a long cooldown on the binding it produces one "I have aggro"
    /// announce per fight rather than every tick.
    /// </summary>
    public sealed class TankEngagedTrigger : IBotTrigger
    {
        public string Name => "tank-engaged";

        public bool Check(BotContext ctx)
        {
            return ctx.Brain != null
                && ctx.Brain.IsMainTank
                && ctx.Bot.InCombat
                && ctx.Bot.TargetObject is GameLiving t
                && t.IsAlive;
        }
    }
}

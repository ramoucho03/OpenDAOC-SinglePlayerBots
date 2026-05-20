namespace DOL.GS.Scripts.AI.Strategies.Triggers
{
    /// <summary>
    /// Fires when this bot is acting as the group's tank and just engaged a
    /// target. Combined with a long cooldown on the binding it produces one
    /// "I have aggro" announce per fight rather than every tick.
    ///
    /// Gated on IsActingAsTank (not IsMainTank) so a de-facto tank mimic in a
    /// player-led group — which is rarely the formally-assigned MainTank —
    /// still drives its engage activation.
    /// </summary>
    public sealed class TankEngagedTrigger : IBotTrigger
    {
        public string Name => "tank-engaged";

        public bool Check(BotContext ctx)
        {
            return ctx.Brain != null
                && ctx.Brain.IsActingAsTank
                && ctx.Bot.InCombat
                && ctx.Bot.TargetObject is GameLiving t
                && t.IsAlive;
        }
    }
}

namespace DOL.GS.Scripts.AI.Strategies.Triggers
{
    /// <summary>
    /// Fires when the bot is acting as the group's tank, has a current target,
    /// is in combat, and that target is currently attacking another group
    /// member instead of the tank. Used by the immersion / coordination
    /// layer so the tank publicly calls out lost aggro and the group can
    /// react (caster pauses nukes, healer prepares an off-target heal).
    ///
    /// Gated on IsActingAsTank (not IsMainTank): in a player-led group the
    /// de-facto tank mimic is rarely the formally-assigned MainTank, and the
    /// lost-aggro recovery cycle must still run for it.
    ///
    /// Reactive callout only — the tank's own taunt rotation lives in
    /// <see cref="DOL.AI.Brain.MimicBrain.CheckSpells"/> with
    /// <c>eCheckSpellType.Defensive</c> and continues to fire normally.
    /// </summary>
    public sealed class TankLostAggroTrigger : IBotTrigger
    {
        public string Name => "tank-lost-aggro";

        public bool Check(BotContext ctx)
        {
            if (!ctx.Brain.IsActingAsTank || !ctx.Bot.InCombat)
                return false;

            if (ctx.Bot.TargetObject is not GameLiving target || !target.IsAlive)
                return false;

            if (target.TargetObject is not GameLiving tankerOfTarget)
                return false;

            // The mob is on the tank — no problem.
            if (tankerOfTarget == ctx.Bot)
                return false;

            // The mob is on something not in the group — that's not a "lost
            // aggro" situation, it's normal RvR / world chatter.
            if (ctx.Group == null || !ctx.Group.IsInTheGroup(tankerOfTarget))
                return false;

            return true;
        }
    }
}

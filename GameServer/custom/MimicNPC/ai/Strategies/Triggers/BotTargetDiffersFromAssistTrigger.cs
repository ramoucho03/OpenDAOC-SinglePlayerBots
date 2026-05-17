namespace DOL.GS.Scripts.AI.Strategies.Triggers
{
    /// <summary>
    /// True when the bot's current target is not the main assist's current
    /// target, and the assist has a valid attackable target. Used to keep
    /// the focus fire tight in a group: if the main assist switches to a
    /// freshly-mezzed add that just broke, every DPS bot picks up the
    /// switch within one cooldown window instead of finishing their old
    /// mob first.
    ///
    /// Stateless on purpose — comparing the two TargetObject references is
    /// cheap, the binding cooldown handles the throttling.
    /// </summary>
    public sealed class BotTargetDiffersFromAssistTrigger : IBotTrigger
    {
        public string Name => "bot-target-not-assist";

        public bool Check(BotContext ctx)
        {
            MimicGroup mg = ctx.MimicGroup;

            if (mg?.MainAssist is not GameLiving assist || !assist.IsAlive)
                return false;

            if (assist == ctx.Bot)
                return false;

            if (assist.TargetObject is not GameLiving assistTarget)
                return false;

            if (!assistTarget.IsAlive || assistTarget.ObjectState != GameObject.eObjectState.Active)
                return false;

            // Already on the same target: nothing to do.
            if (ctx.Bot.TargetObject == assistTarget)
                return false;

            // Healer/passive bots are off-limits for forced focus switches.
            if (ctx.Brain.PreventCombat || ctx.Brain.IsHealer)
                return false;

            // Bug fix: only force-switch when the assist is genuinely engaging.
            // A player just left-clicking around to pick a target should not
            // make every DPS pivot. InCombatInLast handles the brief windup.
            bool assistEngaging = assist.IsAttacking
                                  || assist.attackComponent.AttackState
                                  || assist.InCombatInLast(2000)
                                  || (assist.IsCasting && assist.castingComponent?.SpellHandler?.Spell?.IsHarmful == true);
            if (!assistEngaging)
                return false;

            return GameServer.ServerRules.IsAllowedToAttack(ctx.Bot, assistTarget, true);
        }
    }
}

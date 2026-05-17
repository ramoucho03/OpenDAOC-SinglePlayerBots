namespace DOL.GS.Scripts.AI.Strategies.Triggers
{
    /// <summary>
    /// Vrai quand l'assist principal du groupe existe, est vivant, et a
    /// une cible vivante attaquable. Permet de déclencher un focus de
    /// cible cohérent dans tout le groupe.
    /// </summary>
    public sealed class MainAssistHasTargetTrigger : IBotTrigger
    {
        public string Name => "main-assist-has-target";

        public bool Check(BotContext ctx)
        {
            MimicGroup mg = ctx.MimicGroup;

            if (mg?.MainAssist is not GameLiving assist || !assist.IsAlive)
                return false;

            if (assist == ctx.Bot)
                return false;

            if (assist.TargetObject is not GameLiving target)
                return false;

            if (!target.IsAlive || target.ObjectState != GameObject.eObjectState.Active)
                return false;

            // Bug fix: the assist must actually be engaging the target, not
            // just have it selected. Without this check, a player picking a
            // mob with left-click (which sets TargetObject) would make every
            // DPS in the group rush in. Mimic should only assist once the
            // leader has committed (attacking, casting harmful, or already
            // in combat — InCombatInLast handles brief gaps between swings).
            bool assistEngaging = assist.IsAttacking
                                  || assist.attackComponent.AttackState
                                  || assist.InCombatInLast(2000)
                                  || (assist.IsCasting && assist.castingComponent?.SpellHandler?.Spell?.IsHarmful == true);
            if (!assistEngaging)
                return false;

            return GameServer.ServerRules.IsAllowedToAttack(ctx.Bot, target, true);
        }
    }
}

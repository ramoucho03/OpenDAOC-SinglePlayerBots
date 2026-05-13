namespace DOL.GS.Scripts.AI.Strategies.Actions
{
    /// <summary>
    /// Calque la cible du bot sur celle de l'assist principal et l'ajoute à
    /// l'AggroList du brain pour que la logique de combat existante prenne
    /// la suite (style, sort, attaque distance, etc.). N'agresse pas un
    /// allié et respecte PreventCombat.
    /// </summary>
    public sealed class FocusMainAssistTargetAction : IBotAction
    {
        public string Name => "focus-assist";

        public bool IsPossible(BotContext ctx)
        {
            if (ctx.Brain.PreventCombat || ctx.Brain.IsHealer)
                return false;

            MimicGroup mg = ctx.MimicGroup;

            if (mg?.MainAssist is not GameLiving assist || !assist.IsAlive || assist == ctx.Bot)
                return false;

            if (assist.TargetObject is not GameLiving target || !target.IsAlive)
                return false;

            return GameServer.ServerRules.IsAllowedToAttack(ctx.Bot, target, true);
        }

        public bool Execute(BotContext ctx)
        {
            GameLiving target = (GameLiving)ctx.MimicGroup.MainAssist.TargetObject;
            ctx.Bot.TargetObject = target;
            ctx.Brain.AddToAggroList(target, 1);
            return true;
        }
    }
}

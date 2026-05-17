namespace DOL.GS.Scripts.AI.Strategies.Triggers
{
    /// <summary>
    /// Vrai quand le bot n'a pas de cible vivante. Sert à détecter quand
    /// un bot DPS est "à vide" et doit calquer la cible de l'assist
    /// principal.
    /// </summary>
    public sealed class NoLiveTargetTrigger : IBotTrigger
    {
        public string Name => "no-live-target";

        public bool Check(BotContext ctx)
        {
            if (ctx.Target is not GameLiving t)
                return true;

            return !t.IsAlive || t.ObjectState != GameObject.eObjectState.Active;
        }
    }
}

namespace DOL.GS.Scripts.AI.Strategies.Triggers
{
    /// <summary>
    /// Vrai dès que le groupe a au moins une cible CC en attente (typiquement
    /// posée par le puller / BAF de StandardMobBrain).
    /// </summary>
    public sealed class GroupHasCcTargetsTrigger : IBotTrigger
    {
        public string Name => "group-has-cc-targets";

        public bool Check(BotContext ctx)
        {
            MimicGroup mg = ctx.MimicGroup;
            return mg?.CCTargets != null && mg.CCTargets.Count > 0;
        }
    }
}

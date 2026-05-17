namespace DOL.GS.Scripts.AI.Strategies.Triggers
{
    /// <summary>
    /// Fires when the group's MimicGroup.CampPhase matches the expected phase.
    /// Lets strategies attach phase-specific announces (e.g. "camp prêt" on
    /// Ready, "engage !" on Engaging). The trigger optionally restricts the
    /// firing to a specific role so we don't have every bot in the camp
    /// shouting the same line on phase change.
    /// </summary>
    public sealed class CampPhaseTrigger : IBotTrigger
    {
        public enum eRoleFilter { Any, Puller, Tank, Leader, CC, Healer }

        private readonly MimicGroup.eCampPhase _phase;
        private readonly eRoleFilter _role;

        public string Name { get; }

        public CampPhaseTrigger(MimicGroup.eCampPhase phase, eRoleFilter role = eRoleFilter.Any)
        {
            _phase = phase;
            _role = role;
            Name = "camp-phase=" + phase + (role == eRoleFilter.Any ? "" : "/" + role);
        }

        public bool Check(BotContext ctx)
        {
            MimicGroup mg = ctx.MimicGroup;
            if (mg == null || mg.CampPhase != _phase)
                return false;

            return _role switch
            {
                eRoleFilter.Puller => ctx.Brain.IsMainPuller,
                eRoleFilter.Tank   => ctx.Brain.IsMainTank,
                eRoleFilter.Leader => ctx.Brain.IsMainLeader,
                eRoleFilter.CC     => ctx.Brain.IsMainCC,
                eRoleFilter.Healer => ctx.Brain.IsHealer,
                _ => true,
            };
        }
    }
}

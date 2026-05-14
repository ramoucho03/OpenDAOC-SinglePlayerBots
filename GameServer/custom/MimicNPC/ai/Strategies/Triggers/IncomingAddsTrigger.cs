namespace DOL.GS.Scripts.AI.Strategies.Triggers
{
    /// <summary>
    /// Fires when the group's IncomingPullTarget has at least <see cref="_minAdds"/>
    /// hostile neighbours likely to BAF along. Used to broadcast "adds with the
    /// pull !" before the pack lands so CC/healers can pre-stage. Restricted
    /// to the puller by default — they're the ones who can see the pack as
    /// they walk it in.
    /// </summary>
    public sealed class IncomingAddsTrigger : IBotTrigger
    {
        private const int BAF_SCAN_RADIUS = 500;

        private readonly int _minAdds;
        private readonly bool _pullerOnly;

        public string Name { get; }

        public IncomingAddsTrigger(int minAdds = 1, bool pullerOnly = true)
        {
            _minAdds = minAdds;
            _pullerOnly = pullerOnly;
            Name = "incoming-adds>=" + minAdds + (pullerOnly ? "/puller" : "");
        }

        public bool Check(BotContext ctx)
        {
            if (_pullerOnly && !ctx.Brain.IsMainPuller)
                return false;

            MimicGroup mg = ctx.MimicGroup;
            if (mg?.IncomingPullTarget is not GameNPC pulled || !pulled.IsAlive)
                return false;

            int adds = 0;
            foreach (GameNPC n in pulled.GetNPCsInRadius(BAF_SCAN_RADIUS))
            {
                if (n == null || n == pulled || !n.IsAlive)
                    continue;
                if (n is MimicNPC || n is GameTaxi or GameTrainingDummy)
                    continue;
                if (!GameServer.ServerRules.IsAllowedToAttack(ctx.Bot, n, true))
                    continue;

                adds++;
                if (adds >= _minAdds)
                    return true;
            }

            return false;
        }
    }
}

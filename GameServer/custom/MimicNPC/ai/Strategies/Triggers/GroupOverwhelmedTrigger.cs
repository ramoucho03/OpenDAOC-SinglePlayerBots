using DOL.AI.Brain;

namespace DOL.GS.Scripts.AI.Strategies.Triggers
{
    /// <summary>
    /// Fires when the group is fighting more hostile mobs than the camp's
    /// pull budget allows. Counts unique hostile NPCs aggroed on any group
    /// member; when that exceeds <see cref="_overflow"/> on top of the budget
    /// (e.g. budget 2 + overflow 1 = retreat at 4 mobs), the trigger fires.
    /// Restricted to the leader/main assist so only one bot yells "retreat".
    /// </summary>
    public sealed class GroupOverwhelmedTrigger : IBotTrigger
    {
        private readonly int _overflow;

        public string Name { get; }

        public GroupOverwhelmedTrigger(int overflow = 1)
        {
            _overflow = overflow;
            Name = "group-overwhelmed+" + overflow;
        }

        public bool Check(BotContext ctx)
        {
            // Only the leader or main assist calls retreat; without this filter
            // every DPS in the camp would shout the line on the same tick.
            if (!ctx.Brain.IsMainLeader && !ctx.Brain.IsMainAssist)
                return false;

            MimicGroup mg = ctx.MimicGroup;
            if (mg == null || ctx.Group == null)
                return false;

            int budget = mg.MainTank != null && mg.MainTank.IsAlive ? 2 : 1;
            // CC capable bots widen the budget by 1 each, matching the puller's
            // own GetMaxPullCount heuristic without re-importing it here.
            foreach (GameLiving gl in ctx.Group.GetMembersInTheGroup())
            {
                if (gl is MimicNPC m && m.IsAlive && m.CanCastCrowdControlSpells)
                    budget++;
            }

            int hostiles = CountActiveHostiles(ctx);
            return hostiles > budget + _overflow;
        }

        private static int CountActiveHostiles(BotContext ctx)
        {
            // Walk the group's bots and collect unique hostile NPCs from their
            // ordered aggro lists. GetOrderedAggroList() is the public surface;
            // the underlying dict is protected so we route through it.
            System.Collections.Generic.HashSet<int> seen = new();

            foreach (GameLiving gl in ctx.Group.GetMembersInTheGroup())
            {
                if (gl is not MimicNPC m || !m.IsAlive || m.MimicBrain == null)
                    continue;

                foreach (MimicBrain.OrderedAggroListElement entry in m.MimicBrain.GetOrderedAggroList())
                {
                    if (entry?.Living is GameNPC npc
                        && npc.IsAlive
                        && npc.ObjectState == GameObject.eObjectState.Active
                        && npc is not MimicNPC)
                        seen.Add(npc.ObjectID);
                }
            }

            return seen.Count;
        }
    }
}

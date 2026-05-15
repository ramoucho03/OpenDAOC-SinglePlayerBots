namespace DOL.GS.Scripts.AI.Strategies.Triggers
{
    /// <summary>
    /// True when this bot is the group's MainPuller AND currently chain-pulling
    /// (already locked one mob this cycle and stacking another). Paired with
    /// the AwarenessStrategy callout binding so the puller yells "Another one
    /// incoming!" — group needs to brace for the second mob arriving.
    ///
    /// Stateless: reads the puller's <see cref="DOL.AI.Brain.MimicBrain.ChainPullCount"/>
    /// every tick. The binding's own cooldown keeps the callout from
    /// repeating every tick of the chain.
    /// </summary>
    public sealed class IsChainPullingTrigger : IBotTrigger
    {
        public string Name => "is-chain-pulling";

        public bool Check(BotContext ctx)
        {
            if (!ctx.Brain.IsMainPuller)
                return false;

            return ctx.Brain.ChainPullCount > 0;
        }
    }
}

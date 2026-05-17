namespace DOL.GS.Scripts.AI.Strategies.Triggers
{
    /// <summary>Trigger qui n'est vrai que si tous ses sous-triggers le sont. Évaluation court-circuitée.</summary>
    public sealed class CompositeAndTrigger : IBotTrigger
    {
        private readonly IBotTrigger[] _children;
        public string Name { get; }

        public CompositeAndTrigger(params IBotTrigger[] children)
        {
            _children = children ?? System.Array.Empty<IBotTrigger>();
            Name = "and(" + string.Join(",", System.Array.ConvertAll(_children, c => c?.Name ?? "?")) + ")";
        }

        public bool Check(BotContext ctx)
        {
            for (int i = 0; i < _children.Length; i++)
            {
                if (_children[i] == null || !_children[i].Check(ctx))
                    return false;
            }

            return true;
        }
    }
}

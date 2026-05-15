using DOL.AI.Brain;

namespace DOL.GS.Scripts.AI.Strategies.Actions
{
    /// <summary>
    /// Generic Bot AI v2 action that routes a tick of decision-making into
    /// the existing <see cref="MimicBrain.CheckSpells"/> dispatcher with a
    /// fixed <see cref="MimicBrain.eCheckSpellType"/>.
    ///
    /// Each role strategy binds an instance with the spell-type matching
    /// the role: tanks use Defensive (taunts, group-shielding ticks),
    /// casters and melee/ranged DPS use Offensive (the dispatcher then
    /// picks the right spell or "no spell" so the bot falls through to
    /// auto-attacks), CC specialists use CrowdControl.
    ///
    /// Centralising on one action class keeps Phase B small: per-role
    /// behaviour lives in the strategy's trigger + priority + cooldown,
    /// not in N parallel copies of nearly identical Execute bodies.
    /// </summary>
    public sealed class DelegateCheckSpellsAction : IBotAction
    {
        private readonly MimicBrain.eCheckSpellType _type;
        public string Name { get; }

        public DelegateCheckSpellsAction(MimicBrain.eCheckSpellType type, string name = null)
        {
            _type = type;
            Name = name ?? "check-spells-" + type.ToString().ToLowerInvariant();
        }

        public bool IsPossible(BotContext ctx)
        {
            if (ctx.Bot == null || !ctx.Bot.IsAlive)
                return false;

            if (ctx.Bot.IsStunned || ctx.Bot.IsMezzed)
                return false;

            return true;
        }

        public bool Execute(BotContext ctx)
        {
            // Already mid-cast: treat as a productive tick so the binding
            // cooldown applies. CheckSpells short-circuits internally when
            // the bot is busy, so calling it again is safe but wasteful.
            if (ctx.Bot.IsCasting)
                return true;

            return ctx.Brain.CheckSpells(_type);
        }
    }
}

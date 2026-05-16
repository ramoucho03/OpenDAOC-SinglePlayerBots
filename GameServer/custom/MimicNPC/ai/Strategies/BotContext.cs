using DOL.AI.Brain;
using System.Collections.Generic;

namespace DOL.GS.Scripts.AI.Strategies
{
    /// <summary>
    /// Lightweight per-tick context handed to triggers and actions of the bot
    /// strategy system. Avoids re-fetching the same references in every check.
    ///
    /// Per-tick caches: <see cref="GroupMembers"/> snapshots
    /// Group.GetMembersInTheGroup() once per Refresh — multiple triggers and
    /// actions enumerate group members on every Think, and the underlying
    /// call takes a lock + allocates a pooled copy. Caching it once per tick
    /// per bot collapses N enumerations into 1.
    /// </summary>
    public sealed class BotContext
    {
        public MimicNPC Bot { get; }
        public MimicBrain Brain { get; }
        public long NowMs { get; private set; }

        public GameObject Target => Bot.TargetObject;
        public Group Group => Bot.Group;
        public MimicGroup MimicGroup => Bot.Group?.MimicGroup;
        public bool InCombat => Bot.InCombat;

        // Per-tick cached group snapshot. Built lazily on first access and
        // invalidated by Refresh(). The list is owned by BotContext — callers
        // MUST NOT mutate it (treated as IReadOnlyList).
        private List<GameLiving> _groupMembers;
        private bool _groupMembersBuilt;

        /// <summary>
        /// Cached snapshot of the current group's living members for this
        /// tick. Returns an empty list when the bot is solo. The list is
        /// rebuilt on the next Refresh().
        /// </summary>
        public IReadOnlyList<GameLiving> GroupMembers
        {
            get
            {
                if (_groupMembersBuilt)
                    return _groupMembers ?? (IReadOnlyList<GameLiving>)System.Array.Empty<GameLiving>();

                _groupMembersBuilt = true;
                Group g = Bot.Group;
                if (g == null)
                {
                    _groupMembers = null;
                    return System.Array.Empty<GameLiving>();
                }

                _groupMembers = new List<GameLiving>(8);
                foreach (GameLiving gl in g.GetMembersInTheGroup())
                {
                    if (gl != null)
                        _groupMembers.Add(gl);
                }

                return _groupMembers;
            }
        }

        internal BotContext(MimicNPC bot, MimicBrain brain)
        {
            Bot = bot;
            Brain = brain;
            NowMs = GameLoop.GameLoopTime;
        }

        internal void Refresh()
        {
            NowMs = GameLoop.GameLoopTime;
            // Invalidate per-tick caches.
            _groupMembersBuilt = false;
            _groupMembers?.Clear();
        }
    }
}

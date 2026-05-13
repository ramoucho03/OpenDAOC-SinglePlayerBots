using DOL.AI.Brain;
using System;
using System.Collections.Generic;
using System.Linq;

namespace DOL.GS.Scripts.AI.Strategies
{
    /// <summary>
    /// Per-bot owner of the active strategies. Lazily created from
    /// MimicBrain when the strategy system is enabled.
    /// </summary>
    public sealed class BotStrategyManager
    {
        private readonly MimicNPC _bot;
        private readonly MimicBrain _brain;
        private readonly BotContext _ctx;

        private readonly object _stateLock = new();
        private readonly Dictionary<string, IBotStrategy> _strategies = new(StringComparer.OrdinalIgnoreCase);
        private List<BotTriggerActionBinding> _bindingsCache;

        public BotStrategyManager(MimicNPC bot, MimicBrain brain)
        {
            _bot = bot;
            _brain = brain;
            _ctx = new BotContext(bot, brain);
        }

        public IReadOnlyList<string> ActiveStrategies
        {
            get
            {
                lock (_stateLock)
                    return _strategies.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase).ToList();
            }
        }

        public bool Enable(string key)
        {
            if (!BotStrategyRegistry.TryCreate(key, out IBotStrategy strategy))
                return false;

            lock (_stateLock)
            {
                if (_strategies.ContainsKey(strategy.Name))
                    return false;

                _strategies[strategy.Name] = strategy;
                _bindingsCache = null;
            }

            strategy.OnEnable(_ctx);
            return true;
        }

        public bool Disable(string key)
        {
            IBotStrategy removed;

            lock (_stateLock)
            {
                if (!_strategies.TryGetValue(key, out removed))
                    return false;

                _strategies.Remove(key);
                _bindingsCache = null;
            }

            removed.OnDisable(_ctx);
            return true;
        }

        public bool Toggle(string key)
        {
            lock (_stateLock)
            {
                if (_strategies.ContainsKey(key))
                {
                    // Release lock before invoking OnDisable.
                }
            }

            return Disable(key) || Enable(key);
        }

        public void Clear()
        {
            List<IBotStrategy> snapshot;

            lock (_stateLock)
            {
                snapshot = _strategies.Values.ToList();
                _strategies.Clear();
                _bindingsCache = null;
            }

            foreach (IBotStrategy s in snapshot)
            {
                try { s.OnDisable(_ctx); }
                catch { /* swallow — strategy disable must not break the brain */ }
            }
        }

        /// <summary>
        /// Evaluate every binding in priority order. A binding fires if its
        /// trigger checks AND the action is possible AND the cooldown
        /// elapsed. Exclusive bindings end the tick after firing.
        /// </summary>
        public void Tick()
        {
            if (_bot == null || !_bot.IsAlive || _bot.ObjectState != GameObject.eObjectState.Active)
                return;

            List<BotTriggerActionBinding> bindings = GetOrBuildBindings();

            if (bindings.Count == 0)
                return;

            _ctx.Refresh();
            long now = _ctx.NowMs;

            for (int i = 0; i < bindings.Count; i++)
            {
                BotTriggerActionBinding b = bindings[i];

                if (b.NextAllowedTick > now)
                    continue;

                bool triggered;
                try { triggered = b.Trigger.Check(_ctx); }
                catch { triggered = false; }

                if (!triggered)
                    continue;

                bool possible;
                try { possible = b.Action.IsPossible(_ctx); }
                catch { possible = false; }

                if (!possible)
                    continue;

                bool executed;
                try { executed = b.Action.Execute(_ctx); }
                catch { executed = false; }

                if (!executed)
                    continue;

                if (b.CooldownMs > 0)
                    b.NextAllowedTick = now + b.CooldownMs;

                if (b.Exclusive)
                    break;
            }
        }

        private List<BotTriggerActionBinding> GetOrBuildBindings()
        {
            lock (_stateLock)
            {
                if (_bindingsCache != null)
                    return _bindingsCache;

                List<BotTriggerActionBinding> list = new();

                foreach (IBotStrategy strat in _strategies.Values)
                {
                    IEnumerable<BotTriggerActionBinding> contributed;

                    try { contributed = strat.GetBindings(_ctx); }
                    catch { contributed = null; }

                    if (contributed == null)
                        continue;

                    foreach (BotTriggerActionBinding b in contributed)
                    {
                        if (b == null || b.Trigger == null || b.Action == null)
                            continue;

                        b.OwnerStrategy = strat.Name;
                        list.Add(b);
                    }
                }

                list.Sort(static (a, b) => b.Priority.CompareTo(a.Priority));
                _bindingsCache = list;
                return list;
            }
        }
    }
}

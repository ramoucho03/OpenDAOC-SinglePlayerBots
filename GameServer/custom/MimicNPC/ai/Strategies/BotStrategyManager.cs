using DOL.AI.Brain;
using DOL.GS.PacketHandler;
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

        // ---- Diagnostic trace ----
        // When an operator runs /mstrategy trace <secs>, every binding
        // evaluated by Tick() is reported to the observer for the requested
        // window. Trace state is per-bot, not shared, so multiple players
        // can each follow their own mimic without overlap.
        private GamePlayer _traceObserver;
        private long _traceUntilTick;

        /// <summary>
        /// Streams every binding evaluation to <paramref name="observer"/>
        /// for the next <paramref name="seconds"/> seconds. Pass null /
        /// negative seconds to stop a running trace.
        /// </summary>
        public void StartTrace(GamePlayer observer, int seconds)
        {
            if (observer == null || seconds <= 0)
            {
                _traceObserver = null;
                _traceUntilTick = 0;
                return;
            }

            _traceObserver = observer;
            _traceUntilTick = GameLoop.GameLoopTime + seconds * 1000L;
        }

        public bool IsTracing => _traceObserver != null && _traceUntilTick > GameLoop.GameLoopTime;

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
            bool tracing = IsTracing;

            for (int i = 0; i < bindings.Count; i++)
            {
                BotTriggerActionBinding b = bindings[i];

                if (b.NextAllowedTick > now)
                {
                    if (tracing)
                        TraceLine($"  [skip-cd] {Describe(b)} cd={b.NextAllowedTick - now}ms");
                    continue;
                }

                bool triggered;
                try { triggered = b.Trigger.Check(_ctx); }
                catch (Exception ex) { triggered = false; if (tracing) TraceLine($"  [trigger-EX] {Describe(b)} {ex.GetType().Name}: {ex.Message}"); }

                if (!triggered)
                {
                    if (tracing)
                        TraceLine($"  [skip-trig] {Describe(b)}");
                    continue;
                }

                bool possible;
                try { possible = b.Action.IsPossible(_ctx); }
                catch (Exception ex) { possible = false; if (tracing) TraceLine($"  [action-IsPossible-EX] {Describe(b)} {ex.GetType().Name}: {ex.Message}"); }

                if (!possible)
                {
                    if (tracing)
                        TraceLine($"  [skip-imposs] {Describe(b)}");
                    continue;
                }

                bool executed;
                try { executed = b.Action.Execute(_ctx); }
                catch (Exception ex) { executed = false; if (tracing) TraceLine($"  [action-Execute-EX] {Describe(b)} {ex.GetType().Name}: {ex.Message}"); }

                if (tracing)
                    TraceLine(executed ? $"  [FIRE]      {Describe(b)}" : $"  [no-effect] {Describe(b)}");

                if (!executed)
                    continue;

                if (b.CooldownMs > 0)
                    b.NextAllowedTick = now + b.CooldownMs;

                if (b.Exclusive)
                    break;
            }

            // Auto-disable trace once the window closes — keeps the observer
            // from being silently spammed if the manager keeps ticking.
            if (_traceObserver != null && _traceUntilTick > 0 && now >= _traceUntilTick)
            {
                _traceObserver.Out.SendMessage($"[mstrategy] trace de {_bot.Name} terminée.", eChatType.CT_System, eChatLoc.CL_SystemWindow);
                _traceObserver = null;
                _traceUntilTick = 0;
            }
        }

        private static string Describe(BotTriggerActionBinding b)
        {
            return $"strat={b.OwnerStrategy} prio={b.Priority} trig={b.Trigger?.Name ?? "?"} act={b.Action?.Name ?? "?"}";
        }

        private void TraceLine(string line)
        {
            if (_traceObserver == null)
                return;
            try
            {
                _traceObserver.Out.SendMessage($"[trace {_bot.Name}] {line}", eChatType.CT_System, eChatLoc.CL_SystemWindow);
            }
            catch { /* observer disconnected mid-trace */ }
        }

        private List<BotTriggerActionBinding> GetOrBuildBindings()
        {
            // Fast path: cached snapshot under lock.
            lock (_stateLock)
            {
                if (_bindingsCache != null)
                    return _bindingsCache;
            }

            // Snapshot the active strategies under the lock, but call
            // GetBindings OUTSIDE the lock. A strategy's GetBindings can be
            // arbitrary user code — letting it run while we hold _stateLock
            // creates a deadlock risk if the implementation calls back into
            // the manager (Enable/Disable/Toggle/Tick all take _stateLock).
            IBotStrategy[] snapshot;
            lock (_stateLock)
            {
                snapshot = new IBotStrategy[_strategies.Values.Count];
                _strategies.Values.CopyTo(snapshot, 0);
            }

            List<BotTriggerActionBinding> list = new();

            foreach (IBotStrategy strat in snapshot)
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

            // Publish. A concurrent Enable/Disable may have invalidated the
            // cache while we built this list — last writer wins is fine
            // because the next Tick will rebuild from the up-to-date
            // _strategies snapshot.
            lock (_stateLock)
            {
                _bindingsCache = list;
                return list;
            }
        }
    }
}

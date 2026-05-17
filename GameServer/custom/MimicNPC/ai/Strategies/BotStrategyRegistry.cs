using System;
using System.Collections.Generic;
using System.Linq;

namespace DOL.GS.Scripts.AI.Strategies
{
    /// <summary>
    /// Static registry of available strategy factories. Strategies must be
    /// registered with a stable lower-case key — the player-facing /mstrategy
    /// command resolves names against it.
    /// </summary>
    public static class BotStrategyRegistry
    {
        private static readonly object _lock = new();
        private static readonly Dictionary<string, Func<IBotStrategy>> _factories = new(StringComparer.OrdinalIgnoreCase);

        public static void Register(string key, Func<IBotStrategy> factory)
        {
            if (string.IsNullOrWhiteSpace(key) || factory == null)
                return;

            lock (_lock)
                _factories[key] = factory;
        }

        public static bool TryCreate(string key, out IBotStrategy strategy)
        {
            strategy = null;

            if (string.IsNullOrWhiteSpace(key))
                return false;

            Func<IBotStrategy> factory;

            lock (_lock)
            {
                if (!_factories.TryGetValue(key, out factory))
                    return false;
            }

            strategy = factory();
            return strategy != null;
        }

        public static IReadOnlyList<string> ListKeys()
        {
            lock (_lock)
                return _factories.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase).ToList();
        }
    }
}

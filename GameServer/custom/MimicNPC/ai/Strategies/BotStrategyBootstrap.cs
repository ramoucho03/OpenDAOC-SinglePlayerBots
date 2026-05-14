using DOL.Events;
using DOL.GS.Scripts.AI.Strategies.Builtin;
using System;
using System.Reflection;
using DOL.Logging;

namespace DOL.GS.Scripts.AI.Strategies
{
    /// <summary>
    /// Registers the built-in bot strategies into the global registry as
    /// soon as the scripts are compiled. Third-party assemblies are free
    /// to add their own strategies via BotStrategyRegistry.Register.
    /// </summary>
    public static class BotStrategyBootstrap
    {
        private static readonly Logger log = LoggerManager.Create(MethodBase.GetCurrentMethod().DeclaringType);

        [ScriptLoadedEvent]
        public static void OnScriptsCompiled(DOLEvent e, object sender, EventArgs args)
        {
            BotStrategyRegistry.Register(PassiveStrategy.Key, static () => new PassiveStrategy());
            BotStrategyRegistry.Register(SurvivalStrategy.Key, static () => new SurvivalStrategy());
            BotStrategyRegistry.Register(AwarenessStrategy.Key, static () => new AwarenessStrategy());
            BotStrategyRegistry.Register(AssistStrategy.Key, static () => new AssistStrategy());
            BotStrategyRegistry.Register(SupportStrategy.Key, static () => new SupportStrategy());
            BotStrategyRegistry.Register(CampStrategy.Key, static () => new CampStrategy());

            log.Info("Registre des stratégies bot initialisé (" + BotStrategyRegistry.ListKeys().Count + " stratégies).");
        }
    }
}

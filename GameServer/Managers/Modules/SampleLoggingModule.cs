using System;
using System.Collections.Generic;
using System.Reflection;
using DOL.Logging;

namespace DOL.GS.Modules
{
    /// <summary>
    /// Reference implementation of <see cref="IGameModule"/>.
    ///
    /// Disabled by default (<see cref="GameModuleAttribute.Enabled"/> = false)
    /// so a stock server gets the registry plumbed in without any behavior
    /// change. Set Enabled = true here to verify the discovery+lifecycle
    /// path end-to-end during development.
    ///
    /// Real modules should live next to the feature they enable (script
    /// directory, custom/, etc.) and not in this folder.
    /// </summary>
    [GameModule(Enabled = false)]
    public sealed class SampleLoggingModule : IGameModule
    {
        private static readonly Logger log = LoggerManager.Create(MethodBase.GetCurrentMethod().DeclaringType);

        public string Name => nameof(SampleLoggingModule);
        public IEnumerable<Type> DependsOn => Array.Empty<Type>();

        public bool Init(IGameServerContext context)
        {
            if (log.IsInfoEnabled)
                log.Info($"{Name} initialized (server: {context.Configuration.ServerName})");

            return true;
        }

        public void Shutdown()
        {
            if (log.IsInfoEnabled)
                log.Info($"{Name} shutting down");
        }
    }
}

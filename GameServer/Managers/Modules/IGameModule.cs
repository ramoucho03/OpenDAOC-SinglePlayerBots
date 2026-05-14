using System;
using System.Collections.Generic;

namespace DOL.GS.Modules
{
    /// <summary>
    /// Contract for an optional game-server module.
    ///
    /// A module is a self-contained extension that is discovered by reflection at
    /// boot time (either inside <c>GameServer</c> itself or inside a loaded script
    /// assembly) and initialized in dependency order.
    ///
    /// Modules are an additive extension point: the historical inline initialization
    /// in <see cref="GameServer.Start"/> still runs unchanged. Modules let new
    /// features plug in without touching <c>GameServer.cs</c>, and they can declare
    /// dependencies on each other so init order is explicit instead of implicit.
    /// </summary>
    public interface IGameModule
    {
        /// <summary>
        /// Stable identifier used in dependency declarations and logs. Should match
        /// the type's <c>Name</c> in the simple case.
        /// </summary>
        string Name { get; }

        /// <summary>
        /// Other module types that must complete <see cref="Init"/> successfully
        /// before this module starts. Return an empty enumerable when there are
        /// no dependencies. Cycles are detected by the registry and reported.
        /// </summary>
        IEnumerable<Type> DependsOn { get; }

        /// <summary>
        /// Called once during server startup, after all <c>InitComponent</c> calls
        /// in <see cref="GameServer.Start"/> have completed and before the game
        /// loop is started. Should be idempotent — the registry guards against
        /// double-init but modules should still avoid global side effects in their
        /// constructors.
        /// </summary>
        /// <returns>True on success. A false return aborts dependent modules.</returns>
        bool Init(IGameServerContext context);

        /// <summary>
        /// Called during server shutdown in reverse init order. Modules that did
        /// not successfully init are still asked to shut down so they can clean
        /// up partial state.
        /// </summary>
        void Shutdown();
    }
}

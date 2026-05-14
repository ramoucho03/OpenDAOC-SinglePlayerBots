using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using DOL.Logging;

namespace DOL.GS.Modules
{
    /// <summary>
    /// Discovers, orders, and lifecycles <see cref="IGameModule"/> implementations.
    ///
    /// The registry is invoked once at the end of <see cref="GameServer.Start"/>
    /// (after every <c>InitComponent</c> call has finished) and once at
    /// <see cref="GameServer.Stop"/>. It is intentionally additive: a server with
    /// no modules behaves identically to a server before the registry existed.
    /// </summary>
    public sealed class GameModuleRegistry
    {
        private static readonly Logger log = LoggerManager.Create(MethodBase.GetCurrentMethod().DeclaringType);

        private readonly List<IGameModule> _initOrder = new();
        private readonly HashSet<Type> _initialized = new();
        private bool _initRan;

        /// <summary>
        /// Modules that successfully initialized, in init order. Exposed for
        /// diagnostics and tests; do not mutate.
        /// </summary>
        public IReadOnlyList<IGameModule> InitializedModules => _initOrder;

        /// <summary>
        /// Scan <paramref name="assemblies"/> for module types, build a dependency
        /// graph, and call <see cref="IGameModule.Init"/> in topological order.
        ///
        /// Returns true if every discovered module initialized successfully. A
        /// false return does not unwind already-initialized modules — call
        /// <see cref="ShutdownAll"/> from the same flow that started the server
        /// to release them.
        /// </summary>
        public bool InitAll(IGameServerContext context, IEnumerable<Assembly> assemblies)
        {
            if (_initRan)
            {
                if (log.IsWarnEnabled)
                    log.Warn($"{nameof(GameModuleRegistry)}.{nameof(InitAll)} called twice; ignoring");

                return true;
            }

            _initRan = true;

            List<IGameModule> discovered = Discover(assemblies);

            if (discovered.Count == 0)
            {
                if (log.IsInfoEnabled)
                    log.Info("No game modules discovered");

                return true;
            }

            if (!TopologicalSort(discovered, out List<IGameModule> ordered, out string sortError))
            {
                if (log.IsErrorEnabled)
                    log.Error($"Module dependency resolution failed: {sortError}");

                return false;
            }

            bool allOk = true;

            foreach (IGameModule module in ordered)
            {
                if (!DependenciesSatisfied(module))
                {
                    if (log.IsErrorEnabled)
                        log.Error($"Skipping module \"{module.Name}\" — one or more declared dependencies failed to initialize");

                    allOk = false;
                    continue;
                }

                bool ok;

                try
                {
                    ok = module.Init(context);
                }
                catch (Exception e)
                {
                    if (log.IsErrorEnabled)
                        log.Error($"Module \"{module.Name}\" threw during Init", e);

                    ok = false;
                }

                if (ok)
                {
                    _initialized.Add(module.GetType());
                    _initOrder.Add(module);

                    if (log.IsInfoEnabled)
                        log.Info($"Module \"{module.Name}\" initialized");
                }
                else
                {
                    if (log.IsErrorEnabled)
                        log.Error($"Module \"{module.Name}\" reported init failure");

                    allOk = false;
                }
            }

            return allOk;
        }

        /// <summary>
        /// Shut down every module that initialized successfully, in reverse init
        /// order. Exceptions from one module do not stop the others.
        /// </summary>
        public void ShutdownAll()
        {
            for (int i = _initOrder.Count - 1; i >= 0; i--)
            {
                IGameModule module = _initOrder[i];

                try
                {
                    module.Shutdown();
                }
                catch (Exception e)
                {
                    if (log.IsErrorEnabled)
                        log.Error($"Module \"{module.Name}\" threw during Shutdown", e);
                }
            }

            _initOrder.Clear();
            _initialized.Clear();
            _initRan = false;
        }

        private bool DependenciesSatisfied(IGameModule module)
        {
            foreach (Type dep in module.DependsOn ?? Array.Empty<Type>())
            {
                if (!_initialized.Contains(dep))
                    return false;
            }

            return true;
        }

        private static List<IGameModule> Discover(IEnumerable<Assembly> assemblies)
        {
            List<IGameModule> result = new();
            HashSet<Type> seenTypes = new();

            foreach (Assembly assembly in assemblies)
            {
                Type[] types;

                try
                {
                    types = assembly.GetTypes();
                }
                catch (ReflectionTypeLoadException e)
                {
                    types = e.Types.Where(static t => t != null).ToArray();

                    if (log.IsWarnEnabled)
                        log.Warn($"Partial type load from {assembly.GetName().Name}; some modules may be missing", e);
                }

                foreach (Type type in types)
                {
                    if (type == null || !type.IsClass || type.IsAbstract)
                        continue;

                    if (!typeof(IGameModule).IsAssignableFrom(type))
                        continue;

                    GameModuleAttribute attr = type.GetCustomAttribute<GameModuleAttribute>(false);

                    if (attr == null || !attr.Enabled)
                        continue;

                    if (!seenTypes.Add(type))
                        continue;

                    try
                    {
                        IGameModule instance = (IGameModule) Activator.CreateInstance(type);
                        result.Add(instance);
                    }
                    catch (Exception e)
                    {
                        if (log.IsErrorEnabled)
                            log.Error($"Failed to instantiate module \"{type.FullName}\"", e);
                    }
                }
            }

            return result;
        }

        private static bool TopologicalSort(List<IGameModule> modules, out List<IGameModule> ordered, out string error)
        {
            ordered = new List<IGameModule>(modules.Count);
            error = null;

            Dictionary<Type, IGameModule> byType = modules.ToDictionary(static m => m.GetType());
            Dictionary<Type, int> inDegree = byType.Keys.ToDictionary(static t => t, static _ => 0);
            Dictionary<Type, List<Type>> dependents = byType.Keys.ToDictionary(static t => t, static _ => new List<Type>());

            foreach (IGameModule module in modules)
            {
                foreach (Type dep in module.DependsOn ?? Array.Empty<Type>())
                {
                    if (!byType.ContainsKey(dep))
                    {
                        // Missing dependency is not fatal at this stage — the module
                        // will still be ordered, and DependenciesSatisfied will skip
                        // it at init time. This way one disabled module doesn't
                        // crash the whole boot.
                        continue;
                    }

                    inDegree[module.GetType()]++;
                    dependents[dep].Add(module.GetType());
                }
            }

            Queue<Type> ready = new();

            foreach ((Type type, int deg) in inDegree)
            {
                if (deg == 0)
                    ready.Enqueue(type);
            }

            while (ready.Count > 0)
            {
                Type next = ready.Dequeue();
                ordered.Add(byType[next]);

                foreach (Type dependent in dependents[next])
                {
                    if (--inDegree[dependent] == 0)
                        ready.Enqueue(dependent);
                }
            }

            if (ordered.Count != modules.Count)
            {
                IEnumerable<string> cycleNames = modules
                    .Where(m => !ordered.Contains(m))
                    .Select(m => m.Name);
                error = $"dependency cycle involving: {string.Join(", ", cycleNames)}";
                return false;
            }

            return true;
        }
    }
}

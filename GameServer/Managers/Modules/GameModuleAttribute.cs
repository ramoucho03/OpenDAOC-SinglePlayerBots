using System;

namespace DOL.GS.Modules
{
    /// <summary>
    /// Marks an <see cref="IGameModule"/> implementation as discoverable by
    /// <see cref="GameModuleRegistry"/>. Without this attribute a class
    /// implementing <see cref="IGameModule"/> is ignored at scan time, which
    /// lets test doubles and abstract base classes coexist in the assembly
    /// without being booted.
    /// </summary>
    [AttributeUsage(AttributeTargets.Class, Inherited = false, AllowMultiple = false)]
    public sealed class GameModuleAttribute : Attribute
    {
        /// <summary>
        /// When false, the registry skips this module entirely. Useful for
        /// disabling a module without removing the class.
        /// </summary>
        public bool Enabled { get; set; } = true;
    }
}

using DOL.Database;

namespace DOL.GS.Modules
{
    /// <summary>
    /// Narrow view of <see cref="GameServer"/> handed to modules at init time.
    ///
    /// Modules are deliberately not given the full <c>GameServer</c> instance:
    /// keeping the surface small forces dependencies to be explicit and makes the
    /// module easier to test in isolation. Add accessors here when a real need
    /// shows up; do not widen the contract speculatively.
    /// </summary>
    public interface IGameServerContext
    {
        GameServerConfiguration Configuration { get; }
        IObjectDatabase Database { get; }
    }

    /// <summary>
    /// Default adapter that exposes the running <see cref="GameServer"/> as an
    /// <see cref="IGameServerContext"/>. Constructed by the registry; modules
    /// never see <see cref="GameServer"/> directly.
    /// </summary>
    internal sealed class GameServerContext : IGameServerContext
    {
        private readonly GameServer _server;

        public GameServerContext(GameServer server)
        {
            _server = server;
        }

        public GameServerConfiguration Configuration => _server.Configuration;
        public IObjectDatabase Database => _server.IDatabase;
    }
}

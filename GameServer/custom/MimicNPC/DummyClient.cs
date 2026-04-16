using DOL.GS;
using DOL.Network;
using DOL.Database;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Net.Sockets;
using DOL.GS.PacketHandler;

namespace DOL.GS.Scripts
{
    public class DummyClient : GameClient
    {
        public DummyClient(Socket socket) : base(socket)
        {
            Account = new DbAccount();
            Account.Language = "EN";
            Account.PrivLevel = (int)ePrivLevel.Player;
        }
    }
}

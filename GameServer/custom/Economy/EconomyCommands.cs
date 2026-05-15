using System.Threading.Tasks;
using DOL.GS.Commands;
using DOL.GS.PacketHandler;

namespace DOL.GS.Economy
{
    [CmdAttribute(
        "&economy",
        ePrivLevel.GM,
        "/economy <stats|refresh|clear|suspend|resume|topup> - Manages the dynamic auction-house economy.",
        "/economy stats - shows merchant counts and listings.",
        "/economy topup - synchronously top up to target stock.",
        "/economy refresh - rotate stock now (does not block the game loop).",
        "/economy clear - remove all bot listings.",
        "/economy suspend - pause periodic rotations.",
        "/economy resume - resume periodic rotations.")]
    public class EconomyCommand : AbstractCommandHandler, ICommandHandler
    {
        public void OnCommand(GameClient client, string[] args)
        {
            GamePlayer player = client.Player;
            if (player == null)
                return;

            if (args.Length < 2)
            {
                DisplaySyntax(client);
                return;
            }

            string sub = args[1].ToLowerInvariant();

            switch (sub)
            {
                case "stats":
                {
                    int total = EconomyManager.TotalListings;
                    var merchants = EconomyManager.Merchants;
                    player.Out.SendMessage($"Economy: initialized={EconomyManager.IsInitialized}, suspended={EconomyManager.IsSuspended}", eChatType.CT_System, eChatLoc.CL_SystemWindow);
                    player.Out.SendMessage($"Economy: target stock = {EconomyConfig.ECONOMY_TARGET_STOCK}, current listings = {total}", eChatType.CT_System, eChatLoc.CL_SystemWindow);
                    player.Out.SendMessage($"Economy: market cache total = {MarketCache.ItemCount}", eChatType.CT_System, eChatLoc.CL_SystemWindow);
                    player.Out.SendMessage($"Economy: {merchants.Count} virtual sellers across realms.", eChatType.CT_System, eChatLoc.CL_SystemWindow);
                    foreach (var m in merchants)
                        player.Out.SendMessage($"  {m.Name} (lot {m.HouseNumber}, {m.SellerRealm}): {m.ItemCount}/{EconomyConfig.ECONOMY_SELLER_CAPACITY}", eChatType.CT_System, eChatLoc.CL_SystemWindow);
                    break;
                }
                case "topup":
                {
                    player.Out.SendMessage("Economy: topping up...", eChatType.CT_System, eChatLoc.CL_SystemWindow);
                    Task.Run(() =>
                    {
                        int added = EconomyManager.ForceTopUp();
                        try { player.Out.SendMessage($"Economy: top-up complete. {added} listings added (total={EconomyManager.TotalListings}).", eChatType.CT_System, eChatLoc.CL_SystemWindow); }
                        catch { /* player may have logged out */ }
                    });
                    break;
                }
                case "refresh":
                {
                    player.Out.SendMessage("Economy: rotation kicked. Check stats in a moment.", eChatType.CT_System, eChatLoc.CL_SystemWindow);
                    // Manually rotate by scheduling a clear + top up on the worker.
                    Task.Run(() =>
                    {
                        int removed = 0;
                        var merchants = EconomyManager.Merchants;
                        int pct = System.Math.Clamp(EconomyConfig.ECONOMY_ROTATION_PERCENT, 1, 100);
                        foreach (var m in merchants)
                        {
                            int target = m.ItemCount * pct / 100;
                            for (int i = 0; i < target; i++)
                            {
                                if (m.PopRandomListing() != null)
                                    removed++;
                            }
                        }
                        int added = EconomyManager.ForceTopUp();
                        try { player.Out.SendMessage($"Economy: rotation done. Removed={removed}, added={added}, total={EconomyManager.TotalListings}.", eChatType.CT_System, eChatLoc.CL_SystemWindow); }
                        catch { }
                    });
                    break;
                }
                case "clear":
                {
                    int n = EconomyManager.ClearAll();
                    player.Out.SendMessage($"Economy: cleared {n} listings.", eChatType.CT_System, eChatLoc.CL_SystemWindow);
                    break;
                }
                case "suspend":
                    EconomyManager.Suspend(true);
                    player.Out.SendMessage("Economy: rotations suspended.", eChatType.CT_System, eChatLoc.CL_SystemWindow);
                    break;
                case "resume":
                    EconomyManager.Suspend(false);
                    player.Out.SendMessage("Economy: rotations resumed.", eChatType.CT_System, eChatLoc.CL_SystemWindow);
                    break;
                default:
                    DisplaySyntax(client);
                    break;
            }
        }
    }
}

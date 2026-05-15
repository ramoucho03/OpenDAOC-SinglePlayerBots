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
        "/economy topup - top up to target stock (background, serialized).",
        "/economy refresh - rotate a slice of stock now (background, serialized).",
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

            switch (args[1].ToLowerInvariant())
            {
                case "stats":
                {
                    int total = EconomyManager.TotalListings;
                    var merchants = EconomyManager.Merchants;
                    player.Out.SendMessage($"Economy: initialized={EconomyManager.IsInitialized}, suspended={EconomyManager.IsSuspended}", eChatType.CT_System, eChatLoc.CL_SystemWindow);
                    player.Out.SendMessage($"Economy: target stock = {EconomyConfig.ECONOMY_TARGET_STOCK}, current listings = {total}", eChatType.CT_System, eChatLoc.CL_SystemWindow);
                    int marketTotal = MarketCache.ItemCount;
                    int playerListings = System.Math.Max(0, marketTotal - total);
                    player.Out.SendMessage($"Economy: market cache total = {marketTotal} (bots={total}, players={playerListings})", eChatType.CT_System, eChatLoc.CL_SystemWindow);
                    player.Out.SendMessage($"Economy: bot-buys-from-players={EconomyConfig.ECONOMY_BOT_BUYS_FROM_PLAYERS}, max overprice={EconomyConfig.ECONOMY_MAX_OVERPRICE_PERCENT}%", eChatType.CT_System, eChatLoc.CL_SystemWindow);
                    player.Out.SendMessage($"Economy: {merchants.Count} virtual sellers across realms.", eChatType.CT_System, eChatLoc.CL_SystemWindow);
                    foreach (var m in merchants)
                        player.Out.SendMessage($"  {m.Name} (lot {m.HouseNumber}, {m.SellerRealm}): {m.ItemCount}/{GameConsignmentMerchant.CONSIGNMENT_SIZE}", eChatType.CT_System, eChatLoc.CL_SystemWindow);
                    break;
                }
                case "topup":
                    player.Out.SendMessage("Economy: topping up...", eChatType.CT_System, eChatLoc.CL_SystemWindow);
                    Task.Run(() =>
                    {
                        int added = EconomyManager.ForceTopUp();
                        try { player.Out.SendMessage($"Economy: top-up complete. {added} listings added (total={EconomyManager.TotalListings}).", eChatType.CT_System, eChatLoc.CL_SystemWindow); }
                        catch { /* player may have logged out */ }
                    });
                    break;
                case "refresh":
                    player.Out.SendMessage("Economy: rotation kicked.", eChatType.CT_System, eChatLoc.CL_SystemWindow);
                    Task.Run(() =>
                    {
                        int result = EconomyManager.ManualRotate();
                        try { player.Out.SendMessage($"Economy: rotation done. Total={EconomyManager.TotalListings} (rotated={result}).", eChatType.CT_System, eChatLoc.CL_SystemWindow); }
                        catch { }
                    });
                    break;
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

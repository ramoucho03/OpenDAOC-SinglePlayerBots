using System;
using System.Reflection;
using System.Threading.Tasks;
using DOL.GS.Commands;
using DOL.GS.PacketHandler;
using DOL.Logging;

namespace DOL.GS.Economy
{
    [CmdAttribute(
        "&economy",
        ePrivLevel.GM,
        "/economy <stats|refresh|clear|suspend|resume|topup> - Manages the dynamic auction-house economy.",
        "/economy stats - shows merchant counts and listings.",
        "/economy topup - top up to target stock (background, serialized).",
        "/economy refresh - rotate a slice of stock now (background, serialized).",
        "/economy clear confirm - remove all bot listings (requires the 'confirm' keyword).",
        "/economy suspend - pause periodic rotations.",
        "/economy resume - resume periodic rotations.")]
    public class EconomyCommand : AbstractCommandHandler, ICommandHandler
    {
        private static readonly Logger log = LoggerManager.Create(MethodBase.GetCurrentMethod().DeclaringType);

        // Wraps a background command so an exception in ForceTopUp / ManualRotate is
        // logged (instead of becoming an unobserved task exception) and the operator
        // sees a clear failure message when still online. Without this, a thrown
        // exception was silently swallowed and the player just never received the
        // completion message.
        private static void RunBackgroundCommand(GamePlayer player, string label, Func<string> work)
        {
            Task.Run(() =>
            {
                string result;
                try { result = work(); }
                catch (Exception ex)
                {
                    log.Error($"Economy {label} failed.", ex);
                    if (player?.Client?.IsPlaying == true)
                    {
                        try { player.Out.SendMessage($"Economy: {label} failed: {ex.Message}", eChatType.CT_Important, eChatLoc.CL_SystemWindow); }
                        catch { /* player may have logged out mid-send */ }
                    }
                    return;
                }

                if (player?.Client?.IsPlaying == true)
                {
                    try { player.Out.SendMessage(result, eChatType.CT_System, eChatLoc.CL_SystemWindow); }
                    catch { /* player may have logged out mid-send */ }
                }
            });
        }

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
                    if (!EconomyManager.IsInitialized)
                    {
                        player.Out.SendMessage("Economy: not initialized (disabled, market off, or no eligible templates).", eChatType.CT_System, eChatLoc.CL_SystemWindow);
                        break;
                    }
                    int total = EconomyManager.TotalListings;
                    var merchants = EconomyManager.Merchants;
                    int marketTotal = MarketCache.ItemCount;
                    int playerListings = Math.Max(0, marketTotal - total);
                    player.Out.SendMessage($"Economy: initialized={EconomyManager.IsInitialized}, suspended={EconomyManager.IsSuspended}", eChatType.CT_System, eChatLoc.CL_SystemWindow);
                    player.Out.SendMessage($"Economy: target stock = {EconomyConfig.ECONOMY_TARGET_STOCK}, current listings = {total}", eChatType.CT_System, eChatLoc.CL_SystemWindow);
                    player.Out.SendMessage($"Economy: market cache total = {marketTotal} (bots={total}, players={playerListings})", eChatType.CT_System, eChatLoc.CL_SystemWindow);
                    player.Out.SendMessage($"Economy: template pool = {EconomyItemPool.TotalTemplates}", eChatType.CT_System, eChatLoc.CL_SystemWindow);
                    player.Out.SendMessage($"Economy: tick={EconomyConfig.ECONOMY_TICK_SECONDS}s, turnover={EconomyConfig.ECONOMY_TURNOVER_PERCENT_PER_HOUR}%/h", eChatType.CT_System, eChatLoc.CL_SystemWindow);
                    player.Out.SendMessage($"Economy: bot-buys-from-players={EconomyConfig.ECONOMY_BOT_BUYS_FROM_PLAYERS}, fair-time={EconomyConfig.ECONOMY_FAIR_PRICE_BASE_HOURS}h, hard-ceiling={EconomyConfig.ECONOMY_HARD_MAX_OVERPRICE_PERCENT}%", eChatType.CT_System, eChatLoc.CL_SystemWindow);
                    player.Out.SendMessage($"Economy: persist={EconomyConfig.ECONOMY_PERSIST}, flush={EconomyConfig.ECONOMY_DB_FLUSH_SECONDS}s", eChatType.CT_System, eChatLoc.CL_SystemWindow);
                    player.Out.SendMessage($"Economy: {merchants.Count} virtual sellers across realms.", eChatType.CT_System, eChatLoc.CL_SystemWindow);
                    foreach (var m in merchants)
                        player.Out.SendMessage($"  {m.Name} (lot {m.HouseNumber}, {m.SellerRealm}): {m.ItemCount}/{GameConsignmentMerchant.CONSIGNMENT_SIZE}", eChatType.CT_System, eChatLoc.CL_SystemWindow);
                    break;
                }
                case "topup":
                    player.Out.SendMessage("Economy: topping up...", eChatType.CT_System, eChatLoc.CL_SystemWindow);
                    RunBackgroundCommand(player, "top-up", () =>
                    {
                        int added = EconomyManager.ForceTopUp();
                        return $"Economy: top-up complete. {added} listings added (total={EconomyManager.TotalListings}).";
                    });
                    break;
                case "refresh":
                    player.Out.SendMessage("Economy: rotation kicked.", eChatType.CT_System, eChatLoc.CL_SystemWindow);
                    RunBackgroundCommand(player, "refresh", () =>
                    {
                        int rotated = EconomyManager.ManualRotate();
                        return $"Economy: rotation done. Total={EconomyManager.TotalListings} (rotated={rotated}).";
                    });
                    break;
                case "clear":
                {
                    // Two-step confirmation: typing just '/economy clear' shows a
                    // preview with the count and requires '/economy clear confirm'
                    // to actually wipe. Prevents one-keystroke disasters.
                    bool confirmed = args.Length >= 3 && args[2].Equals("confirm", StringComparison.OrdinalIgnoreCase);

                    if (!confirmed)
                    {
                        int n = EconomyManager.TotalListings;
                        player.Out.SendMessage($"Economy: '/economy clear' will WIPE {n} bot listings.", eChatType.CT_Important, eChatLoc.CL_SystemWindow);
                        player.Out.SendMessage("Type '/economy clear confirm' within a few seconds to proceed.", eChatType.CT_Important, eChatLoc.CL_SystemWindow);
                        break;
                    }

                    int cleared = EconomyManager.ClearAll();
                    log.Warn($"Economy: /economy clear confirmed by {player.Name} (acct={player.Client?.Account?.Name}, plvl={player.Client?.Account?.PrivLevel}). Wiped {cleared} listings.");
                    player.Out.SendMessage($"Economy: cleared {cleared} listings.", eChatType.CT_System, eChatLoc.CL_SystemWindow);
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

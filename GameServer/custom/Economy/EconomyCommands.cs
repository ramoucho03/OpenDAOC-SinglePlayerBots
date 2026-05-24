using System;
using System.Reflection;
using System.Threading.Tasks;
using DOL.Database;
using DOL.GS.Commands;
using DOL.GS.PacketHandler;
using DOL.Logging;

namespace DOL.GS.Economy
{
    [CmdAttribute(
        "&economy",
        ePrivLevel.GM,
        "/economy <stats|refresh|clear|suspend|resume|topup|price|recompute|rebuild-pool|velocity> - Manages the dynamic auction-house economy.",
        "/economy stats - shows merchant counts and listings.",
        "/economy topup - top up to target stock (background, serialized).",
        "/economy refresh - rotate a slice of stock now (background, serialized).",
        "/economy clear confirm - remove all bot listings (requires the 'confirm' keyword).",
        "/economy suspend - pause periodic rotations.",
        "/economy resume - resume periodic rotations.",
        "/economy price <Id_nb> - preview the price the formula computes for a template.",
        "/economy recompute - recalculate SellPrice for every live listing using the current formula.",
        "/economy rebuild-pool - drop and reload the item-template pool (after a content push).",
        "/economy velocity - dump demand-tracker stats (recently bought templates).")]
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
                case "price":
                {
                    if (args.Length < 3)
                    {
                        player.Out.SendMessage("Usage: /economy price <Id_nb>", eChatType.CT_System, eChatLoc.CL_SystemWindow);
                        break;
                    }
                    string idNb = args[2];
                    DbItemTemplate tpl = GameServer.Database.FindObjectByKey<DbItemTemplate>(idNb);
                    if (tpl == null)
                    {
                        player.Out.SendMessage($"Economy: template '{idNb}' not found.", eChatType.CT_System, eChatLoc.CL_SystemWindow);
                        break;
                    }
                    int fair = EconomyPricing.ComputeFairValue(tpl);
                    double demand = EconomyManager.GetDemandMultiplier(idNb);
                    double utility = EconomyPricing.ComputeUtility(tpl);
                    int sampleSell = EconomyPricing.ComputeSellPrice(tpl);
                    player.Out.SendMessage($"Economy: {tpl.Id_nb} ({tpl.Name})", eChatType.CT_System, eChatLoc.CL_SystemWindow);
                    player.Out.SendMessage($"  level={tpl.Level} quality={tpl.Quality} bonus(SC)={tpl.Bonus} utility={utility:F1}", eChatType.CT_System, eChatLoc.CL_SystemWindow);
                    player.Out.SendMessage($"  DPS_AF={tpl.DPS_AF} SPD_ABS={tpl.SPD_ABS} proc={tpl.ProcSpellID}/{tpl.ProcSpellID1} charge={tpl.SpellID}/{tpl.SpellID1}", eChatType.CT_System, eChatLoc.CL_SystemWindow);
                    player.Out.SendMessage($"  fair value = {FormatCopper(fair)}", eChatType.CT_System, eChatLoc.CL_SystemWindow);
                    player.Out.SendMessage($"  demand multiplier = {demand:F2}× (range {EconomyConfig.ECONOMY_DEMAND_MIN_MARKDOWN_PERCENT}–{EconomyConfig.ECONOMY_DEMAND_MAX_MARKUP_PERCENT}%)", eChatType.CT_System, eChatLoc.CL_SystemWindow);
                    player.Out.SendMessage($"  sample listing price = {FormatCopper(sampleSell)} (rolled, includes random {EconomyConfig.ECONOMY_PRICE_MIN_MULTIPLIER}–{EconomyConfig.ECONOMY_PRICE_MAX_MULTIPLIER}%)", eChatType.CT_System, eChatLoc.CL_SystemWindow);
                    break;
                }
                case "recompute":
                {
                    if (!EconomyManager.IsInitialized)
                    {
                        player.Out.SendMessage("Economy: not initialized.", eChatType.CT_System, eChatLoc.CL_SystemWindow);
                        break;
                    }
                    player.Out.SendMessage("Economy: recomputing all listing prices...", eChatType.CT_System, eChatLoc.CL_SystemWindow);
                    RunBackgroundCommand(player, "recompute", () =>
                    {
                        int n = EconomyManager.RecomputeAllListings();
                        return $"Economy: recompute done. {n} listings updated.";
                    });
                    break;
                }
                case "rebuild-pool":
                case "rebuildpool":
                {
                    player.Out.SendMessage("Economy: rebuilding template pool...", eChatType.CT_System, eChatLoc.CL_SystemWindow);
                    RunBackgroundCommand(player, "rebuild-pool", () =>
                    {
                        int n = EconomyManager.RebuildItemPool();
                        return $"Economy: pool rebuilt. {n} eligible templates loaded.";
                    });
                    break;
                }
                case "velocity":
                {
                    var (n, total) = EconomyManager.GetDemandStats();
                    player.Out.SendMessage($"Economy: demand tracking enabled={EconomyConfig.ECONOMY_DEMAND_TRACKING_ENABLED}, half-life={EconomyConfig.ECONOMY_DEMAND_DECAY_MINUTES}min.", eChatType.CT_System, eChatLoc.CL_SystemWindow);
                    player.Out.SendMessage($"Economy: {n} templates with non-trivial demand score (sum={total:F1}).", eChatType.CT_System, eChatLoc.CL_SystemWindow);
                    player.Out.SendMessage($"Economy: bounds: markdown floor={EconomyConfig.ECONOMY_DEMAND_MIN_MARKDOWN_PERCENT}%, markup ceiling={EconomyConfig.ECONOMY_DEMAND_MAX_MARKUP_PERCENT}%.", eChatType.CT_System, eChatLoc.CL_SystemWindow);
                    break;
                }
                default:
                    DisplaySyntax(client);
                    break;
            }
        }

        /// <summary>
        /// Formats a copper amount as plat/gold/silver/copper for at-a-glance reads.
        /// 10_000_000 copper = 10 platines. Always shows non-zero leading denomination.
        /// </summary>
        private static string FormatCopper(long copper)
        {
            if (copper <= 0) return "0c";
            long c = copper;
            long plat = c / 10_000_000L;       c %= 10_000_000L;
            long gold = c / 10_000L;           c %= 10_000L;
            long silver = c / 100L;            c %= 100L;
            var parts = new System.Text.StringBuilder();
            if (plat   > 0) parts.Append(plat).Append("p ");
            if (gold   > 0) parts.Append(gold).Append("g ");
            if (silver > 0) parts.Append(silver).Append("s ");
            if (c      > 0 || parts.Length == 0) parts.Append(c).Append("c");
            return parts.ToString().TrimEnd();
        }
    }
}

using System.Collections.Generic;
using System.Threading;
using ScalpFeedVendor.Feed;
using ScalpFeedVendor.Ibkr;
using ScalpFeedVendor.Tastytrade;
using TradingPlatform.BusinessLayer;
using TradingPlatform.BusinessLayer.Integration;

namespace ScalpFeedVendor;

/// <summary>
/// The one connection Quantower sees. tastytrade fills MultiVendor's symbol, quote and history
/// slots and supplies the tape and chart history. IBKR supplies bid, ask, depth and the tape's
/// other venues through the same quote slot, and paper trading through the account and order
/// methods below; IBKR-SEAMS.md lists where each piece plugs in and what must be true before
/// real orders go through it.
/// </summary>
public sealed class ScalpFeedVendor : MultiVendor
{
    public const string VendorName = "ScalpFeed";
    private const string TastytradeKey = "tasty";
    private const string HotkeyHeader = "ScalpFeed hotkey";

    /// <summary>
    /// The order hotkeys (OrderHotkeys): Alt+1 buys 1,000 shares, Alt+2 2,000 and Alt+0 1, at the ask plus 2.5%;
    /// Shift+Z sells the whole position and Shift+X half of it, at the bid minus 2.5%.
    /// </summary>
    private static readonly IReadOnlyList<OrderHotkey> Hotkeys =
        [OrderHotkey.BuyAsk, OrderHotkey.BuyTwoThousand, OrderHotkey.SellAll, OrderHotkey.SellHalf, OrderHotkey.BuyOne];

    private TastytradeMarketVendor marketData;
    private PaperTrading trading;
    private Action<OrderHotkey> onHotkey;

    public static VendorMetaData GetVendorMetaData() => new()
    {
        VendorName = VendorName,
        VendorDescription = "tastytrade tape and charts, IBKR bid, ask and depth, and IBKR paper trading.",
        GetDefaultConnections = () => new List<ConnectionInfo>
        {
            CreateDefaultConnectionInfo(typeof(ScalpFeedVendor).Assembly, VendorName, VendorName, "scalpfeed.svg", null, true)
        },
        GetConnectionParameters = () => new List<SettingItem>()
    };

    public override ConnectionResult Connect(ConnectRequestParameters connectRequestParameters)
    {
        if (this.marketData == null)
        {
            this.marketData = new TastytradeMarketVendor();
            ((IVendor)this.marketData).Key = TastytradeKey;
            this.RegisterSymbolVendor(this.marketData);
            this.RegisterQuoteVendor(this.marketData);
            this.RegisterHistoryVendor(this.marketData);
        }

        var result = base.Connect(connectRequestParameters);
        if (this.trading == null)
        {
            this.trading = new PaperTrading(this.PushMessage, TastytradeKey);
            this.trading.Start();
        }

        this.onHotkey ??= this.OnOrderHotkey;
        HotkeyListener.Shared.Start(Hotkeys, this.onHotkey);
        return result;
    }

    public override void Disconnect()
    {
        if (this.onHotkey != null)
            HotkeyListener.Shared.Release(this.onHotkey);
        this.trading?.Stop();
        this.trading = null;
        base.Disconnect();
    }

    /// <summary>An order hotkey was pressed with Quantower in front: its order goes, or a warning sound says why not.</summary>
    private void OnOrderHotkey(OrderHotkey hotkey)
    {
        var market = this.marketData;
        var paper = this.trading;
        // Each depth symbol with the paper account's position and working orders, for a position key.
        List<HotkeyQuote> Depth() => market?.DepthQuotes().Select(q =>
        {
            var (position, buys, sells) = paper?.Exposure(q.Symbol) ?? (0, 0, 0);
            return q with { Position = position, WorkingBuys = buys, WorkingSells = sells };
        }).ToList();

        var depth = Depth();
        // A position key cancels the stock's working orders on the other side first, or IBKR reprices its order away
        // from them and an IOC dies unfilled (LVWR order 394, 2026-11-19); then it looks again, for the position
        // with their fills and the quote now.
        if (hotkey.OfPosition > 0 && paper != null && depth is [var only])
        {
            string cancelled = paper.CancelBeforeHotkey(only.Symbol, hotkey.Buy);
            if (cancelled != null)
            {
                Log.Info($"[hotkey] {hotkey.Keys}: {cancelled}");
                depth = Depth();
            }
        }

        var (ticket, quote, problem) = depth == null
            ? (null, double.NaN, "market data isn't connected")
            : OrderHotkeys.Decide(hotkey, depth);

        int id = 0;
        if (problem == null)
            (id, problem) = paper == null ? (0, "trading isn't connected") : paper.PlaceFromHotkey(ticket);

        if (problem == null)
        {
            string what = $"{(ticket.Buy ? "buy" : "sell")} {ticket.Quantity:#,0} {ticket.Symbol} {ticket.Tif} limit {ticket.LimitPrice} ({(ticket.Buy ? "ask" : "bid")} {quote} {(ticket.Buy ? "+" : "-")} {hotkey.Offset:0.##%}, at most {OrderHotkey.MaxOffset:0.00})";
            // Logged only: a pop-up for every order sent was noise (your request, 2026-11-19). One that sent
            // nothing still pops up, with a sound.
            Log.Info($"[hotkey] {hotkey.Keys}: order {id}, {what}");
        }
        else
        {
            Log.Info($"[hotkey] {hotkey.Keys}: nothing sent, {problem}");
            FeedAlarm.PlayWarning();
            this.PushMessage(MessageDealTicket.CreateRefuseDealTicket(HotkeyHeader, $"{hotkey.Keys}: nothing sent, {problem}."));
        }
    }

    public override IList<MessageAccount> GetAccounts(CancellationToken token) => this.trading?.GetAccounts() ?? new List<MessageAccount>();

    public override IList<MessageRule> GetRules(CancellationToken token) => this.trading?.GetRules() ?? new List<MessageRule>();

    public override IList<OrderType> GetAllowedOrderTypes(CancellationToken token) => this.trading?.GetAllowedOrderTypes() ?? new List<OrderType>();

    public override IList<MessageOpenOrder> GetPendingOrders(CancellationToken token) => this.trading?.GetPendingOrders() ?? new List<MessageOpenOrder>();

    public override IList<MessageOpenPosition> GetPositions(CancellationToken token) => this.trading?.GetPositions() ?? new List<MessageOpenPosition>();

    public override IList<MessageTrade> GetTrades(TradesHistoryRequestParameters parameters) => this.trading?.GetTrades(parameters) ?? new List<MessageTrade>();

    /// <summary>
    /// Says the connection has a trade history to ask for (IBKR's fills, kept by IbkrTrader), so panels that ask for
    /// one, like Account performance, do; Quantower isn't to keep its own copy, as IBKR's is the record.
    /// </summary>
    public override TradesHistoryMetadata GetTradesMetadata() => new() { AllowLocalStorage = false };

    public override TradingOperationResult PlaceOrder(PlaceOrderRequestParameters parameters) =>
        this.trading?.PlaceOrder(parameters) ?? TradingOperationResult.CreateError(parameters.RequestId, "not connected");

    public override TradingOperationResult ModifyOrder(ModifyOrderRequestParameters parameters) =>
        this.trading?.ModifyOrder(parameters) ?? TradingOperationResult.CreateError(parameters.RequestId, "not connected");

    public override TradingOperationResult CancelOrder(CancelOrderRequestParameters parameters) =>
        this.trading?.CancelOrder(parameters) ?? TradingOperationResult.CreateError(parameters.RequestId, "not connected");

    public override TradingOperationResult ClosePosition(ClosePositionRequestParameters parameters) =>
        this.trading?.ClosePosition(parameters) ?? TradingOperationResult.CreateError(parameters.RequestId, "not connected");
}

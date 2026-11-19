using System.Collections.Concurrent;
using System.Globalization;
using ScalpFeedVendor.Feed;
using TradingPlatform.BusinessLayer;
using TradingPlatform.BusinessLayer.Integration;

namespace ScalpFeedVendor.Ibkr;

/// <summary>
/// IBKR paper trading as Quantower sees it: the account, positions, working orders and fills, and the
/// place, modify, cancel and close requests that come back. The orders themselves go through
/// IbkrTrader, which refuses anything but a paper account.
/// </summary>
internal sealed class PaperTrading : ITraderSink
{
    private const string Header = "ScalpFeed paper";
    private const string Usd = "USD";
    private static readonly TimeSpan WaitForReady = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan CommissionWait = TimeSpan.FromSeconds(3);
    private static readonly TimeZoneInfo Eastern = TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");

    private static readonly Dictionary<string, string> ToIbkrType = new()
    {
        [OrderType.Market] = "MKT",
        [OrderType.Limit] = "LMT",
        [OrderType.Stop] = "STP",
        [OrderType.StopLimit] = "STP LMT",
    };

    private readonly Action<Message> push;
    private readonly string vendorKey;
    private readonly ConcurrentDictionary<string, DateTime> positionOpened = new();
    private readonly ConcurrentDictionary<int, bool> historyStarted = new();
    private readonly ConcurrentDictionary<string, bool> tradesPushed = new();
    private IbkrTrader trader;

    public PaperTrading(Action<Message> push, string vendorKey)
    {
        this.push = push;
        this.vendorKey = vendorKey;
    }

    public void Start() => this.trader = TraderSession.Attach(this);

    public void Stop() => TraderSession.Detach(this);

    #region Quantower asks

    public IList<MessageAccount> GetAccounts()
    {
        var account = this.WaitForAccount();
        return account == null ? [] : [this.AccountMessage(account, this.trader.AccountValues(account))];
    }

    public IList<MessageRule> GetRules() =>
    [
        new() { Name = Rule.ALLOW_TRADING, Value = true },
        new() { Name = Rule.ALLOW_SL, Value = false },
        new() { Name = Rule.ALLOW_TP, Value = false },
        new() { Name = Rule.ALLOW_MODIFY_TIF, Value = false },
        new() { Name = Rule.ALLOW_MODIFY_ORDER_TYPE, Value = false },
        new() { Name = Rule.PLACE_ORDER_TRADING_OPERATION_HAS_ORDER_ID, Value = true },
    ];

    public IList<OrderType> GetAllowedOrderTypes() =>
    [
        new LimitOrderType(TimeInForce.Day, TimeInForce.GTC, TimeInForce.IOC),
        new MarketOrderType(TimeInForce.Day, TimeInForce.IOC),
        new StopOrderType(TimeInForce.Day, TimeInForce.GTC),
        new StopLimitOrderType(TimeInForce.Day, TimeInForce.GTC),
    ];

    public IList<MessageOpenOrder> GetPendingOrders() =>
        this.trader?.OpenOrders().Select(this.OrderMessage).ToList() ?? [];

    public IList<MessageOpenPosition> GetPositions() =>
        this.trader?.Positions().Select(this.PositionMessage).ToList() ?? [];

    public IList<MessageTrade> GetTrades() =>
        this.trader?.Fills().Select(this.TradeMessage).ToList() ?? [];

    public TradingOperationResult PlaceOrder(PlaceOrderRequestParameters p)
    {
        if (this.trader == null)
            return TradingOperationResult.CreateError(p.RequestId, "IBKR is turned off in ibkr.json");
        if (p.Account?.Id != this.trader.Account)
            return TradingOperationResult.CreateError(p.RequestId, $"orders go to the IBKR paper account {this.trader.Account ?? "(not connected)"}, not {p.Account?.Id}");
        if (!ToIbkrType.TryGetValue(p.OrderTypeId ?? "", out var type))
            return TradingOperationResult.CreateError(p.RequestId, $"order type {p.OrderTypeId} isn't supported");

        var ticket = new IbkrOrderTicket(TradingSymbols.ToIbkr(p.Symbol.Id), p.Side == Side.Buy, p.Quantity, type, p.Price, p.TriggerPrice, ToIbkrTif(p.TimeInForce));
        var (id, error) = this.trader.Place(ticket);
        Log.Info(error == null
            ? $"[trade] placed order {id}: {(ticket.Buy ? "buy" : "sell")} {ticket.Quantity} {ticket.Symbol} {ticket.Type} {Describe(ticket)} {ticket.Tif}"
            : $"[trade] order refused before sending: {error}");
        return error == null
            ? TradingOperationResult.CreateSuccess(p.RequestId, id.ToString(CultureInfo.InvariantCulture))
            : TradingOperationResult.CreateError(p.RequestId, error);
    }

    /// <summary>An order hotkey's order (OrderHotkeys), sent like one from the order entry, to the paper account only.</summary>
    public (int Id, string Error) PlaceFromHotkey(IbkrOrderTicket ticket) =>
        this.trader == null ? (0, "IBKR is turned off in ibkr.json") : this.trader.Place(ticket);

    /// <summary>
    /// For a position hotkey: the paper account's position in an IBKR symbol (negative when short), as IBKR's
    /// position stream last said, and the shares still to fill in its working buy and sell orders.
    /// </summary>
    public (double Position, double WorkingBuys, double WorkingSells) Exposure(string ibkrSymbol)
    {
        if (this.trader == null)
            return (0, 0, 0);

        double position = this.trader.Positions().Where(p => p.Symbol == ibkrSymbol).Sum(p => p.Quantity);
        var working = this.trader.OpenOrders().Where(o => o.Symbol == ibkrSymbol).ToList();
        return (position,
            working.Where(o => o.Buy).Sum(o => Math.Max(0, o.Quantity - o.Filled)),
            working.Where(o => !o.Buy).Sum(o => Math.Max(0, o.Quantity - o.Filled)));
    }

    public TradingOperationResult ModifyOrder(ModifyOrderRequestParameters p)
    {
        if (this.trader == null || !int.TryParse(p.OrderId, NumberStyles.Integer, CultureInfo.InvariantCulture, out int id))
            return TradingOperationResult.CreateError(p.RequestId, $"order {p.OrderId} isn't an IBKR order");

        string error = this.trader.Modify(id, p.Quantity, p.Price, p.TriggerPrice);
        Log.Info(error == null ? $"[trade] modify order {id}: {p.Quantity} at {p.Price}/{p.TriggerPrice}" : $"[trade] modify order {id} refused: {error}");
        return error == null
            ? TradingOperationResult.CreateSuccess(p.RequestId, p.OrderId)
            : TradingOperationResult.CreateError(p.RequestId, error);
    }

    public TradingOperationResult CancelOrder(CancelOrderRequestParameters p)
    {
        string orderId = p.Order?.Id;
        if (this.trader == null || !int.TryParse(orderId, NumberStyles.Integer, CultureInfo.InvariantCulture, out int id))
            return TradingOperationResult.CreateError(p.RequestId, $"order {orderId} isn't an IBKR order");

        string error = this.trader.Cancel(id);
        Log.Info(error == null ? $"[trade] cancel order {id}" : $"[trade] cancel order {id} refused: {error}");
        return error == null
            ? TradingOperationResult.CreateSuccess(p.RequestId, orderId)
            : TradingOperationResult.CreateError(p.RequestId, error);
    }

    /// <summary>
    /// A market order the other way, in regular hours only: IBKR doesn't fill market orders in premarket
    /// or after hours, so outside them a position closes with a limit order like any other.
    /// </summary>
    public TradingOperationResult ClosePosition(ClosePositionRequestParameters p)
    {
        var position = p.Position;
        if (this.trader == null || position == null)
            return TradingOperationResult.CreateError(p.RequestId, "no IBKR position to close");
        if (!InRegularHours(DateTime.UtcNow))
            return TradingOperationResult.CreateError(p.RequestId, "outside regular hours IBKR won't fill a market order: close with a limit order");

        double quantity = p.CloseQuantity > 0 ? p.CloseQuantity : position.Quantity;
        var ticket = new IbkrOrderTicket(TradingSymbols.ToIbkr(position.Symbol.Id), position.Side != Side.Buy, quantity, "MKT", double.NaN, double.NaN, "DAY");
        var (id, error) = this.trader.Place(ticket);
        Log.Info(error == null ? $"[trade] closing {position.Symbol.Id} with market order {id} for {quantity}" : $"[trade] close refused: {error}");
        return error == null
            ? TradingOperationResult.CreateSuccess(p.RequestId, id.ToString(CultureInfo.InvariantCulture))
            : TradingOperationResult.CreateError(p.RequestId, error);
    }

    #endregion Quantower asks

    #region IBKR says

    public void OnTraderState(bool ready, string detail)
    {
        if (ready)
        {
            var account = this.trader?.Account;
            if (account != null)
                this.push(this.AccountMessage(account, this.trader.AccountValues(account)));
            return;
        }

        this.push(MessageDealTicket.CreateRefuseDealTicket(Header, $"PAPER TRADING DOWN: {detail}. Working orders stay at IBKR."));
    }

    public void OnTraderAccount(string account, IReadOnlyDictionary<string, string> values) =>
        this.push(this.AccountMessage(account, values));

    public void OnTraderPosition(IbkrPosition position)
    {
        string id = PositionId(position);
        if (position.Quantity == 0)
        {
            this.positionOpened.TryRemove(id, out _);
            this.push(new MessageClosePosition { PositionId = id });
            return;
        }

        this.push(this.PositionMessage(position));
    }

    public void OnTraderOrder(IbkrOrder order)
    {
        var message = this.OrderMessage(order);
        if (this.historyStarted.TryAdd(order.Id, true))
            this.push(new MessageOrderHistory(this.OrderMessage(order)));

        if (!order.IsDone)
        {
            this.push(message);
            return;
        }

        this.push(new MessageCloseOrder { OrderId = message.OrderId });
        this.push(new MessageOrderHistory(message));
    }

    public void OnTraderFill(IbkrFill fill)
    {
        // The commission comes in a message of its own just after; wait a moment for it, but not forever.
        if (double.IsNaN(fill.Commission))
        {
            _ = Task.Delay(CommissionWait).ContinueWith(_ => this.PushTradeOnce(fill), TaskScheduler.Default);
            return;
        }

        this.PushTradeOnce(fill);
    }

    /// <summary>Each fill goes to Quantower once: with its commission if that came in time, else without.</summary>
    private void PushTradeOnce(IbkrFill fill)
    {
        if (!this.tradesPushed.TryAdd(fill.ExecId, true))
            return;

        var latest = this.trader?.Fills().FirstOrDefault(f => f.ExecId == fill.ExecId) ?? fill;
        this.push(this.TradeMessage(latest));
    }

    /// <summary>
    /// Only what ended an order pops up, and not a cancel (202), which an IOC's unfilled rest always gets: every order
    /// popped up IBKR's 2109 warning (outside regular hours is ignored) and every IOC a 202 (your request, 2026-11-19:
    /// no extra notifications on a hotkey order). IbkrTrader has logged each already.
    /// </summary>
    public void OnTraderOrderMessage(int orderId, int code, string text, bool endedOrder)
    {
        if (endedOrder && code != 202)
            this.push(MessageDealTicket.CreateRefuseDealTicket(Header, $"IBKR order {orderId}: {text} ({code})"));
    }

    #endregion IBKR says

    #region Messages

    private string SymbolId(string ibkrSymbol) => TradingSymbols.FromIbkr(ibkrSymbol, this.vendorKey);

    private static string PositionId(IbkrPosition p) => p.Account + ":" + p.Symbol;

    private static string Describe(IbkrOrderTicket t) => t.Type switch
    {
        "LMT" => $"at {t.LimitPrice}",
        "STP" => $"stop {t.StopPrice}",
        "STP LMT" => $"stop {t.StopPrice} limit {t.LimitPrice}",
        _ => "",
    };

    private static string ToIbkrTif(TimeInForce tif) => tif switch
    {
        TimeInForce.GTC => "GTC",
        TimeInForce.IOC => "IOC",
        _ => "DAY",
    };

    private static TimeInForce ToTif(string tif) => tif switch
    {
        "GTC" => TimeInForce.GTC,
        "IOC" => TimeInForce.IOC,
        _ => TimeInForce.Day,
    };

    private static string ToQuantowerType(string type) => type switch
    {
        "MKT" => OrderType.Market,
        "STP" => OrderType.Stop,
        "STP LMT" => OrderType.StopLimit,
        _ => OrderType.Limit,
    };

    private static OrderStatus ToStatus(IbkrOrder o) => o.Status switch
    {
        "Filled" => OrderStatus.Filled,
        "Cancelled" or "ApiCancelled" or IbkrTrader.GoneStatus => OrderStatus.Cancelled,
        "Inactive" => OrderStatus.Refused,
        _ => o.Filled > 0 ? OrderStatus.PartiallyFilled : OrderStatus.Opened,
    };

    private static bool InRegularHours(DateTime utc)
    {
        var et = TimeZoneInfo.ConvertTimeFromUtc(utc, Eastern);
        return et.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday)
            && et.TimeOfDay >= new TimeSpan(9, 30, 0) && et.TimeOfDay < new TimeSpan(16, 0, 0);
    }

    private static double Value(IReadOnlyDictionary<string, string> values, string tag) =>
        values.TryGetValue(tag, out var text) && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double value) ? value : 0;

    private MessageTrade TradeMessage(IbkrFill f) => new()
    {
        TradeId = f.ExecId,
        SymbolId = this.SymbolId(f.Symbol),
        AccountId = f.Account,
        OrderId = f.OrderId.ToString(CultureInfo.InvariantCulture),
        DateTime = f.TimeUtc,
        Side = f.Buy ? Side.Buy : Side.Sell,
        Price = f.Price,
        Quantity = f.Shares,
        Fee = new PnLItem { AssetID = Usd, Value = double.IsNaN(f.Commission) ? 0 : -f.Commission },
    };

    private string WaitForAccount()
    {
        if (this.trader == null)
            return null;

        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (!this.trader.Ready && clock.Elapsed < WaitForReady)
            Thread.Sleep(50);

        if (!this.trader.Ready)
            Log.Info($"[trade] no paper account yet: {this.trader.NotReadyWhy}");
        return this.trader.Account;
    }

    private MessageAccount AccountMessage(string account, IReadOnlyDictionary<string, string> values) => new()
    {
        AccountId = account,
        AccountName = $"{account} (IBKR paper)",
        Balance = Value(values, "NetLiquidation"),
        NettingType = NettingType.OnePosition,
        AccountAdditionalInfo =
        [
            Info("cash", "Cash", Value(values, "TotalCashValue"), 10),
            Info("buyingPower", "Buying power", Value(values, "BuyingPower"), 20),
            Info("availableFunds", "Available funds", Value(values, "AvailableFunds"), 30),
            Info("excessLiquidity", "Excess liquidity", Value(values, "ExcessLiquidity"), 40),
            Info("dayTradesLeft", "Day trades left", Value(values, "DayTradesRemaining"), 50),
        ],
    };

    private static AdditionalInfoItem Info(string id, string name, double value, int sortIndex) => new()
    {
        Id = id,
        NameKey = name,
        DataType = ComparingType.Double,
        SortIndex = sortIndex,
        Value = value,
    };

    private MessageOpenPosition PositionMessage(IbkrPosition p)
    {
        string id = PositionId(p);
        return new MessageOpenPosition(this.SymbolId(p.Symbol))
        {
            PositionId = id,
            AccountId = p.Account,
            Side = p.Quantity > 0 ? Side.Buy : Side.Sell,
            Quantity = Math.Abs(p.Quantity),
            OpenPrice = p.AvgCost,
            OpenTime = this.positionOpened.GetOrAdd(id, _ => DateTime.UtcNow),
        };
    }

    private MessageOpenOrder OrderMessage(IbkrOrder o)
    {
        var message = new MessageOpenOrder(this.SymbolId(o.Symbol))
        {
            OrderId = o.Id.ToString(CultureInfo.InvariantCulture),
            AccountId = o.Account,
            OrderTypeId = ToQuantowerType(o.Type),
            Side = o.Buy ? Side.Buy : Side.Sell,
            TotalQuantity = o.Quantity,
            FilledQuantity = o.Filled,
            TimeInForce = ToTif(o.Tif),
            Status = ToStatus(o),
            LastUpdateTime = o.UpdatedUtc,
        };

        // A finished order with fills shows its average fill as its price (IbkrOrder.ShownPrice), and its limit, if it
        // had one, in its comment.
        double shown = o.ShownPrice;
        if (!double.IsNaN(shown))
            message.Price = shown;
        if (shown != o.LimitPrice && !double.IsNaN(o.LimitPrice))
            message.Comment = $"limit {o.LimitPrice.ToString(CultureInfo.InvariantCulture)}";
        if (!double.IsNaN(o.StopPrice))
            message.TriggerPrice = o.StopPrice;
        if (o.Filled > 0)
            message.AverageFillPrice = o.AvgFillPrice;
        return message;
    }

    #endregion Messages
}

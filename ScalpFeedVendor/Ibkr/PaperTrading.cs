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

    // Each position's quantity before the fills IBKR gave, worked out once from its positions and those fills, so a
    // trade can say whether it opened or closed. A position first traded after that starts flat.
    private Dictionary<string, double> startQuantity;
    private readonly object startGate = new();
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

    /// <summary>
    /// The fills IBKR has given (today's and those since), as trades, those in the range asked for and of the symbols
    /// asked for when there are any: Account performance asks for its period ("Today") and showed nothing while the
    /// Trades list had them all (your report, 2026-11-19).
    /// </summary>
    public IList<MessageTrade> GetTrades(TradesHistoryRequestParameters parameters = null)
    {
        var fills = this.trader?.Fills() ?? [];
        var symbols = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrEmpty(parameters?.SymbolId))
            symbols.Add(parameters.SymbolId);
        foreach (var id in parameters?.SymbolIds ?? [])
        {
            if (!string.IsNullOrEmpty(id))
                symbols.Add(id);
        }

        // Quantower's times are UTC; one not marked either way is taken as UTC too.
        static DateTime Utc(DateTime d) => d.Kind == DateTimeKind.Local ? d.ToUniversalTime() : DateTime.SpecifyKind(d, DateTimeKind.Utc);
        DateTime from = parameters == null || parameters.From == default ? DateTime.MinValue : Utc(parameters.From);
        DateTime to = parameters == null || parameters.To == default || parameters.To == DateTime.MaxValue ? DateTime.MaxValue : Utc(parameters.To);
        var closes = this.Closes(fills);
        var trades = fills
            .Where(f => f.TimeUtc >= from && f.TimeUtc <= to)
            .Select(f => this.TradeMessage(f, closes))
            .Where(t => symbols.Count == 0 || symbols.Contains(t.SymbolId))
            .ToList();
        if (parameters != null)
            Log.Info($"[trade] trades asked for {parameters.From:yyyy-MM-dd HH:mm:ss} ({parameters.From.Kind}) to {parameters.To:yyyy-MM-dd HH:mm:ss}{(symbols.Count > 0 ? $", {string.Join(' ', symbols)}" : "")}: {trades.Count} of {fills.Count}");
        return trades;
    }

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
    /// Before a position hotkey's order: cancels the stock's working orders on the other side (a sell key's buys) and
    /// waits for them to finish and the position to count their fills (OrderHotkey.CancelWait, PositionWait). Returns
    /// what happened, for the log, or null when there were none.
    /// </summary>
    public string CancelBeforeHotkey(string ibkrSymbol, bool keyBuys) =>
        this.trader?.CancelSide(ibkrSymbol, !keyBuys, OrderHotkey.CancelWait, OrderHotkey.PositionWait);

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
        this.push(this.TradeMessage(latest, this.Closes(this.trader?.Fills() ?? [])));
    }

    /// <summary>
    /// Only a refusal pops up (your request, 2026-11-19: no extra notifications on a hotkey order): what ended an order,
    /// and a cancel (202) that gives a reason. Every order popped up IBKR's 2109 warning (outside regular hours is
    /// ignored), and every IOC a 202 with no reason for its unfilled rest; those are only logged, as IbkrTrader has.
    /// A 202 with a reason is IBKR refusing the order: the same morning VIVS at $0.25 got "We cannot accept an order at
    /// a limit price at or more aggressive than 0.2752686" for the ask plus 10 cents.
    /// </summary>
    public void OnTraderOrderMessage(int orderId, int code, string text, bool endedOrder)
    {
        string reason = CancelReason(text);
        if (code == 202 ? reason.Length > 0 : endedOrder)
            this.push(MessageDealTicket.CreateRefuseDealTicket(Header, $"IBKR order {orderId}: {(code == 202 ? reason : text)} ({code})"));
    }

    /// <summary>What follows "reason:" in IBKR's cancel message, without its HTML line breaks; empty when there's none.</summary>
    public static string CancelReason(string text)
    {
        int at = text?.IndexOf("reason:", StringComparison.OrdinalIgnoreCase) ?? -1;
        return at < 0 ? "" : text[(at + "reason:".Length)..].Replace("<br>", " ", StringComparison.OrdinalIgnoreCase).Trim();
    }

    #endregion IBKR says

    #region Messages

    private string SymbolId(string ibkrSymbol) => TradingSymbols.FromIbkr(ibkrSymbol, this.vendorKey);

    private static string PositionId(IbkrPosition p) => IbkrTrader.PositionKey(p.Account, p.Symbol);

    /// <summary>
    /// Whether each fill closed shares (IbkrTrader.Closes), from each position's quantity before the fills first
    /// seen: IBKR's position then, less those fills.
    /// </summary>
    private Dictionary<string, bool> Closes(IReadOnlyList<IbkrFill> fills)
    {
        lock (this.startGate)
        {
            if (this.startQuantity == null)
            {
                this.startQuantity = [];
                foreach (var p in this.trader?.Positions() ?? [])
                    this.startQuantity[PositionId(p)] = p.Quantity;
                foreach (var f in fills)
                {
                    string key = IbkrTrader.PositionKey(f.Account, f.Symbol);
                    this.startQuantity[key] = this.startQuantity.GetValueOrDefault(key) - (f.Buy ? f.Shares : -f.Shares);
                }
            }

            return IbkrTrader.Closes(fills, this.startQuantity);
        }
    }

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

    /// <summary>
    /// A fill as a Quantower trade, with its P&amp;L when it closed shares (your report, 2026-11-19: paper trades showed
    /// no P&amp;L, as only the commission was sent): net is IBKR's realized P&amp;L, after commissions, and gross adds
    /// this fill's commission back. A fill that opened or added has none. The Trades panel has no P&amp;L % column,
    /// so the net P&amp;L as a share of what the closed shares cost goes in its comment, "+3.76%" (your request, the
    /// same day).
    /// </summary>
    private MessageTrade TradeMessage(IbkrFill f, IReadOnlyDictionary<string, bool> closes)
    {
        double fee = double.IsNaN(f.Commission) ? 0 : f.Commission;
        var trade = new MessageTrade
        {
            TradeId = f.ExecId,
            SymbolId = this.SymbolId(f.Symbol),
            AccountId = f.Account,
            OrderId = f.OrderId.ToString(CultureInfo.InvariantCulture),
            DateTime = f.TimeUtc,
            Side = f.Buy ? Side.Buy : Side.Sell,
            Price = f.Price,
            Quantity = f.Shares,
            Fee = new PnLItem { AssetID = Usd, Value = -fee },

            // Which position it changed, and whether it opened or closed it, for Account performance (your report,
            // 2026-11-19: it showed no trades while the Trades list had 661), which pairs openings with closings.
            PositionId = IbkrTrader.PositionKey(f.Account, f.Symbol),
            PositionImpactType = closes.GetValueOrDefault(f.ExecId) ? PositionImpactType.Close : PositionImpactType.Open,
        };

        if (!double.IsNaN(f.RealizedPnl))
        {
            trade.NetPnl = new PnLItem { AssetID = Usd, Value = f.RealizedPnl };
            trade.GrossPnl = new PnLItem { AssetID = Usd, Value = f.RealizedPnl + fee };
            double percent = IbkrTrader.PnlPercent(f.Buy, f.Price, f.Shares, f.RealizedPnl + fee, f.RealizedPnl);
            if (!double.IsNaN(percent))
                trade.Comment = percent.ToString("+0.00;-0.00;0.00", CultureInfo.InvariantCulture) + "%";
        }

        return trade;
    }

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

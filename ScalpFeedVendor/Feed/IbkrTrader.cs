using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.Net.Sockets;
using System.Text;

namespace ScalpFeedVendor.Feed;

/// <summary>One order as IBKR last described it. Prices are NaN where the order type has none.</summary>
internal sealed record IbkrOrder(
    int Id,
    string Account,
    string Symbol,
    bool Buy,
    double Quantity,
    string Type,
    double LimitPrice,
    double StopPrice,
    string Tif,
    bool OutsideRth,
    string Status,
    double Filled,
    double AvgFillPrice,
    DateTime UpdatedUtc)
{
    /// <summary>IBKR's statuses after which the order can't fill any more.</summary>
    public bool IsDone => this.Status is "Filled" or "Cancelled" or "ApiCancelled" or "Inactive" or IbkrTrader.GoneStatus;

    /// <summary>
    /// The price Quantower shows for the order: its limit while it works, where it rests, and once it's finished with
    /// fills, its average fill, so order history and the chart's order markers show where it filled rather than the
    /// limit it would have paid (your request, 2026-11-18: an Alt+1 at a 3.34 limit filled at 3.24 and showed 3.34).
    /// NaN for a working order with no limit (market, stop).
    /// </summary>
    public double ShownPrice => this.IsDone && this.Filled > 0 && this.AvgFillPrice > 0 ? this.AvgFillPrice : this.LimitPrice;
}

/// <summary>What Quantower asks for: an IBKR order type (MKT, LMT, STP, STP LMT) and time in force (DAY, GTC, IOC).</summary>
internal sealed record IbkrOrderTicket(string Symbol, bool Buy, double Quantity, string Type, double LimitPrice, double StopPrice, string Tif);

internal sealed record IbkrPosition(string Account, string Symbol, double Quantity, double AvgCost);

/// <param name="RealizedPnl">
/// IBKR's realized P&amp;L for a fill that closed shares, after commissions, from its commission report; NaN for one
/// that opened or added (IBKR sends the largest double then), or before the report comes in.
/// </param>
internal sealed record IbkrFill(string ExecId, int OrderId, string Account, string Symbol, bool Buy, double Shares, double Price, DateTime TimeUtc, double Commission, double RealizedPnl = double.NaN);

internal interface ITraderSink
{
    void OnTraderState(bool ready, string detail);

    void OnTraderAccount(string account, IReadOnlyDictionary<string, string> values);

    void OnTraderPosition(IbkrPosition position);

    void OnTraderOrder(IbkrOrder order);

    void OnTraderFill(IbkrFill fill);

    /// <summary>IBKR's message about one order: a warning it carries on with, or an error that ended it.</summary>
    void OnTraderOrderMessage(int orderId, int code, string text, bool endedOrder);
}

/// <summary>
/// Orders, positions and the account, over a connection of its own to IB Gateway with its own client
/// ID, so the market data connection never carries an order and its AllowedRequests stays without
/// one. Paper accounts only: if the Gateway is logged in to anything but a paper account (IDs start
/// with D), this client sends nothing beyond startApi and refuses every order. There is no setting
/// that changes that. It never places an order by itself; each one comes from you through Quantower.
/// </summary>
internal sealed class IbkrTrader
{
    /// <summary>startApi, reqPositions, reqAccountSummary, reqOpenOrders, reqExecutions, placeOrder, cancelOrder.</summary>
    public static readonly IReadOnlySet<int> AllowedRequests = new HashSet<int> { 71, 61, 62, 5, 7, 3, 4 };

    /// <summary>An order that was open when the connection dropped and wasn't there when it came back.</summary>
    public const string GoneStatus = "Gone";

    // ib_insync 0.9.86's warning codes: an order keeps working through these.
    private static readonly HashSet<int> WarningCodes = [110, 165, 202, 399, 404, 434, 492, 10167];

    private const string ClientVersions = "v100..176";
    private const int SummaryRequestId = 9001;
    private const int ExecutionsRequestId = 9002;
    private const string SummaryTags = "NetLiquidation,TotalCashValue,BuyingPower,AvailableFunds,ExcessLiquidity,DayTradesRemaining";
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan SendTimeout = TimeSpan.FromSeconds(5);
    private static readonly double[] BackoffSeconds = [1, 2, 4, 8, 15, 30];
    private static readonly TimeSpan NotPaperRetry = TimeSpan.FromMinutes(1);

    private readonly string host;
    private readonly int port;
    private readonly int clientId;
    private readonly object gate = new();
    private readonly SemaphoreSlim sendGate = new(1, 1);
    private readonly Dictionary<int, IbkrOrder> orders = new();
    private readonly Dictionary<string, IbkrPosition> positions = new();
    private readonly Dictionary<string, IbkrFill> fills = new();
    private readonly Dictionary<string, Dictionary<string, string>> accountValues = new();
    private readonly HashSet<string> positionsSeen = new();
    private readonly HashSet<int> ordersSeen = new();

    // When, by symbol, IBKR last sent a fill as it happened (not today's fills asked for at connect) and a position
    // (Stopwatch ticks), so a position hotkey that cancels orders first can tell when the position counts their fills.
    private readonly Dictionary<string, long> liveFillHeard = new();
    private readonly Dictionary<string, long> positionHeard = new();

    private ITraderSink sink;
    private CancellationTokenSource stopCts;
    private Task runTask;
    private NetworkStream net;
    private string[] accounts = [];
    private bool gotAccounts;
    private bool notPaper;
    private bool requested;
    private int nextOrderId = -1;
    private volatile bool ready;
    private volatile string notReadyWhy = "IBKR trading is connecting";

    public IbkrTrader(string host, int port, int clientId)
    {
        this.host = host;
        this.port = port;
        this.clientId = clientId;
    }

    public bool Ready => this.ready;

    public string NotReadyWhy => this.notReadyWhy;

    /// <summary>The paper account orders go to, once connected.</summary>
    public string Account
    {
        get
        {
            lock (this.gate)
                return this.ready && this.accounts.Length > 0 ? this.accounts[0] : null;
        }
    }

    /// <summary>Messages sent since the start, by message ID, so tests can see what went out.</summary>
    public List<int> Sent { get; } = new();

    public void SetSink(ITraderSink newSink) => Volatile.Write(ref this.sink, newSink);

    public void Start()
    {
        this.stopCts = new CancellationTokenSource();
        this.runTask = Task.Run(() => this.RunAsync(this.stopCts.Token));
    }

    public async Task StopAsync()
    {
        if (this.stopCts == null)
            return;

        this.stopCts.Cancel();
        try
        {
            await this.runTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
    }

    public IReadOnlyList<IbkrOrder> OpenOrders()
    {
        lock (this.gate)
            return this.orders.Values.Where(o => !o.IsDone).ToList();
    }

    public IReadOnlyList<IbkrPosition> Positions()
    {
        lock (this.gate)
            return this.positions.Values.Where(p => p.Quantity != 0).ToList();
    }

    public IReadOnlyList<IbkrFill> Fills()
    {
        lock (this.gate)
            return this.fills.Values.OrderBy(f => f.TimeUtc).ToList();
    }

    public IReadOnlyDictionary<string, string> AccountValues(string account)
    {
        lock (this.gate)
            return this.accountValues.TryGetValue(account, out var values) ? new Dictionary<string, string>(values) : new Dictionary<string, string>();
    }

    public IbkrOrder Order(int id)
    {
        lock (this.gate)
            return this.orders.GetValueOrDefault(id);
    }

    /// <summary>Sends a new order. Returns its ID, or 0 and why not.</summary>
    public (int Id, string Error) Place(IbkrOrderTicket ticket)
    {
        string problem = Check(ticket);
        if (problem != null)
            return (0, problem);

        IbkrOrder order;
        lock (this.gate)
        {
            if (!this.ready)
                return (0, this.notReadyWhy);

            order = new IbkrOrder(this.nextOrderId++, this.accounts[0], ticket.Symbol, ticket.Buy, ticket.Quantity, ticket.Type,
                HasLimit(ticket.Type) ? ticket.LimitPrice : double.NaN, HasStop(ticket.Type) ? ticket.StopPrice : double.NaN,
                ticket.Tif, OutsideRthFor(ticket.Type), "PendingSubmit", 0, 0, DateTime.UtcNow);
            this.orders[order.Id] = order;
        }

        string error = this.SendNow(PlaceOrderFields(order));
        if (error != null)
        {
            lock (this.gate)
                this.orders.Remove(order.Id);
            return (0, error);
        }

        Volatile.Read(ref this.sink)?.OnTraderOrder(order);
        return (order.Id, null);
    }

    /// <summary>Changes a working order's quantity or prices, which IBKR does by the same ID sent again.</summary>
    public string Modify(int id, double quantity, double limitPrice, double stopPrice)
    {
        IbkrOrder changed;
        lock (this.gate)
        {
            if (!this.ready)
                return this.notReadyWhy;
            if (!this.orders.TryGetValue(id, out var order) || order.IsDone)
                return $"order {id} isn't working any more";

            changed = order with
            {
                Quantity = quantity > 0 ? quantity : order.Quantity,
                LimitPrice = HasLimit(order.Type) ? limitPrice : double.NaN,
                StopPrice = HasStop(order.Type) ? stopPrice : double.NaN,
            };
        }

        string problem = Check(new IbkrOrderTicket(changed.Symbol, changed.Buy, changed.Quantity, changed.Type, changed.LimitPrice, changed.StopPrice, changed.Tif));
        return problem ?? this.SendNow(PlaceOrderFields(changed));
    }

    public string Cancel(int id)
    {
        lock (this.gate)
        {
            if (!this.ready)
                return this.notReadyWhy;
            if (!this.orders.TryGetValue(id, out var order) || order.IsDone)
                return $"order {id} isn't working any more";
        }

        return this.SendNow([4, 1, id, ""]);
    }

    /// <summary>
    /// Cancels the working orders in a symbol on one side, buys or sells, then waits for IBKR to say each is done (up
    /// to doneWait) and for its position to count their last fills (up to positionWait), so an order sized on the
    /// position can follow and IBKR won't reprice it away from them. Returns what happened, for the log, or null when
    /// nothing on that side was working.
    /// </summary>
    public string CancelSide(string symbol, bool buys, TimeSpan doneWait, TimeSpan positionWait)
    {
        var working = this.OpenOrders().Where(o => o.Symbol == symbol && o.Buy == buys).ToList();
        if (working.Count == 0)
            return null;

        var clock = Stopwatch.StartNew();
        var refused = new List<string>();
        foreach (var order in working)
        {
            string error = this.Cancel(order.Id);
            if (error != null && this.Order(order.Id)?.IsDone != true)
                refused.Add($"{order.Id} ({error})");
        }

        bool done = this.WaitUntil(() => working.All(o => this.Order(o.Id)?.IsDone != false), doneWait);
        long doneMs = clock.ElapsedMilliseconds;
        bool counted = this.WaitUntil(() => this.PositionCountsFills(symbol), positionWait);
        long countedMs = clock.ElapsedMilliseconds - doneMs;

        string which = string.Join(", ", working.Select(o => this.Order(o.Id) is { } now
            ? $"{o.Id} ({now.Filled:#,0} of {now.Quantity:#,0} filled, {now.Status})"
            : o.Id.ToString(CultureInfo.InvariantCulture)));
        var said = new List<string> { $"cancelled working {(buys ? "buy" : "sell")} {which}" };
        if (refused.Count > 0)
            said.Add($"cancel refused for {string.Join(", ", refused)}");
        said.Add(done ? $"done in {doneMs} ms" : $"IBKR hadn't confirmed after {doneMs} ms");
        said.Add(counted ? $"position up to date {countedMs} ms later" : $"no position after the last fill within {countedMs} ms, so the size goes by the one before");
        return string.Join("; ", said);
    }

    /// <summary>
    /// Cancels one working order and waits, up to doneWait, for IBKR to say it's done. Returns what happened, for the
    /// log; done is false while it may still fill.
    /// </summary>
    public string CancelAndWait(int id, TimeSpan doneWait, out bool done)
    {
        var clock = Stopwatch.StartNew();
        string error = this.Order(id)?.Status == "PendingCancel" ? null : this.Cancel(id);
        done = this.WaitUntil(() => this.Order(id)?.IsDone != false, doneWait);
        var now = this.Order(id);
        string refused = error != null && !done ? $"; cancel refused ({error})" : "";
        return $"cancelled the last buy, order {id} ({now?.Filled:#,0} of {now?.Quantity:#,0} filled, {now?.Status}), " +
               $"{(done ? "done in" : "IBKR hadn't confirmed after")} {clock.ElapsedMilliseconds} ms{refused}";
    }

    /// <summary>Whether IBKR's position in the symbol has come in since its last fill as it happened, so it counts that fill.</summary>
    public bool PositionCountsFills(string symbol)
    {
        lock (this.gate)
            return !this.liveFillHeard.TryGetValue(symbol, out long fill) || this.positionHeard.GetValueOrDefault(symbol) >= fill;
    }

    /// <summary>Waits, up to the timeout, for the condition to hold, checking it each time IBKR changes an order, fill or position.</summary>
    private bool WaitUntil(Func<bool> condition, TimeSpan timeout)
    {
        var clock = Stopwatch.StartNew();
        lock (this.gate)
        {
            while (!condition())
            {
                var left = timeout - clock.Elapsed;
                if (left <= TimeSpan.Zero)
                    return false;
                Monitor.Wait(this.gate, left);
            }
        }

        return true;
    }

    /// <summary>
    /// The placeOrder message for server version 176, field for field as ib_insync 0.9.86 builds it
    /// for a plain stock order: SMART, USD, transmitted, no algo, conditions, legs or scale fields.
    /// </summary>
    internal static object[] PlaceOrderFields(IbkrOrder o) =>
    [
        3, o.Id,
        0, o.Symbol, "STK", "", "0.0", "", "", "SMART", "", "USD", "", "", // contract
        "", "", // secIdType, secId
        o.Buy ? "BUY" : "SELL", Num(o.Quantity), o.Type, Price(o.LimitPrice), Price(o.StopPrice), o.Tif,
        "", o.Account, "O", 0, "", // ocaGroup, account, openClose, origin, orderRef
        true, 0, false, false, 0, 0, o.OutsideRth, false, // transmit, parentId, blockOrder, sweepToFill, displaySize, triggerMethod, outsideRth, hidden
        "", // deprecated sharesAllocation
        "0.0", "", "", // discretionaryAmt, goodAfterTime, goodTillDate
        "", "", "", "", "", // faGroup, faMethod, faPercentage, faProfile, modelCode
        0, "", -1, // shortSaleSlot, designatedLocation, exemptCode
        0, "", "", // ocaType, rule80A, settlingFirm
        false, "", "", false, false, "", // allOrNone, minQty, percentOffset, eTradeOnly, firmQuoteOnly, nbboPriceCap
        0, "", "", "", "", "", // auctionStrategy, startingPrice, stockRefPrice, delta, stockRangeLower, stockRangeUpper
        false, "", "", "", "", // overridePercentageConstraints, volatility, volatilityType, deltaNeutralOrderType, deltaNeutralAuxPrice
        false, "", "", "", "", "", "", // continuousUpdate, referencePriceType, trailStopPrice, trailingPercent, scale init/subs level size, scalePriceIncrement
        "", "", "", "", // scaleTable, activeStartTime, activeStopTime, hedgeType
        false, "", "", false, // optOutSmartRouting, clearingAccount, clearingIntent, notHeld
        false, // no delta neutral contract
        "", // algoStrategy
        "", false, "", false, false, false, // algoId, whatIf, orderMiscOptions, solicited, randomizeSize, randomizePrice
        0, // conditions
        "", "", "", "", "", "", 0, "", // adjustedOrderType, triggerPrice, lmtPriceOffset, adjustedStopPrice, adjustedStopLimitPrice, adjustedTrailingAmount, adjustableTrailingUnit, extOperator
        "", "", "", // softDollarTier name and value, cashQty
        "", "", "", "", // MiFID II fields
        false, false, false, false, // dontUseAutoPriceForHedge, isOmsContainer, discretionaryUpToLimitPrice, usePriceMgmtAlgo
        "", "", false, "", "", // duration, postToAts, autoCancelParent, advancedErrorOverride, manualOrderTime
    ];

    /// <summary>
    /// Limit orders are allowed to work in premarket and after hours; IBKR ignores the flag in regular
    /// hours. Market and stop orders keep to regular hours, where IBKR will execute them.
    /// </summary>
    internal static bool OutsideRthFor(string type) => type is "LMT" or "STP LMT";

    private static bool HasLimit(string type) => type is "LMT" or "STP LMT";

    private static bool HasStop(string type) => type is "STP" or "STP LMT";

    /// <summary>What's wrong with an order before it's sent, or null.</summary>
    internal static string Check(IbkrOrderTicket t)
    {
        if (string.IsNullOrWhiteSpace(t.Symbol))
            return "no symbol";
        if (!(t.Quantity > 0) || t.Quantity != Math.Floor(t.Quantity))
            return $"quantity {t.Quantity} isn't a whole number of shares";
        if (t.Type is not ("MKT" or "LMT" or "STP" or "STP LMT"))
            return $"order type {t.Type} isn't supported";
        if (t.Tif is not ("DAY" or "GTC" or "IOC"))
            return $"time in force {t.Tif} isn't supported";
        if (HasLimit(t.Type) && !(t.LimitPrice > 0))
            return "a limit order needs a limit price";
        if (HasStop(t.Type) && !(t.StopPrice > 0))
            return "a stop order needs a stop price";
        return null;
    }

    // Shortest form that reads back as the same double, so a limit price goes out exactly as entered.
    private static string Num(double value) => value.ToString("R", CultureInfo.InvariantCulture);

    private static string Price(double value) => double.IsNaN(value) ? "" : Num(value);

    private string SendNow(object[] fields)
    {
        try
        {
            using var timeout = new CancellationTokenSource(SendTimeout);
            this.SendAsync(timeout.Token, fields).GetAwaiter().GetResult();
            return null;
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException or OperationCanceledException or SocketException)
        {
            return $"not sent to IBKR: {ex.Message}";
        }
    }

    private async Task SendAsync(CancellationToken ct, params object[] fields)
    {
        int messageId = Convert.ToInt32(fields[0], CultureInfo.InvariantCulture);
        if (!AllowedRequests.Contains(messageId))
            throw new InvalidOperationException($"IBKR message {messageId} isn't allowed on the trading connection");

        // The last line of defence: nothing but startApi goes to an account that isn't paper.
        if (messageId != 71)
        {
            lock (this.gate)
            {
                if (!this.gotAccounts || this.notPaper)
                    throw new InvalidOperationException("refusing: the Gateway isn't logged in to a paper account");
            }
        }

        var target = this.net ?? throw new IOException("not connected");
        var text = new StringBuilder();
        foreach (var field in fields)
            text.Append(field is bool b ? (b ? "1" : "0") : Convert.ToString(field, CultureInfo.InvariantCulture)).Append('\0');

        await this.WriteFrameAsync(target, Encoding.ASCII.GetBytes(text.ToString()), ct).ConfigureAwait(false);
        lock (this.Sent)
            this.Sent.Add(messageId);
    }

    private async Task WriteFrameAsync(NetworkStream target, byte[] body, CancellationToken ct)
    {
        var frame = new byte[body.Length + 4];
        BinaryPrimitives.WriteInt32BigEndian(frame, body.Length);
        body.CopyTo(frame, 4);

        await this.sendGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await target.WriteAsync(frame, ct).ConfigureAwait(false);
        }
        finally
        {
            this.sendGate.Release();
        }
    }

    #region Connection

    private async Task RunAsync(CancellationToken stop)
    {
        int attempt = 0;
        while (!stop.IsCancellationRequested)
        {
            string reason;
            try
            {
                reason = await this.RunOneConnectionAsync(stop).ConfigureAwait(false);
                attempt = 0;
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                reason = ex.GetBaseException().Message;
                if (ex is not (SocketException or IOException or ObjectDisposedException or OperationCanceledException))
                    Log.Info($"[trade] unexpected failure: {ex}");
            }

            this.SetNotReady(this.notPaper ? "the Gateway isn't logged in to a paper account, so trading is off" : $"IBKR trading connection is down: {reason}");
            var delay = this.notPaper ? NotPaperRetry : TimeSpan.FromSeconds(BackoffSeconds[Math.Min(attempt++, BackoffSeconds.Length - 1)]);
            try
            {
                await Task.Delay(delay, stop).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        this.SetNotReady("IBKR trading is stopped");
    }

    private async Task<string> RunOneConnectionAsync(CancellationToken stop)
    {
        lock (this.gate)
        {
            this.gotAccounts = false;
            this.notPaper = false;
            this.requested = false;
            this.nextOrderId = -1;
        }

        using var connection = CancellationTokenSource.CreateLinkedTokenSource(stop);
        using var tcp = new TcpClient { NoDelay = true };
        using (var connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(connection.Token))
        {
            connectTimeout.CancelAfter(ConnectTimeout);
            await tcp.ConnectAsync(this.host, this.port, connectTimeout.Token).ConfigureAwait(false);
        }

        var stream = tcp.GetStream();
        this.net = stream;
        try
        {
            await stream.WriteAsync("API\0"u8.ToArray(), connection.Token).ConfigureAwait(false);
            await this.WriteFrameAsync(stream, Encoding.ASCII.GetBytes(ClientVersions), connection.Token).ConfigureAwait(false);
            await this.SendAsync(connection.Token, 71, 2, this.clientId, "").ConfigureAwait(false);
            return await this.ReceiveLoopAsync(stream, connection).ConfigureAwait(false);
        }
        finally
        {
            this.net = null;
        }
    }

    private async Task<string> ReceiveLoopAsync(NetworkStream stream, CancellationTokenSource connection)
    {
        var buffer = new byte[64 * 1024];
        int start = 0;
        int end = 0;
        bool greeted = false;
        while (true)
        {
            while (end - start >= 4)
            {
                int length = BinaryPrimitives.ReadInt32BigEndian(buffer.AsSpan(start));
                if (end - start - 4 < length)
                    break;

                var fields = Encoding.UTF8.GetString(buffer, start + 4, length).Split('\0');
                start += 4 + length;
                if (!greeted)
                {
                    greeted = true; // The server's version and time.
                    continue;
                }

                this.Handle(fields);
                if (this.notPaper)
                {
                    connection.Cancel();
                    return "not a paper account";
                }
            }

            if (start > 0)
            {
                Buffer.BlockCopy(buffer, start, buffer, 0, end - start);
                end -= start;
                start = 0;
            }

            if (end == buffer.Length)
                Array.Resize(ref buffer, buffer.Length * 2);

            int read = await stream.ReadAsync(buffer.AsMemory(end), connection.Token).ConfigureAwait(false);
            if (read == 0)
                return "the Gateway closed the connection";
            end += read;
        }
    }

    private void SetNotReady(string why)
    {
        bool was = this.ready;
        this.notReadyWhy = why;
        this.ready = false;
        if (was)
        {
            Log.Info($"[trade] {why}");
            Volatile.Read(ref this.sink)?.OnTraderState(false, why);
        }
    }

    private void MaybeReady()
    {
        lock (this.gate)
        {
            if (this.ready || !this.gotAccounts || this.notPaper || this.nextOrderId < 0)
                return;
            this.ready = true;
        }

        Log.Info($"[trade] ready on paper account {this.Account}, client {this.clientId}, next order {this.nextOrderId}");
        Volatile.Read(ref this.sink)?.OnTraderState(true, "ready");
    }

    private void RequestState()
    {
        lock (this.gate)
        {
            if (this.requested)
                return;
            this.requested = true;
            this.positionsSeen.Clear();
            this.ordersSeen.Clear();
        }

        _ = Task.Run(async () =>
        {
            try
            {
                using var timeout = new CancellationTokenSource(SendTimeout);
                await this.SendAsync(timeout.Token, 61, 1).ConfigureAwait(false);
                await this.SendAsync(timeout.Token, 62, 1, SummaryRequestId, "All", SummaryTags).ConfigureAwait(false);
                await this.SendAsync(timeout.Token, 5, 1).ConfigureAwait(false);
                await this.SendAsync(timeout.Token, 7, 3, ExecutionsRequestId, 0, "", "", "", "", "", "").ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException or OperationCanceledException)
            {
                Log.Info($"[trade] asking for positions and orders failed: {ex.Message}");
            }
        });
    }

    #endregion Connection

    #region Messages

    private void Handle(string[] f)
    {
        if (f.Length == 0)
            return;

        switch (f[0])
        {
            case "15":
                this.OnAccounts(At(f, 2));
                break;
            case "9":
                // Never below an ID already used here, whatever the Gateway says after a reconnect.
                lock (this.gate)
                    this.nextOrderId = Math.Max(Math.Max(this.nextOrderId, Int(At(f, 2))), this.orders.Count > 0 ? this.orders.Keys.Max() + 1 : 0);
                this.MaybeReady();
                break;
            case "4":
                this.OnError(Int(At(f, 2)), Int(At(f, 3)), At(f, 4));
                break;
            case "3":
                this.OnOrderStatus(f);
                break;
            case "5":
                this.OnOpenOrder(f);
                break;
            case "53":
                this.OnOpenOrderEnd();
                break;
            case "11":
                this.OnExecution(f);
                break;
            case "59":
                this.OnCommission(f);
                break;
            case "61":
                this.OnPosition(f);
                break;
            case "62":
                this.OnPositionEnd();
                break;
            case "63":
                this.OnAccountSummary(At(f, 3), At(f, 4), At(f, 5));
                break;
        }
    }

    private void OnAccounts(string list)
    {
        var ids = list.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        bool paper = ids.Length > 0 && ids.All(id => id.StartsWith('D'));
        lock (this.gate)
        {
            this.accounts = ids;
            this.gotAccounts = true;
            this.notPaper = !paper;
        }

        if (!paper)
        {
            Log.Info("[trade] the Gateway isn't logged in to a paper account: trading stays off and nothing is sent");
            return;
        }

        this.RequestState();
        this.MaybeReady();
    }

    /// <summary>
    /// As ib_insync does: an error that isn't one of these warnings means the order is dead. Beyond
    /// that, a price-increment complaint (110) on an order IBKR never acknowledged means it was refused.
    /// </summary>
    private void OnError(int id, int code, string text)
    {
        bool forOrder;
        IbkrOrder killed = null;
        lock (this.gate)
        {
            forOrder = this.orders.TryGetValue(id, out var order);
            if (forOrder && !order.IsDone)
            {
                bool warning = WarningCodes.Contains(code) || code is >= 2100 and < 2200;
                bool neverAcknowledged = order.Status == "PendingSubmit";
                if (!warning || (neverAcknowledged && code == 110))
                {
                    killed = order with { Status = neverAcknowledged ? "Inactive" : "Cancelled", UpdatedUtc = DateTime.UtcNow };
                    this.orders[id] = killed;
                    Monitor.PulseAll(this.gate);
                }
            }
        }

        Log.Info($"[trade] {(forOrder ? $"order {id}: " : id > 0 ? $"request {id}: " : "")}{code} {text}{(killed != null ? $", so it's {killed.Status}" : "")}");
        if (!forOrder)
            return;

        var target = Volatile.Read(ref this.sink);
        target?.OnTraderOrderMessage(id, code, text, killed != null);
        if (killed != null)
            target?.OnTraderOrder(killed);
    }

    // [3, orderId, status, filled, remaining, avgFillPrice, permId, parentId, lastFillPrice, clientId, whyHeld, mktCapPrice]
    private void OnOrderStatus(string[] f)
    {
        int id = Int(At(f, 1));
        IbkrOrder updated;
        lock (this.gate)
        {
            if (!this.orders.TryGetValue(id, out var order))
                return;

            updated = order with { Status = At(f, 2), Filled = Dbl(At(f, 3)), AvgFillPrice = Dbl(At(f, 5)), UpdatedUtc = DateTime.UtcNow };
            if (updated == order with { UpdatedUtc = updated.UpdatedUtc })
                return;
            this.orders[id] = updated;
            Monitor.PulseAll(this.gate);
        }

        Log.Info($"[trade] order {id} {updated.Status}, filled {updated.Filled} of {updated.Quantity}{(updated.Filled > 0 ? $" at {updated.AvgFillPrice}" : "")}");
        Volatile.Read(ref this.sink)?.OnTraderOrder(updated);
    }

    // [5, orderId, conId, symbol, secType, lastTradeDate, strike, right, multiplier, exchange, currency, localSymbol,
    //  tradingClass, action, totalQuantity, orderType, lmtPrice, auxPrice, tif, ocaGroup, account, ... outsideRth at 26 ...]
    private void OnOpenOrder(string[] f)
    {
        if (At(f, 4) != "STK")
            return;

        int id = Int(At(f, 1));
        string type = At(f, 15);
        IbkrOrder updated;
        lock (this.gate)
        {
            this.ordersSeen.Add(id);
            this.orders.TryGetValue(id, out var known);
            updated = new IbkrOrder(
                id,
                At(f, 20),
                At(f, 3),
                At(f, 13) == "BUY",
                Dbl(At(f, 14)),
                type,
                HasLimit(type) ? Dbl(At(f, 16)) : double.NaN,
                HasStop(type) ? Dbl(At(f, 17)) : double.NaN,
                At(f, 18),
                At(f, 26) == "1",
                known?.Status ?? "Submitted",
                known?.Filled ?? 0,
                known?.AvgFillPrice ?? 0,
                DateTime.UtcNow);
            if (known != null && updated == known with { UpdatedUtc = updated.UpdatedUtc })
                return;
            this.orders[id] = updated;
        }

        Volatile.Read(ref this.sink)?.OnTraderOrder(updated);
    }

    private void OnOpenOrderEnd()
    {
        // Working orders the Gateway no longer reports finished while the connection was down.
        List<IbkrOrder> gone;
        lock (this.gate)
        {
            gone = this.orders.Values.Where(o => !o.IsDone && !this.ordersSeen.Contains(o.Id) && o.Status != "PendingSubmit").ToList();
            foreach (var order in gone)
                this.orders[order.Id] = order with { Status = GoneStatus, UpdatedUtc = DateTime.UtcNow };
            Monitor.PulseAll(this.gate);
        }

        foreach (var order in gone)
        {
            Log.Info($"[trade] order {order.Id} finished while disconnected; positions and fills show how");
            Volatile.Read(ref this.sink)?.OnTraderOrder(this.Order(order.Id));
        }
    }

    // [11, reqId, orderId, conId, symbol, secType, lastTradeDate, strike, right, multiplier, exchange, currency, localSymbol,
    //  tradingClass, execId, time, acctNumber, exchange, side, shares, price, permId, clientId, liquidation, cumQty, avgPrice, ...]
    private void OnExecution(string[] f)
    {
        if (At(f, 5) != "STK")
            return;

        var fill = new IbkrFill(At(f, 14), Int(At(f, 2)), At(f, 16), At(f, 4), At(f, 18) == "BOT", Dbl(At(f, 19)), Dbl(At(f, 20)), ParseTime(At(f, 15)), double.NaN);
        lock (this.gate)
        {
            if (this.fills.ContainsKey(fill.ExecId))
                return;
            this.fills[fill.ExecId] = fill;
            if (At(f, 1) == "-1")
                this.liveFillHeard[fill.Symbol] = Stopwatch.GetTimestamp();
            Monitor.PulseAll(this.gate);
        }

        Log.Info($"[trade] fill {fill.Symbol} {(fill.Buy ? "bought" : "sold")} {fill.Shares} at {fill.Price}, order {fill.OrderId}");
        Volatile.Read(ref this.sink)?.OnTraderFill(fill);
    }

    // [59, version, execId, commission, currency, realizedPNL, yield, yieldRedemptionDate]
    private void OnCommission(string[] f)
    {
        IbkrFill updated;
        lock (this.gate)
        {
            if (!this.fills.TryGetValue(At(f, 2), out var fill))
                return;
            updated = fill with { Commission = Dbl(At(f, 3)), RealizedPnl = Reported(Dbl(At(f, 5))) };
            this.fills[fill.ExecId] = updated;
        }

        Volatile.Read(ref this.sink)?.OnTraderFill(updated);
    }

    // [61, version, account, conId, symbol, secType, lastTradeDate, strike, right, multiplier, exchange, currency,
    //  localSymbol, tradingClass, position, avgCost]
    private void OnPosition(string[] f)
    {
        if (At(f, 5) != "STK")
            return;

        var position = new IbkrPosition(At(f, 2), At(f, 4), Dbl(At(f, 14)), Dbl(At(f, 15)));
        string key = position.Account + "|" + position.Symbol;
        lock (this.gate)
        {
            this.positionsSeen.Add(key);
            this.positionHeard[position.Symbol] = Stopwatch.GetTimestamp();
            Monitor.PulseAll(this.gate);
            if (this.positions.TryGetValue(key, out var known) && known == position)
                return;
            this.positions[key] = position;
        }

        Volatile.Read(ref this.sink)?.OnTraderPosition(position);
    }

    private void OnPositionEnd()
    {
        // Positions closed while the connection was down aren't in the new list at all.
        List<IbkrPosition> closed;
        lock (this.gate)
        {
            closed = this.positions.Where(p => p.Value.Quantity != 0 && !this.positionsSeen.Contains(p.Key)).Select(p => p.Value with { Quantity = 0 }).ToList();
            foreach (var position in closed)
                this.positions[position.Account + "|" + position.Symbol] = position;
        }

        foreach (var position in closed)
            Volatile.Read(ref this.sink)?.OnTraderPosition(position);
    }

    private void OnAccountSummary(string account, string tag, string value)
    {
        Dictionary<string, string> snapshot;
        lock (this.gate)
        {
            if (!this.accountValues.TryGetValue(account, out var values))
                this.accountValues[account] = values = new Dictionary<string, string>();
            if (values.TryGetValue(tag, out var known) && known == value)
                return;
            values[tag] = value;
            snapshot = new Dictionary<string, string>(values);
        }

        Volatile.Read(ref this.sink)?.OnTraderAccount(account, snapshot);
    }

    private static string At(string[] f, int i) => i < f.Length ? f[i] : "";

    private static int Int(string text) => int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value) ? value : -1;

    /// <summary>
    /// A closing fill's net P&amp;L as a percentage of what its shares cost: closing a long (a sell), they cost the sale
    /// less the gross P&amp;L; covering a short (a buy), the purchase plus it. NaN when that cost isn't positive.
    /// </summary>
    internal static double PnlPercent(bool buy, double price, double shares, double gross, double net)
    {
        double cost = buy ? (price * shares) + gross : (price * shares) - gross;
        return cost > 0 ? net / cost * 100 : double.NaN;
    }

    /// <summary>A fill's position: its account and symbol, as the position it changes is known.</summary>
    internal static string PositionKey(string account, string symbol) => account + ":" + symbol;

    /// <summary>
    /// Which fills closed shares, by replaying them in time order from each position's starting quantity (start, by
    /// PositionKey; none is flat): a fill against the position held before it (selling a long, buying back a short)
    /// closes, one with it or from flat opens. A fill that goes through flat to the other side counts as closing.
    /// </summary>
    internal static Dictionary<string, bool> Closes(IReadOnlyList<IbkrFill> fills, IReadOnlyDictionary<string, double> start)
    {
        var held = new Dictionary<string, double>(start);
        var closes = new Dictionary<string, bool>();
        foreach (var f in fills.OrderBy(f => f.TimeUtc))
        {
            string key = PositionKey(f.Account, f.Symbol);
            double before = held.GetValueOrDefault(key);
            double signed = f.Buy ? f.Shares : -f.Shares;
            closes[f.ExecId] = before != 0 && Math.Sign(before) != Math.Sign(signed);
            held[key] = before + signed;
        }

        return closes;
    }

    /// <summary>IBKR's number, or NaN for its "not set" (the largest double, 1.7976931348623157E308).</summary>
    internal static double Reported(double value) => Math.Abs(value) > 1e300 ? double.NaN : value;

    private static double Dbl(string text) => double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double value) ? value : double.NaN;

    /// <summary>IBKR's execution time, "20261111  13:38:07" or with a zone after it; without one it's this PC's local time.</summary>
    internal static DateTime ParseTime(string text)
    {
        var parts = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2 || !DateTime.TryParseExact(parts[0] + " " + parts[1], "yyyyMMdd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var local))
            return DateTime.UtcNow;

        if (parts.Length >= 3)
        {
            try
            {
                var zone = TimeZoneInfo.FindSystemTimeZoneById(parts[2] is "US/Eastern" or "America/New_York" ? "Eastern Standard Time" : parts[2]);
                return TimeZoneInfo.ConvertTimeToUtc(local, zone);
            }
            catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException or ArgumentException)
            {
            }
        }

        return TimeZoneInfo.ConvertTimeToUtc(local, TimeZoneInfo.Local);
    }

    #endregion Messages
}

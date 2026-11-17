using System.Buffers.Binary;
using System.Buffers.Text;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ScalpFeedVendor.Feed;

/// <summary>Where the tape comes from: IBKR alone, tastytrade plus IBKR's other venues, or tastytrade alone.</summary>
internal enum TapeMode { Ibkr, Merged, Tastytrade }

/// <summary>
/// An IBKR market scanner the client keeps running while connected (your request, 2026-11-16), set up as momo-algo's
/// (your choice, same day): the top 25 percentage gainers on the US listing exchanges (STK.US.MAJOR leaves out OTC),
/// $0.10 to $25, over 50,000 shares and up 10% or more, as a fresh one-off scan every 2 seconds from 4:00 to 20:00 ET
/// and every minute outside it. momo-algo found a one-off scan fresher than a subscription, whose updates come about
/// every 30 seconds, and IBKR answers one in 160-730 ms. Filters left NaN or 0 are off.
/// From 16:00 to 4:00 ET it's the after-hours gainers instead, since Top % Gainers keeps the regular session's list
/// after the close (GMM and GDHG at 16:22 ET on 11-16, while the after-hours scan had BKHAR, PARA and DVLT).
/// IBKR has no premarket scan, and doesn't need one: momo-algo measured on 2026-08-05 that in premarket Top %
/// Gainers ranks premarket trades against the prior close, while the after-hours scan returns nothing after 4:00.
/// </summary>
internal sealed record IbkrScan(
    string Code = "TOP_PERC_GAIN",
    string AfterHoursCode = "TOP_AFTER_HOURS_PERC_GAIN",
    string Location = "STK.US.MAJOR",
    int Rows = 25,
    double PriceAbove = 0.10,
    double PriceBelow = 25,
    int VolumeAbove = 50_000,
    double ChangeAbove = 10,
    double IntervalSeconds = 2)
{
    private static readonly TimeZoneInfo Eastern = TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");

    /// <summary>Outside 4:00 to 20:00 ET nothing moves, so the scan runs once a minute.</summary>
    public const double QuietIntervalSeconds = 60;

    /// <summary>Seconds between scans at this moment.</summary>
    public double IntervalAt(DateTime utc)
    {
        var time = TimeZoneInfo.ConvertTimeFromUtc(utc, Eastern).TimeOfDay;
        return time >= TimeSpan.FromHours(4) && time < TimeSpan.FromHours(20) ? this.IntervalSeconds : QuietIntervalSeconds;
    }

    /// <summary>The scan for this moment: after hours from 16:00 to 4:00 ET, the regular one from 4:00.</summary>
    public string CodeAt(DateTime utc)
    {
        var time = TimeZoneInfo.ConvertTimeFromUtc(utc, Eastern).TimeOfDay;
        return time >= TimeSpan.FromHours(16) || time < TimeSpan.FromHours(4) ? this.AfterHoursCode : this.Code;
    }

    /// <summary>
    /// IBKR's halted stocks (your request, 2026-11-17), kept as the IBKR Halts list: scan code HALTED, on the same
    /// exchanges and $0.10-$25 as the gainers, up to 50 rows, every 2 s, and only stocks traded today. Unfiltered
    /// on the paper Gateway at 11:20 ET on 11-17 its 19 rows were all halts with no trading that day (SPAC units,
    /// names halted for weeks), Halted 1 and volume 0, and a volume over 0 left none of them.
    /// </summary>
    public static IbkrScan HaltDefaults { get; } = new("HALTED", "HALTED", Rows: 50, VolumeAbove: 1, ChangeAbove: 0);

    /// <summary>From ibkr.json's topGainers or halts: false turns it off, an object changes what it asks for.</summary>
    public static IbkrScan From(JsonNode json, IbkrScan defaults = null)
    {
        var d = defaults ?? new IbkrScan();
        if (json is JsonValue v && v.TryGetValue(out bool on))
            return on ? d : null;
        if (json is not JsonObject o)
            return d;

        return new IbkrScan(
            (string)o["scanCode"] ?? d.Code,
            (string)o["afterHoursScanCode"] ?? d.AfterHoursCode,
            (string)o["location"] ?? d.Location,
            o["rows"]?.GetValue<int>() ?? d.Rows,
            o["priceAbove"]?.GetValue<double>() ?? d.PriceAbove,
            o["priceBelow"]?.GetValue<double>() ?? d.PriceBelow,
            o["volumeAbove"]?.GetValue<int>() ?? d.VolumeAbove,
            o["changeAbove"]?.GetValue<double>() ?? d.ChangeAbove,
            o["intervalSeconds"]?.GetValue<double>() ?? d.IntervalSeconds);
    }

    /// <summary>
    /// The change filter for this scan. It has to match the scan: on the after-hours one, changePercAbove would
    /// still filter by the regular session's change and hide a stock that only started moving after the close.
    /// </summary>
    public string Filter(string code) =>
        this.ChangeAbove > 0
            ? $"{(code.Contains("AFTER_HOURS", StringComparison.Ordinal) ? "afterHoursChangePercAbove" : "changePercAbove")}={this.ChangeAbove.ToString(CultureInfo.InvariantCulture)};"
            : "";

    /// <summary>reqScannerSubscription for server versions 143 and up, which take filter options and no version field.</summary>
    public object[] Request(int id, string code) =>
    [
        22, id, this.Rows, "STK", this.Location, code,
        Optional(this.PriceAbove), Optional(this.PriceBelow), this.VolumeAbove > 0 ? this.VolumeAbove : "",
        "", "", "", "", "", "", "", "", "", "", // market cap, ratings, maturity and coupon bounds
        false, "", "", "", // exclude convertibles, option volume, setting pairs, stock type
        this.Filter(code), "", // filter options, subscription options
    ];

    private static object Optional(double value) => double.IsNaN(value) ? "" : value;
}

internal sealed record IbkrConfig(bool Enabled, string Host, int Port, int ClientId, bool AllowLiveAccount, int DepthRows = 50, TapeMode Tape = TapeMode.Ibkr, int TradingClientIdSetting = 0, IbkrScan TopGainers = null, IbkrScan Halts = null)
{
    public static string FilePath => Path.Combine(Log.AppDir, "ibkr.json");

    /// <summary>The trading connection's own client ID: tradingClientId in ibkr.json, else the data one plus 1.</summary>
    public int TradingClientId => this.TradingClientIdSetting > 0 ? this.TradingClientIdSetting : this.ClientId + 1;

    /// <summary>Reads ibkr.json, which is optional. The defaults suit a paper IB Gateway.</summary>
    public static IbkrConfig Load()
    {
        var config = new IbkrConfig(true, "127.0.0.1", 4002, 78, false, TopGainers: new IbkrScan(), Halts: IbkrScan.HaltDefaults);
        try
        {
            if (!File.Exists(FilePath))
                return config;

            var json = JsonNode.Parse(File.ReadAllText(FilePath));
            return new IbkrConfig(
                json["enabled"]?.GetValue<bool>() ?? config.Enabled,
                (string)json["host"] ?? config.Host,
                json["port"]?.GetValue<int>() ?? config.Port,
                json["clientId"]?.GetValue<int>() ?? config.ClientId,
                json["allowLiveAccount"]?.GetValue<bool>() ?? config.AllowLiveAccount,
                json["depthRows"]?.GetValue<int>() ?? config.DepthRows,
                Enum.TryParse<TapeMode>((string)json["tape"], true, out var tape) ? tape : config.Tape,
                json["tradingClientId"]?.GetValue<int>() ?? config.TradingClientIdSetting,
                IbkrScan.From(json["topGainers"]),
                IbkrScan.From(json["halts"], IbkrScan.HaltDefaults));
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or IOException)
        {
            Log.Info($"Couldn't read {FilePath}, using defaults: {ex.Message}");
            return config;
        }
    }
}

internal readonly record struct IbkrDepthRow(double Price, double Size, string Venue);

internal enum IbkrState { Stopped, Connecting, Live, Down, Refused }

/// <summary>Everything here is called on the client's receive thread and must not block.</summary>
internal interface IIbkrSink
{
    /// <summary>A price of -1 means that side has no market: a halt, or a one-sided book.</summary>
    void OnIbkrQuote(string symbol, double bid, double bidSize, double ask, double askSize);

    /// <summary>The lists are reused, so copy what you keep before returning.</summary>
    void OnIbkrDepth(string symbol, List<IbkrDepthRow> bids, List<IbkrDepthRow> asks);

    /// <summary>A print for the tape, already filtered for the tape mode. Its time is whole seconds only.</summary>
    void OnIbkrPrint(string symbol, double price, double size, long unixSeconds, string venue, long arrivalUtcTicks, bool roundLot, bool reopening);

    /// <summary>IBKR's Halted tick on the symbol's quote: 0 trading, 1 a halt, 2 a volatility (LULD) pause.</summary>
    void OnIbkrHalted(string symbol, int halted);

    /// <summary>
    /// The symbol's quote stream was cancelled, so its Halted tick and auction numbers aren't watched any more.
    /// A new stream sends the Halted tick only when it isn't 0, so nothing would say a pause had ended meanwhile.
    /// </summary>
    void OnIbkrQuoteStopped(string symbol);

    /// <summary>The scanner's latest list, best first, in IBKR's symbols. It comes again whenever IBKR updates it.</summary>
    void OnIbkrScan(IReadOnlyList<string> symbols);

    /// <summary>The halted stocks scan's latest list (<see cref="IbkrScan.HaltDefaults"/>), in IBKR's symbols.</summary>
    void OnIbkrHaltScan(IReadOnlyList<string> symbols);

    /// <summary>
    /// One of the listing market's auction numbers, from the symbol's quote: <see cref="IbkrClient.AuctionPriceTick"/>,
    /// <see cref="IbkrClient.AuctionVolumeTick"/> (paired shares) or <see cref="IbkrClient.AuctionImbalanceTick"/>
    /// (shares left over, positive to buy). Returns whether the symbol's book should go out again.
    /// </summary>
    bool OnIbkrAuction(string symbol, int tick, double value);

    /// <summary>
    /// Any print at all on this symbol's tape, including ones that stay off the screen. Carried means
    /// it's on tastytrade's tape as well, so in merged mode it won't come to OnIbkrPrint.
    /// </summary>
    void OnIbkrTapeActive(string symbol, double price, double size, long unixSeconds, bool carried);

    void OnIbkrState(IbkrState state, string detail);

    void OnIbkrProblem(string symbol, string feed, int code, string message);
}

/// <summary>
/// Market data from TWS or IB Gateway over its socket protocol: consolidated quotes, smart depth,
/// and tick-by-tick prints. It can only send the requests in AllowedRequests, and order messages
/// aren't among them. Quotes go to the sink as soon as a burst from the server ends. Depth rows arrive in
/// many small writes, so a book waits a moment to settle and the sink sees whole books rather
/// than each row change. Each depth symbol also has a quote from each venue in VenueTops, which
/// goes into its book as that venue's row.
/// </summary>
internal sealed class IbkrClient
{
    public const string QuoteFeed = "Quote";
    public const string DepthFeed = "Depth";
    public const string TapeFeed = "Tape";
    public const string VenueFeedPrefix = "Venue:";

    // Generic tick 225 on the SMART quote: the listing market's auction numbers.
    private const string AuctionTicks = "225";
    public const int AuctionVolumeTick = 34;
    public const int AuctionPriceTick = 35;
    public const int AuctionImbalanceTick = 36;

    private static readonly string[] VenueFeeds = VenueTops.Venues.Select(v => VenueFeedPrefix + v).ToArray();

    /// <summary>
    /// startApi, reqMarketDataType, reqMktData, cancelMktData, reqMktDepth, cancelMktDepth,
    /// reqTickByTickData, cancelTickByTickData, reqHistoricalData, cancelHistoricalData, reqScannerSubscription,
    /// cancelScannerSubscription.
    /// </summary>
    public static readonly IReadOnlySet<int> AllowedRequests = new HashSet<int> { 71, 59, 1, 2, 10, 11, 97, 98, 20, 25, 22, 23 };

    /// <summary>The scanners' request ids count up from here, far above the streams' and history's, which start at 1.</summary>
    public const int ScanRequestId = 2_000_000_000;

    /// <summary>The scans, which take turns with one in flight at a time: the top gainers, then the halts.</summary>
    public const int GainersScan = 0;
    public const int HaltsScan = 1;

    // The one-off scan waiting for its answer (0 when none is), which scan it is, when it went, and by scan the code
    // last asked for and when the next may go, in Stopwatch stamps.
    private static readonly long ScanGiveUpTicks = Stopwatch.Frequency * 10;
    private int scanId;
    private int scanKind;
    private int lastScanKind = HaltsScan;
    private int scanSeq;
    private long scanSentStamp;
    private readonly long[] nextScanStamps = new long[2];
    private readonly string[] scanCodes = new string[2];
    private Timer scanTimer;

    /// <summary>What decides the session for the scan. The harness sets it; FeedClock won't stray that far.</summary>
    public Func<DateTime> ScanClock { get; set; } = () => DateTime.UtcNow;

    private const string ClientVersions = "v100..176";
    private const int FlushDelayMs = 20;
    private const int AllLast = 2;
    private const int TooManyDepthRequests = 309;
    private const int TooManyTapeRequests = 10190;
    private const int NothingToCancel = 310;

    // Shared with real historical data failures, so it's only ignored on a scan's id.
    private const int ScannerCancelled = 162;

    // "Historical Market Data Service query message": on a scan, either nothing to list or a failure, by its text.
    private const int NoScanItems = 165;
    private const int FirstInfoCode = 2100;
    private const int LastInfoCode = 2999;
    private const int ConnectivityLost = 1100;
    private const int RestoredDataLost = 1101;
    private const int RestoredDataKept = 1102;
    private static readonly long MaxBurstTicks = Stopwatch.Frequency / 10;
    private static readonly long DepthSettleTicks = Stopwatch.Frequency * 30 / 1000;
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan StableAfter = TimeSpan.FromSeconds(60);

    // A cancel frees its slot at once, but a waiting symbol only gets in while fewer than 3 are in
    // use, and that count can be over 3 (see DepthAnswerLimit), so one retry may not be enough. A slot
    // can also open with no cancel of ours, when another client lets go, so also ask now and then.
    private static readonly TimeSpan SlotRetryWindow = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan OccasionalSlotRetry = TimeSpan.FromSeconds(15);

    // Depth requests go out one at a time, the next after a first row or a refusal. Measured
    // 2026-11-05: spaced past Gateway's answer they get exactly 3 slots, but a burst got 5, and then
    // a cancel left a waiting symbol still refused. Empty books never send a row, hence the limit.
    private static readonly TimeSpan DepthAnswerLimit = TimeSpan.FromSeconds(1);

    // A chart that gets no answer falls back to tastytrade's bars, so it shouldn't wait long.
    private static readonly TimeSpan HistoryTimeout = TimeSpan.FromSeconds(10);

    // IBKR's limits, from its documentation as remembered and not tested: 6 requests per contract
    // in 2 seconds, and no identical request within 15 seconds. A chart open asks for several periods
    // within a second, and sometimes the same one twice.
    private static readonly TimeSpan HistorySpacing = TimeSpan.FromMilliseconds(400);
    private static readonly TimeSpan IdenticalRequestWindow = TimeSpan.FromSeconds(15);
    private static readonly double[] BackoffSeconds = [0.5, 1, 2, 4, 8, 15, 30];

    private readonly IbkrConfig config;
    private readonly FeedHealth health = new();
    private readonly SubscriptionBook book = new();
    private readonly SemaphoreSlim sendGate = new(1, 1);
    private readonly ConcurrentDictionary<int, Stream> streams = new();
    private readonly ConcurrentDictionary<FeedKey, Stream> streamsByKey = new();
    private readonly ConcurrentQueue<FeedKey> replays = new();
    private readonly ConcurrentQueue<string> bookRefreshes = new();
    private readonly ConcurrentQueue<string> quoteStops = new();
    private readonly ConcurrentDictionary<string, DateTime> slotRetryUntilUtc = new();
    private readonly ConcurrentDictionary<FeedKey, bool> slotRefusalsLogged = new();
    private readonly List<Stream> dirtyQuotes = new();
    private readonly List<Stream> dirtyDepth = new();
    private readonly List<IbkrDepthRow> topBids = new();
    private readonly List<IbkrDepthRow> topAsks = new();
    private readonly List<IbkrDepthRow> bookBids = new();
    private readonly List<IbkrDepthRow> bookAsks = new();
    private readonly List<string> venueNames = new();
    private readonly HashSet<int> codesLogged = new();
    private readonly object depthGate = new();
    private readonly Queue<Stream> depthQueue = new();
    private readonly SemaphoreSlim historyGate = new(1, 1);
    private readonly ConcurrentDictionary<int, TaskCompletionSource<List<BarRow>>> historyWaits = new();
    private readonly Dictionary<string, (DateTime AtUtc, List<BarRow> Bars)> recentBars = new();

    private DateTime nextHistoryRequestUtc;
    private volatile Stream depthAwaited;
    private IIbkrSink sink;
    private CancellationTokenSource stopCts;
    private CancellationTokenSource connectionCts;
    private Task runTask;
    private NetworkStream net;
    private Timer flushTimer;
    private Timer housekeepingTimer;
    private volatile IbkrState state = IbkrState.Stopped;
    private volatile bool refused;
    private volatile bool subscribed;
    private DateTime resyncUntilUtc;
    private DateTime liveSinceUtc;
    private DateTime nextStatsUtc;
    private DateTime nextOccasionalRetryUtc;
    private int nextId = 1000;
    private long lastQuoteFlushStamp;

    // Written on the receive thread, read by the timer.
    private long quoteUpdates;
    private long venueQuoteUpdates;
    private long auctionUpdates;
    private long depthUpdates;
    private long depthDeliveries;
    private long maxFlushMicros;
    private long printsAdded;
    private long printsCarried;
    private long printsOffMarket;
    private long frameArrivalUtcTicks;

    public IbkrClient(IbkrConfig config)
    {
        this.config = config;
        this.book.Pin(new FeedKey(QuoteFeed, FeedHealth.CanarySymbol));
    }

    public IbkrState State => this.state;

    public TapeMode TapeMode => this.config.Tape;

    /// <summary>Requests sent since the start, so tests can see that churn costs nothing.</summary>
    public long RequestsSent { get; private set; }

    /// <summary>Tastytrade symbols write share classes as BRK/B; IBKR writes BRK B.</summary>
    public static string ToIbkrSymbol(string symbol) => symbol.Replace('/', ' ');

    public static string FromIbkrSymbol(string symbol) => symbol.Replace(' ', '/');

    public void SetSink(IIbkrSink newSink) => Volatile.Write(ref this.sink, newSink);

    public void Start()
    {
        Log.Info($"[ibkr] tape mode: {this.config.Tape}");
        this.stopCts = new CancellationTokenSource();
        this.flushTimer = new Timer(this.OnFlushTimer, null, Timeout.Infinite, Timeout.Infinite);
        this.housekeepingTimer = new Timer(this.OnHousekeeping, null, 1000, 1000);
        if (this.config.TopGainers != null || this.config.Halts != null)
            this.scanTimer = new Timer(this.OnScanTimer, null, 250, 250);
        this.nextStatsUtc = DateTime.UtcNow.AddMinutes(1);
        this.runTask = Task.Run(() => this.RunAsync(this.stopCts.Token));
    }

    public async Task StopAsync()
    {
        if (this.stopCts == null)
            return;

        this.housekeepingTimer?.Dispose();
        this.scanTimer?.Dispose();
        this.flushTimer?.Dispose();
        this.stopCts.Cancel();
        try
        {
            await this.runTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }

        this.AbandonHistoryWaits();
        this.SetState(IbkrState.Stopped, "stopped");
    }

    /// <summary>
    /// Chart bars for one span, oldest first. Empty if IBKR can't supply them right now for any
    /// reason, because the chart then shows tastytrade's. Requests run one at a time.
    /// </summary>
    public async Task<IReadOnlyList<BarRow>> LoadBarsAsync(string symbol, string barSize, string duration, string endUtc, CancellationToken ct)
    {
        if (this.state != IbkrState.Live)
            return [];

        string ibkrSymbol = ToIbkrSymbol(symbol);
        string key = $"{ibkrSymbol}|{barSize}|{duration}|{endUtc}";
        await this.historyGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var now = DateTime.UtcNow;
            foreach (var old in this.recentBars.Where(r => now - r.Value.AtUtc >= IdenticalRequestWindow).Select(r => r.Key).ToList())
                this.recentBars.Remove(old);

            if (this.recentBars.TryGetValue(key, out var recent))
                return recent.Bars;

            var wait = this.nextHistoryRequestUtc - now;
            if (wait > TimeSpan.Zero)
                await Task.Delay(wait, ct).ConfigureAwait(false);
            this.nextHistoryRequestUtc = DateTime.UtcNow + HistorySpacing;

            int id = Interlocked.Increment(ref this.nextId);
            var answer = new TaskCompletionSource<List<BarRow>>(TaskCreationOptions.RunContinuationsAsynchronously);
            this.historyWaits[id] = answer;
            var clock = Stopwatch.StartNew();
            try
            {
                await this.SendAsync(CancellationToken.None, [20, id, .. Contract(ibkrSymbol), 0, endUtc, barSize, duration, 0, "TRADES", 2, 0, ""]).ConfigureAwait(false);
                var bars = await answer.Task.WaitAsync(HistoryTimeout, ct).ConfigureAwait(false);
                if (bars == null)
                    return [];

                Log.Info($"[ibkr] bars {ibkrSymbol} {barSize} {duration}: {bars.Count} in {clock.ElapsedMilliseconds} ms");
                this.recentBars[key] = (DateTime.UtcNow, bars);
                return bars;
            }
            catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
            {
                if (ex is TimeoutException)
                    Log.Info($"[ibkr] bars {ibkrSymbol} {barSize} {duration}: no answer in {HistoryTimeout.TotalSeconds:F0} s");
                await this.SendAsync(CancellationToken.None, 25, 1, id).ConfigureAwait(false);
                return [];
            }
            finally
            {
                this.historyWaits.TryRemove(id, out _);
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException or OperationCanceledException)
        {
            return [];
        }
        finally
        {
            this.historyGate.Release();
        }
    }

    public void SetQuote(string symbol, bool on) => this.SetInterest(new FeedKey(QuoteFeed, ToIbkrSymbol(symbol)), on);

    public void SetDepth(string symbol, bool on)
    {
        // The venue quotes take no depth slot, only a market data line each.
        string ibkrSymbol = ToIbkrSymbol(symbol);
        this.SetInterest(new FeedKey(DepthFeed, ibkrSymbol), on);
        foreach (var feed in VenueFeeds)
            this.SetInterest(new FeedKey(feed, ibkrSymbol), on);
    }

    public void SetTape(string symbol, bool on)
    {
        // Tick-by-tick slots are scarce, so a tape that doesn't use them doesn't take them.
        if (this.config.Tape != TapeMode.Tastytrade)
            this.SetInterest(new FeedKey(TapeFeed, ToIbkrSymbol(symbol)), on);
    }

    /// <summary>Requests refused because every slot is taken, still waiting for one.</summary>
    public IReadOnlyList<string> WaitingForSlot(string feed) =>
        this.streams.Values.Where(s => s.WaitingForSlot && s.Key.EventType == feed).Select(s => s.Key.Symbol).ToList();

    /// <summary>Shares on one symbol's IBKR tape so far, by what happened to them.</summary>
    public (double Added, double Carried, double OffMarket) TapeShares(string symbol) =>
        this.streamsByKey.TryGetValue(new FeedKey(TapeFeed, ToIbkrSymbol(symbol)), out var stream)
            ? (stream.SharesAdded, stream.SharesCarried, stream.SharesOffMarket)
            : default;

    /// <summary>
    /// A new vendor instance is about to re-subscribe what it wants. Forget the current wishes,
    /// and don't act on the difference until it has had time to say.
    /// </summary>
    public void BeginResync(TimeSpan window)
    {
        this.resyncUntilUtc = DateTime.UtcNow + window;
        this.book.ClearUnpinned();
        this.flushTimer?.Change(window, Timeout.InfiniteTimeSpan);
    }

    public void ForceReconnect(string reason)
    {
        Log.Info($"[ibkr] forcing reconnect: {reason}");
        try
        {
            this.connectionCts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private static string Clip(string text) => text.Length <= 220 ? text : text[..220] + "...";

    private static object[] Contract(string symbol, string exchange = "SMART") => [0, symbol, "STK", "", "0.0", "", "", exchange, "", "USD", "", ""];

    private static bool IsVenueFeed(string feed) => feed.StartsWith(VenueFeedPrefix, StringComparison.Ordinal);

    private void SetInterest(FeedKey key, bool on)
    {
        this.book.Set(key, on);

        // Already streaming means no fresh snapshot is coming, so a new reader gets the current
        // state. A tape has no state to give: prints that have gone are gone.
        if (on && key.EventType != TapeFeed && this.streamsByKey.ContainsKey(key))
            this.replays.Enqueue(key);

        if (DateTime.UtcNow >= this.resyncUntilUtc)
            this.flushTimer?.Change(FlushDelayMs, Timeout.Infinite);
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
                this.SetState(IbkrState.Connecting, "connecting");
                reason = await this.RunOneConnectionAsync(stop).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested)
            {
                break;
            }
            catch (OperationCanceledException)
            {
                reason = "reconnecting";
            }
            catch (Exception ex)
            {
                // Whatever went wrong, including in the sink, the answer is to reconnect. Letting
                // it escape would end this loop and leave IBKR silently dead.
                reason = ex.GetBaseException().Message;
                if (ex is not (SocketException or IOException or ObjectDisposedException))
                    Log.Info($"[ibkr] unexpected failure: {ex}");
            }

            if (stop.IsCancellationRequested)
                break;

            this.SetState(this.refused ? IbkrState.Refused : IbkrState.Down, reason);

            if (this.liveSinceUtc != default && DateTime.UtcNow - this.liveSinceUtc > StableAfter)
                attempt = 0;
            this.liveSinceUtc = default;

            double delay = this.refused ? BackoffSeconds[^1] : BackoffSeconds[Math.Min(attempt, BackoffSeconds.Length - 1)];
            attempt++;

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(delay), stop).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task<string> RunOneConnectionAsync(CancellationToken stop)
    {
        this.streams.Clear();
        this.streamsByKey.Clear();
        this.dirtyQuotes.Clear();
        this.dirtyDepth.Clear();
        this.replays.Clear();
        this.bookRefreshes.Clear();

        // A scan in flight ends with the socket; the next starts once this connection is subscribed.
        Volatile.Write(ref this.scanId, 0);
        Volatile.Write(ref this.nextScanStamps[GainersScan], 0);
        Volatile.Write(ref this.nextScanStamps[HaltsScan], 0);
        this.slotRetryUntilUtc.Clear();
        this.slotRefusalsLogged.Clear();
        lock (this.depthGate)
        {
            this.depthQueue.Clear();
            this.depthAwaited = null;
        }

        this.AbandonHistoryWaits();
        this.refused = false;
        this.subscribed = false;

#if TESTHOOKS
        if (File.Exists(Path.Combine(Log.AppDir, "test-ibkr-block")))
            throw new IOException("test outage");
#endif

        using var connection = CancellationTokenSource.CreateLinkedTokenSource(stop);
        this.connectionCts = connection;
        using var tcp = new TcpClient { NoDelay = true };
        using (var connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(connection.Token))
        {
            connectTimeout.CancelAfter(ConnectTimeout);
            await tcp.ConnectAsync(this.config.Host, this.config.Port, connectTimeout.Token).ConfigureAwait(false);
        }

        var stream = tcp.GetStream();
        this.net = stream;
        try
        {
            var prefix = "API\0"u8.ToArray();
            await stream.WriteAsync(prefix, connection.Token).ConfigureAwait(false);
            await this.WriteFrameAsync(stream, Encoding.ASCII.GetBytes(ClientVersions), connection.Token).ConfigureAwait(false);
            await this.SendAsync(connection.Token, 71, 2, this.config.ClientId, "").ConfigureAwait(false);
            return await this.ReceiveLoopAsync(stream, connection.Token).ConfigureAwait(false);
        }
        finally
        {
            this.net = null;
            this.connectionCts = null;
        }
    }

    /// <summary>The connection these were asked on has gone, so no answer is coming.</summary>
    private void AbandonHistoryWaits()
    {
        foreach (var wait in this.historyWaits.Values)
            wait.TrySetResult(null);
    }

    private void OnAccount(string accounts)
    {
        bool paper = accounts.StartsWith('D');
        Log.Info($"[ibkr] connected, {(paper ? "paper" : "live")} account");
        if (!paper && !this.config.AllowLiveAccount)
        {
            this.refused = true;
            Log.Info($"[ibkr] refusing a live account until allowLiveAccount is set in {IbkrConfig.FilePath}");
            this.ForceReconnect("live account not allowed");
            return;
        }

        this.health.Reset();
        if (!this.subscribed)
            _ = this.AfterAccountAsync();
    }

    private async Task AfterAccountAsync()
    {
        try
        {
            await this.SendAsync(CancellationToken.None, 59, 1, 1).ConfigureAwait(false);

            // From here on the flush timer handles changes, including any made during this loop.
            var everything = this.book.TakeAll();
            this.subscribed = true;
            foreach (var key in everything)
                await this.RequestAsync(key).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
            Log.Info($"[ibkr] subscribing failed: {ex.Message}");
        }
    }

    #endregion Connection

    #region Requests

    private void OnFlushTimer(object timerState) => _ = this.FlushSubscriptionsAsync();

    private async Task FlushSubscriptionsAsync()
    {
        try
        {
            // Before the first full subscribe, the diff would just request everything twice.
            if (!this.subscribed || this.net == null)
                return;

            if (!this.book.TakeDiff(out var add, out var remove))
                return;

            Log.Info($"[ibkr] subscriptions: +{add.Count} -{remove.Count}");
            foreach (var key in remove)
            {
                // A slot is about to open for a request that was refused earlier, so housekeeping
                // asks again. Dropping a request that never had a slot opens nothing.
                if (await this.CancelAsync(key).ConfigureAwait(false))
                {
                    this.slotRetryUntilUtc[key.EventType] = DateTime.UtcNow + SlotRetryWindow;
                    this.slotRefusalsLogged.Clear();
                }
            }

            foreach (var key in add)
                await this.RequestAsync(key).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException or OperationCanceledException)
        {
            Log.Info($"[ibkr] subscription flush failed: {ex.Message}");
        }
    }

    private async Task RetryWaitingAsync(string feed)
    {
        try
        {
            foreach (var waiting in this.streams.Values.Where(s => s.WaitingForSlot && s.Key.EventType == feed).OrderBy(s => s.Id).ToList())
            {
                // Gateway treats a refused request's id as taken, so ask under a new one.
                this.streams.TryRemove(waiting.Id, out _);
                await this.RequestAsync(waiting.Key).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException or OperationCanceledException)
        {
            Log.Info($"[ibkr] slot retry failed: {ex.Message}");
        }
    }

    private async Task RequestAsync(FeedKey key)
    {
        var stream = new Stream(Interlocked.Increment(ref this.nextId), key);
        this.streams[stream.Id] = stream;
        this.streamsByKey[key] = stream;

        if (key.EventType == QuoteFeed)
            await this.SendAsync(CancellationToken.None, [1, 11, stream.Id, .. Contract(key.Symbol), false, key.Symbol == FeedHealth.CanarySymbol ? "" : AuctionTicks, false, false, ""]).ConfigureAwait(false);
        else if (IsVenueFeed(key.EventType))
            await this.SendAsync(CancellationToken.None, [1, 11, stream.Id, .. Contract(key.Symbol, key.EventType[VenueFeedPrefix.Length..]), false, "", false, false, ""]).ConfigureAwait(false);
        else if (key.EventType == DepthFeed)
            await this.QueueDepthRequestAsync(stream).ConfigureAwait(false);
        else
            await this.SendAsync(CancellationToken.None, [97, stream.Id, .. Contract(key.Symbol), "AllLast", 0, false]).ConfigureAwait(false);
    }

    private Task QueueDepthRequestAsync(Stream stream)
    {
        stream.DepthQueued = true;
        lock (this.depthGate)
            this.depthQueue.Enqueue(stream);

        return this.SendNextDepthAsync();
    }

    private async Task SendNextDepthAsync()
    {
        Stream next = null;
        lock (this.depthGate)
        {
            if (this.depthAwaited != null)
                return;

            while (next == null && this.depthQueue.TryDequeue(out var queued))
            {
                if (this.streams.ContainsKey(queued.Id))
                    next = queued;
            }

            this.depthAwaited = next;
        }

        if (next == null)
            return;

        next.DepthQueued = false;
        _ = Task.Delay(DepthAnswerLimit).ContinueWith(_ => this.OnDepthAnswered(next), TaskScheduler.Default);
        try
        {
            await this.SendAsync(CancellationToken.None, [10, 5, next.Id, .. Contract(next.Key.Symbol), this.config.DepthRows, true, ""]).ConfigureAwait(false);

            // Dropped while this was on its way, so its cancel may have gone first and missed it.
            if (!this.streams.ContainsKey(next.Id))
                await this.SendAsync(CancellationToken.None, 11, 1, next.Id, true).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException or OperationCanceledException)
        {
            Log.Info($"[ibkr] depth request failed: {ex.Message}");
        }
    }

    /// <summary>Lets the next depth request go. Only the one being waited on counts, so a late call does nothing.</summary>
    private void OnDepthAnswered(Stream stream)
    {
        lock (this.depthGate)
        {
            if (!ReferenceEquals(this.depthAwaited, stream))
                return;

            this.depthAwaited = null;
        }

        _ = this.SendNextDepthAsync();
    }

    /// <summary>Returns whether a live request was cancelled, which is what releases a slot.</summary>
    private async Task<bool> CancelAsync(FeedKey key)
    {
        if (!this.streamsByKey.TryRemove(key, out var stream))
            return false;

        this.streams.TryRemove(stream.Id, out _);

        // Its ticks stop here. The receive thread tells the sink before it handles anything read after this.
        if (key.EventType == QuoteFeed)
            this.quoteStops.Enqueue(key.Symbol);

        if (stream.Failed || stream.WaitingForSlot || stream.DepthQueued)
            return false;

        if (key.EventType == QuoteFeed || IsVenueFeed(key.EventType))
        {
            await this.SendAsync(CancellationToken.None, 2, 2, stream.Id).ConfigureAwait(false);
        }
        else if (key.EventType == DepthFeed)
        {
            await this.SendAsync(CancellationToken.None, 11, 1, stream.Id, true).ConfigureAwait(false);
            this.OnDepthAnswered(stream);
        }
        else
        {
            await this.SendAsync(CancellationToken.None, 98, stream.Id).ConfigureAwait(false);
        }

        return true;
    }

    private async Task SendAsync(CancellationToken ct, params object[] fields)
    {
        int messageId = Convert.ToInt32(fields[0], CultureInfo.InvariantCulture);
        if (!AllowedRequests.Contains(messageId))
            throw new InvalidOperationException($"IBKR message {messageId} isn't allowed");

        var target = this.net ?? throw new IOException("not connected");
        var text = new StringBuilder();
        foreach (var field in fields)
            text.Append(field is bool b ? (b ? "1" : "0") : Convert.ToString(field, CultureInfo.InvariantCulture)).Append('\0');

        await this.WriteFrameAsync(target, Encoding.ASCII.GetBytes(text.ToString()), ct).ConfigureAwait(false);
        this.RequestsSent++;
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

    #endregion Requests

    #region Data path

    private async Task<string> ReceiveLoopAsync(NetworkStream stream, CancellationToken ct)
    {
        var buffer = new byte[256 * 1024];
        int start = 0;
        int end = 0;
        bool greeted = false;

        while (true)
        {
            // First, so a new stream's first Halted tick comes after the old stream's stop, never before.
            while (this.quoteStops.TryDequeue(out var stopped))
                Volatile.Read(ref this.sink)?.OnIbkrQuoteStopped(stopped);

            while (end - start >= 4)
            {
                int length = BinaryPrimitives.ReadInt32BigEndian(buffer.AsSpan(start));
                if (end - start - 4 < length)
                    break;

                var frame = buffer.AsSpan(start + 4, length);
                if (greeted)
                    this.HandleFrame(frame);
                else
                    greeted = true; // The first frame is the server's version and time.
                start += 4 + length;
            }

            if (start > 0)
            {
                Buffer.BlockCopy(buffer, start, buffer, 0, end - start);
                end -= start;
                start = 0;
            }

            if (end == buffer.Length)
                Array.Resize(ref buffer, buffer.Length * 2);

            long now = Stopwatch.GetTimestamp();
            while (this.replays.TryDequeue(out var replay))
            {
                if (this.streamsByKey.TryGetValue(replay, out var replayed))
                    this.MarkDirty(replayed, now);
            }

            while (this.bookRefreshes.TryDequeue(out var refresh))
                this.MarkBookDirty(refresh);

            // Nothing more is waiting, so the burst is over and the sink can have the quotes.
            if (this.dirtyQuotes.Count > 0 && (!stream.DataAvailable || now - this.lastQuoteFlushStamp > MaxBurstTicks))
                this.FlushQuotes(now);
            this.FlushSettledDepth(now);

            int read;
            if (this.dirtyDepth.Count == 0)
            {
                read = await stream.ReadAsync(buffer.AsMemory(end), ct).ConfigureAwait(false);
            }
            else
            {
                // A book is settling, so watch the clock as well as the socket.
                var pending = stream.ReadAsync(buffer.AsMemory(end), ct).AsTask();
                while (this.dirtyDepth.Count > 0 && !pending.IsCompleted)
                {
                    long waitTicks = this.dirtyDepth[0].DueStamp - Stopwatch.GetTimestamp();
                    int waitMs = Math.Max(1, (int)(waitTicks * 1000 / Stopwatch.Frequency) + 1);
                    await Task.WhenAny(pending, Task.Delay(waitMs, ct)).ConfigureAwait(false);
                    this.FlushSettledDepth(Stopwatch.GetTimestamp());
                }

                read = await pending.ConfigureAwait(false);
            }

            if (read == 0)
                return "Gateway closed the socket";
            end += read;
            this.frameArrivalUtcTicks = FeedClock.UtcNowTicks;
        }
    }

    private void FlushQuotes(long stamp)
    {
        var target = Volatile.Read(ref this.sink);
        foreach (var stream in this.dirtyQuotes)
        {
            stream.IsDirty = false;

            // Zero means this side hasn't been heard from yet; a side with no market is -1.
            if (target != null && stream.Bid != 0 && stream.Ask != 0)
                target.OnIbkrQuote(stream.Key.Symbol, stream.Bid, stream.BidSize, stream.Ask, stream.AskSize);
        }

        this.dirtyQuotes.Clear();
        this.lastQuoteFlushStamp = stamp;
        this.NoteFlushTime(stamp, "ibkr quotes");
    }

    private void FlushSettledDepth(long now)
    {
        // Oldest first, so the ones that are due are always at the front.
        int due = 0;
        while (due < this.dirtyDepth.Count && this.dirtyDepth[due].DueStamp <= now)
            due++;
        if (due == 0)
            return;

        var target = Volatile.Read(ref this.sink);
        for (int i = 0; i < due; i++)
        {
            var stream = this.dirtyDepth[i];
            stream.IsDirty = false;
            if (target != null)
            {
                this.AddVenueTops(stream);
                target.OnIbkrDepth(stream.Key.Symbol, this.bookBids, this.bookAsks);
            }

            this.depthDeliveries++;
        }

        this.dirtyDepth.RemoveRange(0, due);
        this.NoteFlushTime(now, "ibkr books");
    }

    /// <summary>Fills bookBids and bookAsks with the smart book plus each venue's own quote.</summary>
    private void AddVenueTops(Stream depth)
    {
        this.topBids.Clear();
        this.topAsks.Clear();
        for (int i = 0; i < VenueFeeds.Length; i++)
        {
            if (this.streamsByKey.TryGetValue(new FeedKey(VenueFeeds[i], depth.Key.Symbol), out var venue) && !venue.Failed)
            {
                this.topBids.Add(new IbkrDepthRow(venue.Bid, venue.BidSize, VenueTops.Venues[i]));
                this.topAsks.Add(new IbkrDepthRow(venue.Ask, venue.AskSize, VenueTops.Venues[i]));
            }
        }

        VenueTops.Merge(depth.Bids, this.topBids, true, this.bookBids);
        VenueTops.Merge(depth.Asks, this.topAsks, false, this.bookAsks);
    }

    private void NoteFlushTime(long stamp, string what)
    {
        long micros = (Stopwatch.GetTimestamp() - stamp) * 1_000_000 / Stopwatch.Frequency;
        if (micros > this.maxFlushMicros)
            this.maxFlushMicros = micros;
        StallWatch.Note(what, stamp);
    }

    private void MarkDirty(Stream stream, long now)
    {
        if (IsVenueFeed(stream.Key.EventType))
        {
            // A venue's quote is part of its symbol's book, so the book goes out again.
            this.MarkBookDirty(stream.Key.Symbol);
            return;
        }

        if (stream.IsDirty)
            return;
        stream.IsDirty = true;

        if (stream.Key.EventType == QuoteFeed)
        {
            this.dirtyQuotes.Add(stream);
        }
        else
        {
            stream.DueStamp = now + DepthSettleTicks;
            this.dirtyDepth.Add(stream);
        }
    }

    /// <summary>
    /// From any thread: the symbol's book goes out again from the receive thread, the next time data arrives,
    /// for rows that change with time rather than with the market (the Res row's countdown).
    /// </summary>
    public void RefreshBook(string symbol) => this.bookRefreshes.Enqueue(symbol);

    /// <summary>Sends the symbol's book again. Not before it has a row: alone, added rows would pass for the whole market.</summary>
    private void MarkBookDirty(string symbol)
    {
        if (this.streamsByKey.TryGetValue(new FeedKey(DepthFeed, symbol), out var depth)
            && depth.HasBook && !depth.Failed && !depth.WaitingForSlot)
            this.MarkDirty(depth, Stopwatch.GetTimestamp());
    }

    private void HandleFrame(ReadOnlySpan<byte> frame)
    {
        var f = new FieldReader(frame);
        switch (f.Int())
        {
            case 99:
                this.OnTickByTick(ref f);
                break;
            case 1:
                f.Skip();
                this.OnTickPrice(ref f);
                break;
            case 2:
                f.Skip();
                this.OnTickSize(ref f);
                break;
            case 45:
                f.Skip();
                this.OnTickGeneric(ref f);
                break;
            case 13:
                f.Skip();
                this.OnDepth(ref f, true);
                break;
            case 12:
                f.Skip();
                this.OnDepth(ref f, false);
                break;
            case 4:
                f.Skip();
                this.OnError(ref f);
                break;
            case 15:
                f.Skip();
                this.OnAccount(f.Text());
                break;
            case 17:
                this.OnHistoricalData(ref f);
                break;
            case 20:
                f.Skip();
                this.OnScannerData(ref f);
                break;
        }
    }

    /// <summary>
    /// Every 250 ms: starts the next one-off scan once the last has answered, the gainers and the halts in turn,
    /// each when its own interval is up. One that never answers is cancelled after 10 seconds, so a lost answer
    /// doesn't stop the lists.
    /// </summary>
    private void OnScanTimer(object timerState)
    {
        if (!this.subscribed || this.net == null)
            return;

        long now = Stopwatch.GetTimestamp();
        int waiting = Volatile.Read(ref this.scanId);
        if (waiting != 0)
        {
            if (now - Volatile.Read(ref this.scanSentStamp) > ScanGiveUpTicks && Interlocked.CompareExchange(ref this.scanId, 0, waiting) == waiting)
                _ = this.CancelScanAsync(waiting);
            return;
        }

        for (int turn = 1; turn <= 2; turn++)
        {
            int kind = (this.lastScanKind + turn) % 2;
            var scan = kind == GainersScan ? this.config.TopGainers : this.config.Halts;
            if (scan == null || now < Volatile.Read(ref this.nextScanStamps[kind]))
                continue;

            var utc = this.ScanClock();
            this.lastScanKind = kind;
            Volatile.Write(ref this.nextScanStamps[kind], now + (long)(scan.IntervalAt(utc) * Stopwatch.Frequency));
            _ = this.StartScanAsync(scan, kind, scan.CodeAt(utc), now);
            return;
        }
    }

    private async Task StartScanAsync(IbkrScan scan, int kind, string code, long now)
    {
        int id = ScanRequestId + Interlocked.Increment(ref this.scanSeq);
        Volatile.Write(ref this.scanSentStamp, now);
        Volatile.Write(ref this.scanKind, kind);
        Volatile.Write(ref this.scanId, id);
        try
        {
            await this.SendAsync(CancellationToken.None, scan.Request(id, code)).ConfigureAwait(false);
            if (code != this.scanCodes[kind])
                Log.Info($"[scan] asking IBKR for {code} on {scan.Location} every {scan.IntervalSeconds:0.#} s: {scan.Rows} rows, {scan.Filter(code)} price {scan.PriceAbove}-{scan.PriceBelow}, volume over {scan.VolumeAbove:#,0}");
            this.scanCodes[kind] = code;
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
            Interlocked.CompareExchange(ref this.scanId, 0, id);
            Log.Info($"[scan] couldn't send the scan: {ex.Message}");
        }
    }

    private async Task CancelScanAsync(int id)
    {
        try
        {
            await this.SendAsync(CancellationToken.None, 23, 1, id).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
            // The socket went; the scan went with it.
        }
    }

    /// <summary>A one-off scan's answer: it's cancelled at once, and the next may go when its interval is up.</summary>
    private void OnScannerData(ref FieldReader f)
    {
        int id = f.Int();
        int kind = Volatile.Read(ref this.scanKind);
        if (id != Volatile.Read(ref this.scanId) || Interlocked.CompareExchange(ref this.scanId, 0, id) != id)
            return;

        _ = this.CancelScanAsync(id);

        int count = f.Int();
        var symbols = new List<string>(Math.Max(count, 0));
        for (int i = 0; i < count; i++)
        {
            f.Skip(); // Rank: the rows come in order.
            f.Skip(); // Contract id.
            string symbol = f.Text();
            bool stock = f.Text() == "STK";

            // Expiry, strike, right, exchange, currency, local symbol, market, trading class, distance,
            // benchmark, projection and legs.
            for (int k = 0; k < 12; k++)
                f.Skip();
            if (stock)
                symbols.Add(symbol);
        }

        this.DeliverScan(kind, symbols);
    }

    private void DeliverScan(int kind, IReadOnlyList<string> symbols)
    {
        var current = Volatile.Read(ref this.sink);
        if (kind == HaltsScan)
            current?.OnIbkrHaltScan(symbols);
        else
            current?.OnIbkrScan(symbols);
    }

    private void OnHistoricalData(ref FieldReader f)
    {
        if (!this.historyWaits.TryGetValue(f.Int(), out var wait))
            return;

        // The start and end dates, which come before server version 196. ClientVersions stops at 176.
        f.Skip();
        f.Skip();
        int count = f.Int();
        var bars = new List<BarRow>(Math.Max(count, 0));
        for (int i = 0; i < count; i++)
        {
            long seconds = f.Long();
            double open = f.Double();
            double high = f.Double();
            double low = f.Double();
            double close = f.Double();
            double volume = f.Double();
            f.Skip(); // Average price.
            bars.Add(new BarRow(seconds * 1000, open, high, low, close, volume, f.Int()));
        }

        wait.TrySetResult(bars);
    }

    private void OnTickByTick(ref FieldReader f)
    {
        if (!this.streams.TryGetValue(f.Int(), out var stream) || f.Int() != AllLast)
            return;

        long unixSeconds = f.Long();
        double price = f.Double();
        double size = f.Double();
        f.Skip(); // Attribute mask.
        var venue = f.Next();
        var conditions = f.Next();

        if (double.IsNaN(price) || double.IsNaN(size))
            return;

#if TESTHOOKS
        if (File.Exists(Path.Combine(Log.AppDir, "test-ibkr-tape-mute")))
            return;
#endif

        this.OnData();

        bool carried = this.config.Tape == TapeMode.Merged && PrintFilter.IsCarriedByTastytrade(venue);
        Volatile.Read(ref this.sink)?.OnIbkrTapeActive(stream.Key.Symbol, price, size, unixSeconds, carried);

        if (carried)
        {
            this.printsCarried++;
            stream.SharesCarried += size;
        }
        else if (PrintFilter.IsOffMarket(conditions))
        {
            this.printsOffMarket++;
            stream.SharesOffMarket += size;
        }
        else
        {
            this.printsAdded++;
            stream.SharesAdded += size;
            Volatile.Read(ref this.sink)?.OnIbkrPrint(stream.Key.Symbol, price, size, unixSeconds, this.InternVenue(venue), this.frameArrivalUtcTicks,
                !PrintFilter.IsOddLot(conditions), PrintFilter.IsReopening(conditions));
        }
    }

    private void OnTickPrice(ref FieldReader f)
    {
        if (!this.streams.TryGetValue(f.Int(), out var stream))
            return;

        int tick = f.Int();
        double price = f.Double();
        double size = f.Double();
        if (tick == AuctionPriceTick)
        {
            this.OnAuction(stream, tick, price);
            return;
        }

        if (tick == 1)
        {
            stream.Bid = price;
            if (!double.IsNaN(size))
                stream.BidSize = size;
        }
        else if (tick == 2)
        {
            stream.Ask = price;
            if (!double.IsNaN(size))
                stream.AskSize = size;
        }
        else
        {
            return;
        }

        this.MarkDirty(stream, 0);
        this.OnData();
        if (stream.Key.EventType != QuoteFeed)
        {
            this.venueQuoteUpdates++;
            return;
        }

        this.quoteUpdates++;
    }

    private void OnTickSize(ref FieldReader f)
    {
        if (!this.streams.TryGetValue(f.Int(), out var stream))
            return;

        int tick = f.Int();
        double size = f.Double();
        if (tick is AuctionVolumeTick or AuctionImbalanceTick)
        {
            this.OnAuction(stream, tick, size);
            return;
        }

        if (tick == 0)
            stream.BidSize = size;
        else if (tick == 3)
            stream.AskSize = size;
        else
            return;

        this.MarkDirty(stream, 0);
        this.OnData();
    }

    /// <summary>
    /// The listing market's auction numbers (generic tick 225), about once a second while an auction is
    /// forming: the opening and closing crosses and, the part we use, the reopening after a halt or pause.
    /// </summary>
    private void OnAuction(Stream stream, int tick, double value)
    {
        if (stream.Key.EventType != QuoteFeed || double.IsNaN(value))
            return;

        this.auctionUpdates++;
        if (Volatile.Read(ref this.sink)?.OnIbkrAuction(stream.Key.Symbol, tick, value) == true)
            this.MarkBookDirty(stream.Key.Symbol);
    }

    private void OnTickGeneric(ref FieldReader f)
    {
        const int Halted = 49;
        if (!this.streams.TryGetValue(f.Int(), out var stream) || stream.Key.EventType != QuoteFeed || f.Int() != Halted)
            return;

        double value = f.Double();
        if (!double.IsNaN(value))
            Volatile.Read(ref this.sink)?.OnIbkrHalted(stream.Key.Symbol, (int)value);
    }

    /// <summary>Any quote, depth change or print, on any symbol, shows the connection is delivering.</summary>
    private void OnData()
    {
        this.health.OnData(Stopwatch.GetTimestamp());
        if (this.state != IbkrState.Live)
        {
            this.liveSinceUtc = DateTime.UtcNow;
            this.SetState(IbkrState.Live, "data is flowing");
        }
    }

    private void OnDepth(ref FieldReader f, bool hasVenue)
    {
        if (!this.streams.TryGetValue(f.Int(), out var stream))
            return;

        int position = f.Int();
        string venue = hasVenue ? this.InternVenue(f.Next()) : string.Empty;
        int operation = f.Int();
        int side = f.Int();
        double price = f.Double();
        double size = f.Double();

        var rows = side == 1 ? stream.Bids : stream.Asks;
        if (operation == 0 && position <= rows.Count)
            rows.Insert(position, new IbkrDepthRow(price, size, venue));
        else if (operation == 1 && position < rows.Count)
            rows[position] = new IbkrDepthRow(price, size, venue);
        else if (operation == 2 && position < rows.Count)
            rows.RemoveAt(position);

        stream.HasBook = true;
        this.depthUpdates++;
        this.MarkDirty(stream, Stopwatch.GetTimestamp());
        this.OnData();
        if (ReferenceEquals(this.depthAwaited, stream))
            this.OnDepthAnswered(stream);
    }

    private string InternVenue(ReadOnlySpan<byte> utf8)
    {
        foreach (var name in this.venueNames)
        {
            if (name.Length == utf8.Length && Encoding.ASCII.GetByteCount(name) == utf8.Length && MatchesAscii(name, utf8))
                return name;
        }

        var added = Encoding.ASCII.GetString(utf8);
        this.venueNames.Add(added);
        return added;
    }

    private static bool MatchesAscii(string name, ReadOnlySpan<byte> utf8)
    {
        for (int i = 0; i < name.Length; i++)
        {
            if (name[i] != utf8[i])
                return false;
        }

        return true;
    }

    private void OnError(ref FieldReader f)
    {
        int id = f.Int();
        int code = f.Int();
        string message = f.Text();

        if (code is ConnectivityLost or RestoredDataLost or RestoredDataKept)
        {
            Log.Info($"[ibkr] {code}: {Clip(message)}");

            // Gateway is back, but every subscription is gone. Starting over is the simple cure.
            if (code == RestoredDataLost)
                this.ForceReconnect("Gateway lost its subscriptions");
            return;
        }

        if (id >= ScanRequestId)
        {
            // A scan with nothing to list answers 165 "...no items retrieved" instead of with no rows (HALTED on the
            // paper Gateway, 11:03 ET on 11-17), so that's the list going empty. Other 165s are failures: "Trading
            // TWS session is connected from a different IP address" on 11-16.
            bool empty = code == NoScanItems && message.Contains("no items retrieved", StringComparison.OrdinalIgnoreCase);

            // Every cancel is answered with 162 "API scanner subscription cancelled". Anything else ends this scan,
            // and says why once per kind; the next goes when its interval is up.
            if (code != ScannerCancelled && !empty && this.codesLogged.Add(code))
                Log.Info($"[scan] {code}: {Clip(message)}");

            // A refused scan can stay open at IBKR, and ten open ones block every new one (322 at 14:17 on
            // 11-16, after a competing TWS login made each scan fail with 165), so it's cancelled as well.
            int kind = Volatile.Read(ref this.scanKind);
            if (Interlocked.CompareExchange(ref this.scanId, 0, id) == id)
            {
                _ = this.CancelScanAsync(id);
                if (empty)
                    this.DeliverScan(kind, []);
            }

            return;
        }

        if (code is >= FirstInfoCode and <= LastInfoCode || id < 0)
        {
            // Informational: farm status and depth entitlements. Worth keeping once per code.
            if (this.codesLogged.Add(code))
                Log.Info($"[ibkr] {code}: {Clip(message)}");
            return;
        }

        if (this.historyWaits.TryGetValue(id, out var historyWait))
        {
            Log.Info($"[ibkr] bars request {id} problem {code}: {Clip(message)}");
            historyWait.TrySetResult(null);
            return;
        }

        if (code == NothingToCancel || !this.streams.TryGetValue(id, out var stream))
        {
            if (code != NothingToCancel)
                Log.Info($"[ibkr] {code} on request {id}: {Clip(message)}");
            return;
        }

        // Each retry is refused the same way, and one line says it.
        bool slotRefusal = code is TooManyDepthRequests or TooManyTapeRequests;
        if (slotRefusal)
            stream.WaitingForSlot = true;
        else
            stream.Failed = true;

        if (!slotRefusal || this.slotRefusalsLogged.TryAdd(stream.Key, true))
            Log.Info($"[ibkr] {stream.Key.Symbol} {stream.Key.EventType} problem {code}: {Clip(message)}");

        // A venue that fails leaves the book, and the rest of the book stays.
        if (IsVenueFeed(stream.Key.EventType))
            this.MarkDirty(stream, 0);
        Volatile.Read(ref this.sink)?.OnIbkrProblem(stream.Key.Symbol, stream.Key.EventType, code, message);
        if (ReferenceEquals(this.depthAwaited, stream))
            this.OnDepthAnswered(stream);
    }

    #endregion Data path

    #region Housekeeping

    private void OnHousekeeping(object timerState)
    {
        try
        {
#if TESTHOOKS
            if (this.net != null && File.Exists(Path.Combine(Log.AppDir, "test-ibkr-block")))
                this.ForceReconnect("test hook");
#endif

            var slotClock = DateTime.UtcNow;
            bool occasional = slotClock >= this.nextOccasionalRetryUtc;
            if (occasional)
                this.nextOccasionalRetryUtc = slotClock + OccasionalSlotRetry;

            if (this.subscribed)
            {
                foreach (var feed in new[] { DepthFeed, TapeFeed })
                {
                    if (occasional || (this.slotRetryUntilUtc.TryGetValue(feed, out var until) && slotClock < until))
                        _ = this.RetryWaitingAsync(feed);
                }
            }

            if (this.state == IbkrState.Live)
            {
                if (this.health.Evaluate(DateTime.UtcNow, Stopwatch.GetTimestamp()) == HealthChange.WentStale)
                    this.SetState(IbkrState.Down, $"no data at all for {this.health.Silence(Stopwatch.GetTimestamp()).TotalSeconds:F0}s");
            }
            else if (this.state == IbkrState.Down && this.net != null && this.liveSinceUtc != default
                && this.health.Silence(Stopwatch.GetTimestamp()) > TimeSpan.FromSeconds(30)
                && FeedHealth.SilenceLimit(DateTime.UtcNow) is { } limit && limit < TimeSpan.FromSeconds(30))
            {
                // Connected but silent for a long stretch of an active session.
                this.liveSinceUtc = default;
                this.ForceReconnect("quotes stopped");
            }

            var now = DateTime.UtcNow;
            if (now >= this.nextStatsUtc)
            {
                this.nextStatsUtc = now.AddMinutes(1);
                long quotes = Interlocked.Exchange(ref this.quoteUpdates, 0);
                long venueQuotes = Interlocked.Exchange(ref this.venueQuoteUpdates, 0);
                long auctions = Interlocked.Exchange(ref this.auctionUpdates, 0);
                long depth = Interlocked.Exchange(ref this.depthUpdates, 0);
                long books = Interlocked.Exchange(ref this.depthDeliveries, 0);
                long flush = Interlocked.Exchange(ref this.maxFlushMicros, 0);
                long added = Interlocked.Exchange(ref this.printsAdded, 0);
                long carried = Interlocked.Exchange(ref this.printsCarried, 0);
                long offMarket = Interlocked.Exchange(ref this.printsOffMarket, 0);
                Log.Info($"[ibkr] stats 60s: state={this.state} quoteUpdates={quotes} venueQuoteUpdates={venueQuotes} auctionUpdates={auctions} depthRowChanges={depth} booksDelivered={books} printsAdded={added} printsCarried={carried} printsOffMarket={offMarket} maxFlushUs={flush} streams={this.streams.Count}");
            }
        }
        catch (Exception ex)
        {
            // An exception escaping a timer callback would take Quantower down with it.
            Log.Info($"[ibkr] housekeeping failed: {ex}");
        }
    }

    private void SetState(IbkrState newState, string detail)
    {
        if (this.state == newState)
            return;

        this.state = newState;
        Log.Info($"[ibkr] {newState}: {detail}");
        Volatile.Read(ref this.sink)?.OnIbkrState(newState, detail);
    }

    #endregion Housekeeping

    private sealed class Stream
    {
        public Stream(int id, FeedKey key)
        {
            this.Id = id;
            this.Key = key;
        }

        public int Id { get; }

        public FeedKey Key { get; }

        public bool IsDirty;
        public long DueStamp;
        public volatile bool Failed;
        public volatile bool WaitingForSlot;
        public volatile bool DepthQueued;
        public bool HasBook;
        public double Bid;
        public double BidSize;
        public double Ask;
        public double AskSize;
        public double SharesAdded;
        public double SharesCarried;
        public double SharesOffMarket;
        public readonly List<IbkrDepthRow> Bids = new();
        public readonly List<IbkrDepthRow> Asks = new();
    }

    /// <summary>Walks the zero-separated fields of one frame without allocating.</summary>
    private ref struct FieldReader
    {
        private ReadOnlySpan<byte> rest;

        public FieldReader(ReadOnlySpan<byte> frame) => this.rest = frame;

        public ReadOnlySpan<byte> Next()
        {
            int end = this.rest.IndexOf((byte)0);
            if (end < 0)
            {
                var last = this.rest;
                this.rest = default;
                return last;
            }

            var field = this.rest[..end];
            this.rest = this.rest[(end + 1)..];
            return field;
        }

        public void Skip() => this.Next();

        public int Int() => Utf8Parser.TryParse(this.Next(), out int value, out _) ? value : -1;

        public long Long() => Utf8Parser.TryParse(this.Next(), out long value, out _) ? value : 0;

        public double Double() => Utf8Parser.TryParse(this.Next(), out double value, out _) ? value : double.NaN;

        public string Text() => Encoding.UTF8.GetString(this.Next());
    }
}

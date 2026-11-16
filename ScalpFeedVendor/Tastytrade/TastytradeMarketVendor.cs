using System.Collections.Concurrent;
using System.Diagnostics;
using ScalpFeedVendor.Feed;
using ScalpFeedVendor.Ibkr;
using TradingPlatform.BusinessLayer;
using TradingPlatform.BusinessLayer.Integration;

namespace ScalpFeedVendor.Tastytrade;

/// <summary>
/// Turns the tastytrade feed into Quantower messages. The On* methods run on the receive
/// thread for every event, so they only decode, check and push: no I/O, no logging. The one
/// lock is per symbol, shared with IBKR's prints on that symbol's tape, and held only for the
/// push. Lifecycle work runs on Quantower's threads and a one-second timer.
/// </summary>
internal sealed class TastytradeMarketVendor : Vendor, IFeedSink
{
    private const string ExchangeId = "US";
    private const string Usd = "USD";
    private const string Header = "ScalpFeed";
    private const int FlushDelayMs = 20;

    private const int LagPrintsToWarn = 5;
    private const int UnprintedTradesForFallback = 3;

    private static readonly long DayBarEveryTicks = TimeSpan.FromMilliseconds(250).Ticks;
    private static readonly long DelayedFeedTicks = TimeSpan.FromMinutes(15).Ticks;
    private static readonly long LagWarnTicks = TimeSpan.FromSeconds(5).Ticks;
    private static readonly long FreshPrintTicks = TimeSpan.FromSeconds(5).Ticks;
    private static readonly TimeSpan TokenMaxAge = TimeSpan.FromHours(23);
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(20);

    // A chart reload means a full reconnect, which interrupts data for about 1.7 seconds.
    // A blip shorter than this isn't worth that; charts may miss a print or two from it.
    private static readonly TimeSpan MinGapForReload = TimeSpan.FromSeconds(2);

    // The first TimeAndSale print can take seconds to arrive after subscribing, while Trade
    // starts at once. Without this wait a normal connect could look like a missing tape.
    private static readonly TimeSpan FallbackGrace = TimeSpan.FromSeconds(15);

    // A blip is over before an alarm would help. Only a socket still down after this sounds one.
    private static readonly TimeSpan LinkDownAlarmAfter = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan DelayedWarningEvery = TimeSpan.FromHours(1);

    // Quantower builds a new vendor for every reconnect, so these outlive any one instance.
    private static DateTime lastDelayedWarningUtc;
    private static QuoteToken sharedToken;
    private static volatile bool staleNoticeOpen;

    private static readonly IReadOnlyDictionary<string, string[]> LiveFields = new Dictionary<string, string[]>
    {
        ["Quote"] = ["eventType", "eventSymbol", "bidTime", "askTime", "bidPrice", "bidSize", "askPrice", "askSize"],
        ["TimeAndSale"] = ["eventType", "eventSymbol", "eventFlags", "index", "time", "exchangeCode", "price", "size", "bidPrice", "askPrice", "aggressorSide", "extendedTradingHours", "validTick", "type", "exchangeSaleConditions"],
        ["Trade"] = ["eventType", "eventSymbol", "time", "price", "size", "dayVolume", "extendedTradingHours"],
        ["Summary"] = ["eventType", "eventSymbol", "dayOpenPrice", "dayHighPrice", "dayLowPrice", "dayClosePrice", "prevDayClosePrice", "prevDayVolume"]
    };

    private readonly ConcurrentDictionary<string, SymbolState> statesByStreamer = new();
    private readonly ConcurrentDictionary<string, string> streamerById = new();
    private readonly ConcurrentDictionary<string, EquityInfo> equityBySymbol = new();
    private readonly SubscriptionBook book = new();
    private readonly FeedHealth health = new();
    private readonly SemaphoreSlim tokenGate = new(1, 1);

    private TastytradeApi api;
    private DxLinkLink live;
    private HistoryClient history;
    private IbkrBridge ibkr;
    private FloatSource floats;

    private const string TopGainersList = "IBKR Top Gainers";
    private readonly object topGainersGate = new();
    private IReadOnlyList<string> topGainersSent;
    private IReadOnlyList<string> topGainersNext;
    private bool topGainersLogged;
    private System.Threading.Timer flushTimer;
    private System.Threading.Timer housekeepingTimer;
    private CancellationTokenSource lifetime;
    private QuoteToken currentToken;
    private PingMeter pingMeter;
    private bool refreshingToken;
    private bool stayAwakeRefusalLogged;
    private DateTime nextStatsUtc;

    // Written on the receive thread, read by the timer.
    private long quotesPushed;
    private long printsPushed;
    private long oddLotsShown;
    private long oddLotsTooFar;
    private volatile string lastTooFar;
    private long lagPrints;
    private long lastDelayTicks;
    private long unprintedTrades;
    private volatile bool printsSeen;
    private volatile bool fallbackOn;
    private volatile bool lagWarned;
    private long quoteTimeShiftTicks;
    private long expectedDelayTicks;
    private long lastQuotes;
    private long lastPrints;

    // Chart reload after a gap. Quantower only reloads history when it reconnects, and it
    // only reconnects after a failed ping, so one ping reports failure on purpose.
    private volatile bool reloadPending;
    private volatile bool reloadAfterNextRecovery;

    private long dataSeenAtStale;
    private DateTime linkDownSinceUtc;
    private DateTime liveReadyUtc;

#if TESTHOOKS
    private volatile bool testMuteCanary;
    private volatile bool testMutePrints;
#endif

    public override bool AllowNonFixedList => true;

    #region Connection

    public override ConnectionResult Connect(ConnectRequestParameters connectRequestParameters)
    {
        var ct = connectRequestParameters.CancellationToken;
        Log.Info("Connect");

        // Quantower is still retrying, so a running alarm should keep going.
        FeedAlarm.KeepAlive();
        this.StopFeed();

        try
        {
            ThrowIfTestOutage();
#if TESTHOOKS
            this.testMutePrints = File.Exists(Path.Combine(Log.AppDir, "test-no-prints"));
#endif
            this.lifetime = new CancellationTokenSource();
            this.api = new TastytradeApi(Path.Combine(Log.AppDir, "tastytrade.json"));
            this.currentToken = this.GetStartupToken(ct);
            this.ApplyLevel(this.currentToken);

            this.health.Reset();
            this.printsSeen = false;
            this.fallbackOn = false;
            this.book.Pin(new FeedKey("Quote", FeedHealth.CanarySymbol));
            this.GetState(FeedHealth.CanarySymbol).IsCanary = true;

            // Quantower measures latency with its internet-corrected clock, so the plugin stamps with it too.
            FeedClock.Use(QuantowerClock);
            Log.Info($"clock: Quantower's is {FeedClock.AheadOfPcMs:F0} ms ahead of the PC's, {MeasureClockReadNs():F0} ns a read");
            Log.Info($"luld: {LuldBand.LoadTier1(Path.Combine(Log.AppDir, "luld-tier1.txt"))} Tier 1 symbols in luld-tier1.txt, the rest Tier 2");
            if (File.Exists(Path.Combine(Log.AppDir, StallWatch.TriggerFile)) && !StallWatch.On)
            {
                StallWatch.Start();
                _ = Task.Run(() =>
                {
                    var (median, max) = StallWatch.MeasureSleep1();
                    Log.Info($"stalls: diagnostics on; Sleep(1) inside Quantower takes {median:F2} ms (max {max:F2})");
                });
            }

            this.live = new DxLinkLink("live", this.GetTokenAsync, LiveFields, this);
            this.live.Ready += this.OnLiveReady;
            this.live.Recovered += this.OnLiveRecovered;
            this.live.Start();

            this.history = new HistoryClient(this.GetTokenAsync);
            this.flushTimer = new System.Threading.Timer(this.FlushBook, null, Timeout.Infinite, Timeout.Infinite);
            this.housekeepingTimer = new System.Threading.Timer(this.Housekeeping, null, 1000, 1000);
            this.pingMeter = new PingMeter(Header, this.currentToken.Url.Replace("wss://", "https://", StringComparison.Ordinal));
            this.nextStatsUtc = DateTime.UtcNow.AddMinutes(1);

            if (!this.live.WaitReadyAsync(ConnectTimeout, ct).GetAwaiter().GetResult())
            {
                var problem = this.live.LastProblem ?? "timed out waiting for the feed";
                this.StopFeed();
                RaiseAlarm($"couldn't reach the feed: {problem}");
                return ConnectionResult.CreateFail($"Couldn't reach the tastytrade feed: {problem}");
            }

            // Gateway being closed mustn't fail the connection: tape and charts still work,
            // and the quote stays on tastytrade until IBKR answers.
            this.ibkr = new IbkrBridge(this.TimedPush, this.OnIbkrPrint, this.OnTopGainers);
            this.ibkr.Start();
            this.floats = new FloatSource(this.OnFloatArrived);

            return ConnectionResult.CreateSuccess();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            this.StopFeed();
            return ConnectionResult.CreateCancelled();
        }
        catch (Exception ex) when (ex is TastytradeConfigException or TastytradeApiException or HttpRequestException or OperationCanceledException)
        {
            // A request timing out also lands here. It's a failure rather than a cancellation,
            // so that Quantower keeps retrying through a real network outage.
            Log.Info($"Connect failed: {ex.Message}");
            this.StopFeed();
            if (ex is not TastytradeConfigException)
                RaiseAlarm($"couldn't connect: {ex.Message}");
            return ConnectionResult.CreateFail(ex.Message);
        }
    }

    public override void OnConnected(CancellationToken token)
    {
        Log.Info($"OnConnected, level={this.currentToken?.Level}");

        // Reconnects come through here too, and one warning an hour is enough to be noticed.
        if (this.currentToken is { IsRealTime: false } && DateTime.UtcNow - lastDelayedWarningUtc > DelayedWarningEvery)
        {
            lastDelayedWarningUtc = DateTime.UtcNow;
            this.PushMessage(MessageDealTicket.CreateRefuseDealTicket(
                Header,
                $"DELAYED DATA: tastytrade returned feed level '{this.currentToken.Level}', about 15 minutes behind. Don't trade from these prices. Reconnect once your account is funded."));
        }
    }

    public override void Disconnect()
    {
        Log.Info("Disconnect");
        this.StopFeed();
    }

    public override PingResult Ping()
    {
        var link = this.live;
        if (link == null || link.State != LinkState.Ready)
            return new PingResult { State = PingEnum.Disconnected };

        if (this.reloadPending)
        {
            this.reloadPending = false;
            Log.Info("Ping reports disconnected once, to reload charts after a gap");
            return new PingResult { State = PingEnum.Disconnected };
        }

        TimeSpan? ping = null;
        try
        {
            ping = this.pingMeter?.MeasurePing();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            // The meter failing says nothing about the feed; the socket state is what counts.
        }

        return new PingResult { State = PingEnum.Connected, PingTime = ping };
    }

    #endregion Connection

    #region Symbols

    public override IList<MessageExchange> GetExchanges(CancellationToken token) => new List<MessageExchange>
    {
        new() { Id = ExchangeId, ExchangeName = "US stocks" }
    };

    public override IList<MessageAsset> GetAssets(CancellationToken token) => new List<MessageAsset>
    {
        new() { Id = Usd, Name = Usd, IsoCode = Usd, MinimumChange = 0.01 }
    };

    public override MessageSymbolTypes GetSymbolTypes(CancellationToken token) => new()
    {
        SymbolTypes = new List<SymbolType> { SymbolType.Equities, SymbolType.ETF }
    };

    public override IList<MessageRule> GetRules(CancellationToken token)
    {
        var rules = base.GetRules(token);
        rules.Add(new MessageRule { Name = Rule.ALLOW_TRADING, Value = false });
        return rules;
    }

    public override IList<MessageSymbolInfo> SearchSymbols(SearchSymbolsRequestParameters requestParameters)
    {
        var result = new List<MessageSymbolInfo>();
        try
        {
            var hits = this.api.SearchAsync(requestParameters.FilterName, requestParameters.CancellationToken).GetAwaiter().GetResult();
            foreach (var hit in hits)
            {
                result.Add(new MessageSymbolInfo(hit.Symbol)
                {
                    Name = hit.Symbol,
                    Description = hit.Description,
                    SymbolType = SymbolType.Equities,
                    ExchangeId = ExchangeId,
                    HistoryType = HistoryType.Last,
                    AvailableOptions = AvailableDerivatives.None,
                    AvailableFutures = AvailableDerivatives.None
                });
            }
        }
        catch (Exception ex) when (ex is TastytradeApiException or HttpRequestException or TaskCanceledException)
        {
            Log.Info($"Search '{requestParameters.FilterName}' failed: {ex.Message}");
        }

        return result;
    }

    public override MessageSymbol GetNonFixedSymbol(GetSymbolRequestParameters requestParameters)
    {
        try
        {
            var info = this.api.GetEquityAsync(requestParameters.SymbolId, requestParameters.CancellationToken).GetAwaiter().GetResult();
            return info == null ? null : this.CreateSymbol(info);
        }
        catch (Exception ex) when (ex is TastytradeApiException or HttpRequestException or TaskCanceledException)
        {
            Log.Info($"Symbol lookup '{requestParameters.SymbolId}' failed: {ex.Message}");
            return null;
        }
    }

    private MessageSymbol CreateSymbol(EquityInfo info)
    {
        this.streamerById[info.Symbol] = info.StreamerSymbol;
        this.equityBySymbol[info.Symbol] = info;
        this.GetState(info.StreamerSymbol).Id = info.Symbol;

        return new MessageSymbol(info.Symbol)
        {
            Name = info.Symbol,
            Description = info.Description,
            SymbolType = info.IsEtf ? SymbolType.ETF : SymbolType.Equities,
            ExchangeId = ExchangeId,
            QuotingCurrencyAssetID = Usd,
            HistoryType = HistoryType.Last,
            VolumeType = SymbolVolumeType.Volume,
            NettingType = NettingType.Undefined,
            QuotingType = SymbolQuotingType.LotSize,
            DeltaCalculationType = DeltaCalculationType.AggressorFlag,
            VariableTickList = ToVariableTicks(info.TickBands),
            AvailableOptions = AvailableDerivatives.None,
            AvailableFutures = AvailableDerivatives.None,
            LotSize = 1,
            LotStep = 1,
            MinLot = 1,
            MaxLot = int.MaxValue,
            NotionalValueStep = 1,
            AllowCalculateRealtimeChange = false,
            AllowCalculateRealtimeVolume = false,
            AllowCalculateRealtimeTicks = true,
            AllowCalculateRealtimeTrades = true,
            QuoteDelay = this.currentToken is { IsRealTime: false } ? TimeSpan.FromTicks(DelayedFeedTicks) : TimeSpan.Zero,
            SymbolAdditionalInfo = this.FloatInfo(info.Symbol),
        };
    }

    /// <summary>The float in Symbol Info, from what's kept; null until there's something to show.</summary>
    private List<AdditionalInfoItem> FloatInfo(string symbol)
    {
        var facts = this.floats?.Get(symbol);
        if (facts == null)
            return null;

        string fetched = $"{DateTimeOffset.FromUnixTimeMilliseconds(facts.FetchedMs).LocalDateTime:MM-dd HH:mm}, stockanalysis.com";
        return facts.Known
            ?
            [
                FloatItem("float", "Float", facts.Describe(), 10),
                FloatItem("sharesOut", "Shares outstanding", FloatFacts.Shares(facts.SharesOut), 20),
                FloatItem("floatFetched", "Float fetched", fetched, 30),
            ]
            :
            [
                FloatItem("float", "Float", facts.Describe(), 10),
                FloatItem("floatFetched", "Float fetched", fetched, 30),
            ];
    }

    private static AdditionalInfoItem FloatItem(string id, string name, string value, int sortIndex) => new()
    {
        Id = id,
        NameKey = name,
        GroupInfo = "Float",
        DataType = ComparingType.String,
        SortIndex = sortIndex,
        Value = value,
    };

    /// <summary>
    /// IBKR's top gainers, kept as a symbol list you open in a Watchlist: the regular session's, or after hours the
    /// after-hours ones (IbkrScan). Looking the symbols up can call back into this connection (a symbol it hasn't
    /// made yet), so it runs off IBKR's thread, one list at a time. A list the same as the last is left alone,
    /// unless the list has gone (deleted in Quantower), when it's made again.
    /// </summary>
    private void OnTopGainers(IReadOnlyList<string> symbols)
    {
        lock (this.topGainersGate)
        {
            bool idle = this.topGainersNext == null;
            this.topGainersNext = symbols;
            if (idle)
                _ = Task.Run(this.PublishTopGainers);
        }
    }

    private void PublishTopGainers()
    {
        while (true)
        {
            IReadOnlyList<string> names;
            lock (this.topGainersGate)
            {
                names = this.topGainersNext;
                if (names == null)
                    return;
            }

            try
            {
                this.PublishTopGainers(names);
            }
            catch (Exception ex)
            {
                Log.Info($"[scan] couldn't update {TopGainersList}: {ex.GetBaseException().Message}");
            }

            lock (this.topGainersGate)
            {
                this.topGainersSent = names;
                if (ReferenceEquals(this.topGainersNext, names))
                {
                    this.topGainersNext = null;
                    return;
                }
            }
        }
    }

    private void PublishTopGainers(IReadOnlyList<string> names)
    {
        bool exists = Core.Instance.SymbolList.Any(l => l.Name == TopGainersList);
        if (exists && this.topGainersSent != null && names.SequenceEqual(this.topGainersSent))
            return;

        string connectionId = Core.Instance.Connections.Connected.FirstOrDefault(c => c.VendorName == ScalpFeedVendor.VendorName)?.Id;
        var symbols = new List<Symbol>();
        foreach (var name in names)
        {
            // Stocks only: leveraged ETFs (SOXS, UVIX, MSTZ on 11-16) crowd a gainers list.
            var symbol = Core.Instance.GetSymbol(new GetSymbolRequestParameters { SymbolId = name }, connectionId);
            if (symbol != null && symbol.SymbolType != SymbolType.ETF)
                symbols.Add(symbol);
        }

        if (exists)
            Core.Instance.ReplaceSymbolList(TopGainersList, symbols);
        else
            Core.Instance.AddSymbolList(TopGainersList, symbols);

        // It changes every 30 seconds or so, so only the first of each connection, and each remaking, go in the log.
        if (!this.topGainersLogged || !exists)
            Log.Info($"[scan] {TopGainersList}: {symbols.Count} stocks of {names.Count} rows, {string.Join(" ", symbols.Take(10).Select(s => s.Name))} ...");
        this.topGainersLogged = true;
    }

    /// <summary>
    /// Sends the symbol again with its float, from the float fetcher's thread. Whether Quantower takes in a symbol
    /// sent again isn't documented; if it doesn't, Symbol Info shows the float from the next start, and the
    /// Watchlist's Float column, which reads float-cache.json itself, shows it either way.
    /// </summary>
    private void OnFloatArrived(string symbol)
    {
        if (this.equityBySymbol.TryGetValue(symbol, out var info))
            this.PushMessage(this.CreateSymbol(info));
    }

    private static List<VariableTick> ToVariableTicks(IReadOnlyList<TickBand> bands) =>
        PriceBands.ForDisplay(bands).Select(b => new VariableTick(b.Low, b.High, true, b.TickSize, 1, b.Decimals)).ToList();

    #endregion Symbols

    #region Subscriptions

    public override void SubscribeSymbol(SubscribeQuotesParameters parameters) => this.SetInterest(parameters, true);

    public override void UnSubscribeSymbol(SubscribeQuotesParameters parameters) => this.SetInterest(parameters, false);

    private void SetInterest(SubscribeQuotesParameters parameters, bool on)
    {
        if (parameters.SubscribeType is not (SubscribeQuoteType.Quote or SubscribeQuoteType.Last or SubscribeQuoteType.Level2))
            return;

        var id = parameters.SymbolId;
        var streamer = this.streamerById.GetValueOrDefault(id, id);
        var state = this.GetState(streamer);
        state.Id = id;

        // Depth only exists on IBKR, so tastytrade never hears about it.
        if (parameters.SubscribeType == SubscribeQuoteType.Level2)
        {
            Log.Info($"Depth {(on ? "on" : "off")}: {id}");
            state.DepthOn = on;
            this.ibkr?.SetDepth(state, on);
            return;
        }

        if (parameters.SubscribeType == SubscribeQuoteType.Quote)
        {
            if (on)
                this.floats?.Want(id);
            state.QuoteOn = on;
            this.ibkr?.SetQuote(state, on);
        }
        else
        {
            state.LastOn = on;
            this.ibkr?.SetTape(state, on);
        }

        bool any = state.QuoteOn || state.LastOn;
        if (!any)
            state.TradeTimeCurrent = false;
        this.book.Set(new FeedKey("Quote", streamer), state.QuoteOn);
        this.book.Set(new FeedKey("TimeAndSale", streamer), state.LastOn);
        this.book.Set(new FeedKey("Trade", streamer), any);
        this.book.Set(new FeedKey("Summary", streamer), any);

        // Wait a moment and send only the net change, so chart-open churn never reaches the server.
        this.flushTimer?.Change(FlushDelayMs, Timeout.Infinite);
    }

    private SymbolState GetState(string streamer) =>
        this.statesByStreamer.GetOrAdd(streamer, static s => new SymbolState(s));

    private void FlushBook(object state)
    {
        try
        {
            var link = this.live;
            if (link == null || link.State != LinkState.Ready)
                return;

            if (this.book.TakeDiff(out var add, out var remove))
            {
                Log.Info($"Subscriptions: +{add.Count} -{remove.Count}");
                _ = SendSafelyAsync(link, add, remove, false);
            }
        }
        catch (Exception ex)
        {
            Log.Info($"Subscription flush failed: {ex.Message}");
        }
    }

    private void OnLiveReady()
    {
        this.liveReadyUtc = DateTime.UtcNow;
        this.health.Reset();
        var link = this.live;
        if (link != null)
            _ = SendSafelyAsync(link, this.book.TakeAll(), null, true);
    }

    private void OnLiveRecovered(DateTime droppedUtc, DateTime backUtc)
    {
        var gap = backUtc - droppedUtc;
        bool reload = gap >= MinGapForReload || this.reloadAfterNextRecovery;
        this.reloadAfterNextRecovery = false;
        Log.Info($"Live feed back after {gap.TotalSeconds:F2}s, chart reload: {(reload ? "yes" : "no")}");
        if (reload)
            this.reloadPending = true;
    }

    private static async Task SendSafelyAsync(DxLinkLink link, List<FeedKey> add, List<FeedKey> remove, bool reset)
    {
        try
        {
            await link.SendSubscriptionAsync(add?.Count > 0 ? add : null, remove?.Count > 0 ? remove : null, reset).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Info($"Subscription send failed: {ex.Message}");
        }
    }

    #endregion Subscriptions

    #region Data path

    /// <summary>Any quote, print or trade, on any symbol, shows the feed is delivering.</summary>
    private void NoteData()
    {
#if TESTHOOKS
        if (this.testMuteCanary)
            return;
#endif
        this.health.OnData(Stopwatch.GetTimestamp());
    }

    public void OnQuote(FeedRow row)
    {
        var state = StateOf(row, this);
        this.NoteData();

        if (!state.QuoteOn || state.IbkrQuotes)
            return;

        // Quote timestamps can be absent on this feed, so fall back to arrival time.
        long timeMs = Math.Max(row.BidTime, row.AskTime);
        long ticks = timeMs > 0 ? ToUtcTicks(timeMs) : row.ArrivalUtcTicks - Volatile.Read(ref this.quoteTimeShiftTicks);
        this.TimedPush(new Quote(state.Id, row.Bid, row.BidSize, row.Ask, row.AskSize, new DateTime(ticks, DateTimeKind.Utc)));
        this.quotesPushed++;
    }

    public void OnPrint(FeedRow row)
    {
        this.NoteData();
#if TESTHOOKS
        if (this.testMutePrints)
            return;
#endif

        if ((row.Flags & EventFlags.Remove) != 0 || row.Sale != SaleKind.New || double.IsNaN(row.Price))
            return;

        var state = StateOf(row, this);
        if (!PrintFilter.Shows(row, state.LastPrice))
        {
            if (row.NormalOddLot)
            {
                this.oddLotsTooFar++;
                this.lastTooFar = $"{row.Symbol.Name} {row.Price} against {row.Bid}/{row.Ask}";
            }

            return;
        }

        this.printsSeen = true;
        if (!row.ValidTick)
            this.oddLotsShown++;

        long exchangeTicks = ToUtcTicks(row.Time);
        long delay = row.ArrivalUtcTicks - exchangeTicks;
        Volatile.Write(ref this.lastDelayTicks, delay);
        this.lagPrints = delay - Volatile.Read(ref this.expectedDelayTicks) > LagWarnTicks ? this.lagPrints + 1 : 0;

        if (row.Index != 0 && row.Index == state.LastPrintIndex)
            return;
        state.LastPrintIndex = row.Index;

        // Overnight prints (code O) aren't on IBKR's SMART tape, so they show whichever tape is on.
        // Late reports can't show IBKR's tape has stopped, so only a fresh print starts its clock.
        if (this.ibkr is { IsMerged: true })
        {
            if (state.Merge.OnTastytradePrint(row.Price, row.Size, row.Time / 1000, row.ArrivalUtcTicks))
                this.EmitPrint(state, row.Price, row.Size, exchangeTicks, row.Aggressor, row.Bid, row.Ask, row.ValidTick, row.Reopening);
        }
        else if (!state.IbkrTape || row.Exchange == 'O')
            this.EmitPrint(state, row.Price, row.Size, exchangeTicks, row.Aggressor, row.Bid, row.Ask, row.ValidTick, row.Reopening);
        else if (delay < FreshPrintTicks)
            state.IbkrTapeWatch.OnExpectedPrint(row.ArrivalUtcTicks);

        this.MaybePushDayBar(state, row.ArrivalUtcTicks);
    }

    public void OnTrade(FeedRow row)
    {
        this.NoteData();
        var state = StateOf(row, this);
        bool volumeRose = row.DayVolume > state.DayVolume;
        if (!double.IsNaN(row.DayVolume))
            state.DayVolume = row.DayVolume;

        // Volume rising with no TimeAndSale at all means prints exist but aren't reaching us.
        if (volumeRose && state.LastOn && !this.printsSeen)
            Interlocked.Increment(ref this.unprintedTrades);

        if (this.fallbackOn && !state.IbkrTape && row.Time > state.LastTradeTimeMs && !double.IsNaN(row.Price))
            this.EmitPrint(state, row.Price, row.Size, ToUtcTicks(row.Time), PrintSide.Undefined, double.NaN, double.NaN, true, false);

        state.LastTradeTimeMs = Math.Max(state.LastTradeTimeMs, row.Time);
        state.TradeTimeCurrent = true;
        this.MaybePushDayBar(state, row.ArrivalUtcTicks);
    }

    public void OnSummary(FeedRow row)
    {
        var state = StateOf(row, this);
        state.Open = row.Open;
        state.High = row.High;
        state.Low = row.Low;
        state.PrevClose = row.PrevClose;
        state.NextDayBarTicks = 0;
        this.MaybePushDayBar(state, row.ArrivalUtcTicks);
    }

    public void OnCandle(FeedRow row)
    {
    }

    private static SymbolState StateOf(FeedRow row, TastytradeMarketVendor vendor)
    {
        var symbol = row.Symbol;
        if (symbol.Tag is SymbolState known)
            return known;

        var state = vendor.GetState(symbol.Name);
        symbol.Tag = state;
        return state;
    }

    private static long ToUtcTicks(long unixMs) => DateTime.UnixEpoch.Ticks + (unixMs * TimeSpan.TicksPerMillisecond);

    /// <summary>On IBKR's receive thread: a print for this symbol's tape.</summary>
    private void OnIbkrPrint(SymbolState state, double price, double size, long utcTicks, double bid, double ask, bool roundLot, bool reopening) =>
        this.EmitPrint(state, price, size, utcTicks, PrintSide.Undefined, bid, ask, roundLot, reopening);

    private void EmitPrint(SymbolState state, double price, double size, long exchangeTicks, PrintSide side, double bid, double ask, bool roundLot, bool reopening)
    {
        // Two feeds write this tape, and the push stays inside the lock so that a print's time
        // and its place in line can't disagree. It's almost always uncontended.
        lock (state)
        {
            var direction = price > state.LastPrice ? TickDirection.Up : price < state.LastPrice ? TickDirection.Down : TickDirection.None;
            state.LastPrice = price;
            state.LastSize = size;

            // Each trade reaches here once, from whichever feed put it on the tape, so the LULD mean counts it once.
            state.Luld.OnTrade(price, FeedClock.UtcNowTicks, roundLot, reopening);

            if (!state.LastOn)
                return;

            // Quantower expects strictly increasing times per symbol.
            long ticks = exchangeTicks > state.LastPrintTicks ? exchangeTicks : state.LastPrintTicks + 1;
            state.LastPrintTicks = ticks;

            // IBKR's quote is the NBBO. tastytrade's is Nasdaq's own, for when IBKR isn't quoting.
            bool hasNbbo = state.IbkrBid > 0 && state.IbkrAsk > 0;
            var place = state.RecentQuotes.Judge(price, hasNbbo ? state.IbkrBid : bid, hasNbbo ? state.IbkrAsk : ask, bid, ask, FeedClock.UtcNowTicks);

            var last = new Last(state.Id, price, size, new DateTime(ticks, DateTimeKind.Utc))
            {
                AggressorFlag = ToAggressor(place, side),
                TickDirection = direction
            };

            // A print has no bid or ask field for a table rule to compare against, and Quantower works
            // out Tick direction itself. Buyer and Seller are otherwise empty for stocks, so they say
            // outright when a print hit outside the quote. Both, for whichever a rule can test.
            if (ToOutsideFlag(place) is { } outside)
            {
                last.Buyer = outside;
                last.Seller = outside;
            }

            this.TimedPush(last);
        }

        Interlocked.Increment(ref this.printsPushed);
    }

    /// <summary>A hand-off into Quantower, timed while StallWatch is on.</summary>
    private void TimedPush(Message message)
    {
        if (!StallWatch.On)
        {
            this.PushMessage(message);
            return;
        }

        long start = Stopwatch.GetTimestamp();
        this.PushMessage(message);
        StallWatch.Note(message switch { Last => "push print", Quote => "push quote", DOMQuote => "push book", _ => "push other" }, start);
    }

    /// <summary>
    /// The tape's color comes from this. tastytrade labels every print BUY or SELL, even though 66-97%
    /// of them hit between its bid and ask, so the label only decides when there's no quote at all.
    /// </summary>
    private static AggressorFlag ToAggressor(PrintPlace place, PrintSide side) => place switch
    {
        PrintPlace.AboveAsk or PrintPlace.AtAsk => AggressorFlag.Buy,
        PrintPlace.BelowBid or PrintPlace.AtBid => AggressorFlag.Sell,
        PrintPlace.Between => AggressorFlag.None,
        _ => side == PrintSide.Buy ? AggressorFlag.Buy : side == PrintSide.Sell ? AggressorFlag.Sell : AggressorFlag.None
    };

    private static string ToOutsideFlag(PrintPlace place) => place switch
    {
        PrintPlace.AboveAsk => "ABOVE",
        PrintPlace.BelowBid => "BELOW",
        _ => null
    };

    private void MaybePushDayBar(SymbolState state, long nowTicks)
    {
        if (!(state.QuoteOn || state.LastOn) || nowTicks < state.NextDayBarTicks)
            return;
        state.NextDayBarTicks = nowTicks + DayBarEveryTicks;

        var bar = new DayBar(state.Id, new DateTime(nowTicks, DateTimeKind.Utc));
        if (!double.IsNaN(state.Open))
            bar.Open = state.Open;
        if (!double.IsNaN(state.High))
            bar.High = state.High;
        if (!double.IsNaN(state.Low))
            bar.Low = state.Low;
        if (!double.IsNaN(state.PrevClose))
            bar.PreviousClose = state.PrevClose;
        if (!double.IsNaN(state.DayVolume))
            bar.Volume = state.DayVolume;

        if (!double.IsNaN(state.LastPrice))
        {
            bar.Last = state.LastPrice;
            if (!double.IsNaN(state.LastSize))
                bar.LastSize = state.LastSize;

            // Measured from the previous close, so premarket moves show up as they happen.
            if (state.PrevClose > 0)
            {
                bar.Change = state.LastPrice - state.PrevClose;
                bar.ChangePercentage = bar.Change / state.PrevClose * 100;
            }
        }

        this.PushMessage(bar);
    }

    #endregion Data path

    #region History

    public override HistoryMetadata GetHistoryMetadata(CancellationToken cancellationToken) => new()
    {
        AllowedAggregations = new[] { HistoryAggregation.TIME, HistoryAggregation.TICK },
        AllowedPeriodsHistoryAggregationTime = new[]
        {
            Period.SECOND1, Period.SECOND5, Period.SECOND10, Period.SECOND15, Period.SECOND30,
            Period.MIN1, Period.MIN5, Period.MIN15, Period.MIN30, Period.HOUR1, Period.HOUR4, Period.DAY1, Period.WEEK1, Period.MONTH1
        },
        AllowedHistoryTypesHistoryAggregationTime = new[] { HistoryType.Last },
        AllowedHistoryTypesHistoryAggregationTick = new[] { HistoryType.Last },
        DownloadingStep_Tick = TimeSpan.FromDays(1),

        // Reload from the source each time, so a chart never keeps a gap from a cached copy.
        UseHistoryLocalCache = false
    };

    public override IList<IHistoryItem> LoadHistory(HistoryRequestParameters requestParameters)
    {
        var items = new List<IHistoryItem>();

        // tastytrade has no usable tick history (requests return about a dozen prints), so
        // tick charts start empty and build from live prints.
        if (requestParameters.Aggregation is not HistoryAggregationTime time)
            return items;

        var client = this.history;
        if (client == null)
            return items;

        var id = requestParameters.SymbolId;
        var streamer = this.streamerById.GetValueOrDefault(id, id);
        bool regularHoursBars = UsesRegularHoursBars(time.Period);
        var from = requestParameters.FromTime;
        var to = requestParameters.ToTime;
        var ct = requestParameters.CancellationToken;

        try
        {
            // Started first and waited for last, so a slow answer from one doesn't hold up the other.
            var bridge = this.ibkr;
            var fromIbkr = bridge == null || regularHoursBars ? null : bridge.LoadBarsAsync(streamer, time.Period, from, to, ct);
            IReadOnlyList<BarRow> bars = client.LoadAsync(streamer + "{" + CandleCode(time.Period, regularHoursBars) + "}", from, to, ct).GetAwaiter().GetResult();

            if (fromIbkr != null)
            {
                var better = fromIbkr.GetAwaiter().GetResult();
                if (better.Count > 0)
                    bars = IbkrBars.Overlay(bars, better);
            }

            // Regular-hours candles count only part of the market's volume (SPY showed 20M of
            // 62M one day), while all-hours candles carry the whole-market figure. So daily and
            // longer bars take their prices from the first and their volume from the second.
            Dictionary<long, double> wholeMarketVolume = null;
            if (regularHoursBars)
            {
                var allHours = client.LoadAsync(streamer + "{" + CandleCode(time.Period, false) + "}", from, to, ct).GetAwaiter().GetResult();
                wholeMarketVolume = allHours.ToDictionary(b => b.TimeMs, b => b.Volume);
            }

            foreach (var bar in bars)
            {
                double volume = bar.Volume;
                if (wholeMarketVolume != null && wholeMarketVolume.TryGetValue(bar.TimeMs, out double whole))
                    volume = whole;

                items.Add(new HistoryItemBar
                {
                    TicksLeft = ToUtcTicks(bar.TimeMs),
                    Open = bar.Open,
                    High = bar.High,
                    Low = bar.Low,
                    Close = bar.Close,
                    Volume = volume,
                    Ticks = bar.Count
                });
            }
        }
        catch (OperationCanceledException)
        {
            // The chart closed or changed while loading.
        }

        return items;
    }

    /// <summary>Daily and longer bars use regular trading hours, the convention traders expect.</summary>
    private static bool UsesRegularHoursBars(Period period) =>
        period.BasePeriod is BasePeriod.Day or BasePeriod.Week or BasePeriod.Month or BasePeriod.Year;

    private static string CandleCode(Period period, bool regularHoursOnly)
    {
        int n = period.PeriodMultiplier;
        var code = period.BasePeriod switch
        {
            BasePeriod.Second => $"={n}s",
            BasePeriod.Minute => $"={n}m",
            BasePeriod.Hour => $"={n}h",
            BasePeriod.Day => $"={n}d",
            BasePeriod.Week => $"={n}w",
            BasePeriod.Month => $"={n}mo",
            BasePeriod.Year => $"={n}y",
            _ => "=1m"
        };
        return regularHoursOnly ? code + ",tho=true" : code;
    }

    #endregion History

    #region Housekeeping

    private void ApplyLevel(QuoteToken token)
    {
        bool realTime = token.IsRealTime;
        Volatile.Write(ref this.quoteTimeShiftTicks, realTime ? 0 : DelayedFeedTicks);
        Volatile.Write(ref this.expectedDelayTicks, realTime ? 0 : DelayedFeedTicks);
    }

    /// <summary>Test builds only: while this file exists, act as if the network is down.</summary>
    [Conditional("TESTHOOKS")]
    private static void ThrowIfTestOutage()
    {
#if TESTHOOKS
        if (File.Exists(Path.Combine(Log.AppDir, "test-block")))
            throw new HttpRequestException("test outage");
#endif
    }

    private QuoteToken GetStartupToken(CancellationToken ct)
    {
        // Reusing a real-time token keeps a reconnect fast and independent of the REST API.
        // A delayed one is always replaced, so a newly funded account upgrades on reconnect.
        var token = sharedToken;
        if (token is { IsRealTime: true } && DateTime.UtcNow - token.FetchedUtc < TokenMaxAge)
            return token;

        token = this.api.GetQuoteTokenAsync(ct).GetAwaiter().GetResult();
        sharedToken = token;
        return token;
    }

    private async Task<QuoteToken> GetTokenAsync(bool forceNew, CancellationToken ct)
    {
        ThrowIfTestOutage();
        await this.tokenGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var token = this.currentToken;
            if (!forceNew && token != null && DateTime.UtcNow - token.FetchedUtc < TokenMaxAge)
                return token;

            var fresh = await this.api.GetQuoteTokenAsync(ct).ConfigureAwait(false);
            if (token != null && fresh.Level != token.Level)
                this.PushMessage(MessageDealTicket.CreateInfoDealTicket(Header, $"Feed level changed from {token.Level} to {fresh.Level}."));

            this.ApplyLevel(fresh);
            this.currentToken = fresh;
            sharedToken = fresh;
            return fresh;
        }
        finally
        {
            this.tokenGate.Release();
        }
    }

    private void Housekeeping(object state)
    {
        try
        {
            var link = this.live;
            if (link == null)
                return;

#if TESTHOOKS
            var trigger = Path.Combine(Log.AppDir, "test-drop");
            if (File.Exists(trigger))
            {
                File.Delete(trigger);
                link.ForceReconnect("test hook");
            }

            // While this file exists, act as if the canary went silent, with a short limit.
            bool muted = File.Exists(Path.Combine(Log.AppDir, "test-stale"));
            this.testMuteCanary = muted;
            this.health.LimitOverride = muted ? TimeSpan.FromSeconds(5) : null;

            // While this file exists, act as if TimeAndSale isn't being served.
            this.testMutePrints = File.Exists(Path.Combine(Log.AppDir, "test-no-prints"));
#endif

            if (FeedHealth.SilenceLimit(DateTime.UtcNow) != null && !StayAwake.Renew() && !this.stayAwakeRefusalLogged)
            {
                this.stayAwakeRefusalLogged = true;
                Log.Info("Windows refused the request to stay awake");
            }

            this.CheckHealth(link);
            this.ibkr?.CheckHealth();
            this.CheckFallback();
            this.CheckLag();
            this.CheckToken(link);
            this.WriteStats(link);
        }
        catch (Exception ex)
        {
            // An exception escaping a timer callback would take Quantower down with it.
            Log.Info($"Housekeeping failed: {ex}");
        }
    }

    /// <summary>The canary is expected to be silent when markets are shut, so no alarm then.</summary>
    private static void RaiseAlarm(string why)
    {
        if (FeedHealth.SilenceLimit(DateTime.UtcNow) != null)
            FeedAlarm.Raise(why);
    }

    private void CheckHealth(DxLinkLink link)
    {
        var utcNow = DateTime.UtcNow;

        // A reconnect proves nothing by itself, so only a real quote gives the all-clear.
        // The notice is static because a reconnect can hand over to a new vendor instance.
        if (this.health.DataSeen > this.dataSeenAtStale)
        {
            FeedAlarm.Clear();
            if (staleNoticeOpen)
            {
                staleNoticeOpen = false;
                Log.Info("Feed recovered");
                this.PushMessage(MessageDealTicket.CreateInfoDealTicket(Header, "Feed recovered: data is flowing again."));
            }
        }

        if (link.State != LinkState.Ready)
        {
            if (this.linkDownSinceUtc == default)
                this.linkDownSinceUtc = utcNow;

            if (utcNow - this.linkDownSinceUtc >= LinkDownAlarmAfter)
            {
                this.dataSeenAtStale = this.health.DataSeen;
                RaiseAlarm($"live link down: {link.LastProblem}");
            }

            return;
        }

        this.linkDownSinceUtc = default;

        // Still silent after the all-clear check above means it's still a problem.
        if (staleNoticeOpen && FeedHealth.SilenceLimit(utcNow) != null)
            FeedAlarm.KeepAlive();

        long now = Stopwatch.GetTimestamp();
        switch (this.health.Evaluate(utcNow, now))
        {
            case HealthChange.WentStale:
                var age = this.health.Silence(now).TotalSeconds;
                Log.Info($"Feed stale: no data at all for {age:F0}s");
                this.dataSeenAtStale = this.health.DataSeen;
                RaiseAlarm($"no data for {age:F0}s");
                if (!staleNoticeOpen)
                {
                    staleNoticeOpen = true;
                    this.PushMessage(MessageDealTicket.CreateRefuseDealTicket(Header, $"FEED STALE: no tastytrade data at all for {age:F0} seconds. Don't trust these prices."));
                }

                break;
            case HealthChange.NeedsReconnect:
                // The socket itself may come back fast, but data has been missing for a while.
                this.reloadAfterNextRecovery = true;
                link.ForceReconnect("feed stale");
                break;
        }
    }

    private void CheckFallback()
    {
        if (!this.fallbackOn && !this.printsSeen && DateTime.UtcNow - this.liveReadyUtc > FallbackGrace && Volatile.Read(ref this.unprintedTrades) >= UnprintedTradesForFallback)
        {
            this.fallbackOn = true;
            Log.Info("TimeAndSale isn't arriving; using Trade events for prints");
            this.PushMessage(MessageDealTicket.CreateRefuseDealTicket(
                Header,
                "TimeAndSale isn't being served, so prints now come from Trade events. Trade can miss extended-hours prints: the tape may be empty or stale premarket."));
        }
        else if (this.fallbackOn && this.printsSeen)
        {
            this.fallbackOn = false;
            Interlocked.Exchange(ref this.unprintedTrades, 0);
            Log.Info("TimeAndSale is arriving again");
            this.PushMessage(MessageDealTicket.CreateInfoDealTicket(Header, "TimeAndSale is back: prints are coming from the full tape again."));
        }
    }

    private void CheckLag()
    {
        bool lagging = Volatile.Read(ref this.lagPrints) >= LagPrintsToWarn;
        if (lagging && !this.lagWarned)
        {
            this.lagWarned = true;
            var seconds = TimeSpan.FromTicks(Volatile.Read(ref this.lastDelayTicks) - Volatile.Read(ref this.expectedDelayTicks)).TotalSeconds;
            Log.Info($"Prints lagging by {seconds:F1}s");
            FeedAlarm.PlayWarning();
            this.PushMessage(MessageDealTicket.CreateRefuseDealTicket(
                Header,
                $"FEED LAGGING: prints are arriving {seconds:F1} seconds behind exchange time. If your PC clock is off, this can be a false alarm."));
        }
        else if (!lagging && this.lagWarned)
        {
            this.lagWarned = false;
            Log.Info("Prints caught up");
            FeedAlarm.PlayAllClear();
            this.PushMessage(MessageDealTicket.CreateInfoDealTicket(Header, "Prints have caught up with exchange time."));
        }
    }

    private void CheckToken(DxLinkLink link)
    {
        var token = this.currentToken;
        if (this.refreshingToken || token == null || DateTime.UtcNow - token.FetchedUtc < TokenMaxAge)
            return;

        this.refreshingToken = true;
        _ = Task.Run(async () =>
        {
            try
            {
                var fresh = await this.GetTokenAsync(true, this.lifetime.Token).ConfigureAwait(false);
                await link.ReauthorizeAsync(fresh).ConfigureAwait(false);
                Log.Info("Quote token refreshed");
            }
            catch (Exception ex)
            {
                // The socket is still fine. If the old token lapses the link reconnects with a new one.
                Log.Info($"Quote token refresh failed: {ex.Message}");
            }
            finally
            {
                this.refreshingToken = false;
            }
        });
    }

    private void WriteStats(DxLinkLink link)
    {
        var now = DateTime.UtcNow;
        if (now < this.nextStatsUtc)
            return;
        this.nextStatsUtc = now.AddMinutes(1);

        long quotes = Volatile.Read(ref this.quotesPushed);
        long prints = Volatile.Read(ref this.printsPushed);
        long oddLots = Interlocked.Exchange(ref this.oddLotsShown, 0);
        long tooFar = Interlocked.Exchange(ref this.oddLotsTooFar, 0);
        var tooFarSample = this.lastTooFar;
        this.lastTooFar = null;
        if (tooFarSample != null)
            Log.Info($"Odd lots off the tape for being too far from the quote: {tooFar}, last was {tooFarSample}");

        Log.Info(
            $"stats 60s: quotes={quotes - this.lastQuotes} prints={prints - this.lastPrints} oddLots={oddLots} frames={link.TakeFrameCount()} " +
            $"maxFrameHandleUs={link.TakeMaxHandleMicros()} lastPrintDelayMs={TimeSpan.FromTicks(Volatile.Read(ref this.lastDelayTicks)).TotalMilliseconds:F0} " +
            $"silenceS={this.health.Silence(Stopwatch.GetTimestamp()).TotalSeconds:F1} level={this.currentToken?.Level} " +
            $"clockAheadMs={FeedClock.AheadOfPcMs:F0}");
        this.lastQuotes = quotes;
        this.lastPrints = prints;
        if (StallWatch.TakeSummary() is { } stalls)
            Log.Info(stalls);
    }

    private static DateTime QuantowerClock() => Core.Instance.TimeUtils.DateTimeUtcNow;

    /// <summary>The clock is read on every frame and print, so its cost goes in the log once.</summary>
    private static double MeasureClockReadNs()
    {
        const int Reads = 10_000;
        long sum = 0;
        long start = Stopwatch.GetTimestamp();
        for (int i = 0; i < Reads; i++)
            sum += FeedClock.UtcNowTicks & 1;
        double ns = (Stopwatch.GetTimestamp() - start) * 1e9 / Stopwatch.Frequency / Reads;
        return sum < 0 ? 0 : ns;
    }

    private void StopFeed()
    {
        this.housekeepingTimer?.Dispose();
        this.housekeepingTimer = null;
        this.flushTimer?.Dispose();
        this.flushTimer = null;
        this.lifetime?.Cancel();

        // The Gateway connection itself stays open for whichever vendor comes next.
        this.ibkr?.Stop();
        this.ibkr = null;
        this.floats?.Dispose();
        this.floats = null;

        var link = this.live;
        this.live = null;
        var client = this.history;
        this.history = null;

        if (link != null)
        {
            link.Ready -= this.OnLiveReady;
            link.Recovered -= this.OnLiveRecovered;
            StopQuietly(link.StopAsync);
        }

        if (client != null)
            StopQuietly(client.StopAsync);

        this.api?.Dispose();
        this.api = null;
    }

    private static void StopQuietly(Func<Task> stop)
    {
        // A failure stopping one socket must not leave the other one running.
        try
        {
            stop().Wait(TimeSpan.FromSeconds(3));
        }
        catch (AggregateException ex)
        {
            Log.Info($"Stopping the feed: {ex.GetBaseException().Message}");
        }
    }

    #endregion Housekeeping
}

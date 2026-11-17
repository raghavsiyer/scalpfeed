using System.Collections.Concurrent;
using System.Diagnostics;
using ScalpFeedVendor.Feed;
using ScalpFeedVendor.Tastytrade;
using TradingPlatform.BusinessLayer;
using TradingPlatform.BusinessLayer.Integration;

namespace ScalpFeedVendor.Ibkr;

/// <summary>
/// Turns IBKR's quotes and depth into Quantower messages for the vendor that owns the symbols.
/// A symbol shows IBKR's bid and ask once IBKR has quoted it, and goes back to tastytrade's if
/// IBKR goes down or can't serve it, so the quote never just freezes. The depth panel empties
/// instead, because tastytrade has no depth and a frozen book looks real. The tape follows the
/// tape mode: IBKR's whole tape once its first print arrives, or both feeds put together by TapeMerge
/// so each trade shows once. If IBKR goes down, has no slot for a symbol, or its tape goes quiet
/// while tastytrade's carries on, that tape is simply tastytrade's alone until IBKR prints again.
/// </summary>
internal sealed class IbkrBridge : IIbkrSink
{
    /// <summary>Puts one print on the symbol's tape, with the quote that decides its side.</summary>
    public delegate void PrintHandler(SymbolState state, double price, double size, long utcTicks, double bid, double ask, bool roundLot, bool reopening);

    private const string Header = "ScalpFeed";
    private static readonly List<IbkrDepthRow> NoRows = new();

    // A blip is over before a warning would help.
    private static readonly TimeSpan WarnAfter = TimeSpan.FromSeconds(2);

    // Longer limits mean a quiet overnight session, where silence isn't a sign of trouble.
    private static readonly TimeSpan QuietSession = TimeSpan.FromSeconds(30);

    // Quantower builds a new vendor for every reconnect, so these outlive any one instance.
    private static volatile bool lossAnnounced;
    private static long tooFar;
    private static long reportedAlreadyShown;
    private static long reportedAdded;
    private static DateTime nextMergeStatsUtc;

    private readonly Action<Message> push;
    private readonly PrintHandler emitPrint;
    private readonly ConcurrentDictionary<string, SymbolState> statesByIbkr = new();
    private readonly string[] bidIds;
    private readonly string[] askIds;

    /// <summary>The depth row showing the reopening auction's indicative price during a halt or pause.</summary>
    public const string ResVenue = "Res";

    /// <summary>
    /// What the Res row reads when the reopening time isn't known (a halt, or a pause whose start wasn't seen and
    /// with no recent last trade to estimate it from): a fixed size far above any real row. In a pause it reads the
    /// seconds until the reopening times 1000 instead (PauseClock), by your choice of 2026-11-13, estimated from
    /// the last trade when the pause was already on when the symbol was opened (your choice of 2026-11-16). The
    /// Limit rows read 1.
    /// </summary>
    public const double MarkerRowSize = 999_999;

    // IBKR's receive thread only: the book with a LULD band's row put in, then the Res row.
    private readonly List<IbkrDepthRow> bandRow = new(1);
    private readonly List<IbkrDepthRow> bidsWithBand = new();
    private readonly List<IbkrDepthRow> asksWithBand = new();
    private readonly List<IbkrDepthRow> bidsWithRes = new();
    private readonly List<IbkrDepthRow> asksWithRes = new();

    private IbkrClient client;
    private DateTime notLiveSinceUtc;

    private readonly Action<IReadOnlyList<string>> topGainers;

    public IbkrBridge(Action<Message> push, PrintHandler emitPrint, Action<IReadOnlyList<string>> topGainers = null)
    {
        this.push = push;
        this.emitPrint = emitPrint;
        this.topGainers = topGainers;
        this.bidIds = Enumerable.Range(0, 200).Select(i => "b" + i).ToArray();
        this.askIds = Enumerable.Range(0, 200).Select(i => "a" + i).ToArray();
    }

    public bool IsMerged => this.client?.TapeMode == TapeMode.Merged;

    public void Start() => this.client = IbkrSession.Attach(this);

    /// <summary>IBKR's bars for this span of a chart, within what one request can cover. Empty if there are none to use.</summary>
    public async Task<IReadOnlyList<BarRow>> LoadBarsAsync(string symbol, Period period, DateTime fromUtc, DateTime toUtc, CancellationToken ct)
    {
        var current = this.client;
        int seconds = period.BasePeriod switch
        {
            BasePeriod.Second => period.PeriodMultiplier,
            BasePeriod.Minute => period.PeriodMultiplier * 60,
            BasePeriod.Hour => period.PeriodMultiplier * 3600,
            _ => 0
        };
        string barSize = IbkrBars.BarSize(seconds);
        if (current == null || barSize == null)
            return [];

        var now = FeedClock.UtcNow;
        var end = toUtc > now ? now : toUtc;
        long span = Math.Min((long)(end - fromUtc).TotalSeconds, IbkrBars.MaxSeconds(seconds));
        if (span <= 0)
            return [];

        var bars = await current.LoadBarsAsync(symbol, barSize, IbkrBars.Duration(span), IbkrBars.EndTime(end, now), ct).ConfigureAwait(false);
        long fromMs = new DateTimeOffset(DateTime.SpecifyKind(fromUtc, DateTimeKind.Utc)).ToUnixTimeMilliseconds();
        long toMs = new DateTimeOffset(DateTime.SpecifyKind(toUtc, DateTimeKind.Utc)).ToUnixTimeMilliseconds();
        return bars.Where(b => b.TimeMs >= fromMs && b.TimeMs <= toMs).ToList();
    }

    public void Stop()
    {
        IbkrSession.Detach(this);
        this.client = null;
    }

    public void SetQuote(SymbolState state, bool on)
    {
        if (!on)
            state.IbkrQuotes = false;

        this.statesByIbkr[IbkrClient.ToIbkrSymbol(state.Streamer)] = state;
        this.client?.SetQuote(state.Streamer, on);
    }

    public void SetDepth(SymbolState state, bool on)
    {
        this.statesByIbkr[IbkrClient.ToIbkrSymbol(state.Streamer)] = state;
        this.client?.SetDepth(state.Streamer, on);
    }

    public void SetTape(SymbolState state, bool on)
    {
        if (!on)
            state.IbkrTape = false;

        this.statesByIbkr[IbkrClient.ToIbkrSymbol(state.Streamer)] = state;
        this.client?.SetTape(state.Streamer, on);
    }

    /// <summary>Called once a second off the data path.</summary>
    public void CheckHealth()
    {
        var current = this.client;
        if (current == null)
            return;

        long refused = Interlocked.Exchange(ref tooFar, 0);
        if (refused > 0)
            Log.Info($"[ibkr] prints off the tape for being too far from the quote: {refused}");

        // The tape watch holds arrival times, which are on FeedClock.
        var utcNow = FeedClock.UtcNow;
        this.CheckTapes(utcNow);
        this.RefreshCountdowns(current);
        if (utcNow >= nextMergeStatsUtc)
        {
            nextMergeStatsUtc = utcNow.AddMinutes(1);
            long alreadyShown = Interlocked.Exchange(ref reportedAlreadyShown, 0);
            long added = Interlocked.Exchange(ref reportedAdded, 0);
            if (alreadyShown + added > 0)
                Log.Info($"[ibkr] merge 60s: FINRA prints also on tastytrade's tape={alreadyShown}, added from IBKR={added}");
        }

        if (current.State == IbkrState.Live)
        {
            this.notLiveSinceUtc = default;
            if (lossAnnounced)
            {
                lossAnnounced = false;
                Log.Info("[ibkr] back: quotes and depth are coming from IBKR again");
                FeedAlarm.PlayAllClear();
                this.push(MessageDealTicket.CreateInfoDealTicket(Header, "IBKR is back: bid, ask and depth are coming from IBKR again."));
            }

            return;
        }

        if (this.notLiveSinceUtc == default)
            this.notLiveSinceUtc = utcNow;

        if (lossAnnounced || utcNow - this.notLiveSinceUtc < WarnAfter || FeedHealth.SilenceLimit(utcNow) is not { } limit || limit >= QuietSession)
            return;

        lossAnnounced = true;
        string why = current.State == IbkrState.Refused
            ? "Gateway is logged in to a live account, which isn't allowed yet"
            : "Gateway isn't answering";
        Log.Info($"[ibkr] warning: {why}, state {current.State}");
        FeedAlarm.PlayWarning();
        this.push(MessageDealTicket.CreateRefuseDealTicket(Header, $"IBKR DOWN: {why}. Bid and ask are from tastytrade, which isn't the NBBO, there's no depth, and the tape is missing ARCA, Cboe, NYSE and other venues."));
    }

    public void OnIbkrQuote(string symbol, double bid, double bidSize, double ask, double askSize)
    {
        if (!this.statesByIbkr.TryGetValue(symbol, out var state))
            return;

#if TESTHOOKS
        if (File.Exists(Path.Combine(Log.AppDir, "test-ibkr-halt")))
            bid = ask = -1;
#endif

        state.IbkrBid = bid;
        state.IbkrAsk = ask;
        state.RecentQuotes.Add(bid, ask, FeedClock.UtcNowTicks);
        if (!state.QuoteOn)
            return;

        state.IbkrQuotes = true;

        // A side with no market has to show as empty. Skipping it would leave the old price up,
        // and in a halt or a one-sided book that price is gone.
        this.push(new Quote(
            state.Id,
            bid > 0 ? bid : double.NaN,
            bid > 0 ? bidSize : 0,
            ask > 0 ? ask : double.NaN,
            ask > 0 ? askSize : 0,
            FeedClock.UtcNow));
    }

    public void OnIbkrDepth(string symbol, List<IbkrDepthRow> bids, List<IbkrDepthRow> asks)
    {
        if (this.statesByIbkr.TryGetValue(symbol, out var state) && state.DepthOn)
            this.PushBook(state, bids, asks);
    }

    public void OnIbkrHalted(string symbol, int halted)
    {
        if (!this.statesByIbkr.TryGetValue(symbol, out var state) || halted == state.IbkrHalted)
            return;

        int before = state.IbkrHalted;
        bool wasKnown = before >= 0;
        PauseClock.Shared.OnHalted(symbol, before, halted, Stopwatch.GetTimestamp());
        state.IbkrHalted = halted;
        state.PauseEstimateLogged = false;

        // Which way it went, asked of the band before the halt stops it. A pause turning into a halt keeps it.
        if (halted <= 0)
        {
            state.HaltWay = ResSide.Unknown;
        }
        else if (before <= 0)
        {
            state.HaltWay = ResSide.FromBand(state.Luld, state.LastPrice, FeedClock.UtcNowTicks);
            if (state.HaltWay != ResSide.Unknown)
                Log.Info($"[res] {symbol} halted {ResSide.Word(state.HaltWay)} at {state.LastPrice}, so its Res row goes among the {ResSide.Side(state.HaltWay)}");
        }

        state.Luld.OnHalt(halted > 0);

        // Auction numbers from before a halt are the opening or closing cross's, and after it they're stale.
        state.ResPrice = state.ResPaired = state.ResImbalance = double.NaN;
        if (wasKnown || halted > 0)
            Log.Info($"[luld] {symbol} {(halted == 2 ? "volatility pause" : halted > 0 ? "halted" : "trading again; the reopening print sets the band")}");
    }

    /// <summary>The scanner's list, in tastytrade's symbols.</summary>
    public void OnIbkrScan(IReadOnlyList<string> symbols) =>
        this.topGainers?.Invoke(symbols.Select(IbkrClient.FromIbkrSymbol).ToList());

    public void OnIbkrQuoteStopped(string symbol)
    {
        if (this.statesByIbkr.TryGetValue(symbol, out var state))
            ForgetHalt(symbol, state, keepPauseStart: false);
    }

    /// <summary>
    /// What IBKR's quote stream said about a halt holds only while it streams. A new stream sends the Halted tick
    /// only when it isn't 0, so a pause that ended unwatched stayed on, Res row and all, while the symbol traded:
    /// GDHG on 2026-11-16, its DOM moved to another symbol mid-pause and back after the reopening. And a symbol
    /// left at 0 then met mid-pause counted down from when it was met, not from when the pause began.
    /// </summary>
    private static void ForgetHalt(string symbol, SymbolState state, bool keepPauseStart)
    {
        if (!keepPauseStart)
            PauseClock.Shared.Forget(symbol);

        int was = state.IbkrHalted;
        state.IbkrHalted = -1;
        state.PauseEstimateLogged = false;
        if (was <= 0)
            return;

        state.ResPrice = state.ResPaired = state.ResImbalance = double.NaN;
        state.HaltWay = ResSide.Unknown;
        state.Luld.ForgetHalt();
        Log.Info($"[luld] {symbol} not streaming from IBKR mid-{(was == 2 ? "pause" : "halt")}, so it's forgotten until IBKR says it again");
    }

    public bool OnIbkrAuction(string symbol, int tick, double value)
    {
        // Only a halt's reopening auction makes a Res row; the opening and closing crosses don't.
        if (!this.statesByIbkr.TryGetValue(symbol, out var state) || state.IbkrHalted <= 0)
            return false;

        switch (tick)
        {
            case IbkrClient.AuctionPriceTick:
                double price = Math.Round(value, 4);
                if (!(price > 0) || price == state.ResPrice)
                    return false;
                state.ResPrice = price;
                Log.Info($"[res] {symbol} reopening auction at {price}, {state.ResPaired:#,0} paired, imbalance {state.ResImbalance:+#,0;-#,0;0}");
                return state.DepthOn;
            case IbkrClient.AuctionVolumeTick:
                state.ResPaired = value;
                return false;
            case IbkrClient.AuctionImbalanceTick:
                state.ResImbalance = value;
                return false;
            default:
                return false;
        }
    }

    public void OnIbkrPrint(string symbol, double price, double size, long unixSeconds, string venue, long arrivalUtcTicks, bool roundLot, bool reopening)
    {
        if (!this.statesByIbkr.TryGetValue(symbol, out var state) || !state.LastOn)
            return;

        if (this.client?.TapeMode == TapeMode.Ibkr)
        {
            state.IbkrTape = true;
            if (state.IbkrTapeLost)
            {
                state.IbkrTapeLost = false;
                Log.Info($"[ibkr] {symbol} tape is printing again, back on IBKR's");
            }
        }

        double bid = state.IbkrBid;
        double ask = state.IbkrAsk;
        if (!PrintFilter.IsBelievable(price, bid, ask, state.LastPrice))
        {
            Interlocked.Increment(ref tooFar);
            return;
        }

        if (this.IsMerged && venue == PrintFilter.ReportedVenue)
        {
            if (!state.Merge.OnIbkrReportedPrint(price, size, unixSeconds, arrivalUtcTicks))
            {
                Interlocked.Increment(ref reportedAlreadyShown);
                return;
            }

            Interlocked.Increment(ref reportedAdded);
        }

        // Stamped when it arrived, since IBKR gives only whole seconds. Quantower's latency measure
        // reads this stamp's age, and backdating it made that number blink between these rows and
        // tastytrade's. A print near a bar's end can land in the next bar until the chart reloads.
        this.emitPrint(state, price, size, arrivalUtcTicks, bid, ask, roundLot, reopening);
    }

    public void OnIbkrTapeActive(string symbol, double price, double size, long unixSeconds, bool carried)
    {
        if (!this.statesByIbkr.TryGetValue(symbol, out var state))
            return;

        state.IbkrTapeWatch.OnIbkrPrint();
        if (carried)
            state.Merge.OnIbkrCarriedPrint(price, size, unixSeconds, FeedClock.UtcNowTicks);
    }

    public void OnIbkrState(IbkrState newState, string detail)
    {
        if (newState == IbkrState.Live)
            return;

        foreach (var (symbol, state) in this.statesByIbkr)
        {
            state.IbkrQuotes = false;
            state.IbkrTape = false;
            state.IbkrBid = double.NaN;
            state.IbkrAsk = double.NaN;

            // A reconnect is usually seconds, so a pause's start is kept for when IBKR says it again.
            ForgetHalt(symbol, state, keepPauseStart: true);
            if (state.DepthOn)
                this.PushBook(state, NoRows, NoRows);
        }
    }

    public void OnIbkrProblem(string symbol, string feed, int code, string message)
    {
        if (!this.statesByIbkr.TryGetValue(symbol, out var state))
            return;

        if (feed == IbkrClient.QuoteFeed)
            state.IbkrQuotes = false;
        else if (feed == IbkrClient.TapeFeed)
            state.IbkrTape = false;

        // A venue quote's problem only takes that venue's rows out, which the client does itself.
        else if (feed == IbkrClient.DepthFeed && state.DepthOn)
            this.PushBook(state, NoRows, NoRows);
    }

    /// <summary>
    /// A paused symbol's book goes out again each second, so its Res row's countdown moves even when nothing
    /// else in the book does. The client sends it from its own thread, the next time any data arrives.
    /// </summary>
    private void RefreshCountdowns(IbkrClient current)
    {
        long now = Stopwatch.GetTimestamp();
        foreach (var (symbol, state) in this.statesByIbkr)
        {
            if (state.DepthOn && state.IbkrHalted > 0 && state.ResPrice > 0 && (PauseClock.Shared.SecondsLeft(symbol, now) ?? EstimatedSecondsLeft(state)) != null)
                current.RefreshBook(symbol);
        }
    }

    private void CheckTapes(DateTime utcNow)
    {
        foreach (var (symbol, state) in this.statesByIbkr)
        {
            if (state.IbkrTape && state.IbkrTapeWatch.TakeWentQuiet(utcNow.Ticks))
            {
                state.IbkrTape = false;
                state.IbkrTapeLost = true;
                Log.Info($"[ibkr] {symbol} tape went quiet while tastytrade's printed, showing tastytrade's until it prints");
            }
        }
    }

    private void PushBook(SymbolState state, List<IbkrDepthRow> bids, List<IbkrDepthRow> asks)
    {
        var now = FeedClock.UtcNow;
        var book = new DOMQuote(state.Id, now);
        double bestBid = DepthSizes.Best(bids, true);
        double bestAsk = DepthSizes.Best(asks, false);
        bool hasBook = bids.Count + asks.Count > 0;

        // The computed LULD bands go in as one marked row a side, the lower among the bids and the upper
        // among the asks. Never into an empty book, where they'd pass for the market, and nor does Res.
        if (hasBook && state.Luld.TryGetBands(now.Ticks, state.PrevClose, state.IsTier1, out var band))
        {
            if (band.Lower > 0)
                bids = this.WithMarkerRow(bids, band.Lower, LuldBand.MarkerShares, LuldBand.Label(false, band.Exact), true, this.bidsWithBand);
            asks = this.WithMarkerRow(asks, band.Upper, LuldBand.MarkerShares, LuldBand.Label(true, band.Exact), false, this.asksWithBand);
            LogBand(state, band);
        }

        // During a halt or pause, the price the listing market would reopen at now: among the asks for a halt
        // up and the bids for a halt down (ResSide), on both sides while which way isn't known, sized by the
        // seconds until the reopening where that's known. Paired shares and imbalance are in the log.
        if (hasBook && state.IbkrHalted > 0 && state.ResPrice > 0)
        {
            int? left = PauseClock.Shared.SecondsLeft(IbkrClient.ToIbkrSymbol(state.Streamer), Stopwatch.GetTimestamp());
            if (left == null && (left = EstimatedSecondsLeft(state)) != null && !state.PauseEstimateLogged)
            {
                state.PauseEstimateLogged = true;
                Log.Info($"[res] {state.Streamer} opened mid-pause, last trade at {DateTimeOffset.FromUnixTimeMilliseconds(state.LastTradeTimeMs).LocalDateTime:HH:mm:ss.fff}, " +
                         $"so the countdown is estimated from that: {left} s to the reopening");
            }

            // A halt whose start the band didn't see goes the way its reopening points, decided once.
            if (state.HaltWay == ResSide.Unknown)
            {
                double last = LastTrade(state);
                state.HaltWay = ResSide.FromAuction(state.ResPrice, last);
                if (state.HaltWay != ResSide.Unknown)
                    Log.Info($"[res] {state.Streamer} which way it halted wasn't seen, so the auction at {state.ResPrice}, " +
                             $"{(state.HaltWay == ResSide.Up ? "above" : "below")} the last trade at {last}, puts its Res row among the {ResSide.Side(state.HaltWay)}");
            }

            double size = PauseClock.RowSize(left, MarkerRowSize);
            if (ResSide.OnBids(state.HaltWay))
                bids = this.WithMarkerRow(bids, state.ResPrice, size, ResVenue, true, this.bidsWithRes);
            if (ResSide.OnAsks(state.HaltWay))
                asks = this.WithMarkerRow(asks, state.ResPrice, size, ResVenue, false, this.asksWithRes);
        }

        Fill(book.Bids, bids, QuotePriceType.Bid, this.bidIds, state.Id, now, bestBid, state.IbkrBid);
        Fill(book.Asks, asks, QuotePriceType.Ask, this.askIds, state.Id, now, bestAsk, state.IbkrAsk);
        this.push(book);
    }

    /// <summary>The last trade: the Trade stream's once it's current, since the tape may not have printed since the symbol was last open.</summary>
    private static double LastTrade(SymbolState state) =>
        state.TradeTimeCurrent && state.LastTradePrice > 0 ? state.LastTradePrice : state.LastPrice;

    /// <summary>For a pause whose start wasn't seen: the countdown from the last trade, once tastytrade's Trade stream is current.</summary>
    private static int? EstimatedSecondsLeft(SymbolState state) =>
        state.IbkrHalted == 2 && state.TradeTimeCurrent
            ? PauseClock.EstimatedLeft(state.LastTradeTimeMs, (FeedClock.UtcNowTicks - DateTime.UnixEpoch.Ticks) / TimeSpan.TicksPerMillisecond)
            : null;

    private List<IbkrDepthRow> WithMarkerRow(List<IbkrDepthRow> rows, double price, double shares, string venue, bool bids, List<IbkrDepthRow> into)
    {
        this.bandRow.Clear();
        this.bandRow.Add(new IbkrDepthRow(price, shares, venue));
        VenueTops.Merge(rows, this.bandRow, bids, into);
        return into;
    }

    private static void LogBand(SymbolState state, LuldBand.Bands band)
    {
        if (band.Upper == state.LuldLoggedUpper && (band.Lower == state.LuldLoggedLower || (double.IsNaN(band.Lower) && double.IsNaN(state.LuldLoggedLower))))
            return;

        state.LuldLoggedLower = band.Lower;
        state.LuldLoggedUpper = band.Upper;
        Log.Info($"[luld] {state.Streamer} bands {band.Lower}/{band.Upper}, reference {band.Reference:0.####}, " +
                 $"{(state.IsTier1 ? "Tier 1" : "Tier 2")}, prev close {state.PrevClose}" +
                 (band.Exact ? "" : ", picked up mid-session so it can be off by up to 1%"));
    }

    private static void Fill(List<Level2Quote> target, List<IbkrDepthRow> rows, QuotePriceType side, string[] ids, string symbolId, DateTime now, double bestInBook, double quoted)
    {
        // Several venues can sit at one price, so a row's position is its identity, not its price.
        foreach (var row in rows)
        {
            if (target.Count == ids.Length)
                break;

            bool res = row.Venue == ResVenue;
            if (res || LuldBand.IsLabel(row.Venue) || DepthSizes.Shows(row.Size, row.Price, bestInBook, quoted))
            {
                // The Res row's size is already what it should read. A Limit row's 100 shares read 1.
                double size = res ? row.Size : DepthSizes.ToHundreds(row.Size);
                target.Add(new Level2Quote(side, symbolId, ids[target.Count], row.Price, size, now) { Broker = row.Venue });
            }
        }
    }
}

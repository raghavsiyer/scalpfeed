using System.Collections.Concurrent;
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

    // IBKR's receive thread only: the book with a LULD band's row put in, then the Res row.
    private readonly List<IbkrDepthRow> bandRow = new(1);
    private readonly List<IbkrDepthRow> bidsWithBand = new();
    private readonly List<IbkrDepthRow> asksWithBand = new();
    private readonly List<IbkrDepthRow> bidsWithRes = new();
    private readonly List<IbkrDepthRow> asksWithRes = new();

    private IbkrClient client;
    private DateTime notLiveSinceUtc;

    public IbkrBridge(Action<Message> push, PrintHandler emitPrint)
    {
        this.push = push;
        this.emitPrint = emitPrint;
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

        bool wasKnown = state.IbkrHalted >= 0;
        state.IbkrHalted = halted;
        state.Luld.OnHalt(halted > 0);

        // Auction numbers from before a halt are the opening or closing cross's, and after it they're stale.
        state.ResPrice = state.ResPaired = state.ResImbalance = double.NaN;
        if (wasKnown || halted > 0)
            Log.Info($"[luld] {symbol} {(halted == 2 ? "volatility pause" : halted > 0 ? "halted" : "trading again; the reopening print sets the band")}");
    }

    public bool OnIbkrAuction(string symbol, int tick, double value)
    {
        // Only a halt's reopening auction makes a Res row; the opening and closing crosses don't.
        if (!this.statesByIbkr.TryGetValue(symbol, out var state) || state.IbkrHalted <= 0)
            return false;

        bool showing = state.DepthOn && state.ResPrice > 0;
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
                bool pairedChanged = value != state.ResPaired;
                state.ResPaired = value;
                return showing && pairedChanged;
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

        foreach (var state in this.statesByIbkr.Values)
        {
            state.IbkrQuotes = false;
            state.IbkrTape = false;
            state.IbkrBid = double.NaN;
            state.IbkrAsk = double.NaN;
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

        // During a halt or pause, the price the listing market would reopen at now, on both sides so it
        // stays put as the imbalance swings, sized by the shares it would pair.
        if (hasBook && state.IbkrHalted > 0 && state.ResPrice > 0)
        {
            double paired = state.ResPaired > 0 ? state.ResPaired : LuldBand.MarkerShares;
            bids = this.WithMarkerRow(bids, state.ResPrice, paired, ResVenue, true, this.bidsWithRes);
            asks = this.WithMarkerRow(asks, state.ResPrice, paired, ResVenue, false, this.asksWithRes);
        }

        Fill(book.Bids, bids, QuotePriceType.Bid, this.bidIds, state.Id, now, bestBid, state.IbkrBid);
        Fill(book.Asks, asks, QuotePriceType.Ask, this.askIds, state.Id, now, bestAsk, state.IbkrAsk);
        this.push(book);
    }

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

            bool marker = LuldBand.IsLabel(row.Venue) || row.Venue == ResVenue;
            if (marker || DepthSizes.Shows(row.Size, row.Price, bestInBook, quoted))
                target.Add(new Level2Quote(side, symbolId, ids[target.Count], row.Price, DepthSizes.ToHundreds(row.Size), now) { Broker = row.Venue });
        }
    }
}

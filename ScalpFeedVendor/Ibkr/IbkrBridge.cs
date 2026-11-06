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
/// tape mode: IBKR's whole tape once its first print arrives, or tastytrade's plus IBKR's other
/// venues. If IBKR goes down, has no slot for a symbol, or its tape goes quiet while tastytrade's
/// carries on, that tape is simply tastytrade's alone until IBKR prints again.
/// </summary>
internal sealed class IbkrBridge : IIbkrSink
{
    /// <summary>Puts one print on the symbol's tape, with the quote that decides its side.</summary>
    public delegate void PrintHandler(SymbolState state, double price, double size, long utcTicks, double bid, double ask);

    private const string Header = "ScalpFeed";
    private static readonly List<IbkrDepthRow> NoRows = new();

    // IBKR's prints come stamped in whole seconds, about this long after they happened (measured
    // 2026-11-05: 115 ms behind tastytrade, itself about 60 ms behind the exchange). Backing off
    // by this much keeps them from pushing the times of tastytrade's prints forward.
    private static readonly long TypicalLagTicks = TimeSpan.FromMilliseconds(175).Ticks;

    // A blip is over before a warning would help.
    private static readonly TimeSpan WarnAfter = TimeSpan.FromSeconds(2);

    // Longer limits mean a quiet overnight session, where silence isn't a sign of trouble.
    private static readonly TimeSpan QuietSession = TimeSpan.FromSeconds(30);

    // Quantower builds a new vendor for every reconnect, so these outlive any one instance.
    private static volatile bool lossAnnounced;
    private static long tooFar;

    private readonly Action<Message> push;
    private readonly PrintHandler emitPrint;
    private readonly ConcurrentDictionary<string, SymbolState> statesByIbkr = new();
    private readonly string[] bidIds;
    private readonly string[] askIds;

    private IbkrClient client;
    private DateTime notLiveSinceUtc;

    public IbkrBridge(Action<Message> push, PrintHandler emitPrint)
    {
        this.push = push;
        this.emitPrint = emitPrint;
        this.bidIds = Enumerable.Range(0, 200).Select(i => "b" + i).ToArray();
        this.askIds = Enumerable.Range(0, 200).Select(i => "a" + i).ToArray();
    }

    public void Start() => this.client = IbkrSession.Attach(this);

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

        var utcNow = DateTime.UtcNow;
        this.CheckTapes(utcNow);
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
            DateTime.UtcNow));
    }

    public void OnIbkrDepth(string symbol, List<IbkrDepthRow> bids, List<IbkrDepthRow> asks)
    {
        if (this.statesByIbkr.TryGetValue(symbol, out var state) && state.DepthOn)
            this.PushBook(state, bids, asks);
    }

    public void OnIbkrPrint(string symbol, double price, double size, long unixSeconds, string venue, long arrivalUtcTicks)
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

        // Stays inside the second IBKR says it happened in, so it lands in the right bar.
        long lastTickOfSecond = DateTime.UnixEpoch.Ticks + ((unixSeconds + 1) * TimeSpan.TicksPerSecond) - 1;
        this.emitPrint(state, price, size, Math.Min(arrivalUtcTicks - TypicalLagTicks, lastTickOfSecond), bid, ask);
    }

    public void OnIbkrTapeActive(string symbol)
    {
        if (this.statesByIbkr.TryGetValue(symbol, out var state))
            state.IbkrTapeWatch.OnIbkrPrint();
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
        else if (state.DepthOn)
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
        var now = DateTime.UtcNow;
        var book = new DOMQuote(state.Id, now);
        Fill(book.Bids, bids, QuotePriceType.Bid, this.bidIds, state.Id, now);
        Fill(book.Asks, asks, QuotePriceType.Ask, this.askIds, state.Id, now);
        this.push(book);
    }

    private static void Fill(List<Level2Quote> target, List<IbkrDepthRow> rows, QuotePriceType side, string[] ids, string symbolId, DateTime now)
    {
        // Several venues can sit at one price, so a row's position is its identity, not its price.
        int count = Math.Min(rows.Count, ids.Length);
        for (int i = 0; i < count; i++)
        {
            var row = rows[i];
            target.Add(new Level2Quote(side, symbolId, ids[i], row.Price, row.Size, now) { Broker = row.Venue });
        }
    }
}

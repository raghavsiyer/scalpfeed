using ScalpFeedVendor.Feed;

namespace ScalpFeedVendor.Tastytrade;

/// <summary>
/// Per-symbol state. The volatile flags are set from Quantower's threads; everything else
/// belongs to tastytrade's receive thread, except where noted. The tape's fields are shared
/// with IBKR's receive thread, under a lock on this object, because merged prints come from both.
/// </summary>
internal sealed class SymbolState
{
    public SymbolState(string streamer) => this.Streamer = streamer;

    public string Streamer { get; }

    public volatile string Id;
    public volatile bool QuoteOn;
    public volatile bool LastOn;
    public volatile bool DepthOn;

    /// <summary>IBKR is quoting this symbol, so tastytrade's quote stays off the screen.</summary>
    public volatile bool IbkrQuotes;

    /// <summary>IBKR is supplying this symbol's whole tape, so tastytrade's prints stay off it.</summary>
    public volatile bool IbkrTape;

    /// <summary>The tape went quiet and tastytrade's took over, so its return is worth a log line.</summary>
    public volatile bool IbkrTapeLost;
    public readonly TapeWatch IbkrTapeWatch = new();

    /// <summary>Used in merged mode, from both feeds' receive threads.</summary>
    public readonly TapeMerge Merge = new();
    public bool IsCanary;

    /// <summary>Computed LULD bands, fed by the tape. Locks for itself, since both feeds print.</summary>
    public readonly LuldBand Luld = new();

    // Belong to IBKR's receive thread: what was last said in the log about this symbol's LULD.
    public int IbkrHalted = -1;

    // The reopening auction's indicative price, paired shares and imbalance, from IBKR during a halt or pause only.
    public double ResPrice = double.NaN;
    public double ResPaired = double.NaN;
    public double ResImbalance = double.NaN;
    public bool PauseEstimateLogged;

    // Which way the halt went, for the side its Res row goes on (ResSide): 1 up, -1 down, 0 not known yet.
    public int HaltWay;

    // The Watchlist row shows a halt's Res price and countdown, so the first row after the halt clears them.
    public volatile bool HaltColumnsShown;
    public double LuldLoggedLower = double.NaN;
    public double LuldLoggedUpper = double.NaN;

    public bool IsTier1 => LuldBand.IsTier1(this.Streamer);

    // Belong to IBKR's receive thread: the quote its prints are judged against.
    public double IbkrBid = double.NaN;
    public double IbkrAsk = double.NaN;
    public readonly QuoteWindow RecentQuotes = new();

    public long LastPrintIndex;
    public long LastPrintTicks;
    public long LastTradeTimeMs;

    // The Trade stream's last price, written by tastytrade's receive thread. A symbol opened mid-halt has printed
    // nothing, so this is its only last trade to judge the Res row's side by.
    public double LastTradePrice = double.NaN;

    // Whether LastTradeTimeMs is from the Trade stream as it is now. A resubscribed stream's first event brings it
    // up to date, and until then it can be from before the symbol was last closed.
    public volatile bool TradeTimeCurrent;
    public long NextDayBarTicks;
    public double LastPrice = double.NaN;
    public double LastSize = double.NaN;
    public double DayVolume = double.NaN;
    public double DayTurnover = double.NaN;
    public double Open = double.NaN;
    public double High = double.NaN;
    public double Low = double.NaN;
    public double PrevClose = double.NaN;

    // dxFeed's Summary dayClosePrice: the session's close once it's in, which after hours the change is measured from.
    public double DayClose = double.NaN;
}

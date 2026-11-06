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

    // Belong to IBKR's receive thread: the quote its prints are judged against.
    public double IbkrBid = double.NaN;
    public double IbkrAsk = double.NaN;

    public long LastPrintIndex;
    public long LastPrintTicks;
    public long LastTradeTimeMs;
    public long NextDayBarTicks;
    public double LastPrice = double.NaN;
    public double LastSize = double.NaN;
    public double DayVolume = double.NaN;
    public double Open = double.NaN;
    public double High = double.NaN;
    public double Low = double.NaN;
    public double PrevClose = double.NaN;
}

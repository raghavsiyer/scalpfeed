namespace ScalpFeedVendor.Feed;

internal enum PrintSide : byte { Undefined, Buy, Sell }

internal enum SaleKind : byte { New, Correction, Cancel }

/// <summary>dxFeed event flags on time-series events.</summary>
internal static class EventFlags
{
    public const int TxPending = 0x01;
    public const int Remove = 0x02;
    public const int SnapshotBegin = 0x04;
    public const int SnapshotEnd = 0x08;
    public const int SnapshotSnip = 0x10;
}

/// <summary>One symbol as it appears on the wire, kept so rows never allocate a string.</summary>
internal sealed class FeedSymbol
{
    public FeedSymbol(string name, byte[] utf8)
    {
        this.Name = name;
        this.Utf8 = utf8;
    }

    public string Name { get; }

    public byte[] Utf8 { get; }

    /// <summary>Per-symbol state owned by the sink. Touched only on the receive thread.</summary>
    public object Tag { get; set; }
}

/// <summary>
/// A single decoded event. The parser reuses one instance, so a sink must copy what it
/// keeps and must not hold on to the row itself.
/// </summary>
internal sealed class FeedRow
{
    public FeedSymbol Symbol;
    public int Flags;
    public long Index;
    public long Time;
    public long BidTime;
    public long AskTime;
    public long Count;
    public double Price;
    public double Size;
    public double Bid;
    public double Ask;
    public double BidSize;
    public double AskSize;
    public double DayVolume;
    public double Open;
    public double High;
    public double Low;
    public double Close;
    public double Volume;
    public double PrevClose;
    public double PrevVolume;
    public char Exchange;
    public PrintSide Aggressor;
    public SaleKind Sale;
    public bool ExtendedHours;
    public bool ValidTick;
    public bool NormalOddLot;

    /// <summary>Condition 5: the listing market's reopening auction after a halt or pause.</summary>
    public bool Reopening;

    /// <summary>Arrival time of the frame, in UTC ticks, stamped before parsing.</summary>
    public long ArrivalUtcTicks;

    public void Reset()
    {
        this.Symbol = null;
        this.Flags = 0;
        this.Index = 0;
        this.Time = 0;
        this.BidTime = 0;
        this.AskTime = 0;
        this.Count = 0;
        this.Price = double.NaN;
        this.Size = double.NaN;
        this.Bid = double.NaN;
        this.Ask = double.NaN;
        this.BidSize = double.NaN;
        this.AskSize = double.NaN;
        this.DayVolume = double.NaN;
        this.Open = double.NaN;
        this.High = double.NaN;
        this.Low = double.NaN;
        this.Close = double.NaN;
        this.Volume = double.NaN;
        this.PrevClose = double.NaN;
        this.PrevVolume = double.NaN;
        this.Exchange = '\0';
        this.Aggressor = PrintSide.Undefined;
        this.Sale = SaleKind.New;
        this.ExtendedHours = false;
        this.ValidTick = true;
        this.NormalOddLot = false;
        this.Reopening = false;
    }
}

internal interface IFeedSink
{
    void OnQuote(FeedRow row);

    void OnPrint(FeedRow row);

    void OnTrade(FeedRow row);

    void OnSummary(FeedRow row);

    void OnCandle(FeedRow row);
}

namespace ScalpFeedIndicators;

/// <summary>One rolling candle: the open, high, low, close and volume of the trades in the window, and how many.</summary>
public readonly record struct RollingBar(double Open, double High, double Low, double Close, double Volume, int Trades)
{
    public static RollingBar None { get; } = new(double.NaN, double.NaN, double.NaN, double.NaN, 0, 0);

    public bool IsEmpty => double.IsNaN(this.Close);
}

/// <summary>
/// A single candle over a rolling window of the last N seconds (your idea, 2026-11-18), built like a normal candle
/// from the trades inside it: open the first trade in the window, high and low the extremes, close the latest
/// trade, volume their shares. As time passes, trades older than the window drop out, so it rolls rather than
/// starting fresh each period. With nothing traded in the window it is flat at the last trade, with no volume.
/// Times are arrival times on this PC's clock, for adding and for rolling alike, so a feed's own clock can't skew
/// the window. Trades are added from the feed's thread and read from the chart's, so it locks for both. This
/// depends on nothing in Quantower, so the harness can test it.
/// </summary>
public sealed class RollingCandle
{
    private readonly object gate = new();
    private readonly Queue<(long Ticks, double Price, double Size)> trades = new();
    private double last = double.NaN;
    private long window;

    public RollingCandle(TimeSpan window) => this.Window = window;

    public TimeSpan Window
    {
        get => TimeSpan.FromTicks(Volatile.Read(ref this.window));
        set => Volatile.Write(ref this.window, Math.Max(TimeSpan.TicksPerMillisecond, value.Ticks));
    }

    /// <summary>A trade that arrived at utcTicks. Prices of 0 or less, or sizes below 0, are skipped.</summary>
    public void Add(long utcTicks, double price, double size)
    {
        if (!(price > 0) || !(size >= 0))
            return;

        lock (this.gate)
        {
            this.trades.Enqueue((utcTicks, price, size));
            this.last = price;
        }
    }

    /// <summary>The candle at nowUtcTicks: the trades after nowUtcTicks minus the window, up to now.</summary>
    public RollingBar At(long nowUtcTicks)
    {
        lock (this.gate)
        {
            long start = nowUtcTicks - Volatile.Read(ref this.window);
            while (this.trades.Count > 0 && this.trades.Peek().Ticks <= start)
                this.trades.Dequeue();

            if (this.trades.Count == 0)
                return double.IsNaN(this.last) ? RollingBar.None : new RollingBar(this.last, this.last, this.last, this.last, 0, 0);

            double open = this.trades.Peek().Price;
            double high = open;
            double low = open;
            double volume = 0;
            foreach (var (_, price, size) in this.trades)
            {
                if (price > high)
                    high = price;
                if (price < low)
                    low = price;
                volume += size;
            }

            return new RollingBar(open, high, low, this.last, volume, this.trades.Count);
        }
    }

    public void Clear()
    {
        lock (this.gate)
        {
            this.trades.Clear();
            this.last = double.NaN;
        }
    }

    /// <summary>Shares as the label under the candle reads them: 850, 12.3K, 1.25M.</summary>
    public static string FormatVolume(double shares) => shares switch
    {
        >= 1_000_000 => $"{shares / 1_000_000:0.##}M",
        >= 10_000 => $"{shares / 1_000:0.#}K",
        >= 1_000 => $"{shares / 1_000:0.##}K",
        _ => $"{shares:0}",
    };
}

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

/// <summary>
/// How the rolling candle shows its volume (your ideas, 2026-11-18): against the stock's typical volume for a window
/// that long, so the look means "unusual", not just "the most lately". Typical is the average over the last few
/// minutes: the shares traded in them, spread evenly over windows of the candle's length. The window's volume over
/// typical is its ratio. Opacity goes by the ratio on a log scale: typical is half, the full multiple (2 by default)
/// full colour, its inverse (a half) or less a quarter's opacity. Above the glow multiple (4 by default) it's more
/// than full colour: a glow whose heat rises on the same log scale to its most at the glow multiple squared (16).
/// Until it has seen two windows' worth of time there's no telling what's typical, and the ratio is unknown. Sizes
/// are added as trades arrive, on this PC's clock like the candle. This depends on nothing in Quantower, so the
/// harness can test it.
/// </summary>
public sealed class VolumeBrightness
{
    /// <summary>The candle's opacity at no strength: dim, but still there.</summary>
    public const double MinOpacity = 0.25;

    private readonly object gate = new();
    private readonly Queue<(long Ticks, double Size)> trades = new();
    private double sum;
    private long firstTicks = long.MinValue;
    private long lookback;
    private long window;

    public VolumeBrightness(TimeSpan lookback, TimeSpan window)
    {
        this.Lookback = lookback;
        this.Window = window;
    }

    /// <summary>How far back typical volume is averaged.</summary>
    public TimeSpan Lookback
    {
        get => TimeSpan.FromTicks(Volatile.Read(ref this.lookback));
        set => Volatile.Write(ref this.lookback, Math.Max(TimeSpan.TicksPerSecond, value.Ticks));
    }

    /// <summary>The candle's window, the length typical volume is given for.</summary>
    public TimeSpan Window
    {
        get => TimeSpan.FromTicks(Volatile.Read(ref this.window));
        set => Volatile.Write(ref this.window, Math.Max(TimeSpan.TicksPerMillisecond, value.Ticks));
    }

    /// <summary>A trade's shares, as it arrives.</summary>
    public void Add(long utcTicks, double size)
    {
        if (!(size > 0))
            return;

        lock (this.gate)
        {
            if (this.firstTicks == long.MinValue)
                this.firstTicks = utcTicks;
            this.trades.Enqueue((utcTicks, size));
            this.sum += size;
        }
    }

    /// <summary>Typical volume for one window at utcTicks, or NaN until two windows' worth of time has been seen.</summary>
    public double Typical(long utcTicks)
    {
        lock (this.gate)
        {
            long back = Volatile.Read(ref this.lookback);
            long length = Volatile.Read(ref this.window);
            while (this.trades.Count > 0 && this.trades.Peek().Ticks <= utcTicks - back)
                this.sum -= this.trades.Dequeue().Size;
            if (this.trades.Count == 0)
                this.sum = 0;

            if (this.firstTicks == long.MinValue)
                return double.NaN;
            long seen = Math.Min(back, utcTicks - this.firstTicks);
            return seen < 2 * length ? double.NaN : this.sum / seen * length;
        }
    }

    /// <summary>The window's volume over typical: 1 is typical; NaN while typical isn't known yet.</summary>
    public double Ratio(long utcTicks, double windowVolume)
    {
        double typical = this.Typical(utcTicks);
        if (double.IsNaN(typical))
            return double.NaN;
        if (!(windowVolume > 0))
            return 0;
        return typical > 0 ? windowVolume / typical : double.PositiveInfinity;
    }

    /// <summary>Colour strength for a ratio, 0 to 1: a half at typical, full at fullMultiple, none at its inverse; a half while unknown.</summary>
    public static double Strength(double ratio, double fullMultiple)
    {
        if (double.IsNaN(ratio))
            return 0.5;
        if (!(ratio > 0))
            return 0;
        double full = Math.Max(1.1, fullMultiple);
        return Math.Clamp(0.5 + (0.5 * Math.Log(ratio) / Math.Log(full)), 0, 1);
    }

    /// <summary>A burst's glow for a ratio, 0 to 1: none up to glowMultiple, most at glowMultiple squared.</summary>
    public static double Heat(double ratio, double glowMultiple)
    {
        double glow = Math.Max(1.1, glowMultiple);
        if (double.IsNaN(ratio) || !(ratio > glow))
            return 0;
        return Math.Clamp(Math.Log(ratio / glow) / Math.Log(glow), 0, 1);
    }

    public void Clear()
    {
        lock (this.gate)
        {
            this.trades.Clear();
            this.sum = 0;
            this.firstTicks = long.MinValue;
        }
    }

    /// <summary>The colour's alpha for a strength: a quarter at none, full at full strength.</summary>
    public static int Alpha(double strength) =>
        (int)Math.Round(255 * (MinOpacity + ((1 - MinOpacity) * Math.Clamp(double.IsNaN(strength) ? 0 : strength, 0, 1))));
}

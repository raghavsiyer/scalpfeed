namespace ScalpFeedIndicators;

/// <summary>One rolling candle: the open, high, low, close and volume of the trades in the window, and how many.</summary>
public readonly record struct RollingBar(double Open, double High, double Low, double Close, double Volume, int Trades)
{
    public static RollingBar None { get; } = new(double.NaN, double.NaN, double.NaN, double.NaN, 0, 0);

    public bool IsEmpty => double.IsNaN(this.Close);
}

/// <summary>
/// The shares a rolling candle traded at one price, and of them how many were bought (at or above the ask) and sold
/// (at or below the bid), as the tape colours them; the rest traded between.
/// </summary>
public readonly record struct PriceLevel(double Price, double Shares, double Bought = 0, double Sold = 0);

/// <summary>Which side took a print, as the tape colours it: bought at or above the ask, sold at or below the bid.</summary>
public enum PrintTaker
{
    Between = 0,
    Buyer = 1,
    Seller = 2,
}

/// <summary>
/// How the rolling candle shades by price (your idea, 2026-11-18): each price as bright as the shares traded there,
/// so the candle shows where in it volume was and wasn't traded. Trades are rounded to their tick, a cent at $1 and
/// up and a hundredth of a cent below (Reg NMS's sub-penny rule), so a midpoint print at 5.005 counts at 5.01. A
/// price with fewer than the least shares (1,000 by default) is at the lowest intensity, as are prices nothing
/// traded at; above it, the busiest price gets the candle's colour (its strength against typical volume) and the
/// rest fall between by the square root of their shares over the busiest's, so a price with a quarter of the
/// busiest's shares is half way. A whole window under the least shares is all at the lowest intensity. The prices
/// are stops of one smooth gradient down the candle (your feedback, 2026-11-18: slices of shade looked boxy). Each
/// price's colour is by who took it (your idea, 2026-11-18): green where it was bought, at or above the ask, red where
/// it was sold, at or below the bid, as the tape colours them, and in between by the share bought, so big selling
/// in the middle of a green candle shows red there. Prints between the bid and ask count for brightness but not
/// colour; a price with none bought or sold, or under the least shares, and the quiet stretches between, keep the
/// candle's own colour. This depends on nothing in Quantower, so the harness can test it.
/// </summary>
public static class VolumeAtPrice
{
    /// <summary>Under this many shares at a price (or in the window), the lowest intensity.</summary>
    public const double DefaultMinShares = 1000;

    /// <summary>The price increment quotes come in: a cent at $1 and up, a hundredth of a cent below.</summary>
    public static double TickAt(double price) => price >= 1 ? 0.01 : 0.0001;

    /// <summary>A trade's price rounded to its tick, halves up.</summary>
    public static double Level(double price)
    {
        double tick = TickAt(price);
        return Math.Round(Math.Round(price / tick, MidpointRounding.AwayFromZero) * tick, 6);
    }

    /// <summary>
    /// The alpha for a price that traded these shares, when the busiest price in the window traded busiest and the
    /// candle's colour has alpha top: the lowest intensity under minShares, top at the busiest.
    /// </summary>
    public static int Alpha(double shares, double busiest, double minShares, int top)
    {
        int floor = VolumeBrightness.Alpha(0);
        if (!(shares > 0) || !(shares >= minShares) || !(busiest > 0))
            return floor;
        top = Math.Clamp(top, floor, 255);
        return (int)Math.Round(floor + ((top - floor) * Math.Sqrt(Math.Min(1, shares / busiest))));
    }

    /// <summary>
    /// The gradient down a candle drawn from yTop (its high) to yBottom (its low), toY giving a price's y: a stop at
    /// each traded price with its alpha, and one at the lowest intensity a tick either side where nothing traded, so a
    /// busy price fades into quiet ones over a tick instead of ending in a hard edge. Stops are merged a pixel row at a
    /// time (the row's middle, the brightest of them), so zoomed out, where many prices share a row, there's one stop
    /// a row; ones past the candle's ends count at the end. The first stop is at yTop and the last at yBottom, top
    /// to bottom in between. With nothing to go on, the whole candle is at the lowest intensity. Each stop says the
    /// share of its shares bought, from prices with at least minShares (the rest are NaN: the candle's colour).
    /// </summary>
    public static List<ShadeStop> Stops(IReadOnlyList<PriceLevel> levels, Func<double, double> toY, double yTop, double yBottom, double minShares, int top)
    {
        int floor = VolumeBrightness.Alpha(0);
        if (yBottom < yTop)
            (yTop, yBottom) = (yBottom, yTop);

        double busiest = 0;
        var traded = new HashSet<double>();
        foreach (var level in levels)
        {
            busiest = Math.Max(busiest, level.Shares);
            traded.Add(level.Price);
        }

        // Pixel row -> brightest alpha on it, and the shares bought and sold at the prices on it that count.
        var byRow = new SortedDictionary<long, (int Alpha, double Bought, double Sold)>();
        void Put(double price, int alpha, double bought, double sold)
        {
            double y = toY(price);
            if (double.IsNaN(y))
                return;
            long row = (long)Math.Floor(Math.Clamp(y, yTop, Math.Max(yTop, yBottom - 1e-6)));
            byRow[row] = byRow.TryGetValue(row, out var seen)
                ? (Math.Max(seen.Alpha, alpha), seen.Bought + bought, seen.Sold + sold)
                : (alpha, bought, sold);
        }

        foreach (var level in levels)
        {
            bool counts = level.Shares > 0 && level.Shares >= minShares;
            Put(level.Price, Alpha(level.Shares, busiest, minShares, top), counts ? level.Bought : 0, counts ? level.Sold : 0);
            double tick = TickAt(level.Price);
            foreach (double next in new[] { Level(level.Price + tick), Level(level.Price - tick) })
            {
                if (!traded.Contains(next))
                    Put(next, floor, 0, 0);
            }
        }

        var stops = new List<ShadeStop>(byRow.Count + 2);
        foreach (var (row, (alpha, bought, sold)) in byRow)
            stops.Add(new ShadeStop(Math.Clamp(row + 0.5, yTop, yBottom), alpha, bought + sold > 0 ? bought / (bought + sold) : double.NaN));
        if (stops.Count == 0)
            stops.Add(new ShadeStop(yTop, floor, double.NaN));
        if (stops[0].Y > yTop)
            stops.Insert(0, stops[0] with { Y = yTop });
        if (stops[^1].Y < yBottom)
            stops.Add(stops[^1] with { Y = yBottom });
        return stops;
    }
}

/// <summary>
/// One stop of a rolling candle's gradient: its y on the chart, its alpha there, and the share of its shares bought
/// (1 all bought, 0 all sold; NaN for the candle's own colour).
/// </summary>
public readonly record struct ShadeStop(double Y, int Alpha, double Buying);

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
    private readonly Queue<(long Ticks, double Price, double Size, PrintTaker Taker)> trades = new();
    private double last = double.NaN;
    private long window;

    public RollingCandle(TimeSpan window) => this.Window = window;

    public TimeSpan Window
    {
        get => TimeSpan.FromTicks(Volatile.Read(ref this.window));
        set => Volatile.Write(ref this.window, Math.Max(TimeSpan.TicksPerMillisecond, value.Ticks));
    }

    /// <summary>A trade that arrived at utcTicks, and who took it. Prices of 0 or less, or sizes below 0, are skipped.</summary>
    public void Add(long utcTicks, double price, double size, PrintTaker taker = PrintTaker.Between)
    {
        if (!(price > 0) || !(size >= 0))
            return;

        lock (this.gate)
        {
            this.trades.Enqueue((utcTicks, price, size, taker));
            this.last = price;
        }
    }

    /// <summary>The candle at nowUtcTicks: the trades after nowUtcTicks minus the window, up to now.</summary>
    public RollingBar At(long nowUtcTicks) => this.At(nowUtcTicks, null);

    /// <summary>
    /// The candle at nowUtcTicks, and into levels (cleared first, when given) the shares it traded at each price, each
    /// trade rounded to its tick (VolumeAtPrice.Level), with how many were bought and sold, highest price first: where
    /// in the candle volume was traded, and by whom.
    /// </summary>
    public RollingBar At(long nowUtcTicks, List<PriceLevel> levels)
    {
        levels?.Clear();
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
            Dictionary<double, PriceLevel> byLevel = levels == null ? null : new();
            foreach (var (_, price, size, taker) in this.trades)
            {
                if (price > high)
                    high = price;
                if (price < low)
                    low = price;
                volume += size;
                if (byLevel != null)
                {
                    double level = VolumeAtPrice.Level(price);
                    var was = byLevel.GetValueOrDefault(level);
                    byLevel[level] = new PriceLevel(
                        level,
                        was.Shares + size,
                        was.Bought + (taker == PrintTaker.Buyer ? size : 0),
                        was.Sold + (taker == PrintTaker.Seller ? size : 0));
                }
            }

            if (byLevel != null)
            {
                levels.AddRange(byLevel.Values);
                levels.Sort((a, b) => b.Price.CompareTo(a.Price));
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

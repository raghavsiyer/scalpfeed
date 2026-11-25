namespace ScalpFeedIndicators;

/// <summary>A bar a fixed range profile is worked out from.</summary>
public readonly record struct RangeBar(long TimeMs, double Open, double High, double Low, double Close, double Volume);

/// <summary>
/// A fixed range volume profile worked out the way TradingView publishes for its FRVP (your request, 2026-11-25: "make
/// sure its the exact same as frvp"). From its help pages "Fixed range volume profile drawing tool" and "Volume profile
/// indicators: basic concepts", and its charting library's volume profile page:
/// - lower timeframe bars: 1-second ones for a seconds chart or a range of 5 minutes or less, else the first of 1, 5, 15,
///   30, 60 and 240 minutes and a day with fewer than 5,000 bars in the range (Timeframe);
/// - a bar is up volume when it closes at or over its open, else down; on a range of a single chart bar only over it;
/// - a bar's volume goes to the rows its high to low crosses, in proportion to how much of it each one covers;
/// - 24 rows from the range's low, each a whole number of ticks, rounded whichever way brings the count nearer to 24, the
///   last row cut short at the high;
/// - the point of control is the row with the most volume; the value area grows from it by the larger of the next row
///   above and the next below, the nearer when equal and the upper when that's equal too, and stops before passing 70%.
/// It doesn't say which row wins a tie for the most: here the one nearest the middle, then the upper. This depends on
/// nothing in Quantower, so the harness can test it.
/// </summary>
public sealed class FixedProfile
{
    public const int MostBars = 5_000;
    private const int MostRows = 6_000;

    private readonly double bottom;
    private readonly double top;
    private readonly double rowHeight;

    private FixedProfile(double bottom, double top, double rowHeight, int ticksPerRow, double[] up, double[] down, int bars)
    {
        this.bottom = bottom;
        this.top = top;
        this.rowHeight = rowHeight;
        this.TicksPerRow = ticksPerRow;
        this.Up = up;
        this.Down = down;
        this.Bars = bars;
    }

    /// <summary>The timeframes a range can be worked out from, finest first, in minutes.</summary>
    public static IReadOnlyList<int> Timeframes { get; } = [1, 5, 15, 30, 60, 240, 1440];

    /// <summary>The up and down volume in each row, from the lowest price up.</summary>
    public double[] Up { get; }

    public double[] Down { get; }

    public int Rows => this.Up.Length;

    public int TicksPerRow { get; }

    /// <summary>How many bars it's from.</summary>
    public int Bars { get; }

    /// <summary>The row with the most volume.</summary>
    public int Poc { get; private set; }

    /// <summary>The value area's top row and bottom row.</summary>
    public int ValueHigh { get; private set; }

    public int ValueLow { get; private set; }

    public double Total(int row) => this.Up[row] + this.Down[row];

    public double Bottom(int row) => this.bottom + (row * this.rowHeight);

    public double Top(int row) => Math.Min(this.top, this.Bottom(row + 1));

    /// <summary>Where a row's line goes.</summary>
    public double Middle(int row) => (this.Bottom(row) + this.Top(row)) / 2;

    /// <summary>
    /// How many ticks make a row: the span's ticks over the rows asked for, rounded whichever way brings the number of
    /// rows nearer to that, down when they're as near. 100 ticks are 4 a row for 25 rows and 3 for 30 (its 34 are nearer
    /// than 25).
    /// </summary>
    public static int TicksEach(long ticks, int rows)
    {
        rows = Math.Max(1, rows);
        long down = Math.Max(1, ticks / rows), up = ticks % rows == 0 ? down : (ticks / rows) + 1;
        long Count(long each) => (ticks + each - 1) / each;
        return (int)(Math.Abs(Count(down) - rows) <= Math.Abs(Count(up) - rows) ? down : up);
    }

    /// <summary>The tick TradingView gives a US stock at this price.</summary>
    public static double Tick(double price) => price < 1 ? 0.0001 : 0.01;

    /// <summary>
    /// The finest timeframe whose bars in a range may be fewer than 5,000, from its length: a stock trades 960 minutes a
    /// weekday. A thinner one has fewer, so one that comes to twice that is still to be counted.
    /// </summary>
    public static int FirstTimeframe(long startMs, long endMs)
    {
        double minutes = (endMs - startMs) / 60_000.0;
        double traded = minutes <= 960 ? minutes : Math.Max(960, minutes / 1440 * 5 / 7 * 960);
        foreach (int timeframe in Timeframes)
        {
            if (traded / timeframe < MostBars * 2)
                return timeframe;
        }

        return Timeframes[^1];
    }

    /// <param name="tick">The price tick.</param>
    /// <param name="size">How many rows, or with ticksEach how many ticks in each.</param>
    /// <param name="valueAreaShare">How much of the volume the value area holds, 0.7 for 70%.</param>
    /// <param name="singleBar">The range is one bar of the chart.</param>
    /// <returns>Null when nothing traded.</returns>
    public static FixedProfile Of(IReadOnlyList<RangeBar> bars, double tick, int size, bool ticksEach, double valueAreaShare, bool singleBar)
    {
        double low = double.MaxValue, high = double.MinValue;
        int used = 0;
        foreach (var bar in bars)
        {
            if (!Counts(bar))
                continue;

            used++;
            low = Math.Min(low, bar.Low);
            high = Math.Max(high, bar.High);
        }

        if (used == 0)
            return null;

        long first = (long)Math.Floor((low / tick) + 1e-6), last = (long)Math.Ceiling((high / tick) - 1e-6);
        long ticks = Math.Max(1, last - first);
        size = Math.Max(1, size);
        long perRow = ticksEach ? size : TicksEach(ticks, size);
        long rows = (ticks + perRow - 1) / perRow;
        if (rows > MostRows)
            return null;

        double bottom = first * tick, top = (first + ticks) * tick, rowHeight = perRow * tick;
        var up = new double[rows];
        var down = new double[rows];
        foreach (var bar in bars)
        {
            if (!Counts(bar))
                continue;

            var into = (singleBar ? bar.Close > bar.Open : bar.Close >= bar.Open) ? up : down;
            int from = Row(bar.Low), to = Row(bar.High);
            double span = bar.High - bar.Low;
            if (from == to || span <= 0)
            {
                into[from] += bar.Volume;
                continue;
            }

            for (int row = from; row <= to; row++)
            {
                double covered = Math.Min(bar.High, bottom + ((row + 1) * rowHeight)) - Math.Max(bar.Low, bottom + (row * rowHeight));
                if (covered > 0)
                    into[row] += bar.Volume * covered / span;
            }
        }

        var profile = new FixedProfile(bottom, top, rowHeight, (int)perRow, up, down, used);
        profile.Mark(Math.Clamp(valueAreaShare, 0, 1));
        return profile;

        int Row(double price) => Math.Clamp((int)Math.Floor(((price - bottom) / rowHeight) + 1e-9), 0, (int)rows - 1);
    }

    private static bool Counts(RangeBar bar) => bar.Volume > 0 && bar.Low > 0 && bar.High >= bar.Low;

    private void Mark(double share)
    {
        double total = 0, middle = (this.Rows - 1) / 2.0;
        int poc = 0;
        for (int i = 0; i < this.Rows; i++)
        {
            total += this.Total(i);
            bool nearer = Math.Abs(i - middle) <= Math.Abs(poc - middle);
            if (this.Total(i) > this.Total(poc) || (this.Total(i) == this.Total(poc) && nearer))
                poc = i;
        }

        int high = poc, low = poc;
        double left = (total * share) - this.Total(poc);
        while (high < this.Rows - 1 || low > 0)
        {
            bool up;
            if (high == this.Rows - 1)
                up = false;
            else if (low == 0)
                up = true;
            else if (this.Total(high + 1) != this.Total(low - 1))
                up = this.Total(high + 1) > this.Total(low - 1);
            else
                up = high + 1 - poc <= poc - (low - 1);

            double next = this.Total(up ? high + 1 : low - 1);
            if (next > left)
                break;

            left -= next;
            if (up)
                high++;
            else
                low--;
        }

        this.Poc = poc;
        this.ValueHigh = high;
        this.ValueLow = low;
    }
}

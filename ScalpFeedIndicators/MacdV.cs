namespace ScalpFeedIndicators;

/// <summary>
/// MACD-V (Spiroglou 2022): 100 times the fast EMA of the close less the slow, over the average true range (Wilder's),
/// with an EMA of that as its signal; 12, 26, 9 and 26 by default, as Quantower's built-in has them. Fed each bar as it
/// changes: a bar with the same time replaces itself, one with a later time follows, one with an earlier time starts
/// over. This depends on nothing in Quantower, so the harness can test it.
/// </summary>
public sealed class MacdV(int fast = 12, int slow = 26, int signal = 9, int atr = 26)
{
    private readonly record struct State(double Fast, double Slow, double Range, double Signal, double Close);

    private State? before;
    private State? now;
    private DateTime bar;

    public double Line { get; private set; } = double.NaN;

    public double Signal { get; private set; } = double.NaN;

    public void Clear()
    {
        this.before = this.now = null;
        this.bar = default;
        this.Line = this.Signal = double.NaN;
    }

    public void Update(DateTime time, double high, double low, double close)
    {
        if (time < this.bar)
            this.Clear();
        else if (time > this.bar)
            this.before = this.now;
        this.bar = time;

        double line, sig;
        if (this.before is not State last)
        {
            line = sig = 0;
            this.now = new State(close, close, high - low, 0, close);
        }
        else
        {
            double f = Ema(last.Fast, close, fast);
            double s = Ema(last.Slow, close, slow);
            double range = Math.Max(high - low, Math.Max(Math.Abs(high - last.Close), Math.Abs(low - last.Close)));
            double r = last.Range + ((range - last.Range) / atr);

            // The range is 0 only when every bar so far is flat, when the EMAs are the same too.
            line = r > 0 ? 100 * (f - s) / r : 0;
            sig = Ema(last.Signal, line, signal);
            this.now = new State(f, s, r, sig, close);
        }

        this.Line = line;
        this.Signal = sig;
    }

    private static double Ema(double last, double value, int length) => last + ((value - last) * 2 / (length + 1));
}

/// <summary>
/// Where one line crosses another, from the gap between them at each bar (by its time): up when the gap goes from 0 or
/// less to more, down the other way. This depends on nothing in Quantower, so the harness can test it.
/// </summary>
public sealed class Crossovers
{
    private readonly object gate = new();
    private readonly Dictionary<DateTime, double> gaps = new();

    public void Set(DateTime bar, double gap)
    {
        lock (this.gate)
            this.gaps[bar] = gap;
    }

    public void Clear()
    {
        lock (this.gate)
            this.gaps.Clear();
    }

    /// <summary>1 when the gap crosses up at this bar, -1 down, 0 neither or not known at both.</summary>
    public int At(DateTime previousBar, DateTime bar)
    {
        lock (this.gate)
        {
            if (!this.gaps.TryGetValue(previousBar, out double from) || !this.gaps.TryGetValue(bar, out double to))
                return 0;
            return from <= 0 && to > 0 ? 1 : from >= 0 && to < 0 ? -1 : 0;
        }
    }
}

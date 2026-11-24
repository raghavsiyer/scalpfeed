namespace ScalpFeedIndicators;

/// <summary>
/// The volume point of control as the session has gone: from each EndMs on, the price with the most shares traded
/// since the session's start, and the value area around it.
/// </summary>
public readonly record struct VpocPoint(long EndMs, long SessionStartMs, long SessionEndMs, double Poc, double High, double Low);

/// <summary>Stretches of trading, each from one set New York time to the next.</summary>
public sealed class EtSessions
{
    private static readonly TimeZoneInfo Eastern = TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");

    private readonly TimeSpan[] starts;
    private (long StartMs, long EndMs) last;

    public EtSessions(params TimeSpan[] startsEt)
    {
        if (startsEt == null || startsEt.Length == 0)
            throw new ArgumentException("At least one session start is needed.", nameof(startsEt));
        this.starts = startsEt.Distinct().OrderBy(s => s).ToArray();
    }

    /// <summary>The session a time is in: its start, and its end, the next one's start.</summary>
    public (long StartMs, long EndMs) Of(long timeMs)
    {
        if (timeMs >= this.last.StartMs && timeMs < this.last.EndMs)
            return this.last;

        var et = TimeZoneInfo.ConvertTimeFromUtc(DateTimeOffset.FromUnixTimeMilliseconds(timeMs).UtcDateTime, Eastern);
        int i = Array.FindLastIndex(this.starts, s => et.TimeOfDay >= s);
        var start = i >= 0 ? et.Date + this.starts[i] : et.Date.AddDays(-1) + this.starts[^1];
        var end = i < 0 ? et.Date + this.starts[0] : i + 1 < this.starts.Length ? et.Date + this.starts[i + 1] : et.Date.AddDays(1) + this.starts[0];
        return this.last = (Ms(start), Ms(end));
    }

    /// <summary>4:00 ET, where the trading day begins, this many weekdays back (0 is the day a time is in).</summary>
    public static long DayStart(long timeMs, int weekdaysBack)
    {
        var et = TimeZoneInfo.ConvertTimeFromUtc(DateTimeOffset.FromUnixTimeMilliseconds(timeMs).UtcDateTime, Eastern);
        var day = et.TimeOfDay >= TimeSpan.FromHours(4) ? et.Date : et.Date.AddDays(-1);
        while (Weekend(day))
            day = day.AddDays(-1);
        for (int back = 0; back < weekdaysBack; back++)
        {
            do
                day = day.AddDays(-1);
            while (Weekend(day));
        }

        return Ms(day.AddHours(4));
    }

    private static bool Weekend(DateTime day) => day.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday;

    private static long Ms(DateTime et) =>
        new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(et, DateTimeKind.Unspecified), Eastern)).ToUnixTimeMilliseconds();
}

/// <summary>
/// The dynamic volume point of control (your request, 2026-11-24; Quantower's own comes with its Volume Analysis
/// licence): the price with the most shares traded since the session's start, as it was at each moment of it, and the
/// value area, the prices around it that hold 70% of the shares. From the shares traded at each price in each period
/// (VolumeAtPriceFeed); a half step counts toward the nearest step, so a print at 1.2497 counts at 1.25. The value
/// area grows from the point of control by whichever two prices next above or below have more shares, the usual way.
/// This depends on nothing in Quantower, so the harness can test it.
/// </summary>
public sealed class DynamicVpoc
{
    // A print so far off that a row per step would be this many is a bad one, and its session isn't worked out.
    private const long MostRows = 200_000;

    private readonly double step;
    private readonly double share;
    private readonly EtSessions sessions;
    private readonly SortedDictionary<long, Session> bySession = new();

    /// <param name="step">The price step.</param>
    /// <param name="valueAreaShare">How much of the volume the value area holds, 0.7 for 70%.</param>
    /// <param name="sessions">Where it starts over.</param>
    public DynamicVpoc(double step, double valueAreaShare, EtSessions sessions)
    {
        this.step = step;
        this.share = Math.Clamp(valueAreaShare, 0, 1);
        this.sessions = sessions;
    }

    private sealed class Session
    {
        public long StartMs;
        public long EndMs;
        public readonly SortedDictionary<(long TimeMs, long LengthMs), Dictionary<long, double>> Periods = new();
        public VpocPoint[] Points;
    }

    /// <summary>The rows are complete from here, so a session that began before isn't drawn.</summary>
    public long CoveredFromMs { get; set; }

    /// <summary>The price step for a stock at this price: its tick, or past $20 about a twentieth of a percent.</summary>
    public static double AutoStep(double price)
    {
        double tick = price < 0.1 ? 0.0001 : price < 1 ? 0.001 : 0.01;
        double want = price * 0.0005;
        for (double power = 0.0001; ; power *= 10)
        {
            foreach (int times in (int[])[1, 2, 5])
            {
                double step = Math.Round(power * times, 4);
                if (step >= tick - 1e-9 && step >= want - 1e-9)
                    return step;
            }
        }
    }

    public void Clear() => this.bySession.Clear();

    /// <summary>The shares traded in one period within one half step (level), in place of what was there.</summary>
    public void Set(long timeMs, long lengthMs, long level, double volume)
    {
        var (start, end) = this.sessions.Of(timeMs);
        if (!this.bySession.TryGetValue(start, out var session))
            this.bySession[start] = session = new Session { StartMs = start, EndMs = end };
        if (!session.Periods.TryGetValue((timeMs, lengthMs), out var levels))
            session.Periods[(timeMs, lengthMs)] = levels = new Dictionary<long, double>();
        levels[level] = volume;
        session.Points = null;
    }

    /// <summary>In time order; a point each time the point of control or the value area moves.</summary>
    public VpocPoint[] Points()
    {
        var all = new List<VpocPoint>();
        foreach (var session in this.bySession.Values)
        {
            if (session.StartMs >= this.CoveredFromMs)
                all.AddRange(session.Points ??= this.Build(session));
        }

        return [.. all];
    }

    /// <summary>
    /// The point as of a time: the last one by then, if it's of the session the time is the end of. A period still
    /// running counts only when it's asked for, as it's of the chart's latest bar.
    /// </summary>
    public static VpocPoint? At(VpocPoint[] points, long timeMs, bool running)
    {
        int low = 0, high = points.Length;
        if (running)
        {
            low = points.Length;
        }
        else
        {
            while (low < high)
            {
                int middle = (low + high) / 2;
                if (points[middle].EndMs <= timeMs)
                    low = middle + 1;
                else
                    high = middle;
            }
        }

        if (low == 0)
            return null;

        var point = points[low - 1];
        return timeMs > point.SessionStartMs && timeMs <= point.SessionEndMs ? point : null;
    }

    private VpocPoint[] Build(Session session)
    {
        static long Row(long level) => (level + 1) >> 1;

        long first = long.MaxValue, last = long.MinValue;
        foreach (var levels in session.Periods.Values)
        {
            foreach (var (level, volume) in levels)
            {
                if (volume > 0)
                {
                    first = Math.Min(first, Row(level));
                    last = Math.Max(last, Row(level));
                }
            }
        }

        if (first > last || last - first >= MostRows)
            return [];

        var rows = new double[last - first + 1];
        double total = 0;
        int poc = -1, lowest = rows.Length, highest = -1;
        var points = new List<VpocPoint>();
        foreach (var ((timeMs, lengthMs), levels) in session.Periods)
        {
            foreach (var (level, volume) in levels)
            {
                if (!(volume > 0))
                    continue;

                int row = (int)(Row(level) - first);
                rows[row] += volume;
                total += volume;
                lowest = Math.Min(lowest, row);
                highest = Math.Max(highest, row);
            }

            if (total <= 0)
                continue;

            // Only a price that traded this period can pass the point of control; an equal one leaves it be.
            int best = poc;
            foreach (var (level, volume) in levels)
            {
                int row = (int)(Row(level) - first);
                if (volume > 0 && (best < 0 || rows[row] > rows[best] || (best != poc && rows[row] == rows[best] && row < best)))
                    best = row;
            }

            poc = best;
            var (up, down) = this.ValueArea(rows, poc, lowest, highest, total);
            var point = new VpocPoint(timeMs + lengthMs, session.StartMs, session.EndMs, this.Price(first + poc), this.Price(first + up), this.Price(first + down));
            if (points.Count == 0 || points[^1] with { EndMs = point.EndMs } != point)
                points.Add(point);
        }

        return [.. points];
    }

    private (int Up, int Down) ValueArea(double[] rows, int poc, int lowest, int highest, double total)
    {
        int up = poc, down = poc;
        double held = rows[poc], target = this.share * total;
        while (held < target && (up < highest || down > lowest))
        {
            double above = 0, below = 0;
            int toUp = Math.Min(up + 2, highest), toDown = Math.Max(down - 2, lowest);
            for (int i = up + 1; i <= toUp; i++)
                above += rows[i];
            for (int i = down - 1; i >= toDown; i--)
                below += rows[i];

            bool canUp = up < highest, canDown = down > lowest;
            bool takeUp = canUp && (!canDown || above >= below);
            bool takeDown = canDown && (!canUp || below >= above);
            if (takeUp)
            {
                held += above;
                up = toUp;
            }

            if (takeDown)
            {
                held += below;
                down = toDown;
            }
        }

        return (up, down);
    }

    private double Price(long row) => Math.Round(row * this.step, 8);
}

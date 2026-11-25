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
/// Those rows are only tastytrade's part of the market, and not the same part from minute to minute, so each minute's
/// are brought up to what the whole market traded in it (SetMarket; your request, 2026-11-25). This depends on nothing
/// in Quantower, so the harness can test it.
/// </summary>
public sealed class DynamicVpoc
{
    // A print so far off that a row per step would be this many is a bad one, and its session isn't worked out.
    private const long MostRows = 200_000;

    // What a share in the rows can stand for. Where they have less than a quarter of a minute's shares, they're too few
    // to say where the rest traded, which go evenly over the prices the minute traded at.
    private const double MostTimes = 4;

    // How many minutes back says what a minute's rows stand for while the market's count of it isn't in.
    private const int RecentMinutes = 10;

    private const long MinuteMs = 60_000;

    private readonly double step;
    private readonly double share;
    private readonly EtSessions sessions;
    private readonly SortedDictionary<long, Session> bySession = new();
    private RangeBar[] market = [];
    private long marketToMs = long.MinValue;

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
        public double Seen;
        public double Counted;
    }

    /// <summary>The shares so far at each price, from the lowest up, and where the point of control is.</summary>
    private sealed class Profile(long first, long last)
    {
        public readonly double[] Rows = new double[last - first + 1];

        /// <summary>The rows traded at since the point of control was last looked at.</summary>
        public readonly List<int> Traded = new();
        public double Total;
        public int Poc = -1;
        public int Lowest = int.MaxValue;
        public int Highest = -1;

        public void Add(long at, double volume)
        {
            int row = (int)(at - first);
            this.Rows[row] += volume;
            this.Total += volume;
            this.Lowest = Math.Min(this.Lowest, row);
            this.Highest = Math.Max(this.Highest, row);
            this.Traded.Add(row);
        }
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

    /// <summary>
    /// What the whole market traded in each minute, in time order and in place of what was there, all in up to a time.
    /// The minutes after it, and any the market's count has nothing for, aren't known.
    /// </summary>
    public void SetMarket(IEnumerable<RangeBar> minutes, long wholeToMs)
    {
        this.market = minutes.Where(m => m.Volume > 0 && m.Low > 0 && m.High >= m.Low && m.TimeMs + MinuteMs <= wholeToMs).ToArray();
        this.marketToMs = wholeToMs;
        foreach (var session in this.bySession.Values)
            session.Points = null;
    }

    /// <summary>The latest session's shares in the rows, and what they've been brought up to.</summary>
    public (double Seen, double Counted) Shares()
    {
        this.Points();
        var latest = this.bySession.Count > 0 ? this.bySession.Values.Last() : null;
        return latest == null ? (0, 0) : (latest.Seen, latest.Counted);
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
        session.Seen = session.Counted = 0;
        int from = this.MarketFrom(session.StartMs), to = this.MarketFrom(session.EndMs);
        long first = long.MaxValue, last = long.MinValue;
        void Reach(long row)
        {
            first = Math.Min(first, row);
            last = Math.Max(last, row);
        }

        foreach (var levels in session.Periods.Values)
        {
            foreach (var (level, volume) in levels)
            {
                if (volume > 0)
                    Reach(Row(level));
            }
        }

        for (int i = from; i < to; i++)
        {
            Reach(this.RowOf(this.market[i].Low));
            Reach(this.RowOf(this.market[i].High));
        }

        if (first > last || last - first >= MostRows)
            return [];

        var profile = new Profile(first, last);
        var points = new List<VpocPoint>();
        var recent = new Queue<(double Whole, double Seen)>();
        double recentWhole = 0, recentSeen = 0;

        void Spread(RangeBar minute, double volume)
        {
            long low = this.RowOf(minute.Low), high = this.RowOf(minute.High);
            for (long row = low; row <= high; row++)
                profile.Add(row, volume / (high - low + 1));
        }

        void Mark(long endMs)
        {
            if (profile.Total <= 0)
                return;

            // Only a price that traded this period can pass the point of control; an equal one leaves it be.
            var rows = profile.Rows;
            int best = profile.Poc;
            foreach (int row in profile.Traded)
            {
                if (best < 0 || rows[row] > rows[best] || (best != profile.Poc && rows[row] == rows[best] && row < best))
                    best = row;
            }

            profile.Traded.Clear();
            profile.Poc = best;
            var (up, down) = this.ValueArea(rows, best, profile.Lowest, profile.Highest, profile.Total);
            var point = new VpocPoint(endMs, session.StartMs, session.EndMs, this.Price(first + best), this.Price(first + up), this.Price(first + down));
            if (points.Count == 0 || points[^1] with { EndMs = point.EndMs } != point)
                points.Add(point);
        }

        // A minute the market traded in and no row is of.
        void Alone(RangeBar minute)
        {
            Spread(minute, minute.Volume);
            Mark(minute.TimeMs + MinuteMs);
        }

        int next = from;
        foreach (var ((timeMs, lengthMs), levels) in session.Periods)
        {
            for (; next < to && this.market[next].TimeMs < timeMs; next++)
                Alone(this.market[next]);

            int begin = next;
            double whole = 0, seen = 0;
            for (; next < to && this.market[next].TimeMs < timeMs + lengthMs; next++)
                whole += this.market[next].Volume;
            foreach (double volume in levels.Values)
                seen += Math.Max(0, volume);
            session.Seen += seen;

            bool over = timeMs + lengthMs <= this.marketToMs;
            bool known = over && whole > 0;
            double times = known ? (seen > 0 ? whole / seen : 0) : over || recentSeen <= 0 ? 1 : recentWhole / recentSeen;
            times = Math.Min(times, MostTimes);
            foreach (var (level, volume) in levels)
            {
                if (volume > 0)
                    profile.Add(Row(level), volume * times);
            }

            if (known)
            {
                double rest = whole - (seen * times);
                for (int i = begin; i < next && rest > whole * 1e-9; i++)
                    Spread(this.market[i], rest * this.market[i].Volume / whole);

                recent.Enqueue((whole, seen));
                recentWhole += whole;
                recentSeen += seen;
                if (recent.Count > RecentMinutes)
                {
                    var old = recent.Dequeue();
                    recentWhole -= old.Whole;
                    recentSeen -= old.Seen;
                }
            }

            Mark(timeMs + lengthMs);
        }

        for (; next < to; next++)
            Alone(this.market[next]);

        session.Counted = profile.Total;
        return [.. points];
    }

    private static long Row(long level) => (level + 1) >> 1;

    private long RowOf(double price) => Row((long)Math.Floor((price / this.step * 2) + 1e-6));

    /// <summary>The first of the market's minutes at or after a time.</summary>
    private int MarketFrom(long timeMs)
    {
        int low = 0, high = this.market.Length;
        while (low < high)
        {
            int middle = (low + high) / 2;
            if (this.market[middle].TimeMs < timeMs)
                low = middle + 1;
            else
                high = middle;
        }

        return low;
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

using TradingPlatform.BusinessLayer;

namespace ScalpFeedIndicators;

/// <summary>
/// What was read for a range: its bars, which load and change they're as of, and whether they're final, as there's a
/// later bar.
/// </summary>
internal sealed record RangeRead(RangeBar[] Bars, int Load, int Version, bool Final);

/// <summary>
/// A stock's bars of one length for its fixed range profiles, loaded through Quantower off its thread from the day a
/// range starts on, live from then on, and shared by every chart that needs them. They're the chart's own kind: IBKR's
/// whole-market bars over tastytrade's.
/// </summary>
internal sealed class RangeBars
{
    private const long RetryMs = 10_000;
    private static readonly Dictionary<string, RangeBars> Open = new();

    private readonly string key;
    private readonly Symbol symbol;
    private readonly Period period;
    private readonly object gate = new();
    private HistoricalData data;
    private long fromMs = long.MaxValue;
    private long wantedMs = long.MaxValue;
    private long? failedAt;
    private int load;
    private int version;
    private int users;
    private bool loading;
    private bool closed;

    private RangeBars(string key, Symbol symbol, Period period)
    {
        this.key = key;
        this.symbol = symbol;
        this.period = period;
    }

    /// <summary>When bars have loaded. Must not block.</summary>
    public event Action Loaded;

    /// <summary>For a chart that's to close it when it's done.</summary>
    public static RangeBars For(Symbol symbol, Period period)
    {
        string key = $"{symbol.ConnectionId}|{symbol.Id}|{period}";
        lock (Open)
        {
            if (!Open.TryGetValue(key, out var bars))
                Open[key] = bars = new RangeBars(key, symbol, period);
            bars.users++;
            return bars;
        }
    }

    public void Close()
    {
        lock (Open)
        {
            if (--this.users > 0)
                return;

            Open.Remove(this.key);
        }

        HistoricalData ending;
        lock (this.gate)
        {
            this.closed = true;
            ending = this.data;
            this.data = null;
        }

        this.Drop(ending);
    }

    /// <summary>
    /// The bars that start in a range, the same read again when nothing in it can have changed. Null while they're
    /// loading, which this starts if it's needed.
    /// </summary>
    public RangeRead Between(long startMs, long endMs, RangeRead had)
    {
        HistoricalData loaded;
        int loadNow, versionNow;
        lock (this.gate)
        {
            if (this.closed)
                return null;

            if (startMs < this.fromMs)
            {
                this.Load(startMs);
                return null;
            }

            loaded = this.data;
            (loadNow, versionNow) = (this.load, this.version);
            if (had != null && had.Load == loadNow && (had.Final || had.Version == versionNow))
                return had;
        }

        return Read(loaded, startMs, endMs, loadNow, versionNow);
    }

    /// <summary>The bars of any history that start in a range.</summary>
    public static RangeRead Read(HistoricalData history, long startMs, long endMs, int load = 0, int version = 0)
    {
        int count = history.Count, low = 0, high = count;
        while (low < high)
        {
            int middle = (low + high) / 2;
            if (Ms(history[middle, SeekOriginHistory.Begin].TimeLeft) < startMs)
                low = middle + 1;
            else
                high = middle;
        }

        var bars = new List<RangeBar>();
        for (int i = low; i < count; i++)
        {
            if (history[i, SeekOriginHistory.Begin] is not HistoryItemBar bar)
                continue;

            long at = Ms(bar.TimeLeft);
            if (at >= endMs)
                return new RangeRead(bars.ToArray(), load, version, true);
            bars.Add(new RangeBar(at, bar.Open, bar.High, bar.Low, bar.Close, double.IsNaN(bar.Volume) ? 0 : bar.Volume));
        }

        return new RangeRead(bars.ToArray(), load, version, false);
    }

    public static long Ms(DateTime utc) => new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc)).ToUnixTimeMilliseconds();

    /// <summary>In the gate. From the start of the range's day, so moving its edge about that day loads nothing more.</summary>
    private void Load(long startMs)
    {
        long from = this.period.BasePeriod == BasePeriod.Second ? startMs - 60_000 : Math.Min(startMs, EtSessions.DayStart(startMs, 0));
        this.wantedMs = Math.Min(this.wantedMs, from);
        if (this.loading || (this.failedAt is long at && Environment.TickCount64 - at < RetryMs))
            return;

        this.loading = true;
        from = this.wantedMs;
        _ = Task.Run(() =>
        {
            HistoricalData loaded = null;
            try
            {
                loaded = this.symbol.GetHistory(this.period, this.symbol.HistoryType, DateTimeOffset.FromUnixTimeMilliseconds(from).UtcDateTime);
                loaded.NewHistoryItem += this.OnItem;
                loaded.HistoryItemUpdated += this.OnItem;
            }
            catch (Exception)
            {
                // Not to be had this time: a paint after the retry time asks again.
            }

            HistoricalData dropped;
            lock (this.gate)
            {
                this.loading = false;
                this.failedAt = loaded == null ? Environment.TickCount64 : null;
                if (loaded != null && !this.closed)
                {
                    dropped = this.data;
                    this.data = loaded;
                    this.fromMs = from;
                    this.load++;
                }
                else
                {
                    dropped = loaded;
                }
            }

            this.Drop(dropped);
            if (loaded != null)
                this.Loaded?.Invoke();
        });
    }

    private void Drop(HistoricalData dropped)
    {
        if (dropped == null)
            return;

        dropped.NewHistoryItem -= this.OnItem;
        dropped.HistoryItemUpdated -= this.OnItem;
        dropped.Dispose();
    }

    private void OnItem(object sender, HistoryEventArgs e) => Interlocked.Increment(ref this.version);
}

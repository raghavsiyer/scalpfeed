using ScalpFeedVendor.Feed;

namespace ScalpFeedIndicators;

/// <summary>What a dynamic VPOC is worked out with.</summary>
internal sealed record VpocSettings(double Step, double ValueAreaShare, TimeSpan[] StartsEt, int Days)
{
    public string Key => FormattableString.Invariant($"{this.Step}|{this.ValueAreaShare}|{string.Join(',', this.StartsEt)}|{this.Days}");
}

/// <summary>
/// One stock's dynamic VPOC, kept up with the rows the connection sends (VolumeAtPriceShare), and shared by every chart
/// that shows it the same way, so six charts of a stock cost what one does. The whole market's minutes, which the rows
/// are brought up to, are read when it opens and once a minute while the stock trades, so every minute's count is the
/// same kind: a minute a chart builds from live trades has the odd lots too, which the bars it loads don't.
/// </summary>
internal sealed class VpocSource
{
    private const int EveryMs = 500;
    private const long MinuteMs = 60_000;

    // The line waits this long for the market's minutes, rather than be drawn without them and then move.
    private const int MarketWaitMs = 8_000;
    private const int MarketRetryMs = 15_000;

    // How long after a minute it's read, and how long after one its bar is taken to be all in.
    private const long MarketAfterMs = 10_000;
    private const long SettledMs = 3_000;

    private static readonly Dictionary<string, VpocSource> Open = new();

    // The stock charted last. Only its minutes are read again, as reading a stock's bars is how the connection tells
    // which one you charted last.
    private static string latest;

    private readonly string key;
    private readonly string symbolId;
    private readonly VpocSettings settings;
    private readonly Func<long, long, RangeBar[]> market;
    private readonly long fromMs;
    private readonly long openedAt = Environment.TickCount64;
    private readonly DynamicVpoc vpoc;
    private readonly object gate = new();
    private readonly Timer timer;
    private IDisposable subscription;
    private VpocPoint[] points = [];
    private int users;
    private int busy;
    private bool dirty;
    private volatile bool arrived;
    private volatile bool reading;
    private volatile bool tradedSince;
    private long nextReadMs;
    private bool closed;

    private VpocSource(string key, string symbolId, VpocSettings settings, Func<long, long, RangeBar[]> market)
    {
        this.key = key;
        this.symbolId = symbolId;
        this.settings = settings;
        this.market = market;
        this.fromMs = EtSessions.DayStart(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), Math.Max(0, settings.Days - 1));
        this.vpoc = new DynamicVpoc(settings.Step, settings.ValueAreaShare, new EtSessions(settings.StartsEt)) { CoveredFromMs = long.MaxValue };
        this.timer = new Timer(_ => this.OnTimer(), null, 0, EveryMs);
    }

    /// <summary>When what it draws changes. Must not block.</summary>
    public event Action Changed;

    /// <summary>Not changed once set: a new array takes its place.</summary>
    public VpocPoint[] Points => Volatile.Read(ref this.points);

    /// <summary>The connection that's loaded can send the rows.</summary>
    public static bool Offered => VolumeAtPriceShare.Offered != null;

    /// <summary>The rows have come, if there were any.</summary>
    public bool Arrived => this.arrived;

    public double Step => this.settings.Step;

    /// <summary>How many times the market's minutes have been read.</summary>
    public int MarketReads { get; private set; }

    /// <summary>They're all in up to here.</summary>
    public long MarketToMs { get; private set; }

    /// <summary>The one for this stock and these settings, for a chart that's to close it when it's done.</summary>
    /// <param name="symbolId">The connection's name for it (VolumeAtPriceShare.SymbolOf).</param>
    /// <param name="market">
    /// The whole market's minute bars from one time to another, read off Quantower's thread. Without it only the rows'
    /// own shares count.
    /// </param>
    public static VpocSource For(string symbolId, VpocSettings settings, Func<long, long, RangeBar[]> market = null)
    {
        string key = symbolId + "|" + settings.Key;
        lock (Open)
        {
            latest = symbolId;
            if (!Open.TryGetValue(key, out var source))
                Open[key] = source = new VpocSource(key, symbolId, settings, market);
            source.users++;
            return source;
        }
    }

    /// <summary>The latest session's shares in the rows, and what they've been brought up to.</summary>
    public (double Seen, double Counted) Shares()
    {
        lock (this.gate)
            return this.vpoc.Shares();
    }

    public void Close()
    {
        lock (Open)
        {
            if (--this.users > 0)
                return;

            Open.Remove(this.key);
        }

        this.timer.Dispose();
        IDisposable ending;
        lock (this.gate)
        {
            this.closed = true;
            ending = this.subscription;
            this.subscription = null;
        }

        // Outside the gate: the connection holds its own lock while it calls OnRows, which waits for this one.
        ending?.Dispose();
    }

    private void OnTimer()
    {
        if (Interlocked.Exchange(ref this.busy, 1) == 1)
            return;

        try
        {
            this.Subscribe();
            this.ReadMarket();

            VpocPoint[] built;
            lock (this.gate)
            {
                bool waiting = this.market != null && this.MarketReads == 0 && Environment.TickCount64 - this.openedAt < MarketWaitMs;
                if (!this.dirty || this.closed || waiting)
                    return;

                this.dirty = false;
                built = this.vpoc.Points();
            }

            bool changed = !built.AsSpan().SequenceEqual(this.Points);
            Volatile.Write(ref this.points, built);
            if (changed)
                this.Changed?.Invoke();
        }
        catch (Exception)
        {
            // The next tick tries again; it mustn't take Quantower down from a timer.
        }
        finally
        {
            Volatile.Write(ref this.busy, 0);
        }
    }

    /// <summary>Once the connection offers the rows, which it doesn't until it has connected.</summary>
    private void Subscribe()
    {
        if (this.subscription != null || this.closed || VolumeAtPriceShare.Offered is not { } offered)
            return;

        var taken = offered(this.symbolId, this.settings.Step, this.fromMs, this.OnRows);
        lock (this.gate)
        {
            if (!this.closed)
            {
                this.subscription = taken;
                return;
            }
        }

        taken?.Dispose();
    }

    /// <summary>Off this thread, when it's due: at first, then once a minute's over if it traded.</summary>
    private void ReadMarket()
    {
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        if (this.market == null || this.reading || this.closed || this.subscription == null || now < Volatile.Read(ref this.nextReadMs))
            return;
        if (this.MarketReads > 0 && (!this.tradedSince || Volatile.Read(ref latest) != this.symbolId))
            return;

        this.reading = true;
        this.tradedSince = false;
        _ = Task.Run(() =>
        {
            RangeBar[] read = null;
            try
            {
                read = this.market(this.fromMs, now);
            }
            catch (Exception)
            {
                // Not to be had this time: it's read again after the retry time.
            }

            if (read is { Length: > 0 })
            {
                lock (this.gate)
                {
                    this.MarketToMs = (now - SettledMs) / MinuteMs * MinuteMs;
                    this.vpoc.SetMarket(read, this.MarketToMs);
                    this.MarketReads++;
                    this.dirty = true;
                }
            }
            else
            {
                this.tradedSince = true;
            }

            long due = read is { Length: > 0 } ? (((now / MinuteMs) + 1) * MinuteMs) + MarketAfterMs : now + MarketRetryMs;
            Volatile.Write(ref this.nextReadMs, due);
            this.reading = false;
        });
    }

    private void OnRows(bool whole, long coveredFromMs, (long TimeMs, long LengthMs, long Level, double Volume)[] rows)
    {
        lock (this.gate)
        {
            if (whole)
                this.vpoc.Clear();
            this.vpoc.CoveredFromMs = coveredFromMs;
            foreach (var (timeMs, lengthMs, level, volume) in rows)
                this.vpoc.Set(timeMs, lengthMs, level, volume);
            this.dirty = true;
        }

        this.arrived = true;
        this.tradedSince = true;
    }
}

using ScalpFeedVendor.Feed;

namespace ScalpFeedIndicators;

/// <summary>What a dynamic VPOC is worked out with.</summary>
internal sealed record VpocSettings(double Step, double ValueAreaShare, TimeSpan[] StartsEt, int Days)
{
    public string Key => FormattableString.Invariant($"{this.Step}|{this.ValueAreaShare}|{string.Join(',', this.StartsEt)}|{this.Days}");
}

/// <summary>
/// One stock's dynamic VPOC, kept up with the rows the connection sends (VolumeAtPriceShare), and shared by every chart
/// that shows it the same way, so six charts of a stock cost what one does.
/// </summary>
internal sealed class VpocSource
{
    private const int EveryMs = 500;
    private static readonly Dictionary<string, VpocSource> Open = new();

    private readonly string key;
    private readonly string symbolId;
    private readonly VpocSettings settings;
    private readonly DynamicVpoc vpoc;
    private readonly object gate = new();
    private readonly Timer timer;
    private IDisposable subscription;
    private VpocPoint[] points = [];
    private int users;
    private int busy;
    private bool dirty;
    private volatile bool arrived;
    private bool closed;

    private VpocSource(string key, string symbolId, VpocSettings settings)
    {
        this.key = key;
        this.symbolId = symbolId;
        this.settings = settings;
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

    /// <summary>The one for this stock and these settings, for a chart that's to close it when it's done.</summary>
    public static VpocSource For(string symbolId, VpocSettings settings)
    {
        string key = symbolId + "|" + settings.Key;
        lock (Open)
        {
            if (!Open.TryGetValue(key, out var source))
                Open[key] = source = new VpocSource(key, symbolId, settings);
            source.users++;
            return source;
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

            VpocPoint[] built;
            lock (this.gate)
            {
                if (!this.dirty || this.closed)
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

        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var taken = offered(this.symbolId, this.settings.Step, EtSessions.DayStart(now, Math.Max(0, this.settings.Days - 1)), this.OnRows);
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
    }
}

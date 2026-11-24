using System.Globalization;

namespace ScalpFeedVendor.Feed;

/// <summary>
/// The shares traded at each price, minute by minute, for the Dynamic VPOC (your request, 2026-11-24). tastytrade has
/// no usable tick history, but its candles can be split by price ("{=m,pl=0.005}": a candle per minute and half cent),
/// live and back several days, and they add up to the plain candles' volume to the share. A snapshot holds only its
/// newest 8,000 rows, so where a stock has more, the time before them comes from 5-minute rows, and before those
/// 30-minute ones. On a socket of its own, so a snapshot (up to 5 seconds) never holds up a chart or the live feed.
/// </summary>
internal sealed class VolumeAtPriceFeed : IFeedSink
{
    private static readonly IReadOnlyDictionary<string, string[]> Fields = new Dictionary<string, string[]>
    {
        ["Candle"] = ["eventType", "eventSymbol", "eventFlags", "index", "time", "low", "volume"]
    };

    /// <summary>The periods rows come in, finest first; the first stays live.</summary>
    internal static readonly (string Code, long LengthMs)[] Tiers = [("m", 60_000), ("5m", 300_000), ("30m", 1_800_000)];

    private const double AggregationSeconds = 1;
    private static readonly TimeSpan LoadingLinger = TimeSpan.FromSeconds(2);

    private readonly object gate = new();
    private readonly Dictionary<(string Streamer, long Level), Watch> watches = new();
    private readonly int cap;
    private readonly TimeSpan linger;
    private readonly TimeSpan quietLimit;
    private object owner;
    private Func<bool, CancellationToken, Task<QuoteToken>> tokenSource;
    private Func<string, string> streamerOf = id => id;
    private DxLinkLink link;
    private Timer timer;

    /// <summary>The one the plugin uses: charts keep their rows through a disconnect and reconnect.</summary>
    public static VolumeAtPriceFeed Shared { get; } = new();

    /// <param name="cap">A snapshot with this many rows was cut short (the server's limit is 8,000).</param>
    /// <param name="linger">How long a stock nobody watches is kept, as charts drop and retake one when they reload.</param>
    /// <param name="quietLimit">How long a snapshot can go without a row before it's asked for again.</param>
    public VolumeAtPriceFeed(int cap = 7_900, TimeSpan? linger = null, TimeSpan? quietLimit = null)
    {
        this.cap = cap;
        this.linger = linger ?? TimeSpan.FromSeconds(30);
        this.quietLimit = quietLimit ?? TimeSpan.FromSeconds(20);
    }

    private readonly record struct RowKey(long LengthMs, long TimeMs, long Level);

    private sealed class Watch
    {
        public string Streamer;
        public double LevelSize;
        public long FromMs;
        public readonly List<Action<bool, long, (long, long, long, double)[]>> Listeners = new();
        public DateTime UnwatchedUtc;

        /// <summary>The tier whose snapshot is awaited, or past the last when all that's needed has come.</summary>
        public int Loading;

        /// <summary>A snapshot's rows count from the one that begins it; earlier ones are left over from before.</summary>
        public bool Begun;
        public DateTime LastRowUtc;

        /// <summary>The rows are complete from here; none are handed out while it's long.MaxValue.</summary>
        public long CoveredFromMs;

        /// <summary>Rows of the live tier count from here; what's before it is the coarser tiers'.</summary>
        public long LiveFromMs;

        public readonly Dictionary<long, (RowKey Key, double Volume)>[] ByIndex = [new(), new(), new()];
        public readonly Dictionary<RowKey, double> Totals = new();
        public readonly HashSet<RowKey> Changed = new();

        /// <summary>The tiers the server has been asked for on this connection.</summary>
        public readonly HashSet<int> Asked = new();

        /// <summary>What can be handed out: not a coarser snapshot's rows until it's all in.</summary>
        public IEnumerable<RowKey> Ready => this.Loading >= Tiers.Length ? this.Totals.Keys : this.Totals.Keys.Where(k => k.LengthMs != Tiers[this.Loading].LengthMs);

        public void Clear()
        {
            foreach (var rows in this.ByIndex)
                rows.Clear();
            this.Totals.Clear();
            this.Changed.Clear();
            this.Loading = 0;
            this.Begun = false;
            this.CoveredFromMs = long.MaxValue;
            this.LiveFromMs = long.MinValue;
        }
    }

    /// <summary>What a symbol on the wire is for, worked out once for each.</summary>
    private sealed record Route(string Streamer, long Level, int Tier);

    /// <param name="owner">Whose connection this is; only its own stop stops it.</param>
    public void Start(object owner, Func<bool, CancellationToken, Task<QuoteToken>> tokenSource, Func<string, string> streamerOf)
    {
        DxLinkLink replaced;
        lock (this.gate)
        {
            replaced = this.link;
            this.link = null;
            this.owner = owner;
            this.tokenSource = tokenSource;
            this.streamerOf = streamerOf;
            this.timer ??= new Timer(_ => this.OnTimer(), null, 500, 500);
            if (this.watches.Count > 0)
                this.EnsureLink();
        }

        if (replaced != null)
        {
            replaced.Ready -= this.OnReady;
            _ = Quietly(replaced.StopAsync());
        }
    }

    public async Task StopAsync(object owner)
    {
        DxLinkLink stopping;
        lock (this.gate)
        {
            if (!ReferenceEquals(owner, this.owner))
                return;

            stopping = this.link;
            this.link = null;
            this.owner = null;
            this.tokenSource = null;
            this.timer?.Dispose();
            this.timer = null;
        }

        if (stopping != null)
        {
            stopping.Ready -= this.OnReady;
            await stopping.StopAsync().ConfigureAwait(false);
        }
    }

    /// <summary>VolumeAtPriceShare.Offered says what this takes and gives.</summary>
    public IDisposable Subscribe(string symbolId, double step, long fromMs, Action<bool, long, (long TimeMs, long LengthMs, long Level, double Volume)[]> onRows)
    {
        lock (this.gate)
        {
            string streamer = this.streamerOf(symbolId);
            double size = step / 2;
            var key = (streamer, LevelKey(size));
            if (!this.watches.TryGetValue(key, out var watch))
            {
                watch = new Watch { Streamer = streamer, LevelSize = size, FromMs = fromMs };
                watch.Clear();
                this.watches[key] = watch;
                watch.Listeners.Add(onRows);
                this.EnsureLink();
                this.Load(watch);
            }
            else
            {
                watch.Listeners.Add(onRows);
                if (fromMs < watch.FromMs)
                {
                    watch.FromMs = fromMs;
                    this.Load(watch);
                }
                else if (watch.CoveredFromMs != long.MaxValue)
                {
                    Hand(onRows, true, watch, watch.Ready);
                }
            }

            return new Stopper(() => this.Unsubscribe(watch, onRows));
        }
    }

    public void OnCandle(FeedRow row)
    {
        lock (this.gate)
        {
            var route = row.Symbol.Tag as Route;
            if (route == null)
            {
                route = RouteOf(row.Symbol.Name);
                if (route == null)
                    return;
                row.Symbol.Tag = route;
            }

            if (!this.watches.TryGetValue((route.Streamer, route.Level), out var watch))
                return;

            bool loading = route.Tier == watch.Loading;
            if (!loading && !(route.Tier == 0 && watch.Loading > 0))
                return;

            watch.LastRowUtc = DateTime.UtcNow;
            if (loading && !watch.Begun)
            {
                if ((row.Flags & EventFlags.SnapshotBegin) == 0)
                    return;
                watch.Begun = true;
            }

            bool counts = route.Tier > 0 ? loading : row.Time >= watch.LiveFromMs;
            if (counts && row.Time >= watch.FromMs)
                Apply(watch, route.Tier, row);

            if (loading && (row.Flags & (EventFlags.SnapshotEnd | EventFlags.SnapshotSnip)) != 0)
                this.Loaded(watch);
        }
    }

    public void OnQuote(FeedRow row)
    {
    }

    public void OnPrint(FeedRow row)
    {
    }

    public void OnTrade(FeedRow row)
    {
    }

    public void OnSummary(FeedRow row)
    {
    }

    /// <summary>The candle a tier's rows come from, as tastytrade spells it, so they can be told by their symbol.</summary>
    internal static string CandleSymbol(string streamer, double levelSize, int tier) =>
        $"{streamer}{{={Tiers[tier].Code},pl={levelSize.ToString("0.##########", CultureInfo.InvariantCulture)}}}";

    /// <summary>Its start when every period from there on is whole: the next one on the coarser tier's boundaries.</summary>
    internal static long WholeFrom(long earliestMs, long lengthMs, long coarserLengthMs)
    {
        long after = earliestMs + lengthMs;
        return (after + coarserLengthMs - 1) / coarserLengthMs * coarserLengthMs;
    }

    private static long LevelKey(double size) => (long)Math.Round(size * 1e9);

    private static Route RouteOf(string name)
    {
        int brace = name.IndexOf('{');
        if (brace <= 0 || !name.EndsWith('}'))
            return null;

        int tier = -1;
        double level = double.NaN;
        foreach (var part in name[(brace + 1)..^1].Split(','))
        {
            if (part.StartsWith('='))
                tier = Array.FindIndex(Tiers, t => t.Code == part[1..]);
            else if (part.StartsWith("pl=", StringComparison.Ordinal) && double.TryParse(part[3..], NumberStyles.Float, CultureInfo.InvariantCulture, out double size))
                level = size;
        }

        return tier < 0 || double.IsNaN(level) ? null : new Route(name[..brace], LevelKey(level), tier);
    }

    private static void Apply(Watch watch, int tier, FeedRow row)
    {
        var rows = watch.ByIndex[tier];
        if (rows.Remove(row.Index, out var old))
        {
            watch.Totals[old.Key] -= old.Volume;
            watch.Changed.Add(old.Key);
        }

        // A row is told by its index, not its price: one price may be in either of two rows, as prices aren't exact.
        bool removed = (row.Flags & EventFlags.Remove) != 0;
        if (removed || !(row.Volume > 0) || !(row.Low > 0))
            return;

        var key = new RowKey(Tiers[tier].LengthMs, row.Time, (long)Math.Floor((row.Low / watch.LevelSize) + 1e-6));
        rows[row.Index] = (key, row.Volume);
        watch.Totals[key] = watch.Totals.GetValueOrDefault(key) + row.Volume;
        watch.Changed.Add(key);
    }

    private static void Hand(Action<bool, long, (long, long, long, double)[]> listener, bool whole, Watch watch, IEnumerable<RowKey> keys)
    {
        try
        {
            listener(whole, watch.CoveredFromMs, [.. keys.Select(k => (k.TimeMs, k.LengthMs, k.Level, Math.Max(0, watch.Totals.GetValueOrDefault(k))))]);
        }
        catch (Exception ex)
        {
            Log.Info($"[volume at price] a chart failed on {watch.Streamer}'s rows: {ex.GetType().Name} {ex.Message}");
        }
    }

    /// <summary>The awaited snapshot is in: its rows before where it's whole go, and the next coarser one fills those.</summary>
    private void Loaded(Watch watch)
    {
        int tier = watch.Loading;
        var rows = watch.ByIndex[tier];
        bool cut = rows.Count >= this.cap;
        bool coarsest = tier == Tiers.Length - 1;
        long wholeFrom = watch.FromMs;
        if (cut)
        {
            long earliest = rows.Values.Min(r => r.Key.TimeMs);
            wholeFrom = Math.Max(watch.FromMs, WholeFrom(earliest, Tiers[tier].LengthMs, Tiers[coarsest ? tier : tier + 1].LengthMs));
        }

        // What this tier is for: from where it's whole to where the finer one took over.
        long until = watch.CoveredFromMs;
        long length = Tiers[tier].LengthMs;
        foreach (var key in watch.Totals.Keys.Where(k => k.LengthMs == length && (k.TimeMs < wholeFrom || k.TimeMs >= until)).ToList())
            watch.Totals.Remove(key);
        foreach (var index in rows.Where(r => !watch.Totals.ContainsKey(r.Value.Key)).Select(r => r.Key).ToList())
            rows.Remove(index);

        watch.CoveredFromMs = Math.Min(until, wholeFrom);
        if (tier == 0)
            watch.LiveFromMs = wholeFrom;

        watch.Changed.Clear();
        watch.Begun = false;
        watch.Loading = cut && !coarsest ? tier + 1 : Tiers.Length;
        Log.Info($"[volume at price] {watch.Streamer} {Tiers[tier].Code} by {watch.LevelSize * 2:0.######}: {rows.Count} rows, {(cut ? "cut short, " : "")}whole from {DateTimeOffset.FromUnixTimeMilliseconds(watch.CoveredFromMs):MM-dd HH:mm} UTC");
        foreach (var listener in watch.Listeners.ToList())
            Hand(listener, true, watch, watch.Ready);

        // The live tier stays subscribed, for what trades from here on; a coarser one has done its job.
        if (tier > 0)
            this.Drop(watch, tier);
        if (watch.Loading < Tiers.Length)
            this.Ask(watch, watch.Loading);
    }

    /// <summary>From nothing: the live tier's snapshot, then whatever coarser ones it takes.</summary>
    private void Load(Watch watch)
    {
        this.DropAll(watch);
        watch.Clear();
        this.Ask(watch, 0);
    }

    private void Ask(Watch watch, int tier)
    {
        watch.LastRowUtc = DateTime.UtcNow;
        if (this.link is not { State: LinkState.Ready } current)
            return;

        watch.Asked.Add(tier);
        _ = Quietly(current.SendSubscriptionAsync([new FeedKey("Candle", CandleSymbol(watch.Streamer, watch.LevelSize, tier))], null, reset: false, watch.FromMs));
    }

    private void Drop(Watch watch, int tier)
    {
        if (watch.Asked.Remove(tier) && this.link is { State: LinkState.Ready } current)
            _ = Quietly(current.SendSubscriptionAsync(null, [new FeedKey("Candle", CandleSymbol(watch.Streamer, watch.LevelSize, tier))], reset: false));
    }

    private void DropAll(Watch watch)
    {
        for (int tier = 0; tier < Tiers.Length; tier++)
            this.Drop(watch, tier);
    }

    private static async Task Quietly(Task send)
    {
        try
        {
            await send.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // The socket went as it was sent: the link reconnects, and everything's asked for again.
            Log.Info($"[volume at price] send: {ex.Message}");
        }
    }

    private void EnsureLink()
    {
        if (this.link != null || this.tokenSource == null)
            return;

        this.link = new DxLinkLink("volume at price", this.tokenSource, Fields, this, AggregationSeconds);
        this.link.Ready += this.OnReady;
        this.link.Start();
    }

    /// <summary>Connected, or connected again: the server knows no subscriptions, and rows may have been missed.</summary>
    private void OnReady()
    {
        lock (this.gate)
        {
            foreach (var watch in this.watches.Values)
            {
                watch.Clear();
                watch.Asked.Clear();
                this.Ask(watch, 0);
            }
        }
    }

    private void OnTimer()
    {
        lock (this.gate)
        {
            var now = DateTime.UtcNow;
            foreach (var (key, watch) in this.watches.ToList())
            {
                if (watch.Listeners.Count == 0)
                {
                    // One still loading is dropped sooner, so going quickly from stock to stock doesn't leave the one
                    // you stop at waiting behind the rest: the server sends one snapshot at a time.
                    var keep = watch.Loading < Tiers.Length && this.linger > LoadingLinger ? LoadingLinger : this.linger;
                    if (now - watch.UnwatchedUtc >= keep)
                    {
                        this.watches.Remove(key);
                        this.DropAll(watch);
                    }

                    continue;
                }

                if (watch.Loading < Tiers.Length && this.link?.State == LinkState.Ready && now - watch.LastRowUtc > this.quietLimit)
                {
                    Log.Info($"[volume at price] {watch.Streamer} {Tiers[watch.Loading].Code}: no rows for {this.quietLimit.TotalSeconds:0} s, asking again");
                    this.Load(watch);
                    continue;
                }

                if (watch.CoveredFromMs == long.MaxValue || watch.Changed.Count == 0)
                    continue;

                // A coarser snapshot still coming is handed out whole when it's in.
                var changed = watch.Changed.Where(k => k.LengthMs == Tiers[0].LengthMs).ToList();
                watch.Changed.Clear();
                if (changed.Count > 0)
                {
                    foreach (var listener in watch.Listeners.ToList())
                        Hand(listener, false, watch, changed);
                }
            }
        }
    }

    private void Unsubscribe(Watch watch, Action<bool, long, (long, long, long, double)[]> listener)
    {
        lock (this.gate)
        {
            watch.Listeners.Remove(listener);
            if (watch.Listeners.Count == 0)
                watch.UnwatchedUtc = DateTime.UtcNow;
        }
    }

    private sealed class Stopper(Action stop) : IDisposable
    {
        private Action stop = stop;

        public void Dispose() => Interlocked.Exchange(ref this.stop, null)?.Invoke();
    }
}

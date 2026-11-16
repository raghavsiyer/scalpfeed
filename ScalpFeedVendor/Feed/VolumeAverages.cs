using System.Collections.Concurrent;

namespace ScalpFeedVendor.Feed;

/// <summary>
/// Each quoted symbol's average daily volume over the 50 sessions before today, which RVOL divides by. It comes
/// from the daily bars, which carry whole-market volume and are split-adjusted: loaded once an Eastern day per
/// symbol, in the background, one request at a time, so a chart's history never waits behind more than one.
/// The averages outlive the connection object, because Quantower makes a new one at every reconnect.
/// </summary>
internal sealed class VolumeAverages : IDisposable
{
    /// <summary>The bars of this candle symbol between two times, as HistoryClient loads them.</summary>
    public delegate Task<IReadOnlyList<BarRow>> Load(string candleSymbol, DateTime fromUtc, DateTime toUtc, CancellationToken ct);

    private sealed record Entry(DateTime Day, double Volume);

    // 50 sessions are about 72 calendar days; the rest covers holidays.
    private const int DaysBack = 100;

    // A failed load can hold the history socket for seconds, so only the first retry comes quickly.
    private static readonly TimeSpan FirstRetry = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan LaterRetries = TimeSpan.FromMinutes(10);
    private static readonly ConcurrentDictionary<string, Entry> Kept = new(StringComparer.Ordinal);

    private readonly ConcurrentDictionary<string, (DateTime AtUtc, int Failures)> retry = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> queued = new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<string> queue = new();
    private readonly CancellationTokenSource stop = new();
    private readonly Load load;
    private readonly Action<string> arrived;
    private readonly Func<DateTime> utcNow;
    private int working;

    /// <param name="arrived">Called from the background with a symbol whose average just came in.</param>
    public VolumeAverages(Load load, Action<string> arrived, Func<DateTime> utcNow = null)
    {
        this.load = load;
        this.arrived = arrived;
        this.utcNow = utcNow ?? (() => DateTime.UtcNow);
    }

    /// <summary>
    /// The symbol's latest average, or NaN with none yet or under 5 earlier sessions. Yesterday's stands in
    /// until today's is loaded: the two share 49 of their 50 sessions.
    /// </summary>
    public double Get(string symbol) => Kept.TryGetValue(symbol, out var entry) ? entry.Volume : double.NaN;

    /// <summary>Asks for today's average unless it's here, already asked for, or a try just failed.</summary>
    public void Want(string symbol)
    {
        if (string.IsNullOrEmpty(symbol))
            return;

        var now = this.utcNow();
        if (Kept.TryGetValue(symbol, out var entry) && entry.Day == RelativeVolumeMath.TodayEt(now))
            return;
        if (this.retry.TryGetValue(symbol, out var failed) && now < failed.AtUtc)
            return;
        if (!this.queued.TryAdd(symbol, 0))
            return;

        this.queue.Enqueue(symbol);
        if (Interlocked.CompareExchange(ref this.working, 1, 0) == 0)
            _ = Task.Run(this.WorkAsync);
    }

    /// <summary>For the harness: waits until nothing is queued or being loaded.</summary>
    public async Task IdleAsync()
    {
        while (Volatile.Read(ref this.working) != 0 || !this.queue.IsEmpty)
            await Task.Delay(10).ConfigureAwait(false);
    }

    public void Dispose() => this.stop.Cancel();

    private async Task WorkAsync()
    {
        while (true)
        {
            while (!this.stop.IsCancellationRequested && this.queue.TryDequeue(out var symbol))
            {
                try
                {
                    await this.LoadAsync(symbol).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    if (this.stop.IsCancellationRequested)
                        break;
                    this.Failed(symbol, ex.GetBaseException().Message);
                }
                finally
                {
                    this.queued.TryRemove(symbol, out _);
                }
            }

            Volatile.Write(ref this.working, 0);

            // Something queued after the loop looked, but before it let go, would otherwise wait for the next Want.
            if (this.stop.IsCancellationRequested || this.queue.IsEmpty || Interlocked.CompareExchange(ref this.working, 1, 0) != 0)
                return;
        }
    }

    private async Task LoadAsync(string symbol)
    {
        var now = this.utcNow();
        var today = RelativeVolumeMath.TodayEt(now);
        var bars = await this.load(symbol + "{=1d}", now.AddDays(-DaysBack), now, this.stop.Token).ConfigureAwait(false);

        // Even a stock listed today has today's bar, so nothing at all means the request failed.
        if (bars.Count == 0)
        {
            this.Failed(symbol, "no daily bars came back");
            return;
        }

        var sessions = bars.Select(b => (DateTimeOffset.FromUnixTimeMilliseconds(b.TimeMs).UtcDateTime, b.Volume)).ToList();
        double average = RelativeVolumeMath.Average(sessions, today);
        int earlier = Math.Min(sessions.Count(s => s.Item1.Date < today), RelativeVolumeMath.Days);

        Kept[symbol] = new Entry(today, average);
        this.retry.TryRemove(symbol, out _);
        Log.Info(double.IsNaN(average)
            ? $"[rvol] {symbol} has {earlier} earlier sessions, under {RelativeVolumeMath.MinDays}, so no RVOL today"
            : $"[rvol] {symbol} averaged {FloatFacts.Shares(average)} a day over its last {earlier} sessions");
        this.arrived?.Invoke(symbol);
    }

    private void Failed(string symbol, string why)
    {
        int failures = this.retry.TryGetValue(symbol, out var before) ? before.Failures + 1 : 1;
        var wait = failures == 1 ? FirstRetry : LaterRetries;
        this.retry[symbol] = (this.utcNow() + wait, failures);
        Log.Info($"[rvol] {symbol} average volume not loaded, trying again in {wait.TotalMinutes:0} min: {why}");
    }
}

using System.Diagnostics;

namespace ScalpFeedVendor.Feed;

internal readonly record struct BarRow(long TimeMs, double Open, double High, double Low, double Close, double Volume, long Count);

/// <summary>
/// Loads chart history from Candle events on its own socket, so a large snapshot can't
/// hold up the live feed. dxFeed sends candle snapshots one at a time anyway, so requests
/// run one at a time too.
/// </summary>
internal sealed class HistoryClient : IFeedSink
{
    private static readonly IReadOnlyDictionary<string, string[]> Fields = new Dictionary<string, string[]>
    {
        ["Candle"] = ["eventType", "eventSymbol", "eventFlags", "time", "count", "open", "high", "low", "close", "volume"]
    };

    private static readonly TimeSpan ReadyTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan FirstRowTimeout = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan QuietTimeout = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan TotalTimeout = TimeSpan.FromSeconds(45);

    private readonly DxLinkLink link;
    private readonly SemaphoreSlim requestGate = new(1, 1);
    private readonly object collectorGate = new();
    private Collector active;
    private bool started;

    private sealed class Collector
    {
        public readonly Dictionary<long, BarRow> Bars = new();
        public readonly TaskCompletionSource Done = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public long LastRowStamp;
        public int Rows;
        public FeedSymbol Symbol;
        public string SymbolPrefix;
    }

    /// <summary>
    /// Whether a bar starting at barMs and lasting lengthMs (0: an instant) belongs in fromMs to toMs: it starts by
    /// toMs, and ends after fromMs, or with no length starts at or after it.
    /// </summary>
    public static bool InRange(long barMs, long lengthMs, long fromMs, long toMs) =>
        barMs <= toMs && (lengthMs > 0 ? barMs + lengthMs > fromMs : barMs >= fromMs);

    public HistoryClient(Func<bool, CancellationToken, Task<QuoteToken>> tokenSource)
    {
        this.link = new DxLinkLink("history", tokenSource, Fields, this);
    }

    /// <summary>Bars that start in the range.</summary>
    public Task<IReadOnlyList<BarRow>> LoadAsync(string candleSymbol, DateTime fromUtc, DateTime toUtc, CancellationToken ct) =>
        this.LoadAsync(candleSymbol, fromUtc, toUtc, ct, TimeSpan.Zero);

    /// <param name="barLength">
    /// The bars' length, so a bar that started before fromUtc but runs into it is kept. Without it a bar must start
    /// in the range. A chart asking for just the current day dropped today's daily bar, stamped at the day's start
    /// ("ANT{=1d,tho=true}: 0 bars from 1 rows", 2026-11-19), so today's candle had no history.
    /// </param>
    public async Task<IReadOnlyList<BarRow>> LoadAsync(string candleSymbol, DateTime fromUtc, DateTime toUtc, CancellationToken ct, TimeSpan barLength)
    {
        await this.requestGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!this.started)
            {
                this.started = true;
                this.link.Start();
            }

            if (!await this.link.WaitReadyAsync(ReadyTimeout, ct).ConfigureAwait(false))
            {
                Log.Info($"history link not ready for {candleSymbol}: {this.link.LastProblem}");
                return [];
            }

            var collector = new Collector
            {
                LastRowStamp = Stopwatch.GetTimestamp(),
                SymbolPrefix = candleSymbol[..(candleSymbol.IndexOf('{') + 1)]
            };
            lock (this.collectorGate)
                this.active = collector;

            var started = Stopwatch.StartNew();
            long fromMs = new DateTimeOffset(DateTime.SpecifyKind(fromUtc, DateTimeKind.Utc)).ToUnixTimeMilliseconds();
            long lengthMs = (long)barLength.TotalMilliseconds;

            // tastytrade sends only bars that start at or after the time asked from, so the bar already running at
            // fromUtc (today's daily bar, for a chart asking from an hour ago) needs asking from a bar's length before.
            await this.link.SendSubscriptionAsync([new FeedKey("Candle", candleSymbol)], null, reset: true, fromMs - Math.Max(0, lengthMs)).ConfigureAwait(false);

            string ending = await WaitForEndAsync(collector, started, ct).ConfigureAwait(false);

            List<BarRow> result;
            lock (this.collectorGate)
            {
                this.active = null;
                result = collector.Bars.Values.ToList();
            }

            await this.link.SendSubscriptionAsync(null, null, reset: true).ConfigureAwait(false);

            long toMs = new DateTimeOffset(DateTime.SpecifyKind(toUtc, DateTimeKind.Utc)).ToUnixTimeMilliseconds();
            result.RemoveAll(b => !InRange(b.TimeMs, lengthMs, fromMs, toMs));
            result.Sort((a, b) => a.TimeMs.CompareTo(b.TimeMs));
            Log.Info($"history {candleSymbol}: {result.Count} bars from {collector.Rows} rows in {started.ElapsedMilliseconds} ms ({ending})");
            return result;
        }
        finally
        {
            this.requestGate.Release();
        }
    }

    public Task StopAsync() => this.started ? this.link.StopAsync() : Task.CompletedTask;

    public void OnCandle(FeedRow row)
    {
        lock (this.collectorGate)
        {
            var collector = this.active;
            if (collector == null)
                return;

            // The previous request's live bar updates can still be in flight when this one
            // starts, and would plant its bars on this chart. So lock on to the symbol that
            // opens a snapshot, which the server spells differently from us ({=1m} is {=m}).
            if (collector.Symbol == null)
            {
                bool opensSnapshot = (row.Flags & (EventFlags.SnapshotBegin | EventFlags.SnapshotEnd | EventFlags.SnapshotSnip)) != 0;
                if (!opensSnapshot || !row.Symbol.Name.StartsWith(collector.SymbolPrefix, StringComparison.Ordinal))
                    return;
                collector.Symbol = row.Symbol;
            }
            else if (!ReferenceEquals(row.Symbol, collector.Symbol))
            {
                return;
            }

            collector.Rows++;
            collector.LastRowStamp = Stopwatch.GetTimestamp();

            // The snapshot's last row is often a marker (end + remove) rather than a bar,
            // and bars can repeat, so key by time and skip anything flagged as removed.
            bool removed = (row.Flags & EventFlags.Remove) != 0;
            bool valid = !double.IsNaN(row.Open) && !double.IsNaN(row.Close);
            if (!removed && valid)
                collector.Bars[row.Time] = new BarRow(row.Time, row.Open, row.High, row.Low, row.Close, double.IsNaN(row.Volume) ? 0 : row.Volume, row.Count);

            if ((row.Flags & (EventFlags.SnapshotEnd | EventFlags.SnapshotSnip)) != 0)
                collector.Done.TrySetResult();
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

    private static async Task<string> WaitForEndAsync(Collector collector, Stopwatch started, CancellationToken ct)
    {
        while (true)
        {
            var finished = await Task.WhenAny(collector.Done.Task, Task.Delay(100, ct)).ConfigureAwait(false);
            if (finished == collector.Done.Task)
                return "snapshot end";

            ct.ThrowIfCancellationRequested();

            double quiet = (Stopwatch.GetTimestamp() - Volatile.Read(ref collector.LastRowStamp)) / (double)Stopwatch.Frequency;
            if (Volatile.Read(ref collector.Rows) == 0)
            {
                if (started.Elapsed > FirstRowTimeout)
                    return "no rows";
            }
            else if (quiet > QuietTimeout.TotalSeconds)
            {
                return "went quiet";
            }

            if (started.Elapsed > TotalTimeout)
                return "timed out";
        }
    }
}

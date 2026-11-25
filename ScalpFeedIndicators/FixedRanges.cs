using System.Text.Json;
using ScalpFeedVendor.Feed;

namespace ScalpFeedIndicators;

/// <summary>
/// A stretch of a stock's chart, from its first candle's start to its last one's end; with ExtendRight it's built on to
/// the newest candle, as TradingView's "Extend right" has it.
/// </summary>
public readonly record struct FixedRange(long Id, long StartMs, long EndMs, bool ExtendRight = false);

/// <summary>
/// The ranges you've put fixed range profiles on, by stock: the same on every chart of it, and kept in a file so they're
/// there after a restart. One whose end is over 30 days old is dropped. This depends on nothing in Quantower, so the
/// harness can test it.
/// </summary>
public sealed class FixedRanges
{
    private static readonly TimeSpan Keep = TimeSpan.FromDays(30);

    private readonly string path;
    private readonly object gate = new();
    private readonly Dictionary<string, FixedRange[]> bySymbol = new(StringComparer.Ordinal);
    private long lastId;

    public FixedRanges(string path, long nowMs)
    {
        this.path = path;
        this.Load(nowMs);
    }

    /// <summary>Every chart's.</summary>
    public static FixedRanges Shared { get; } = new(
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".scalpfeed", "fixed-ranges.json"),
        DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

    /// <summary>When a stock's ranges change, with its name. Must not block.</summary>
    public event Action<string> Changed;

    /// <summary>Not changed once given: a new array takes its place.</summary>
    public FixedRange[] Of(string symbol)
    {
        lock (this.gate)
            return this.bySymbol.GetValueOrDefault(symbol, []);
    }

    public FixedRange Add(string symbol, long startMs, long endMs)
    {
        FixedRange added;
        lock (this.gate)
        {
            added = new FixedRange(++this.lastId, startMs, endMs);
            this.bySymbol[symbol] = [.. this.bySymbol.GetValueOrDefault(symbol, []), added];
            this.Save();
        }

        this.Changed?.Invoke(symbol);
        return added;
    }

    /// <summary>In place of the one with its id.</summary>
    public void Replace(string symbol, FixedRange with) => this.Change(symbol, with.Id, _ => with);

    public void Remove(string symbol, long id) => this.Change(symbol, id, _ => null);

    private void Change(string symbol, long id, Func<FixedRange, FixedRange?> with)
    {
        lock (this.gate)
        {
            var had = this.bySymbol.GetValueOrDefault(symbol, []);
            var now = had.Select(r => r.Id == id ? with(r) : r).OfType<FixedRange>().ToArray();
            if (now.SequenceEqual(had))
                return;

            if (now.Length > 0)
                this.bySymbol[symbol] = now;
            else
                this.bySymbol.Remove(symbol);
            this.Save();
        }

        this.Changed?.Invoke(symbol);
    }

    private void Load(long nowMs)
    {
        try
        {
            if (!File.Exists(this.path))
                return;

            long oldest = nowMs - (long)Keep.TotalMilliseconds;
            foreach (var (symbol, ranges) in JsonSerializer.Deserialize<Dictionary<string, FixedRange[]>>(File.ReadAllText(this.path)) ?? [])
            {
                var kept = (ranges ?? []).Where(r => r.EndMs >= oldest && r.EndMs > r.StartMs).ToArray();
                if (kept.Length == 0)
                    continue;

                this.bySymbol[symbol] = kept;
                this.lastId = Math.Max(this.lastId, kept.Max(r => r.Id));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // None to be had: it starts with none, and the next one placed writes the file again.
        }
    }

    private void Save()
    {
        try
        {
            CacheFile.Replace(this.path, JsonSerializer.Serialize(this.bySymbol));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // They still show; they just won't be there after a restart.
        }
    }
}

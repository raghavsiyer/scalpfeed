namespace ScalpFeedVendor.Feed;

/// <summary>
/// How far each stock has climbed or dropped in IBKR's top gainers scan over the last 5 minutes (your request,
/// 2026-11-19: a red or green arrow for stocks moving up or down the scanner). A Watchlist column takes only numbers,
/// so it's the places climbed, net: +3 up three places, -2 down two, 0 back where it was. It's the stock's place now
/// against its place in the newest scan 5 minutes old or more, or in the scanner's first 5 minutes against the first
/// scan. A stock that wasn't on the list then counts from one place below that scan's last row. Blank for a stock
/// not on the list now, or before a second scan.
///
/// Earlier the same day it compared against a minute before (mostly 0, your feedback), then showed each stock's last
/// move as it happened; you then chose the total over 5 minutes. Thread-safe; nothing here needs Quantower.
/// </summary>
internal sealed class ScanRanks
{
    public static readonly TimeSpan Lookback = TimeSpan.FromMinutes(5);

    private readonly object gate = new();
    private readonly List<(long Ticks, Dictionary<string, int> Ranks, int Rows)> history = [];
    private Dictionary<string, int> current = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>A scan's answer, best first, at utcTicks.</summary>
    public void Record(IReadOnlyList<string> names, long utcTicks)
    {
        var ranks = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < names.Count; i++)
            ranks.TryAdd(names[i], i + 1);

        lock (this.gate)
        {
            this.history.Add((utcTicks, ranks, names.Count));
            this.current = ranks;

            // Keep the newest that's 5 minutes old or more, and everything after it.
            long cutoff = utcTicks - Lookback.Ticks;
            while (this.history.Count >= 2 && this.history[1].Ticks <= cutoff)
                this.history.RemoveAt(0);
        }
    }

    /// <summary>Places climbed, net, over the last 5 minutes: up positive, down negative; NaN when there's nothing to say.</summary>
    public double Change(string symbol, long utcTicks)
    {
        if (string.IsNullOrEmpty(symbol))
            return double.NaN;

        lock (this.gate)
        {
            if (this.history.Count < 2 || !this.current.TryGetValue(symbol, out int now))
                return double.NaN;

            // The newest scan 5 minutes old or more; in the first 5 minutes, the first.
            long cutoff = utcTicks - Lookback.Ticks;
            var baseline = this.history[0];
            for (int i = this.history.Count - 1; i >= 0; i--)
            {
                if (this.history[i].Ticks <= cutoff)
                {
                    baseline = this.history[i];
                    break;
                }
            }

            int before = baseline.Ranks.TryGetValue(symbol, out int was) ? was : baseline.Rows + 1;
            return before - now;
        }
    }

    public void Clear()
    {
        lock (this.gate)
        {
            this.history.Clear();
            this.current = new(StringComparer.OrdinalIgnoreCase);
        }
    }
}

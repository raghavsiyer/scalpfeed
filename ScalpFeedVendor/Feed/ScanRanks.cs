namespace ScalpFeedVendor.Feed;

/// <summary>
/// How far each stock has climbed or dropped in IBKR's top gainers scan over the last minute (your request,
/// 2026-11-19: a red or green arrow for stocks moving up or down the scanner). A Watchlist column takes only numbers,
/// so it's the places climbed: +3 up three places, -2 down two, 0 the same. A stock new to the list counts from one
/// place below the list's last row a minute ago. Blank for a stock not on the list now, and until the scan has run a
/// minute. The scan answers every 2 seconds, so ranks are kept a minute and a bit; the one compared against is the
/// newest a minute old or more. Thread-safe; nothing here needs Quantower.
/// </summary>
internal sealed class ScanRanks
{
    public static readonly TimeSpan Lookback = TimeSpan.FromMinutes(1);

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

            // Keep the newest that's a minute old or more, and everything after it.
            long cutoff = utcTicks - Lookback.Ticks;
            while (this.history.Count >= 2 && this.history[1].Ticks <= cutoff)
                this.history.RemoveAt(0);
        }
    }

    /// <summary>Places climbed over the last minute: up positive, down negative; NaN when there's nothing to say.</summary>
    public double Change(string symbol, long utcTicks)
    {
        if (string.IsNullOrEmpty(symbol))
            return double.NaN;

        lock (this.gate)
        {
            if (!this.current.TryGetValue(symbol, out int now))
                return double.NaN;

            long cutoff = utcTicks - Lookback.Ticks;
            int baseline = -1;
            for (int i = this.history.Count - 1; i >= 0; i--)
            {
                if (this.history[i].Ticks <= cutoff)
                {
                    baseline = i;
                    break;
                }
            }

            if (baseline < 0)
                return double.NaN;

            var (_, ranks, rows) = this.history[baseline];
            int before = ranks.TryGetValue(symbol, out int was) ? was : rows + 1;
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

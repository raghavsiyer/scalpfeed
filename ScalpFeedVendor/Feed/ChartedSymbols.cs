namespace ScalpFeedVendor.Feed;

/// <summary>
/// The stocks opened in a chart today, added to the end of IBKR Top Gainers when the scan hasn't got them (your
/// request, 2026-11-19), so a ticker you searched sits in the list with the gainers. A chart is told apart from a
/// Watchlist row by asking for intraday bars: a row asks for none, only the daily bars for RVOL. Most recent first,
/// at most 10, and cleared at the start of each Eastern day. Thread-safe; nothing here needs Quantower.
/// </summary>
internal sealed class ChartedSymbols
{
    public const int Most = 10;

    private readonly object gate = new();
    private readonly List<string> symbols = [];
    private string day;

    /// <summary>Notes a symbol charted on this Eastern day. True when the list changed.</summary>
    public bool Add(string symbol, string today)
    {
        if (string.IsNullOrWhiteSpace(symbol))
            return false;

        lock (this.gate)
        {
            this.Roll(today);
            if (this.symbols.Count > 0 && string.Equals(this.symbols[0], symbol, StringComparison.OrdinalIgnoreCase))
                return false;

            this.symbols.RemoveAll(s => string.Equals(s, symbol, StringComparison.OrdinalIgnoreCase));
            this.symbols.Insert(0, symbol);
            if (this.symbols.Count > Most)
                this.symbols.RemoveRange(Most, this.symbols.Count - Most);
            return true;
        }
    }

    /// <summary>Today's charted symbols, most recent first.</summary>
    public IReadOnlyList<string> Today(string today)
    {
        lock (this.gate)
        {
            this.Roll(today);
            return [.. this.symbols];
        }
    }

    /// <summary>The scan's rows, then each charted symbol the scan hasn't got.</summary>
    public static IReadOnlyList<string> Merge(IReadOnlyList<string> scan, IReadOnlyList<string> charted)
    {
        var seen = new HashSet<string>(scan, StringComparer.OrdinalIgnoreCase);
        var merged = new List<string>(scan);
        foreach (var s in charted)
        {
            if (seen.Add(s))
                merged.Add(s);
        }

        return merged;
    }

    private void Roll(string today)
    {
        if (this.day == today)
            return;

        this.day = today;
        this.symbols.Clear();
    }
}

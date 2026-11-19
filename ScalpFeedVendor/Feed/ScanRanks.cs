namespace ScalpFeedVendor.Feed;

/// <summary>
/// Each stock's last move in IBKR's top gainers scan (your request, 2026-11-19: a red or green arrow for stocks
/// moving up or down the scanner). A Watchlist column takes only numbers, so it's the places the stock moved the last
/// time its place changed: +3 up three places, -2 down two. It changes the moment a scan has the stock in a new
/// place (your feedback, the same day: a minute's comparison mostly showed 0) and stays until the next change. A
/// stock new to the list moved from one place below the previous scan's last row. Blank for a stock not on the list
/// now, or that hasn't moved since it's been watched. Thread-safe; nothing here needs Quantower.
/// </summary>
internal sealed class ScanRanks
{
    private readonly object gate = new();
    private Dictionary<string, int> current;
    private int currentRows;
    private readonly Dictionary<string, int> lastMove = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>A scan's answer, best first.</summary>
    public void Record(IReadOnlyList<string> names)
    {
        var ranks = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < names.Count; i++)
            ranks.TryAdd(names[i], i + 1);

        lock (this.gate)
        {
            if (this.current != null)
            {
                foreach (var (symbol, now) in ranks)
                {
                    int before = this.current.TryGetValue(symbol, out int was) ? was : this.currentRows + 1;
                    if (before != now)
                        this.lastMove[symbol] = before - now;
                }

                foreach (var symbol in this.lastMove.Keys.Where(s => !ranks.ContainsKey(s)).ToList())
                    this.lastMove.Remove(symbol);
            }

            this.current = ranks;
            this.currentRows = names.Count;
        }
    }

    /// <summary>The places the stock moved at its last change: up positive, down negative; NaN when there's none.</summary>
    public double Change(string symbol)
    {
        if (string.IsNullOrEmpty(symbol))
            return double.NaN;

        lock (this.gate)
            return this.lastMove.TryGetValue(symbol, out int moved) ? moved : double.NaN;
    }

    public void Clear()
    {
        lock (this.gate)
        {
            this.current = null;
            this.currentRows = 0;
            this.lastMove.Clear();
        }
    }
}

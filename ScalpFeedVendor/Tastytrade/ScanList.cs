using ScalpFeedVendor.Feed;
using TradingPlatform.BusinessLayer;

namespace ScalpFeedVendor.Tastytrade;

/// <summary>
/// One of IBKR's scans kept as a symbol list you open in a Watchlist: IBKR Top Gainers, the regular session's or
/// after hours the after-hours ones, and IBKR Halts (IbkrScan). Looking the symbols up can call back into this
/// connection (a symbol it hasn't made yet), so it runs off IBKR's thread, one list at a time. A list the same as
/// the last is left alone, unless the list has gone (deleted in Quantower), when it's made again. Stocks only:
/// leveraged ETFs (SOXS, UVIX, MSTZ on 11-16) crowd a gainers list. With <paramref name="charted"/>, the stocks
/// opened in a chart today follow the scan's rows (ChartedSymbols), ETFs included, since you asked for those.
/// </summary>
internal sealed class ScanList(string name, bool logEachChange, ChartedSymbols charted = null)
{
    private readonly object gate = new();
    private IReadOnlyList<string> sent;
    private IReadOnlyList<string> next;
    private IReadOnlyList<string> scan = [];
    private bool logged;
    private string lostLogged;

    public string Name { get; } = name;

    /// <summary>
    /// Sends Quantower a symbol from this connection before it's looked up (TastytradeMarketVendor.MakeSymbol);
    /// false when tastytrade hasn't got it, which leaves it out.
    /// </summary>
    public Func<string, bool> Make { get; set; }

    public void Offer(IReadOnlyList<string> symbols)
    {
        lock (this.gate)
        {
            this.scan = symbols;
            this.Queue();
        }
    }

    /// <summary>A symbol just opened in a chart: added to the list now if the scan hasn't got it.</summary>
    public void Charted(string symbol)
    {
        if (charted == null || !charted.Add(symbol, FloatFacts.DayOf(DateTime.UtcNow)))
            return;

        lock (this.gate)
            this.Queue();
    }

    private void Queue()
    {
        bool idle = this.next == null;
        this.next = charted == null ? this.scan : ChartedSymbols.Merge(this.scan, charted.Today(FloatFacts.DayOf(DateTime.UtcNow)));
        if (idle)
            _ = Task.Run(this.PublishAll);
    }

    private void PublishAll()
    {
        while (true)
        {
            IReadOnlyList<string> names;
            lock (this.gate)
            {
                names = this.next;
                if (names == null)
                    return;
            }

            bool whole = false;
            try
            {
                whole = this.Publish(names);
            }
            catch (Exception ex)
            {
                Log.Info($"[scan] couldn't update {this.Name}: {ex.GetBaseException().Message}");
            }

            lock (this.gate)
            {
                // A list with a symbol Quantower didn't give back is sent again with the next scan.
                this.sent = whole ? names : null;
                if (ReferenceEquals(this.next, names))
                {
                    this.next = null;
                    return;
                }
            }
        }
    }

    /// <summary>True when Quantower gave back every symbol tastytrade has.</summary>
    private bool Publish(IReadOnlyList<string> names)
    {
        bool exists = Core.Instance.SymbolList.Any(l => l.Name == this.Name);
        if (exists && this.sent != null && names.SequenceEqual(this.sent))
            return true;

        string connectionId = Core.Instance.Connections.Connected.FirstOrDefault(c => c.VendorName == ScalpFeedVendor.VendorName)?.Id;
        if (connectionId == null)
            return false;

        var symbols = new List<Symbol>();
        var lost = new List<string>();
        foreach (var name in names)
        {
            if (this.Make != null && !this.Make(name))
                continue;

            // GetSymbol gives null for a stock a Watchlist already had open from the saved layout (POAS, VIVS and
            // others on 2026-11-19, though they quoted fine), so those are found among the symbols already loaded.
            var symbol = Core.Instance.GetSymbol(new GetSymbolRequestParameters { SymbolId = name }, connectionId)
                ?? Core.Instance.Symbols.FirstOrDefault(s => s.ConnectionId == connectionId && (string.Equals(s.Id, name, StringComparison.OrdinalIgnoreCase) || string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase)));
            if (symbol == null)
                lost.Add(name);
            else if (symbol.SymbolType != SymbolType.ETF || !this.scan.Contains(name, StringComparer.OrdinalIgnoreCase))
                symbols.Add(symbol);
        }

        if (lost.Count > 0)
        {
            string text = string.Join(" ", lost);
            if (text != this.lostLogged)
                Log.Info($"[scan] {this.Name}: Quantower gave no symbol for {text}; trying again with the next scan");
            this.lostLogged = text;
        }

        if (exists)
            Core.Instance.ReplaceSymbolList(this.Name, symbols);
        else
            Core.Instance.AddSymbolList(this.Name, symbols);

        // The gainers change every 30 seconds or so, so only the first of each connection and each remaking go in
        // the log; the halts list changes seldom, and each change is worth a line.
        if (logEachChange || !this.logged || !exists)
            Log.Info($"[scan] {this.Name}: {symbols.Count} stocks of {names.Count} rows{(symbols.Count > 0 ? ", " : "")}{string.Join(" ", symbols.Take(10).Select(s => s.Name))}{(symbols.Count > 10 ? " ..." : "")}");
        this.logged = true;
        return lost.Count == 0;
    }
}

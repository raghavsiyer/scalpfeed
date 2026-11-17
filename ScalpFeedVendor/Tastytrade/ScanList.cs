using ScalpFeedVendor.Feed;
using TradingPlatform.BusinessLayer;

namespace ScalpFeedVendor.Tastytrade;

/// <summary>
/// One of IBKR's scans kept as a symbol list you open in a Watchlist: IBKR Top Gainers, the regular session's or
/// after hours the after-hours ones, and IBKR Halts (IbkrScan). Looking the symbols up can call back into this
/// connection (a symbol it hasn't made yet), so it runs off IBKR's thread, one list at a time. A list the same as
/// the last is left alone, unless the list has gone (deleted in Quantower), when it's made again. Stocks only:
/// leveraged ETFs (SOXS, UVIX, MSTZ on 11-16) crowd a gainers list.
/// </summary>
internal sealed class ScanList(string name, bool logEachChange)
{
    private readonly object gate = new();
    private IReadOnlyList<string> sent;
    private IReadOnlyList<string> next;
    private bool logged;

    public string Name { get; } = name;

    public void Offer(IReadOnlyList<string> symbols)
    {
        lock (this.gate)
        {
            bool idle = this.next == null;
            this.next = symbols;
            if (idle)
                _ = Task.Run(this.PublishAll);
        }
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

            try
            {
                this.Publish(names);
            }
            catch (Exception ex)
            {
                Log.Info($"[scan] couldn't update {this.Name}: {ex.GetBaseException().Message}");
            }

            lock (this.gate)
            {
                this.sent = names;
                if (ReferenceEquals(this.next, names))
                {
                    this.next = null;
                    return;
                }
            }
        }
    }

    private void Publish(IReadOnlyList<string> names)
    {
        bool exists = Core.Instance.SymbolList.Any(l => l.Name == this.Name);
        if (exists && this.sent != null && names.SequenceEqual(this.sent))
            return;

        string connectionId = Core.Instance.Connections.Connected.FirstOrDefault(c => c.VendorName == ScalpFeedVendor.VendorName)?.Id;
        var symbols = new List<Symbol>();
        foreach (var name in names)
        {
            var symbol = Core.Instance.GetSymbol(new GetSymbolRequestParameters { SymbolId = name }, connectionId);
            if (symbol != null && symbol.SymbolType != SymbolType.ETF)
                symbols.Add(symbol);
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
    }
}

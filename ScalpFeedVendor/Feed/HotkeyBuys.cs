namespace ScalpFeedVendor.Feed;

/// <summary>
/// The buy hotkeys' orders (your choice, 2026-11-24, on paper, which is all they trade on): from 9:30 to 16:00 ET each
/// works for a second and is then cancelled, where it was immediate or cancel. On a fast stock IBKR caps a buy's limit
/// at about the last trade (message 2161), so an IOC can find nothing offered at it and be cancelled within a fraction
/// of a second. Orders that stay open could add up, so there's one open at a time in a stock: a press cancels the one
/// before it if it's still open, waits for IBKR to confirm, and sends nothing if it doesn't. Buys already filled stand,
/// and so do orders from anywhere but these keys. A sell key wins over a buy still waiting: that buy isn't sent. The
/// cancel comes from here, so closing Quantower inside the second can leave the order working. Nothing here needs
/// Quantower.
/// </summary>
internal sealed class HotkeyBuys
{
    /// <summary>How many times, a second apart, a cancel is asked for before it's left to you: IBKR may be away a while.</summary>
    private const int MostAsks = 120;

    private readonly IbkrTrader trader;
    private readonly TimeSpan cancelWait;
    private readonly TimeSpan askAgain;

    // One buy press at a time, so each sees the one before it; and a buy going against a sell key.
    private readonly object pressGate = new();
    private readonly object placeGate = new();
    private readonly Dictionary<string, int> last = new(StringComparer.Ordinal);
    private int sellPresses;

    /// <summary>What a press did: its order, or why none; and what it cancelled first, for the log.</summary>
    public readonly record struct Result(int Id, IbkrOrderTicket Ticket, double Quote, string Problem, string Cleared);

    /// <param name="cancelWait">How long a press waits for IBKR to confirm the last buy's cancel.</param>
    public HotkeyBuys(IbkrTrader trader, TimeSpan cancelWait, TimeSpan? askAgain = null)
    {
        this.trader = trader;
        this.cancelWait = cancelWait;
        this.askAgain = askAgain ?? TimeSpan.FromSeconds(1);
    }

    /// <summary>A sell key, before it does anything else: buys pressed before it that haven't gone don't go.</summary>
    public void SellPressed()
    {
        lock (this.placeGate)
            this.sellPresses++;
    }

    /// <summary>
    /// A buy key on this stock. The order is decided after the last one's cancel is confirmed, for the quote then.
    /// With workFor more than zero it's cancelled after that long; with zero it's left working, as before 9:30.
    /// </summary>
    public Result Press(string symbol, TimeSpan workFor, Func<(IbkrOrderTicket Ticket, double Quote, string Problem)> decide)
    {
        int sells;
        lock (this.placeGate)
            sells = this.sellPresses;

        lock (this.pressGate)
        {
            string cleared = null;
            if (this.last.TryGetValue(symbol, out int open) && this.trader.Order(open) is { IsDone: false })
            {
                cleared = this.trader.CancelAndWait(open, this.cancelWait, out bool done);
                if (!done)
                    return new Result(0, null, double.NaN, $"the last buy, order {open}, isn't cancelled yet", cleared);
            }

            var (ticket, quote, problem) = decide();
            if (problem != null)
                return new Result(0, ticket, quote, problem, cleared);

            int id;
            lock (this.placeGate)
            {
                if (sells != this.sellPresses)
                    return new Result(0, ticket, quote, "a sell key was pressed after it", cleared);

                (id, problem) = this.trader.Place(ticket);
            }

            if (problem == null && workFor > TimeSpan.Zero)
            {
                this.last[symbol] = id;
                _ = this.CancelAfterAsync(id, workFor);
            }

            return new Result(id, ticket, quote, problem, cleared);
        }
    }

    private async Task CancelAfterAsync(int id, TimeSpan workFor)
    {
        try
        {
            await Task.Delay(workFor).ConfigureAwait(false);
            for (int asks = 0; asks < MostAsks; asks++)
            {
                if (this.trader.Order(id) is not { IsDone: false } order)
                    return;

                // One already being cancelled, by another press or a sell key, is only asked about again if it stays so.
                string error = asks == 0 && order.Status == "PendingCancel" ? null : this.trader.Cancel(id);
                if (asks == 0)
                    Log.Info($"[hotkey] order {id}: {order.Filled:#,0} of {order.Quantity:#,0} filled in {workFor.TotalSeconds:0.##} s, cancelling the rest{(error == null ? "" : $": {error}")}");
                await Task.Delay(this.askAgain).ConfigureAwait(false);
            }

            Log.Info($"[hotkey] order {id} is still working after {MostAsks} tries to cancel it: cancel it yourself");
        }
        catch (Exception ex)
        {
            Log.Info($"[hotkey] order {id} wasn't cancelled: {ex.GetType().Name} {ex.Message}");
        }
    }
}

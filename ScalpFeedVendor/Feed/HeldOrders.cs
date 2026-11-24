namespace ScalpFeedVendor.Feed;

/// <summary>An order placed in Quantower on a halted stock, waiting here: its ID (no IBKR order has one that high) and what it asks for.</summary>
internal sealed record HeldOrder(int Id, IbkrOrderTicket Ticket, DateTime HeldUtc);

/// <summary>
/// Orders placed in Quantower while their stock is halted (your request, 2026-11-24): taken, but kept here until it
/// trades again, then sent in the order they were placed. IBKR's paper simulator doesn't wait: it fills an order in a
/// halt from quotes it still has, while nothing trades. Quantower shows a held order as working, and it can be changed
/// or cancelled. One whose day is over, or a market or stop order whose stock trades again only outside 9:30-16:00 ET,
/// isn't sent at all, as IBKR would keep it for the next open. Held orders outlive a reconnect but not Quantower.
///
/// A halt counts as over when IBKR says it is. When IBKR stops saying (its connection dropped), a new quote stream says
/// Halted only while it is, so the stock is taken to be trading once its quotes have come for a while without that.
/// Orders already at IBKR when a halt begins aren't held, and neither are hotkeys, which refuse a halted stock. Nothing
/// here needs Quantower.
/// </summary>
internal sealed class HeldOrders
{
    /// <summary>The connection's part: whether a stock is halted (more than 0 yes, 0 no, -1 not known) and its quotes are coming, and so on.</summary>
    /// <param name="Watch">Keeps a stock's quote coming, so its halt's end is heard, whatever Quantower has on screen.</param>
    /// <param name="Gone">A held order that isn't any more: sent or cancelled (no problem), or not sent and why.</param>
    public sealed record Hooks(
        Func<string, (int Halted, bool Quoting)> Halt,
        Action<string, bool> Watch,
        Func<bool> Ready,
        Func<IbkrOrderTicket, (int Id, string Error)> Place,
        Action<HeldOrder> Shown,
        Action<HeldOrder, string> Gone);

    public static HeldOrders Shared { get; } = new();

    private const int FirstId = 2_000_000_000;
    private static readonly TimeZoneInfo Eastern = TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");
    private static readonly TimeSpan CheckEvery = TimeSpan.FromMilliseconds(200);
    private static int lastId = FirstId;

    private readonly object gate = new();
    private readonly List<HeldOrder> orders = [];
    private readonly Dictionary<string, DateTime> unknownSince = new(StringComparer.Ordinal);
    private readonly Func<DateTime> utcNow;
    private readonly Timer timer;
    private Hooks hooks;
    private int checking;

    /// <summary>How long a stock's quotes have to come with no word of a halt before it's taken to be trading.</summary>
    public static readonly TimeSpan UnknownWait = TimeSpan.FromSeconds(5);

    /// <param name="onTimer">The harness passes false and calls Check itself.</param>
    public HeldOrders(Func<DateTime> utcNow = null, bool onTimer = true)
    {
        this.utcNow = utcNow ?? (() => DateTime.UtcNow);
        if (onTimer)
            this.timer = new Timer(_ => this.Check(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    public static bool IsHeldId(int id) => id > FirstId;

    /// <summary>A connection has started: it watches the stocks with orders held and shows them.</summary>
    public void Connect(Hooks with)
    {
        Volatile.Write(ref this.hooks, with);
        var held = this.All();
        foreach (string symbol in held.Select(h => h.Ticket.Symbol).Distinct())
            with.Watch(symbol, true);
        foreach (var order in held)
            with.Shown(order);
        this.Run(held.Count > 0);
    }

    /// <summary>It has stopped: what's held stays, and nothing is sent until the next one starts.</summary>
    public void Disconnect(Hooks from) => Interlocked.CompareExchange(ref this.hooks, null, from);

    public IReadOnlyList<HeldOrder> All()
    {
        lock (this.gate)
            return [.. this.orders];
    }

    /// <summary>Keeps the order if its stock is halted. Null when it isn't, or isn't known to be: it's sent as ever.</summary>
    public HeldOrder TryHold(IbkrOrderTicket ticket)
    {
        var with = Volatile.Read(ref this.hooks);
        if (with == null || with.Halt(ticket.Symbol).Halted <= 0)
            return null;

        var held = new HeldOrder(Interlocked.Increment(ref lastId), ticket, this.utcNow());
        lock (this.gate)
            this.orders.Add(held);
        with.Watch(ticket.Symbol, true);
        Log.Info($"[held] {ticket.Symbol} is halted: {Describe(ticket)} held as {held.Id} until it trades again");
        with.Shown(held);
        this.Run(true);
        return held;
    }

    /// <summary>Changes a held order's quantity or prices. Returns why not, or null.</summary>
    public string Modify(int id, double quantity, double limitPrice, double stopPrice)
    {
        HeldOrder changed;
        lock (this.gate)
        {
            int at = this.orders.FindIndex(o => o.Id == id);
            if (at < 0)
                return $"order {id} isn't held any more";

            var ticket = this.orders[at].Ticket;
            ticket = ticket with
            {
                Quantity = quantity > 0 ? quantity : ticket.Quantity,
                LimitPrice = double.IsNaN(ticket.LimitPrice) ? double.NaN : limitPrice,
                StopPrice = double.IsNaN(ticket.StopPrice) ? double.NaN : stopPrice,
            };
            if (IbkrTrader.Check(ticket) is string problem)
                return problem;

            changed = this.orders[at] = this.orders[at] with { Ticket = ticket };
        }

        Log.Info($"[held] {id} changed to {Describe(changed.Ticket)}");
        Volatile.Read(ref this.hooks)?.Shown(changed);
        return null;
    }

    /// <summary>Drops a held order. Returns why not, or null.</summary>
    public string Cancel(int id)
    {
        var held = this.Take(id);
        if (held == null)
            return $"order {id} isn't held any more";

        Log.Info($"[held] {id} ({Describe(held.Ticket)}) cancelled");
        this.Finish(held, null);
        return null;
    }

    /// <summary>Sends what's held for stocks that trade again, and drops what can't be sent any more.</summary>
    public void Check()
    {
        if (Interlocked.Exchange(ref this.checking, 1) != 0)
            return;

        try
        {
            var with = Volatile.Read(ref this.hooks);
            if (with == null)
                return;

            var now = this.utcNow();
            foreach (var stock in this.All().GroupBy(o => o.Ticket.Symbol))
            {
                foreach (var held in stock.Where(o => Day(o.HeldUtc) != Day(now)).ToList())
                    this.Drop(held, "the day it was placed is over");

                if (!this.Trades(stock.Key, with, now) || !with.Ready())
                    continue;

                foreach (var held in stock)
                {
                    if (!IbkrTrader.OutsideRthFor(held.Ticket.Type) && !OrderHotkeys.InRegularHours(now))
                        this.Drop(held, "it trades again outside 9:30-16:00 ET, where IBKR would keep the order for the next open");
                    else
                        this.Send(held, with);
                }
            }

            this.Run(this.All().Count > 0);
        }
        catch (Exception ex)
        {
            Log.Info($"[held] checking: {ex.GetType().Name} {ex.Message}");
        }
        finally
        {
            Volatile.Write(ref this.checking, 0);
        }
    }

    public static string Describe(IbkrOrderTicket t) =>
        $"{(t.Buy ? "buy" : "sell")} {t.Quantity:#,0} {t.Symbol} {t.Type}{(double.IsNaN(t.LimitPrice) ? "" : $" limit {t.LimitPrice}")}{(double.IsNaN(t.StopPrice) ? "" : $" stop {t.StopPrice}")}";

    private bool Trades(string symbol, Hooks with, DateTime now)
    {
        var (halted, quoting) = with.Halt(symbol);
        if (halted == 0)
            return true;

        if (halted > 0 || !quoting)
        {
            this.unknownSince.Remove(symbol);
            return false;
        }

        if (!this.unknownSince.TryGetValue(symbol, out var since))
            this.unknownSince[symbol] = since = now;
        return now - since >= UnknownWait;
    }

    private void Send(HeldOrder held, Hooks with)
    {
        // Cancelled or changed since this look: the one there now goes, or none.
        held = this.Take(held.Id);
        if (held == null)
            return;

        var (id, error) = with.Place(held.Ticket);
        Log.Info(error == null
            ? $"[held] {held.Ticket.Symbol} trades again: {held.Id} sent as order {id}, {Describe(held.Ticket)}"
            : $"[held] {held.Ticket.Symbol} trades again, but {held.Id} ({Describe(held.Ticket)}) wasn't sent: {error}");
        this.Finish(held, error);
    }

    private void Drop(HeldOrder held, string why)
    {
        held = this.Take(held.Id);
        if (held == null)
            return;

        Log.Info($"[held] {held.Id} ({Describe(held.Ticket)}) not sent: {why}");
        this.Finish(held, why);
    }

    private HeldOrder Take(int id)
    {
        lock (this.gate)
        {
            int at = this.orders.FindIndex(o => o.Id == id);
            if (at < 0)
                return null;

            var held = this.orders[at];
            this.orders.RemoveAt(at);
            return held;
        }
    }

    private void Finish(HeldOrder held, string problem)
    {
        var with = Volatile.Read(ref this.hooks);
        with?.Gone(held, problem);

        // The quote stays on while the stock has any left.
        bool more;
        lock (this.gate)
            more = this.orders.Any(o => o.Ticket.Symbol == held.Ticket.Symbol);
        if (more)
            return;

        this.unknownSince.Remove(held.Ticket.Symbol);
        with?.Watch(held.Ticket.Symbol, false);
    }

    private void Run(bool on) =>
        this.timer?.Change(on ? CheckEvery : Timeout.InfiniteTimeSpan, on ? CheckEvery : Timeout.InfiniteTimeSpan);

    private static DateTime Day(DateTime utc) => TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), Eastern).Date;
}

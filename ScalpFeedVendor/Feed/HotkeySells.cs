using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;

namespace ScalpFeedVendor.Feed;

/// <summary>
/// The sell hotkeys' orders. IBKR refuses a sell more than 9.8% under its own "current market price", which lags a fast
/// drop, so it refused sells you pressed under a falling bid (27 of them by 2026-11-25). Its refusal says the price it
/// won't go to, so a press it refuses that way is sent again once, at the first price past that one (your choice,
/// 2026-11-25), decided afresh: the bid, the position and what's working as they are then, never more shares than it
/// was first for and never another stock. It only goes at or under the bid, where it can fill: one above it would just
/// be cancelled, or outside 9:30-16:00 ET rest there, and until then the shares in it would be all the next press finds.
/// Presses and resends are decided and placed one at a time, so two can't go for the same shares. Nothing here needs
/// Quantower.
/// </summary>
internal sealed partial class HotkeySells
{
    /// <summary>How long after a press its order is still sent again. IBKR's refusals came 0.1 to 0.3 s after.</summary>
    public static readonly TimeSpan ResendWithin = TimeSpan.FromSeconds(3);

    /// <summary>How long a resend waits for IBKR to say the refused order is done.</summary>
    private static readonly TimeSpan DoneWait = TimeSpan.FromMilliseconds(500);

    private readonly IbkrTrader trader;
    private readonly TimeSpan within;
    private readonly object placeGate = new();
    private readonly Dictionary<int, Pressed> pressed = new();

    public HotkeySells(IbkrTrader trader, TimeSpan? within = null)
    {
        this.trader = trader;
        this.within = within ?? ResendWithin;
    }

    /// <summary>What a refused press came to: sent again; not, as IBKR's lowest price is over the bid; nothing left to sell; or not for the reason in Problem.</summary>
    public enum Outcome
    {
        Sent,
        AboveBid,
        NothingLeft,
        NotSent,
    }

    /// <param name="Refused">The order IBKR refused.</param>
    /// <param name="Past">The price it said it won't take a sell at or under.</param>
    /// <param name="Lowest">The first price it takes: the next tick over that.</param>
    /// <param name="Id">The order sent again, or 0.</param>
    public readonly record struct Resent(Outcome Outcome, string Symbol, int Refused, double Past, double Lowest, int Id, IbkrOrderTicket Ticket, double Quote, string Problem);

    private sealed record Pressed(IbkrOrderTicket Ticket, long At, Func<double, (IbkrOrderTicket Ticket, double Quote, string Problem)> Decide, Action<Resent> OnResent);

    /// <summary>A sell key. Its order goes as decided for no lowest price (0); a refused one is decided again for IBKR's.</summary>
    /// <param name="onResent">What a refused press came to, said off IBKR's thread.</param>
    public (int Id, IbkrOrderTicket Ticket, double Quote, string Problem) Press(Func<double, (IbkrOrderTicket Ticket, double Quote, string Problem)> decide, Action<Resent> onResent)
    {
        // IBKR's answer waits for this too, so an order is always known here before its refusal is looked up.
        lock (this.placeGate)
        {
            var (ticket, quote, problem) = decide(0);
            if (problem != null)
                return (0, ticket, quote, problem);

            (int id, problem) = this.trader.Place(ticket);
            if (problem == null)
            {
                foreach (int old in this.pressed.Where(p => Stopwatch.GetElapsedTime(p.Value.At) > this.within).Select(p => p.Key).ToList())
                    this.pressed.Remove(old);
                this.pressed[id] = new Pressed(ticket, Stopwatch.GetTimestamp(), decide, onResent);
            }

            return (id, ticket, quote, problem);
        }
    }

    /// <summary>
    /// IBKR cancelled an order and gave this as its message (202). True when it's a press's order refused for its price,
    /// so it's being decided again and onResent will say what came of it; once for each press.
    /// </summary>
    public bool OnRefused(int orderId, string text)
    {
        Pressed was;
        lock (this.placeGate)
        {
            if (!this.pressed.Remove(orderId, out was))
                return false;
        }

        double past = Past(text);
        if (!(past >= was.Ticket.LimitPrice - 1e-9) || Stopwatch.GetElapsedTime(was.At) > this.within || this.trader.Order(orderId) is { Filled: > 0 })
            return false;

        _ = Task.Run(() => was.OnResent(this.Resend(orderId, was, past)));
        return true;
    }

    /// <summary>The price in IBKR's refusal that it won't take a limit at or past, or NaN in any other text. It breaks its lines with &lt;br&gt; anywhere.</summary>
    public static double Past(string text)
    {
        var said = Refusal().Match(LineBreak().Replace(text ?? "", " "));
        return said.Success && double.TryParse(said.Groups[1].Value, NumberStyles.AllowDecimalPoint | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out double past) && past > 0
            ? past
            : double.NaN;
    }

    /// <summary>The first price over one that IBKR won't take a sell at: the next tick, $0.01 from a dollar and $0.0001 below.</summary>
    public static double JustInside(double past)
    {
        decimal price = (decimal)past;
        decimal tick = price >= 1m ? 0.01m : 0.0001m;
        return (double)((Math.Floor(price / tick) + 1) * tick);
    }

    private Resent Resend(int refusedId, Pressed was, double past)
    {
        double lowest = JustInside(past);
        Resent Came(Outcome outcome, string problem, IbkrOrderTicket ticket = null, double quote = double.NaN, int id = 0) =>
            new(outcome, was.Ticket.Symbol, refusedId, past, lowest, id, ticket, quote, problem);

        try
        {
            // Its shares aren't free to sell until IBKR says it's done.
            bool done = this.trader.WaitDone(refusedId, DoneWait);
            lock (this.placeGate)
            {
                if (!done)
                    return Came(Outcome.NotSent, $"order {refusedId} isn't done yet");
                if (this.trader.Order(refusedId) is { Filled: > 0 } part)
                    return Came(Outcome.NotSent, $"{part.Filled:#,0} shares of order {refusedId} had filled");
                if (Stopwatch.GetElapsedTime(was.At) > this.within)
                    return Came(Outcome.NotSent, $"it's over {this.within.TotalSeconds:0.##} s since the press");

                var (ticket, quote, problem) = was.Decide(lowest);
                if (problem != null)
                    return Came(this.Left(was.Ticket.Symbol) ? Outcome.NotSent : Outcome.NothingLeft, problem, ticket, quote);
                if (ticket.Buy || ticket.Symbol != was.Ticket.Symbol)
                    return Came(Outcome.NotSent, $"it's not for {was.Ticket.Symbol} any more", ticket, quote);
                if (!(ticket.LimitPrice <= quote + 1e-9))
                    return Came(Outcome.AboveBid, null, ticket, quote);

                ticket = ticket with { Quantity = Math.Min(ticket.Quantity, was.Ticket.Quantity) };
                (int id, problem) = this.trader.Place(ticket);
                return Came(problem == null ? Outcome.Sent : Outcome.NotSent, problem, ticket, quote, id);
            }
        }
        catch (Exception ex)
        {
            return Came(Outcome.NotSent, $"{ex.GetType().Name} {ex.Message}");
        }
    }

    /// <summary>Whether any of the long position isn't in a working sell.</summary>
    private bool Left(string symbol)
    {
        double held = this.trader.Positions().Where(p => p.Symbol == symbol).Sum(p => p.Quantity);
        double selling = this.trader.OpenOrders().Where(o => o.Symbol == symbol && !o.Buy).Sum(o => Math.Max(0, o.Quantity - o.Filled));
        return held - selling >= 1;
    }

    [GeneratedRegex(@"limit\s+price\s+at\s+or\s+more\s+aggressive\s+than\s+([\d,]*\.?\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex Refusal();

    [GeneratedRegex(@"<br\s*/?>", RegexOptions.IgnoreCase)]
    private static partial Regex LineBreak();
}

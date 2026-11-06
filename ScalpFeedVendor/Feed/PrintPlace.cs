namespace ScalpFeedVendor.Feed;

/// <summary>Where a print hit the quote, which is what a tape's colors are for.</summary>
internal enum PrintPlace : byte { Unknown, AboveAsk, AtAsk, Between, AtBid, BelowBid }

internal static class PrintPlaces
{
    private const double Tolerance = 1e-9;

    /// <summary>Unknown if either side has no market: a halt, a one-sided book, or no quote yet.</summary>
    public static PrintPlace Of(double price, double bid, double ask)
    {
        if (!(bid > 0) || !(ask > 0))
            return PrintPlace.Unknown;

        if (price > ask + Tolerance)
            return PrintPlace.AboveAsk;
        if (price >= ask - Tolerance)
            return PrintPlace.AtAsk;
        if (price < bid - Tolerance)
            return PrintPlace.BelowBid;
        return price <= bid + Tolerance ? PrintPlace.AtBid : PrintPlace.Between;
    }
}

/// <summary>
/// Judges a print against the NBBO, allowing for that quote reaching us late. Taken alone it called 22%
/// of VSA's tastytrade prints outside the market on 2026-11-06, because the market had already moved.
/// So a print only counts as outside if it's beyond the quote it came with and every quote of the last
/// moment too, which brought that to 0.3%. Short of that it counts as at that side of the market.
/// </summary>
internal sealed class QuoteWindow
{
    private const double Tolerance = 1e-9;
    private static readonly long KeepTicks = TimeSpan.FromMilliseconds(800).Ticks;

    private readonly object gate = new();
    private readonly List<(long Ticks, double Bid, double Ask)> quotes = new();

    public void Add(double bid, double ask, long nowTicks)
    {
        if (!(bid > 0) || !(ask > 0))
            return;

        lock (this.gate)
        {
            // Each quote stands until the next, so the one just before the window still counts.
            while (this.quotes.Count >= 2 && this.quotes[1].Ticks < nowTicks - KeepTicks)
                this.quotes.RemoveAt(0);
            this.quotes.Add((nowTicks, bid, ask));
        }
    }

    /// <summary>The bid and ask are the NBBO now; the print's own quote is tastytrade's, or the same again for IBKR's rows.</summary>
    public PrintPlace Judge(double price, double bid, double ask, double ownBid, double ownAsk, long nowTicks)
    {
        var place = PrintPlaces.Of(price, bid, ask);
        if (place == PrintPlace.AboveAsk && (price <= ownAsk + Tolerance || this.AnyAskAtOrAbove(price, nowTicks)))
            return PrintPlace.AtAsk;
        if (place == PrintPlace.BelowBid && (price >= ownBid - Tolerance && ownBid > 0 || this.AnyBidAtOrBelow(price, nowTicks)))
            return PrintPlace.AtBid;
        return place;
    }

    private bool AnyAskAtOrAbove(double price, long nowTicks)
    {
        lock (this.gate)
        {
            for (int i = 0; i < this.quotes.Count; i++)
            {
                if (this.StoodInWindow(i, nowTicks) && this.quotes[i].Ask >= price - Tolerance)
                    return true;
            }

            return false;
        }
    }

    private bool AnyBidAtOrBelow(double price, long nowTicks)
    {
        lock (this.gate)
        {
            for (int i = 0; i < this.quotes.Count; i++)
            {
                if (this.StoodInWindow(i, nowTicks) && this.quotes[i].Bid <= price + Tolerance)
                    return true;
            }

            return false;
        }
    }

    private bool StoodInWindow(int i, long nowTicks) =>
        i == this.quotes.Count - 1 || this.quotes[i + 1].Ticks >= nowTicks - KeepTicks;
}

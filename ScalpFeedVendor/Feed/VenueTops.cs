namespace ScalpFeedVendor.Feed;

/// <summary>
/// The best bid and ask of the venues IBKR's smart depth leaves out for this account: ARCA, NYSE and
/// Nasdaq BX, which its message 2152 lists under "Need additional market data permissions". Each
/// one's own quote still comes through reqMktData with the exchange set, which is how TWS shows them,
/// so the depth gets one row a side for each. Measured 2026-11-09 on paper, CKX and SPY premarket:
/// ARCA's own quote matched the NBBO whenever IBKR named ARCA at the best, and on CKX ARCA was often
/// the only venue there, so without these rows the book's best was a level worse than the real one.
/// </summary>
internal static class VenueTops
{
    private const double SamePrice = 1e-9;

    /// <summary>IBKR's exchange names, which are also what the rows show as their venue.</summary>
    public static readonly string[] Venues = ["ARCA", "NYSE", "BEX"];

    /// <summary>
    /// The book with each venue's top row put in price order, after any rows already at that price.
    /// A venue with no market on that side (price -1 or 0, or no size) adds nothing, and nor does one
    /// the book already carries, in case the account gains that venue's full book later.
    /// </summary>
    public static void Merge(List<IbkrDepthRow> book, List<IbkrDepthRow> tops, bool bids, List<IbkrDepthRow> into)
    {
        into.Clear();
        into.AddRange(book);
        foreach (var top in tops)
        {
            if (!(top.Price > 0) || !(top.Size > 0) || HasVenue(book, top.Venue))
                continue;

            int at = 0;
            while (at < into.Count && (bids ? into[at].Price > top.Price - SamePrice : into[at].Price < top.Price + SamePrice))
                at++;
            into.Insert(at, top);
        }
    }

    /// <summary>
    /// What a depth row shows as its venue: IBKR's name, but EDGX for DRCTEDGE, its old name for that Cboe exchange
    /// (your request, 2026-11-24). Only for showing: everything here goes by IBKR's own names.
    /// </summary>
    public static string Label(string venue) => venue == "DRCTEDGE" ? "EDGX" : venue;

    private static bool HasVenue(List<IbkrDepthRow> rows, string venue)
    {
        foreach (var row in rows)
        {
            if (row.Venue == venue)
                return true;
        }

        return false;
    }
}

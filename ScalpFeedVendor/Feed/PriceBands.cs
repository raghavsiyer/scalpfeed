namespace ScalpFeedVendor.Feed;

/// <summary>
/// Sizes in the market depth panel, by your choice of 2026-11-06: in whole hundreds, so 2870 shares
/// shows as 28, and with rows of 100 shares or fewer left out unless they're at the best price, because
/// hiding those would show a wrong spread. Only depth rows use this. The quote, tape and bars are
/// unaffected.
/// </summary>
internal static class DepthSizes
{
    private const double MostSharesHidden = 100;
    private const double SamePrice = 1e-9;

    /// <param name="bestInBook">This side's best price among the depth rows.</param>
    /// <param name="quoted">This side's price on IBKR's quote, which can differ from the book for a moment.</param>
    public static bool Shows(double shares, double price, double bestInBook, double quoted) =>
        shares > 0 && (shares > MostSharesHidden || Math.Abs(price - bestInBook) < SamePrice || Math.Abs(price - quoted) < SamePrice);

    /// <summary>A shown row reads at least 1, since 85 shares at the best price mustn't read as nothing there.</summary>
    public static double ToHundreds(double shares) => Math.Max(1, Math.Floor(shares / 100));

    public static double Best(List<IbkrDepthRow> rows, bool bids)
    {
        double best = double.NaN;
        foreach (var row in rows)
        {
            if (double.IsNaN(best) || (bids ? row.Price > best : row.Price < best))
                best = row.Price;
        }

        return best;
    }
}

/// <summary>
/// The price bands Quantower gets for a symbol: 4 decimals under 10 cents (your choice of 2026-11-11),
/// 3 from there to a dollar, and 2 from a dollar up (2026-11-06). That's only how prices are written.
/// Tick sizes stay as tastytrade gives them, because they're what price steps and chart bucketing go
/// by, and prints and bars still carry their full prices, so 2.7188 shows as 2.72 but is still 2.7188
/// to everything that uses it.
/// </summary>
internal static class PriceBands
{
    private const double DollarLine = 1.0;

    // Where the number of decimals changes: prices below each line, up from the one before, get that many.
    private static readonly (double Below, int Decimals)[] DisplayLines = [(0.10, 4), (DollarLine, 3), (double.PositiveInfinity, 2)];

    public static List<(double Low, double High, double TickSize, int Decimals)> ForDisplay(IReadOnlyList<TickBand> bands)
    {
        // Stocks under $1 can trade in $0.0001 steps. Fall back to that shape if a lookup omits them.
        if (bands.Count == 0)
            bands = [new TickBand(DollarLine, 0.0001), new TickBand(double.PositiveInfinity, 0.01)];

        var result = new List<(double, double, double, int)>(bands.Count + DisplayLines.Length);
        double low = 0;
        foreach (var band in bands)
        {
            // A band across a line has to split, since its two sides are written differently.
            double from = low;
            foreach (var (below, decimals) in DisplayLines)
            {
                if (from >= band.Below)
                    break;
                if (below <= from)
                    continue;

                double to = Math.Min(below, band.Below);
                result.Add((from, to, band.Size, decimals));
                from = to;
            }

            low = band.Below;
        }

        return result;
    }
}

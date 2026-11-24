namespace ScalpFeedVendor.Feed;

/// <summary>
/// Sizes in the market depth panel, by your choice of 2026-11-06: in whole hundreds, so 2870 shares
/// shows as 28, and with rows of 100 shares or fewer left out unless they're at the best price, because
/// hiding those would show a wrong spread. Nasdaq's own rows (venue NSDQ) behind the best price are left
/// out up to 500 shares, 5 lots (your choice, 2026-11-18); its market makers (TSSM, MLCO and so on) keep
/// the 100. Only depth rows use this. The quote, tape and bars are unaffected.
/// </summary>
internal static class DepthSizes
{
    /// <summary>IBKR's smart depth names Nasdaq's own book NSDQ (SVRE's book, 2026-11-18).</summary>
    public const string NasdaqVenue = "NSDQ";

    private const double MostSharesHidden = 100;
    private const double MostNasdaqSharesHidden = 500;
    private const double SamePrice = 1e-9;

    /// <param name="bestInBook">This side's best price among the depth rows.</param>
    /// <param name="quoted">This side's price on IBKR's quote, which can differ from the book for a moment.</param>
    /// <param name="venue">The row's venue, which sets how many shares behind the best price are left out.</param>
    public static bool Shows(double shares, double price, double bestInBook, double quoted, string venue = null) =>
        shares > 0 && (shares > MostHidden(venue) || Math.Abs(price - bestInBook) < SamePrice || Math.Abs(price - quoted) < SamePrice);

    private static double MostHidden(string venue) => venue == NasdaqVenue ? MostNasdaqSharesHidden : MostSharesHidden;

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
/// 3 from there to a dollar, and 2 from a dollar up (2026-11-06). Each band's tick size is tastytrade's, unless
/// that's finer than the band's decimals can write: then it's the next tick they can (your choice, 2026-11-24), so
/// 10 cents to a dollar has 3 decimals and a $0.001 tick, not tastytrade's $0.0001.
/// Quantower steps a price one tick at a time and rounds each step to the band's decimals: with 3 decimals and a
/// $0.0001 tick, 0.7135 + 0.0001 rounds back to 0.714 and the step never advances, so Quantower recursed until its
/// stack overflowed and it closed (a chart on YFOR 11-19, the DOM on VIVS 11-20 and WETO 11-23, all between 10 cents
/// and a dollar; the trace names its VariableTick price stepping). From 11-23 to 11-24 that band had 4 decimals
/// instead, for the DOM ladder, which you've stopped using. A tick its decimals can write always advances.
/// The tick size is what Quantower's price steps and chart bucketing go by. Prints, quotes, depth and bars still
/// carry their full prices, so 2.7188 shows as 2.72 but is still 2.7188 to everything that uses it, and hotkey
/// orders round to IBKR's own tick.
/// </summary>
internal static class PriceBands
{
    private const double DollarLine = 1.0;

    // Where the number of decimals changes: prices below each line, up from the one before, get that many, and the
    // smallest step they can write.
    private static readonly (double Below, int Decimals, double Step)[] DisplayLines = [(0.10, 4, 0.0001), (DollarLine, 3, 0.001), (double.PositiveInfinity, 2, 0.01)];

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
            foreach (var (below, decimals, step) in DisplayLines)
            {
                if (from >= band.Below)
                    break;
                if (below <= from)
                    continue;

                double to = Math.Min(below, band.Below);
                double tick = DecimalsOf(band.Size) <= decimals ? band.Size : Math.Round(Math.Ceiling((band.Size / step) - 1e-9) * step, decimals);
                result.Add((from, to, tick, decimals));
                from = to;
            }

            low = band.Below;
        }

        return result;
    }

    /// <summary>How many decimals it takes to write this tick size exactly: 0.0001 and 0.0005 need 4, 0.01 needs 2.</summary>
    public static int DecimalsOf(double tickSize)
    {
        double scaled = tickSize;
        for (int decimals = 0; decimals < 10; decimals++, scaled *= 10)
        {
            if (Math.Abs(scaled - Math.Round(scaled)) < 1e-9 * Math.Max(1, scaled))
                return decimals;
        }

        return 10;
    }
}

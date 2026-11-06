namespace ScalpFeedVendor.Feed;

/// <summary>
/// Sizes in the market depth panel, by your choice of 2026-11-06: in whole hundreds, so 2870 shares
/// shows as 28, and with rows of 100 shares or fewer left out. That can leave out the best bid or ask
/// itself. Only depth rows use this. The quote, tape and bars are unaffected.
/// </summary>
internal static class DepthSizes
{
    private const double MostSharesHidden = 100;

    public static bool Shows(double shares) => shares > MostSharesHidden;

    public static double ToHundreds(double shares) => Math.Floor(shares / 100);
}

/// <summary>
/// The price bands Quantower gets for a symbol: 3 decimals under a dollar and 2 from a dollar up, by
/// your choice of 2026-11-06. That's only how prices are written. Tick sizes stay as tastytrade gives
/// them, because they're what price steps and chart bucketing go by, and prints and bars still carry
/// their full prices, so 2.7188 shows as 2.72 but is still 2.7188 to everything that uses it.
/// </summary>
internal static class PriceBands
{
    private const double DollarLine = 1.0;
    private const int DecimalsUnderDollar = 3;
    private const int DecimalsFromDollar = 2;

    public static List<(double Low, double High, double TickSize, int Decimals)> ForDisplay(IReadOnlyList<TickBand> bands)
    {
        // Stocks under $1 can trade in $0.0001 steps. Fall back to that shape if a lookup omits them.
        if (bands.Count == 0)
            bands = [new TickBand(DollarLine, 0.0001), new TickBand(double.PositiveInfinity, 0.01)];

        var result = new List<(double, double, double, int)>(bands.Count + 1);
        double low = 0;
        foreach (var band in bands)
        {
            // A band across the dollar line has to split, since its two sides are written differently.
            if (low < DollarLine && band.Below > DollarLine)
            {
                result.Add((low, DollarLine, band.Size, DecimalsUnderDollar));
                result.Add((DollarLine, band.Below, band.Size, DecimalsFromDollar));
            }
            else
            {
                result.Add((low, band.Below, band.Size, band.Below <= DollarLine ? DecimalsUnderDollar : DecimalsFromDollar));
            }

            low = band.Below;
        }

        return result;
    }
}

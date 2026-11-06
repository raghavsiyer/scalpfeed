namespace ScalpFeedVendor.Feed;

/// <summary>
/// Which prints go on the tape. tastytrade marks odd lots invalid, and they're most of a stock's
/// prints by count (AEHL: 84% of IBKR's tape was under 100 shares), so they stay. The other invalid
/// prints (average price, contingent, derivatively priced) can be far from the market and don't.
/// Odd lots do reach chart bars as well, and history leaves them out, so live bars show more
/// volume and sometimes a wider high or low until a reload.
/// </summary>
internal static class PrintFilter
{
    // Beyond the bid and ask carried on the print. In the 2026-11-05 recordings the one print past
    // 2% was a 1-share SPY odd lot 4.9% out. AEHL's furthest was 1.89%, in a fast move, and its
    // valid prints reach past 1% too, so a tighter guard would hide real trades in momentum names.
    private const double MaxBeyondQuote = 0.03;

    public static bool Shows(FeedRow row, double lastPrice) =>
        row.ValidTick || (row.NormalOddLot && IsBelievable(row.Price, row.Bid, row.Ask, lastPrice));

    /// <summary>Settlement, reason, extended-hours and detail codes, one character each.</summary>
    public static bool IsNormalOddLot(ReadOnlySpan<byte> conditions) =>
        conditions.Length == 4
        && conditions[0] == '@'
        && (conditions[1] == ' ' || conditions[1] == 'F')
        && (conditions[2] == ' ' || conditions[2] == 'T')
        && conditions[3] == 'o';

    /// <summary>
    /// IBKR venues whose prints already come from tastytrade, and sooner. Merging only the other
    /// venues means the two feeds never overlap, so nothing has to be matched print by print.
    /// Overnight is here until a session shows tastytrade's overnight prints are a different set.
    /// </summary>
    public static bool IsCarriedByTastytrade(ReadOnlySpan<byte> venue) =>
        venue.SequenceEqual("NASDAQ"u8)
        || venue.SequenceEqual("BEX"u8)
        || venue.SequenceEqual("PSX"u8)
        || venue.SequenceEqual("FINRA"u8)
        || venue.SequenceEqual("OVERNIGHT"u8);

    /// <summary>
    /// IBKR's condition codes for what tastytrade marks invalid and PrintFilter hides, plus special
    /// settlement and out-of-sequence prints. Codes are one character each, with or without spaces
    /// between them (premarket shows TI and FTI). Odd lots (I), sweeps (F) and extended hours (T)
    /// are ordinary trades and stay.
    /// </summary>
    public static bool IsOffMarket(ReadOnlySpan<byte> conditions)
    {
        foreach (byte code in conditions)
        {
            if (code is (byte)'4' or (byte)'7' or (byte)'W' or (byte)'V' or (byte)'B' or (byte)'P'
                or (byte)'C' or (byte)'N' or (byte)'R' or (byte)'U' or (byte)'Z')
                return true;
        }

        return false;
    }

    public static bool IsBelievable(double price, double bid, double ask, double lastPrice)
    {
        // With nothing to compare against, a print that may be bad stays off the chart.
        double low = bid > 0 ? bid : lastPrice;
        double high = ask > 0 ? ask : lastPrice;
        return low > 0 && high > 0 && price >= low * (1 - MaxBeyondQuote) && price <= high * (1 + MaxBeyondQuote);
    }
}

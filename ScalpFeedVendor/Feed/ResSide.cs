namespace ScalpFeedVendor.Feed;

/// <summary>
/// Which side of the depth a halt's Res row goes on (your choice, 2026-11-17): among the asks for a halt up and
/// the bids for a halt down, and on both sides, as before, while which way isn't known. A way is 1 up, -1 down,
/// 0 not known.
///
/// Which way is the last trade against the LULD band's reference price, asked as the halt begins, before the
/// halt stops the band (FromBand). A pause at the upper band is always above the reference and one at the lower
/// band below it, by the band's whole width (5% to 20%, or $0.15), so the rough band of a symbol picked up
/// mid-session (up to about 1% off) still tells. When the band can't say (the halt began before the symbol was
/// opened, or outside 9:30-16:00, where only regulatory halts happen), the reopening auction's price against the
/// last trade says which way the reopening points instead (FromAuction), decided once and kept for the halt.
/// </summary>
internal static class ResSide
{
    public const int Up = 1;
    public const int Down = -1;
    public const int Unknown = 0;

    /// <summary>Asked as a halt begins: the last trade above the band's recent reference is up, below it down.</summary>
    public static int FromBand(LuldBand band, double lastTrade, long utcTicks) =>
        lastTrade > 0 && band.TryGetRecentReference(utcTicks, out double reference) ? Way(lastTrade - reference) : Unknown;

    /// <summary>For a halt whose start the band didn't see: the auction above the last trade is up, below it down.</summary>
    public static int FromAuction(double auctionPrice, double lastTrade) =>
        auctionPrice > 0 && lastTrade > 0 ? Way(auctionPrice - lastTrade) : Unknown;

    public static bool OnBids(int way) => way != Up;

    public static bool OnAsks(int way) => way != Down;

    public static string Word(int way) => way == Up ? "up" : way == Down ? "down" : "neither way";

    public static string Side(int way) => way == Up ? "asks" : way == Down ? "bids" : "bids and asks";

    // IBKR rounds the auction price to 4 places, so a hair's difference is none.
    private static int Way(double difference)
    {
        double d = Math.Round(difference, 6);
        return d > 0 ? Up : d < 0 ? Down : Unknown;
    }
}

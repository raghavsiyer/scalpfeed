namespace ScalpFeedIndicators;

/// <summary>
/// VWAP that starts over at a fixed New York time each day: the sum of each bar's typical price
/// (high + low + close) / 3 times its volume, over the sum of volume, from the session's first bar.
/// Quantower's own daily VWAP restarted at midnight UTC (19:00 ET the evening before) even with the
/// terminal on Pacific time, so it carried the previous evening's after-hours into each day
/// (seen on NXXT 2026-11-11). This depends on nothing in Quantower, so the harness can test it.
/// </summary>
public sealed class SessionVwap
{
    private static readonly TimeZoneInfo Eastern = TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");

    private readonly TimeSpan start;
    private readonly Dictionary<DateTime, (double PriceVolume, double Volume, DateTime Session)> byBar = new();

    /// <param name="sessionStartEt">When each day's VWAP starts, New York time: 4:00 for premarket, 9:30 for the open.</param>
    public SessionVwap(TimeSpan sessionStartEt) => this.start = sessionStartEt;

    /// <summary>Which session a bar belongs to: the New York date its start time falls in, counted from the session start.</summary>
    public DateTime SessionOf(DateTime barStartUtc) =>
        (TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(barStartUtc, DateTimeKind.Utc), Eastern) - this.start).Date;

    /// <summary>
    /// Takes the bar starting at barStartUtc, again each time it changes, and returns the VWAP through it,
    /// or NaN while the session has had no volume. previousBarUtc is the bar before it, or default for the first.
    /// </summary>
    public double Update(DateTime barStartUtc, DateTime previousBarUtc, double high, double low, double close, double volume)
    {
        var session = this.SessionOf(barStartUtc);
        double priceVolume = 0;
        double totalVolume = 0;
        if (previousBarUtc != default && this.byBar.TryGetValue(previousBarUtc, out var before) && before.Session == session)
        {
            priceVolume = before.PriceVolume;
            totalVolume = before.Volume;
        }

        if (volume > 0 && high > 0 && low > 0 && close > 0)
        {
            priceVolume += (high + low + close) / 3 * volume;
            totalVolume += volume;
        }

        this.byBar[barStartUtc] = (priceVolume, totalVolume, session);
        return totalVolume > 0 ? priceVolume / totalVolume : double.NaN;
    }

    public void Clear() => this.byBar.Clear();
}

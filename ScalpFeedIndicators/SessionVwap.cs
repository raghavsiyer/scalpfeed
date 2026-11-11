namespace ScalpFeedIndicators;

/// <summary>
/// VWAP that starts over at set New York times: the sum of each bar's typical price (high + low + close) / 3
/// times its volume, over the sum of volume, from the session's first bar. With 4:00, 9:30 and 16:00 ET it's
/// a VWAP per trading session, premarket, regular hours and after hours each on its own, which is what the
/// user's reference shows (NXXT 2026-11-11 at about 18:15 ET: 4.62 from 16:00, against 2.28 from 4:00).
/// Overnight bars belong to the after-hours session before them. Quantower's own daily VWAP restarted at
/// midnight UTC (19:00 ET the evening before) even with the terminal on Pacific time (seen on NXXT
/// 2026-11-11). This depends on nothing in Quantower, so the harness can test it.
/// </summary>
public sealed class SessionVwap
{
    private static readonly TimeZoneInfo Eastern = TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");

    private readonly TimeSpan[] starts;
    private readonly Dictionary<DateTime, (double PriceVolume, double Volume, DateTime Session, bool BeginsLine)> byBar = new();

    /// <param name="sessionStartsEt">When each session's VWAP starts, New York time, e.g. 4:00, 9:30 and 16:00.</param>
    public SessionVwap(params TimeSpan[] sessionStartsEt)
    {
        if (sessionStartsEt == null || sessionStartsEt.Length == 0)
            throw new ArgumentException("At least one session start is needed.", nameof(sessionStartsEt));
        this.starts = sessionStartsEt.Distinct().OrderBy(s => s).ToArray();
    }

    /// <summary>
    /// Which session a bar belongs to, as that session's start in New York time: the latest start at or
    /// before the bar's start, today's or, before the day's first start, the last one of the day before.
    /// </summary>
    public DateTime SessionOf(DateTime barStartUtc)
    {
        var et = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(barStartUtc, DateTimeKind.Utc), Eastern);
        for (int i = this.starts.Length - 1; i >= 0; i--)
        {
            if (et.TimeOfDay >= this.starts[i])
                return et.Date + this.starts[i];
        }

        return et.Date.AddDays(-1) + this.starts[^1];
    }

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

        bool hadValue = totalVolume > 0;
        if (volume > 0 && high > 0 && low > 0 && close > 0)
        {
            priceVolume += (high + low + close) / 3 * volume;
            totalVolume += volume;
        }

        this.byBar[barStartUtc] = (priceVolume, totalVolume, session, !hadValue && totalVolume > 0);
        return totalVolume > 0 ? priceVolume / totalVolume : double.NaN;
    }

    /// <summary>
    /// True for the first bar of its session that has a VWAP: where that session's line begins, and where the
    /// chart should not join it to the session before. Bars with no volume at the start of a session (a halt
    /// at the open, a quiet premarket) have no VWAP, so the first bar with some is the one that counts.
    /// </summary>
    public bool BeginsLine(DateTime barStartUtc) => this.byBar.TryGetValue(barStartUtc, out var bar) && bar.BeginsLine;

    public void Clear() => this.byBar.Clear();
}

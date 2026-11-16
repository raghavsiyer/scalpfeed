namespace ScalpFeedIndicators;

/// <summary>
/// RVOL's arithmetic, apart from Quantower so the harness can test it: today's volume over the average daily
/// volume of the 50 sessions before today (your definition, 2026-11-15). The daily bars carry whole-market
/// volume, split-adjusted (FGL's days before its 1:100 reverse split read a hundredth, with fractions), and are
/// stamped with their session date at 00:00 UTC.
/// </summary>
internal static class RelativeVolumeMath
{
    public const int Days = 50;

    /// <summary>Fewer sessions than this (a new listing) and there's no average worth dividing by.</summary>
    public const int MinDays = 5;

    private static readonly TimeZoneInfo Eastern = TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");

    public static DateTime TodayEt(DateTime utc) => TimeZoneInfo.ConvertTimeFromUtc(utc, Eastern).Date;

    /// <summary>The average volume of the last 50 sessions before today, or NaN with fewer than 5.</summary>
    public static double Average(IEnumerable<(DateTime Day, double Volume)> sessions, DateTime today)
    {
        var before = sessions.Where(s => s.Day.Date < today && double.IsFinite(s.Volume))
            .OrderByDescending(s => s.Day)
            .Take(Days)
            .ToList();
        return before.Count >= MinDays ? before.Average(s => s.Volume) : double.NaN;
    }

    public static double Ratio(double todayVolume, double average) =>
        average > 0 && todayVolume >= 0 ? todayVolume / average : double.NaN;
}

namespace ScalpFeedVendor.Feed;

/// <summary>
/// The close the day's change is measured from. Premarket and the regular session measure from the previous
/// day's close. After the close, from 16:00 to 4:00 ET and over the weekend, they measure from the last session's
/// own close, as IBKR's after-hours gainers scan does (your choice, 2026-11-16). FCUV that evening was up 24% from
/// its $4.72 close and #2 on the after-hours list, while the Watchlist said -19%: against Friday's $7.08, after a
/// 33% drop in the session.
/// </summary>
internal static class DayChange
{
    private static readonly TimeZoneInfo Eastern = TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");

    /// <summary>
    /// Today's close when the session is over and it's known, else the previous close. dxFeed's Summary carries
    /// both: dayClosePrice and prevDayClosePrice, rolling to the new day overnight, when the day's close is unknown
    /// again and the previous close is the one wanted.
    /// </summary>
    public static double Base(DateTime utc, double prevClose, double dayClose) =>
        AfterClose(utc) && dayClose > 0 ? dayClose : prevClose;

    public static bool AfterClose(DateTime utc)
    {
        var et = TimeZoneInfo.ConvertTimeFromUtc(utc, Eastern);
        return et.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday || et.TimeOfDay >= TimeSpan.FromHours(16) || et.TimeOfDay < TimeSpan.FromHours(4);
    }
}

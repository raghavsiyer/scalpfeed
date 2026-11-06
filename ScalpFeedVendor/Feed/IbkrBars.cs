using System.Globalization;

namespace ScalpFeedVendor.Feed;

/// <summary>
/// Chart bars from IBKR, which count every venue. tastytrade's intraday candles count only Nasdaq
/// Last Sale's valid prints, about half the market. IBKR's are the whole tape without odd lots: 98 of
/// 98 bars matched to the share on 2026-11-06. It has no bars from 20:00 to 04:00 ET, so tastytrade's
/// stay underneath for those hours and for anything IBKR can't supply.
/// </summary>
internal static class IbkrBars
{
    /// <summary>IBKR's name for a bar length, or null if chart bars of that length stay on tastytrade.</summary>
    public static string BarSize(int seconds) => seconds switch
    {
        1 => "1 secs",
        5 => "5 secs",
        10 => "10 secs",
        15 => "15 secs",
        30 => "30 secs",
        60 => "1 min",
        300 => "5 mins",
        900 => "15 mins",
        1800 => "30 mins",
        3600 => "1 hour",
        _ => null
    };

    /// <summary>
    /// The longest span one request gets. Measured 2026-11-06: 1 s refuses 3,600 S, and 10 s took 4.9 s
    /// for 28,800 S against 0.3 s for 14,400 S. Anything older shows tastytrade's bars.
    /// </summary>
    public static long MaxSeconds(int barSeconds) => barSeconds switch
    {
        1 => 1800,
        5 => 7200,
        10 or 15 => 14400,
        30 => 2 * 86400,
        _ => 30 * 86400
    };

    /// <summary>Whole minutes up to a day, then whole days, so a repeat of a request is identical.</summary>
    public static string Duration(long seconds)
    {
        if (seconds <= 86400)
            return ((Math.Max(seconds, 1) + 59) / 60 * 60).ToString(CultureInfo.InvariantCulture) + " S";

        return ((seconds + 86399) / 86400).ToString(CultureInfo.InvariantCulture) + " D";
    }

    /// <summary>Blank means now, which is what nearly every chart asks for.</summary>
    public static string EndTime(DateTime toUtc, DateTime nowUtc) =>
        nowUtc - toUtc < TimeSpan.FromMinutes(1) ? string.Empty : toUtc.ToString("yyyyMMdd-HH:mm:ss", CultureInfo.InvariantCulture);

    /// <summary>
    /// IBKR's bars replace tastytrade's at the same time, or join them. Quiet times come from IBKR as
    /// empty bars. Those don't show, and they clear a tastytrade bar there as well: on the shortest
    /// bars the two can put one print in neighbouring seconds, and it mustn't count twice.
    /// </summary>
    public static List<BarRow> Overlay(IEnumerable<BarRow> tastytrade, IEnumerable<BarRow> ibkr)
    {
        var byTime = new Dictionary<long, BarRow>();
        foreach (var bar in tastytrade)
            byTime[bar.TimeMs] = bar;

        foreach (var bar in ibkr)
        {
            if (bar.Volume > 0)
                byTime[bar.TimeMs] = bar;
            else
                byTime.Remove(bar.TimeMs);
        }

        var merged = byTime.Values.ToList();
        merged.Sort((a, b) => a.TimeMs.CompareTo(b.TimeMs));
        return merged;
    }
}

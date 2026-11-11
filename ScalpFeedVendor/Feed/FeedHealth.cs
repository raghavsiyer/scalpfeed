using System.Diagnostics;

namespace ScalpFeedVendor.Feed;

internal enum HealthChange { None, WentStale, Recovered, NeedsReconnect }

/// <summary>
/// Decides whether a feed has stalled: no data of any kind (a quote, print, trade or depth change on any
/// symbol) for longer than the session allows. SPY stays subscribed so there's always some traffic, but
/// it isn't watched on its own: after the close on 2026-11-11 SPY's quote sat unchanged for 20 s again
/// and again while thousands of prints a minute were arriving, and each time both feeds were called
/// stale, an alarm sounded and IBKR reconnected for nothing.
/// </summary>
internal sealed class FeedHealth
{
    /// <summary>Always subscribed, so a feed has traffic even when no chart is open.</summary>
    public const string CanarySymbol = "SPY";

    private const int EscalationFactor = 3;
    private static readonly TimeZoneInfo Eastern = TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");

    private long lastDataStamp = Stopwatch.GetTimestamp();
    private long dataSeen;
    private bool stale;
    private bool escalated;

    /// <summary>Replaces the session-based limit. Test builds only.</summary>
    public TimeSpan? LimitOverride { get; set; }

    /// <summary>How long since the feed last delivered anything.</summary>
    public TimeSpan Silence(long nowStamp) =>
        TimeSpan.FromSeconds((nowStamp - Volatile.Read(ref this.lastDataStamp)) / (double)Stopwatch.Frequency);

    /// <summary>Data seen, which unlike the silence isn't changed by a reconnect's reset.</summary>
    public long DataSeen => Volatile.Read(ref this.dataSeen);

    /// <summary>Called on the receive thread for every quote, print, trade or depth change, on any symbol.</summary>
    public void OnData(long stamp)
    {
        Volatile.Write(ref this.lastDataStamp, stamp);
        Volatile.Write(ref this.dataSeen, this.dataSeen + 1);
    }

    public void Reset()
    {
        Volatile.Write(ref this.lastDataStamp, Stopwatch.GetTimestamp());
        this.stale = false;
        this.escalated = false;
    }

    /// <summary>The longest silence that's normal right now; null when markets are shut.</summary>
    public static TimeSpan? SilenceLimit(DateTime utcNow)
    {
        var et = TimeZoneInfo.ConvertTimeFromUtc(utcNow, Eastern);
        var time = et.TimeOfDay;

        bool shut = et.DayOfWeek == DayOfWeek.Saturday
            || (et.DayOfWeek == DayOfWeek.Friday && time >= TimeSpan.FromHours(20))
            || (et.DayOfWeek == DayOfWeek.Sunday && time < TimeSpan.FromHours(20));
        if (shut)
            return null;

        if (time >= new TimeSpan(9, 30, 0) && time < TimeSpan.FromHours(16))
            return TimeSpan.FromSeconds(5);
        if (time >= TimeSpan.FromHours(4) && time < new TimeSpan(9, 30, 0))
            return TimeSpan.FromSeconds(15);
        if (time >= TimeSpan.FromHours(16) && time < TimeSpan.FromHours(20))
            return TimeSpan.FromSeconds(60);
        return TimeSpan.FromSeconds(120);
    }

    /// <summary>Called once a second off the data path. At most one change per call.</summary>
    public HealthChange Evaluate(DateTime utcNow, long nowStamp)
    {
        var limit = this.LimitOverride ?? SilenceLimit(utcNow);
        var age = this.Silence(nowStamp);

        if (limit == null || age <= limit)
        {
            if (!this.stale)
                return HealthChange.None;

            this.stale = false;
            this.escalated = false;
            return HealthChange.Recovered;
        }

        if (!this.stale)
        {
            this.stale = true;
            return HealthChange.WentStale;
        }

        if (!this.escalated && age > limit * EscalationFactor)
        {
            this.escalated = true;
            return HealthChange.NeedsReconnect;
        }

        return HealthChange.None;
    }
}

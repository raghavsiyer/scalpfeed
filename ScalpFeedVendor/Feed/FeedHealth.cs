using System.Diagnostics;

namespace ScalpFeedVendor.Feed;

internal enum HealthChange { None, WentStale, Recovered, NeedsReconnect }

/// <summary>
/// Decides whether the feed has stalled. Thin stocks can sit quiet for minutes, so silence
/// on one symbol proves nothing. Instead a liquid canary symbol is always subscribed, and
/// the limit depends on the session: SPY quotes constantly in regular hours but can go
/// quiet for seconds overnight.
/// </summary>
internal sealed class FeedHealth
{
    public const string CanarySymbol = "SPY";

    private const int EscalationFactor = 3;
    private static readonly TimeZoneInfo Eastern = TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");

    private long lastCanaryStamp = Stopwatch.GetTimestamp();
    private long canaryQuotes;
    private bool stale;
    private bool escalated;

    /// <summary>Replaces the session-based limit. Test builds only.</summary>
    public TimeSpan? LimitOverride { get; set; }

    public TimeSpan CanaryAge(long nowStamp) =>
        TimeSpan.FromSeconds((nowStamp - Volatile.Read(ref this.lastCanaryStamp)) / (double)Stopwatch.Frequency);

    /// <summary>Real quotes seen, which unlike the age isn't changed by a reconnect's reset.</summary>
    public long CanaryQuotes => Volatile.Read(ref this.canaryQuotes);

    /// <summary>Called on the receive thread for every canary quote.</summary>
    public void OnCanaryQuote(long stamp)
    {
        Volatile.Write(ref this.lastCanaryStamp, stamp);
        Volatile.Write(ref this.canaryQuotes, this.canaryQuotes + 1);
    }

    public void Reset()
    {
        Volatile.Write(ref this.lastCanaryStamp, Stopwatch.GetTimestamp());
        this.stale = false;
        this.escalated = false;
    }

    /// <summary>The longest canary silence that's normal right now; null when markets are shut.</summary>
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
            return TimeSpan.FromSeconds(20);
        return TimeSpan.FromSeconds(120);
    }

    /// <summary>Called once a second off the data path. At most one change per call.</summary>
    public HealthChange Evaluate(DateTime utcNow, long nowStamp)
    {
        var limit = this.LimitOverride ?? SilenceLimit(utcNow);
        var age = this.CanaryAge(nowStamp);

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

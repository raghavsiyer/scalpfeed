namespace ScalpFeedVendor.Feed;

/// <summary>
/// The time stamped on what the plugin sends Quantower, and on arrivals compared with exchange times.
/// Quantower corrects the PC clock by internet time and measures latency with the corrected time, while
/// the PC clock wanders: 0.24-0.66 s slow on 2026-11-11 and 1.5 s fast on 11-09. Stamped with the PC
/// clock, every IBKR print looked that much late in Quantower. The vendor points this at Quantower's clock;
/// the harness and the probes keep the PC clock. Timers that only measure intervals stay on DateTime.UtcNow
/// or Stopwatch, since Quantower's correction jumps when it re-syncs (by 411 ms at 05:26 on 11-11).
/// </summary>
internal static class FeedClock
{
    // Quantower's correction has been under a second. Anything far beyond that is a clock not yet set.
    private static readonly long MostCorrectionTicks = TimeSpan.FromMinutes(5).Ticks;

    private static Func<DateTime> source;

    public static long UtcNowTicks
    {
        get
        {
            long pc = DateTime.UtcNow.Ticks;
            var current = Volatile.Read(ref source);
            if (current == null)
                return pc;

            try
            {
                long corrected = current().Ticks;
                return Math.Abs(corrected - pc) <= MostCorrectionTicks ? corrected : pc;
            }
            catch (Exception)
            {
                // Whatever went wrong with the other clock, the PC's is still a time.
                return pc;
            }
        }
    }

    public static DateTime UtcNow => new(UtcNowTicks, DateTimeKind.Utc);

    /// <summary>How far this clock is ahead of the PC's, in milliseconds.</summary>
    public static double AheadOfPcMs => (UtcNowTicks - DateTime.UtcNow.Ticks) / (double)TimeSpan.TicksPerMillisecond;

    /// <summary>Null goes back to the PC clock.</summary>
    public static void Use(Func<DateTime> clock) => Volatile.Write(ref source, clock);
}

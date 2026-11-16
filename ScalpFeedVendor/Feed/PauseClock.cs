using System.Collections.Concurrent;
using System.Diagnostics;

namespace ScalpFeedVendor.Feed;

/// <summary>
/// When each symbol's LULD pause began, and the seconds left until its reopening, which the depth's Res row
/// shows as its size. A pause is 5 minutes, and when the listing market can't reopen then it extends by 5
/// minutes at a time, so the reopening is due at the next 5-minute mark from the start. Seen on 2026-11-11:
/// NXXT reopened 300.2, 599.9 (one extension) and 299.9 s after IBKR's Halted tick said 2, and WHLR 300.1 s.
/// The start is IBKR's tick arriving, so it can be a fraction of a second late.
///
/// A start is only known when the symbol was seen trading first on the same IBKR stream. One already paused when
/// first seen (opened mid-pause) has none, and nor has a regulatory halt (Halted 1), which has no set reopening
/// time. A cancelled stream forgets its start, since the reopening could pass unwatched. Starts are kept here
/// rather than per vendor, so Quantower's reconnects, which build new symbol states that first see the pause
/// as new, don't lose them, and nor do IBKR reconnects, which are usually seconds.
/// </summary>
internal sealed class PauseClock
{
    public const int PauseSeconds = 300;

    /// <summary>The one the plugin uses; tests make their own.</summary>
    public static readonly PauseClock Shared = new();

    // A start older than this, met again with nothing in between, is from a reopening the plugin didn't see.
    private static readonly long StaleTicks = Stopwatch.Frequency * 3600;

    // Stopwatch stamps, by IBKR symbol.
    private readonly ConcurrentDictionary<string, long> starts = new();

    /// <summary>IBKR's Halted value went from before (-1 when not known yet) to now, at stamp.</summary>
    public void OnHalted(string symbol, int before, int now, long stamp)
    {
        if (now == 2)
        {
            if (before == 0)
                this.starts[symbol] = stamp;
            else if (this.starts.TryGetValue(symbol, out long start) && stamp - start > StaleTicks)
                this.starts.TryRemove(symbol, out _);

            // Otherwise a pause first seen now keeps a start from before a reconnect, or has none.
            return;
        }

        this.starts.TryRemove(symbol, out _);
    }

    /// <summary>IBKR stopped streaming the symbol, so its reopening could pass unseen and the start mean nothing.</summary>
    public void Forget(string symbol) => this.starts.TryRemove(symbol, out _);

    /// <summary>Seconds until the reopening is due, 300 down to 1, or null when the pause's start isn't known.</summary>
    public int? SecondsLeft(string symbol, long stamp) =>
        this.starts.TryGetValue(symbol, out long start) ? Left((double)(stamp - start) / Stopwatch.Frequency) : null;

    /// <summary>
    /// What the Res row reads: the seconds left times 1000, so 300,000 counting down to 1,000 (your choice of
    /// 2026-11-13), or the given size when the reopening time isn't known.
    /// </summary>
    public static double RowSize(int? secondsLeft, double unknown) => secondsLeft is int s ? s * 1000.0 : unknown;

    /// <summary>Seconds to the next 5-minute mark after elapsed seconds: 300 at the start, 1 in a pause's last second.</summary>
    public static int Left(double elapsedSeconds)
    {
        double into = Math.Max(0, elapsedSeconds) % PauseSeconds;
        return Math.Max(1, (int)Math.Ceiling(PauseSeconds - into));
    }
}

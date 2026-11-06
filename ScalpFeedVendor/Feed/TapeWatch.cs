namespace ScalpFeedVendor.Feed;

/// <summary>
/// Notices one symbol's IBKR tape going quiet while its quotes carry on, which nothing else can
/// see. tastytrade's daytime prints all reach IBKR's tape too, so one that isn't followed by any
/// IBKR print on that symbol means the tape has stopped, and the symbol goes back to tastytrade's
/// tape until IBKR prints again. The prints in those few seconds are lost, and a few can show
/// twice if IBKR was only slow.
/// </summary>
internal sealed class TapeWatch
{
    /// <summary>Longer than IBKR's slowest ordinary delivery. See IBKR-SEAMS.md for how it was measured.</summary>
    public static readonly long LimitTicks = TimeSpan.FromSeconds(3).Ticks;

    private long waitingSinceTicks;

    /// <summary>On tastytrade's receive thread: a print IBKR's tape should follow. The first one starts the clock.</summary>
    public void OnExpectedPrint(long arrivalUtcTicks) => Interlocked.CompareExchange(ref this.waitingSinceTicks, arrivalUtcTicks, 0);

    /// <summary>On IBKR's receive thread: any print at all, shown or not.</summary>
    public void OnIbkrPrint() => Volatile.Write(ref this.waitingSinceTicks, 0);

    /// <summary>Called once a second. Says so once per silence, then starts afresh.</summary>
    public bool TakeWentQuiet(long nowUtcTicks)
    {
        long since = Volatile.Read(ref this.waitingSinceTicks);
        if (since == 0 || nowUtcTicks - since <= LimitTicks)
            return false;

        Interlocked.CompareExchange(ref this.waitingSinceTicks, 0, since);
        return true;
    }
}

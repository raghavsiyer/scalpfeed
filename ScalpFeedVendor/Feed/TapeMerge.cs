namespace ScalpFeedVendor.Feed;

/// <summary>
/// Puts tastytrade's tape and IBKR's together so that each trade shows once. tastytrade's tape is
/// Nasdaq Last Sale: trades on Nasdaq's own markets, plus those reported to the FINRA/Nasdaq TRF.
/// IBKR's is the whole consolidated tape. That fixes what each IBKR print can be:
/// <list type="bullet">
/// <item>NASDAQ, BEX, PSX: always on tastytrade's tape too, so never shown from IBKR.</item>
/// <item>Any other exchange: never on tastytrade's tape, so always shown from IBKR.</item>
/// <item>FINRA: IBKR's name for every TRF, and only some of them are on tastytrade's tape. Matching
/// the print is the only way to tell, so it shows unless tastytrade has the same print.</item>
/// </list>
/// Whichever feed brings a FINRA print first shows it. tastytrade also has fractional trades that
/// IBKR's tape leaves out: 2.67 shares arrive as 2 and 0.67, and IBKR has the 2. A print under one
/// share has no partner, so it simply shows, which is right because it did trade.
/// </summary>
internal sealed class TapeMerge
{
    // The two feeds can round a sub-penny price differently (0.0591 and 0.059).
    private const double PriceTolerance = 0.00011;

    // Both feeds stamp the trade itself, so they rarely disagree by more than a second.
    private const long SecondsTolerance = 3;

    // Longer than the slowest IBKR delivery measured, 3.2 s behind tastytrade.
    private static readonly long KeepTicks = TimeSpan.FromSeconds(6).Ticks;

    // tastytrade is the faster feed and IBKR only ever leads it briefly. Holding IBKR's print any
    // longer lets a later trade at the same price and size on tastytrade be taken for it, and be lost.
    private static readonly long KeepIbkrFirstTicks = TimeSpan.FromSeconds(1).Ticks;

    private readonly object gate = new();
    private readonly List<Entry> fromTastytrade = new();
    private readonly List<Entry> fromIbkr = new();
    private readonly List<Entry> carriedFirst = new();

    private readonly record struct Entry(double Price, double Size, long Seconds, long AddedTicks);

    /// <summary>Returns whether to show it. No, if IBKR has already shown this print.</summary>
    public bool OnTastytradePrint(double price, double size, long printSeconds, long nowTicks)
    {
        lock (this.gate)
        {
            Expire(this.fromTastytrade, nowTicks);
            Expire(this.fromIbkr, nowTicks, KeepIbkrFirstTicks);
            Expire(this.carriedFirst, nowTicks, KeepIbkrFirstTicks);
            if (TakeMatch(this.fromIbkr, price, size, printSeconds))
                return false;

            // IBKR's copy has been and gone, so nothing further can claim this print.
            if (!TakeMatch(this.carriedFirst, price, size, printSeconds))
                this.fromTastytrade.Add(new Entry(price, size, printSeconds, nowTicks));
            return true;
        }
    }

    /// <summary>A FINRA print. Returns whether to show it. No, if tastytrade has already shown it.</summary>
    public bool OnIbkrReportedPrint(double price, double size, long printSeconds, long nowTicks)
    {
        lock (this.gate)
        {
            Expire(this.fromTastytrade, nowTicks);
            Expire(this.fromIbkr, nowTicks, KeepIbkrFirstTicks);
            if (TakeMatch(this.fromTastytrade, price, size, printSeconds))
                return false;

            this.fromIbkr.Add(new Entry(price, size, printSeconds, nowTicks));
            return true;
        }
    }

    /// <summary>A print on Nasdaq's own markets, which never shows from IBKR. It uses up its partner, so a FINRA print can't match it by mistake.</summary>
    public void OnIbkrCarriedPrint(double price, double size, long printSeconds, long nowTicks)
    {
        lock (this.gate)
        {
            Expire(this.fromTastytrade, nowTicks);
            Expire(this.carriedFirst, nowTicks, KeepIbkrFirstTicks);
            if (!TakeMatch(this.fromTastytrade, price, size, printSeconds))
                this.carriedFirst.Add(new Entry(price, size, printSeconds, nowTicks));
        }
    }

    private static void Expire(List<Entry> entries, long nowTicks) => Expire(entries, nowTicks, KeepTicks);

    private static void Expire(List<Entry> entries, long nowTicks, long keepTicks)
    {
        int old = 0;
        while (old < entries.Count && nowTicks - entries[old].AddedTicks > keepTicks)
            old++;
        if (old > 0)
            entries.RemoveRange(0, old);
    }

    private static bool TakeMatch(List<Entry> entries, double price, double size, long seconds)
    {
        int best = -1;
        long bestScore = long.MaxValue;
        for (int i = 0; i < entries.Count; i++)
        {
            var entry = entries[i];
            if (!SameSize(entry.Size, size) || !SamePrice(entry.Price, price))
                continue;

            long apart = Math.Abs(entry.Seconds - seconds);
            if (apart > SecondsTolerance)
                continue;

            long score = apart + (Math.Abs(entry.Price - price) > 1e-7 ? 100 : 0);
            if (score < bestScore)
            {
                best = i;
                bestScore = score;
                if (score == 0)
                    break;
            }
        }

        if (best < 0)
            return false;

        entries.RemoveAt(best);
        return true;
    }

    private static bool SameSize(double a, double b) => Math.Floor(a) == Math.Floor(b);

    private static bool SamePrice(double a, double b) =>
        Math.Abs(a - b) <= PriceTolerance || RoundsTo(a, b) || RoundsTo(b, a);

    // IBKR rounds some stocks' prices to the cent: 735.4799 and 735.465 on tastytrade are 735.48 and
    // 735.47 on IBKR. Under a dollar a cent is too wide to say anything, and it hasn't been seen there.
    private static bool RoundsTo(double exact, double cents) =>
        cents >= 1 && Math.Abs(exact - cents) <= 0.00501 && Math.Abs((cents * 100) - Math.Round(cents * 100)) < 1e-6;
}

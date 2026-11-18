namespace ScalpFeedVendor.Feed;

/// <summary>
/// Limit Up-Limit Down price bands, worked out from the tape by the LULD Plan's published rules
/// (luldplan.com, permanent since 2019), because neither IBKR's API nor tastytrade's feed carries the
/// SIPs' own (IBKR-SEAMS.md, "LULD bands"). The rules: the reference price is the arithmetic mean of
/// eligible trades over the prior five minutes; the first of the day is the opening price; it changes only
/// when that mean is at least 1% away, and each one stands for at least 30 seconds; with no eligible trades
/// it stays. Band = reference +/- reference x percentage, rounded to the penny, the percentage set by the
/// previous close and tier. 9:30-16:00 ET only.
///
/// Where this can differ from the SIPs': the opening price is taken as the first eligible print from 9:30,
/// not the primary market's official open; eligible means a round lot that isn't off-market; IBKR rounds
/// some prices to the cent; trades are timed on arrival, about 0.3 s after the SIPs see them. A symbol
/// picked up mid-session starts from its first print, so until a reopening or the next day its band can be
/// off by up to about 1% of the price, and its rows carry a ~ (Limit Up ~, Limit Down ~).
///
/// After a pause the listing market's reopening auction prints (condition 5) about 0.2 s before IBKR's
/// halt flag clears (NXXT 2026-11-11: auction prints at 13:56:40.190 ET, tastytrade's status 13:56:40.357),
/// so a reopening print restarts the band itself instead of waiting for the flag.
/// </summary>
internal sealed class LuldBand
{
    // Each row's label in the depth's venue column: [lower, upper], then the same with the rough mark.
    private static readonly string[] Labels = ["Limit Down", "Limit Up", "Limit Down ~", "Limit Up ~"];

    /// <summary>The size a band's row carries in the depth: shown as 1, the smallest a row can read.</summary>
    public const double MarkerShares = 100;

    private static readonly TimeZoneInfo Eastern = TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");
    private static readonly TimeSpan SessionOpen = new(9, 30, 0);
    private static readonly TimeSpan SessionClose = new(16, 0, 0);
    private static readonly TimeSpan DoubledFrom = new(15, 35, 0);
    private static readonly TimeSpan OpeningGrace = TimeSpan.FromMinutes(1);
    private static readonly long WindowTicks = TimeSpan.FromMinutes(5).Ticks;
    private static readonly long MinHoldTicks = TimeSpan.FromSeconds(30).Ticks;
    private static HashSet<string> tier1 = new(StringComparer.OrdinalIgnoreCase);

    private readonly object gate = new();
    private readonly Queue<(long Ticks, double Price)> window = new();
    private double sum;
    private DateTime day;
    private double reference = double.NaN;
    private long referenceSince;
    private bool halted;
    private bool awaitingReopen;
    private bool flagStillUp;
    private bool exact;

    public readonly record struct Bands(double Lower, double Upper, double Reference, bool Exact);

    /// <summary>What a band's row says in the depth: Limit Up or Limit Down, with a ~ when it's rough.</summary>
    public static string Label(bool upper, bool exact) => Labels[(upper ? 1 : 0) + (exact ? 0 : 2)];

    public static bool IsLabel(string venue) => Array.IndexOf(Labels, venue) >= 0;

    /// <summary>How far past a rough band (picked up mid-session) rows still show: as far as it can be off.</summary>
    public const double RoughMargin = 0.01;

    /// <summary>
    /// The depth's rows on one side, less those past the band (your request, 2026-11-18: no rows under Limit Down
    /// or Limit Up): bids priced below the lower band and asks above the upper, resting orders that can't trade
    /// while the band stands. Rows at the band itself stay. A rough band (Exact false) can be off by up to 1% of the
    /// price either way, so rows within 1% past it stay too, rather than hide orders the real band may allow; with
    /// no lower band (a reference so low it would be 0 or less), the bids all stay.
    /// </summary>
    public static void Within(List<IbkrDepthRow> rows, Bands band, bool bids, List<IbkrDepthRow> into)
    {
        into.Clear();
        double edge = bids ? band.Lower : band.Upper;
        if (!(edge > 0))
        {
            into.AddRange(rows);
            return;
        }

        if (!band.Exact)
            edge *= bids ? 1 - RoughMargin : 1 + RoughMargin;
        foreach (var row in rows)
        {
            if (bids ? row.Price >= edge - 1e-9 : row.Price <= edge + 1e-9)
                into.Add(row);
        }
    }

    /// <summary>Tier 1 is the S&P 500, the Russell 1000 and some ETPs; everything else is Tier 2.</summary>
    public static bool IsTier1(string symbol) => Volatile.Read(ref tier1).Contains(symbol);

    /// <summary>One symbol per line; blank lines and lines starting with # are skipped. Returns how many.</summary>
    public static int LoadTier1(string path)
    {
        var symbols = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (File.Exists(path))
        {
            foreach (var line in File.ReadAllLines(path))
            {
                var symbol = line.Trim();
                if (symbol.Length > 0 && symbol[0] != '#')
                    symbols.Add(symbol);
            }
        }

        Volatile.Write(ref tier1, symbols);
        return symbols.Count;
    }

    /// <summary>
    /// A print that went on the tape. Only round lots count toward the reference price; a reopening
    /// print (condition 5) sets it whatever its size.
    /// </summary>
    public void OnTrade(double price, long utcTicks, bool roundLot, bool reopening = false)
    {
        if (!(roundLot || reopening) || !(price > 0) || !InSession(utcTicks, out var et))
            return;

        lock (this.gate)
        {
            this.StartDay(et.Date);
            bool reopensNow = reopening && (this.halted || this.awaitingReopen);
            if (reopensNow)
            {
                // The auction beat the halt flag here; when the flag does clear, the band is already running.
                this.flagStillUp = this.halted;
                this.halted = false;
            }
            else if (this.halted)
            {
                return;
            }

            if (double.IsNaN(this.reference) || this.awaitingReopen || reopensNow)
            {
                // The opening print, or the reopening one after a pause, is the first reference price.
                this.exact = this.awaitingReopen || reopensNow || et.TimeOfDay - SessionOpen < OpeningGrace;
                this.reference = price;
                this.referenceSince = utcTicks;
                this.awaitingReopen = false;
                this.window.Clear();
                this.sum = 0;
            }

            if (roundLot)
            {
                this.window.Enqueue((utcTicks, price));
                this.sum += price;
            }

            this.Advance(utcTicks);
        }
    }

    /// <summary>
    /// IBKR's Halted tick isn't being watched any more, so a halt the band is waiting out could end unseen. The
    /// band starts over from the next print, as when picked up mid-session; if the halt is still on, IBKR says so
    /// again when it streams the symbol again. Nothing changes when the band isn't waiting out a halt.
    /// </summary>
    public void ForgetHalt()
    {
        lock (this.gate)
        {
            if (!this.halted)
                return;

            this.halted = false;
            this.flagStillUp = false;
            this.awaitingReopen = false;
            this.exact = false;
            this.reference = double.NaN;
            this.window.Clear();
            this.sum = 0;
        }
    }

    /// <summary>IBKR's Halted tick: no bands during a pause, and the reopening print starts them again.</summary>
    public void OnHalt(bool isHalted)
    {
        lock (this.gate)
        {
            if (isHalted)
            {
                this.halted = true;
                this.flagStillUp = false;
            }
            else if (this.flagStillUp)
            {
                this.flagStillUp = false;
            }
            else if (this.halted)
            {
                this.halted = false;
                this.awaitingReopen = true;
            }
        }
    }

    /// <summary>
    /// The reference price, for which way a halt beginning now went (ResSide). Only while the band runs and has had
    /// a round lot in the last five minutes, so a reference left from earlier in the day doesn't count. Asked
    /// before OnHalt, which stops the band.
    /// </summary>
    public bool TryGetRecentReference(long utcTicks, out double reference)
    {
        reference = double.NaN;
        if (!InSession(utcTicks, out var et))
            return false;

        lock (this.gate)
        {
            this.StartDay(et.Date);
            if (this.halted || this.awaitingReopen || double.IsNaN(this.reference))
                return false;

            this.Advance(utcTicks);
            if (this.window.Count == 0)
                return false;

            reference = this.reference;
            return true;
        }
    }

    public bool TryGetBands(long utcTicks, double prevClose, bool isTier1, out Bands bands)
    {
        bands = default;
        if (!InSession(utcTicks, out var et))
            return false;

        lock (this.gate)
        {
            this.StartDay(et.Date);
            if (this.halted || this.awaitingReopen || double.IsNaN(this.reference))
                return false;

            this.Advance(utcTicks);

            // In decimal, so a band of 0.175 rounds to 0.18 the way the plan's penny rounding would, not to the
            // 0.17 that 0.1 + 0.075 gives in binary.
            decimal basis = prevClose > 0 ? (decimal)prevClose : (decimal)this.reference;
            decimal r = (decimal)this.reference;
            decimal offset = basis > 3.00m ? r * (isTier1 ? 0.05m : 0.10m)
                : basis >= 0.75m ? r * 0.20m
                : Math.Min(0.15m, r * 0.75m);
            if (et.TimeOfDay >= DoubledFrom && (isTier1 || basis <= 3.00m))
                offset *= 2;

            double upper = (double)Math.Round(r + offset, 2, MidpointRounding.AwayFromZero);
            double lower = (double)Math.Round(r - offset, 2, MidpointRounding.AwayFromZero);
            bands = new Bands(lower > 0 ? lower : double.NaN, upper, this.reference, this.exact);
            return true;
        }
    }

    private static bool InSession(long utcTicks, out DateTime et)
    {
        et = TimeZoneInfo.ConvertTimeFromUtc(new DateTime(utcTicks, DateTimeKind.Utc), Eastern);
        return et.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday)
            && et.TimeOfDay >= SessionOpen && et.TimeOfDay < SessionClose;
    }

    private void StartDay(DateTime date)
    {
        if (date == this.day)
            return;

        this.day = date;
        this.reference = double.NaN;
        this.awaitingReopen = false;
        this.flagStillUp = false;
        this.exact = false;
        this.window.Clear();
        this.sum = 0;
    }

    /// <summary>Drops trades older than five minutes, then moves the reference if the plan would.</summary>
    private void Advance(long utcTicks)
    {
        while (this.window.Count > 0 && this.window.Peek().Ticks <= utcTicks - WindowTicks)
            this.sum -= this.window.Dequeue().Price;

        if (this.window.Count == 0)
        {
            this.sum = 0;
            return;
        }

        // "At least 1%": a mean exactly 1% away counts, though binary arithmetic can land it a hair short.
        double mean = this.sum / this.window.Count;
        if (Math.Abs(mean - this.reference) >= (0.01 * this.reference) - 1e-9 && utcTicks - this.referenceSince >= MinHoldTicks)
        {
            this.reference = mean;
            this.referenceSince = utcTicks;
        }
    }
}

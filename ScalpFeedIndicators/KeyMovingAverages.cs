using System.Drawing;
using TradingPlatform.BusinessLayer;
using TradingPlatform.BusinessLayer.Utils;

namespace ScalpFeedIndicators;

/// <summary>
/// EMA 9, 20 and 200, simple MA 50 and 200, and a session VWAP on the price chart, as one indicator.
/// Each has its own on/off switch in the indicator's inputs; colour, width and style are in its line
/// settings. The averages are Quantower's built-in EMA and SMA. The VWAP is worked out here from the
/// chart's own bars and by default starts over at each session, 4:00, 9:30 and 16:00 ET, with a break
/// in its line at each restart (SessionVwap says why Quantower's own wasn't used). It can also draw the rolling
/// candle (RollingCandleView), on by default, so a Free licence's two indicators a chart can still have it.
/// </summary>
public sealed class KeyMovingAverages : Indicator
{
    private const string Ema = "EMA";
    private const string Sma = "MA";
    private const int VwapLine = 5;

    [InputParameter("Show EMA 9", 10)]
    public bool ShowEma9 = true;

    [InputParameter("EMA 9 period", 11, 1, 9999, 1, 0)]
    public int Ema9Period = 9;

    [InputParameter("Show EMA 20", 20)]
    public bool ShowEma20 = true;

    [InputParameter("EMA 20 period", 21, 1, 9999, 1, 0)]
    public int Ema20Period = 20;

    [InputParameter("Show EMA 200", 30)]
    public bool ShowEma200 = true;

    [InputParameter("EMA 200 period", 31, 1, 9999, 1, 0)]
    public int Ema200Period = 200;

    [InputParameter("Show MA 50", 40)]
    public bool ShowSma50 = true;

    [InputParameter("MA 50 period", 41, 1, 9999, 1, 0)]
    public int Sma50Period = 50;

    [InputParameter("Show MA 200", 50)]
    public bool ShowSma200 = true;

    [InputParameter("MA 200 period", 51, 1, 9999, 1, 0)]
    public int Sma200Period = 200;

    [InputParameter("Price for the averages", 60, variants: new object[]
    {
        "Close", PriceType.Close,
        "Open", PriceType.Open,
        "High", PriceType.High,
        "Low", PriceType.Low,
        "Typical (H+L+C)/3", PriceType.Typical,
        "Median (H+L)/2", PriceType.Median,
        "Weighted (H+L+2C)/4", PriceType.Weighted,
    })]
    public PriceType SourcePrice = PriceType.Close;

    [InputParameter("Show VWAP", 70)]
    public bool ShowVwap = true;

    // A new name, not "VWAP starts each day at": workspaces saved that as 4:00 ET only, and keeping the name
    // would bring that back instead of the per-session default.
    [InputParameter("VWAP starts over", 71, variants: new object[]
    {
        "Each session: 4:00, 9:30 and 16:00 ET", VwapEachSession,
        "Once a day, at 4:00 ET", VwapFrom4,
        "Once a day, at 9:30 ET", VwapFrom930,
    })]
    public int VwapStarts = VwapEachSession;

    private const int VwapEachSession = 0;
    private const int VwapFrom4 = 1;
    private const int VwapFrom930 = 2;

    // The rolling candle (RollingCandleView), here as well as on its own so a Free licence's two indicators a chart
    // can still have it (your request, 2026-11-18).
    [InputParameter("Show rolling candle", 80)]
    public bool ShowRollingCandle = true;

    [InputParameter("Rolling candle window (seconds)", 81, 1, 600, 1, 0)]
    public int RollingCandleSeconds = 10;

    [InputParameter("Rolling candle typical volume over the last (minutes)", 82, 1, 240, 1, 0)]
    public int RollingCandleTypicalMinutes = 5;

    [InputParameter("Rolling candle full colour at (times typical volume)", 83, 1.1, 50, 0.1, 1)]
    public double RollingCandleFullMultiple = 2;

    [InputParameter("Rolling candle glow beyond (times typical volume)", 84, 1.1, 50, 0.1, 1)]
    public double RollingCandleGlowMultiple = 4;

    private RollingCandleView rollingCandle;

    // One per moving average line, in the order the lines were added.
    private readonly Indicator[] averages = new Indicator[5];
    private readonly int[] periods = new int[5];
    private SessionVwap vwap;
    private readonly HashSet<DateTime> vwapStartsLogged = new();
    private string shortName = "MAs";

    // Colour trace: which instance, and what the lines held when last logged.
    private static int instances;
    private readonly int instance = Interlocked.Increment(ref instances);
    private Color[] seenColours;
    private string lastSaved;

    public KeyMovingAverages()
    {
        this.Name = "Key Moving Averages";
        this.Description = "EMA 9, 20 and 200, simple MA 50 and 200, and a session VWAP, each on or off, with its own colour, width and style; and a live rolling candle of the last N seconds";

        this.AddLineSeries("EMA 9", Color.Gold, 1, LineStyle.Solid);
        this.AddLineSeries("EMA 20", Color.DeepSkyBlue, 1, LineStyle.Solid);
        this.AddLineSeries("EMA 200", Color.MediumPurple, 2, LineStyle.Solid);
        this.AddLineSeries("MA 50", Color.Orange, 1, LineStyle.Solid);
        this.AddLineSeries("MA 200", Color.Red, 2, LineStyle.Solid);
        this.AddLineSeries("VWAP", Color.Magenta, 2, LineStyle.Solid);

        this.SeparateWindow = false;
    }

    /// <summary>The chart legend's name: which lines are on, with their periods.</summary>
    public override string ShortName => this.shortName;

    /// <summary>
    /// Unchanged behaviour; logs the line colours going in and out, to find where a colour change is lost.
    /// Quantower reads this for the settings window and for each workspace save, and sets it on OK and on load.
    /// </summary>
    public override IList<SettingItem> Settings
    {
        get
        {
            var value = base.Settings;
            string colours = ColoursIn(value);
            if (colours != this.lastSaved)
            {
                this.lastSaved = colours;
                this.Trace("read", colours);
            }
            return value;
        }
        set
        {
            this.Trace("applying", ColoursIn(value));
            base.Settings = value;
            this.Trace("applied", this.LineColours());
        }
    }

    protected override void OnInit()
    {
        this.Trace("init", this.LineColours());

        var lines = new (bool On, int Period, string Kind)[]
        {
            (this.ShowEma9, this.Ema9Period, Ema),
            (this.ShowEma20, this.Ema20Period, Ema),
            (this.ShowEma200, this.Ema200Period, Ema),
            (this.ShowSma50, this.Sma50Period, Sma),
            (this.ShowSma200, this.Sma200Period, Sma),
        };

        var shown = new List<string>();
        for (int i = 0; i < lines.Length; i++)
        {
            var (on, period, kind) = lines[i];
            this.periods[i] = Math.Max(1, period);

            // Off means not drawn, not in the legend, and not worked out at all.
            this.LinesSeries[i].Visible = on;
            if (!on)
                continue;

            this.averages[i] = kind == Ema
                ? Core.Instance.Indicators.BuiltIn.EMA(this.periods[i], this.SourcePrice)
                : Core.Instance.Indicators.BuiltIn.SMA(this.periods[i], this.SourcePrice);
            this.AddIndicator(this.averages[i]);
            shown.Add($"{kind} {this.periods[i]}");
        }

        this.LinesSeries[VwapLine].Visible = this.ShowVwap;
        if (this.ShowVwap)
        {
            var premarket = new TimeSpan(4, 0, 0);
            var open = new TimeSpan(9, 30, 0);
            var afterHours = new TimeSpan(16, 0, 0);
            (this.vwap, string label) = this.VwapStarts switch
            {
                VwapFrom4 => (new SessionVwap(premarket), "VWAP from 4:00 ET"),
                VwapFrom930 => (new SessionVwap(open), "VWAP from 9:30 ET"),
                _ => (new SessionVwap(premarket, open, afterHours), "VWAP"),
            };
            shown.Add(label);
        }

        if (this.ShowRollingCandle)
        {
            this.rollingCandle ??= new RollingCandleView(this, () => this.Count > 0 ? this.Time() : null);
            this.rollingCandle.Start(this.Symbol, this.RollingCandleSeconds, this.RollingCandleTypicalMinutes,
                this.RollingCandleFullMultiple, this.RollingCandleGlowMultiple);
            shown.Add($"{Math.Max(1, this.RollingCandleSeconds)}s candle");
        }

        this.shortName = shown.Count > 0 ? $"MAs ({string.Join(", ", shown)})" : "MAs (none)";
        this.Trace("init done", this.LineColours());
        this.seenColours = this.LinesSeries.Select(l => l.Color).ToArray();
    }

    public override void OnPaintChart(PaintChartEventArgs args)
    {
        base.OnPaintChart(args);
        if (this.ShowRollingCandle)
            this.rollingCandle?.Paint(args);
    }

    protected override void OnUpdate(UpdateArgs args)
    {
        this.MatchSymbolDecimals();

        // A colour that changes without going through Settings.
        if (this.seenColours != null)
        {
            for (int i = 0; i < this.seenColours.Length; i++)
            {
                if (this.LinesSeries[i].Color != this.seenColours[i])
                {
                    this.seenColours = this.LinesSeries.Select(l => l.Color).ToArray();
                    this.Trace("changed while drawing", this.LineColours());
                    break;
                }
            }
        }

        for (int i = 0; i < this.averages.Length; i++)
        {
            // Until a full period of bars exists the average isn't meaningful, so nothing is drawn.
            if (this.averages[i] != null && this.Count >= this.periods[i])
                this.SetValue(this.averages[i].GetValue(), i);
        }

        if (this.vwap != null)
        {
            DateTime bar = this.Time();
            DateTime previous = this.Count > 1 ? this.Time(1) : default;
            double value = this.vwap.Update(
                bar,
                previous,
                this.GetPrice(PriceType.High),
                this.GetPrice(PriceType.Low),
                this.GetPrice(PriceType.Close),
                this.GetPrice(PriceType.Volume));
            if (!double.IsNaN(value))
            {
                this.SetValue(value, VwapLine);

                // Each day's VWAP is a line of its own: nothing joins it to the day before, so the restart
                // shows even when the two days' VWAPs are close.
                if (this.vwap.BeginsLine(bar))
                {
                    this.SetLineBreak(0, VwapLine);
                    if (previous != default)
                        this.LogVwapStart(bar, value);
                }
            }
        }
    }

    /// <summary>
    /// Shows the lines' values with as many decimals as the ticker's price has now: the symbol's own band
    /// for its latest close (from the connection, 4 under 10 cents, 3 under a dollar, 2 from a dollar up).
    /// Quantower's default for an indicator is one precision for the symbol whatever its price, which put
    /// extra decimals on a $4 stock. Only the display changes; the lines keep their full values.
    /// </summary>
    private void MatchSymbolDecimals()
    {
        try
        {
            double price = this.GetPrice(PriceType.Close);
            var band = price > 0 ? this.Symbol?.FindVariableTick(price) : null;
            if (band != null && band.Precision >= 0 && band.Precision != this.Digits)
                this.Digits = band.Precision;
        }
        catch
        {
            // Quantower's own formatting stays in place if the symbol can't say.
        }
    }

    /// <summary>Says in Quantower's log where each day's VWAP started over, once per day and chart.</summary>
    private void LogVwapStart(DateTime barUtc, double value)
    {
        if (!this.vwapStartsLogged.Add(barUtc))
            return;

        try
        {
            var eastern = TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");
            var utc = DateTime.SpecifyKind(barUtc, DateTimeKind.Utc);
            double before = this.Count > 1 ? this.GetValue(1, VwapLine) : double.NaN;
            Core.Instance.Loggers.Log(
                $"Key Moving Averages: {this.Symbol?.Name} VWAP starts over at {TimeZoneInfo.ConvertTimeFromUtc(utc, eastern):MM-dd HH:mm} ET " +
                $"({utc.ToLocalTime():HH:mm} local): {before:0.####} on the bar before, {value:0.####} now",
                LoggingLevel.System);
        }
        catch
        {
            // Logging must never stop the indicator.
        }
    }

    protected override void OnClear()
    {
        for (int i = 0; i < this.averages.Length; i++)
        {
            if (this.averages[i] == null)
                continue;

            this.RemoveIndicator(this.averages[i]);
            this.averages[i].Dispose();
            this.averages[i] = null;
        }

        this.vwap?.Clear();
        this.vwap = null;
        this.vwapStartsLogged.Clear();
        this.rollingCandle?.Stop();
        this.seenColours = null;
        this.Trace("clear", this.LineColours());
    }

    private string LineColours() => string.Join(" ", this.LinesSeries.Select(l => ColourName(l.Color)));

    /// <summary>The colour each Line_N group carries, or - where the group or its line is missing.</summary>
    private static string ColoursIn(IList<SettingItem> settings)
    {
        if (settings == null)
            return "(none)";

        var names = new List<string>();
        for (int i = 0; i < 6; i++)
        {
            var group = settings.OfType<SettingItemGroup>().FirstOrDefault(g => g.Name == $"Line_{i}");
            var line = (group?.Value as IEnumerable<SettingItem>)?.FirstOrDefault(s => s.Name == "Line");
            names.Add(line?.Value is LineOptions options ? ColourName(options.Color) : "-");
        }
        return string.Join(" ", names);
    }

    private static string ColourName(Color c) => c.IsNamedColor ? c.Name : $"#{c.R:X2}{c.G:X2}{c.B:X2}";

    private void Trace(string step, string colours)
    {
        try
        {
            Core.Instance.Loggers.Log($"Key Moving Averages #{this.instance} colours, {step}: {colours}", LoggingLevel.System);
        }
        catch
        {
            // The trace must never stop the indicator.
        }
    }
}

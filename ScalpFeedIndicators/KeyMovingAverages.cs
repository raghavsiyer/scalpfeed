using System.Drawing;
using TradingPlatform.BusinessLayer;
using TradingPlatform.BusinessLayer.Utils;

namespace ScalpFeedIndicators;

/// <summary>
/// EMA 9, 20 and 200, simple MA 50 and 200, and a session VWAP on the price chart, as one indicator.
/// Each has its own on/off switch in the indicator's inputs; colour, width and style are in its line
/// settings. The averages are Quantower's built-in EMA and SMA. The VWAP is worked out here from the
/// chart's own bars and starts over at a set New York time each day (SessionVwap says why Quantower's
/// own wasn't used).
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

    [InputParameter("VWAP starts each day at", 71, variants: new object[]
    {
        "4:00 ET (premarket open)", 240,
        "9:30 ET (regular open)", 570,
        "Midnight ET", 0,
    })]
    public int VwapStartMinutes = 240;

    // One per moving average line, in the order the lines were added.
    private readonly Indicator[] averages = new Indicator[5];
    private readonly int[] periods = new int[5];
    private SessionVwap vwap;
    private string shortName = "MAs";

    // Colour trace: which instance, and what the lines held when last logged.
    private static int instances;
    private readonly int instance = Interlocked.Increment(ref instances);
    private Color[] seenColours;
    private string lastSaved;

    public KeyMovingAverages()
    {
        this.Name = "Key Moving Averages";
        this.Description = "EMA 9, 20 and 200, simple MA 50 and 200, and a session VWAP, each on or off, with its own colour, width and style";

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
            var start = TimeSpan.FromMinutes(this.VwapStartMinutes);
            this.vwap = new SessionVwap(start);
            shown.Add($"VWAP from {start:h\\:mm} ET");
        }

        this.shortName = shown.Count > 0 ? $"MAs ({string.Join(", ", shown)})" : "MAs (none)";
        this.Trace("init done", this.LineColours());
        this.seenColours = this.LinesSeries.Select(l => l.Color).ToArray();
    }

    protected override void OnUpdate(UpdateArgs args)
    {
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
            double value = this.vwap.Update(
                this.Time(),
                this.Count > 1 ? this.Time(1) : default,
                this.GetPrice(PriceType.High),
                this.GetPrice(PriceType.Low),
                this.GetPrice(PriceType.Close),
                this.GetPrice(PriceType.Volume));
            if (!double.IsNaN(value))
                this.SetValue(value, VwapLine);
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

using System.Drawing;
using TradingPlatform.BusinessLayer;

namespace ScalpFeedIndicators;

/// <summary>
/// EMA 9, 20 and 200 and simple MA 50 and 200 on the price chart, as one indicator. Each has its own
/// on/off switch and period in the indicator's inputs; colour, width and style are in its line settings.
/// The averages are Quantower's own built-in EMA and SMA, so they match the stock indicators exactly.
/// </summary>
public sealed class KeyMovingAverages : Indicator
{
    private const string Ema = "EMA";
    private const string Sma = "MA";

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

    [InputParameter("Price", 60, variants: new object[]
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

    // One per line, in the order the lines were added.
    private readonly Indicator[] averages = new Indicator[5];
    private readonly int[] periods = new int[5];
    private string shortName = "MAs";

    public KeyMovingAverages()
    {
        this.Name = "Key Moving Averages";
        this.Description = "EMA 9, 20 and 200 and simple MA 50 and 200, each on or off, with its own colour, width and style";

        this.AddLineSeries("EMA 9", Color.Gold, 1, LineStyle.Solid);
        this.AddLineSeries("EMA 20", Color.DeepSkyBlue, 1, LineStyle.Solid);
        this.AddLineSeries("EMA 200", Color.MediumPurple, 2, LineStyle.Solid);
        this.AddLineSeries("MA 50", Color.Orange, 1, LineStyle.Solid);
        this.AddLineSeries("MA 200", Color.Red, 2, LineStyle.Solid);

        this.SeparateWindow = false;
    }

    protected override void OnInit()
    {
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

        this.shortName = shown.Count > 0 ? $"MAs ({string.Join(", ", shown)})" : "MAs (none)";
    }

    /// <summary>The chart legend's name: which averages are on, with their periods.</summary>
    public override string ShortName => this.shortName;

    protected override void OnUpdate(UpdateArgs args)
    {
        for (int i = 0; i < this.averages.Length; i++)
        {
            // Until a full period of bars exists the average isn't meaningful, so nothing is drawn.
            if (this.averages[i] != null && this.Count >= this.periods[i])
                this.SetValue(this.averages[i].GetValue(), i);
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
    }
}

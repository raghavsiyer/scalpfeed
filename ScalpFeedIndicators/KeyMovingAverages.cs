using System.Collections.Concurrent;
using System.Drawing;
using TradingPlatform.BusinessLayer;
using TradingPlatform.BusinessLayer.Abstractions;
using TradingPlatform.BusinessLayer.History.Aggregations;

namespace ScalpFeedIndicators;

/// <summary>
/// EMA 9, 20 and 200, simple MA 50 and 200, and VWAP on the price chart, as one indicator. Each has its
/// own on/off switch in the indicator's inputs; colour, width and style are in its line settings.
/// The averages are Quantower's built-in EMA and SMA, and the VWAP is Quantower's own VWAP calculation
/// (the one behind the chart's VWAP tool, as in its VwapExamples sample), so all of them match
/// Quantower's own tools exactly.
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

    // One per moving average line, in the order the lines were added.
    private readonly Indicator[] averages = new Indicator[5];
    private readonly int[] periods = new int[5];

    // Quantower's VWAP for each chart bar, keyed by the bar's start time. Its updates come on another thread.
    private readonly ConcurrentDictionary<DateTime, double> vwapByBar = new();
    private HistoricalData vwapHistory;
    private int vwapHits;
    private int vwapMisses;
    private bool vwapReported;
    private string shortName = "MAs";

    public KeyMovingAverages()
    {
        this.Name = "Key Moving Averages";
        this.Description = "EMA 9, 20 and 200, simple MA 50 and 200, and VWAP, each on or off, with its own colour, width and style";

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

        this.LinesSeries[VwapLine].Visible = this.ShowVwap;
        if (this.ShowVwap && this.StartVwap())
            shown.Add("VWAP");

        this.shortName = shown.Count > 0 ? $"MAs ({string.Join(", ", shown)})" : "MAs (none)";
    }

    protected override void OnUpdate(UpdateArgs args)
    {
        for (int i = 0; i < this.averages.Length; i++)
        {
            // Until a full period of bars exists the average isn't meaningful, so nothing is drawn.
            if (this.averages[i] != null && this.Count >= this.periods[i])
                this.SetValue(this.averages[i].GetValue(), i);
        }

        if (this.vwapHistory == null)
            return;

        bool found = this.vwapByBar.TryGetValue(this.Time(), out double vwap);
        if (found)
            this.SetValue(vwap, VwapLine);

        // Once the history is drawn, say in Quantower's log how many bars found their VWAP, so a
        // chart where the two don't line up shows as that rather than as a quietly missing line.
        if (args.Reason == UpdateReason.HistoricalBar)
        {
            if (found)
                this.vwapHits++;
            else
                this.vwapMisses++;
        }
        else if (!this.vwapReported)
        {
            this.vwapReported = true;
            Core.Instance.Loggers.Log($"Key Moving Averages: VWAP on {this.vwapHits} of {this.vwapHits + this.vwapMisses} bars of {this.Symbol?.Name}", LoggingLevel.System);
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

        this.StopVwap();
    }

    /// <summary>
    /// Asks Quantower for its daily VWAP on this chart's own bars, as the chart's VWAP tool does with its
    /// default settings: typical price (H+L+C)/3 times each bar's volume, starting over each day in the
    /// terminal's time zone. It loads with the chart's history and then keeps updating live.
    /// </summary>
    private bool StartVwap()
    {
        var chart = this.HistoricalData;
        if (chart?.Symbol == null || chart.Aggregation == null)
            return false;

        try
        {
            var parameters = new HistoryAggregationVwapParameters
            {
                Aggregation = chart.Aggregation,
                DataType = VwapDataType.CurrentTF,
                Period = Period.DAY1,
                PriceType = VwapPriceType.HLC3,
                StdCalculationType = VwapStdCalculationType.StandardDeviation,
                TimeZone = Core.Instance.TimeUtils.SelectedTimeZone,
            };

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            this.vwapHistory = chart.Symbol.GetHistory(new HistoryRequestParameters
            {
                Aggregation = new HistoryAggregationVwap(parameters),
                Symbol = chart.Symbol,
                FromTime = chart.FromTime,
                ToTime = default,
                CancellationToken = cts.Token,
                SessionsContainer = DefaultSessionsContainer.Instance,
            });

            if (this.vwapHistory == null)
                return false;

            for (int i = 0; i < this.vwapHistory.Count; i++)
                this.Remember(this.vwapHistory[i, SeekOriginHistory.Begin]);

            this.vwapHistory.NewHistoryItem += this.OnVwapItem;
            this.vwapHistory.HistoryItemUpdated += this.OnVwapItem;
            return true;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // A chart type VWAP can't follow (Renko, say) just goes without the line.
            Core.Instance.Loggers.Log($"Key Moving Averages: no VWAP on this chart: {ex.Message}", LoggingLevel.Error);
            this.StopVwap();
            return false;
        }
    }

    private void StopVwap()
    {
        if (this.vwapHistory != null)
        {
            this.vwapHistory.NewHistoryItem -= this.OnVwapItem;
            this.vwapHistory.HistoryItemUpdated -= this.OnVwapItem;
            this.vwapHistory.Dispose();
            this.vwapHistory = null;
        }

        this.vwapByBar.Clear();
        this.vwapHits = 0;
        this.vwapMisses = 0;
        this.vwapReported = false;
    }

    private void OnVwapItem(object sender, HistoryEventArgs e) => this.Remember(e.HistoryItem);

    private void Remember(IHistoryItem item)
    {
        if (item is IVwapHistoryItem vwap && vwap.Value > 0 && !double.IsNaN(vwap.Value))
            this.vwapByBar[vwap.TimeLeft] = vwap.Value;
    }
}

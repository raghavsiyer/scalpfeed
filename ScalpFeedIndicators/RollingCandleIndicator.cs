using TradingPlatform.BusinessLayer;

namespace ScalpFeedIndicators;

/// <summary>
/// The rolling candle on its own (RollingCandle, RollingCandleView): a single candle of the last N seconds' trades
/// next to the latest bar, live. Key Moving Averages can draw the same candle, which saves an indicator slot on a
/// Free licence; this one is for charts without it.
/// </summary>
public sealed class RollingCandleIndicator : Indicator
{
    [InputParameter("Window (seconds)", 10, 1, 600, 1, 0)]
    public int WindowSeconds = 10;

    [InputParameter("Typical volume over the last (minutes)", 20, 1, 240, 1, 0)]
    public int TypicalMinutes = 5;

    [InputParameter("Full colour at (times typical volume)", 30, 1.1, 50, 0.1, 1)]
    public double FullMultiple = 2;

    [InputParameter("Glow beyond (times typical volume)", 40, 1.1, 50, 0.1, 1)]
    public double GlowMultiple = 4;

    [InputParameter("Lowest intensity under (shares at a price, or in the window)", 50, 0, 1_000_000, 100, 0)]
    public int MinShares = 1000;

    private readonly RollingCandleView view;

    public RollingCandleIndicator()
    {
        this.Name = "Rolling Candle";
        this.Description = "One live candle of the last N seconds' trades, drawn next to the latest bar";
        this.SeparateWindow = false;
        this.view = new RollingCandleView(this, () => this.Count > 0 ? this.Time() : null);
    }

    public override string ShortName => $"Rolling {this.WindowSeconds}s";

    protected override void OnInit() => this.view.Start(this.Symbol, this.WindowSeconds, this.TypicalMinutes, this.FullMultiple, this.GlowMultiple, this.MinShares);

    protected override void OnClear() => this.view.Stop();

    public override void OnPaintChart(PaintChartEventArgs args) => this.view.Paint(args);
}

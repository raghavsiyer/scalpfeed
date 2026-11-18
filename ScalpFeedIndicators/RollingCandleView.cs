using System.Drawing;
using TradingPlatform.BusinessLayer;

namespace ScalpFeedIndicators;

/// <summary>
/// Puts a RollingCandle on its indicator's price chart: one candle just right of the latest bar, as wide as the
/// chart's bars, green with the close at or above the open and red below, its window over it ("10s") and the
/// window's volume under it. Its colour is as strong as its volume relative to the busiest window of the last few
/// minutes (VolumeBrightness): full at the busiest, a quarter with nothing traded. It takes the chart symbol's
/// prints as they arrive (Symbol.NewLast) and, four times a
/// second, asks the chart to redraw when the candle has changed, so trades roll out of the window even when nothing
/// trades. Used by the Rolling Candle indicator and by Key Moving Averages, so a Free licence's two indicators a
/// chart can still have it.
/// </summary>
internal sealed class RollingCandleView
{
    private const int RedrawEveryMs = 250;
    private static readonly Color Up = Color.FromArgb(38, 166, 154);
    private static readonly Color Down = Color.FromArgb(239, 83, 80);
    private static readonly Color Label = Color.FromArgb(200, 200, 200);

    private readonly Indicator owner;
    private readonly Func<DateTime?> latestBarTime;
    private readonly RollingCandle candle = new(TimeSpan.FromSeconds(10));
    private readonly VolumeBrightness brightness = new(TimeSpan.FromMinutes(5));
    private readonly object gate = new();
    private Symbol symbol;
    private Timer timer;
    private (RollingBar Bar, int Alpha) painted = (RollingBar.None, 0);
    private int seconds = 10;

    /// <param name="owner">The indicator whose chart it draws on.</param>
    /// <param name="latestBarTime">The open time of the chart's latest bar, or null with no bars yet.</param>
    public RollingCandleView(Indicator owner, Func<DateTime?> latestBarTime)
    {
        this.owner = owner;
        this.latestBarTime = latestBarTime;
    }

    /// <summary>
    /// Starts over on this symbol with a window of this many seconds, its colour's strength judged against the
    /// busiest window of this many minutes.
    /// </summary>
    public void Start(Symbol chartSymbol, int windowSeconds, int brightnessMinutes)
    {
        this.Stop();
        this.seconds = Math.Max(1, windowSeconds);
        this.candle.Window = TimeSpan.FromSeconds(this.seconds);
        this.candle.Clear();
        this.brightness.Lookback = TimeSpan.FromMinutes(Math.Max(1, brightnessMinutes));
        this.brightness.Clear();
        if (chartSymbol == null)
            return;

        lock (this.gate)
        {
            this.symbol = chartSymbol;
            chartSymbol.NewLast += this.OnNewLast;
            this.timer = new Timer(_ => this.OnTimer(), null, RedrawEveryMs, RedrawEveryMs);
        }
    }

    public void Stop()
    {
        lock (this.gate)
        {
            if (this.symbol != null)
                this.symbol.NewLast -= this.OnNewLast;
            this.symbol = null;
            this.timer?.Dispose();
            this.timer = null;
        }

        this.candle.Clear();
        this.brightness.Clear();
    }

    private void OnNewLast(Symbol from, Last last) => this.candle.Add(DateTime.UtcNow.Ticks, last.Price, last.Size);

    /// <summary>The candle now, and its colour's alpha, with this look counted as a brightness sample.</summary>
    private (RollingBar Bar, int Alpha) Look()
    {
        long now = DateTime.UtcNow.Ticks;
        var bar = this.candle.At(now);
        return (bar, bar.IsEmpty ? 0 : VolumeBrightness.Alpha(this.brightness.Observe(now, bar.Volume)));
    }

    private void OnTimer()
    {
        var now = this.Look();
        (RollingBar Bar, int Alpha) shown;
        lock (this.gate)
            shown = this.painted;
        if (now == shown)
            return;

        try
        {
            this.owner.CurrentChart?.RedrawBuffer();
        }
        catch (Exception)
        {
            // A chart closing as the timer fires; the next tick, or none, will do.
        }
    }

    public void Paint(PaintChartEventArgs args)
    {
        var chart = this.owner.CurrentChart;
        var window = chart?.MainWindow;
        var graphics = args?.Graphics;
        if (window == null || graphics == null)
            return;

        var look = this.Look();
        var bar = look.Bar;
        lock (this.gate)
            this.painted = look;
        if (bar.IsEmpty)
            return;

        var converter = window.CoordinatesConverter;
        Rectangle area = window.ClientRectangle;
        float barWidth = Math.Max(1f, (float)chart.BarsWidth);
        float body = Math.Max(3f, barWidth * 0.7f);

        // Two bar widths right of the latest bar, kept inside the chart when it's scrolled back or tight on the right.
        float x = this.latestBarTime() is DateTime latest ? (float)converter.GetChartX(latest) + barWidth * 2 : area.Right - body * 2;
        float left = area.Left + body;
        float right = area.Right - body - 2;
        x = right > left ? Math.Clamp(x, left, right) : area.Right - body;

        float yOpen = (float)converter.GetChartY(bar.Open);
        float yHigh = (float)converter.GetChartY(bar.High);
        float yLow = (float)converter.GetChartY(bar.Low);
        float yClose = (float)converter.GetChartY(bar.Close);

        var state = graphics.Save();
        try
        {
            graphics.SetClip(area);
            var colour = Color.FromArgb(look.Alpha, bar.Close >= bar.Open ? Up : Down);
            using var pen = new Pen(colour, 1);
            using var fill = new SolidBrush(colour);
            graphics.DrawLine(pen, x, yHigh, x, yLow);
            float top = Math.Min(yOpen, yClose);
            float height = Math.Max(1f, Math.Abs(yClose - yOpen));
            graphics.FillRectangle(fill, x - body / 2, top, body, height);

            using var font = new Font("Segoe UI", 8f);
            using var text = new SolidBrush(Label);
            string over = $"{this.seconds}s";
            var size = graphics.MeasureString(over, font);
            graphics.DrawString(over, font, text, x - size.Width / 2, yHigh - size.Height - 1);
            string under = RollingCandle.FormatVolume(bar.Volume);
            size = graphics.MeasureString(under, font);
            graphics.DrawString(under, font, text, x - size.Width / 2, yLow + 1);
        }
        finally
        {
            graphics.Restore(state);
        }
    }
}

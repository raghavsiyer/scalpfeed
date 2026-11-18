using System.Drawing;
using System.Drawing.Drawing2D;
using TradingPlatform.BusinessLayer;

namespace ScalpFeedIndicators;

/// <summary>
/// Puts a RollingCandle on its indicator's price chart: one candle just right of the latest bar, as wide as the
/// chart's bars, green with the close at or above the open and red below, its window over it ("10s") and the
/// window's volume under it. Its colour is as strong as its volume against the stock's typical volume for a window
/// that long (VolumeBrightness): half at typical, full at twice typical, a quarter's opacity when quiet, and in a
/// burst (over 4 times typical) more than that: a glow hugging the body and the wick (not the whole high-low range)
/// that grows with the burst, with a white edge once it's well past. The wick fades by age: each part of it is as
/// visible as the freshest trade that reached it (RollingCandle.WithWicks), so a high or low fades out over its last
/// 5 seconds in the window instead of snapping off when it leaves. It takes the chart symbol's prints as they arrive
/// (Symbol.NewLast) and, about 7 times a second, asks the chart to redraw while trades are in the window, so the
/// wick fades and trades roll out even when nothing trades. Used by the Rolling Candle indicator and by Key Moving
/// Averages, so a Free licence's two indicators a chart can still have it.
/// </summary>
internal sealed class RollingCandleView
{
    // About 7 times a second: often enough that a fading wick looks smooth.
    private const int RedrawEveryMs = 150;
    private static readonly Color Up = Color.FromArgb(38, 166, 154);
    private static readonly Color Down = Color.FromArgb(239, 83, 80);
    private static readonly Color Label = Color.FromArgb(200, 200, 200);

    private readonly Indicator owner;
    private readonly Func<DateTime?> latestBarTime;
    private readonly RollingCandle candle = new(TimeSpan.FromSeconds(10));
    private readonly VolumeBrightness brightness = new(TimeSpan.FromMinutes(5), TimeSpan.FromSeconds(10));
    private readonly object gate = new();
    private Symbol symbol;
    private Timer timer;
    private Look painted = new(RollingBar.None, 0, 0);
    private int seconds = 10;
    private double fullMultiple = 2;
    private double glowMultiple = 4;

    /// <summary>The candle as drawn: its bar, colour alpha, and glow heat in twentieths (so a redraw waits for a visible change).</summary>
    private readonly record struct Look(RollingBar Bar, int Alpha, int Heat);

    /// <param name="owner">The indicator whose chart it draws on.</param>
    /// <param name="latestBarTime">The open time of the chart's latest bar, or null with no bars yet.</param>
    public RollingCandleView(Indicator owner, Func<DateTime?> latestBarTime)
    {
        this.owner = owner;
        this.latestBarTime = latestBarTime;
    }

    /// <summary>
    /// Starts over on this symbol with a window of this many seconds, judged against the typical volume of the last
    /// typicalMinutes: full colour at fullMultiple times typical, glowing beyond glowMultiple.
    /// </summary>
    public void Start(Symbol chartSymbol, int windowSeconds, int typicalMinutes, double fullMultiple, double glowMultiple)
    {
        this.Stop();
        this.seconds = Math.Max(1, windowSeconds);
        this.candle.Window = TimeSpan.FromSeconds(this.seconds);
        this.candle.Clear();
        this.brightness.Window = TimeSpan.FromSeconds(this.seconds);
        this.brightness.Lookback = TimeSpan.FromMinutes(Math.Max(1, typicalMinutes));
        this.brightness.Clear();
        this.fullMultiple = fullMultiple;
        this.glowMultiple = glowMultiple;
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

    private void OnNewLast(Symbol from, Last last)
    {
        long now = DateTime.UtcNow.Ticks;
        this.candle.Add(now, last.Price, last.Size);
        if (last.Price > 0)
            this.brightness.Add(now, last.Size);
    }

    /// <summary>The candle now, its colour's alpha, and its glow.</summary>
    private Look TakeLook()
    {
        long now = DateTime.UtcNow.Ticks;
        var bar = this.candle.At(now);
        if (bar.IsEmpty)
            return new Look(bar, 0, 0);

        double ratio = this.brightness.Ratio(now, bar.Volume);
        int alpha = VolumeBrightness.Alpha(VolumeBrightness.Strength(ratio, this.fullMultiple));
        int heat = (int)Math.Round(VolumeBrightness.Heat(ratio, this.glowMultiple) * 20);
        return new Look(bar, alpha, heat);
    }

    private void OnTimer()
    {
        // While trades are in the window the wick is fading, so it redraws even when the candle's numbers haven't moved.
        var now = this.TakeLook();
        Look shown;
        lock (this.gate)
            shown = this.painted;
        if (now == shown && now.Bar.Trades == 0)
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

        var look = this.TakeLook();
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

        // The wick in segments, each as visible as the freshest trade that reached it, so a high or low fades out as its
        // trade ages instead of snapping off when it leaves the window.
        float top = Math.Min(yOpen, yClose);
        float height = Math.Max(1f, Math.Abs(yClose - yOpen));
        var (_, upper, lower) = this.candle.WithWicks(DateTime.UtcNow.Ticks);
        var wick = new List<(float From, float To, float Seen)>();
        for (int i = 0; i < upper.Count; i++)
        {
            float to = i + 1 < upper.Count ? (float)converter.GetChartY(upper[i + 1].Price) : top;
            wick.Add(((float)converter.GetChartY(upper[i].Price), to, (float)RollingCandle.WickVisibility(upper[i].Fresh)));
        }

        for (int i = 0; i < lower.Count; i++)
        {
            float to = i + 1 < lower.Count ? (float)converter.GetChartY(lower[i + 1].Price) : top + height;
            wick.Add(((float)converter.GetChartY(lower[i].Price), to, (float)RollingCandle.WickVisibility(lower[i].Fresh)));
        }

        var state = graphics.Save();
        try
        {
            graphics.SetClip(area);
            var hue = bar.Close >= bar.Open ? Up : Down;

            // A burst: a glow that follows the candle itself (a rounded halo hugging the body, a thin one along the
            // wick, not a box over the whole range), wider and stronger the bigger the burst, then a white edge on
            // the body once it's well past. Faintest and widest layer first.
            float heat = look.Heat / 20f;
            if (heat > 0)
            {
                graphics.SmoothingMode = SmoothingMode.AntiAlias;
                for (int layer = 3; layer >= 1; layer--)
                {
                    float spread = (1.5f + (4.5f * heat)) * layer / 3f;
                    int glowAlpha = (int)((30 + (80 * heat)) / layer);
                    var glow = Color.FromArgb(glowAlpha, hue);
                    foreach (var (from, to, seen) in wick)
                    {
                        if ((int)(glowAlpha * seen) < 2)
                            continue;
                        using var wickGlow = new Pen(Color.FromArgb((int)(glowAlpha * seen), hue), 1 + spread) { StartCap = LineCap.Round, EndCap = LineCap.Round };
                        graphics.DrawLine(wickGlow, x, from, x, to);
                    }

                    using var bodyGlow = new SolidBrush(glow);
                    using var halo = Rounded(x - (body / 2) - spread, top - spread, body + (2 * spread), height + (2 * spread), spread);
                    graphics.FillPath(bodyGlow, halo);
                }

                graphics.SmoothingMode = SmoothingMode.None;
            }

            var colour = Color.FromArgb(look.Alpha, hue);
            foreach (var (from, to, seen) in wick)
            {
                int alpha = (int)(look.Alpha * seen);
                if (alpha < 2)
                    continue;
                using var wickPen = new Pen(Color.FromArgb(alpha, hue), heat > 0 ? 2 : 1);
                graphics.DrawLine(wickPen, x, from, x, to);
            }

            using var fill = new SolidBrush(colour);
            graphics.FillRectangle(fill, x - body / 2, top, body, height);
            if (heat >= 0.5f)
            {
                using var edge = new Pen(Color.FromArgb(220, Color.White), 1);
                graphics.DrawRectangle(edge, x - body / 2, top, body, height);
            }

            // In a burst the volume reads in the candle's colour, in bold.
            using var font = new Font("Segoe UI", 8f, heat > 0 ? FontStyle.Bold : FontStyle.Regular);
            using var text = new SolidBrush(heat > 0 ? hue : Label);
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

    /// <summary>A rectangle with corners rounded by radius, for a glow that hugs the body rather than boxing it.</summary>
    private static GraphicsPath Rounded(float left, float top, float width, float height, float radius)
    {
        var path = new GraphicsPath();
        float d = Math.Min(Math.Min(width, height), radius * 2);
        if (d < 1)
        {
            path.AddRectangle(new RectangleF(left, top, width, height));
            return path;
        }

        path.AddArc(left, top, d, d, 180, 90);
        path.AddArc(left + width - d, top, d, d, 270, 90);
        path.AddArc(left + width - d, top + height - d, d, d, 0, 90);
        path.AddArc(left, top + height - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }
}

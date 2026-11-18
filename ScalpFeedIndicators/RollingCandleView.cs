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
/// that grows with the burst, with a white edge once it's well past. That colour is the candle's brightest; down its
/// body and wick each price is shaded by the shares traded there (VolumeAtPrice), the busiest price brightest and
/// prices under the least shares (1,000) or with none at a quarter's opacity, so it shows where volume was and
/// wasn't traded. A window under the least shares is all at a quarter, with no glow. It takes the chart symbol's
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
    private readonly VolumeBrightness brightness = new(TimeSpan.FromMinutes(5), TimeSpan.FromSeconds(10));
    private readonly object gate = new();
    private Symbol symbol;
    private Timer timer;
    private Look painted = new(RollingBar.None, 0, 0);
    private int seconds = 10;
    private double fullMultiple = 2;
    private double glowMultiple = 4;
    private double minShares = VolumeAtPrice.DefaultMinShares;

    // What the chart's Symbol.NewLast delivered in the current minute, logged once a minute to Quantower's log, to
    // check against the prints the connection sent for the symbol (your report, 2026-11-18: highs and lows jumped
    // more than a full tape's window would; replaying captures says a full tape steps little).
    private long printsSeen;
    private double sharesSeen;
    private long nextReportTicks;
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
    /// typicalMinutes: full colour at fullMultiple times typical, glowing beyond glowMultiple, and prices (or a
    /// window) under minShares at the lowest intensity.
    /// </summary>
    public void Start(Symbol chartSymbol, int windowSeconds, int typicalMinutes, double fullMultiple, double glowMultiple, double minShares)
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
        this.minShares = Math.Max(0, minShares);
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

        lock (this.gate)
        {
            this.printsSeen++;
            this.sharesSeen += last.Size;
        }
    }

    /// <summary>Once a minute: how many prints and shares Symbol.NewLast brought, into Quantower's log.</summary>
    private void ReportPrints(long nowTicks)
    {
        long prints;
        double shares;
        string name;
        lock (this.gate)
        {
            if (this.nextReportTicks == 0)
                this.nextReportTicks = nowTicks + TimeSpan.TicksPerMinute;
            if (nowTicks < this.nextReportTicks)
                return;

            this.nextReportTicks = nowTicks + TimeSpan.TicksPerMinute;
            prints = this.printsSeen;
            shares = this.sharesSeen;
            this.printsSeen = 0;
            this.sharesSeen = 0;
            name = this.symbol?.Name;
        }

        try
        {
            Core.Instance.Loggers.Log($"Rolling candle {name}: {prints} prints, {shares:0} shares from NewLast in the last minute", LoggingLevel.System);
        }
        catch
        {
            // The count must never stop the candle.
        }
    }

    /// <summary>The candle now, its colour's alpha (its brightest), and its glow; into levels, when given, its shares at each price.</summary>
    private Look TakeLook(List<PriceLevel> levels = null)
    {
        long now = DateTime.UtcNow.Ticks;
        var bar = this.candle.At(now, levels);
        if (bar.IsEmpty)
            return new Look(bar, 0, 0);
        if (!(bar.Volume >= this.minShares))
            return new Look(bar, VolumeBrightness.Alpha(0), 0);

        double ratio = this.brightness.Ratio(now, bar.Volume);
        int alpha = VolumeBrightness.Alpha(VolumeBrightness.Strength(ratio, this.fullMultiple));
        int heat = (int)Math.Round(VolumeBrightness.Heat(ratio, this.glowMultiple) * 20);
        return new Look(bar, alpha, heat);
    }

    private void OnTimer()
    {
        this.ReportPrints(DateTime.UtcNow.Ticks);
        var now = this.TakeLook();
        Look shown;
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

        var levels = new List<PriceLevel>();
        var look = this.TakeLook(levels);
        var bar = look.Bar;
        lock (this.gate)
            this.painted = look;
        if (bar.IsEmpty)
            return;

        var converter = window.CoordinatesConverter;
        Rectangle area = window.ClientRectangle;
        float barWidth = Math.Max(1f, (float)chart.BarsWidth);
        float body = Math.Max(3f, MathF.Round(barWidth * 0.7f));

        // Two bar widths right of the latest bar, kept inside the chart when it's scrolled back or tight on the right.
        float x = this.latestBarTime() is DateTime latest ? (float)converter.GetChartX(latest) + barWidth * 2 : area.Right - body * 2;
        float left = area.Left + body;
        float right = area.Right - body - 2;
        x = MathF.Round(right > left ? Math.Clamp(x, left, right) : area.Right - body);

        // Whole pixel rows, from the one the high is on to the one the low is on, so the shading's slices tile.
        int Row(double price) => (int)Math.Clamp(Math.Floor(converter.GetChartY(price)), area.Top - 1, area.Bottom + 1);
        int rowHigh = Row(bar.High);
        int rowLow = Math.Max(rowHigh, Row(bar.Low));
        int bodyTop = Math.Clamp(Math.Min(Row(bar.Open), Row(bar.Close)), rowHigh, rowLow);
        int bodyBottom = Math.Clamp(Math.Max(Row(bar.Open), Row(bar.Close)), bodyTop, rowLow);
        int[] rows = VolumeAtPrice.RowAlphas(levels, price => converter.GetChartY(price), rowHigh, rowLow, this.minShares, look.Alpha);

        var state = graphics.Save();
        try
        {
            graphics.SetClip(area);
            graphics.SmoothingMode = SmoothingMode.None;
            var hue = bar.Close >= bar.Open ? Up : Down;
            float bodyLeft = x - MathF.Round(body / 2);
            float heat = look.Heat / 20f;
            float wick = Math.Clamp(MathF.Round(body / 4), 1f, 3f) + (heat > 0 ? 1 : 0);
            float wickLeft = x - MathF.Floor(wick / 2);

            // A burst: a glow that follows the candle itself (a rounded halo hugging the body, a thin one along the
            // wick, not a box over the whole range), wider and stronger the bigger the burst. Faintest and widest
            // layer first.
            if (heat > 0)
            {
                graphics.SmoothingMode = SmoothingMode.AntiAlias;
                for (int layer = 3; layer >= 1; layer--)
                {
                    float spread = (1.5f + (4.5f * heat)) * layer / 3f;
                    var glow = Color.FromArgb((int)((30 + (80 * heat)) / layer), hue);
                    using var wickGlow = new Pen(glow, wick + spread) { StartCap = LineCap.Round, EndCap = LineCap.Round };
                    graphics.DrawLine(wickGlow, x, rowHigh, x, rowLow + 1);
                    using var bodyGlow = new SolidBrush(glow);
                    using var halo = Rounded(bodyLeft - spread, bodyTop - spread, body + (2 * spread), bodyBottom + 1 - bodyTop + (2 * spread), spread);
                    graphics.FillPath(bodyGlow, halo);
                }

                graphics.SmoothingMode = SmoothingMode.None;
            }

            // The candle in slices of equal shade: the body's width between the open and the close, the wick's
            // outside them, so the wick doesn't show through the body.
            int start = 0;
            for (int i = 1; i <= rows.Length; i++)
            {
                if (i < rows.Length && rows[i] == rows[start])
                    continue;

                int y0 = rowHigh + start;
                int y1 = rowHigh + i;
                using var fill = new SolidBrush(Color.FromArgb(rows[start], hue));
                int inTop = Math.Max(y0, bodyTop);
                int inBottom = Math.Min(y1, bodyBottom + 1);
                if (inBottom > inTop)
                    graphics.FillRectangle(fill, bodyLeft, inTop, body, inBottom - inTop);
                int above = Math.Min(y1, bodyTop);
                if (above > y0)
                    graphics.FillRectangle(fill, wickLeft, y0, wick, above - y0);
                int below = Math.Max(y0, bodyBottom + 1);
                if (y1 > below)
                    graphics.FillRectangle(fill, wickLeft, below, wick, y1 - below);
                start = i;
            }

            // Well into a burst, a white edge on the body.
            if (heat >= 0.5f)
            {
                using var edge = new Pen(Color.FromArgb(220, Color.White), 1);
                graphics.DrawRectangle(edge, bodyLeft, bodyTop, body - 1, bodyBottom - bodyTop);
            }

            // In a burst the volume reads in the candle's colour, in bold.
            using var font = new Font("Segoe UI", 8f, heat > 0 ? FontStyle.Bold : FontStyle.Regular);
            using var text = new SolidBrush(heat > 0 ? hue : Label);
            string over = $"{this.seconds}s";
            var size = graphics.MeasureString(over, font);
            graphics.DrawString(over, font, text, x - size.Width / 2, rowHigh - size.Height - 1);
            string under = RollingCandle.FormatVolume(bar.Volume);
            size = graphics.MeasureString(under, font);
            graphics.DrawString(under, font, text, x - size.Width / 2, rowLow + 2);
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

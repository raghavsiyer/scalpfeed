using System.Drawing;
using System.Drawing.Drawing2D;
using ScalpFeedVendor.Feed;
using TradingPlatform.BusinessLayer;
using TradingPlatform.BusinessLayer.Chart;

namespace ScalpFeedIndicators;

/// <summary>
/// A circle with an R in it, and the ratio beside it, under the bar of the stock's last reverse split on a daily chart
/// (your request, 2026-11-23). It sits on the bottom edge of the price area, just above the time axis: Quantower's
/// documented chart API has nothing for the axis strip itself. The splits come from split-cache.json, which the
/// ScalpFeed connection fills from Massive (SplitSource). Nothing is drawn on any other timeframe, when the chart
/// doesn't reach back to the last reverse split, or for one announced but still to come.
/// </summary>
internal sealed class ReverseSplitMark
{
    private static readonly Color Ring = Color.FromArgb(255, 152, 0);
    private static readonly Color Inside = Color.FromArgb(43, 30, 10);
    private static readonly Color Back = Color.FromArgb(240, 14, 14, 18);
    private static readonly ReverseSplitFile Splits = new(ReverseSplits.CachePath);
    private string logged;
    private bool failureLogged;

    public void Paint(PaintChartEventArgs args, IChart chart, HistoricalData history, string symbol)
    {
        try
        {
            var window = chart?.MainWindow;
            var graphics = args?.Graphics;
            if (window == null || graphics == null || string.IsNullOrEmpty(symbol))
                return;

            // Which timeframe it took the chart for, and whether it found a split, go in the log too: without them a
            // marker that never draws says nothing about why.
            if (history?.Aggregation is not HistoryAggregationTime time || time.Period != Period.DAY1)
            {
                this.LogOnce($"{symbol} not a daily chart ({history?.Aggregation?.GetType().Name} {(history?.Aggregation as HistoryAggregationTime)?.Period})");
                return;
            }

            var splits = Splits.Of(symbol);
            if (ReverseSplits.Latest(splits, history.Count, i => history[i, SeekOriginHistory.Begin].TimeLeft) is not var (bar, split))
            {
                this.LogOnce($"{symbol} no reverse split to mark: {splits.Count} listed, {history.Count} daily bars");
                return;
            }


            // GetChartX is a bar's left edge (Quantower's DrawOnBars sample), so half a bar more is its middle.
            var barTime = history[bar, SeekOriginHistory.Begin].TimeLeft;
            float x = (float)(window.CoordinatesConverter.GetChartX(barTime) + (chart.BarsWidth / 2.0));
            Rectangle area = window.ClientRectangle;
            Draw(graphics, area, x, split.Ratio);
            this.LogOnce($"{symbol} reverse split {split.Ratio} on {split.Day:yyyy-MM-dd}, under the {barTime:yyyy-MM-dd} bar at x {x:0} of {area}; the paint covers {args.Rectangle}, window {args.WindowIndex}");
        }
        catch (Exception ex)
        {
            // The bars can change under a repaint, and a marker must never stop the chart: the next paint tries again.
            if (!this.failureLogged)
                this.LogOnce($"{symbol} marker not drawn: {ex.GetType().Name} {ex.Message}");
            this.failureLogged = true;
        }
    }

    /// <summary>The circle centred on x along the bottom of area, and the ratio to its right, or left with no room.</summary>
    internal static void Draw(Graphics graphics, Rectangle area, float x, string ratio)
    {
        using var font = new Font("Segoe UI", 9f, FontStyle.Bold);
        using var small = new Font("Segoe UI", 9f, FontStyle.Regular);
        float size = MathF.Ceiling(font.GetHeight(graphics)) + 5;
        if (x + (size / 2) < area.Left || x - (size / 2) > area.Right)
            return;

        var state = graphics.Save();
        try
        {
            graphics.SetClip(area);
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            var circle = new RectangleF(MathF.Round(x - (size / 2)), area.Bottom - size - 2, size, size);
            using var fill = new SolidBrush(Inside);
            using var ring = new Pen(Ring, 1.75f);
            using var text = new SolidBrush(Ring);
            graphics.FillEllipse(fill, circle);
            graphics.DrawEllipse(ring, circle);

            using var centred = new StringFormat(StringFormat.GenericTypographic) { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            graphics.DrawString("R", font, text, circle, centred);

            if (!string.IsNullOrEmpty(ratio))
            {
                // On a dark box of its own, so it reads over candles and volume (your request, 2026-11-24).
                var box = new RectangleF(circle.Right + 3, circle.Top + 1.5f, graphics.MeasureString(ratio, small, PointF.Empty, centred).Width + 9, size - 3);
                if (box.Right > area.Right)
                    box.X = circle.Left - 3 - box.Width;
                using var back = new SolidBrush(Back);
                using var shape = Rounded(box, 4);
                graphics.FillPath(back, shape);
                graphics.DrawString(ratio, small, text, box, centred);
            }
        }
        finally
        {
            graphics.Restore(state);
        }
    }

    private static GraphicsPath Rounded(RectangleF box, float radius)
    {
        var path = new GraphicsPath();
        float d = radius * 2;
        path.AddArc(box.Left, box.Top, d, d, 180, 90);
        path.AddArc(box.Right - d, box.Top, d, d, 270, 90);
        path.AddArc(box.Right - d, box.Bottom - d, d, d, 0, 90);
        path.AddArc(box.Left, box.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    /// <summary>Once per stock, into Quantower's log: where it went, to check against the chart.</summary>
    private void LogOnce(string what)
    {
        // The same stock again only when what's said of it changes, as when the splits arrive after the chart.
        string about = string.Join(' ', what.Split(' ').Take(3));
        if (about == this.logged)
            return;

        this.logged = about;
        try
        {
            Core.Instance.Loggers.Log($"Key Moving Averages: {what}", LoggingLevel.System);
        }
        catch
        {
            // Logging must never stop the chart.
        }
    }
}

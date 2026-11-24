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
        using var font = new Font("Segoe UI", 7.5f, FontStyle.Bold);
        using var small = new Font("Segoe UI", 7.5f, FontStyle.Regular);
        float size = MathF.Ceiling(font.GetHeight(graphics)) + 3;
        if (x + (size / 2) < area.Left || x - (size / 2) > area.Right)
            return;

        var state = graphics.Save();
        try
        {
            graphics.SetClip(area);
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            var circle = new RectangleF(MathF.Round(x - (size / 2)), area.Bottom - size - 2, size, size);
            using var fill = new SolidBrush(Inside);
            using var ring = new Pen(Ring, 1.5f);
            using var text = new SolidBrush(Ring);
            graphics.FillEllipse(fill, circle);
            graphics.DrawEllipse(ring, circle);

            using var centred = new StringFormat(StringFormat.GenericTypographic) { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            graphics.DrawString("R", font, text, circle, centred);

            if (!string.IsNullOrEmpty(ratio))
            {
                var width = graphics.MeasureString(ratio, small, PointF.Empty, centred).Width;
                float left = circle.Right + 3;
                if (left + width > area.Right)
                    left = circle.Left - 3 - width;
                graphics.DrawString(ratio, small, text, new RectangleF(left, circle.Top, width + 1, size), centred);
            }
        }
        finally
        {
            graphics.Restore(state);
        }
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

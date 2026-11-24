using System.Drawing;
using System.Drawing.Drawing2D;
using ScalpFeedVendor.Feed;
using TradingPlatform.BusinessLayer;
using TradingPlatform.BusinessLayer.Chart;

namespace ScalpFeedIndicators;

/// <summary>
/// Circles along the bottom of a daily chart, under the bars they belong to: an orange R, with the ratio beside it,
/// for the stock's last reverse split (your request, 2026-11-23), and a pink O for each offering prospectus it filed
/// (2026-11-24; Offerings says what those are), or a number where that many would overlap. An O that would cover the
/// split's goes a row up. They sit on the bottom edge of the price area, just above the time axis: a chart's paint
/// covers the price area alone (Quantower's log, 2026-11-24), and its documented API has nothing for the axis. The
/// ScalpFeed connection fetches both from Massive (SplitSource, OfferingSource). Nothing is drawn on any other
/// timeframe, for days the chart doesn't reach back to, or for a split announced but still to come.
/// </summary>
internal sealed class DailyMarks
{
    private static readonly Color SplitRing = Color.FromArgb(255, 152, 0);
    private static readonly Color SplitInside = Color.FromArgb(43, 30, 10);
    private static readonly Color OfferingRing = Color.FromArgb(255, 96, 128);
    private static readonly Color OfferingInside = Color.FromArgb(48, 16, 24);
    private static readonly Color Back = Color.FromArgb(240, 14, 14, 18);
    private static readonly WatchedFile<ReverseSplits> Splits = new(ReverseSplits.CachePath, ReverseSplits.Read);
    private static readonly WatchedFile<Offerings> Filings = new(Offerings.CachePath, Offerings.Read);

    // Which bars the offerings are under, worked out again only when the stock, its list or the bars change.
    private (IReadOnlyList<DateTime> Days, int Count, DateTime First) placed;
    private int[] offeringBars = [];
    private string logged;

    public void Paint(PaintChartEventArgs args, IChart chart, HistoricalData history, string symbol, bool showSplit, bool showOfferings)
    {
        try
        {
            var window = chart?.MainWindow;
            var graphics = args?.Graphics;
            if (window == null || graphics == null || string.IsNullOrEmpty(symbol))
                return;

            // Which timeframe it took the chart for, and what it found, go in the log too: without them marks that
            // never draw say nothing about why.
            if (history?.Aggregation is not HistoryAggregationTime time || time.Period != Period.DAY1)
            {
                this.LogOnce(symbol, () => $"{symbol} not a daily chart ({history?.Aggregation?.GetType().Name} {(history?.Aggregation as HistoryAggregationTime)?.Period})");
                return;
            }

            DateTime TimeOf(int bar) => history[bar, SeekOriginHistory.Begin].TimeLeft;

            // GetChartX is a bar's left edge (Quantower's DrawOnBars sample), so half a bar more is its middle.
            float Middle(int bar) => (float)(window.CoordinatesConverter.GetChartX(TimeOf(bar)) + (chart.BarsWidth / 2.0));

            int count = history.Count;
            var split = showSplit ? ReverseSplits.Latest(Splits.Value.Of(symbol), count, TimeOf) : null;
            int[] offerings = showOfferings ? this.OfferingBars(Filings.Value.Of(symbol), count, TimeOf) : [];
            (float X, string Ratio)? splitMark = split is var (bar, s) ? (Middle(bar), s.Ratio) : null;
            Rectangle area = window.ClientRectangle;
            Draw(graphics, area, splitMark, offerings.Select(Middle));
            this.LogOnce($"{symbol} {split?.Split.Day:yyyyMMdd} {offerings.Length}", () =>
                $"{symbol} daily marks: {(split is var (_, r) ? $"reverse split {r.Ratio} on {r.Day:yyyy-MM-dd} at x {splitMark?.X:0}" : "no reverse split")}, {offerings.Length} offerings, {count} bars, area {area}");
        }
        catch (Exception ex)
        {
            // The bars can change under a repaint, and a marker must never stop the chart: the next paint tries again.
            this.LogOnce($"{symbol} failed", () => $"{symbol} daily marks not drawn: {ex.GetType().Name} {ex.Message}");
        }
    }

    private int[] OfferingBars(IReadOnlyList<DateTime> days, int count, Func<int, DateTime> timeOf)
    {
        var now = (days, count, count > 0 ? timeOf(0) : default);
        if (now != this.placed)
        {
            this.offeringBars = [.. days.Select(d => ReverseSplits.BarFor(d, count, timeOf)).Where(b => b >= 0).Distinct()];
            this.placed = now;
        }

        return this.offeringBars;
    }

    /// <summary>The marks along the bottom of area: the split's, and the offerings', each centred on its place across.</summary>
    internal static void Draw(Graphics graphics, Rectangle area, (float X, string Ratio)? split, IEnumerable<float> offerings)
    {
        using var font = new Font("Segoe UI", 9f, FontStyle.Bold);
        using var small = new Font("Segoe UI", 9f, FontStyle.Regular);
        float size = MathF.Ceiling(font.GetHeight(graphics)) + 5;
        bool Shows(float x) => x + (size / 2) >= area.Left && x - (size / 2) <= area.Right;

        var state = graphics.Save();
        try
        {
            graphics.SetClip(area);
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using var centred = new StringFormat(StringFormat.GenericTypographic) { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            RectangleF Place(float x, int row) => new(MathF.Round(x - (size / 2)), area.Bottom - 2 - size - (row * (size + 3)), size, size);

            void Circle(RectangleF at, string letter, Color colour, Color inside)
            {
                using var fill = new SolidBrush(inside);
                using var ring = new Pen(colour, 1.75f);
                using var text = new SolidBrush(colour);
                graphics.FillEllipse(fill, at);
                graphics.DrawEllipse(ring, at);
                graphics.DrawString(letter, font, text, at, centred);
            }

            RectangleF? taken = null;
            if (split is var (splitX, ratio) && Shows(splitX))
            {
                var circle = Place(splitX, 0);
                Circle(circle, "R", SplitRing, SplitInside);
                taken = circle;
                if (!string.IsNullOrEmpty(ratio))
                {
                    // On a dark box of its own, to the right or left with no room, so it reads over candles and
                    // volume (your request, 2026-11-24).
                    var box = new RectangleF(circle.Right + 3, circle.Top + 1.5f, graphics.MeasureString(ratio, small, PointF.Empty, centred).Width + 9, size - 3);
                    if (box.Right > area.Right)
                        box.X = circle.Left - 3 - box.Width;
                    using var back = new SolidBrush(Back);
                    using var shape = Rounded(box, 4);
                    using var text = new SolidBrush(SplitRing);
                    graphics.FillPath(back, shape);
                    graphics.DrawString(ratio, small, text, box, centred);
                    taken = RectangleF.Union(circle, box);
                }
            }

            foreach (var (x, many) in Offerings.Group(offerings.Where(Shows), size + 1))
            {
                var at = Place(x, 0);
                if (taken is RectangleF t && t.IntersectsWith(at))
                    at = Place(x, 1);
                Circle(at, many == 1 ? "O" : Math.Min(many, 99).ToString(), OfferingRing, OfferingInside);
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

    /// <summary>Into Quantower's log, again only when the key changes: a stock, its split or how many offerings.</summary>
    private void LogOnce(string key, Func<string> what)
    {
        if (key == this.logged)
            return;

        this.logged = key;
        try
        {
            Core.Instance.Loggers.Log($"Key Moving Averages: {what()}", LoggingLevel.System);
        }
        catch
        {
            // Logging must never stop the chart.
        }
    }
}

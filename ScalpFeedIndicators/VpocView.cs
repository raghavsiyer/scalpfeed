using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using ScalpFeedVendor.Feed;
using TradingPlatform.BusinessLayer;
using TradingPlatform.BusinessLayer.Chart;

namespace ScalpFeedIndicators;

/// <summary>How a dynamic VPOC is drawn.</summary>
internal readonly record struct VpocLook(Color Vpoc, int VpocWidth, bool ShowValueArea, Color ValueArea);

/// <summary>A bar's place across the chart, the session it's in, and where its lines cross it; NaN where it has none.</summary>
internal readonly record struct VpocBar(float Left, float Right, long Session, float Poc, float High, float Low);

/// <summary>
/// The dynamic VPOC (DynamicVpoc) on an intraday price chart: a stepped line at the price with the most shares traded
/// so far in the session, dotted ones at the edges of its value area, each bar showing them as they were at its end,
/// and the price itself in a dark box at the right. The rows it's worked out from are the connection's, not the chart's,
/// so every timeframe shows the same one; they're a minute long, so a bar shorter than that shows the last whole
/// minute's, and only the latest bar a running one's. They're brought up to the whole market's volume with the same
/// 1-minute bars a chart loads, which are IBKR's.
/// </summary>
internal sealed class VpocView(Indicator owner)
{
    private static readonly Color Back = Color.FromArgb(225, 14, 14, 18);

    private VpocSettings settings;
    private VpocLook look;
    private VpocSource source;
    private string logged;

    // Kept from paint to paint rather than made each time; a chart paints one at a time.
    private readonly List<VpocBar> bars = new();

    /// <param name="settings">A step of 0 for one that suits the stock's price.</param>
    public void Start(VpocSettings settings, VpocLook look)
    {
        this.Stop();
        this.settings = settings;
        this.look = look;
    }

    public void Stop()
    {
        var ending = this.source;
        this.source = null;
        this.settings = null;
        if (ending == null)
            return;

        ending.Changed -= this.Redraw;
        ending.Close();
    }

    public void Paint(PaintChartEventArgs args)
    {
        var symbol = owner.Symbol;
        try
        {
            var chart = owner.CurrentChart;
            var window = chart?.MainWindow;
            var graphics = args?.Graphics;
            var history = owner.HistoricalData;
            if (window == null || graphics == null || symbol == null || this.settings == null || history == null || history.Count == 0
                || history.Aggregation is not HistoryAggregationTime time || !Intraday(time.Period))
                return;

            this.Take(symbol, history);
            var points = this.source.Points;
            if (points.Length == 0)
            {
                this.LogOnce($"{symbol.Name} none {VpocSource.Offered} {this.source.Arrived}", () => !VpocSource.Offered
                    ? $"{symbol.Name} VPOC waiting: the connection that's loaded has no volume at price (restart Quantower for one that has)"
                    : $"{symbol.Name} VPOC waiting: {(this.source.Arrived ? "no shares traded yet" : "volume at price is loading")}");
                return;
            }

            var converter = window.CoordinatesConverter;
            Rectangle area = window.ClientRectangle;
            int count = history.Count;
            int from = Math.Max(0, (int)converter.GetBarIndex(converter.GetTime(area.Left)) - 1);
            int to = Math.Min(count - 1, (int)Math.Ceiling(converter.GetBarIndex(converter.GetTime(area.Right))) + 1);
            float width = chart.BarsWidth;
            double length = time.Period.Duration.TotalMilliseconds;
            this.bars.Clear();
            for (int i = from; i <= to; i++)
            {
                var start = history[i, SeekOriginHistory.Begin].TimeLeft;
                long end = new DateTimeOffset(DateTime.SpecifyKind(start, DateTimeKind.Utc)).ToUnixTimeMilliseconds() + (long)length;
                float left = (float)converter.GetChartX(start);
                var at = DynamicVpoc.At(points, end, running: i == count - 1);
                this.bars.Add(at is VpocPoint p
                    ? new VpocBar(left, left + width, p.SessionStartMs, (float)converter.GetChartY(p.Poc), (float)converter.GetChartY(p.High), (float)converter.GetChartY(p.Low))
                    : new VpocBar(left, left + width, 0, float.NaN, float.NaN, float.NaN));
            }

            var latest = points[^1];
            Draw(graphics, area, this.bars, this.look, $"VPOC {symbol.FormatPrice(latest.Poc)}", (float)converter.GetChartY(latest.Poc));
            this.LogOnce($"{symbol.Name} drawn {Math.Min(this.source.MarketReads, 2)}", () => $"{symbol.Name} VPOC {latest.Poc} value area {latest.Low} to {latest.High} on {time}, by {this.source.Step}, {this.Counted()}, {points.Length} points from " +
                $"{DateTimeOffset.FromUnixTimeMilliseconds(points[0].SessionStartMs):MM-dd HH:mm} UTC, {this.bars.Count(b => !float.IsNaN(b.Poc))} of {this.bars.Count} bars on screen, in {ColorTranslator.ToHtml(this.look.Vpoc)} and {ColorTranslator.ToHtml(this.look.ValueArea)}");
        }
        catch (Exception ex)
        {
            // The bars can change under a repaint, and a line must never stop the chart: the next paint tries again.
            this.LogOnce($"{symbol?.Name} failed", () => $"{symbol?.Name} VPOC not drawn: {ex.GetType().Name} {ex.Message}");
        }
    }

    /// <summary>The lines over the bars, and the label at labelY by the right edge when that's on the chart.</summary>
    internal static void Draw(Graphics graphics, Rectangle area, IReadOnlyList<VpocBar> bars, VpocLook look, string label, float labelY)
    {
        var state = graphics.Save();
        try
        {
            graphics.SetClip(area);

            // Level lines a pixel wide stay sharp.
            graphics.SmoothingMode = SmoothingMode.None;
            if (look.ShowValueArea)
            {
                using var dotted = new Pen(look.ValueArea, 1) { DashStyle = DashStyle.Dot };
                Steps(graphics, area, bars, b => b.High, dotted);
                Steps(graphics, area, bars, b => b.Low, dotted);
            }

            using var pen = new Pen(look.Vpoc, look.VpocWidth) { LineJoin = LineJoin.Miter };
            Steps(graphics, area, bars, b => b.Poc, pen);

            if (labelY < area.Top || labelY > area.Bottom)
                return;

            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using var font = new Font("Segoe UI", 8.25f, FontStyle.Regular);
            using var centred = new StringFormat(StringFormat.GenericTypographic) { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            var size = graphics.MeasureString(label, font, PointF.Empty, centred);
            var box = new RectangleF(area.Right - size.Width - 13, labelY - (size.Height / 2) - 1.5f, size.Width + 9, size.Height + 3);
            box.Y = Math.Clamp(box.Y, area.Top, Math.Max(area.Top, area.Bottom - box.Height));
            using var back = new SolidBrush(Back);
            using var text = new SolidBrush(look.Vpoc);
            graphics.FillRectangle(back, box);
            graphics.DrawString(label, font, text, box, centred);
        }
        finally
        {
            graphics.Restore(state);
        }
    }

    private static bool Intraday(Period period) => period.BasePeriod is BasePeriod.Second or BasePeriod.Minute or BasePeriod.Hour;

    /// <summary>
    /// One line: level over each bar, straight up or down between two, and broken where a bar has none and where a
    /// session starts over, as the VWAP's is.
    /// </summary>
    private static void Steps(Graphics graphics, Rectangle area, IReadOnlyList<VpocBar> bars, Func<VpocBar, float> value, Pen pen)
    {
        var path = new List<PointF>();
        long session = 0;
        void Flush()
        {
            if (path.Count > 1)
                graphics.DrawLines(pen, CollectionsMarshal.AsSpan(path));
            path.Clear();
        }

        foreach (var bar in bars)
        {
            float y = value(bar);
            if (float.IsNaN(y) || bar.Session != session)
                Flush();
            session = bar.Session;
            if (float.IsNaN(y))
                continue;

            // Far enough off the chart to be the same, and not so far that GDI+ can't draw it.
            y = MathF.Round(Math.Clamp(y, area.Top - 5000f, area.Bottom + 5000f));
            if (path.Count > 1 && path[^1].Y == y)
            {
                path[^1] = new PointF(bar.Right, y);
                continue;
            }

            path.Add(new PointF(path.Count > 0 ? path[^1].X : bar.Left, y));
            path.Add(new PointF(bar.Right, y));
        }

        Flush();
    }

    /// <summary>The source for the chart's stock, at its first paint: its price says what step suits it.</summary>
    private void Take(Symbol symbol, HistoricalData history)
    {
        if (this.source != null)
            return;

        double price = history[history.Count - 1, SeekOriginHistory.Begin][PriceType.Close];
        var asked = this.settings.Step > 0 ? this.settings : this.settings with { Step = DynamicVpoc.AutoStep(price) };
        this.source = VpocSource.For(VolumeAtPriceShare.SymbolOf(symbol.Id), asked, (fromMs, toMs) => Minutes(symbol, fromMs, toMs));
        this.source.Changed += this.Redraw;
    }

    /// <summary>The bars a 1-minute chart loads, not live: IBKR's, over tastytrade's for the hours it hasn't got.</summary>
    private static RangeBar[] Minutes(Symbol symbol, long fromMs, long toMs)
    {
        static DateTime Utc(long ms) => DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime;
        using var history = symbol.GetHistory(Period.MIN1, symbol.HistoryType, Utc(fromMs), Utc(toMs));
        return RangeBars.Read(history, fromMs, toMs).Bars;
    }

    /// <summary>For the log: what the latest session's shares are.</summary>
    private string Counted()
    {
        var (seen, counted) = this.source.Shares();
        return this.source.MarketReads == 0
            ? $"{seen:0} shares by price, the whole market's minutes not in"
            : $"{seen:0} shares by price brought up to {counted:0} by the whole market's minutes to {DateTimeOffset.FromUnixTimeMilliseconds(this.source.MarketToMs):HH:mm} UTC";
    }

    private void Redraw()
    {
        try
        {
            owner.CurrentChart?.RedrawBuffer();
        }
        catch (Exception)
        {
            // A chart closing as it changes; the next change, or none, will do.
        }
    }

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

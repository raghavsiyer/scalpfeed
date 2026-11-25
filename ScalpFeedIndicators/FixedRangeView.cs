using System.Drawing;
using System.Drawing.Drawing2D;
using ScalpFeedVendor.Feed;
using TradingPlatform.BusinessLayer;
using TradingPlatform.BusinessLayer.Chart;
using TradingPlatform.BusinessLayer.Native;

namespace ScalpFeedIndicators;

/// <summary>How fixed range profiles are worked out and drawn; the settings FRVP has, with its defaults.</summary>
internal sealed record FixedRangeLook(
    int Size,
    bool TicksEach,
    double ValueAreaShare,
    int WidthPercent,
    bool OnRight,
    Color Up,
    Color Down,
    Color ValueUp,
    Color ValueDown,
    bool ShowPoc,
    Color Poc,
    bool ShowVah,
    bool ShowVal,
    Color Value,
    Color Box);

/// <summary>One row across the chart: its up and down volume against the fullest row's, and whether it's in the value area.</summary>
internal readonly record struct FixedRangeRow(float Top, float Bottom, float Up, float Down, bool InValueArea);

/// <summary>
/// A range as it's drawn; no rows while its bars are loading. Edge is the one under the pointer or being moved: -1 the
/// start, 1 the end.
/// </summary>
internal sealed record FixedRangeMark(float Left, float Right, IReadOnlyList<FixedRangeRow> Rows, float PocY, float HighY, float LowY, string Label, bool Hovered, int Edge, bool Placing, bool ExtendRight)
{
    public float Top => this.Rows.Count > 0 ? this.Rows[^1].Top : float.NegativeInfinity;

    public float Bottom => this.Rows.Count > 0 ? this.Rows[0].Bottom : float.PositiveInfinity;
}

/// <summary>
/// TradingView's fixed range volume profile on a price chart of any time-based kind (your request, 2026-11-25;
/// FixedProfile is how it's worked out, and the look is its help pages' screenshots'). The FRVP button in the top right
/// corner starts one: click its first candle, then its last. The button again, or the right button, calls it off. Over a
/// range, its edges drag to other candles, and it has an arrow for FRVP's Extend right and a cross to remove it. A range
/// is on every chart of the stock (FixedRanges). Clicks meant for these are marked handled, so the chart does nothing
/// else with them; no other click is touched.
/// </summary>
internal sealed class FixedRangeView(Indicator owner)
{
    private const int EdgeReach = 4;
    private const long Minute = 60_000;

    // IBKR gives 1-second bars for the last 30 minutes, and tastytrade's, for before, count only part of the market.
    private const long SecondsBack = 30 * Minute;
    private const int LiveEveryMs = 500;
    private static readonly Color Back = Color.FromArgb(225, 14, 14, 18);
    private static readonly Color Quiet = Color.FromArgb(120, 130, 145);
    private static readonly Color Anchor = Color.FromArgb(41, 98, 255);
    private static readonly Color Lit = Color.FromArgb(235, 235, 235);
    private static readonly Size ButtonSize = new(40, 18);

    private enum Doing
    {
        Nothing,
        First,
        Last,
        Dragging
    }

    private sealed record Kept(FixedRange Range, long EndMs, string From, RangeRead Read, bool SingleBar, FixedProfile Profile, long At);

    private sealed record Place(long Id, RectangleF Box, RectangleF Cross, RectangleF Arrow);

    private readonly object gate = new();
    private readonly Dictionary<long, Kept> profiles = new();
    private readonly Dictionary<Period, RangeBars> sources = new();
    private readonly List<Place> places = new();
    private FixedRangeLook look;
    private IChart hooked;
    private string symbolName;
    private RectangleF button;
    private Rectangle area;
    private Doing doing;
    private FixedRange dragged;
    private int draggedEdge;
    private (long StartMs, long EndMs) first;
    private (long StartMs, long EndMs)? under;
    private long hoveredId;
    private int hoveredEdge;
    private bool swallow;
    private bool started;
    private string logged;

    public void Start(FixedRangeLook look)
    {
        this.Stop();
        this.look = look;
        this.started = true;
        FixedRanges.Shared.Changed += this.OnRangesChanged;
    }

    public void Stop()
    {
        FixedRanges.Shared.Changed -= this.OnRangesChanged;
        this.Hook(null);
        RangeBars[] ending;
        lock (this.gate)
        {
            this.started = false;
            this.doing = Doing.Nothing;
            this.profiles.Clear();
            this.places.Clear();
            this.button = RectangleF.Empty;
            this.hoveredId = 0;
            this.symbolName = null;
            ending = [.. this.sources.Values];
            this.sources.Clear();
        }

        foreach (var source in ending)
        {
            source.Loaded -= this.Redraw;
            source.Close();
        }
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
            if (!this.started || window == null || graphics == null || symbol == null || history == null || history.Count == 0
                || history.Aggregation is not HistoryAggregationTime time)
                return;

            this.Hook(chart);
            var at = window.ClientRectangle;
            Doing now;
            FixedRange? placing = null;
            string name;
            lock (this.gate)
            {
                name = this.symbolName = VolumeAtPriceShare.SymbolOf(symbol.Id);
                this.area = at;
                this.button = new RectangleF(at.Right - ButtonSize.Width - 6, at.Top + 6, ButtonSize.Width, ButtonSize.Height);
                now = this.doing;
                if (now is Doing.Last or Doing.Dragging && this.under is var (startMs, endMs))
                    placing = this.Between(startMs, endMs);
            }

            var marks = new List<(FixedRange Range, FixedRangeMark Mark)>();
            foreach (var range in FixedRanges.Shared.Of(name))
            {
                bool moving = now == Doing.Dragging && placing is { } moved && moved.Id == range.Id;
                if (this.MarkOf(moving ? placing.Value : range, window, chart, history, time.Period, symbol, moving) is { } mark)
                    marks.Add((range, mark));
            }

            if (now == Doing.Last && placing is { } drawn && this.MarkOf(drawn, window, chart, history, time.Period, symbol, true) is { } preview)
                marks.Add((drawn, preview));

            lock (this.gate)
            {
                this.places.Clear();
                foreach (var (range, mark) in marks)
                {
                    if (range.Id != 0 && mark.Rows.Count > 0)
                        this.places.Add(new Place(range.Id, RectangleF.FromLTRB(mark.Left, mark.Top, mark.Right, mark.Bottom), CrossOf(mark, at), ArrowOf(mark, at)));
                }
            }

            foreach (var (_, mark) in marks)
                Draw(graphics, at, mark, this.look);
            DrawButton(graphics, at, this.button, now switch { Doing.First => "click the first candle", Doing.Last => "click the last candle", _ => null });
            this.LogOnce($"{name} {marks.Count} {marks.Count(m => m.Mark.Rows.Count > 0)} {now}", () => this.Says(name, time, marks, now, at));
        }
        catch (Exception ex)
        {
            // The bars can change under a repaint, and a profile must never stop the chart: the next paint tries again.
            this.LogOnce($"{symbol?.Name} failed {ex.GetType().Name}", () => $"{symbol?.Name} FRVP not drawn: {ex.GetType().Name} {ex.Message}");
        }
    }

    /// <summary>One profile: its box, its rows (up volume, then down) from the side they're placed on, and its lines.</summary>
    internal static void Draw(Graphics graphics, Rectangle area, FixedRangeMark mark, FixedRangeLook look)
    {
        var state = graphics.Save();
        try
        {
            graphics.SetClip(area);
            graphics.SmoothingMode = SmoothingMode.None;

            // Off the chart by enough to be the same, and not by so much that GDI+ can't draw it.
            static float Near(float at, float low, float high) => Math.Clamp(at, low - 3000, high + 3000);
            float left = MathF.Round(Near(mark.Left, area.Left, area.Right)), right = MathF.Round(Near(mark.Right, area.Left, area.Right));
            float top = MathF.Round(Near(mark.Top, area.Top, area.Bottom)), bottom = MathF.Round(Near(mark.Bottom, area.Top, area.Bottom));
            float width = Math.Max(1, right - left);
            using (var box = new SolidBrush(look.Box))
                graphics.FillRectangle(box, left, top, width, Math.Max(1, bottom - top));

            using var up = new SolidBrush(look.Up);
            using var down = new SolidBrush(look.Down);
            using var valueUp = new SolidBrush(look.ValueUp);
            using var valueDown = new SolidBrush(look.ValueDown);
            float longest = width * look.WidthPercent / 100f;
            foreach (var row in mark.Rows)
            {
                if (row.Bottom < area.Top || row.Top > area.Bottom)
                    continue;

                float from = MathF.Round(Near(row.Top, area.Top, area.Bottom)), to = MathF.Round(Near(row.Bottom, area.Top, area.Bottom));
                float height = Math.Max(1, to - from - (to - from >= 3 ? 1 : 0));
                float ups = longest * row.Up, downs = longest * row.Down;
                if (look.OnRight)
                {
                    graphics.FillRectangle(row.InValueArea ? valueUp : up, right - ups, from, ups, height);
                    graphics.FillRectangle(row.InValueArea ? valueDown : down, right - ups - downs, from, downs, height);
                }
                else
                {
                    graphics.FillRectangle(row.InValueArea ? valueUp : up, left, from, ups, height);
                    graphics.FillRectangle(row.InValueArea ? valueDown : down, left + ups, from, downs, height);
                }
            }

            void Level(float at, Color colour, float thick)
            {
                using var pen = new Pen(colour, thick);
                float y = MathF.Round(Near(at, area.Top, area.Bottom));
                graphics.DrawLine(pen, left, y, right, y);
            }

            if (mark.Rows.Count > 0)
            {
                if (look.ShowVah)
                    Level(mark.HighY, look.Value, 1);
                if (look.ShowVal)
                    Level(mark.LowY, look.Value, 1);
                if (look.ShowPoc)
                    Level(mark.PocY, look.Poc, 2);
            }

            if (!mark.Hovered && !mark.Placing)
                return;

            for (int edge = -1; edge <= 1; edge += 2)
            {
                float x = edge < 0 ? left : right;
                using var pen = new Pen(Color.FromArgb(mark.Edge == edge ? 255 : 130, Anchor), 1) { DashStyle = mark.Edge == edge ? DashStyle.Solid : DashStyle.Dot };
                graphics.DrawLine(pen, x, Math.Max(top, area.Top), x, Math.Min(bottom, area.Bottom));
            }

            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using var ring = new Pen(Anchor, 1.5f);
            using var hole = new SolidBrush(Back);
            float high = Math.Clamp(top, area.Top + 5, Math.Max(area.Top + 5, area.Bottom - 5));
            foreach (float x in (float[])[left, right])
            {
                graphics.FillEllipse(hole, x - 4, high - 4, 8, 8);
                graphics.DrawEllipse(ring, x - 4, high - 4, 8, 8);
            }

            if (mark.Rows.Count > 0)
                DrawLabel(graphics, area, mark, look, right);
            if (mark.Hovered && !mark.Placing)
                DrawTools(graphics, area, mark);
        }
        finally
        {
            graphics.Restore(state);
        }
    }

    /// <summary>The button, lit while a range is being placed, with what to do next beside it.</summary>
    internal static void DrawButton(Graphics graphics, Rectangle area, RectangleF button, string next)
    {
        var state = graphics.Save();
        try
        {
            graphics.SetClip(area);
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using var font = new Font("Segoe UI", 8.25f, FontStyle.Regular);
            using var centred = Centred();
            using var back = new SolidBrush(Back);
            var colour = next == null ? Quiet : Lit;
            using var text = new SolidBrush(colour);
            using var edge = new Pen(Color.FromArgb(next == null ? 70 : 200, colour), 1);
            graphics.FillRectangle(back, button);
            graphics.DrawRectangle(edge, button.X, button.Y, button.Width, button.Height);
            graphics.DrawString("FRVP", font, text, button, centred);
            if (next == null)
                return;

            var size = graphics.MeasureString(next, font, PointF.Empty, centred);
            var beside = new RectangleF(button.Left - size.Width - 18, button.Top, size.Width + 12, button.Height);
            graphics.FillRectangle(back, beside);
            graphics.DrawString(next, font, text, beside, centred);
        }
        finally
        {
            graphics.Restore(state);
        }
    }

    /// <summary>The POC's price, while the pointer is over the range.</summary>
    private static void DrawLabel(Graphics graphics, Rectangle area, FixedRangeMark mark, FixedRangeLook look, float right)
    {
        float y = MathF.Round(mark.PocY);
        if (y < area.Top || y > area.Bottom)
            return;

        using var font = new Font("Segoe UI", 8.25f, FontStyle.Regular);
        using var centred = Centred();
        var size = graphics.MeasureString(mark.Label, font, PointF.Empty, centred);
        var box = new RectangleF(0, y - size.Height - 5, size.Width + 8, size.Height + 2);
        box.X = Math.Clamp(right + 4, area.Left + 3, Math.Max(area.Left + 3, area.Right - box.Width - 3));
        if (box.Top < area.Top)
            box.Y = y + 4;
        using var back = new SolidBrush(Back);
        using var text = new SolidBrush(look.Poc);
        graphics.FillRectangle(back, box);
        graphics.DrawString(mark.Label, font, text, box, centred);
    }

    private static void DrawTools(Graphics graphics, Rectangle area, FixedRangeMark mark)
    {
        using var back = new SolidBrush(Back);
        using var plain = new Pen(Color.FromArgb(205, 210, 220), 1.5f);
        using var on = new Pen(Anchor, 2);
        var cross = CrossOf(mark, area);
        graphics.FillRectangle(back, cross);
        graphics.DrawLine(plain, cross.Left + 4, cross.Top + 4, cross.Right - 4, cross.Bottom - 4);
        graphics.DrawLine(plain, cross.Left + 4, cross.Bottom - 4, cross.Right - 4, cross.Top + 4);

        var arrow = ArrowOf(mark, area);
        var pen = mark.ExtendRight ? on : plain;
        float middle = arrow.Top + (arrow.Height / 2);
        graphics.FillRectangle(back, arrow);
        graphics.DrawLine(pen, arrow.Left + 3, middle, arrow.Right - 4, middle);
        graphics.DrawLine(pen, arrow.Right - 8, middle - 4, arrow.Right - 4, middle);
        graphics.DrawLine(pen, arrow.Right - 8, middle + 4, arrow.Right - 4, middle);
    }

    private static StringFormat Centred() => new(StringFormat.GenericTypographic) { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };

    private static RectangleF CrossOf(FixedRangeMark mark, Rectangle area) => ToolOf(mark, area, 0);

    private static RectangleF ArrowOf(FixedRangeMark mark, Rectangle area) => ToolOf(mark, area, 1);

    /// <summary>The tools over a range's top right corner, inside the chart, from the right.</summary>
    private static RectangleF ToolOf(FixedRangeMark mark, Rectangle area, int place)
    {
        const float Size = 16;
        float from = Math.Clamp(mark.Right - Size - 8, area.Left + 4 + Size, Math.Max(area.Left + 4 + Size, area.Right - Size - 4));
        float y = Math.Clamp(mark.Top - Size - 8, area.Top + 28, Math.Max(area.Top + 28, area.Bottom - Size - 2));
        return new RectangleF(from - (place * (Size + 2)), y, Size, Size);
    }

    /// <summary>A range as it's drawn; null when it's off the bars, or has nothing to show yet and isn't being placed.</summary>
    private FixedRangeMark MarkOf(FixedRange range, IChartWindow window, IChart chart, HistoricalData history, Period period, Symbol symbol, bool placing)
    {
        int count = history.Count;
        long TimeOf(int i) => RangeBars.Ms(history[i, SeekOriginHistory.Begin].TimeLeft);

        // The first bar that starts at or after a time, or past the last.
        int AtOrAfter(long ms)
        {
            int low = 0, high = count;
            while (low < high)
            {
                int middle = (low + high) / 2;
                if (TimeOf(middle) < ms)
                    low = middle + 1;
                else
                    high = middle;
            }

            return low;
        }

        // From the bar its start is in to the one its end is in, or the newest.
        int from = Math.Max(0, AtOrAfter(range.StartMs + 1) - 1), to = range.ExtendRight ? count - 1 : AtOrAfter(range.EndMs) - 1;
        if (to < from || (to == 0 && TimeOf(0) >= range.EndMs))
            return null;

        long endMs = range.ExtendRight ? Math.Max(range.EndMs, TimeOf(count - 1) + (long)period.Duration.TotalMilliseconds) : range.EndMs;
        var profile = this.ProfileOf(range, endMs, history, period, symbol, singleBar: from == to, placing);
        if (profile == null && !placing)
            return null;

        var converter = window.CoordinatesConverter;
        float left = (float)converter.GetChartX(history[from, SeekOriginHistory.Begin].TimeLeft), right = (float)converter.GetChartX(history[to, SeekOriginHistory.Begin].TimeLeft) + chart.BarsWidth;
        bool hovered;
        int edge;
        lock (this.gate)
        {
            hovered = range.Id != 0 && this.hoveredId == range.Id;
            edge = hovered ? this.hoveredEdge : 0;
        }

        if (profile == null)
            return new FixedRangeMark(left, right, [], 0, 0, 0, "", hovered, edge, placing, range.ExtendRight);

        double most = profile.Total(profile.Poc);
        var rows = new FixedRangeRow[profile.Rows];
        for (int i = 0; i < rows.Length; i++)
        {
            rows[i] = new FixedRangeRow((float)converter.GetChartY(profile.Top(i)), (float)converter.GetChartY(profile.Bottom(i)),
                (float)(profile.Up[i] / most), (float)(profile.Down[i] / most), i >= profile.ValueLow && i <= profile.ValueHigh);
        }

        double poc = profile.Middle(profile.Poc);
        return new FixedRangeMark(left, right, rows, (float)converter.GetChartY(poc), (float)converter.GetChartY(profile.Top(profile.ValueHigh)), (float)converter.GetChartY(profile.Bottom(profile.ValueLow)),
            $"POC {symbol.FormatPrice(poc)}", hovered, edge, placing, range.ExtendRight);
    }

    /// <summary>Worked out again only when the range has moved or its bars have changed, and then twice a second.</summary>
    private FixedProfile ProfileOf(FixedRange range, long endMs, HistoricalData history, Period period, Symbol symbol, bool singleBar, bool placing)
    {
        Kept kept;
        lock (this.gate)
            this.profiles.TryGetValue(range.Id, out kept);

        long now = Environment.TickCount64;
        bool same = kept != null && kept.Range == range && kept.EndMs == endMs && kept.SingleBar == singleBar;
        if (same && kept.Profile != null && now - kept.At < LiveEveryMs)
            return kept.Profile;

        var (read, from) = this.BarsFor(range.StartMs, endMs, history, period, symbol, same ? kept.Read : null, same ? kept.From : null);
        if (read == null)
            return null;

        if (same && kept.Profile != null && ReferenceEquals(read, kept.Read))
        {
            lock (this.gate)
                this.profiles[range.Id] = kept with { At = now };
            return kept.Profile;
        }

        double price = history[history.Count - 1, SeekOriginHistory.Begin][PriceType.Close];
        var profile = FixedProfile.Of(read.Bars, FixedProfile.Tick(price), this.look.Size, this.look.TicksEach, this.look.ValueAreaShare, singleBar);
        lock (this.gate)
            this.profiles[range.Id] = new Kept(range, endMs, from, read, singleBar, profile, now);
        return profile;
    }

    /// <summary>
    /// The bars FRVP takes for a range (FixedProfile says which), and their name; null while they load. Where there are
    /// none finer than the chart's to be had, the chart's own.
    /// </summary>
    private (RangeRead Read, string From) BarsFor(long startMs, long endMs, HistoricalData history, Period period, Symbol symbol, RangeRead had, string hadFrom)
    {
        (RangeRead, string) Own() => (RangeBars.Read(history, startMs, endMs), "the chart's own bars");
        (RangeRead, string) Finer(Period finer) => (this.Source(symbol, finer).Between(startMs, endMs, hadFrom == finer.ToString() ? had : null), finer.ToString());

        double chartMinutes = period.Duration.TotalMinutes;
        if (period.BasePeriod == BasePeriod.Second || endMs - startMs <= 5 * Minute)
        {
            // One already worked out from 1-second bars keeps them.
            bool reachable = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - startMs <= SecondsBack;
            return (reachable || hadFrom == Period.SECOND1.ToString()) && period != Period.SECOND1 ? Finer(Period.SECOND1) : Own();
        }

        foreach (int minutes in FixedProfile.Timeframes)
        {
            if (minutes < FixedProfile.FirstTimeframe(startMs, endMs))
                continue;
            if (minutes >= chartMinutes)
                break;

            var (read, from) = Finer(PeriodOf(minutes));
            if (read == null || read.Bars.Length < FixedProfile.MostBars)
                return (read, from);
        }

        return Own();
    }

    private static Period PeriodOf(int minutes) => minutes switch
    {
        1 => Period.MIN1,
        5 => Period.MIN5,
        15 => Period.MIN15,
        30 => Period.MIN30,
        60 => Period.HOUR1,
        240 => Period.HOUR4,
        _ => Period.DAY1
    };

    private RangeBars Source(Symbol symbol, Period period)
    {
        lock (this.gate)
        {
            if (this.sources.TryGetValue(period, out var source))
                return source;

            source = RangeBars.For(symbol, period);
            source.Loaded += this.Redraw;
            return this.sources[period] = source;
        }
    }

    /// <summary>In the gate: the range as it'd be with the pointer's candle, whole candles at both ends.</summary>
    private FixedRange Between(long startMs, long endMs)
    {
        // Dragging, the other edge stays and this one stops short of it.
        if (this.doing == Doing.Dragging)
        {
            return this.draggedEdge > 0
                ? this.dragged with { EndMs = endMs > this.dragged.StartMs ? endMs : this.dragged.EndMs }
                : this.dragged with { StartMs = startMs < this.dragged.EndMs ? startMs : this.dragged.StartMs };
        }

        return new FixedRange(0, Math.Min(this.first.StartMs, startMs), Math.Max(this.first.EndMs, endMs));
    }

    private void Hook(IChart chart)
    {
        if (ReferenceEquals(chart, this.hooked))
            return;

        if (this.hooked is { } old)
        {
            old.MouseDown -= this.OnMouseDown;
            old.MouseMove -= this.OnMouseMove;
            old.MouseUp -= this.OnMouseUp;
            old.MouseClick -= this.OnMouseClick;
            old.MouseLeave -= this.OnMouseLeave;
        }

        this.hooked = chart;
        if (chart != null)
        {
            chart.MouseDown += this.OnMouseDown;
            chart.MouseMove += this.OnMouseMove;
            chart.MouseUp += this.OnMouseUp;
            chart.MouseClick += this.OnMouseClick;
            chart.MouseLeave += this.OnMouseLeave;
        }
    }

    private void OnMouseDown(object sender, ChartMouseNativeEventArgs e) => this.Quietly("down", e, () =>
    {
        Action act = null;
        lock (this.gate)
        {
            this.swallow = false;

            // A drag whose button was let go where the chart didn't hear of it.
            if (this.doing == Doing.Dragging)
                this.doing = Doing.Nothing;
            if (!this.InArea(e))
                return;

            var at = new PointF(e.X, e.Y);
            bool placing = this.doing is Doing.First or Doing.Last;
            if (e.Button == NativeMouseButtons.Right)
            {
                if (!placing)
                    return;

                this.doing = Doing.Nothing;
            }
            else if (e.Button != NativeMouseButtons.Left)
            {
                return;
            }
            else if (this.button.Contains(at))
            {
                this.doing = placing ? Doing.Nothing : Doing.First;
                this.under = null;
            }
            else if (placing)
            {
                if (this.CandleAt(e.X) is not var (startMs, endMs))
                    return;

                if (this.doing == Doing.First)
                {
                    this.first = (startMs, endMs);
                    this.under = this.first;
                    this.doing = Doing.Last;
                }
                else
                {
                    var range = this.Between(startMs, endMs);
                    string symbol = this.symbolName;
                    act = () => FixedRanges.Shared.Add(symbol, range.StartMs, range.EndMs);
                    this.doing = Doing.Nothing;
                    this.under = null;
                }
            }
            else
            {
                string symbol = this.symbolName;
                var hovered = this.places.FirstOrDefault(p => p.Id == this.hoveredId);
                if (hovered != null && hovered.Cross.Contains(at))
                {
                    act = () => FixedRanges.Shared.Remove(symbol, hovered.Id);
                    this.hoveredId = 0;
                }
                else if (hovered != null && hovered.Arrow.Contains(at) && RangeOf(hovered.Id) is { } extended)
                {
                    act = () => FixedRanges.Shared.Replace(symbol, extended with { ExtendRight = !extended.ExtendRight });
                }
                else if (this.EdgeAt(at) is var (edgeId, edge) && RangeOf(edgeId) is { } moved && !(edge > 0 && moved.ExtendRight))
                {
                    this.dragged = moved;
                    this.draggedEdge = edge;
                    this.under = this.CandleAt(e.X);
                    this.doing = Doing.Dragging;
                    e.NeedMouseCapture = true;
                }
                else
                {
                    return;
                }
            }

            this.swallow = true;
            e.Handled = true;
            e.NeedRedraw = true;
        }

        // Outside the gate: it tells every chart of the stock, this one too.
        act?.Invoke();
        this.Log($"{this.symbolName} FRVP {e.Button} at {e.X},{e.Y} with the button at {this.button.X:0},{this.button.Y:0}: {(act == null ? this.doing.ToString() : "done")}");

        FixedRange? RangeOf(long id) => FixedRanges.Shared.Of(this.symbolName).Where(r => r.Id == id).Select(r => (FixedRange?)r).FirstOrDefault();
    });

    private void OnMouseMove(object sender, ChartMouseNativeEventArgs e) => this.Quietly("move", e, () =>
    {
        lock (this.gate)
        {
            if (this.doing is Doing.Last or Doing.Dragging)
            {
                var now = this.CandleAt(e.X);
                if (now != this.under)
                {
                    this.under = now;
                    e.NeedRedraw = true;
                }

                if (this.doing == Doing.Dragging)
                    e.Handled = true;
                return;
            }

            if (this.doing != Doing.Nothing || (this.places.Count == 0 && this.hoveredId == 0))
                return;

            var at = new PointF(e.X, e.Y);
            long id = 0;
            int edge = 0;
            if (this.InArea(e))
            {
                if (this.EdgeAt(at) is var (edgeId, which))
                    (id, edge) = (edgeId, which);
                else
                    id = this.places.LastOrDefault(p => RectangleF.Inflate(p.Box, 10, 10).Contains(at) || RectangleF.Inflate(p.Cross, 6, 10).Contains(at) || RectangleF.Inflate(p.Arrow, 6, 10).Contains(at))?.Id ?? 0;
            }

            if (id == this.hoveredId && edge == this.hoveredEdge)
                return;

            this.hoveredId = id;
            this.hoveredEdge = edge;
            e.NeedRedraw = true;
        }
    });

    private void OnMouseUp(object sender, ChartMouseNativeEventArgs e) => this.Quietly("up", e, () =>
    {
        Action act = null;
        lock (this.gate)
        {
            if (this.doing == Doing.Dragging)
            {
                if (this.under is var (startMs, endMs))
                {
                    var range = this.Between(startMs, endMs);
                    string symbol = this.symbolName;
                    act = () => FixedRanges.Shared.Replace(symbol, range);
                }

                this.doing = Doing.Nothing;
                this.under = null;
            }

            if (this.swallow)
            {
                e.Handled = true;
                e.NeedRedraw = true;
            }
        }

        act?.Invoke();
    });

    private void OnMouseClick(object sender, ChartMouseNativeEventArgs e) => this.Quietly("click", e, () =>
    {
        if (this.swallow)
            e.Handled = true;
    });

    private void OnMouseLeave(object sender, ChartMouseNativeEventArgs e) => this.Quietly("leave", e, () =>
    {
        lock (this.gate)
        {
            if (this.hoveredId == 0 || this.doing == Doing.Dragging)
                return;

            this.hoveredId = 0;
            this.hoveredEdge = 0;
            e.NeedRedraw = true;
        }
    });

    /// <summary>Over the price area: not a pane under it, nor the scales.</summary>
    private bool InArea(ChartMouseNativeEventArgs e) =>
        this.area.Contains(e.X, e.Y) && (e.Window == null || e.Window.ClientRectangle == this.area);

    private (long Id, int Edge)? EdgeAt(PointF at)
    {
        foreach (var place in this.places)
        {
            if (at.Y < place.Box.Top - EdgeReach - 4 || at.Y > place.Box.Bottom + EdgeReach)
                continue;
            if (Math.Abs(at.X - place.Box.Right) <= EdgeReach)
                return (place.Id, 1);
            if (Math.Abs(at.X - place.Box.Left) <= EdgeReach)
                return (place.Id, -1);
        }

        return null;
    }

    /// <summary>The candle at a place across the chart: the last one when it's past that.</summary>
    private (long StartMs, long EndMs)? CandleAt(int x)
    {
        var converter = owner.CurrentChart?.MainWindow?.CoordinatesConverter;
        var history = owner.HistoricalData;
        if (converter == null || history == null || history.Count == 0 || history.Aggregation is not HistoryAggregationTime time)
            return null;

        int last = history.Count - 1;
        double Left(int i) => converter.GetChartX(history[i, SeekOriginHistory.Begin].TimeLeft);
        int bar = Math.Clamp((int)converter.GetBarIndex(converter.GetTime(x)), 0, last);
        while (bar < last && Left(bar + 1) <= x)
            bar++;
        while (bar > 0 && Left(bar) > x)
            bar--;

        long start = RangeBars.Ms(history[bar, SeekOriginHistory.Begin].TimeLeft), end = start + (long)time.Period.Duration.TotalMilliseconds;
        if (bar < last)
            end = Math.Min(end, RangeBars.Ms(history[bar + 1, SeekOriginHistory.Begin].TimeLeft));
        return (start, end);
    }

    private void Quietly(string what, ChartMouseNativeEventArgs e, Action act)
    {
        try
        {
            if (this.started && this.symbolName != null)
                act();
        }
        catch (Exception ex)
        {
            // A mouse event mustn't take the chart down with it.
            this.LogOnce($"mouse {what} {ex.GetType().Name}", () => $"{this.symbolName} FRVP mouse {what} at {e?.X},{e?.Y}: {ex.GetType().Name} {ex.Message}");
        }
    }

    private void OnRangesChanged(string symbol)
    {
        if (symbol == this.symbolName)
            this.Redraw();
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

    private string Says(string name, HistoryAggregationTime time, List<(FixedRange Range, FixedRangeMark Mark)> marks, Doing now, Rectangle at)
    {
        var said = new List<string>();
        lock (this.gate)
        {
            foreach (var (range, mark) in marks)
            {
                if (this.profiles.TryGetValue(range.Id, out var kept) && kept.Profile is { } p)
                    said.Add($"x {mark.Left:0}-{mark.Right:0}, {p.Bars} bars from {kept.From}, {p.Rows} rows of {p.TicksPerRow}, {mark.Label}, value area {p.Bottom(p.ValueLow):0.####}-{p.Top(p.ValueHigh):0.####}");
            }
        }

        return $"{name} FRVP on {time}: {marks.Count} of {FixedRanges.Shared.Of(name).Length} ranges [{string.Join("; ", said)}], {now}, area {at}";
    }

    private void LogOnce(string key, Func<string> what)
    {
        if (key == this.logged)
            return;

        this.logged = key;
        this.Log(what());
    }

    private void Log(string what)
    {
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

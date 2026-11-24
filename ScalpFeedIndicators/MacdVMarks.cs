using System.Drawing;
using System.Drawing.Drawing2D;
using TradingPlatform.BusinessLayer;
using TradingPlatform.BusinessLayer.Chart;

namespace ScalpFeedIndicators;

/// <summary>
/// Triangles on the price chart where MACD-V crosses its signal line (your choice, 2026-11-24): green under the candle
/// when it crosses up, red over it when it crosses down. The bar still forming has an outline only, as it can cross
/// back. The lines are Quantower's own MACD-V, run inside this indicator with its default settings (12, 26, 9, ATR 26),
/// so they're the ones in its pane; if it can't be had, MacdV's, worked out the same way.
/// </summary>
internal sealed class MacdVMarks
{
    public const string BuiltInName = "MACD-V (Volatility Normalisation)";

    private static readonly Color Up = Color.FromArgb(0, 230, 118);
    private static readonly Color Down = Color.FromArgb(255, 82, 82);

    private readonly MacdV own = new();
    private readonly Crossovers crossovers = new();
    private Indicator builtIn;
    private bool usesBuiltIn;
    private string logged;

    /// <summary>The built-in to run inside the owner, or null when there's none.</summary>
    public Indicator Start()
    {
        this.own.Clear();
        this.crossovers.Clear();
        this.builtIn = null;
        try
        {
            var info = Core.Instance.Indicators.All.FirstOrDefault(i => i.Name == BuiltInName);
            if (info != null)
                this.builtIn = Core.Instance.Indicators.CreateIndicator(info);
        }
        catch (Exception)
        {
            // MacdV's lines do instead; the log says so.
        }

        return this.builtIn;
    }

    /// <summary>It's no longer in the owner.</summary>
    public void Stop()
    {
        this.builtIn = null;
        this.own.Clear();
        this.crossovers.Clear();
    }

    public void Update(DateTime bar, double high, double low, double close)
    {
        this.own.Update(bar, high, low, close);
        double line = double.NaN, signal = double.NaN;
        if (this.builtIn != null)
        {
            line = this.builtIn.GetValue(0, 0);
            signal = this.builtIn.GetValue(0, 1);
        }

        this.usesBuiltIn = double.IsFinite(line) && double.IsFinite(signal);
        double gap = this.usesBuiltIn ? line - signal : this.builtIn == null ? this.own.Line - this.own.Signal : double.NaN;
        if (double.IsFinite(gap))
            this.crossovers.Set(bar, gap);
    }

    public void Paint(PaintChartEventArgs args, IChart chart, HistoricalData history, string symbol)
    {
        try
        {
            var window = chart?.MainWindow;
            var graphics = args?.Graphics;
            if (window == null || graphics == null || history == null)
                return;

            var converter = window.CoordinatesConverter;
            Rectangle area = window.ClientRectangle;
            int count = history.Count;
            int from = Math.Max(1, (int)converter.GetBarIndex(converter.GetTime(area.Left)) - 1);
            int to = Math.Min(count - 1, (int)Math.Ceiling(converter.GetBarIndex(converter.GetTime(area.Right))) + 1);
            int seen = 0;
            var state = graphics.Save();
            try
            {
                graphics.SetClip(area);
                graphics.SmoothingMode = SmoothingMode.AntiAlias;
                for (int i = from; i <= to; i++)
                {
                    if (history[i, SeekOriginHistory.Begin] is not HistoryItemBar bar)
                        continue;

                    int cross = this.crossovers.At(history[i - 1, SeekOriginHistory.Begin].TimeLeft, bar.TimeLeft);
                    if (cross == 0)
                        continue;

                    seen++;
                    float x = (float)(converter.GetChartX(bar.TimeLeft) + (chart.BarsWidth / 2.0));
                    float y = (float)converter.GetChartY(cross > 0 ? bar.Low : bar.High);
                    Draw(graphics, x, y, cross > 0, solid: i < count - 1);
                }
            }
            finally
            {
                graphics.Restore(state);
            }

            this.LogOnce(symbol, () => $"{symbol} MACD-V crossovers: {seen} on screen of {to - from + 1} bars, from {(this.usesBuiltIn ? "Quantower's MACD-V" : "its own calculation")}; " +
                $"{this.LastLines()}");
        }
        catch (Exception ex)
        {
            // The bars can change under a repaint, and a marker must never stop the chart: the next paint tries again.
            this.LogOnce($"{symbol} failed", () => $"{symbol} MACD-V crossovers not drawn: {ex.GetType().Name} {ex.Message}");
        }
    }

    /// <summary>A triangle pointing up from under y, or down from over it.</summary>
    internal static void Draw(Graphics graphics, float x, float y, bool up, bool solid)
    {
        const float Half = 5, Height = 8, Gap = 5;
        float tip = up ? y + Gap : y - Gap;
        float foot = up ? tip + Height : tip - Height;
        PointF[] shape = [new(x, tip), new(x - Half, foot), new(x + Half, foot)];
        var colour = up ? Up : Down;
        if (solid)
        {
            using var fill = new SolidBrush(colour);
            graphics.FillPolygon(fill, shape);
        }
        else
        {
            using var edge = new Pen(colour, 1.5f);
            graphics.DrawPolygon(edge, shape);
        }
    }

    private string LastLines() =>
        $"latest {(this.builtIn == null ? "no built-in" : $"built-in {this.builtIn.GetValue(0, 0):0.000} signal {this.builtIn.GetValue(0, 1):0.000}")}, own {this.own.Line:0.000} signal {this.own.Signal:0.000}";

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

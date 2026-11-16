using System.Drawing;
using TradingPlatform.BusinessLayer;

namespace ScalpFeedIndicators;

/// <summary>
/// RVOL for a Watchlist column: the symbol's volume so far today, premarket included, over its average daily
/// volume of the previous 50 sessions. IBKR has no 50-day figure (its average volume is 90 days), so the average
/// comes from the daily bars, loaded on their own, whatever timeframe the Watchlist gives the indicator. Today's
/// volume is the whole-market day volume the connection sends with the quote, since the tape and intraday bars
/// count only part of the market.
/// </summary>
public sealed class RelativeVolume : Indicator, IWatchlistIndicator
{
    private sealed record DayAverage(DateTime Day, double Volume);

    private DayAverage average;
    private int loading;
    private long nextTryMs;

    public RelativeVolume()
    {
        this.Name = "RVOL";
        this.Description = "Today's volume over the average daily volume of the previous 50 sessions.";
        this.AddLineSeries("RVOL", Color.DeepSkyBlue, 1, LineStyle.Solid);
        this.SeparateWindow = true;
    }

    public int MinHistoryDepths => 1;

    protected override void OnInit()
    {
        this.Digits = 2;
        this.LoadAverage();
    }

    protected override void OnUpdate(UpdateArgs args)
    {
        var today = RelativeVolumeMath.TodayEt(DateTime.UtcNow);
        var avg = Volatile.Read(ref this.average);
        if (avg?.Day != today)
        {
            this.LoadAverage();
            avg = null;
        }

        this.SetValue(avg == null ? double.NaN : RelativeVolumeMath.Ratio(this.Symbol?.Volume ?? double.NaN, avg.Volume));
    }

    /// <summary>The daily bars, off Quantower's thread: once a day, and again a minute after a failed try.</summary>
    private void LoadAverage()
    {
        var symbol = this.Symbol;
        if (symbol == null || Environment.TickCount64 < this.nextTryMs || Interlocked.Exchange(ref this.loading, 1) == 1)
            return;

        this.nextTryMs = Environment.TickCount64 + 60_000;
        _ = Task.Run(() =>
        {
            try
            {
                var today = RelativeVolumeMath.TodayEt(DateTime.UtcNow);
                var sessions = new List<(DateTime, double)>();
                using (var history = symbol.GetHistory(Period.DAY1, symbol.HistoryType, DateTime.UtcNow.AddDays(-100)))
                {
                    for (int i = 0; i < history.Count; i++)
                    {
                        if (history[i, SeekOriginHistory.Begin] is HistoryItemBar bar)
                            sessions.Add((bar.TimeLeft, bar.Volume));
                    }
                }

                double volume = RelativeVolumeMath.Average(sessions, today);
                if (!double.IsNaN(volume))
                    Volatile.Write(ref this.average, new DayAverage(today, volume));
            }
            catch (Exception)
            {
                // No history yet, or the symbol went away: tried again in a minute.
            }
            finally
            {
                Volatile.Write(ref this.loading, 0);
            }
        });
    }
}

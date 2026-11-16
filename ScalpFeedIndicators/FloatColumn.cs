using System.Drawing;
using ScalpFeedVendor.Feed;
using TradingPlatform.BusinessLayer;

namespace ScalpFeedIndicators;

/// <summary>
/// The float in millions, for a Watchlist column. It reads float-cache.json, which the ScalpFeed connection fills
/// from stockanalysis.com through Firecrawl, so it shows a symbol's float once the connection has fetched it.
/// Blank when the site doesn't have the symbol, it isn't fetched yet, or the stock trades 50 times its float a
/// day or more, which means the float is out of date; Symbol Info then shows the number with a ? and why.
/// </summary>
public sealed class FloatColumn : Indicator, IWatchlistIndicator
{
    public FloatColumn()
    {
        this.Name = "Float";
        this.Description = "Float in millions of shares, from stockanalysis.com through the ScalpFeed connection. Blank when unknown or out of date.";
        this.AddLineSeries("Float (M)", Color.Gold, 1, LineStyle.Solid);
        this.SeparateWindow = true;
    }

    public int MinHistoryDepths => 1;

    protected override void OnInit() => this.Digits = 2;

    protected override void OnUpdate(UpdateArgs args)
    {
        var facts = FloatCacheFile.Get(this.Symbol?.Name);
        this.SetValue(facts is { Known: true, Doubtful: false } ? facts.Float / 1e6 : double.NaN);
    }
}

/// <summary>float-cache.json, read again when it changes, looked at no more than every 2 seconds.</summary>
internal static class FloatCacheFile
{
    private static readonly object Gate = new();
    private static Dictionary<string, FloatFacts> cache = new();
    private static DateTime fileTimeUtc;
    private static long checkedMs = long.MinValue;

    public static FloatFacts Get(string symbol)
    {
        if (string.IsNullOrEmpty(symbol))
            return null;

        lock (Gate)
        {
            long now = Environment.TickCount64;
            if (now - checkedMs >= 2000)
            {
                checkedMs = now;
                string path = FloatFacts.CachePath;
                var time = File.Exists(path) ? File.GetLastWriteTimeUtc(path) : default;
                if (time != fileTimeUtc)
                {
                    fileTimeUtc = time;
                    cache = FloatFacts.ReadCache(path);
                }
            }

            return cache.GetValueOrDefault(symbol);
        }
    }
}

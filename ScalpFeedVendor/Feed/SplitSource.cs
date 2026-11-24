using System.Text.Json.Nodes;

namespace ScalpFeedVendor.Feed;

/// <summary>
/// The market's reverse splits from Massive (MassiveSource), kept in split-cache.json: one request for the newest
/// 5,000, which on 2026-11-24 reached back to February 2021. Massive's docs say its free plan goes back 2 years, so
/// that may shorten. A fetch that fails, or brings too few to be right, leaves the file as it was.
/// </summary>
internal sealed class SplitSource : MassiveSource
{
    public const string Endpoint = Host + "stocks/v1/splits?adjustment_type=reverse_split&limit=5000&sort=execution_date.desc";

    /// <summary>Fewer stocks than this in an answer and something's wrong with it: the market has thousands.</summary>
    public const int FewestStocks = 200;

    private readonly string cachePath;

    public SplitSource(string cachePath = null, Send send = null, Func<string> readKey = null, Func<DateTime> utcNow = null, TimeSpan? checkEvery = null)
        : base("split", send, readKey, utcNow)
    {
        this.cachePath = cachePath ?? ReverseSplits.CachePath;
        this.FetchedUtc = DateTimeOffset.FromUnixTimeMilliseconds(ReverseSplits.Read(this.cachePath).FetchedMs).UtcDateTime;
        this.Start(checkEvery);
    }

    protected override async Task<string> FetchAsync(Func<string, Task<(JsonNode Answer, string Problem)>> get, DateTime now)
    {
        var (answer, problem) = await get(Endpoint).ConfigureAwait(false);
        if (problem != null)
            return problem;

        var splits = ReverseSplits.FromAnswer(answer, new DateTimeOffset(now).ToUnixTimeMilliseconds());
        if (splits.Stocks.Count < FewestStocks)
            return $"only {splits.Stocks.Count} stocks in the answer";

        splits.Write(this.cachePath);
        this.FetchedUtc = now;
        var days = splits.Stocks.Values.SelectMany(s => s).Select(s => s.Day).ToList();
        this.Info($"{days.Count:#,0} reverse splits of {splits.Stocks.Count:#,0} stocks from Massive, {days.Min():yyyy-MM-dd} to {days.Max():yyyy-MM-dd}");
        return null;
    }
}

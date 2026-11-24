using System.Globalization;
using System.Text.Json.Nodes;

namespace ScalpFeedVendor.Feed;

/// <summary>
/// Every stock's offering prospectuses (Offerings) from Massive's index of SEC filings (MassiveSource), kept in
/// offerings-cache.json. The first time it fetches a little over 3 years, newest first, as far as your daily chart
/// goes: a few pages of 10,000, each written as it comes, so the newest show first. After that it asks for the last
/// few days every 6 hours and adds them. The index runs a day behind: at 7:56 ET on 2026-11-24 its newest filing was
/// the day before's, so a morning's offering isn't in it. A fetch cut short is started over.
/// </summary>
internal sealed class OfferingSource : MassiveSource
{
    /// <summary>Fewer stocks than this in the first page of the first fetch and something's wrong with it.</summary>
    public const int FewestStocks = 200;

    /// <summary>More pages than this and something's wrong: 3 years were a handful on 2026-11-24.</summary>
    public const int MostPages = 10;

    private static readonly TimeSpan Back = TimeSpan.FromDays((3 * 366) + 14);

    /// <summary>How far before the last fetch a later one starts, for filings the index was late with.</summary>
    private static readonly TimeSpan Overlap = TimeSpan.FromDays(5);

    private readonly string cachePath;
    private Offerings kept;

    public OfferingSource(string cachePath = null, Send send = null, Func<string> readKey = null, Func<DateTime> utcNow = null, TimeSpan? checkEvery = null)
        : base("offerings", send, readKey, utcNow)
    {
        this.cachePath = cachePath ?? Offerings.CachePath;
        this.kept = Offerings.Read(this.cachePath);
        if (this.kept.Complete)
            this.FetchedUtc = DateTimeOffset.FromUnixTimeMilliseconds(this.kept.FetchedMs).UtcDateTime;
        this.Start(checkEvery);
    }

    /// <summary>The filings since this day, newest first.</summary>
    public static string Since(DateTime day) =>
        $"{Host}stocks/filings/vX/index?form_type.any_of={string.Join("%2C", Offerings.Forms)}&filing_date.gte={day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}&limit=10000&sort=filing_date.desc";

    protected override async Task<string> FetchAsync(Func<string, Task<(JsonNode Answer, string Problem)>> get, DateTime now)
    {
        bool first = !this.kept.Complete;
        var oldest = (now - Back).Date;
        long nowMs = new DateTimeOffset(now).ToUnixTimeMilliseconds();
        var fetched = first ? Offerings.None : this.kept;
        string url = Since(first ? oldest : (this.FetchedUtc - Overlap).Date);
        int filings = 0;
        for (int page = 1; url != null; page++)
        {
            if (page > MostPages)
                return $"more than {MostPages} pages";

            var (answer, problem) = await get(url).ConfigureAwait(false);
            if (problem != null)
                return problem;

            var rows = Offerings.FromAnswer(answer);
            if (page == 1 && (first ? rows.Select(r => r.Ticker).Distinct().Count() < FewestStocks : rows.Count == 0))
                return $"only {rows.Count} offerings in the answer";

            filings += rows.Count;
            fetched = fetched.With(rows, oldest, nowMs, complete: false);
            url = (string)answer["next_url"];

            // The charts get what there is so far, unless a file that has it all is already there.
            if (first && url != null)
                fetched.Write(this.cachePath);
        }

        fetched = fetched with { Complete = true };
        fetched.Write(this.cachePath);
        this.kept = fetched;
        this.FetchedUtc = now;
        this.Info($"{filings:#,0} offering filings from Massive{(first ? "" : " in the last days")}; {fetched.Stocks.Values.Sum(d => d.Count):#,0} days kept, of {fetched.Stocks.Count:#,0} stocks since {oldest:yyyy-MM-dd}");
        return null;
    }
}

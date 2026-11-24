using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ScalpFeedVendor.Feed;

/// <summary>
/// The days each stock filed an offering prospectus with the SEC, newest first, as OfferingSource keeps them in
/// offerings-cache.json for the markers on daily charts (your request, 2026-11-24): forms 424B5 (from a shelf) and
/// 424B4. Those aren't all sales at a set price. Of five read that day two were, two were at-the-market programs (one
/// only changing its size), and one covered the shares of warrants already sold; the index has no price to tell them
/// apart by. Each is a way for the company to add shares. The day is the filing's, which can be a day or more after
/// the news. They come from Massive, whose terms keep its data to your own use, so none of it goes in the repo: the
/// harness makes its own up. Nothing here needs the plugin, so the indicator reads the same file.
/// </summary>
/// <param name="Complete">False while the first fetch, which takes a few pages, is under way or was cut short.</param>
internal sealed record Offerings(long FetchedMs, bool Complete, IReadOnlyDictionary<string, IReadOnlyList<DateTime>> Stocks)
{
    public const string FileName = "offerings-cache.json";

    /// <summary>The forms, as Massive's filings index has them.</summary>
    public static readonly string[] Forms = ["424B4", "424B5"];

    public static readonly Offerings None = new(0, false, new Dictionary<string, IReadOnlyList<DateTime>>());

    public static string CachePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".scalpfeed", FileName);

    /// <summary>The symbol's, newest first, or none. Massive writes share classes with a dot.</summary>
    public IReadOnlyList<DateTime> Of(string symbol) =>
        string.IsNullOrEmpty(symbol) ? [] : this.Stocks.GetValueOrDefault(FloatFacts.SiteSymbol(symbol)) ?? [];

    /// <summary>The offerings in one page of the filings index; a filing with no ticker (a trust, a note) is left out.</summary>
    public static List<(string Ticker, DateTime Day)> FromAnswer(JsonNode answer)
    {
        var found = new List<(string, DateTime)>();
        foreach (var row in answer?["results"] as JsonArray ?? [])
        {
            if ((string)row?["ticker"] is { Length: > 0 } ticker && Forms.Contains((string)row["form_type"]) && TryDay((string)row["filing_date"], out var day))
                found.Add((ticker, day));
        }

        return found;
    }

    /// <summary>These with more added, each day once and newest first, and those before the oldest day kept left out.</summary>
    public Offerings With(IEnumerable<(string Ticker, DateTime Day)> more, DateTime oldest, long fetchedMs, bool complete) =>
        new(fetchedMs, complete, this.Stocks.SelectMany(s => s.Value.Select(day => (Ticker: s.Key, Day: day))).Concat(more)
            .Where(o => o.Day >= oldest)
            .GroupBy(o => o.Ticker, o => o.Day, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<DateTime>)[.. g.Distinct().OrderByDescending(d => d)], StringComparer.OrdinalIgnoreCase));

    /// <summary>
    /// Circles of this width for marks at these places across a chart: one for each, or one for several that would
    /// overlap, at the rightmost of them (the newest), with how many it stands for. Rightmost first.
    /// </summary>
    public static List<(float X, int Count)> Group(IEnumerable<float> places, float width)
    {
        var groups = new List<(float X, int Count)>();
        foreach (float x in places.OrderByDescending(p => p))
        {
            if (groups.Count > 0 && groups[^1].X - x < width)
                groups[^1] = (groups[^1].X, groups[^1].Count + 1);
            else
                groups.Add((x, 1));
        }

        return groups;
    }

    /// <summary>What's in the file, or None with no file or a damaged one.</summary>
    public static Offerings Read(string path)
    {
        try
        {
            if (!File.Exists(path) || JsonNode.Parse(File.ReadAllText(path)) is not JsonObject file || file["stocks"] is not JsonObject stocks)
                return None;

            var read = new Dictionary<string, IReadOnlyList<DateTime>>(StringComparer.OrdinalIgnoreCase);
            foreach (var (ticker, list) in stocks)
            {
                var days = new List<DateTime>();
                foreach (var entry in list as JsonArray ?? [])
                {
                    if (TryDay((string)entry, out var day))
                        days.Add(day);
                }

                read[ticker] = days;
            }

            return new Offerings((long?)file["fetchedMs"] ?? 0, (bool?)file["complete"] ?? false, read);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or IOException or UnauthorizedAccessException or FormatException)
        {
            // Damaged: OfferingSource fetches it all again.
            return None;
        }
    }

    public void Write(string path)
    {
        var stocks = new JsonObject();
        foreach (var (ticker, days) in this.Stocks.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase))
            stocks[ticker] = new JsonArray([.. days.Select(d => (JsonNode)d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))]);

        CacheFile.Replace(path, new JsonObject { ["fetchedMs"] = this.FetchedMs, ["complete"] = this.Complete, ["stocks"] = stocks }.ToJsonString());
    }

    private static bool TryDay(string text, out DateTime day) =>
        DateTime.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out day);
}

using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ScalpFeedVendor.Feed;

/// <summary>
/// A stock's last split, from the same stockanalysis.com statistics the float comes from (FloatFacts), so it costs no
/// lookup of its own, as kept in split-cache.json for the reverse split marker on daily charts (your request,
/// 2026-11-23). Date null means the site lists no split. The site gives only the last one, so a later forward split
/// hides an earlier reverse one. It has its own file because momo-algo writes float-cache.json whole and would drop
/// fields it doesn't know. Nothing here needs the plugin, so the indicator reads the same file.
/// </summary>
internal sealed record SplitFacts(DateTime? Date, bool Reverse, string Ratio, long FetchedMs)
{
    public const string FileName = "split-cache.json";

    public static string CachePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".scalpfeed", FileName);

    // "September 1, 2026" in the exact ("hover") value, "Sep 1, 2026" in the one shown.
    private static readonly string[] DateForms = ["MMMM d, yyyy", "MMM d, yyyy", "yyyy-MM-dd"];

    public string Describe() => this.Date is DateTime date
        ? $"{(this.Reverse ? "reverse" : "forward")} split {this.Ratio ?? "?"} on {date:yyyy-MM-dd}"
        : "no split listed";

    /// <summary>The last split in one statistics result (Firecrawl's data.alexandria[i].data), from its stockSplits section.</summary>
    public static SplitFacts FromStatistics(JsonNode statistics, long fetchedMs)
    {
        string Item(string id)
        {
            foreach (var s in statistics?["sections"]?.AsArray() ?? [])
            {
                if ((string)s?["id"] != "stockSplits")
                    continue;

                foreach (var item in s["items"]?.AsArray() ?? [])
                {
                    if ((string)item?["id"] == id)
                        return ((string)item["hover"] ?? (string)item["value"])?.Trim();
                }
            }

            return null;
        }

        if (!DateTime.TryParseExact(Item("lastSplitDate"), DateForms, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
            return new SplitFacts(null, false, null, fetchedMs);

        bool reverse = string.Equals(Item("lastSplitType"), "Reverse", StringComparison.OrdinalIgnoreCase);
        return new SplitFacts(date, reverse, Item("splitRatio"), fetchedMs);
    }

    /// <summary>
    /// The index of the daily bar a split belongs under: the one for its day or, with no trading that day, the next.
    /// -1 when the bars don't reach back to it, or it hasn't happened yet. Bars oldest first, each stamped with its
    /// session date (RelativeVolumeMath).
    /// </summary>
    public static int BarFor(DateTime splitDay, int count, Func<int, DateTime> timeOf)
    {
        splitDay = splitDay.Date;
        if (count <= 0 || timeOf(0).Date > splitDay || timeOf(count - 1).Date < splitDay)
            return -1;

        int low = 0, high = count - 1;
        while (low < high)
        {
            int middle = low + ((high - low) / 2);
            if (timeOf(middle).Date < splitDay)
                low = middle + 1;
            else
                high = middle;
        }

        return low;
    }

    public static Dictionary<string, SplitFacts> ReadCache(string path)
    {
        var cache = new Dictionary<string, SplitFacts>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(path))
            return cache;

        try
        {
            foreach (var (symbol, entry) in JsonNode.Parse(File.ReadAllText(path))?.AsObject() ?? [])
            {
                if (entry == null)
                    continue;

                DateTime? date = DateTime.TryParseExact((string)entry["date"], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;
                cache[symbol] = new SplitFacts(date, (bool?)entry["reverse"] ?? false, (string)entry["ratio"], (long?)entry["fetchedMs"] ?? 0);
            }
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or IOException or UnauthorizedAccessException or FormatException)
        {
            // Damaged: each stock's split comes back with its next float fetch.
        }

        return cache;
    }

    /// <summary>
    /// Writes the whole cache beside it, then swaps it in, so a reader never sees half a file. Windows refuses the
    /// swap while a chart has the old one open to read, which is for about a millisecond, so it's tried a few times.
    /// </summary>
    public static void WriteCache(string path, IEnumerable<KeyValuePair<string, SplitFacts>> cache)
    {
        var json = new JsonObject();
        foreach (var (symbol, s) in cache.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase))
        {
            json[symbol] = new JsonObject
            {
                ["date"] = s.Date?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                ["reverse"] = s.Reverse,
                ["ratio"] = s.Ratio,
                ["fetchedMs"] = s.FetchedMs,
            };
        }

        string temp = path + ".tmp";
        File.WriteAllText(temp, json.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                File.Move(temp, path, true);
                return;
            }
            catch (Exception ex) when (attempt < 10 && (ex is IOException or UnauthorizedAccessException))
            {
                Thread.Sleep(20);
            }
        }
    }
}

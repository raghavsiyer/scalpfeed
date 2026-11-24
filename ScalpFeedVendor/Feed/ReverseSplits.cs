using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ScalpFeedVendor.Feed;

/// <summary>One reverse split: the first day it trades at the new price, and its ratio as 1:100.</summary>
internal sealed record ReverseSplit(DateTime Day, string Ratio);

/// <summary>
/// Every stock's reverse splits, newest first, as SplitSource keeps them in split-cache.json for the marker on daily
/// charts (your request, 2026-11-23). They come from Massive (your choice, 2026-11-24): one answer covers the whole
/// market and every split back some years, announced ones still to come included, where stockanalysis.com gave one
/// stock's last split, with its float, up to a week old. Massive's terms keep its data to your own use, so none of it
/// goes in the repo: the harness makes its own up. Nothing here needs the plugin, so the indicator reads the same file.
/// </summary>
internal sealed record ReverseSplits(long FetchedMs, IReadOnlyDictionary<string, IReadOnlyList<ReverseSplit>> Stocks)
{
    public const string FileName = "split-cache.json";

    public static readonly ReverseSplits None = new(0, new Dictionary<string, IReadOnlyList<ReverseSplit>>());

    public static string CachePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".scalpfeed", FileName);

    /// <summary>The symbol's, newest first, or none. Massive writes share classes with a dot, as stockanalysis.com does.</summary>
    public IReadOnlyList<ReverseSplit> Of(string symbol) =>
        string.IsNullOrEmpty(symbol) ? [] : this.Stocks.GetValueOrDefault(FloatFacts.SiteSymbol(symbol)) ?? [];

    /// <summary>The reverse splits in one answer from Massive's splits endpoint, whatever order it has them in.</summary>
    public static ReverseSplits FromAnswer(JsonNode answer, long fetchedMs)
    {
        var found = new List<(string Ticker, ReverseSplit Split)>();
        foreach (var row in answer?["results"] as JsonArray ?? [])
        {
            if ((string)row?["ticker"] is not { Length: > 0 } ticker || (string)row["adjustment_type"] != "reverse_split")
                continue;
            if (!TryDay((string)row["execution_date"], out var day))
                continue;

            double from = Number(row["split_from"]), to = Number(row["split_to"]);
            if (from > to && to > 0)
                found.Add((ticker, new ReverseSplit(day, $"{Plain(to)}:{Plain(from)}")));
        }

        return new ReverseSplits(fetchedMs, found
            .GroupBy(f => f.Ticker, f => f.Split, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<ReverseSplit>)[.. g.DistinctBy(s => s.Day).OrderByDescending(s => s.Day)], StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>
    /// The last split (newest first) that has happened by the last bar, and the index of the daily bar it goes under, or
    /// null with none, or when the bars don't reach back to it: an older one isn't the last.
    /// </summary>
    public static (int Bar, ReverseSplit Split)? Latest(IReadOnlyList<ReverseSplit> splits, int count, Func<int, DateTime> timeOf)
    {
        if (count <= 0)
            return null;

        var lastDay = timeOf(count - 1).Date;
        foreach (var split in splits)
        {
            if (split.Day.Date > lastDay)
                continue;

            int bar = BarFor(split.Day, count, timeOf);
            return bar < 0 ? null : (bar, split);
        }

        return null;
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

    /// <summary>What's in the file, or None with no file, a damaged one, or one in the form used before 2026-11-24.</summary>
    public static ReverseSplits Read(string path)
    {
        try
        {
            if (!File.Exists(path) || JsonNode.Parse(File.ReadAllText(path)) is not JsonObject file || file["splits"] is not JsonObject stocks)
                return None;

            var read = new Dictionary<string, IReadOnlyList<ReverseSplit>>(StringComparer.OrdinalIgnoreCase);
            foreach (var (ticker, list) in stocks)
            {
                var splits = new List<ReverseSplit>();
                foreach (var entry in list as JsonArray ?? [])
                {
                    if (entry is JsonArray { Count: 2 } pair && TryDay((string)pair[0], out var day))
                        splits.Add(new ReverseSplit(day, (string)pair[1]));
                }

                read[ticker] = splits;
            }

            return new ReverseSplits((long?)file["fetchedMs"] ?? 0, read);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or IOException or UnauthorizedAccessException or FormatException)
        {
            // Damaged: SplitSource writes it whole again.
            return None;
        }
    }

    /// <summary>
    /// Writes the whole file beside it, then swaps it in, so a reader never sees half of one. Windows refuses the swap
    /// while a chart has the old one open to read, which is for a few milliseconds, so it's tried a few times.
    /// </summary>
    public void Write(string path)
    {
        var stocks = new JsonObject();
        foreach (var (ticker, splits) in this.Stocks.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase))
            stocks[ticker] = new JsonArray([.. splits.Select(s => (JsonNode)new JsonArray(s.Day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), s.Ratio))]);

        string temp = path + ".tmp";
        File.WriteAllText(temp, new JsonObject { ["fetchedMs"] = this.FetchedMs, ["splits"] = stocks }.ToJsonString());
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

    private static bool TryDay(string text, out DateTime day) =>
        DateTime.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out day);

    private static double Number(JsonNode node) => node is JsonValue v && v.TryGetValue(out double d) ? d : double.NaN;

    private static string Plain(double n) => n.ToString("0.####", CultureInfo.InvariantCulture);
}

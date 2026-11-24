using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ScalpFeedVendor.Feed;

/// <summary>
/// A stock's float and share count from stockanalysis.com's statistics page, through Firecrawl, as kept in
/// float-cache.json: one entry per symbol, fetched again after a week (FloatSource). Float NaN means the site
/// doesn't have the symbol. Nothing here needs the plugin, so the Watchlist's Float column reads the same file.
/// </summary>
internal sealed record FloatFacts(string Day, double Float, double SharesOut, double AverageVolume, long FetchedMs)
{
    /// <summary>
    /// A 20-day average volume this many times the float or more means the float is out of date: these companies
    /// issue shares faster than they file. FGL on 2026-11-16 had a float of 8,557 after a 1:100 reverse split
    /// while trading 1.57M shares a day (183 times), and tastytrade's share count was just as old. The others
    /// that day were GDHG 0.2 times, GMM 0.7 and FCUV 11.
    /// </summary>
    public const double DoubtfulTurnover = 50;

    public static string CachePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".scalpfeed", "float-cache.json");

    private static readonly TimeZoneInfo Eastern = TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");

    public bool Known => this.Float > 0;

    /// <summary>Average daily volume as a multiple of the float.</summary>
    public double Turnover => this.AverageVolume / this.Float;

    public bool Doubtful => this.Known && this.Turnover >= DoubtfulTurnover;

    /// <summary>The Eastern date, which decides when a fetch is a day old.</summary>
    public static string DayOf(DateTime utc) => TimeZoneInfo.ConvertTimeFromUtc(utc, Eastern).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// <summary>stockanalysis.com writes share classes with a dot (BRK.B); tastytrade with a slash.</summary>
    public static string SiteSymbol(string symbol) => symbol.Replace('/', '.');

    /// <summary>What the Symbol Info panel and the log say: 29.52M, 546,711, or 8,557 with why it's doubtful.</summary>
    public string Describe() =>
        !this.Known ? (double.IsNaN(this.SharesOut) ? "not on stockanalysis.com" : "no float on stockanalysis.com")
        : this.Doubtful ? $"{Shares(this.Float)} ? (trades {this.Turnover:0}x that a day, so likely out of date)"
        : Shares(this.Float);

    /// <summary>
    /// The float for a Watchlist's Description column: 3.9M, 661K, 9K ? when out of date, n/a when the site has none,
    /// blank before it's fetched.
    /// </summary>
    public static string Short(FloatFacts facts) =>
        facts == null ? ""
        : !facts.Known ? "n/a"
        : facts.Doubtful ? Shares(facts.Float) + " ?"
        : Shares(facts.Float);

    public static string Shares(double n) =>
        double.IsNaN(n) ? "n/a"
        : n >= 999_950_000 ? (n / 1e9).ToString("0.00", CultureInfo.InvariantCulture) + "B"
        : n >= 999_500 ? (n / 1e6).ToString("0.0", CultureInfo.InvariantCulture) + "M"
        : n >= 999.5 ? (n / 1e3).ToString("0", CultureInfo.InvariantCulture) + "K"
        : n.ToString("#,0", CultureInfo.InvariantCulture);

    /// <summary>
    /// The facts in one statistics result (Firecrawl's data.alexandria[i].data): the shares section's float and
    /// share count, and the stock price section's 20-day average volume, each from its exact ("hover") value. Counted
    /// in the ADSs that trade where the site counts the ordinary shares behind them (AdsScale).
    /// </summary>
    public static FloatFacts FromStatistics(JsonNode statistics, string day, long fetchedMs)
    {
        double scale = AdsScale(statistics);
        double Shares(string id)
        {
            double n = Item(statistics, "shares", id);
            return scale == 1 ? n : Math.Round(n * scale);
        }

        return new FloatFacts(day, Shares("float"), Shares("sharesout"), Item(statistics, "stockPrice", "averageVolume"), fetchedMs);
    }

    /// <summary>
    /// What to multiply the site's float and share count by: 1, or for some ADSs the ADSs each ordinary share is. SVRE on
    /// 2026-11-24 had a float of 83.56B and 101.81B shares, SaverOne's ordinary shares, while each ADS is 43,200 of them
    /// (its SEC filings). The site's short interest counts ADSs, and is also given as a share of those outstanding, so
    /// the two say how many there are: 37,917 at 1.520% is 2.49M. A float can't be more than all the shares, so one over
    /// three times that many is in other units, and so is the share count. A company with another class that doesn't
    /// trade has more shares out than that too, but not float.
    /// </summary>
    public static double AdsScale(JsonNode statistics)
    {
        double shortShares = Item(statistics, "shortSelling", "shortInterest");
        double shortPercent = Item(statistics, "shortSelling", "shortShares");
        double flt = Item(statistics, "shares", "float");
        double outstanding = Item(statistics, "shares", "sharesout");

        // The percentage has 3 decimals, so a smaller one would say too little.
        if (!(shortShares >= 100) || !(shortPercent >= 0.05) || !(flt > 0) || !(outstanding > 0))
            return 1;

        double trading = shortShares / (shortPercent / 100);
        return flt > trading * 3 && outstanding > trading * 3 ? trading / outstanding : 1;
    }

    private static double Item(JsonNode statistics, string section, string id)
    {
        foreach (var s in statistics?["sections"]?.AsArray() ?? [])
        {
            if ((string)s?["id"] != section)
                continue;

            foreach (var item in s["items"]?.AsArray() ?? [])
            {
                if ((string)item?["id"] == id)
                    return Number((string)item["hover"] ?? (string)item["value"]);
            }
        }

        return double.NaN;
    }

    private static double Number(string text) =>
        double.TryParse(text?.Replace(",", "").TrimEnd('%'), NumberStyles.Float, CultureInfo.InvariantCulture, out double n) ? n : double.NaN;

    public static Dictionary<string, FloatFacts> ReadCache(string path)
    {
        var cache = new Dictionary<string, FloatFacts>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(path))
            return cache;

        try
        {
            foreach (var (symbol, entry) in JsonNode.Parse(File.ReadAllText(path))?.AsObject() ?? [])
            {
                if (entry == null)
                    continue;

                double Read(string name) => entry[name] is JsonValue v && v.TryGetValue(out double d) ? d : double.NaN;
                cache[symbol] = new FloatFacts((string)entry["day"], Read("float"), Read("sharesOut"), Read("averageVolume"), (long)Read("fetchedMs"));
            }
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or IOException or FormatException)
        {
            // Mid-write or damaged: the plugin writes it whole again on the next fetch.
        }

        return cache;
    }

    /// <summary>Writes the whole cache beside it, then swaps it in, so a reader never sees half a file.</summary>
    public static void WriteCache(string path, IEnumerable<KeyValuePair<string, FloatFacts>> cache)
    {
        var json = new JsonObject();
        foreach (var (symbol, f) in cache.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase))
        {
            json[symbol] = new JsonObject
            {
                ["day"] = f.Day,
                ["float"] = Finite(f.Float),
                ["sharesOut"] = Finite(f.SharesOut),
                ["averageVolume"] = Finite(f.AverageVolume),
                ["fetchedMs"] = f.FetchedMs,
            };
        }

        string temp = path + ".tmp";
        File.WriteAllText(temp, json.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temp, path, true);
    }

    private static JsonNode Finite(double d) => double.IsFinite(d) ? JsonValue.Create(d) : null;
}

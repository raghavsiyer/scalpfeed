using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ScalpFeedVendor.Feed;

/// <summary>
/// Fetches each symbol's float from stockanalysis.com through Firecrawl (your choice, 2026-11-16) when it's quoted
/// (a Watchlist row, chart or DOM), one request at a time, in the background, and keeps it in float-cache.json.
/// tastytrade has no float, and its share counts are stale. Each fetch costs 5 Firecrawl credits and the free plan
/// has 1,000 a month, so a float is kept for a week (your choice, same day): floats change slowly, apart from
/// offerings. A symbol the site doesn't have costs nothing and is asked about again the next day. The key is
/// yours, in firecrawl.json, and stays out of the log. momo-algo looks floats up the same way and shares the file
/// (2026-11-18, its market_data/firecrawl_float.py), so the cache is read again when the file changes, and each
/// write merges with what's on disk, newest per symbol, rather than dropping the bot's.
/// A symbol asked for while there's no key, or while fetches are paused (rate limited, out of credits), is held and
/// asked for again every 30 seconds until it goes through: it's only asked for when first quoted, so on 2026-11-19
/// SEAT, quoted before the key was in, never got its float, and the rows queued behind a rate limit were dropped.
/// </summary>
internal sealed class FloatSource : IDisposable
{
    public static string KeyPath => Path.Combine(Log.AppDir, "firecrawl.json");

    public const string Endpoint = "https://api.firecrawl.dev/v2/scrape";

    private static readonly TimeSpan RetryAfterFailure = TimeSpan.FromMinutes(10);

    /// <summary>The HTTP status and body of one POST of this JSON with this key.</summary>
    public delegate Task<(int Status, string Body)> Send(string key, string json, CancellationToken ct);

    private readonly ConcurrentDictionary<string, FloatFacts> cache;
    private readonly ConcurrentDictionary<string, DateTime> retryAtUtc = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, byte> queued = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentQueue<string> queue = new();
    private readonly ConcurrentDictionary<string, byte> held = new(StringComparer.OrdinalIgnoreCase);
    private readonly Timer retryHeld;
    private readonly CancellationTokenSource stop = new();
    private readonly HttpClient http;
    private readonly Send send;
    private readonly Func<string> readKey;
    private readonly Func<DateTime> utcNow;
    private readonly string cachePath;
    private readonly Action<string> arrived;
    private readonly object fileGate = new();
    private DateTime pausedUntilUtc;
    private bool keyMissingLogged;
    private int working;
    private DateTime fileTimeUtc;

    // Null to look at the file next time, whenever that is. It was long.MinValue until 2026-11-24, and now minus that
    // overflows to a negative number, so what the bot wrote went unseen until the first fetch here.
    private long? fileCheckedMs;

    /// <summary>How often symbols held back by a missing key or a pause are asked for again.</summary>
    public static readonly TimeSpan RetryHeldEvery = TimeSpan.FromSeconds(30);

    /// <param name="arrived">Called from the background with a symbol whose facts just came in.</param>
    /// <param name="retryHeldEvery">The harness passes Timeout.InfiniteTimeSpan and calls RetryHeld itself.</param>
    public FloatSource(Action<string> arrived, string cachePath = null, Send send = null, Func<string> readKey = null, Func<DateTime> utcNow = null, TimeSpan? retryHeldEvery = null)
    {
        var every = retryHeldEvery ?? RetryHeldEvery;
        this.retryHeld = new Timer(_ => this.RetryHeld(), null, every, every);
        this.arrived = arrived;
        this.cachePath = cachePath ?? FloatFacts.CachePath;
        this.cache = new(FloatFacts.ReadCache(this.cachePath), StringComparer.OrdinalIgnoreCase);
        this.fileTimeUtc = File.Exists(this.cachePath) ? File.GetLastWriteTimeUtc(this.cachePath) : default;
        this.utcNow = utcNow ?? (() => DateTime.UtcNow);
        this.readKey = readKey ?? ReadKeyFile;
        if (send == null)
        {
            this.http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
            send = this.PostAsync;
        }

        this.send = send;
    }

    /// <summary>The facts kept for the symbol, however old, or null.</summary>
    public FloatFacts Get(string symbol)
    {
        this.Reload(force: false);
        return this.cache.GetValueOrDefault(symbol);
    }

    /// <summary>
    /// Takes in what another process (momo-algo) wrote to the file since it was last read: entries newer than ours.
    /// Looks at the file's time no more than every 2 seconds unless forced.
    /// </summary>
    private void Reload(bool force)
    {
        lock (this.fileGate)
        {
            long nowMs = Environment.TickCount64;
            if (!force && this.fileCheckedMs is long at && nowMs - at < 2000)
                return;

            this.fileCheckedMs = nowMs;
            var time = File.Exists(this.cachePath) ? File.GetLastWriteTimeUtc(this.cachePath) : default;
            if (time == this.fileTimeUtc)
                return;

            this.fileTimeUtc = time;
            foreach (var (symbol, facts) in FloatFacts.ReadCache(this.cachePath))
            {
                if (!this.cache.TryGetValue(symbol, out var ours) || facts.FetchedMs > ours.FetchedMs)
                    this.cache[symbol] = facts;
            }
        }
    }

    /// <summary>How long a float found is kept before it's fetched again.</summary>
    public static readonly TimeSpan KeepFound = TimeSpan.FromDays(7);

    /// <summary>Asks for the facts unless they're fresh, already asked for, or a recent attempt failed.</summary>
    public void Want(string symbol)
    {
        if (string.IsNullOrEmpty(symbol))
            return;

        var now = this.utcNow();
        this.Reload(force: false);
        if (this.cache.TryGetValue(symbol, out var facts) && Fresh(facts, now))
            return;
        if (now < this.pausedUntilUtc)
        {
            this.held.TryAdd(symbol, 0);
            return;
        }
        if (this.retryAtUtc.TryGetValue(symbol, out var at) && now < at)
            return;
        if (!this.queued.TryAdd(symbol, 0))
            return;

        this.queue.Enqueue(symbol);
        if (Interlocked.CompareExchange(ref this.working, 1, 0) == 0)
            _ = Task.Run(this.WorkAsync);
    }

    /// <summary>A float found is good for a week; that the site hasn't got a symbol, until the next day.</summary>
    public static bool Fresh(FloatFacts facts, DateTime utc) =>
        facts.Known
            ? utc - DateTimeOffset.FromUnixTimeMilliseconds(facts.FetchedMs).UtcDateTime < KeepFound
            : facts.Day == FloatFacts.DayOf(utc);

    /// <summary>Asks again for the symbols held back, once there's a key and fetches aren't paused.</summary>
    public void RetryHeld()
    {
        try
        {
            if (this.held.IsEmpty || this.stop.IsCancellationRequested || this.utcNow() < this.pausedUntilUtc || string.IsNullOrWhiteSpace(this.readKey()))
                return;

            foreach (var symbol in this.held.Keys)
            {
                this.held.TryRemove(symbol, out _);
                this.Want(symbol);
            }
        }
        catch (Exception ex)
        {
            Log.Info($"[float] retrying held symbols: {Log.Scrub(ex.GetBaseException().Message)}");
        }
    }

    /// <summary>For the harness: waits until nothing is queued or being fetched.</summary>
    public async Task IdleAsync()
    {
        while (Volatile.Read(ref this.working) != 0 || !this.queue.IsEmpty)
            await Task.Delay(10).ConfigureAwait(false);
    }

    public void Dispose()
    {
        this.stop.Cancel();
        this.retryHeld.Dispose();
        this.http?.Dispose();
    }

    private async Task WorkAsync()
    {
        while (true)
        {
            while (!this.stop.IsCancellationRequested && this.queue.TryDequeue(out var symbol))
            {
                try
                {
                    await this.FetchAsync(symbol).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    if (this.stop.IsCancellationRequested)
                        return;
                    this.Failed(symbol, ex.GetBaseException().Message);
                }
                finally
                {
                    this.queued.TryRemove(symbol, out _);
                }
            }

            Volatile.Write(ref this.working, 0);

            // Something queued after the loop looked, but before it let go, would otherwise wait for the next Want.
            if (this.stop.IsCancellationRequested || this.queue.IsEmpty || Interlocked.CompareExchange(ref this.working, 1, 0) != 0)
                return;
        }
    }

    private async Task FetchAsync(string symbol)
    {
        var now = this.utcNow();
        if (now < this.pausedUntilUtc)
        {
            this.held.TryAdd(symbol, 0);
            return;
        }

        string key = this.readKey();
        if (string.IsNullOrWhiteSpace(key))
        {
            if (!this.keyMissingLogged)
                Log.Info($"[float] no Firecrawl key yet: paste it as apiKey in {KeyPath}. Nothing is fetched until then.");
            this.keyMissingLogged = true;
            this.held.TryAdd(symbol, 0);
            return;
        }

        this.keyMissingLogged = false;
        Log.AddSecret(key);
        string request = new JsonObject
        {
            ["alexandria"] = new JsonObject
            {
                ["provider"] = "stockanalysis-com",
                ["capability"] = "securities/stock_statistics",
                ["options"] = new JsonObject { ["symbol"] = FloatFacts.SiteSymbol(symbol) },
            },
        }.ToJsonString();

        var (status, body) = await this.send(key, request, this.stop.Token).ConfigureAwait(false);
        switch (status)
        {
            case 401 or 403:
                this.Pause(symbol, TimeSpan.FromMinutes(30), $"Firecrawl refused the key (HTTP {status}); check apiKey in {KeyPath}");
                return;
            case 402:
                this.Pause(symbol, TimeSpan.FromHours(1), "Firecrawl says the account is out of credits (HTTP 402)");
                return;
            case 429:
                this.Pause(symbol, TimeSpan.FromMinutes(1), "Firecrawl is rate limiting (HTTP 429)");
                return;
            case < 200 or >= 300:
                this.Failed(symbol, $"HTTP {status}");
                return;
        }

        var result = JsonNode.Parse(body)?["data"]?["alexandria"]?[0];
        var error = result?["error"];
        string day = FloatFacts.DayOf(now);
        long fetchedMs = new DateTimeOffset(now).ToUnixTimeMilliseconds();
        FloatFacts facts;
        if ((string)error?["code"] == "not_found")
        {
            facts = new FloatFacts(day, double.NaN, double.NaN, double.NaN, fetchedMs);
        }
        else if (result?["data"] is JsonObject statistics)
        {
            facts = FloatFacts.FromStatistics(statistics, day, fetchedMs);
        }
        else
        {
            this.Failed(symbol, (string)error?["message"] ?? "no statistics in the answer");
            return;
        }

        this.retryAtUtc.TryRemove(symbol, out _);
        lock (this.fileGate)
        {
            // Anything the bot wrote since we last looked goes into the file too, not over it.
            this.fileCheckedMs = null;
            this.Reload(force: true);
            this.cache[symbol] = facts;
            FloatFacts.WriteCache(this.cachePath, this.cache);
            this.fileTimeUtc = File.GetLastWriteTimeUtc(this.cachePath);
        }
        Log.Info($"[float] {symbol} {facts.Describe()}" + (facts.Known
            ? $", shares outstanding {FloatFacts.Shares(facts.SharesOut)}, 20-day volume {FloatFacts.Shares(facts.AverageVolume)}, {(int?)result?["creditsCost"] ?? 0} credits"
            : ""));
        this.arrived?.Invoke(symbol);
    }

    private void Failed(string symbol, string why)
    {
        this.retryAtUtc[symbol] = this.utcNow() + RetryAfterFailure;
        Log.Info($"[float] {symbol} not fetched, trying again in {RetryAfterFailure.TotalMinutes:0} minutes: {Log.Scrub(why)}");
    }

    private void Pause(string symbol, TimeSpan span, string why)
    {
        this.held.TryAdd(symbol, 0);
        this.pausedUntilUtc = this.utcNow() + span;
        Log.Info($"[float] {why}. No float fetches for {span.TotalMinutes:0} minutes.");
    }

    private async Task<(int, string)> PostAsync(string key, string json, CancellationToken ct)
    {
        using var message = new HttpRequestMessage(HttpMethod.Post, Endpoint) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        using var response = await this.http.SendAsync(message, ct).ConfigureAwait(false);
        return ((int)response.StatusCode, await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
    }

    private static string ReadKeyFile()
    {
        try
        {
            return File.Exists(KeyPath) ? (string)JsonNode.Parse(File.ReadAllText(KeyPath))?["apiKey"] : null;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or IOException)
        {
            return null;
        }
    }
}

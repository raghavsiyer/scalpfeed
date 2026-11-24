using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ScalpFeedVendor.Feed;

/// <summary>
/// Fetches the market's reverse splits from Massive (formerly Polygon; your choice, 2026-11-24) and keeps them in
/// split-cache.json: one request for the newest 5,000, which on 2026-11-24 reached back to February 2021, when the
/// connection starts with a file over 6 hours old, and every 6 hours after. No symbol of yours is sent. Massive's docs
/// say its free plan goes back 2 years, so that may shorten. The key is yours, in massive.json, goes to Massive alone,
/// in a header, and stays out of the log. A fetch that fails, or brings too few to be right, leaves the file as it was
/// and is tried again in 10 minutes.
/// </summary>
internal sealed class SplitSource : IDisposable
{
    public const string Endpoint = "https://api.massive.com/stocks/v1/splits?adjustment_type=reverse_split&limit=5000&sort=execution_date.desc";

    /// <summary>Fewer stocks than this in an answer and something's wrong with it: the market has thousands.</summary>
    public const int FewestStocks = 200;

    public static readonly TimeSpan Keep = TimeSpan.FromHours(6);
    private static readonly TimeSpan RetryAfterFailure = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan CheckEvery = TimeSpan.FromMinutes(1);

    public static string KeyPath => Path.Combine(Log.AppDir, "massive.json");

    /// <summary>The HTTP status and body of one GET with this key.</summary>
    public delegate Task<(int Status, string Body)> Send(string url, string key, CancellationToken ct);

    private readonly string cachePath;
    private readonly Send send;
    private readonly Func<string> readKey;
    private readonly Func<DateTime> utcNow;
    private readonly HttpClient http;
    private readonly Timer timer;
    private readonly CancellationTokenSource stop = new();
    private DateTime fetchedUtc;
    private DateTime nextTryUtc;
    private bool keyMissingLogged;
    private int working;

    /// <param name="checkEvery">The harness passes Timeout.InfiniteTimeSpan and calls CheckAsync itself.</param>
    public SplitSource(string cachePath = null, Send send = null, Func<string> readKey = null, Func<DateTime> utcNow = null, TimeSpan? checkEvery = null)
    {
        this.cachePath = cachePath ?? ReverseSplits.CachePath;
        this.readKey = readKey ?? ReadKeyFile;
        this.utcNow = utcNow ?? (() => DateTime.UtcNow);
        this.fetchedUtc = DateTimeOffset.FromUnixTimeMilliseconds(ReverseSplits.Read(this.cachePath).FetchedMs).UtcDateTime;
        if (send == null)
        {
            // No redirects, so the key can't be passed on to another host.
            this.http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(60) };
            send = this.GetAsync;
        }

        this.send = send;
        var every = checkEvery ?? CheckEvery;
        this.timer = new Timer(_ => _ = this.CheckAsync(), null, every == Timeout.InfiniteTimeSpan ? every : TimeSpan.Zero, every);
    }

    /// <summary>Fetches if the file is over 6 hours old, a try hasn't just failed, and there's a key.</summary>
    public async Task CheckAsync()
    {
        var now = this.utcNow();
        if (this.stop.IsCancellationRequested || now - this.fetchedUtc < Keep || now < this.nextTryUtc)
            return;
        if (Interlocked.Exchange(ref this.working, 1) != 0)
            return;

        try
        {
            await this.FetchAsync(now).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            if (!this.stop.IsCancellationRequested)
                this.Failed(now, ex.GetBaseException().Message);
        }
        finally
        {
            Volatile.Write(ref this.working, 0);
        }
    }

    public void Dispose()
    {
        this.stop.Cancel();
        this.timer.Dispose();
        this.http?.Dispose();
    }

    private async Task FetchAsync(DateTime now)
    {
        string key = this.readKey();
        if (string.IsNullOrWhiteSpace(key))
        {
            if (!this.keyMissingLogged)
                Log.Info($"[split] no Massive key: put it as apiKey in {KeyPath}. No splits are fetched until then.");
            this.keyMissingLogged = true;
            return;
        }

        this.keyMissingLogged = false;
        Log.AddSecret(key);
        var (status, body) = await this.send(Endpoint, key, this.stop.Token).ConfigureAwait(false);
        if (status != 200)
        {
            this.Failed(now, status is 401 or 403 ? $"Massive refused the key (HTTP {status}); check apiKey in {KeyPath}" : $"HTTP {status}");
            return;
        }

        var splits = ReverseSplits.FromAnswer(JsonNode.Parse(body), new DateTimeOffset(now).ToUnixTimeMilliseconds());
        if (splits.Stocks.Count < FewestStocks)
        {
            this.Failed(now, $"only {splits.Stocks.Count} stocks in the answer");
            return;
        }

        splits.Write(this.cachePath);
        this.fetchedUtc = now;
        var days = splits.Stocks.Values.SelectMany(s => s).Select(s => s.Day).ToList();
        Log.Info($"[split] {days.Count:#,0} reverse splits of {splits.Stocks.Count:#,0} stocks from Massive, {days.Min():yyyy-MM-dd} to {days.Max():yyyy-MM-dd}");
    }

    private void Failed(DateTime now, string why)
    {
        this.nextTryUtc = now + RetryAfterFailure;
        Log.Info($"[split] not fetched, trying again in {RetryAfterFailure.TotalMinutes:0} minutes: {why}");
    }

    private async Task<(int, string)> GetAsync(string url, string key, CancellationToken ct)
    {
        using var message = new HttpRequestMessage(HttpMethod.Get, url);
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

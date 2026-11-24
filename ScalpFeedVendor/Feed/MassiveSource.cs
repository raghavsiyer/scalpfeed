using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ScalpFeedVendor.Feed;

/// <summary>
/// Something kept from Massive (formerly Polygon; your choice, 2026-11-24): fetched when what's kept is over 6 hours
/// old, looked at every minute, and tried again 10 minutes after a failure. The key is yours, in massive.json, goes to
/// Massive alone, in a header, and stays out of the log. No symbol of yours is sent: each fetch is of the whole market.
/// Massive's terms keep its data to your own use, so none of it goes in the repo.
/// </summary>
internal abstract class MassiveSource : IDisposable
{
    public const string Host = "https://api.massive.com/";

    public static readonly TimeSpan Keep = TimeSpan.FromHours(6);

    /// <summary>Between requests, all of ScalpFeed's together: the free plan is said to allow 5 a minute.</summary>
    public static readonly TimeSpan Apart = TimeSpan.FromSeconds(13);

    private static readonly TimeSpan RetryAfterFailure = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan CheckEvery = TimeSpan.FromMinutes(1);
    private static readonly SemaphoreSlim Turn = new(1, 1);
    private static DateTime lastSentUtc;

    public static string KeyPath => Path.Combine(Log.AppDir, "massive.json");

    /// <summary>The HTTP status and body of one GET with this key.</summary>
    public delegate Task<(int Status, string Body)> Send(string url, string key, CancellationToken ct);

    private readonly string tag;
    private readonly Send send;
    private readonly Func<string> readKey;
    private readonly Func<DateTime> utcNow;
    private readonly HttpClient http;
    private readonly CancellationTokenSource stop = new();
    private Timer timer;
    private DateTime nextTryUtc;
    private bool keyMissingLogged;
    private int working;

    protected MassiveSource(string tag, Send send, Func<string> readKey, Func<DateTime> utcNow)
    {
        this.tag = tag;
        this.readKey = readKey ?? ReadKeyFile;
        this.utcNow = utcNow ?? (() => DateTime.UtcNow);
        if (send == null)
        {
            // No redirects, so the key can't be passed on to another host.
            this.http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(90) };
            send = this.GetAsync;
        }

        this.send = send;
    }

    /// <summary>When what's kept was fetched; the start of time when nothing is, or not all of it.</summary>
    protected DateTime FetchedUtc { get; set; }

    /// <summary>Fetches and keeps it, and sets FetchedUtc. Says why when it couldn't; null when it did.</summary>
    protected abstract Task<string> FetchAsync(Func<string, Task<(JsonNode Answer, string Problem)>> get, DateTime now);

    /// <summary>For the end of a constructor. The harness passes Timeout.InfiniteTimeSpan and calls CheckAsync itself.</summary>
    protected void Start(TimeSpan? checkEvery)
    {
        var every = checkEvery ?? CheckEvery;
        this.timer = new Timer(_ => _ = this.CheckAsync(), null, every == Timeout.InfiniteTimeSpan ? every : TimeSpan.Zero, every);
    }

    /// <summary>Fetches if what's kept is over 6 hours old, a try hasn't just failed, and there's a key.</summary>
    public async Task CheckAsync()
    {
        var now = this.utcNow();
        if (this.stop.IsCancellationRequested || now - this.FetchedUtc < Keep || now < this.nextTryUtc)
            return;
        if (Interlocked.Exchange(ref this.working, 1) != 0)
            return;

        try
        {
            string key = this.readKey();
            if (string.IsNullOrWhiteSpace(key))
            {
                if (!this.keyMissingLogged)
                    Log.Info($"[{this.tag}] no Massive key: put it as apiKey in {KeyPath}. Nothing is fetched until then.");
                this.keyMissingLogged = true;
                return;
            }

            this.keyMissingLogged = false;
            Log.AddSecret(key);
            if (await this.FetchAsync(url => this.AskAsync(url, key), now).ConfigureAwait(false) is string problem)
                this.Failed(now, problem);
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
        this.timer?.Dispose();
        this.http?.Dispose();
    }

    protected void Info(string message) => Log.Info($"[{this.tag}] {message}");

    /// <summary>One page. The address of a next page comes from the answer, so it's checked before the key goes to it.</summary>
    private async Task<(JsonNode, string)> AskAsync(string url, string key)
    {
        if (!url.StartsWith(Host, StringComparison.Ordinal))
            return (null, "the next page isn't Massive's, so it wasn't asked for");

        var (status, body) = await this.send(url, key, this.stop.Token).ConfigureAwait(false);
        return status switch
        {
            200 => (JsonNode.Parse(body), null),
            401 or 403 => (null, $"Massive refused the key (HTTP {status}); check apiKey in {KeyPath}"),
            _ => (null, $"HTTP {status}"),
        };
    }

    private void Failed(DateTime now, string why)
    {
        this.nextTryUtc = now + RetryAfterFailure;
        this.Info($"not fetched, trying again in {RetryAfterFailure.TotalMinutes:0} minutes: {why}");
    }

    private async Task<(int, string)> GetAsync(string url, string key, CancellationToken ct)
    {
        await Turn.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var wait = lastSentUtc + Apart - DateTime.UtcNow;
            if (wait > TimeSpan.Zero)
                await Task.Delay(wait, ct).ConfigureAwait(false);
            lastSentUtc = DateTime.UtcNow;
        }
        finally
        {
            Turn.Release();
        }

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

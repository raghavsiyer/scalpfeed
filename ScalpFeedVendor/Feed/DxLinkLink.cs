using System.Diagnostics;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ScalpFeedVendor.Feed;

internal enum LinkState { Stopped, Connecting, Ready, Retrying }

internal readonly record struct FeedKey(string EventType, string Symbol);

/// <summary>
/// One DXLink WebSocket with one feed channel: handshake, keepalive and reconnect with
/// backoff. The receive loop only stamps, parses and hands rows to the sink. All sends
/// happen on other paths, so a slow send can't delay incoming data.
/// </summary>
internal sealed class DxLinkLink
{
    private const int FeedChannel = 1;
    private const string Version = "0.1-ScalpFeed/0.2.0";
    private static readonly TimeSpan KeepaliveEvery = TimeSpan.FromSeconds(30);
    // The server's keepalives can be 60 seconds apart when nothing is trading.
    private static readonly TimeSpan SilenceLimit = TimeSpan.FromSeconds(90);
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan StableAfter = TimeSpan.FromSeconds(60);
    private static readonly double[] BackoffSeconds = [0.25, 0.5, 1, 2, 4, 8, 15, 30];

    private readonly string name;
    private readonly Func<bool, CancellationToken, Task<QuoteToken>> tokenSource;
    private readonly IReadOnlyDictionary<string, string[]> fields;
    private readonly IFeedSink sink;
    private readonly double aggregation;
    private readonly FeedFrameParser parser;
    private readonly SemaphoreSlim sendGate = new(1, 1);
    private readonly Random jitter = new();

    private CancellationTokenSource stopCts;
    private CancellationTokenSource connectionCts;
    private Task runTask;
    private ClientWebSocket socket;
    private QuoteToken token;
    private TaskCompletionSource readyTcs = NewReadyTcs();
    private byte[] buffer = new byte[256 * 1024];
    private bool authSent;
    private bool needNewToken;
    private bool feedConfigured;
    private bool everReady;
    private long lastFrameStamp;
    private DateTime droppedAtUtc;
    private DateTime readyAtUtc;
    private volatile LinkState state = LinkState.Stopped;

    public DxLinkLink(
        string name,
        Func<bool, CancellationToken, Task<QuoteToken>> tokenSource,
        IReadOnlyDictionary<string, string[]> fields,
        IFeedSink sink,
        double aggregationSeconds = 0)
    {
        this.name = name;
        this.tokenSource = tokenSource;
        this.fields = fields;
        this.sink = sink;
        this.aggregation = aggregationSeconds;
        this.parser = new FeedFrameParser(fields);
    }

    public LinkState State => this.state;

    public string LastProblem { get; private set; }

    /// <summary>Aggregation period the server confirmed in FEED_CONFIG.</summary>
    public double ConfirmedAggregation { get; private set; } = double.NaN;

    /// <summary>Raised when the feed channel is usable. Must not block.</summary>
    public event Action Ready;

    /// <summary>Raised after a drop, once the feed is usable again. Must not block.</summary>
    public event Action<DateTime, DateTime> Recovered;

    public event Action<LinkState, string> StateChanged;

    /// <summary>Time of the last frame of any kind, from Stopwatch.GetTimestamp().</summary>
    public long LastFrameStamp => Volatile.Read(ref this.lastFrameStamp);

    /// <summary>Longest time a frame took from arrival to fully handled, in microseconds.</summary>
    public long TakeMaxHandleMicros() => Interlocked.Exchange(ref this.maxHandleMicros, 0);

    public long TakeFrameCount() => Interlocked.Exchange(ref this.frameCount, 0);

    private long maxHandleMicros;
    private long frameCount;

    public void Start()
    {
        this.stopCts = new CancellationTokenSource();
        this.runTask = Task.Run(() => this.RunAsync(this.stopCts.Token));
    }

    public async Task StopAsync()
    {
        if (this.stopCts == null)
            return;

        this.stopCts.Cancel();
        try
        {
            // Linked to stopCts, so a live connection is already cancelled by the line above.
            await this.runTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }

        this.SetState(LinkState.Stopped, "stopped");
    }

    public async Task<bool> WaitReadyAsync(TimeSpan timeout, CancellationToken ct)
    {
        var ready = Volatile.Read(ref this.readyTcs).Task;
        if (ready.IsCompleted)
            return true;

        try
        {
            await ready.WaitAsync(timeout, ct).ConfigureAwait(false);
            return true;
        }
        catch (TimeoutException)
        {
            return false;
        }
    }

    /// <summary>Drops the socket and reconnects, for a socket that's open but dead.</summary>
    public void ForceReconnect(string reason)
    {
        this.LastProblem = reason;
        Log.Info($"[{this.name}] forcing reconnect: {reason}");
        try
        {
            this.connectionCts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    public async Task SendSubscriptionAsync(IEnumerable<FeedKey> add, IEnumerable<FeedKey> remove, bool reset, long fromTimeMs = 0)
    {
        var message = new JsonObject { ["type"] = "FEED_SUBSCRIPTION", ["channel"] = FeedChannel };
        if (reset)
            message["reset"] = true;
        if (add != null)
            message["add"] = ToJson(add, fromTimeMs);
        if (remove != null)
            message["remove"] = ToJson(remove, 0);
        await this.SendAsync(message, CancellationToken.None).ConfigureAwait(false);
    }

    /// <summary>Presents a new quote token on the open socket, avoiding a reconnect.</summary>
    public async Task ReauthorizeAsync(QuoteToken newToken)
    {
        this.token = newToken;
        await this.SendAsync(new JsonObject { ["type"] = "AUTH", ["channel"] = 0, ["token"] = newToken.Token }, CancellationToken.None).ConfigureAwait(false);
    }

    private static TaskCompletionSource NewReadyTcs() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static JsonArray ToJson(IEnumerable<FeedKey> keys, long fromTimeMs)
    {
        var array = new JsonArray();
        foreach (var key in keys)
        {
            var item = new JsonObject { ["type"] = key.EventType, ["symbol"] = key.Symbol };
            if (fromTimeMs > 0)
                item["fromTime"] = fromTimeMs;
            array.Add(item);
        }

        return array;
    }

    private async Task RunAsync(CancellationToken stop)
    {
        int attempt = 0;
        while (!stop.IsCancellationRequested)
        {
            string reason;
            try
            {
                this.SetState(LinkState.Connecting, "connecting");
                reason = await this.RunOneConnectionAsync(stop).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested)
            {
                break;
            }
            catch (OperationCanceledException)
            {
                reason = this.LastProblem ?? "cancelled";
            }
            catch (Exception ex)
            {
                reason = ex.GetBaseException().Message;
            }

            if (stop.IsCancellationRequested)
                break;

            if (this.droppedAtUtc == default)
                this.droppedAtUtc = DateTime.UtcNow;

            this.LastProblem = reason;

            // Only a signal that has fired needs replacing. One that hasn't may have a Connect
            // waiting on it, and that Connect has to hear about the retry that works.
            if (Volatile.Read(ref this.readyTcs).Task.IsCompleted)
                Volatile.Write(ref this.readyTcs, NewReadyTcs());

            if (this.readyAtUtc != default && DateTime.UtcNow - this.readyAtUtc > StableAfter)
                attempt = 0;

            double delay = BackoffSeconds[Math.Min(attempt, BackoffSeconds.Length - 1)];
            delay *= 1 + (this.jitter.NextDouble() * 0.2);
            attempt++;
            this.SetState(LinkState.Retrying, $"{reason}; retrying in {delay:F2}s");

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(delay), stop).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task<string> RunOneConnectionAsync(CancellationToken stop)
    {
        this.authSent = false;
        this.feedConfigured = false;
        this.token = await this.tokenSource(this.needNewToken, stop).ConfigureAwait(false);
        this.needNewToken = false;

        using var connection = CancellationTokenSource.CreateLinkedTokenSource(stop);
        this.connectionCts = connection;
        using var ws = new ClientWebSocket();
        ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(15);
        ws.Options.KeepAliveTimeout = TimeSpan.FromSeconds(20);

        using (var connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(connection.Token))
        {
            connectTimeout.CancelAfter(ConnectTimeout);
            await ws.ConnectAsync(new Uri(this.token.Url), connectTimeout.Token).ConfigureAwait(false);
        }

        this.socket = ws;
        Volatile.Write(ref this.lastFrameStamp, Stopwatch.GetTimestamp());

        await this.SendAsync(
            new JsonObject
            {
                ["type"] = "SETUP",
                ["channel"] = 0,
                ["version"] = Version,
                ["keepaliveTimeout"] = 60,
                ["acceptKeepaliveTimeout"] = 60
            },
            connection.Token).ConfigureAwait(false);

        var keepalive = this.KeepaliveLoopAsync(connection.Token);
        try
        {
            return await this.ReceiveLoopAsync(ws, connection).ConfigureAwait(false);
        }
        finally
        {
            this.socket = null;
            this.connectionCts = null;
            connection.Cancel();
            try
            {
                await keepalive.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }
    }

    private async Task KeepaliveLoopAsync(CancellationToken ct)
    {
        var message = new JsonObject { ["type"] = "KEEPALIVE", ["channel"] = 0 };
        while (true)
        {
            await Task.Delay(KeepaliveEvery, ct).ConfigureAwait(false);

            double silentSeconds = (Stopwatch.GetTimestamp() - this.LastFrameStamp) / (double)Stopwatch.Frequency;
            if (silentSeconds > SilenceLimit.TotalSeconds)
            {
                this.ForceReconnect($"no frames for {silentSeconds:F0}s");
                return;
            }

            try
            {
                await this.SendAsync(message.DeepClone().AsObject(), ct).ConfigureAwait(false);
            }
            catch (WebSocketException)
            {
                return;
            }
        }
    }

    private async Task<string> ReceiveLoopAsync(ClientWebSocket ws, CancellationTokenSource connection)
    {
        var ct = connection.Token;
        while (ws.State == WebSocketState.Open)
        {
            int length = 0;
            ValueWebSocketReceiveResult result;
            do
            {
                if (length == this.buffer.Length)
                    Array.Resize(ref this.buffer, this.buffer.Length * 2);

                result = await ws.ReceiveAsync(this.buffer.AsMemory(length), ct).ConfigureAwait(false);
                length += result.Count;
            } while (!result.EndOfMessage);

            long stamp = Stopwatch.GetTimestamp();
            long arrivalUtcTicks = FeedClock.UtcNowTicks;
            Volatile.Write(ref this.lastFrameStamp, stamp);

            if (result.MessageType == WebSocketMessageType.Close)
                return $"server closed the socket: {ws.CloseStatus} {ws.CloseStatusDescription}";

            var frame = this.buffer.AsSpan(0, length);
            try
            {
                if (this.parser.TryParseData(frame, this.sink, arrivalUtcTicks))
                {
                    long micros = (Stopwatch.GetTimestamp() - stamp) * 1_000_000 / Stopwatch.Frequency;
                    if (micros > Volatile.Read(ref this.maxHandleMicros))
                        Volatile.Write(ref this.maxHandleMicros, micros);
                    StallWatch.Note("tasty frame", stamp);
                    Interlocked.Increment(ref this.frameCount);
                    continue;
                }
            }
            catch (JsonException ex)
            {
                // One bad frame must not end the session; it's rare enough to log.
                Log.Info($"[{this.name}] skipped a bad frame: {ex.Message}");
                continue;
            }

            var problem = await this.HandleControlAsync(Encoding.UTF8.GetString(frame), ct).ConfigureAwait(false);
            if (problem != null)
                return problem;
        }

        return $"socket state {ws.State}";
    }

    /// <summary>Handshake and server messages. Returns a reason to drop the connection.</summary>
    private async Task<string> HandleControlAsync(string text, CancellationToken ct)
    {
        using var document = JsonDocument.Parse(text);
        var root = document.RootElement;
        var type = root.TryGetProperty("type", out var typeElement) ? typeElement.GetString() : null;

        switch (type)
        {
            case "KEEPALIVE":
                break;

            case "SETUP":
                break;

            case "AUTH_STATE":
                var authState = root.GetProperty("state").GetString();
                if (authState == "UNAUTHORIZED")
                {
                    if (this.authSent)
                    {
                        // Refused or expired. Reconnect with a freshly minted token.
                        this.needNewToken = true;
                        return "quote token was refused";
                    }

                    this.authSent = true;
                    await this.SendAsync(new JsonObject { ["type"] = "AUTH", ["channel"] = 0, ["token"] = this.token.Token }, ct).ConfigureAwait(false);
                }
                else if (authState == "AUTHORIZED" && !this.feedConfigured)
                {
                    await this.SendAsync(
                        new JsonObject
                        {
                            ["type"] = "CHANNEL_REQUEST",
                            ["channel"] = FeedChannel,
                            ["service"] = "FEED",
                            ["parameters"] = new JsonObject { ["contract"] = "AUTO" }
                        },
                        ct).ConfigureAwait(false);
                }
                else if (authState == "AUTHORIZED")
                {
                    Log.Info($"[{this.name}] token accepted on the open socket");
                }

                break;

            case "CHANNEL_OPENED":
                await this.SendFeedSetupAsync(ct).ConfigureAwait(false);
                break;

            case "FEED_CONFIG":
                this.HandleFeedConfig(root);
                break;

            case "ERROR":
            case "CHANNEL_CLOSED":
                Log.Info($"[{this.name}] {type}: {Clip(text)}");
                break;

            default:
                Log.Info($"[{this.name}] unexpected {type}: {Clip(text)}");
                break;
        }

        return null;
    }

    private void HandleFeedConfig(JsonElement root)
    {
        if (root.TryGetProperty("aggregationPeriod", out var period) && period.ValueKind == JsonValueKind.Number)
            this.ConfirmedAggregation = period.GetDouble();

        if (root.TryGetProperty("eventFields", out var eventFields) && eventFields.ValueKind == JsonValueKind.Object)
        {
            foreach (var entry in eventFields.EnumerateObject())
            {
                var columns = entry.Value.EnumerateArray().Select(c => c.GetString()).ToArray();
                this.parser.UpdateLayout(entry.Name, columns);
            }
        }

        if (this.feedConfigured)
            return;

        this.feedConfigured = true;
        this.readyAtUtc = DateTime.UtcNow;
        Log.Info($"[{this.name}] feed ready: level={this.token.Level} aggregation={this.ConfirmedAggregation}");
        this.SetState(LinkState.Ready, "ready");
        Volatile.Read(ref this.readyTcs).TrySetResult();
        this.Ready?.Invoke();

        if (this.everReady && this.droppedAtUtc != default)
            this.Recovered?.Invoke(this.droppedAtUtc, this.readyAtUtc);

        this.everReady = true;
        this.droppedAtUtc = default;
    }

    private async Task SendFeedSetupAsync(CancellationToken ct)
    {
        var accept = new JsonObject();
        foreach (var (eventName, columns) in this.fields)
        {
            var list = new JsonArray();
            foreach (var column in columns)
                list.Add(column);
            accept[eventName] = list;
        }

        await this.SendAsync(
            new JsonObject
            {
                ["type"] = "FEED_SETUP",
                ["channel"] = FeedChannel,
                ["acceptAggregationPeriod"] = this.aggregation,
                ["acceptDataFormat"] = "COMPACT",
                ["acceptEventFields"] = accept
            },
            ct).ConfigureAwait(false);
    }

    private async Task SendAsync(JsonObject message, CancellationToken ct)
    {
        var ws = this.socket;
        if (ws == null || ws.State != WebSocketState.Open)
            return;

        var bytes = Encoding.UTF8.GetBytes(message.ToJsonString());
        await this.sendGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await ws.SendAsync(bytes, WebSocketMessageType.Text, true, ct).ConfigureAwait(false);
        }
        finally
        {
            this.sendGate.Release();
        }
    }

    private void SetState(LinkState newState, string detail)
    {
        if (this.state == newState && newState != LinkState.Retrying)
            return;

        this.state = newState;
        Log.Info($"[{this.name}] {newState}: {detail}");
        this.StateChanged?.Invoke(newState, detail);
    }

    private static string Clip(string text) => text.Length <= 400 ? text : text[..400] + "...";
}

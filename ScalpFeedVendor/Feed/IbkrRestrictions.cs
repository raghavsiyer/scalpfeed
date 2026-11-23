using System.Buffers.Binary;
using System.Globalization;
using System.Net.Sockets;
using System.Text;

namespace ScalpFeedVendor.Feed;

/// <summary>
/// Stocks IBKR won't let you open a position in. On 2026-11-23 IBKR refused all 21 of your ZBAO buys with "No Opening
/// Trades: Small Cap, Subject to Compliance Restriction". Its contract details say so beforehand, as a list of
/// ineligibility reasons, but only from API version 186. The data and trading connections speak 176, and moving
/// them would change every other message, so this asks on a short connection of its own
/// (<see cref="IbkrConfig.RestrictionsClientId"/>) and asks for nothing but contract details. The connection stays
/// open while there are stocks to look up and closes after the last.
/// Each stock is looked up once a day: Top Gainers and halted stocks as they arrive, and a charted one ahead of
/// those. Static (<see cref="Shared"/>) because Quantower builds a new vendor for every reconnect.
/// </summary>
internal sealed class IbkrRestrictions
{
    /// <summary>startApi, reqContractDetails.</summary>
    public static readonly IReadOnlySet<int> AllowedRequests = new HashSet<int> { 71, 9 };

    /// <summary>The first API version whose contract details carry ineligibility reasons (checked on Gateway, 2026-11-23).</summary>
    public const int ReasonsVersion = 186;

    private const string ClientVersions = "v100..186";
    private const int NoSecurityDefinition = 200;
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ReplyTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan RetryAfter = TimeSpan.FromMinutes(1);

    public static IbkrRestrictions Shared { get; } = new(IbkrConfig.Load);

    private readonly Func<IbkrConfig> loadConfig;
    private readonly int? clientIdOverride;
    private readonly object gate = new();
    private readonly Dictionary<string, string> reasons = new();
    private readonly Dictionary<string, DateTime> retryAtUtc = new();
    private readonly List<string> queue = new();
    private readonly Dictionary<string, Action<string, string>> warnWhenKnown = new();
    private readonly HashSet<string> warned = new();
    private string day;
    private string inFlight;
    private bool working;
    private bool unsupported;
    private int nextRequestId = 1;

    /// <param name="clientId">The harness's own ID, so it can run beside Quantower; otherwise the config's.</param>
    public IbkrRestrictions(Func<IbkrConfig> loadConfig, int? clientId = null)
    {
        this.loadConfig = loadConfig;
        this.clientIdOverride = clientId;
    }

    /// <summary>Why IBKR won't take an opening trade in this stock; null when it will, or it isn't known yet.</summary>
    public string Reason(string symbol)
    {
        lock (this.gate)
        {
            this.RollDay();
            return this.reasons.TryGetValue(Key(symbol), out var reason) && reason.Length > 0 ? reason : null;
        }
    }

    /// <summary>True once this stock has an answer today, restricted or not.</summary>
    public bool Known(string symbol)
    {
        lock (this.gate)
        {
            this.RollDay();
            return this.reasons.ContainsKey(Key(symbol));
        }
    }

    /// <summary>Looks up the stocks not known yet today, after any already waiting.</summary>
    public void Prefetch(IEnumerable<string> symbols)
    {
        lock (this.gate)
        {
            this.RollDay();
            foreach (var symbol in symbols)
            {
                var key = Key(symbol);
                if (this.Wanted(key))
                    this.queue.Add(key);
            }

            this.StartWork();
        }
    }

    /// <summary>
    /// A stock opened in a chart: looked up ahead of the rest if it isn't known yet. If IBKR restricts it,
    /// <paramref name="warn"/> gets the stock and IBKR's reason, once a day, now or when the answer comes.
    /// </summary>
    public void Charted(string symbol, Action<string, string> warn)
    {
        string key = Key(symbol), reason = null;
        lock (this.gate)
        {
            this.RollDay();
            if (this.warned.Contains(key))
                return;

            if (this.reasons.TryGetValue(key, out var known))
            {
                if (known.Length == 0 || !this.warned.Add(key))
                    return;
                reason = known;
            }
            else
            {
                this.warnWhenKnown[key] = warn;
                if (this.Wanted(key) || this.queue.Remove(key))
                    this.queue.Insert(0, key);
                this.StartWork();
                return;
            }
        }

        warn(key, reason);
    }

    /// <summary>
    /// IBKR's ineligibility reasons from one contract details reply (message 10) at version 186, joined with "; ":
    /// empty when there are none, null when the reply isn't laid out as expected. Fields as Gateway sent them for a
    /// stock, message ID first (checked against ZBAO, NVDA and others on 2026-11-23).
    /// </summary>
    public static string ParseReasons(IReadOnlyList<string> fields)
    {
        // 0 message, 1 request, 2-30 the contract and its details, 31 how many security IDs, then each as a pair.
        const int SecIdCount = 31;
        if (fields.Count <= SecIdCount || fields[0] != "10" || !int.TryParse(fields[SecIdCount], out int secIds) || secIds < 0)
            return null;

        // aggGroup, underSymbol, underSecType, marketRuleIds, realExpirationDate, stockType, minSize, sizeIncrement,
        // suggestedSizeIncrement. A fund has more after these; this asks for stocks only.
        int at = SecIdCount + 1 + 2 * secIds + 9;
        if (fields.Count <= at || fields[3] != "STK" || !int.TryParse(fields[at], out int count) || count < 0 || fields.Count != at + 1 + 2 * count)
            return null;

        var texts = new List<string>();
        for (int i = 0; i < count; i++)
        {
            string text = fields[at + 2 + 2 * i].Trim();
            if (text.Length > 0 && !texts.Contains(text))
                texts.Add(text);
        }

        return string.Join("; ", texts);
    }

    private static string Key(string symbol) => IbkrClient.ToIbkrSymbol(symbol.Trim()).ToUpperInvariant();

    private bool Wanted(string key) =>
        !this.unsupported && key.Length > 0 && key != this.inFlight && !this.reasons.ContainsKey(key) && !this.queue.Contains(key)
        && !(this.retryAtUtc.TryGetValue(key, out var at) && DateTime.UtcNow < at);

    /// <summary>A new day forgets every answer, since IBKR can add or lift a restriction overnight.</summary>
    private void RollDay()
    {
        string today = FloatFacts.DayOf(DateTime.UtcNow);
        if (today == this.day)
            return;

        this.day = today;
        this.reasons.Clear();
        this.retryAtUtc.Clear();
        this.warned.Clear();
    }

    private void StartWork()
    {
        if (this.working || this.queue.Count == 0)
            return;

        this.working = true;
        _ = Task.Run(this.WorkAsync);
    }

    private async Task WorkAsync()
    {
        TcpClient tcp = null;
        NetworkStream stream = null;
        string key = null;
        try
        {
            var config = this.loadConfig();
            while (true)
            {
                lock (this.gate)
                {
                    this.inFlight = null;
                    if (!config.Enabled || this.unsupported || this.queue.Count == 0)
                    {
                        this.queue.Clear();
                        this.working = false;
                        return;
                    }

                    key = this.queue[0];
                    this.queue.RemoveAt(0);
                    this.inFlight = key;
                }

                if (stream == null)
                {
                    tcp = new TcpClient { NoDelay = true };
                    (stream, int version) = await this.OpenAsync(tcp, config).ConfigureAwait(false);
                    if (version < ReasonsVersion)
                    {
                        lock (this.gate)
                            this.unsupported = true;
                        Log.Info($"[restrict] Gateway speaks API version {version}, and restrictions need {ReasonsVersion}, so they aren't looked up");
                        continue;
                    }
                }

                string reason = await this.LookUpAsync(stream, key).ConfigureAwait(false);
                this.Record(key, reason);
            }
        }
        catch (Exception ex) when (ex is IOException or SocketException or TimeoutException or OperationCanceledException or ObjectDisposedException or InvalidOperationException)
        {
            List<string> dropped;
            lock (this.gate)
            {
                dropped = this.queue.ToList();
                if (key != null)
                    dropped.Insert(0, key);
                foreach (var gone in dropped)
                    this.retryAtUtc[gone] = DateTime.UtcNow + RetryAfter;
                this.queue.Clear();
                this.inFlight = null;
                this.working = false;
            }

            Log.Info($"[restrict] couldn't ask IBKR about {string.Join(" ", dropped)}: {ex.Message}; trying again in a minute");
        }
        finally
        {
            tcp?.Dispose();
        }
    }

    /// <summary>Stores an answer and gives a waiting chart its warning. A null reason (no answer) is tried again later.</summary>
    private void Record(string key, string reason)
    {
        Action<string, string> warn = null;
        lock (this.gate)
        {
            if (reason == null)
            {
                this.retryAtUtc[key] = DateTime.UtcNow + RetryAfter;
                return;
            }

            this.reasons[key] = reason;
            if (this.warnWhenKnown.Remove(key, out var waiting) && reason.Length > 0 && this.warned.Add(key))
                warn = waiting;
        }

        if (reason.Length > 0)
            Log.Info($"[restrict] {key}: IBKR won't take an opening trade: {reason}");
        warn?.Invoke(key, reason);
    }

    /// <summary>Connects, says hello and waits for nextValidId, since Gateway drops a request sent before it.</summary>
    private async Task<(NetworkStream Stream, int Version)> OpenAsync(TcpClient tcp, IbkrConfig config)
    {
        using var timeout = new CancellationTokenSource(ConnectTimeout);
        await tcp.ConnectAsync(config.Host, config.Port, timeout.Token).ConfigureAwait(false);
        var stream = tcp.GetStream();
        await stream.WriteAsync("API\0"u8.ToArray(), timeout.Token).ConfigureAwait(false);
        await WriteFrameAsync(stream, Encoding.ASCII.GetBytes(ClientVersions), timeout.Token).ConfigureAwait(false);

        var hello = await ReadFieldsAsync(stream, timeout.Token).ConfigureAwait(false);
        int version = hello.Length > 0 && int.TryParse(hello[0], out int v) ? v : 0;
        if (version < ReasonsVersion)
            return (stream, version);

        int clientId = this.clientIdOverride ?? config.RestrictionsClientId;
        await SendAsync(stream, timeout.Token, 71, 2, clientId, "").ConfigureAwait(false);
        while (true)
        {
            var fields = await ReadFieldsAsync(stream, timeout.Token).ConfigureAwait(false);
            if (fields.Length > 0 && fields[0] == "9")
                return (stream, version);

            // [4, version, id, code, text, ...]: one for the connection (id -1) that isn't a farm notice, like 326 for a
            // client ID in use, means this connection won't do.
            if (fields.Length > 4 && fields[0] == "4" && fields[2] == "-1" && int.TryParse(fields[3], out int code) && code is < 2100 or > 2999)
                throw new IOException($"IBKR said {code} {fields[4]} on connecting (client {clientId})");
        }
    }

    /// <summary>One stock's reasons: empty for none, a stock IBKR doesn't know or a reply it can't read; null for an error.</summary>
    private async Task<string> LookUpAsync(NetworkStream stream, string key)
    {
        int id = this.nextRequestId++;
        using var timeout = new CancellationTokenSource(ReplyTimeout);
        await SendAsync(stream, timeout.Token, 9, 8, id, 0, key, "STK", "", 0, "", "", "SMART", "", "USD", "", "", 0, "", "", "").ConfigureAwait(false);

        string idText = id.ToString(CultureInfo.InvariantCulture);
        var found = new List<string>();
        bool usable = true;
        while (true)
        {
            var fields = await ReadFieldsAsync(stream, timeout.Token).ConfigureAwait(false);
            if (fields.Length < 2)
                continue;

            switch (fields[0])
            {
                case "10" when fields[1] == idText:
                    string reasons = ParseReasons(fields);
                    if (reasons == null)
                    {
                        usable = false;
                        Log.Info($"[restrict] {key}: contract details came in {fields.Length} fields, not as expected, so its restrictions aren't known");
                    }
                    else if (reasons.Length > 0)
                    {
                        found.Add(reasons);
                    }

                    break;
                case "52" when fields.Length > 2 && fields[2] == idText:
                    // A reply in another layout can't be read, so the stock counts as unrestricted for the day, rather
                    // than being asked about again every minute.
                    return usable ? string.Join("; ", found.Distinct()) : string.Empty;
                case "4" when fields.Length > 4 && fields[2] == idText:
                    if (fields[3] == NoSecurityDefinition.ToString(CultureInfo.InvariantCulture))
                        return string.Empty;
                    Log.Info($"[restrict] {key}: IBKR answered {fields[3]} {fields[4]}");
                    return null;
            }
        }
    }

    private static async Task SendAsync(NetworkStream stream, CancellationToken ct, params object[] fields)
    {
        int message = Convert.ToInt32(fields[0], CultureInfo.InvariantCulture);
        if (!AllowedRequests.Contains(message))
            throw new InvalidOperationException($"message {message} isn't one the restrictions connection may send");

        var text = new StringBuilder();
        foreach (var field in fields)
            text.Append(Convert.ToString(field, CultureInfo.InvariantCulture)).Append('\0');
        await WriteFrameAsync(stream, Encoding.ASCII.GetBytes(text.ToString()), ct).ConfigureAwait(false);
    }

    private static async Task WriteFrameAsync(NetworkStream stream, byte[] body, CancellationToken ct)
    {
        var frame = new byte[4 + body.Length];
        BinaryPrimitives.WriteInt32BigEndian(frame, body.Length);
        body.CopyTo(frame, 4);
        await stream.WriteAsync(frame, ct).ConfigureAwait(false);
    }

    private static async Task<string[]> ReadFieldsAsync(NetworkStream stream, CancellationToken ct)
    {
        var header = new byte[4];
        await stream.ReadExactlyAsync(header, ct).ConfigureAwait(false);
        int length = BinaryPrimitives.ReadInt32BigEndian(header);
        if (length is < 0 or > 1 << 24)
            throw new IOException($"a reply said it was {length} bytes long");

        var body = new byte[length];
        await stream.ReadExactlyAsync(body, ct).ConfigureAwait(false);
        var fields = Encoding.UTF8.GetString(body).Split('\0');
        return fields.Length > 0 && fields[^1].Length == 0 ? fields[..^1] : fields;
    }
}

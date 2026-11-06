// Step 2 probe: measures what the tastytrade DXLink feed really delivers. Read-only.
// Credentials come from %USERPROFILE%\.scalpfeed\tastytrade.json and are never printed.
//
//   ScalpFeedProbe --symbols SPY,AAPL --seconds 60 --aggregation 0 --history-hours 6 --candle-days 3

using System.Diagnostics;
using System.Globalization;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

const string ApiBase = "https://api.tastyworks.com";
const string Agent = "scalpfeed-probe/0.1";
const int LiveChannel = 1;
const int HistoryChannel = 3;
// Wide enough to include the 15-minute delayed feed; older events count as history.
const double MaxLiveDelayMs = 20 * 60 * 1000;

string Arg(string name, string fallback)
{
    int i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : fallback;
}

var symbols = Arg("--symbols", "SPY,AAPL").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
int seconds = int.Parse(Arg("--seconds", "60"), CultureInfo.InvariantCulture);
double aggregation = double.Parse(Arg("--aggregation", "0"), CultureInfo.InvariantCulture);
int historyHours = int.Parse(Arg("--history-hours", "6"), CultureInfo.InvariantCulture);
int candleDays = int.Parse(Arg("--candle-days", "3"), CultureInfo.InvariantCulture);
string liveContract = Arg("--contract", "AUTO");
var orderSources = Arg("--order-sources", "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
int tapeFromSeconds = int.Parse(Arg("--tape-from-seconds", "0"), CultureInfo.InvariantCulture);
var tradeExtra = Arg("--trade-extra-fields", "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

var appDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".scalpfeed");
var configPath = Path.Combine(appDir, "tastytrade.json");
var config = JsonNode.Parse(File.ReadAllText(configPath)).AsObject();
var eastern = TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");

var secrets = new List<string> { (string)config["client_secret"], (string)config["refresh_token"] };

string Scrub(string text)
{
    foreach (var s in secrets)
        if (!string.IsNullOrEmpty(s))
            text = text.Replace(s, "<redacted>");
    return text;
}

void Say(string text) => Console.WriteLine(Scrub(text));

string Clip(string text, int max = 600) => text.Length <= max ? text : text[..max] + "...";

Say($"run at {DateTime.Now:yyyy-MM-dd HH:mm:ss} local, {DateTime.UtcNow:HH:mm:ss} UTC, {TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, eastern):HH:mm:ss} ET");
Say($"symbols={string.Join(",", symbols)} seconds={seconds} requested aggregation={aggregation}");

using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", Agent);

// ---- OAuth ----
Say("\n== OAuth ==");
var tokenBody = new JsonObject
{
    ["grant_type"] = "refresh_token",
    ["refresh_token"] = (string)config["refresh_token"],
    ["client_id"] = (string)config["client_id"],
    ["client_secret"] = (string)config["client_secret"]
};
var tokenResp = await http.PostAsync(ApiBase + "/oauth/token", new StringContent(tokenBody.ToJsonString(), Encoding.UTF8, "application/json"));
var tokenText = await tokenResp.Content.ReadAsStringAsync();
Say($"POST /oauth/token -> {(int)tokenResp.StatusCode}");
if (!tokenResp.IsSuccessStatusCode)
{
    Say(Clip(tokenText));
    return 2;
}

var tokenJson = JsonNode.Parse(tokenText).AsObject();
var accessToken = (string)tokenJson["access_token"];
secrets.Add(accessToken);
Say($"access token ok: expires_in={tokenJson["expires_in"]} scope={tokenJson["scope"]}");

var rotated = (string)tokenJson["refresh_token"];
if (!string.IsNullOrEmpty(rotated) && rotated != (string)config["refresh_token"])
{
    secrets.Add(rotated);
    config["refresh_token"] = rotated;
    File.WriteAllText(configPath, config.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    Say("refresh token was rotated; saved the new one to the config file");
}

http.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", "Bearer " + accessToken);

async Task<(int Status, JsonNode Json, string Text)> Get(string path)
{
    var r = await http.GetAsync(ApiBase + path);
    var t = await r.Content.ReadAsStringAsync();
    JsonNode j = null;
    try { j = JsonNode.Parse(t); } catch (JsonException) { }
    return ((int)r.StatusCode, j, t);
}

// ---- Account visibility (no account numbers printed) ----
Say("\n== Account visibility ==");
var accounts = await Get("/customers/me/accounts");
Say($"GET /customers/me/accounts -> {accounts.Status}");
if (accounts.Status == 200)
{
    var items = accounts.Json?["data"]?["items"]?.AsArray();
    Say($"accounts visible: {items?.Count ?? 0}");
    if (items != null)
        foreach (var item in items)
        {
            var a = item?["account"];
            Say($"  type={a?["account-type-name"]} margin-or-cash={a?["margin-or-cash"]} is-closed={a?["is-closed"]} authority={item?["authority-level"]}");
        }
}
else
{
    Say(Clip(accounts.Text));
}

// ---- Instruments ----
Say("\n== Instrument lookup ==");
var instrument = await Get($"/instruments/equities/{symbols[0]}");
Say($"GET /instruments/equities/{symbols[0]} -> {instrument.Status}");
if (instrument.Status == 200)
{
    var d = instrument.Json?["data"];
    Say($"  streamer-symbol={d?["streamer-symbol"]} listed-market={d?["listed-market"]} is-etf={d?["is-etf"]} is-illiquid={d?["is-illiquid"]} active={d?["active"]}");
    Say($"  tick-sizes={d?["tick-sizes"]?.ToJsonString()}");
}
else
{
    Say(Clip(instrument.Text));
}

var search = await Get($"/symbols/search/{symbols[0][..Math.Min(2, symbols[0].Length)]}");
Say($"GET /symbols/search/{symbols[0][..Math.Min(2, symbols[0].Length)]} -> {search.Status}");
if (search.Status == 200)
{
    var items = search.Json?["data"]?["items"]?.AsArray();
    Say($"  results={items?.Count ?? 0} first={string.Join(",", items?.Take(5).Select(i => (string)i?["symbol"]) ?? [])}");
}

// ---- Quote token ----
Say("\n== Quote token ==");
var quote = await Get("/api-quote-tokens");
Say($"GET /api-quote-tokens -> {quote.Status}");
if (quote.Status != 200)
{
    Say(Clip(quote.Text));
    return 3;
}

var quoteToken = (string)quote.Json["data"]["token"];
secrets.Add(quoteToken);
var dxlinkUrl = (string)quote.Json["data"]["dxlink-url"];
var level = (string)quote.Json["data"]["level"];
Say($"level={level}");
if (level != "api")
    Say("WARNING: not a real-time token. Timing numbers from this run do not describe the real-time feed.");
Say($"dxlink-url={dxlinkUrl}");

// ---- DXLink ----
var fieldSets = new Dictionary<string, string[]>
{
    ["Quote"] = ["eventType", "eventSymbol", "eventTime", "sequence", "timeNanoPart", "bidTime", "bidExchangeCode", "bidPrice", "bidSize", "askTime", "askExchangeCode", "askPrice", "askSize"],
    ["Trade"] = ["eventType", "eventSymbol", "eventTime", "time", "timeNanoPart", "sequence", "exchangeCode", "price", "change", "size", "dayVolume", "dayTurnover", "tickDirection", "extendedTradingHours"],
    ["TradeETH"] = ["eventType", "eventSymbol", "eventTime", "time", "timeNanoPart", "sequence", "exchangeCode", "price", "change", "size", "dayVolume", "dayTurnover", "tickDirection", "extendedTradingHours"],
    ["TimeAndSale"] = ["eventType", "eventSymbol", "eventTime", "eventFlags", "index", "time", "timeNanoPart", "sequence", "exchangeCode", "price", "size", "bidPrice", "askPrice", "exchangeSaleConditions", "tradeThroughExempt", "aggressorSide", "spreadLeg", "extendedTradingHours", "validTick", "type"],
    ["Summary"] = ["eventType", "eventSymbol", "eventTime", "dayId", "dayOpenPrice", "dayHighPrice", "dayLowPrice", "dayClosePrice", "dayClosePriceType", "prevDayId", "prevDayClosePrice", "prevDayClosePriceType", "prevDayVolume", "openInterest"],
    ["Profile"] = ["eventType", "eventSymbol", "eventTime", "description", "shortSaleRestriction", "tradingStatus", "statusReason", "haltStartTime", "haltEndTime", "highLimitPrice", "lowLimitPrice", "high52WeekPrice", "low52WeekPrice"],
    ["Candle"] = ["eventType", "eventSymbol", "eventTime", "eventFlags", "index", "time", "sequence", "count", "open", "high", "low", "close", "volume", "vwap", "bidVolume", "askVolume"],
    ["Order"] = ["eventType", "eventSymbol", "eventTime", "eventFlags", "index", "time", "sequence", "source", "scope", "orderSide", "exchangeCode", "marketMaker", "price", "size", "count"]
};

foreach (var name in new[] { "Trade", "TradeETH" })
    fieldSets[name] = [.. fieldSets[name], .. tradeExtra];

var channelFields = new Dictionary<int, Dictionary<string, string[]>>
{
    [LiveChannel] = new(fieldSets),
    [HistoryChannel] = new(fieldSets)
};
var stats = new SortedDictionary<string, EventStat>();
var subscribed = new HashSet<int>();
var authSent = false;
long subscribeTs = 0;

Directory.CreateDirectory(Path.Combine(appDir, "probe"));
var capturePath = Path.Combine(appDir, "probe", $"capture-{DateTime.Now:yyyyMMdd-HHmmss}-{Environment.ProcessId}.jsonl");
await using var capture = new StreamWriter(capturePath, false, new UTF8Encoding(false), 1 << 20);

Say("\n== DXLink ==");
using var ws = new ClientWebSocket();
using var runCts = new CancellationTokenSource();
var sendLock = new SemaphoreSlim(1, 1);

async Task Send(JsonObject msg)
{
    var bytes = Encoding.UTF8.GetBytes(msg.ToJsonString());
    await sendLock.WaitAsync();
    try { await ws.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None); }
    finally { sendLock.Release(); }
}

JsonNode ToNode(object o) => JsonSerializer.SerializeToNode(o);

async Task SetupFeed(int channel)
{
    var fields = new JsonObject();
    foreach (var kv in fieldSets)
        fields[kv.Key] = ToNode(kv.Value);
    await Send(new JsonObject
    {
        ["type"] = "FEED_SETUP",
        ["channel"] = channel,
        ["acceptAggregationPeriod"] = aggregation,
        ["acceptDataFormat"] = "COMPACT",
        ["acceptEventFields"] = fields
    });
}

async Task Subscribe(int channel)
{
    if (!subscribed.Add(channel))
        return;

    var add = new JsonArray();
    long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    if (channel == LiveChannel)
    {
        foreach (var symbol in symbols)
        {
            foreach (var type in new[] { "Quote", "Trade", "TradeETH", "TimeAndSale", "Summary", "Profile" })
            {
                var entry = new JsonObject { ["type"] = type, ["symbol"] = symbol };
                if (type == "TimeAndSale" && tapeFromSeconds > 0)
                    entry["fromTime"] = now - tapeFromSeconds * 1000L;
                add.Add(entry);
            }
            add.Add(new JsonObject { ["type"] = "Candle", ["symbol"] = symbol + "{=1m}", ["fromTime"] = now - candleDays * 86400000L });
            foreach (var source in orderSources)
                add.Add(new JsonObject { ["type"] = "Order", ["symbol"] = symbol, ["source"] = source });
        }
    }
    else
    {
        foreach (var symbol in symbols)
            add.Add(new JsonObject { ["type"] = "TimeAndSale", ["symbol"] = symbol, ["fromTime"] = now - historyHours * 3600000L });
    }

    subscribeTs = subscribeTs == 0 ? Stopwatch.GetTimestamp() : subscribeTs;
    Say($"subscribing on channel {channel}: {add.Count} subscriptions");
    await Send(new JsonObject { ["type"] = "FEED_SUBSCRIPTION", ["channel"] = channel, ["reset"] = true, ["add"] = add });
}

static double Num(JsonElement e) =>
    e.ValueKind == JsonValueKind.Number ? e.GetDouble()
    : e.ValueKind == JsonValueKind.String && double.TryParse(e.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v
    : double.NaN;

static string Str(JsonElement e) => e.ValueKind switch
{
    JsonValueKind.String => e.GetString(),
    JsonValueKind.Null => "null",
    _ => e.GetRawText()
};

string SessionOf(double utcMs)
{
    var et = TimeZoneInfo.ConvertTimeFromUtc(DateTimeOffset.FromUnixTimeMilliseconds((long)utcMs).UtcDateTime, eastern).TimeOfDay;
    if (et < TimeSpan.FromHours(4)) return "overnight";
    if (et < new TimeSpan(9, 30, 0)) return "pre";
    if (et < TimeSpan.FromHours(16)) return "regular";
    if (et < TimeSpan.FromHours(20)) return "post";
    return "overnight";
}

void Record(int channel, string type, JsonElement values, long ts, double utcMs)
{
    if (!channelFields[channel].TryGetValue(type, out var fields))
    {
        Say($"data for unconfigured event {type} on channel {channel}");
        return;
    }

    var key = $"ch{channel} {type}";
    if (!stats.TryGetValue(key, out var st))
        stats[key] = st = new EventStat();

    double elapsedMs = (ts - subscribeTs) * 1000.0 / Stopwatch.Frequency;
    st.Frames++;
    if (st.LastFrameTs != 0)
        st.Gaps.Add((ts - st.LastFrameTs) * 1000.0 / Stopwatch.Frequency);
    st.LastFrameTs = ts;
    if (st.FirstMs < 0)
        st.FirstMs = elapsedMs;
    st.LastMs = elapsedMs;

    int iSym = Array.IndexOf(fields, "eventSymbol");
    int iTime = Array.IndexOf(fields, "time");
    int iBidTime = Array.IndexOf(fields, "bidTime");
    int iAskTime = Array.IndexOf(fields, "askTime");
    int iExchange = Array.IndexOf(fields, "exchangeCode");
    int iBidExchange = Array.IndexOf(fields, "bidExchangeCode");
    int iFlags = Array.IndexOf(fields, "eventFlags");
    int iExt = Array.IndexOf(fields, "extendedTradingHours");
    int iAggressor = Array.IndexOf(fields, "aggressorSide");
    int iValid = Array.IndexOf(fields, "validTick");

    var all = values.EnumerateArray().ToArray();
    var rows = new List<JsonElement[]>();
    if (all.Length > 0 && all[0].ValueKind == JsonValueKind.Array)
    {
        foreach (var r in all)
            rows.Add(r.EnumerateArray().ToArray());
    }
    else
    {
        for (int i = 0; i + fields.Length <= all.Length; i += fields.Length)
            rows.Add(all[i..(i + fields.Length)]);
    }

    foreach (var row in rows)
    {
        st.Events++;
        st.Sample ??= "[" + string.Join(", ", row.Select(Str)) + "]";
        st.Last = "[" + string.Join(", ", row.Select(Str)) + "]";
        if (iSym >= 0)
            st.Symbols.Add(Str(row[iSym]));

        if (iFlags >= 0)
        {
            int flags = (int)Num(row[iFlags]);
            if ((flags & 0x08) != 0)
                st.SnapshotEndMs = elapsedMs;
            if ((flags & 0x10) != 0)
                st.Snipped++;
        }

        double eventMs = double.NaN;
        if (iTime >= 0)
            eventMs = Num(row[iTime]);
        else if (iBidTime >= 0 && iAskTime >= 0)
            eventMs = Math.Max(Num(row[iBidTime]), Num(row[iAskTime]));

        if (!double.IsNaN(eventMs) && eventMs > 0)
        {
            st.MinTime = Math.Min(st.MinTime, eventMs);
            st.MaxTime = Math.Max(st.MaxTime, eventMs);
            var session = SessionOf(eventMs);
            st.Sessions[session] = st.Sessions.GetValueOrDefault(session) + 1;
            double delay = utcMs - eventMs;
            if (Math.Abs(delay) < MaxLiveDelayMs)
                st.Delays.Add(delay);
        }

        var exchangeIndex = iExchange >= 0 ? iExchange : iBidExchange;
        if (exchangeIndex >= 0)
        {
            var code = Str(row[exchangeIndex]);
            st.Exchanges[code] = st.Exchanges.GetValueOrDefault(code) + 1;
        }

        if (iExt >= 0)
        {
            var ext = Str(row[iExt]);
            st.Ext[ext] = st.Ext.GetValueOrDefault(ext) + 1;
        }

        if (iAggressor >= 0)
        {
            var side = Str(row[iAggressor]);
            st.Aggressor[side] = st.Aggressor.GetValueOrDefault(side) + 1;
        }

        if (iValid >= 0)
        {
            var valid = Str(row[iValid]);
            st.Valid[valid] = st.Valid.GetValueOrDefault(valid) + 1;
        }
    }
}

async Task Handle(JsonElement root, string raw, long ts, double utcMs)
{
    var type = root.GetProperty("type").GetString();
    int channel = root.TryGetProperty("channel", out var c) ? c.GetInt32() : 0;

    switch (type)
    {
        case "SETUP":
            Say($"server SETUP: {Clip(raw)}");
            break;

        case "AUTH_STATE":
            var state = root.GetProperty("state").GetString();
            Say($"AUTH_STATE {state}");
            if (state == "UNAUTHORIZED")
            {
                if (authSent)
                {
                    Say("token was refused by DXLink");
                    runCts.Cancel();
                    break;
                }
                authSent = true;
                await Send(new JsonObject { ["type"] = "AUTH", ["channel"] = 0, ["token"] = quoteToken });
            }
            else if (state == "AUTHORIZED")
            {
                await Send(new JsonObject { ["type"] = "CHANNEL_REQUEST", ["channel"] = LiveChannel, ["service"] = "FEED", ["parameters"] = new JsonObject { ["contract"] = liveContract } });
                await Send(new JsonObject { ["type"] = "CHANNEL_REQUEST", ["channel"] = HistoryChannel, ["service"] = "FEED", ["parameters"] = new JsonObject { ["contract"] = "HISTORY" } });
            }
            break;

        case "CHANNEL_OPENED":
            Say($"CHANNEL_OPENED {channel}: {Clip(raw, 200)}");
            await SetupFeed(channel);
            break;

        case "FEED_CONFIG":
            Say($"FEED_CONFIG ch{channel}: {Clip(raw, 1400)}");
            if (root.TryGetProperty("eventFields", out var ef) && ef.ValueKind == JsonValueKind.Object)
                foreach (var p in ef.EnumerateObject())
                    channelFields[channel][p.Name] = p.Value.EnumerateArray().Select(x => x.GetString()).ToArray();
            await Subscribe(channel);
            break;

        case "FEED_DATA":
            var data = root.GetProperty("data");
            if (data.GetArrayLength() > 0 && data[0].ValueKind == JsonValueKind.String)
            {
                for (int i = 0; i + 1 < data.GetArrayLength(); i += 2)
                    Record(channel, data[i].GetString(), data[i + 1], ts, utcMs);
            }
            else
            {
                Say($"unexpected non-COMPACT data on channel {channel}: {Clip(raw, 300)}");
            }
            break;

        case "KEEPALIVE":
            break;

        default:
            Say($"{type} ch{channel}: {Clip(raw, 800)}");
            break;
    }
}

await ws.ConnectAsync(new Uri(dxlinkUrl), CancellationToken.None);
Say("socket connected");
await Send(new JsonObject { ["type"] = "SETUP", ["channel"] = 0, ["version"] = "0.1-ScalpFeed/0.1.0", ["keepaliveTimeout"] = 60, ["acceptKeepaliveTimeout"] = 60 });

var keepalive = Task.Run(async () =>
{
    try
    {
        while (!runCts.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromSeconds(30), runCts.Token);
            await Send(new JsonObject { ["type"] = "KEEPALIVE", ["channel"] = 0 });
        }
    }
    catch (OperationCanceledException) { }
    catch (WebSocketException) { }
});

runCts.CancelAfter(TimeSpan.FromSeconds(seconds));
var buffer = new byte[1 << 20];
var message = new MemoryStream();
try
{
    while (ws.State == WebSocketState.Open)
    {
        message.SetLength(0);
        ValueWebSocketReceiveResult r;
        do
        {
            r = await ws.ReceiveAsync(buffer.AsMemory(), runCts.Token);
            message.Write(buffer, 0, r.Count);
        } while (!r.EndOfMessage);

        // Stamp first so parsing and disk writes don't count toward arrival time.
        long ts = Stopwatch.GetTimestamp();
        double utcMs = DateTime.UtcNow.Subtract(DateTime.UnixEpoch).TotalMilliseconds;

        if (r.MessageType == WebSocketMessageType.Close)
        {
            Say($"server closed socket: {ws.CloseStatus} {ws.CloseStatusDescription}");
            break;
        }

        var raw = Encoding.UTF8.GetString(message.GetBuffer(), 0, (int)message.Length);
        await capture.WriteLineAsync($"{utcMs:F3}\t{Scrub(raw)}");
        using var doc = JsonDocument.Parse(raw);
        await Handle(doc.RootElement, raw, ts, utcMs);
    }
}
catch (OperationCanceledException)
{
    Say($"run window of {seconds}s ended");
}
catch (WebSocketException ex)
{
    Say($"socket error: {ex.Message}");
}

runCts.Cancel();
await keepalive;
await capture.FlushAsync();

// ---- Report ----
static string Pct(List<double> xs)
{
    if (xs.Count == 0)
        return "none";
    var s = xs.OrderBy(x => x).ToArray();
    double At(double p) => s[Math.Min(s.Length - 1, (int)(p * s.Length))];
    return $"n={s.Length} min={s[0]:F1} p50={At(.5):F1} p90={At(.9):F1} p99={At(.99):F1} max={s[^1]:F1}";
}

static string Counts(Dictionary<string, int> d) =>
    d.Count == 0 ? "none" : string.Join(", ", d.OrderByDescending(k => k.Value).Take(20).Select(k => $"{k.Key}:{k.Value}"));

string EtTime(double ms) =>
    ms is double.MaxValue or double.MinValue ? "n/a"
    : TimeZoneInfo.ConvertTimeFromUtc(DateTimeOffset.FromUnixTimeMilliseconds((long)ms).UtcDateTime, eastern).ToString("MM-dd HH:mm:ss.fff") + " ET";

Say("\n== Events received ==");
if (stats.Count == 0)
    Say("no market data events at all");

foreach (var (key, st) in stats)
{
    Say($"\n{key}: events={st.Events} frames={st.Frames} symbols={string.Join(",", st.Symbols)}");
    Say($"  first arrival {st.FirstMs:F0} ms after subscribe, last {st.LastMs:F0} ms, snapshot end {(st.SnapshotEndMs < 0 ? "not seen" : st.SnapshotEndMs.ToString("F0") + " ms")}, snipped={st.Snipped}");
    Say($"  event times {EtTime(st.MinTime)} to {EtTime(st.MaxTime)}");
    Say($"  sessions: {Counts(st.Sessions)}");
    Say($"  exchange codes: {Counts(st.Exchanges)}");
    if (st.Ext.Count > 0) Say($"  extendedTradingHours: {Counts(st.Ext)}");
    if (st.Aggressor.Count > 0) Say($"  aggressorSide: {Counts(st.Aggressor)}");
    if (st.Valid.Count > 0) Say($"  validTick: {Counts(st.Valid)}");
    Say($"  frame gaps ms: {Pct(st.Gaps)}");
    Say($"  delay vs event time ms (only within 20 min): {Pct(st.Delays)}");
    Say($"  first: {Clip(st.Sample ?? "", 420)}");
    Say($"  last:  {Clip(st.Last ?? "", 420)}");
}

Say($"\nsubscribed but silent: {string.Join(", ", new[] { $"ch{LiveChannel} Quote", $"ch{LiveChannel} Trade", $"ch{LiveChannel} TradeETH", $"ch{LiveChannel} TimeAndSale", $"ch{LiveChannel} Summary", $"ch{LiveChannel} Profile", $"ch{LiveChannel} Candle", $"ch{HistoryChannel} TimeAndSale" }.Where(k => !stats.ContainsKey(k)))}");
Say($"capture: {capturePath}");
return 0;

sealed class EventStat
{
    public int Events;
    public int Frames;
    public int Snipped;
    public HashSet<string> Symbols = new();
    public double FirstMs = -1;
    public double LastMs;
    public double SnapshotEndMs = -1;
    public double MinTime = double.MaxValue;
    public double MaxTime = double.MinValue;
    public long LastFrameTs;
    public string Sample;
    public string Last;
    public List<double> Gaps = new();
    public List<double> Delays = new();
    public Dictionary<string, int> Exchanges = new();
    public Dictionary<string, int> Sessions = new();
    public Dictionary<string, int> Ext = new();
    public Dictionary<string, int> Aggressor = new();
    public Dictionary<string, int> Valid = new();
}

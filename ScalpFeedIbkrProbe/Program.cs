// Read-only probe of IB Gateway's market data: which depth venues you get, and how quotes and
// the tick-by-tick tape behave. It speaks the TWS socket protocol directly and can only send the
// requests in AllowedRequests. Order messages aren't on that list, and it refuses to run against
// anything but a paper account.
//
//   ScalpFeedIbkrProbe --port 4002 --symbols SPY,NVDA,AEHL,LABT --quote-seconds 60 --tbt SPY,AEHL
//                      --depth-symbols SPY,NVDA,AEHL,LABT --depth-seconds 20 --venues ISLAND,NASDAQ,BATS,IEX,ARCA,NYSE
//                      --capture ibkr.tsv

using System.Diagnostics;
using System.Globalization;
using System.Net.Sockets;
using System.Text;
using static Fmt;

const int ClientId = 77;
const string ClientVersions = "v100..176";

// startApi, reqMarketDataType, reqMktData, cancelMktData, reqMktDepth, cancelMktDepth,
// reqMktDepthExchanges, reqSmartComponents, reqTickByTickData, cancelTickByTickData.
var AllowedRequests = new HashSet<int> { 71, 59, 1, 2, 10, 11, 82, 83, 97, 98 };

string Arg(string name, string fallback)
{
    int i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : fallback;
}

string[] ListArg(string name, string fallback) => Arg(name, fallback).Split(',', StringSplitOptions.RemoveEmptyEntries);

int port = int.Parse(Arg("--port", "4002"));
var symbols = ListArg("--symbols", "SPY,NVDA,AEHL,LABT");
int quoteSeconds = int.Parse(Arg("--quote-seconds", "60"));
var tickBySymbols = ListArg("--tbt", "");
var depthSymbols = ListArg("--depth-symbols", "SPY,NVDA,AEHL,LABT");
int depthSeconds = int.Parse(Arg("--depth-seconds", "20"));
var venues = ListArg("--venues", "ISLAND,NASDAQ,BATS,IEX,ARCA,NYSE");
var capturePath = Arg("--capture", "");
await using var capture = capturePath.Length > 0 ? new StreamWriter(capturePath, false, new UTF8Encoding(false), 1 << 20) : null;

using var tcp = new TcpClient { NoDelay = true };
await tcp.ConnectAsync("127.0.0.1", port);
var net = tcp.GetStream();
var gate = new object();
var clock = Stopwatch.StartNew();

var quotes = new Dictionary<int, QuoteStat>();
var depths = new Dictionary<int, DepthStat>();
var tapes = new Dictionary<int, TapeStat>();
var errors = new Dictionary<string, int>();
var depthExchanges = new List<string[]>();
var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
string dataTypes = "";

async Task WriteFrame(string text, bool rawPrefix = false)
{
    var body = Encoding.ASCII.GetBytes(text);
    var head = new byte[4];
    System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(head, body.Length);
    if (rawPrefix)
        await net.WriteAsync(Encoding.ASCII.GetBytes("API\0"));
    await net.WriteAsync(head);
    await net.WriteAsync(body);
}

async Task Send(int messageId, params object[] fields)
{
    if (!AllowedRequests.Contains(messageId))
        throw new InvalidOperationException($"message {messageId} isn't allowed by this probe");

    var sb = new StringBuilder().Append(messageId).Append('\0');
    foreach (var f in fields)
        sb.Append(f is bool b ? (b ? "1" : "0") : Convert.ToString(f, CultureInfo.InvariantCulture)).Append('\0');
    await WriteFrame(sb.ToString());
}

async Task<string[]> ReadFrame()
{
    var head = new byte[4];
    await net.ReadExactlyAsync(head);
    int length = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(head);
    var body = new byte[length];
    await net.ReadExactlyAsync(body);
    return Encoding.UTF8.GetString(body).Split('\0');
}

await WriteFrame(ClientVersions, rawPrefix: true);
var hello = await ReadFrame();
Console.WriteLine($"server version {hello[0]}, gateway time {hello.ElementAtOrDefault(1)}");
await Send(71, 2, ClientId, "");

double D(string s) => double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : double.NaN;

double UtcMs() => DateTime.UtcNow.Subtract(DateTime.UnixEpoch).TotalMilliseconds;

string I(double v) => v.ToString(CultureInfo.InvariantCulture);

void Handle(string[] f, double t)
{
    switch (int.Parse(f[0]))
    {
        case 15:
            var account = f[2].Split(',')[0];
            bool paper = account.StartsWith('D');
            Console.WriteLine($"account type: {(paper ? "paper" : "NOT PAPER")}, id starts {account[..Math.Min(2, account.Length)]}");
            if (paper)
                ready.TrySetResult();
            else
                ready.TrySetException(new InvalidOperationException("Refusing to run against a non-paper account."));
            break;

        case 4:
            var key = $"id {f[2]} code {f[3]}: {f[4]}";
            errors[key] = errors.GetValueOrDefault(key) + 1;
            if (int.TryParse(f[2], out int errId))
            {
                if (depths.TryGetValue(errId, out var failedDepth))
                    failedDepth.Error = $"{f[3]}: {f[4]}";
                if (quotes.TryGetValue(errId, out var failedQuote))
                    failedQuote.Error = $"{f[3]}: {f[4]}";
                if (tapes.TryGetValue(errId, out var failedTape))
                    failedTape.Error = $"{f[3]}: {f[4]}";
            }

            break;

        case 58:
            dataTypes += $" {f[2]}={f[3]}";
            break;

        case 80:
            int rows = int.Parse(f[1]);
            for (int i = 0; i < rows; i++)
                depthExchanges.Add(f.Skip(2 + (i * 5)).Take(5).ToArray());
            break;

        case 1:
            if (quotes.TryGetValue(int.Parse(f[2]), out var qp))
            {
                qp.OnPrice(int.Parse(f[3]), D(f[4]), t);
                if (int.Parse(f[3]) is 1 or 2)
                    capture?.WriteLine($"{I(UtcMs())}\tQ\t{qp.Symbol}\t{I(qp.Bid)}\t{I(qp.Ask)}");
            }

            break;

        case 2:
            if (quotes.TryGetValue(int.Parse(f[2]), out var qs))
                qs.OnSize(int.Parse(f[3]), t);
            break;

        case 46:
            if (quotes.TryGetValue(int.Parse(f[2]), out var qe))
                qe.OnString(int.Parse(f[3]), f[4]);
            break;

        case 13:
            if (depths.TryGetValue(int.Parse(f[2]), out var dl2))
                dl2.Apply(int.Parse(f[3]), f[4], int.Parse(f[5]), int.Parse(f[6]), D(f[7]), D(f[8]), t);
            break;

        case 12:
            if (depths.TryGetValue(int.Parse(f[2]), out var dl1))
                dl1.Apply(int.Parse(f[3]), "", int.Parse(f[4]), int.Parse(f[5]), D(f[6]), D(f[7]), t);
            break;

        case 99:
            if (tapes.TryGetValue(int.Parse(f[1]), out var tape))
            {
                int kind = int.Parse(f[2]);
                if (kind is 1 or 2)
                {
                    tape.OnPrint(D(f[4]), D(f[5]), f[7], f[8], t);
                    capture?.WriteLine($"{I(UtcMs())}\tT\t{tape.Symbol}\t{I(D(f[4]))}\t{I(D(f[5]))}\t{f[7]}\t{f[8]}\t{f[3]}");
                }
                else if (kind == 3)
                {
                    tape.OnBidAsk(t);
                    capture?.WriteLine($"{I(UtcMs())}\tB\t{tape.Symbol}\t{I(D(f[4]))}\t{I(D(f[5]))}");
                }
            }

            break;
    }
}

using var stop = new CancellationTokenSource();
var reader = Task.Run(async () =>
{
    try
    {
        while (!stop.IsCancellationRequested)
        {
            var frame = await ReadFrame();
            double t = clock.Elapsed.TotalMilliseconds;
            lock (gate)
                Handle(frame, t);
        }
    }
    catch (Exception ex) when (ex is IOException or ObjectDisposedException or EndOfStreamException)
    {
    }
});

await ready.Task.WaitAsync(TimeSpan.FromSeconds(15));
await Send(59, 1, 1);
await Send(82);
await Task.Delay(1500);

object[] Contract(string symbol, string exchange) => [0, symbol, "STK", "", "0.0", "", "", exchange, "", "USD", "", ""];

int id = 100;
foreach (var symbol in symbols)
{
    lock (gate)
        quotes[id] = new QuoteStat(symbol);
    await Send(1, [11, id, .. Contract(symbol, "SMART"), false, "", false, false, ""]);
    id++;
}

// Tick-by-tick: both the tape and the bid/ask stream, for each symbol asked for.
id = 400;
var tapeIds = new List<int>();
foreach (var symbol in tickBySymbols)
{
    foreach (var kind in new[] { "AllLast", "BidAsk" })
    {
        lock (gate)
            tapes[id] = new TapeStat(symbol, kind);
        await Send(97, [id, .. Contract(symbol, "SMART"), kind, 0, false]);
        tapeIds.Add(id);
        id++;
    }
}

await Task.Delay(TimeSpan.FromSeconds(quoteSeconds));

foreach (var tapeId in tapeIds)
    await Send(98, tapeId);

id = 200;
foreach (var symbol in depthSymbols)
{
    lock (gate)
        depths[id] = new DepthStat(symbol + " SMART");
    await Send(10, [5, id, .. Contract(symbol, "SMART"), 10, true, ""]);
    id++;
}

await Task.Delay(TimeSpan.FromSeconds(depthSeconds));
id = 200;
foreach (var symbol in depthSymbols)
    await Send(11, 1, id++, true);
await Task.Delay(1000);

id = 300;
foreach (var venue in venues)
{
    lock (gate)
        depths[id] = new DepthStat("SPY " + venue);
    await Send(10, [5, id, .. Contract("SPY", venue), 5, false, ""]);
    await Task.Delay(4000);
    await Send(11, 1, id, false);
    await Task.Delay(400);
    id++;
}

foreach (var q in quotes.Keys.ToList())
    await Send(2, 2, q);
await Task.Delay(300);

lock (gate)
{
    Console.WriteLine($"\n=== Market data type (1 real-time, 2 frozen, 3 delayed, 4 delayed frozen):{dataTypes}");

    var us = new HashSet<string> { "ISLAND", "NASDAQ", "BATS", "IEX", "ARCA", "NYSE", "BEX", "ARCAEDGE", "PINK", "SMART", "NASDTAS" };
    Console.WriteLine("\n=== US depth services IBKR offers (exchange, data type) ===");
    Console.WriteLine("  " + string.Join("  ", depthExchanges.Where(r => r[1] == "STK" && us.Contains(r[0])).Select(r => $"{r[0]}:{r[3]}").Distinct()));

    Console.WriteLine("\n=== IBKR quotes ===");
    foreach (var q in quotes.Values)
        Console.WriteLine(q.Report());

    Console.WriteLine("\n=== IBKR tick-by-tick ===");
    foreach (var tape in tapes.Values)
        Console.WriteLine(tape.Report());

    Console.WriteLine("\n=== Depth ===");
    foreach (var d in depths.Values)
        Console.WriteLine(d.Report());

    Console.WriteLine("\n=== Messages from Gateway ===");
    foreach (var (message, count) in errors.OrderBy(e => e.Key))
    {
        if (!message.Contains("farm connection") && !message.Contains("farm is"))
            Console.WriteLine($"  x{count} {message[..Math.Min(900, message.Length)]}");
    }
}

stop.Cancel();
tcp.Close();
return 0;

internal static class Fmt
{
    public static string Pct(List<double> xs)
    {
        if (xs.Count == 0)
            return "none";
        var s = xs.Order().ToArray();
        double At(double p) => s[Math.Min(s.Length - 1, (int)(p * s.Length))];
        return $"n={s.Length} min={s[0]:F1} p50={At(.5):F1} p90={At(.9):F1} p99={At(.99):F1} max={s[^1]:F1}";
    }

    public static string Top(Dictionary<string, int> d, int n = 14) =>
        d.Count == 0 ? "none" : string.Join(", ", d.OrderByDescending(k => k.Value).Take(n).Select(k => $"{k.Key}:{k.Value}"));

    /// <summary>Messages closer than this count as one delivery, so timers show up.</summary>
    public const double BurstMs = 2;
}

internal sealed class QuoteStat
{
    private double lastBook = -1;
    private readonly List<double> bookGaps = new(), spreads = new();
    private readonly Dictionary<string, int> bidVenues = new(), askVenues = new();
    private int bookChanges;

    public QuoteStat(string symbol) => this.Symbol = symbol;

    public string Symbol { get; }

    public double Bid { get; private set; } = double.NaN;

    public double Ask { get; private set; } = double.NaN;

    public string Error { get; set; }

    public void OnPrice(int type, double price, double t)
    {
        if (type is not (1 or 2) || price <= 0)
            return;
        if (type == 1)
            this.Bid = price;
        else
            this.Ask = price;

        this.bookChanges++;
        if (this.lastBook >= 0 && t - this.lastBook > BurstMs)
            this.bookGaps.Add(t - this.lastBook);
        this.lastBook = t;
        if (this.Ask > this.Bid && this.Bid > 0)
            this.spreads.Add(this.Ask - this.Bid);
    }

    public void OnSize(int type, double t)
    {
    }

    public void OnString(int type, string value)
    {
        var target = type == 32 ? this.bidVenues : type == 33 ? this.askVenues : null;
        if (target == null)
            return;
        foreach (var letter in value)
            target[letter.ToString()] = target.GetValueOrDefault(letter.ToString()) + 1;
    }

    public string Report()
    {
        var s = this.spreads.Order().ToArray();
        string spread = s.Length == 0 ? "none" : $"median {s[s.Length / 2]:F4}, at 1c {s.Count(x => x <= 0.0101) / (double)s.Length:P0}, at 2c {s.Count(x => x > 0.0101 && x <= 0.0201) / (double)s.Length:P0}";
        return $"  {this.Symbol,-5} {(this.Error != null ? "ERROR " + this.Error + " | " : "")}last {this.Bid}/{this.Ask}, price updates {this.bookChanges}\n" +
               $"        delivery gaps ms: {Pct(this.bookGaps)}\n" +
               $"        spread: {spread}\n" +
               $"        venues on the bid: {Top(this.bidVenues)}\n" +
               $"        venues on the ask: {Top(this.askVenues)}";
    }
}

internal sealed class TapeStat
{
    private readonly string kind;
    private readonly Dictionary<string, int> venues = new();
    private readonly Dictionary<string, int> conditions = new();
    private readonly List<double> gaps = new();
    private double last = -1;
    private int count;
    private double volume;

    public TapeStat(string symbol, string kind)
    {
        this.Symbol = symbol;
        this.kind = kind;
    }

    public string Symbol { get; }

    public string Error { get; set; }

    public void OnPrint(double price, double size, string venue, string condition, double t)
    {
        this.count++;
        this.volume += size;
        this.venues[venue] = this.venues.GetValueOrDefault(venue) + 1;
        this.conditions[condition] = this.conditions.GetValueOrDefault(condition) + 1;
        this.Delivery(t);
    }

    public void OnBidAsk(double t)
    {
        this.count++;
        this.Delivery(t);
    }

    public string Report()
    {
        var head = $"  {this.Symbol,-5} {this.kind,-8} {(this.Error != null ? "ERROR " + this.Error + " | " : "")}events {this.count}, delivery gaps ms: {Pct(this.gaps)}";
        return this.kind == "AllLast"
            ? $"{head}\n        volume {this.volume:F0}\n        venues: {Top(this.venues, 18)}\n        conditions: {Top(this.conditions, 8)}"
            : head;
    }

    private void Delivery(double t)
    {
        if (this.last >= 0 && t - this.last > BurstMs)
            this.gaps.Add(t - this.last);
        this.last = t;
    }
}

internal sealed class DepthStat
{
    private readonly string name;
    private readonly List<(double Price, double Size, string Maker)>[] book = [new(), new()];
    private readonly Dictionary<string, int> makers = new();
    private readonly List<double> gaps = new();
    private double last = -1;
    private int updates;

    public DepthStat(string name) => this.name = name;

    public string Error { get; set; }

    public void Apply(int position, string maker, int operation, int side, double price, double size, double t)
    {
        this.updates++;
        if (this.last >= 0 && t - this.last > BurstMs)
            this.gaps.Add(t - this.last);
        this.last = t;
        if (maker.Length > 0)
            this.makers[maker] = this.makers.GetValueOrDefault(maker) + 1;

        var rows = this.book[side == 1 ? 0 : 1];
        if (operation == 0 && position <= rows.Count)
            rows.Insert(position, (price, size, maker));
        else if (operation == 1 && position < rows.Count)
            rows[position] = (price, size, maker);
        else if (operation == 2 && position < rows.Count)
            rows.RemoveAt(position);
    }

    public string Report()
    {
        if (this.updates == 0)
            return $"  {this.name,-16} no data{(this.Error != null ? ": " + this.Error[..Math.Min(150, this.Error.Length)] : "")}";

        string Side(List<(double Price, double Size, string Maker)> rows) =>
            string.Join("  ", rows.Take(4).Select(r => $"{r.Price}x{r.Size}{(r.Maker.Length > 0 ? "@" + r.Maker : "")}"));

        return $"  {this.name,-16} updates {this.updates}, delivery gaps ms: {Pct(this.gaps)}\n" +
               $"        venues in rows: {Top(this.makers, 20)}\n" +
               $"        bids: {Side(this.book[0])}\n" +
               $"        asks: {Side(this.book[1])}";
    }
}

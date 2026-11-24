// Tests the plugin's feed core without Quantower. Read-only.
//   ScalpFeedHarness parse <capture.jsonl>
//   ScalpFeedHarness rest <symbol> <search text>
//   ScalpFeedHarness history <symbol>
//   ScalpFeedHarness live <symbols,comma,separated> <seconds>

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ScalpFeedIndicators;
using ScalpFeedVendor.Feed;

Log.FilePath = Path.Combine(Log.AppDir, "harness.log");
Log.Echo = true;

var api = new Func<TastytradeApi>(() => new TastytradeApi(Path.Combine(Log.AppDir, "tastytrade.json")));
int failures = 0;

void Check(bool ok, string what)
{
    Console.WriteLine($"{(ok ? "PASS" : "FAIL")}  {what}");
    if (!ok)
        failures++;
}

switch (args[0])
{
    case "parse":
        RunParse(args[1]);
        break;
    case "rest":
        await RunRest(args[1], args[2]);
        break;
    case "history":
        await RunHistory(args[1]);
        break;
    case "todaybar":
        await RunTodayBar(args.Length > 1 ? args[1] : "SPY");
        break;
    case "live":
        await RunLive(args[1].Split(','), int.Parse(args[2]));
        break;
    case "stoptest":
        await RunStopTest();
        break;
    case "dayvol":
        await RunDayVolume(args[1].Split(','));
        break;
    case "crosstest":
        await RunCrossTest();
        break;
    case "restquote":
        await RunRestQuote(args[1], int.Parse(args[2]));
        break;
    case "ibkr":
        await RunIbkr(int.Parse(args[1]));
        break;
    case "ibkrrows":
        await RunIbkrRows(int.Parse(args[1]), int.Parse(args[2]), args[3..]);
        break;
    case "positions":
        await RunPositions(int.Parse(args[1]));
        break;
    case "raw":
        using (var rawClient = api())
            Console.WriteLine(Log.Scrub(await rawClient.GetRawAsync(args[1], CancellationToken.None)));
        break;
    case "dayrange":
        await RunDayRange(args[1].Split(','));
        break;
    case "ibkrbook":
        await RunIbkrBook(int.Parse(args[1]), args[2], args.Length > 3 ? int.Parse(args[3]) : 8);
        break;
    case "ibkrtape":
        await RunIbkrTape(int.Parse(args[1]), int.Parse(args[2]), args[3..]);
        break;
    case "ibkrslots":
        await RunIbkrSlots(int.Parse(args[1]), args[2..]);
        break;
    case "ibkrswitch":
        await RunIbkrSwitch(int.Parse(args[1]), args[2..]);
        break;
    case "connectretry":
        await RunConnectRetry();
        break;
    case "depthpacing":
        await RunDepthPacing();
        break;
    case "venuetops":
        await RunVenueTops();
        break;
    case "feedclock":
        await RunFeedClock();
        break;
    case "stallwatch":
        await RunStallWatch();
        break;
    case "luld":
        await RunLuld();
        break;
    case "hotkey":
        await RunHotkey();
        break;
    case "trader":
        await RunTrader();
        break;
    case "health":
        RunHealth();
        break;
    case "sessionvwap":
        RunSessionVwap(args.Length > 1 ? args[1] : null);
        break;
    case "tapewatch":
        await RunTapeWatch();
        break;
    case "stayawake":
        Check(StayAwake.Renew(), "Windows accepts the stay-awake request");
        break;
    case "tapemerge":
        RunTapeMerge();
        break;
    case "pricebands":
        var usual = PriceBands.ForDisplay([new TickBand(1.0, 0.0001), new TickBand(double.PositiveInfinity, 0.01)]);
        Check(usual.Count == 3 && usual[0] == (0.0, 0.10, 0.0001, 4) && usual[1] == (0.10, 1.0, 0.001, 3) && usual[2] == (1.0, double.PositiveInfinity, 0.01, 2),
            $"4 decimals under 10 cents, 3 to a dollar with a $0.001 tick, 2 from a dollar up ({string.Join(" ", usual)})");
        Check(PriceBands.ForDisplay([]).SequenceEqual(usual), "a lookup with no tick sizes gets the same bands");
        var across = PriceBands.ForDisplay([new TickBand(double.PositiveInfinity, 0.01)]);
        Check(across.Count == 3 && across[0] == (0.0, 0.10, 0.01, 4) && across[1] == (0.10, 1.0, 0.01, 3) && across[2] == (1.0, double.PositiveInfinity, 0.01, 2), "one band across both lines splits at each, keeping its tick size");
        var several = PriceBands.ForDisplay([new TickBand(0.05, 0.0001), new TickBand(0.5, 0.0005), new TickBand(1.0, 0.001), new TickBand(10.0, 0.01), new TickBand(double.PositiveInfinity, 0.05)]);
        Check(several.Select(b => b.Decimals).SequenceEqual(new[] { 4, 4, 3, 3, 2, 2 }) && several.Select(b => b.TickSize).SequenceEqual(new[] { 0.0001, 0.0005, 0.001, 0.001, 0.01, 0.05 }),
            $"several bands keep their tick sizes where the decimals can write them, and the one across 10 cents splits ({string.Join(" ", several)})");
        Check(PriceBands.ForDisplay([new TickBand(1.0, 0.0025), new TickBand(double.PositiveInfinity, 0.005)]).Select(b => b.TickSize).SequenceEqual(new[] { 0.0025, 0.003, 0.01 }), "a tick too fine for its decimals goes up to the next they can write");
        Check(new[] { usual, across, several }.All(list => list.All(b => b.Decimals >= PriceBands.DecimalsOf(b.TickSize))),
            "no band writes fewer decimals than its tick size needs (Quantower recursed forever on 3 decimals with a $0.0001 tick)");
        // Two steps of one tick from any price, on the tick or off it, each rounded to the band's decimals either way,
        // must each move: a step that rounds back to where it was is what Quantower never got out of. From a price off
        // the tick the first can move (0.7135 to 0.714) where the second can't.
        static bool Moves(double price, double tick, int decimals) => new[] { MidpointRounding.ToEven, MidpointRounding.AwayFromZero }.All(mode => new[] { (decimal)tick, -(decimal)tick }.All(step =>
        {
            decimal from = (decimal)price, one = Math.Round(from + step, decimals, mode), two = Math.Round(one + step, decimals, mode);
            return step > 0 ? one > from && two > one : one < from && two < one;
        }));
        Check(Enumerable.Range(1, 19_999).Select(n => n / 10_000.0).All(p => { var b = usual.First(b => p < b.High); return Moves(p, b.TickSize, b.Decimals); }), "one-tick steps always move, from every $0.0001 price to $2");
        Check(!Enumerable.Range(1000, 9000).Any(n => Moves(n / 10_000.0, 0.0001, 3)), "where with 3 decimals and the $0.0001 tick, as before 11-23, they get stuck from every price between 10 cents and a dollar");
        Check(PriceBands.DecimalsOf(0.0001) == 4 && PriceBands.DecimalsOf(0.0005) == 4 && PriceBands.DecimalsOf(0.001) == 3 && PriceBands.DecimalsOf(0.01) == 2 && PriceBands.DecimalsOf(0.05) == 2 && PriceBands.DecimalsOf(1) == 0,
            "tick sizes' decimals are counted exactly");
        Check(several.Zip(several.Skip(1)).All(p => p.First.High == p.Second.Low) && several[0].Low == 0, "the bands run on from 0 with no gaps or overlaps");
        Check(PriceBands.ForDisplay([new TickBand(0.10, 0.0001), new TickBand(double.PositiveInfinity, 0.01)]).Select(b => b.Decimals).SequenceEqual(new[] { 4, 3, 2 }), "a band ending exactly at 10 cents isn't split into an empty piece");
        Check(DepthSizes.ToHundreds(2870) == 28 && DepthSizes.ToHundreds(100) == 1 && DepthSizes.ToHundreds(199) == 1, "depth sizes truncate to hundreds");
        Check(Enumerable.Range(1, 100000).All(n => DepthSizes.ToHundreds(n * 100) == n), "every whole hundred comes out exact");
        Check(!DepthSizes.Shows(100, 2.55, 2.57, 2.57) && !DepthSizes.Shows(99, 2.55, 2.57, 2.57), "behind the best price, 100 shares or fewer don't show");
        Check(DepthSizes.Shows(101, 2.55, 2.57, 2.57) && DepthSizes.ToHundreds(101) == 1, "101 shares shows anywhere, as 1");
        Check(DepthSizes.Shows(100, 2.57, 2.57, 2.57), "100 shares at the best price shows");
        Check(DepthSizes.Shows(100, 2.57, 2.57, 2.56) && DepthSizes.Shows(100, 2.56, 2.57, 2.56), "the book's best and the quote's price both count, when they differ");
        Check(DepthSizes.Shows(85, 342.00, 342.00, 342.00) && DepthSizes.ToHundreds(85) == 1, "85 shares at the best price shows, reading 1, not 0");
        Check(!DepthSizes.Shows(0, 2.57, 2.57, 2.57), "an empty row never shows");
        Check(DepthSizes.Shows(100, 2.57, 2.57, double.NaN), "with no quote, the book's best still shows");
        Check(!DepthSizes.Shows(500, 2.55, 2.57, 2.57, "NSDQ") && !DepthSizes.Shows(200, 2.55, 2.57, 2.57, "NSDQ"), "behind the best price, Nasdaq's own rows of 500 shares or fewer don't show");
        Check(DepthSizes.Shows(501, 2.55, 2.57, 2.57, "NSDQ") && DepthSizes.ToHundreds(501) == 5, "501 shares on Nasdaq shows behind the best, as 5");
        Check(DepthSizes.Shows(300, 2.57, 2.57, 2.57, "NSDQ") && DepthSizes.Shows(300, 2.56, 2.57, 2.56, "NSDQ"), "Nasdaq's rows at the best price, book's or quote's, show at any size");
        Check(DepthSizes.Shows(200, 2.55, 2.57, 2.57, "TSSM") && DepthSizes.Shows(200, 2.55, 2.57, 2.57, "BATS") && !DepthSizes.Shows(100, 2.55, 2.57, 2.57, "MLCO"),
            "market makers and other venues keep the 100-share line");
        var depthRows = new List<IbkrDepthRow> { new(2.55, 100, "A"), new(2.57, 100, "B"), new(2.56, 300, "C") };
        Check(DepthSizes.Best(depthRows, true) == 2.57 && DepthSizes.Best(depthRows, false) == 2.55 && double.IsNaN(DepthSizes.Best(new List<IbkrDepthRow>(), true)), "the best price is found on each side");
        break;
    case "printplace":
        Check(PrintPlaces.Of(2.74, 2.70, 2.73) == PrintPlace.AboveAsk, "above the ask");
        Check(PrintPlaces.Of(2.73, 2.70, 2.73) == PrintPlace.AtAsk, "at the ask");
        Check(PrintPlaces.Of(2.7188, 2.70, 2.73) == PrintPlace.Between, "between, including a sub-penny price");
        Check(PrintPlaces.Of(2.70, 2.70, 2.73) == PrintPlace.AtBid, "at the bid");
        Check(PrintPlaces.Of(2.69, 2.70, 2.73) == PrintPlace.BelowBid, "below the bid");
        Check(PrintPlaces.Of(2.70, double.NaN, double.NaN) == PrintPlace.Unknown, "no quote yet is unknown");
        Check(PrintPlaces.Of(2.70, -1, 2.73) == PrintPlace.Unknown && PrintPlaces.Of(2.70, 2.69, -1) == PrintPlace.Unknown, "a side with no market is unknown");
        Check(PrintPlaces.Of(2.70, 2.70, 2.70) == PrintPlace.AtAsk, "a locked market counts as at the ask");

        long tick = TimeSpan.TicksPerMillisecond;
        var window = new QuoteWindow();
        long now = 1_000_000 * tick;
        window.Add(2.70, 2.73, now - (5000 * tick));
        Check(window.Judge(2.74, 2.70, 2.73, double.NaN, double.NaN, now) == PrintPlace.AboveAsk, "beyond the NBBO, with nothing else to say otherwise, is above the ask");
        Check(window.Judge(2.69, 2.70, 2.73, double.NaN, double.NaN, now) == PrintPlace.BelowBid, "and below the bid likewise");
        Check(window.Judge(2.74, 2.70, 2.73, 2.60, 2.80, now) == PrintPlace.AtAsk, "not beyond its own quote means only at the ask");
        Check(window.Judge(2.69, 2.70, 2.73, 2.60, 2.80, now) == PrintPlace.AtBid, "and only at the bid");
        Check(window.Judge(2.74, 2.70, 2.73, 2.60, 2.73, now) == PrintPlace.AboveAsk, "beyond its own quote as well is above the ask");

        window = new QuoteWindow();
        window.Add(2.72, 2.75, now - (500 * tick));
        window.Add(2.70, 2.73, now - (100 * tick));
        Check(window.Judge(2.74, 2.70, 2.73, double.NaN, double.NaN, now) == PrintPlace.AtAsk, "an ask of the last moment that covers it means at the ask");
        Check(window.Judge(2.76, 2.70, 2.73, double.NaN, double.NaN, now) == PrintPlace.AboveAsk, "beyond every ask of the last moment is above the ask");
        window = new QuoteWindow();
        window.Add(2.68, 2.72, now - (500 * tick));
        window.Add(2.70, 2.73, now - (100 * tick));
        Check(window.Judge(2.69, 2.70, 2.73, double.NaN, double.NaN, now) == PrintPlace.AtBid, "a bid of the last moment that covers it means at the bid");
        Check(window.Judge(2.67, 2.70, 2.73, double.NaN, double.NaN, now) == PrintPlace.BelowBid, "beyond every bid of the last moment is below the bid");

        window = new QuoteWindow();
        window.Add(2.72, 2.75, now - (2000 * tick));
        window.Add(2.70, 2.73, now - (1500 * tick));
        Check(window.Judge(2.74, 2.70, 2.73, double.NaN, double.NaN, now) == PrintPlace.AboveAsk, "an old quote no longer counts, though the one still standing does");
        window = new QuoteWindow();
        window.Add(-1, -1, now);
        Check(window.Judge(2.74, -1, -1, double.NaN, double.NaN, now) == PrintPlace.Unknown, "a halt stays unknown");
        break;
    case "mergereplay":
        RunMergeReplay(args[1], args[2]);
        break;
    case "ibkrbars":
        await RunIbkrBars();
        break;
    case "ibkrbarslive":
        await RunIbkrBarsLive(int.Parse(args[1]), args[2]);
        break;
    case "tnsmatrix":
        await RunTnsMatrix(args[1], int.Parse(args[2]), args.Length > 3 && args[3] == "delayed");
        break;
    case "refused":
        await RunRefusedCredentials();
        break;
    case "tokenrefused":
        await RunTokenRefused();
        break;
    case "printfilter":
        RunPrintFilter(args[1..]);
        break;
    case "scope":
        await RunScope(args[1]);
        break;
    case "alarm":
        await RunAlarmTest();
        break;
    case "float":
        await RunFloat();
        break;
    case "splits":
        await RunSplits(args.Length > 1 ? args[1] : null);
        break;
    case "charted":
        RunCharted();
        break;
    case "ranks":
        RunRanks();
        break;
    case "rvol":
        await RunRvol(args[1..]);
        break;
    case "daychange":
        RunDayChange();
        break;
    case "scan":
        await RunScan(args.Length > 1 ? int.Parse(args[1], CultureInfo.InvariantCulture) : 0, args.Length > 2 ? args[2] : null);
        break;
    case "searchfilter":
        RunSearchFilter();
        break;
    case "restrictions":
        await RunRestrictions(args.Length > 1 ? int.Parse(args[1], CultureInfo.InvariantCulture) : 0);
        break;
}

Console.WriteLine(failures == 0 ? "\nALL CHECKS PASSED" : $"\n{failures} CHECK(S) FAILED");
return failures == 0 ? 0 : 1;

// IBKR's top gainers scanner against the stand-in, then, given a port, on the real Gateway (read-only).
async Task RunScan(int port, string liveCode)
{
    Check(IbkrScan.From(JsonNode.Parse("false")) == null && IbkrScan.From(null) == new IbkrScan(), "topGainers in ibkr.json: false turns it off, and it's on by default");
    var custom = IbkrScan.From(JsonNode.Parse("""{"priceAbove":1,"priceBelow":20,"volumeAbove":100000}"""));
    Check(custom == new IbkrScan(PriceAbove: 1, PriceBelow: 20, VolumeAbove: 100000), "its filters can be set");
    var haltScan = IbkrScan.HaltDefaults;
    Check(haltScan is { Code: "HALTED", AfterHoursCode: "HALTED", Rows: 50, PriceAbove: 0.10, PriceBelow: 25, VolumeAbove: 1, ChangeAbove: 0, IntervalSeconds: 2, Location: "STK.US.MAJOR" }
          && haltScan.Filter("HALTED") == "",
        "the halts scan is HALTED: 50 rows, $0.10-$25, traded today, no change filter, every 2 s");
    Check(IbkrScan.From(JsonNode.Parse("false"), haltScan) == null && IbkrScan.From(null, haltScan) == haltScan
          && IbkrScan.From(JsonNode.Parse("""{"rows":20}"""), haltScan) == haltScan with { Rows = 20 },
        "halts in ibkr.json: false turns it off, it's on by default, and its filters can be set");

    var eastern = TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");
    DateTime Et(int h, int m, int s) => TimeZoneInfo.ConvertTimeToUtc(new DateTime(2026, 11, 16, h, m, s, DateTimeKind.Unspecified), eastern);
    var plain = new IbkrScan();
    Check(plain is { Rows: 25, PriceAbove: 0.10, PriceBelow: 25, VolumeAbove: 50_000, ChangeAbove: 10, IntervalSeconds: 2, Location: "STK.US.MAJOR" },
        "by default it's momo-algo's: 25 rows, $0.10-$25, over 50,000 shares, up 10%, every 2 s");
    Check(plain.CodeAt(Et(15, 59, 59)) == "TOP_PERC_GAIN" && plain.CodeAt(Et(16, 0, 0)) == "TOP_AFTER_HOURS_PERC_GAIN"
          && plain.CodeAt(Et(3, 59, 59)) == "TOP_AFTER_HOURS_PERC_GAIN" && plain.CodeAt(Et(4, 0, 0)) == "TOP_PERC_GAIN",
        "after-hours gainers from 16:00 to 4:00 ET, Top % Gainers from 4:00");
    Check(plain.Filter("TOP_PERC_GAIN") == "changePercAbove=10;" && plain.Filter("TOP_AFTER_HOURS_PERC_GAIN") == "afterHoursChangePercAbove=10;",
        "the change filter matches the scan: the after-hours one filters by after-hours change");
    Check(plain.IntervalAt(Et(4, 0, 0)) == 2 && plain.IntervalAt(Et(19, 59, 59)) == 2 && plain.IntervalAt(Et(20, 0, 0)) == 60 && plain.IntervalAt(Et(3, 59, 59)) == 60,
        "every 2 s from 4:00 to 20:00 ET, every minute outside");

    var clock = Et(15, 59, 50);
    {
        using var gateway = new FakeGateway();
        var sink = new IbkrTestSink();
        var client = new IbkrClient(new IbkrConfig(true, "127.0.0.1", gateway.Port, 1, false, TopGainers: custom)) { ScanClock = () => clock };
        client.SetSink(sink);
        client.Start();
        var timer = Stopwatch.StartNew();
        async Task<string[]> NextRequest(int count, double seconds)
        {
            var until = timer.Elapsed + TimeSpan.FromSeconds(seconds);
            while (timer.Elapsed < until && gateway.ScanRequests.Count < count)
                await Task.Delay(10);
            return gateway.ScanRequests.Skip(count - 1).FirstOrDefault() ?? [];
        }

        int Id(string[] request) => request.Length > 1 ? int.Parse(request[1], CultureInfo.InvariantCulture) : 0;

        var f = await NextRequest(1, 10);
        int first = Id(f);
        Check(f.Length == 26 && f[0] == "22" && first > IbkrClient.ScanRequestId && f[2] == "25" && f[3] == "STK" && f[4] == "STK.US.MAJOR"
              && f[5] == "TOP_PERC_GAIN" && f[6] == "1" && f[7] == "20" && f[8] == "100000" && f[19] == "0" && f[23] == "changePercAbove=10;",
            $"a scan goes once live, with its filters ({string.Join("|", f)})");

        var answered = timer.Elapsed;
        gateway.SendScan(first, ("GDHG", "STK"), ("BRK B", "STK"), ("XYZ", "WAR"), ("GMM", "STK"));
        gateway.SendScan(12345, ("NOPE", "STK"));
        await Task.Delay(300);
        Check(sink.Scans.Count == 1 && sink.Scans.First().SequenceEqual(new[] { "GDHG", "BRK B", "GMM" }), $"its rows reach the bridge in rank order, stocks only ({string.Join(" ", sink.Scans.FirstOrDefault() ?? [])})");
        Check(gateway.ScanCancels.SequenceEqual(new[] { first }), "and it's cancelled as soon as it answers");

        var second = await NextRequest(2, 5);
        var gap = timer.Elapsed - answered;
        Check(Id(second) > first && second.Length > 5 && second[5] == "TOP_PERC_GAIN" && gap > TimeSpan.FromSeconds(1.4) && gap < TimeSpan.FromSeconds(2.6),
            $"the next goes about 2 s after the last, under a new id ({gap.TotalSeconds:F1} s)");

        // The close: the next scan is the after-hours one, with the after-hours change filter.
        clock = Et(16, 0, 1);
        gateway.SendScan(Id(second), ("GDHG", "STK"));
        var third = await NextRequest(3, 5);
        Check(third.Length > 23 && third[5] == "TOP_AFTER_HOURS_PERC_GAIN" && third[23] == "afterHoursChangePercAbove=10;",
            $"from 16:00 ET it's the after-hours scan ({(third.Length > 23 ? third[5] + " " + third[23] : "nothing")})");

        // A late answer from an old scan is ignored; a refused one lets the next go when its interval is up.
        gateway.SendScan(first, ("OLD", "STK"));
        gateway.SendError(Id(third), 162, "API scanner subscription cancelled");
        await Task.Delay(300);
        Check(sink.Scans.Last().SequenceEqual(new[] { "GDHG" }) && gateway.ScanRequests.Count == 3, "an old scan's answer is ignored");
        gateway.SendError(Id(third), 200, "No security definition has been found for the request");
        var fourth = await NextRequest(4, 5);
        Check(Id(fourth) > Id(third) && gateway.ScanCancels.Contains(Id(third)), "a scan IBKR refuses is cancelled, and doesn't stop the next");
        await client.StopAsync();
    }

    // Both scans: they take turns, one in flight at a time, and each answer goes to its own list.
    {
        using var gateway = new FakeGateway();
        var sink = new IbkrTestSink();
        var client = new IbkrClient(new IbkrConfig(true, "127.0.0.1", gateway.Port, 1, false, TopGainers: new IbkrScan(), Halts: haltScan)) { ScanClock = () => Et(10, 0, 0) };
        client.SetSink(sink);
        client.Start();
        var timer = Stopwatch.StartNew();
        async Task<string[]> Nth(int count)
        {
            var until = timer.Elapsed + TimeSpan.FromSeconds(10);
            while (timer.Elapsed < until && gateway.ScanRequests.Count < count)
                await Task.Delay(10);
            return gateway.ScanRequests.Skip(count - 1).FirstOrDefault() ?? [];
        }

        int IdOf(string[] request) => request.Length > 1 ? int.Parse(request[1], CultureInfo.InvariantCulture) : 0;

        var gainers = await Nth(1);
        Check(gainers.Length > 5 && gainers[5] == "TOP_PERC_GAIN", "the gainers go first");
        await Task.Delay(600);
        Check(gateway.ScanRequests.Count == 1, "and the halts wait for their answer");
        gateway.SendScan(IdOf(gainers), ("GDHG", "STK"));
        var halted = await Nth(2);
        Check(halted.Length == 26 && halted[5] == "HALTED" && halted[2] == "50" && halted[7] == "25" && halted[8] == "1" && halted[23] == "",
            $"then the halts: traded today, no change filter ({string.Join("|", halted)})");
        gateway.SendScan(IdOf(halted), ("NXXT", "STK"), ("AFNXU", "STK"), ("XYZ", "WAR"));
        await Task.Delay(300);
        Check(sink.Scans.Count == 1 && sink.Scans.First().SequenceEqual(new[] { "GDHG" }) && sink.HaltScans.Count == 1 && sink.HaltScans.First().SequenceEqual(new[] { "NXXT", "AFNXU" }),
            "each answer reaches its own list, stocks only");
        Check(gateway.ScanCancels.Contains(IdOf(gainers)) && gateway.ScanCancels.Contains(IdOf(halted)), "and each is cancelled as it answers");
        var again = await Nth(3);
        Check(again.Length > 5 && again[5] == "TOP_PERC_GAIN" && IdOf(again) > IdOf(halted), "then the gainers again, under a new id");

        // Nothing halted: IBKR answers 165 "no items retrieved" (paper Gateway, 11-17), not an empty list.
        gateway.SendScan(IdOf(again), ("GDHG", "STK"));
        var noneHalted = await Nth(4);
        gateway.SendError(IdOf(noneHalted), 165, "Historical Market Data Service query message:no items retrieved");
        await Task.Delay(300);
        Check(noneHalted.Length > 5 && noneHalted[5] == "HALTED" && sink.HaltScans.Count == 2 && sink.HaltScans.Last().Count == 0 && gateway.ScanCancels.Contains(IdOf(noneHalted)),
            "a scan with nothing to list answers 165 no items retrieved, and its list goes empty");
        var refused = await Nth(5);
        gateway.SendError(IdOf(refused), 165, "Historical Market Data Service query message:Trading TWS session is connected from a different IP address");
        await Task.Delay(300);
        Check(refused.Length > 5 && refused[5] == "TOP_PERC_GAIN" && sink.Scans.Count == 2 && sink.Scans.Last().SequenceEqual(new[] { "GDHG" }),
            "any other 165 is a failure, and leaves its list alone");
        await client.StopAsync();
    }

    if (port == 0)
        return;

    var live = new IbkrTestSink();
    var real = new IbkrClient(new IbkrConfig(true, "127.0.0.1", port, 87, IbkrConfig.Load().AllowLiveAccount,
        TopGainers: liveCode == null ? new IbkrScan() : new IbkrScan(liveCode, liveCode), Halts: liveCode == null ? haltScan : null));
    real.SetSink(live);
    real.Start();
    var wait = Stopwatch.StartNew();
    while (wait.Elapsed < TimeSpan.FromSeconds(30) && live.Scans.Count < 5)
        await Task.Delay(100);
    foreach (var scan in live.Scans)
        Console.WriteLine($"RESULT {scan.Count} rows: {string.Join(" ", scan)}");
    foreach (var scan in live.HaltScans)
        Console.WriteLine($"RESULT halts {scan.Count} rows: {string.Join(" ", scan)}");
    Console.WriteLine($"RESULT {live.Scans.Count} lists in {wait.Elapsed.TotalSeconds:F1} s");
    Check(live.Scans.Count >= 2, "the real Gateway answers scan after scan");
    await real.StopAsync();
}

// What the day's change is measured from: the previous close, or after the close the session's own.
void RunDayChange()
{
    var eastern = TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");
    DateTime Et(int day, int h, int m) => TimeZoneInfo.ConvertTimeToUtc(new DateTime(2026, 11, day, h, m, 0, DateTimeKind.Unspecified), eastern);
    double Change(DateTime utc, double last, double prev, double close) => Math.Round((last / DayChange.Base(utc, prev, close) - 1) * 100);

    // FCUV on Monday 2026-11-16: Friday's close 7.08, Monday's 4.72, after hours about 5.86.
    Check(Change(Et(16, 16, 50), 5.86, 7.08, 4.72) == 24, $"after the close it's from today's close: FCUV +24%, not -17% ({Change(Et(16, 16, 50), 5.86, 7.08, 4.72)})");
    Check(Change(Et(16, 15, 59), 4.80, 7.08, 4.72) == -32, "in the session it's from the previous close");
    Check(Change(Et(16, 7, 0), 5.00, 7.08, double.NaN) == -29, "and premarket, with no close for the day yet");
    Check(Change(Et(17, 2, 0), 5.86, 7.08, 4.72) == 24 && Change(Et(17, 2, 0), 5.86, 4.72, double.NaN) == 24, "overnight it's the evening's close, before or after the Summary rolls to the next day");
    Check(Change(Et(17, 4, 0), 6.00, 4.72, double.NaN) == 27, "from 4:00 premarket it's from the previous close again");
    Check(Change(Et(21, 11, 0), 5.00, 4.50, 4.00) == 25 && Change(Et(22, 23, 0), 5.00, 4.50, 4.00) == 25, "all weekend it's from Friday's close");
    Check(DayChange.Base(Et(16, 17, 0), 7.08, double.NaN) == 7.08, "after the close with no close known yet, it's still the previous close");
}

// RVOL's arithmetic, then, for any symbols given, today's figure from tastytrade's daily candles (read-only).
async Task RunRvol(string[] symbols)
{
    var today = new DateTime(2026, 11, 16);
    var sessions = Enumerable.Range(1, 60).Select(i => (today.AddDays(-i), (double)i)).ToList();
    sessions.Add((today, 1_000_000));
    Check(RelativeVolumeMath.Average(sessions, today) == 25.5, "the average is of the 50 sessions before today, today's left out");
    Check(double.IsNaN(RelativeVolumeMath.Average(sessions.Take(4), today)) && RelativeVolumeMath.Average(sessions.Take(5), today) == 3, "with under 5 sessions there's none; with 5 it's their average");
    Check(RelativeVolumeMath.Ratio(51, 25.5) == 2 && double.IsNaN(RelativeVolumeMath.Ratio(10, 0)) && double.IsNaN(RelativeVolumeMath.Ratio(double.NaN, 5)), "RVOL is today over the average, and nothing without either");
    Check(RelativeVolumeMath.TodayEt(new DateTime(2026, 11, 17, 3, 0, 0, DateTimeKind.Utc)) == today && RelativeVolumeMath.TodayEt(new DateTime(2026, 11, 17, 6, 0, 0, DateTimeKind.Utc)) == today.AddDays(1), "today is the Eastern date");

    // The connection's loader of those averages, with a stand-in for the daily bars: 60 earlier sessions of
    // volume 1 to 60 and today's, or 3 for a new listing, or nothing while "down".
    var clock = new DateTime(2026, 11, 16, 15, 0, 0, DateTimeKind.Utc);
    var asked = new ConcurrentQueue<string>();
    var told = new ConcurrentQueue<string>();
    int loading = 0, mostLoading = 0;
    bool down = false;
    async Task<IReadOnlyList<BarRow>> Bars(string candle, DateTime from, DateTime to, CancellationToken ct)
    {
        int n = Interlocked.Increment(ref loading);
        mostLoading = Math.Max(mostLoading, n);
        await Task.Delay(20, ct);
        Interlocked.Decrement(ref loading);
        asked.Enqueue(candle);
        if (down)
            return [];

        int earlier = candle.StartsWith("NEWCO", StringComparison.Ordinal) ? 3 : 60;
        return Enumerable.Range(0, earlier + 1)
            .Select(i => new BarRow(new DateTimeOffset(to.Date.AddDays(-i), TimeSpan.Zero).ToUnixTimeMilliseconds(), 1, 1, 1, 1, i == 0 ? 1_000_000 : i, 1))
            .ToList();
    }

    using var averages = new VolumeAverages(Bars, told.Enqueue, () => clock);
    Check(double.IsNaN(averages.Get("AVGA")), "no average before it's loaded");
    foreach (var s in new[] { "AVGA", "AVGB", "AVGA" })
        averages.Want(s);
    await averages.IdleAsync();
    Check(asked.Order().SequenceEqual(new[] { "AVGA{=1d}", "AVGB{=1d}" }) && mostLoading == 1, $"each symbol's daily bars are loaded once, one at a time ({string.Join(" ", asked)})");
    Check(averages.Get("AVGA") == 25.5 && told.Order().SequenceEqual(new[] { "AVGA", "AVGB" }), $"the average is kept, and the connection told ({averages.Get("AVGA")})");

    asked.Clear();
    averages.Want("AVGA");
    await averages.IdleAsync();
    using (var reconnected = new VolumeAverages(Bars, null, () => clock))
    {
        reconnected.Want("AVGA");
        await reconnected.IdleAsync();
        Check(asked.IsEmpty && reconnected.Get("AVGA") == 25.5, "not loaded again the same day, nor after a reconnect");
    }

    averages.Want("NEWCO");
    await averages.IdleAsync();
    averages.Want("NEWCO");
    await averages.IdleAsync();
    Check(asked.SequenceEqual(new[] { "NEWCO{=1d}" }) && double.IsNaN(averages.Get("NEWCO")), "a new listing has no average, and isn't asked about again that day");

    asked.Clear();
    clock = clock.AddDays(1);
    Check(averages.Get("AVGA") == 25.5, "yesterday's average stands in until today's is loaded");
    averages.Want("AVGA");
    await averages.IdleAsync();
    Check(asked.SequenceEqual(new[] { "AVGA{=1d}" }), "the next day it's loaded again");

    asked.Clear();
    down = true;
    async Task<int> WantDown(TimeSpan later)
    {
        clock += later;
        averages.Want("DOWNCO");
        await averages.IdleAsync();
        return asked.Count;
    }

    Check(await WantDown(TimeSpan.Zero) == 1 && await WantDown(TimeSpan.FromSeconds(59)) == 1 && await WantDown(TimeSpan.FromSeconds(2)) == 2,
        "a load that fails is tried again after a minute");
    Check(await WantDown(TimeSpan.FromMinutes(9)) == 2 && await WantDown(TimeSpan.FromMinutes(2)) == 3 && double.IsNaN(averages.Get("DOWNCO")),
        "and after that every 10 minutes");

    if (symbols.Length == 0)
        return;

    using var client = api();
    var history = new HistoryClient(async (force, ct) => await client.GetQuoteTokenAsync(ct));
    var now = DateTime.UtcNow;
    var todayEt = RelativeVolumeMath.TodayEt(now);
    using var loader = new VolumeAverages(history.LoadAsync, null);
    foreach (var symbol in symbols)
    {
        var bars = await history.LoadAsync(symbol + "{=1d}", now.AddDays(-100), now, CancellationToken.None);
        var days = bars.Select(b => (DateTimeOffset.FromUnixTimeMilliseconds(b.TimeMs).UtcDateTime, b.Volume)).ToList();
        double avg = RelativeVolumeMath.Average(days, todayEt);
        double todayVolume = days.LastOrDefault().Item1.Date == todayEt ? days[^1].Volume : double.NaN;
        Console.WriteLine($"RESULT {symbol,-5} today {todayVolume:#,0}  50-day average {avg:#,0} over {days.Count(d => d.Item1.Date < todayEt)} sessions  RVOL {RelativeVolumeMath.Ratio(todayVolume, avg):0.00}");

        loader.Want(symbol);
        await loader.IdleAsync();
        Check(loader.Get(symbol).Equals(avg), $"{symbol}: the connection's loader gets the same average from the real bars");
    }
}

// The reverse splits from Massive, against answers made up here (its terms keep its data out of a public repo), and
// nothing is sent anywhere. With "live", one real request with the key in massive.json, into a temporary folder; any
// symbols after it, comma separated, have theirs listed.
async Task RunSplits(string mode)
{
    string dir = Path.Combine(Path.GetTempPath(), "scalpfeed-splits-" + Environment.ProcessId);
    Directory.CreateDirectory(dir);
    string path = Path.Combine(dir, ReverseSplits.FileName);
    if (mode != null)
    {
        using var real = new SplitSource(path, checkEvery: Timeout.InfiniteTimeSpan);
        await real.CheckAsync();
        var got = ReverseSplits.Read(path);
        Check(got.Stocks.Count >= SplitSource.FewestStocks, $"Massive answered with {got.Stocks.Count} stocks");
        foreach (var s in mode.Split(',').Where(s => s != "live"))
            Console.WriteLine($"{s}: {string.Join(", ", got.Of(s).Select(x => $"{x.Day:yyyy-MM-dd} {x.Ratio}"))}");
        Directory.Delete(dir, true);
        return;
    }

    long ms = 1_794_860_000_000;
    static string Row(string ticker, string day, string from, string to, string type = "reverse_split") =>
        $$$"""{"ticker":"{{{ticker}}}","execution_date":"{{{day}}}","split_from":{{{from}}},"split_to":{{{to}}},"adjustment_type":"{{{type}}}","id":"x"}""";
    static string Answer(params string[] rows) => "{\"status\":\"OK\",\"results\":[" + string.Join(",", rows) + "]}";
    ReverseSplits Parse(params string[] rows) => ReverseSplits.FromAnswer(JsonNode.Parse(Answer(rows)), ms);
    static DateTime Day(string d) => DateTime.ParseExact(d, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);

    var parsed = Parse(
        Row("AAA", "2025-03-04", "20", "1"), Row("AAA", "2026-06-01", "100.0", "1.0"), Row("AAA", "2025-03-04", "20", "1"), Row("BBB", "2026-05-05", "4.5", "1"),
        Row("CCC", "2026-05-05", "3", "2"), Row("DDD", "2026-05-05", "1", "10", "forward_split"), Row("EEE", "2026-05-05", "1", "10"), Row("FFF", "soon", "10", "1"),
        Row("GG.A", "2026-02-02", "8", "1"), Row("HHH", "2026-05-05", "\"ten\"", "1"), "null", "{}");
    Check(parsed.Of("AAA").SequenceEqual(new[] { new ReverseSplit(new DateTime(2026, 6, 1), "1:100"), new ReverseSplit(new DateTime(2025, 3, 4), "1:20") }), "a stock's reverse splits, newest first whatever order they came in, each once");
    Check(parsed.Of("bbb").Single().Ratio == "1:4.5" && parsed.Of("CCC").Single().Ratio == "2:3", "ratios that aren't whole");
    Check(parsed.Of("GG/A").Single().Ratio == "1:8", "a share class, which tastytrade writes with a slash");
    Check(parsed.Stocks.Keys.Order().SequenceEqual(new[] { "AAA", "BBB", "CCC", "GG.A" }) && parsed.Of("ZZZ").Count == 0 && parsed.Of(null).Count == 0, $"forward splits and rows it can't read are left out ({string.Join(" ", parsed.Stocks.Keys.Order())})");
    Check(Parse().Stocks.Count == 0 && ReverseSplits.FromAnswer(JsonNode.Parse("{\"results\":{}}"), ms).Stocks.Count == 0, "no results is none");

    parsed.Write(path);
    var back = ReverseSplits.Read(path);
    Check(back.FetchedMs == ms && back.Stocks.Count == 4 && back.Of("AAA").SequenceEqual(parsed.Of("AAA")) && back.Of("GG/A").Count == 1, "the file reads back the same");
    var reading = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
    var letGo = Task.Run(async () => { await Task.Delay(60); reading.Dispose(); });
    Parse(Row("NEW", "2026-06-01", "10", "1")).Write(path);
    await letGo;
    Check(ReverseSplits.Read(path).Of("NEW").Count == 1, "a chart reading it only holds up the swap until it's done");
    File.WriteAllText(path, "{\"splits\": {");
    bool damaged = ReverseSplits.Read(path) == ReverseSplits.None;
    File.WriteAllText(path, "{\"AAA\":{\"date\":\"2026-06-01\",\"reverse\":true,\"ratio\":\"1:100\",\"fetchedMs\":5}}");
    Check(damaged && ReverseSplits.Read(path) == ReverseSplits.None, "a damaged file, or one from before 2026-11-24, reads as none");
    File.Delete(path);

    // As a chart reads it. Until 2026-11-24 it never looked at the file, so no marker was ever drawn.
    long clock = 474_000_000;
    var chart = new ReverseSplitFile(path, () => clock);
    bool noFile = chart.Of("AAA").Count == 0;
    parsed.Write(path);
    bool tooSoon = chart.Of("AAA").Count == 0;
    clock += 2000;
    Check(noFile && tooSoon && chart.Of("AAA").Count == 2, "a chart finds the file once it's there, looking every 2 seconds");
    Check(new ReverseSplitFile(path).Of("AAA").Count == 2, "and at its first look when it's already there");
    await Task.Delay(20);
    Parse(Row("AAA", "2026-06-01", "100", "1")).Write(path);
    clock += 2000;
    Check(chart.Of("AAA").Count == 1, "and again when it changes");
    File.Delete(path);

    // Which daily bar, with Monday the 7th a holiday.
    DateTime[] days = [.. new[] { "2026-08-28", "2026-08-31", "2026-09-01", "2026-09-02", "2026-09-03", "2026-09-04", "2026-09-08", "2026-09-09" }.Select(Day)];
    string Bar(string d)
    {
        int i = ReverseSplits.BarFor(Day(d), days.Length, n => days[n]);
        return i < 0 ? "none" : days[i].ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    }
    Check(days.Select(d => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)).All(d => Bar(d) == d), "a split goes under its own day's bar, first and last included");
    Check(Bar("2026-09-05") == "2026-09-08" && Bar("2026-09-07") == "2026-09-08", "on a weekend or holiday, under the next bar");
    Check(Bar("2026-08-27") == "none" && Bar("2026-09-10") == "none" && ReverseSplits.BarFor(days[0], 0, n => days[n]) == -1, "under none when the bars don't reach back to it, it's still to come, or there are no bars");
    string Last(params string[] at) => ReverseSplits.Latest([.. at.Select(d => new ReverseSplit(Day(d), "1:2"))], days.Length, n => days[n]) is { } l ? $"{l.Split.Day:MM-dd} at {days[l.Bar]:MM-dd}" : "none";
    Check(Last("2026-12-24", "2026-09-02", "2026-08-31") == "09-02 at 09-02", "the last split is marked, not one announced but still to come, nor an earlier one");
    Check(Last("2026-12-24", "2026-01-05") == "none" && Last("2026-12-24") == "none" && Last() == "none", "nothing when the last is before the chart, or there's none yet");

    // The fetcher, with a stand-in for Massive.
    var now = new DateTime(2026, 11, 24, 9, 0, 0, DateTimeKind.Utc);
    string key = null;
    var asked = new List<(string Url, string Key)>();
    string Market(int stocks) => Answer([.. Enumerable.Range(0, stocks).Select(n => Row("T" + n, "2026-06-01", "10", "1"))]);
    (int, string) answer = (200, Market(300));
    async Task<(int, string)> Send(string url, string k, CancellationToken ct)
    {
        asked.Add((url, k));
        await Task.Delay(5, ct);
        return answer;
    }

    using (var source = new SplitSource(path, Send, () => key, () => now, Timeout.InfiniteTimeSpan))
    {
        await source.CheckAsync();
        Check(asked.Count == 0 && !File.Exists(path), "with no key, nothing is sent");
        key = "mv-test-key-123";
        await Task.WhenAll(source.CheckAsync(), source.CheckAsync());
        Check(asked.Count == 1 && asked[0].Key == key && !asked[0].Url.Contains(key) && asked[0].Url.StartsWith("https://api.massive.com/"), "once there is, one request, with the key apart from the address");
        Check(ReverseSplits.Read(path).Stocks.Count == 300, "and the file is written");
        now = now.AddHours(5.9);
        await source.CheckAsync();
        Check(asked.Count == 1, "kept for 6 hours");
    }

    using (var source = new SplitSource(path, Send, () => key, () => now, Timeout.InfiniteTimeSpan))
    {
        await source.CheckAsync();
        Check(asked.Count == 1, "across a reconnect or restart too");
        now = now.AddHours(0.2);
        answer = (200, Market(SplitSource.FewestStocks - 1));
        await source.CheckAsync();
        Check(asked.Count == 2 && ReverseSplits.Read(path).Stocks.Count == 300, "then fetched again, and an answer with too few stocks leaves the file alone");
        answer = (200, Market(400));
        now = now.AddMinutes(9);
        await source.CheckAsync();
        bool waited = asked.Count == 2;
        now = now.AddMinutes(2);
        await source.CheckAsync();
        Check(waited && asked.Count == 3 && ReverseSplits.Read(path).Stocks.Count == 400, "a failure waits 10 minutes before trying again");
        foreach (var bad in new[] { (401, ""), (429, ""), (500, "oops"), (200, "<html>"), (302, "") })
        {
            now = now.AddHours(7);
            answer = bad;
            await source.CheckAsync();
        }
        Check(asked.Count == 8 && ReverseSplits.Read(path).Stocks.Count == 400, "a refused key, a rate limit, an error, a redirect or a page that isn't data leaves the file alone");
    }

    Check(!File.ReadAllText(Log.FilePath).Contains("mv-test-key-123"), "the key is never in the log");
    Directory.Delete(dir, true);
}

// The float from stockanalysis.com through Firecrawl, against canned answers: nothing is sent anywhere.
async Task RunFloat()
{
    // Trimmed from Firecrawl's answers on 2026-11-16.
    static string Stats(string symbol, string flt, string shares, string volume) =>
        $$$"""{"success":true,"data":{"alexandria":[{"provider":"stockanalysis-com","capability":"securities/stock_statistics","creditsCost":5,"data":{"name":"{{{symbol}}}","sections":[ {"id":"shares","items":[{"hover":"{{{shares}}}","id":"sharesout","title":"Shares Outstanding","value":"x"},{"hover":"{{{flt}}}","id":"float","title":"Float","value":"x"}]}, {"id":"stockPrice","items":[{"hover":"{{{volume}}}","id":"averageVolume","title":"Average Volume (20 Days)","value":"x"}]}],"symbol":"{{{symbol}}}"}}]}}""";
    const string NotFound = """{"success":true,"data":{"alexandria":[{"provider":"stockanalysis-com","capability":"securities/stock_statistics","error":{"code":"not_found","message":"The workflow could not find that resource.","status":404}}]}}""";

    long ms = 1_794_860_000_000;
    var gdhg = FloatFacts.FromStatistics(JsonNode.Parse(Stats("GDHG", "29,519,326", "47,127,140", "5,256,126"))["data"]["alexandria"][0]["data"], "2026-11-16", ms);
    var fgl = FloatFacts.FromStatistics(JsonNode.Parse(Stats("FGL", "8,557", "11,456", "1,565,709"))["data"]["alexandria"][0]["data"], "2026-11-16", ms);
    var fcuv = FloatFacts.FromStatistics(JsonNode.Parse(Stats("FCUV", "546,711", "648,251", "5,977,944"))["data"]["alexandria"][0]["data"], "2026-11-16", ms);
    Check(gdhg.Float == 29_519_326 && gdhg.SharesOut == 47_127_140 && gdhg.AverageVolume == 5_256_126, "the float, share count and 20-day volume come from the exact values");
    Check(gdhg.Describe() == "29.5M" && fcuv.Describe() == "547K" && FloatFacts.Shares(950) == "950" && FloatFacts.Shares(999_999) == "1.0M" && FloatFacts.Shares(999_499) == "999K" && FloatFacts.Shares(999.7) == "1K", $"shown as 29.5M, under a million as 547K, and under a thousand in full ({gdhg.Describe()}, {fcuv.Describe()})");
    Check(fgl.Doubtful && !gdhg.Doubtful && !fcuv.Doubtful, "FGL, trading 183 times its float a day, is doubtful; GDHG and FCUV (11 times) aren't");
    Check(FloatFacts.Short(gdhg) == "29.5M" && FloatFacts.Short(fgl) == "9K ?" && FloatFacts.Short(null) == ""
        && FloatFacts.Short(new FloatFacts("2026-11-19", double.NaN, 46_143, 651_637, 0)) == "n/a",
        "the Description column's float: short, a ? when out of date, n/a when the site has none, blank before it's fetched");
    Check(fgl.Describe() == "9K ? (trades 183x that a day, so likely out of date)", $"and says why ({fgl.Describe()})");
    Check(FloatFacts.DayOf(new DateTime(2026, 11, 16, 3, 0, 0, DateTimeKind.Utc)) == "2026-11-15" && FloatFacts.DayOf(new DateTime(2026, 11, 16, 9, 0, 0, DateTimeKind.Utc)) == "2026-11-16", "a day is an Eastern day");
    Check(FloatFacts.SiteSymbol("BRK/B") == "BRK.B", "share classes are asked for with a dot");

    string dir = Path.Combine(Path.GetTempPath(), "scalpfeed-float-" + Environment.ProcessId);
    Directory.CreateDirectory(dir);
    string cachePath = Path.Combine(dir, "float-cache.json");
    FloatFacts.WriteCache(cachePath, new Dictionary<string, FloatFacts> { ["GDHG"] = gdhg, ["ZZZZ"] = new("2026-11-16", double.NaN, double.NaN, double.NaN, ms) });
    var back = FloatFacts.ReadCache(cachePath);
    Check(back["gdhg"] == gdhg && !back["ZZZZ"].Known && back["ZZZZ"].Day == "2026-11-16", "the cache file reads back the same, a missing symbol included, whatever the case");
    File.Delete(cachePath);

    // The fetcher, with a stand-in for Firecrawl.
    var now = new DateTime(2026, 11, 16, 15, 0, 0, DateTimeKind.Utc);
    string key = null;
    var sent = new ConcurrentQueue<string>();
    var answers = new ConcurrentDictionary<string, (int, string)>();
    int inFlight = 0, mostInFlight = 0;
    var arrived = new ConcurrentQueue<string>();
    async Task<(int, string)> Send(string k, string json, CancellationToken ct)
    {
        int n = Interlocked.Increment(ref inFlight);
        mostInFlight = Math.Max(mostInFlight, n);
        await Task.Delay(30, ct);
        Interlocked.Decrement(ref inFlight);
        string symbol = (string)JsonNode.Parse(json)["alexandria"]["options"]["symbol"];
        sent.Enqueue(symbol);
        return answers.GetValueOrDefault(symbol, (200, Stats(symbol, "1,000,000", "2,000,000", "500,000")));
    }

    using var floats = new FloatSource(arrived.Enqueue, cachePath, Send, () => key, () => now, Timeout.InfiniteTimeSpan);
    floats.Want("GDHG");
    await floats.IdleAsync();
    floats.RetryHeld();
    await floats.IdleAsync();
    Check(sent.IsEmpty && floats.Get("GDHG") == null, "with no key, nothing is sent");

    key = "fc-test";
    answers["GDHG"] = (200, Stats("GDHG", "29,519,326", "47,127,140", "5,256,126"));
    answers["ZQZQX"] = (200, NotFound);
    floats.RetryHeld();
    await floats.IdleAsync();
    Check(sent.SequenceEqual(new[] { "GDHG" }), "a symbol quoted before the key was in is fetched once it is, without being quoted again (SEAT, 2026-11-19)");
    foreach (var s in new[] { "GDHG", "GMM", "ZQZQX", "BRK/B", "GDHG" })
        floats.Want(s);
    await floats.IdleAsync();
    Check(sent.Order().SequenceEqual(new[] { "BRK.B", "GDHG", "GMM", "ZQZQX" }), $"each symbol asked once ({string.Join(" ", sent)})");
    Check(mostInFlight == 1, "one request at a time");
    Check(floats.Get("GDHG")?.Float == 29_519_326 && arrived.Contains("GDHG"), "the float is kept, and the connection told");
    Check(floats.Get("ZQZQX") is { Known: false } && FloatFacts.ReadCache(cachePath).Count == 4, "a symbol the site doesn't have is kept as unknown, and all four are in the file");

    sent.Clear();
    floats.Want("GDHG");
    floats.Want("ZQZQX");
    await floats.IdleAsync();
    Check(sent.IsEmpty, "not asked again the same day, found or not");
    using (var reopened = new FloatSource(null, cachePath, Send, () => key, () => now))
    {
        reopened.Want("GDHG");
        await reopened.IdleAsync();
        Check(sent.IsEmpty && reopened.Get("GDHG")?.Float == 29_519_326, "nor after a restart: the file is read back");
    }

    // momo-algo shares the file: what it writes shows up here, and a fetch here keeps its entries.
    var shared = FloatFacts.ReadCache(cachePath);
    long botMs = new DateTimeOffset(now).ToUnixTimeMilliseconds() + 1;
    shared["BOTX"] = new FloatFacts(FloatFacts.DayOf(now), 3_300_000, 4_000_000, double.NaN, botMs);
    FloatFacts.WriteCache(cachePath, shared);
    await Task.Delay(2100);
    floats.Want("BOTX");
    await floats.IdleAsync();
    Check(sent.IsEmpty && floats.Get("BOTX")?.Float == 3_300_000, "a float the bot wrote to the file is used, not fetched again");
    string freshPath = Path.Combine(dir, "fresh.json");
    using (var fresh = new FloatSource(null, freshPath, Send, () => key, () => now, Timeout.InfiniteTimeSpan))
    {
        FloatFacts.WriteCache(freshPath, new Dictionary<string, FloatFacts> { ["BOTZ"] = new(FloatFacts.DayOf(now), 5_500_000, 6_000_000, double.NaN, botMs) });
        fresh.Want("BOTZ");
        await fresh.IdleAsync();
        Check(sent.IsEmpty && fresh.Get("BOTZ")?.Float == 5_500_000, "even one written before anything is fetched here");
    }
    sent.Clear();
    shared = FloatFacts.ReadCache(cachePath);
    shared["BOTY"] = new FloatFacts(FloatFacts.DayOf(now), 7_700_000, 9_000_000, double.NaN, botMs);
    FloatFacts.WriteCache(cachePath, shared);
    floats.Want("NEWZ");
    await floats.IdleAsync();
    var afterFetch = FloatFacts.ReadCache(cachePath);
    Check(sent.SequenceEqual(new[] { "NEWZ" }) && afterFetch.ContainsKey("BOTY") && afterFetch.ContainsKey("NEWZ"),
        "a fetch here merges with the file, keeping what the bot wrote a moment ago");
    sent.Clear();

    now = now.AddDays(1);
    floats.Want("GDHG");
    floats.Want("ZQZQX");
    await floats.IdleAsync();
    Check(sent.SequenceEqual(new[] { "ZQZQX" }), $"the next day a float found is still kept, and a symbol not found is asked again, which costs nothing ({string.Join(" ", sent)})");

    sent.Clear();
    now = now.AddDays(5.9);
    floats.Want("GDHG");
    await floats.IdleAsync();
    Check(sent.IsEmpty, "a float is kept for a week");
    now = now.AddDays(0.2);
    floats.Want("GDHG");
    await floats.IdleAsync();
    Check(sent.SequenceEqual(new[] { "GDHG" }), "and fetched again after it");

    sent.Clear();
    answers["FAIL"] = (500, "");
    floats.Want("FAIL");
    await floats.IdleAsync();
    floats.Want("FAIL");
    await floats.IdleAsync();
    now = now.AddMinutes(11);
    floats.Want("FAIL");
    await floats.IdleAsync();
    Check(sent.Count(s => s == "FAIL") == 2, "a failure waits 10 minutes before asking again");

    sent.Clear();
    answers["BROKE"] = (402, "");
    floats.Want("BROKE");
    await floats.IdleAsync();
    floats.Want("OTHER");
    await floats.IdleAsync();
    now = now.AddMinutes(61);
    floats.Want("OTHER");
    await floats.IdleAsync();
    Check(sent.SequenceEqual(new[] { "BROKE", "OTHER" }), $"out of credits stops all fetches for an hour ({string.Join(" ", sent)})");

    sent.Clear();
    answers.TryRemove("BROKE", out _);
    answers["SLOW"] = (429, "");
    floats.Want("SLOW");
    floats.Want("NEXT");
    await floats.IdleAsync();
    answers.TryRemove("SLOW", out _);
    floats.RetryHeld();
    await floats.IdleAsync();
    Check(sent.SequenceEqual(new[] { "SLOW" }), $"rate limited: nothing more is sent for a minute ({string.Join(" ", sent)})");
    now = now.AddMinutes(2);
    floats.RetryHeld();
    await floats.IdleAsync();
    Check(floats.Get("SLOW") is { Known: true } && floats.Get("NEXT") is { Known: true } && floats.Get("BROKE") is { Known: true },
        $"then what was rate limited or queued behind it is fetched without being quoted again ({string.Join(" ", sent)})");
    var lghl = new FloatFacts("2026-11-19", double.NaN, 46_143, 651_637, ms);
    Check(lghl.Describe() == "no float on stockanalysis.com" && new FloatFacts("2026-11-19", double.NaN, double.NaN, double.NaN, ms).Describe() == "not on stockanalysis.com",
        "a stock the site has without a float (LGHL) says so, apart from one the site hasn't got");

    Directory.Delete(dir, true);
}

// IBKR's ineligibility reasons in contract details (IbkrRestrictions): parsing Gateway's replies as captured at version
// 186 on 2026-11-23, then, given a port, real lookups on Gateway under client 96 (contract details only, read-only).
async Task RunRestrictions(int port)
{
    string[] Reply(string symbol, string[] secIds, params string[] reasons)
    {
        var f = new List<string> { "10", "9001", symbol, "STK", "", "", "0", "", "SMART", "USD", symbol, "SCM", "SCM", "932900175", "0.0001", "",
            "LMT,MKT", "SMART,NASDAQ", "1", "0", "LONG NAME", "NASDAQ", "", "Financial", "Insurance", "Insurance Brokers", "US/Eastern",
            "20261123:0400-20261123:2000", "20261123:0930-20261123:1600", "", "" };
        f.Add((secIds.Length / 2).ToString(CultureInfo.InvariantCulture));
        f.AddRange(secIds);
        f.AddRange(["1", "", "", "557,557", "", "COMMON", "0.0001", "0.0001", "100"]);
        f.Add((reasons.Length / 2).ToString(CultureInfo.InvariantCulture));
        f.AddRange(reasons);
        return f.ToArray();
    }

    const string compliance = "No Opening Trades: Small Cap, Subject to Compliance Restriction";
    string[] isin = ["ISIN", "KYG989MC1147"];
    var zbao = Reply("ZBAO", isin, "i189", compliance);
    Check(zbao.Length == 46 && IbkrRestrictions.ParseReasons(zbao) == compliance, "ZBAO's reply, 46 fields as Gateway sent it: its one reason");
    Check(IbkrRestrictions.ParseReasons(Reply("NVDA", isin)) == "", "NVDA's: no reasons");
    Check(IbkrRestrictions.ParseReasons(Reply("XYZ", [])) == "" && IbkrRestrictions.ParseReasons(Reply("XYZ", ["ISIN", "US1", "CUSIP", "1"], "i189", compliance)) == compliance,
        "found past no security IDs, or two");
    Check(IbkrRestrictions.ParseReasons(Reply("XYZ", isin, "i128", "Closing only", "i189", compliance)) == "Closing only; " + compliance, "two reasons are joined");
    Check(IbkrRestrictions.ParseReasons(zbao[..^1]) == null && IbkrRestrictions.ParseReasons(zbao.Append("extra").ToArray()) == null
          && IbkrRestrictions.ParseReasons(zbao.Select((v, i) => i == 3 ? "FUND" : v).ToArray()) == null && IbkrRestrictions.ParseReasons(["4", "2"]) == null,
        "a reply in any other layout isn't read: one field short or long, a fund, or not contract details");

    var config = IbkrConfig.Load();
    Check(config with { ClientId = 78, TradingClientIdSetting = 0 } is { TradingClientId: 79, RestrictionsClientId: 80 }
          && (config with { ClientId = 78, TradingClientIdSetting = 80 }).RestrictionsClientId == 81,
        "its own client ID: data plus 2, or plus 3 when trading has that");
    Check(IbkrRestrictions.AllowedRequests.SetEquals([71, 9]), "it may send only startApi and contract details");

    if (port == 0)
        return;

    var lookups = new IbkrRestrictions(() => IbkrConfig.Load() with { Port = port }, clientId: 96);
    var warnings = new ConcurrentQueue<(string, string)>();
    lookups.Prefetch(["NVDA", "AHMA", "ZZQQX", "BRK/B"]);
    lookups.Charted("ZBAO", (s, r) => warnings.Enqueue((s, r)));
    var watch = Stopwatch.StartNew();
    while (!(lookups.Known("ZBAO") && lookups.Known("BRK B")) && watch.Elapsed < TimeSpan.FromSeconds(20))
        await Task.Delay(50);
    Check(lookups.Reason("zbao") == compliance && lookups.Reason("AHMA") == compliance, $"Gateway: ZBAO and AHMA are restricted ({lookups.Reason("ZBAO")})");
    Check(lookups.Known("NVDA") && lookups.Reason("NVDA") == null && lookups.Known("BRK/B") && lookups.Reason("BRK.B".Replace('.', '/')) == null,
        "NVDA and BRK/B (asked as BRK B) aren't");
    Check(lookups.Known("ZZQQX") && lookups.Reason("ZZQQX") == null, "a symbol IBKR doesn't know counts as unrestricted");
    Check(warnings.Count == 1 && warnings.TryPeek(out var first) && first == ("ZBAO", compliance), "the charted ZBAO warned once, with IBKR's reason");
    lookups.Charted("ZBAO", (s, r) => warnings.Enqueue((s, r)));
    lookups.Charted("NVDA", (s, r) => warnings.Enqueue((s, r)));
    Check(warnings.Count == 1, "charting it again the same day, or charting NVDA, doesn't");
    Console.WriteLine($"Looked up 5 stocks in {watch.ElapsedMilliseconds} ms");
}

// Which symbol search hits Quantower's search box gets: no warrants unless typed exactly.
void RunSearchFilter()
{
    const string common = "Brand Engagement Network Inc. - Common Stock", warrant = "Brand Engagement Network Inc. - Warrant";
    Check(SearchFilter.Shows("BNAI", common, "BNAI") && !SearchFilter.Shows("BNAIW", warrant, "BNAI"), "typing BNAI finds the stock, not its warrant BNAIW");
    Check(SearchFilter.Shows("BNAIW", warrant, "bnaiw ") && !SearchFilter.Shows("BNAIW", warrant, "BNAIW1"), "typing the warrant's exact symbol still finds it, in any case");
    Check(!SearchFilter.Shows("XYZWS", "XYZ Corp - Warrants, each exercisable for one share", "XYZ"), "Warrants, plural, are left out too");
    Check(SearchFilter.Shows("SNOW", "Snowflake Inc. - Class A Common Stock", "SN") && SearchFilter.Shows("BW", "Babcock & Wilcox Enterprises, Inc. Common Stock", "B"),
        "a stock whose symbol ends in W is kept: it's the description that counts");
    Check(SearchFilter.Shows("ABCR", "ABC Inc. - Rights", "ABC") && SearchFilter.Shows("ABCU", "ABC Inc. - Units", "ABC") && SearchFilter.Shows("ABC", null, "ABC"),
        "rights, units and a missing description are kept");
    Check(!SearchFilter.Shows("ACCO", "Acco Brands Corporation Common Stock", "brand") && !SearchFilter.Shows("BNAI", common, "brand"),
        "a company name doesn't match: \"brand\" finds neither ACCO (Acco Brands) nor BNAI (Brand Engagement)");
    Check(SearchFilter.Shows("BNAI", common, "bna") && SearchFilter.Shows("BNAV", "Amplify Fairlead Tactical Bitcoin ETF", "BNA") && !SearchFilter.Shows("ABNA", "x", "BNA"),
        "tickers that start with what's typed match, in any case; one with it in the middle doesn't");
    Check(SearchFilter.Shows("BRK/B", "Berkshire Hathaway Inc. Class B", "BRK.B") && !SearchFilter.Shows("BNAI", common, "  "), "BRK.B finds BRK/B; blank finds nothing");
}

// The stocks opened in a chart, added to the end of IBKR Top Gainers: the current one always, earlier ones over the volume.
void RunCharted()
{
    var charted = new ChartedSymbols();
    const string day = "2026-11-19";
    Check(charted.Add("POAS", day) && !charted.Add("poas", day), "a chart's first bars add its stock; the next timeframes of the same chart change nothing");
    charted.Add("BABA", day);
    charted.Add("SEAT", day);
    Check(charted.Today(day).SequenceEqual(new[] { "SEAT", "BABA", "POAS" }), "most recent first");
    Check(charted.Add("POAS", day) && charted.Today(day).SequenceEqual(new[] { "POAS", "SEAT", "BABA" }), "going back to one moves it up, not in twice");

    var merged = ChartedSymbols.Merge(new[] { "SEAT", "VIVS", "CHSN" }, charted.Today(day));
    Check(merged.SequenceEqual(new[] { "SEAT", "VIVS", "CHSN", "POAS", "BABA" }), $"with no volume rule, the scan's rows come first, then each charted stock it hasn't got ({string.Join(" ", merged)})");

    // POAS on the chart now under the volume, BABA earlier over it, WETO earlier under it.
    var volume = new Dictionary<string, double> { ["POAS"] = 12_000, ["BABA"] = 3_000_000, ["WETO"] = 40_000 };
    Func<string, bool> stays = s => volume.TryGetValue(s, out var v) && v >= 50_000;
    charted.Add("WETO", day);
    charted.Add("POAS", day);
    var byVolume = ChartedSymbols.Merge(new[] { "VIVS" }, charted.Today(day), stays);
    Check(byVolume.SequenceEqual(new[] { "VIVS", "POAS", "BABA" }),
        $"the stock on the chart shows even under the volume; earlier ones stay only over it, so WETO at 40,000 goes ({string.Join(" ", byVolume)})");
    volume["WETO"] = 60_000;
    Check(ChartedSymbols.Merge(new[] { "VIVS" }, charted.Today(day), stays).SequenceEqual(new[] { "VIVS", "POAS", "WETO", "BABA" }), "once it trades past the volume it's back");
    Check(ChartedSymbols.Merge(new[] { "VIVS" }, charted.Today(day), s => false).SequenceEqual(new[] { "VIVS", "POAS" }), "with none over the volume, only the stock on the chart");
    Check(ChartedSymbols.Merge(new[] { "POAS" }, charted.Today(day), s => false).SequenceEqual(new[] { "POAS" }), "a charted stock the scan has isn't in twice");

    for (int i = 0; i < 15; i++)
        charted.Add("S" + i, day);
    Check(charted.Today(day).Count == ChartedSymbols.Most && charted.Today(day)[0] == "S14", $"at most {ChartedSymbols.Most}, the oldest dropped");
    Check(charted.Today("2026-11-20").Count == 0, "a new day starts empty");
    Check(!charted.Add(" ", "2026-11-20") && !charted.Add(null, "2026-11-20"), "a blank symbol is ignored");
}

// Each stock's places climbed in the gainers scan over the last 5 minutes.
void RunRanks()
{
    var ranks = new ScanRanks();
    long Sec(double s) => new DateTime(2026, 11, 19, 15, 0, 0, DateTimeKind.Utc).Ticks + (long)(s * TimeSpan.TicksPerSecond);
    ranks.Record(new[] { "SEAT", "VIVS", "CHSN", "INHD" }, Sec(0));
    Check(double.IsNaN(ranks.Change("SEAT", Sec(0))), "one scan: nothing to compare yet");
    ranks.Record(new[] { "CHSN", "SEAT", "VIVS", "INHD" }, Sec(2));
    Check(ranks.Change("CHSN", Sec(2)) == 2 && ranks.Change("SEAT", Sec(2)) == -1 && ranks.Change("INHD", Sec(2)) == 0,
        "in the first 5 minutes, against the first scan: CHSN up 2, SEAT down 1, INHD where it was (0)");
    for (int t = 4; t <= 300; t += 2)
        ranks.Record(new[] { "CHSN", "SEAT", "VIVS", "INHD" }, Sec(t));
    ranks.Record(new[] { "NEWZ", "SEAT", "CHSN", "VIVS" }, Sec(302));
    Check(ranks.Change("SEAT", Sec(302)) == 0 && ranks.Change("CHSN", Sec(302)) == -2 && ranks.Change("VIVS", Sec(302)) == -1,
        $"against the scan 5 minutes before (the order at 2 s): SEAT back where it was, CHSN down 2, VIVS down 1 ({ranks.Change("SEAT", Sec(302))}, {ranks.Change("CHSN", Sec(302))}, {ranks.Change("VIVS", Sec(302))})");
    Check(ranks.Change("NEWZ", Sec(302)) == 4, $"new to the list at 1st of 4 counts from 5th: up 4 ({ranks.Change("NEWZ", Sec(302))})");
    Check(double.IsNaN(ranks.Change("INHD", Sec(302))) && double.IsNaN(ranks.Change("POAS", Sec(302))), "off the list now: blank");
    for (int t = 304; t <= 610; t += 2)
        ranks.Record(new[] { "NEWZ", "SEAT", "CHSN", "VIVS" }, Sec(t));
    Check(ranks.Change("newz", Sec(610)) == 0 && ranks.Change("CHSN", Sec(610)) == 0, "5 minutes of the same order: 0, whatever the case");
}

// Depth pacing against a stand-in for Gateway, so it takes no real slots and can't disturb Quantower.
async Task RunDepthPacing()
{
    using var gateway = new FakeGateway();
    var sink = new IbkrTestSink();
    var client = new IbkrClient(new IbkrConfig(true, "127.0.0.1", gateway.Port, 1, false));
    client.SetSink(sink);
    client.Start();
    var timer = Stopwatch.StartNew();
    while (client.State != IbkrState.Live && timer.Elapsed < TimeSpan.FromSeconds(10))
        await Task.Delay(20);
    Check(client.State == IbkrState.Live, "the client goes live against the stand-in");

    // One symbol of each kind of answer. Each request must wait for the one before it.
    gateway.Policy["ROW"] = (FakeGateway.Answer.Row, 150);
    gateway.Policy["REFUSED"] = (FakeGateway.Answer.Refused, 60);
    gateway.Policy["EMPTY"] = (FakeGateway.Answer.Silent, 0);
    gateway.Policy["LAST"] = (FakeGateway.Answer.Row, 20);
    foreach (var symbol in new[] { "ROW", "REFUSED", "EMPTY", "LAST" })
        client.SetDepth(symbol, true);
    await Task.Delay(1800);

    var sent = gateway.DepthRequests.ToList();
    Console.WriteLine($"  sent in order: {string.Join(" ", sent.Select(r => $"{r.Symbol}@{r.Ms}"))}");
    // Housekeeping may already be retrying the refused one, so only the first four are fixed.
    Check(sent.Take(4).Select(r => r.Symbol).SequenceEqual(new[] { "ROW", "REFUSED", "EMPTY", "LAST" }), "one request of each, in the order asked");
    long Ms(string symbol) => sent.First(r => r.Symbol == symbol).Ms;
    Check(Ms("REFUSED") - Ms("ROW") >= 140, $"the next waits for a first row ({Ms("REFUSED") - Ms("ROW")} ms, row took 150)");
    Check(Ms("EMPTY") - Ms("REFUSED") is >= 50 and < 500, $"a refusal lets the next go ({Ms("EMPTY") - Ms("REFUSED")} ms, refusal took 60)");
    Check(Ms("LAST") - Ms("EMPTY") is >= 950 and < 1300, $"silence lets the next go after the limit ({Ms("LAST") - Ms("EMPTY")} ms, limit 1000)");
    Check(gateway.MostUnanswered <= 1, $"never more than one unanswered, apart from a silent book past its limit ({gateway.MostUnanswered})");
    Check(client.WaitingForSlot(IbkrClient.DepthFeed).SequenceEqual(new[] { "REFUSED" }), "the refused symbol is marked as waiting");

    // A DOM switching symbol: cancel and request in one tick must not wait for anything.
    gateway.Policy["SWITCHED"] = (FakeGateway.Answer.Row, 20);
    client.SetDepth("ROW", false);
    client.SetDepth("SWITCHED", true);
    await Task.Delay(300);
    var cancel = gateway.DepthCancels.Last();
    var switched = gateway.DepthRequests.First(r => r.Symbol == "SWITCHED");
    Check(switched.Ms >= cancel.Ms && switched.Ms - cancel.Ms < 100, $"a same-tick switch sends cancel, then request ({switched.Ms - cancel.Ms} ms apart)");
    Check(cancel.IsSmart == "1", "the cancel carries the smart flag");

    // The refused symbol gets in once a retry finds room.
    gateway.Policy["REFUSED"] = (FakeGateway.Answer.Row, 20);
    client.SetDepth("LAST", false);
    var retry = Stopwatch.StartNew();
    while (retry.Elapsed < TimeSpan.FromSeconds(5) && sink.DepthCount.GetValueOrDefault("REFUSED") == 0)
        await Task.Delay(20);
    Check(sink.DepthCount.GetValueOrDefault("REFUSED") > 0, $"a refused symbol gets a book on retry after a cancel ({retry.Elapsed.TotalSeconds:F1} s)");
    Check(gateway.DepthRequests.Select(r => r.Id).Distinct().Count() == gateway.DepthRequests.Count, "a retry asks under a new id");

    // Dropped while still queued: Gateway never hears about it. Dropped while being waited on: it's
    // cancelled, and the queue moves on straight away instead of sitting out the limit.
    gateway.Policy["HOLDING"] = (FakeGateway.Answer.Silent, 0);
    gateway.Policy["QUEUED"] = (FakeGateway.Answer.Row, 20);
    gateway.Policy["AFTER"] = (FakeGateway.Answer.Row, 20);
    client.SetDepth("HOLDING", true);
    await Task.Delay(60);
    client.SetDepth("QUEUED", true);
    await Task.Delay(60);
    client.SetDepth("AFTER", true);
    await Task.Delay(60);
    client.SetDepth("QUEUED", false);
    await Task.Delay(60);
    client.SetDepth("HOLDING", false);
    await Task.Delay(200);
    Check(!gateway.DepthRequests.Any(r => r.Symbol == "QUEUED"), "a symbol dropped while queued is never requested");
    var heldId = gateway.DepthRequests.Last(r => r.Symbol == "HOLDING").Id;
    Check(gateway.DepthCancels.Count(c => c.Id == heldId) == 1, "the one being waited on is cancelled");
    var after = gateway.DepthRequests.LastOrDefault(r => r.Symbol == "AFTER");
    Check(after != default && after.Ms >= gateway.DepthCancels.Last(c => c.Id == heldId).Ms, "its cancel lets the next go at once");

    // A reconnect starts the queue over, and each wanted symbol goes out once, one at a time.
    gateway.ClearLog();
    client.SetDepth("EMPTY", false);
    await Task.Delay(100);
    gateway.ClearLog();
    client.ForceReconnect("harness");
    var back = Stopwatch.StartNew();
    while (back.Elapsed < TimeSpan.FromSeconds(10) && gateway.DepthRequests.Count < 3)
        await Task.Delay(20);
    await Task.Delay(300);
    var again = gateway.DepthRequests.ToList();
    Console.WriteLine($"  after reconnect: {string.Join(" ", again.Select(r => $"{r.Symbol}@{r.Ms}"))}");
    Check(again.Select(r => r.Symbol).Distinct().Count() == again.Count && again.Count == 3, $"each wanted symbol is requested once after a reconnect ({again.Count})");
    Check(gateway.MostUnanswered <= 1, "still one at a time after a reconnect");

    await client.StopAsync();
}

// ARCA, NYSE and BX rows in the depth, from each venue's own quote. First the merge on its own, then
// the client against the stand-in for Gateway.
async Task RunVenueTops()
{
    string Venues(List<IbkrDepthRow> rows) => string.Join(" ", rows.Select(r => r.Venue));
    var merged = new List<IbkrDepthRow>();
    var bids = new List<IbkrDepthRow> { new(5.00, 300, "NASDAQ"), new(4.99, 200, "BATS") };
    VenueTops.Merge(bids, [new(5.01, 400, "ARCA"), new(5.00, 200, "NYSE"), new(4.90, 100, "BEX")], true, merged);
    Check(Venues(merged) == "ARCA NASDAQ NYSE BATS BEX", $"bids: each venue in price order, after rows already at its price ({Venues(merged)})");
    Check(bids.Count == 2, "the book itself is left alone");

    var asks = new List<IbkrDepthRow> { new(5.03, 300, "NASDAQ"), new(5.05, 200, "BATS") };
    VenueTops.Merge(asks, [new(5.02, 400, "ARCA"), new(5.05, 100, "NYSE"), new(-1, 0, "BEX")], false, merged);
    Check(Venues(merged) == "ARCA NASDAQ BATS NYSE", $"asks: lowest first, and a venue with no market adds nothing ({Venues(merged)})");

    VenueTops.Merge(bids, [new(0, 100, "ARCA"), new(double.NaN, 100, "NYSE"), new(5.02, 0, "BEX")], true, merged);
    Check(Venues(merged) == "NASDAQ BATS", "a zero price, no price, or no size adds nothing");

    var withArca = new List<IbkrDepthRow> { new(5.00, 300, "ARCA") };
    VenueTops.Merge(withArca, [new(5.01, 400, "ARCA")], true, merged);
    Check(merged.Count == 1 && merged[0].Price == 5.00, "a venue the book already carries isn't added twice");

    using var gateway = new FakeGateway();
    var sink = new IbkrTestSink();
    var client = new IbkrClient(new IbkrConfig(true, "127.0.0.1", gateway.Port, 1, false));
    client.SetSink(sink);
    client.Start();
    var timer = Stopwatch.StartNew();
    while (client.State != IbkrState.Live && timer.Elapsed < TimeSpan.FromSeconds(10))
        await Task.Delay(20);
    Check(client.State == IbkrState.Live, "the client goes live against the stand-in");

    async Task<bool> Until(Func<bool> done)
    {
        var wait = Stopwatch.StartNew();
        while (!done() && wait.Elapsed < TimeSpan.FromSeconds(3))
            await Task.Delay(10);
        return done();
    }

    // The stand-in's book: NASDAQ bid 5.00 x 100, NASDAQ ask 5.05 x 300.
    gateway.VenueQuotes["VEN@ARCA"] = (5.01, 400, 5.04, 200, 0);
    gateway.VenueQuotes["VEN@NYSE"] = (0, 0, 0, 0, 354);
    client.SetDepth("VEN", true);
    Check(await Until(() => sink.Depth.TryGetValue("VEN", out var d) && d.BidTop == 5.01),
        "ARCA's bid above the book's best becomes the top row");
    var view = sink.Depth["VEN"];
    Console.WriteLine($"  book: {view.Bids} bids from {view.BidTop}, {view.Asks} asks from {view.AskTop}, venues {string.Join(" ", view.Venues.Order())}");
    Check(view.AskTop == 5.04 && view.AskTopSize == 200, "and its ask below the book's best does too, with its size");
    Check(view.Venues.SetEquals(new[] { "NASDAQ", "ARCA" }), "NYSE, refused, and BX, with no market, add no rows");
    var asked = gateway.MarketDataRequests.Where(r => r.Symbol == "VEN").ToList();
    Check(asked.Select(r => r.Exchange).Order().SequenceEqual(new[] { "ARCA", "BEX", "NYSE" }), $"one quote per venue, no SMART quote ({string.Join(" ", asked.Select(r => r.Exchange))})");
    Check(sink.Problems.Any(p => p.StartsWith("VEN Venue:NYSE 354")), "the refusal is reported as the venue's");
    Check(!sink.Quotes.ContainsKey("VEN"), "venue quotes never reach the symbol's own quote");

    int deliveries = sink.DepthCount["VEN"];
    gateway.SendVenueQuote("VEN", "ARCA", 4.98, 100, 5.04, 200);
    Check(await Until(() => sink.Depth["VEN"].BidTop == 5.00 && sink.DepthCount["VEN"] > deliveries),
        "a venue's own change sends the book again, with no depth row changing");
    Check(sink.Depth["VEN"].Bids == 2, "ARCA's lower bid now sits under NASDAQ's");

    gateway.SendVenueQuote("VEN", "ARCA", -1, 0, -1, 0);
    Check(await Until(() => sink.Depth["VEN"].Venues.SetEquals(new[] { "NASDAQ" })), "a venue that stops quoting leaves the book");

    // A symbol that gets no depth slot shows nothing, rather than a book of only these rows.
    gateway.Policy["NOSLOT"] = (FakeGateway.Answer.Refused, 20);
    gateway.VenueQuotes["NOSLOT@ARCA"] = (5.01, 400, 5.04, 200, 0);
    client.SetDepth("NOSLOT", true);
    await Task.Delay(600);
    Check(sink.DepthCount.GetValueOrDefault("NOSLOT") == 0, "venue quotes alone never make a book");

    // Turning depth off cancels the venue quotes with it.
    var venueIds = asked.Select(r => r.Id).ToHashSet();
    client.SetDepth("VEN", false);
    await Until(() => gateway.MarketDataCancels.Count(venueIds.Contains) >= 2);
    await Task.Delay(100);
    Check(gateway.MarketDataCancels.Count(venueIds.Contains) == 2, $"the venue quotes are cancelled with the depth, all but the refused one ({gateway.MarketDataCancels.Count(venueIds.Contains)})");
    Check(client.State == IbkrState.Live, "the canary is untouched");
    Check(IbkrClient.AllowedRequests.SetEquals(new[] { 71, 59, 1, 2, 10, 11, 97, 98, 20, 25, 22, 23 }), "the whitelist is unchanged: market data, history and the scanner, no orders");

    await client.StopAsync();
}

// The indicator's session VWAP: made-up bars around each restart, then, given IBKR's 1-minute bars saved by
// analysis\ibkr_history.py, NXXT's values on 2026-11-11 to compare with the ones worked out separately in
// Python with (H+L+C)/3: from 4:00 ET only, 1.0399, 1.4008 and 1.4833 at 11:00, 13:40 and 16:00 ET; by
// session (4:00, 9:30, 16:00 ET), 0.1653, 1.5157 and 4.7286 at 9:30, 16:00 and 18:00 ET. The after-hours
// one is what the user's reference showed: about 4.65 at about 18:15 ET.
void RunSessionVwap(string barsFile)
{
    var eastern = TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");
    DateTime Et(int day, int h, int m) => TimeZoneInfo.ConvertTimeToUtc(new DateTime(2026, 11, day, h, m, 0), eastern);
    var premarket = new TimeSpan(4, 0, 0);
    var open = new TimeSpan(9, 30, 0);
    var afterHours = new TimeSpan(16, 0, 0);

    var vwap = new SessionVwap(premarket);
    double v1 = vwap.Update(Et(10, 19, 0), default, 0.20, 0.18, 0.19, 1_000_000);
    double v2 = vwap.Update(Et(11, 3, 59), Et(10, 19, 0), 0.25, 0.23, 0.24, 1_000_000);
    Check(Math.Abs(v2 - (0.19 + 0.24) / 2) < 1e-9, $"after-hours and overnight bars before 4:00 ET belong to the day before ({v2})");
    double v3 = vwap.Update(Et(11, 4, 0), Et(11, 3, 59), 1.10, 0.90, 1.00, 500_000);
    Check(Math.Abs(v3 - 1.00) < 1e-9, $"the 4:00 ET bar starts over ({v3})");
    double v4 = vwap.Update(Et(11, 4, 1), Et(11, 4, 0), 2.10, 1.90, 2.00, 500_000);
    Check(Math.Abs(v4 - 1.50) < 1e-9, "and later bars weigh in by volume");
    double v4Again = vwap.Update(Et(11, 4, 1), Et(11, 4, 0), 2.10, 1.90, 2.00, 1_500_000);
    Check(Math.Abs(v4Again - 1.75) < 1e-9, "a bar that's still forming replaces its own earlier contribution, not adds to it");
    Check(double.IsNaN(new SessionVwap(open).Update(Et(11, 9, 30), default, 1, 1, 1, 0)), "no volume, no VWAP yet");
    var regular = new SessionVwap(open);
    Check(regular.SessionOf(Et(11, 9, 29)) != regular.SessionOf(Et(11, 9, 30)), "with a 9:30 start, 9:29 belongs to the day before");

    // By session: premarket, regular hours and after hours each start over.
    var sessions = new SessionVwap(afterHours, premarket, open);
    Check(sessions.SessionOf(Et(11, 9, 29)) != sessions.SessionOf(Et(11, 9, 30)), "9:30 starts the regular session");
    Check(sessions.SessionOf(Et(11, 15, 59)) != sessions.SessionOf(Et(11, 16, 0)), "16:00 starts after hours");
    Check(sessions.SessionOf(Et(11, 4, 0)) != sessions.SessionOf(Et(11, 3, 59)), "4:00 starts premarket");
    Check(sessions.SessionOf(Et(10, 16, 0)) == sessions.SessionOf(Et(11, 3, 59)), "overnight stays with the after-hours session before it");
    Check(sessions.SessionOf(Et(11, 9, 30)) == new DateTime(2026, 11, 11, 9, 30, 0), "a session is named by its start, New York time, whatever order the starts came in");
    double a1 = sessions.Update(Et(11, 15, 59), default, 1.00, 1.00, 1.00, 1_000);
    double a2 = sessions.Update(Et(11, 16, 0), Et(11, 15, 59), 5.00, 5.00, 5.00, 1_000);
    Check(Math.Abs(a1 - 1.00) < 1e-9 && Math.Abs(a2 - 5.00) < 1e-9, $"the after-hours VWAP leaves the regular session out ({a2})");

    // Where the line breaks: the first bar of a session with a VWAP, not a quiet bar before it.
    var lines = new SessionVwap(premarket, open, afterHours);
    lines.Update(Et(11, 3, 59), default, 1, 1, 1, 100);
    lines.Update(Et(11, 4, 0), Et(11, 3, 59), 1, 1, 1, 0);
    lines.Update(Et(11, 4, 1), Et(11, 4, 0), 1, 1, 1, 100);
    lines.Update(Et(11, 4, 2), Et(11, 4, 1), 1, 1, 1, 100);
    Check(lines.BeginsLine(Et(11, 3, 59)), "the chart's first bar with volume begins a line");
    Check(!lines.BeginsLine(Et(11, 4, 0)), "a session's opening bar with no volume has no VWAP, so no line begins there");
    Check(lines.BeginsLine(Et(11, 4, 1)), "the session's first bar with volume begins its line");
    Check(!lines.BeginsLine(Et(11, 4, 2)), "later bars carry on the line");

    if (barsFile == null)
        return;

    foreach (var symbol in File.ReadLines(barsFile).Select(l => l.Split('\t')[0]).Distinct())
    {
        var bars = File.ReadLines(barsFile).Select(l => l.Split('\t')).Where(f => f[0] == symbol)
            .Select(f => (Time: DateTime.UnixEpoch.AddSeconds(long.Parse(f[2], CultureInfo.InvariantCulture)), H: double.Parse(f[4], CultureInfo.InvariantCulture),
                L: double.Parse(f[5], CultureInfo.InvariantCulture), C: double.Parse(f[6], CultureInfo.InvariantCulture), V: double.Parse(f[7], CultureInfo.InvariantCulture)))
            .OrderBy(b => b.Time).ToList();

        // The VWAP through the last bar before each time, and where lines began.
        string Replay(SessionVwap replay, DateTime[] at)
        {
            var shown = new List<string>();
            var begins = new List<string>();
            DateTime previous = default;
            double last = double.NaN;
            int next = 0;
            foreach (var bar in bars)
            {
                while (next < at.Length && bar.Time >= at[next])
                {
                    shown.Add($"{TimeZoneInfo.ConvertTimeFromUtc(at[next], eastern):HH:mm} ET {last:0.0000}");
                    next++;
                }

                last = replay.Update(bar.Time, previous, bar.H, bar.L, bar.C, bar.V);
                if (replay.BeginsLine(bar.Time))
                    begins.Add($"{TimeZoneInfo.ConvertTimeFromUtc(bar.Time, eastern):MM-dd HH:mm}");
                previous = bar.Time;
            }

            return $"{string.Join(", ", shown)}; lines begin {string.Join(", ", begins)}";
        }

        Console.WriteLine($"{symbol} VWAP from 4:00 ET only: {Replay(new SessionVwap(premarket), new[] { Et(11, 11, 0), Et(11, 13, 40), Et(11, 16, 0) })}");
        Console.WriteLine($"{symbol} VWAP by session: {Replay(new SessionVwap(premarket, open, afterHours), new[] { Et(11, 9, 30), Et(11, 16, 0), Et(11, 18, 0) })}");
    }
}

// The stale-feed check, offline: any data keeps a feed alive, and the limits by session.
void RunHealth()
{
    var et = (int hour, int minute, int day) => new DateTime(2026, 11, day, 0, 0, 0, DateTimeKind.Utc).AddHours(hour + 5).AddMinutes(minute);
    Check(FeedHealth.SilenceLimit(et(10, 0, 4)) == TimeSpan.FromSeconds(5), "regular hours limit 5s");
    Check(FeedHealth.SilenceLimit(et(5, 0, 4)) == TimeSpan.FromSeconds(15), "premarket limit 15s");
    Check(FeedHealth.SilenceLimit(et(17, 0, 4)) == TimeSpan.FromSeconds(60), "after-hours limit 60s, now that any data counts");
    Check(FeedHealth.SilenceLimit(et(1, 0, 4)) == TimeSpan.FromSeconds(120), "overnight limit 120s");
    Check(FeedHealth.SilenceLimit(et(12, 0, 7)) == null, "Saturday off");

    // After hours on a Wednesday: 60 s allowed.
    long freq = Stopwatch.Frequency;
    long t0 = Stopwatch.GetTimestamp();
    var health = new FeedHealth();
    var afterHours = et(17, 6, 11);
    health.OnData(t0);
    Check(health.Evaluate(afterHours, t0 + 30 * freq) == HealthChange.None, "30 s with no data at all after hours isn't stale any more (it was at 20 s)");
    Check(health.Evaluate(afterHours, t0 + 61 * freq) == HealthChange.WentStale, "61 s with nothing at all is");
    health.OnData(t0 + 62 * freq);
    Check(health.Evaluate(afterHours, t0 + 62 * freq) == HealthChange.Recovered && health.DataSeen == 2, "and any data clears it");
}

// Paper trading against a stand-in for Gateway: the order message field for field against ib_insync's,
// the account and order lifecycle, a reconnect, and refusal of anything but a paper account. No real
// Gateway or account is involved.
async Task RunTrader()
{
    static async Task<bool> WaitFor(Func<bool> condition, double seconds = 5)
    {
        var clock = Stopwatch.StartNew();
        while (!condition() && clock.Elapsed.TotalSeconds < seconds)
            await Task.Delay(20);
        return condition();
    }

    // Built by ib_insync 0.9.86's own placeOrder for server version 176, with no connection
    // (analysis\ibinsync_order_fields.py, run with momo-algo's .venv): LMT buy, MKT sell, STP sell, STP LMT buy GTC.
    string[] reference =
    [
        "3|101|0|NXXT|STK||0.0|||SMART||USD|||||BUY|100|LMT|3.45||DAY||DU000000|O|0||1|0|0|0|0|0|1|0||0.0||||||||0||-1|0|||0|||0|0||0||||||0|||||0|||||||||||0|||0|0|||0||0|0|0|0|||||||0|||||||||0|0|0|0|||0||",
        "3|102|0|NXXT|STK||0.0|||SMART||USD|||||SELL|250|MKT|||DAY||DU000000|O|0||1|0|0|0|0|0|0|0||0.0||||||||0||-1|0|||0|||0|0||0||||||0|||||0|||||||||||0|||0|0|||0||0|0|0|0|||||||0|||||||||0|0|0|0|||0||",
        "3|103|0|NXXT|STK||0.0|||SMART||USD|||||SELL|100|STP||3.1|DAY||DU000000|O|0||1|0|0|0|0|0|0|0||0.0||||||||0||-1|0|||0|||0|0||0||||||0|||||0|||||||||||0|||0|0|||0||0|0|0|0|||||||0|||||||||0|0|0|0|||0||",
        "3|104|0|NXXT|STK||0.0|||SMART||USD|||||BUY|300|STP LMT|0.1234|0.12|GTC||DU000000|O|0||1|0|0|0|0|0|1|0||0.0||||||||0||-1|0|||0|||0|0||0||||||0|||||0|||||||||||0|||0|0|||0||0|0|0|0|||||||0|||||||||0|0|0|0|||0||",
    ];
    IbkrOrder Ord(int id, bool buy, double qty, string type, double lmt, double stp, string tif) =>
        new(id, "DU000000", "NXXT", buy, qty, type, lmt, stp, tif, IbkrTrader.OutsideRthFor(type), "PendingSubmit", 0, 0, DateTime.UtcNow);
    string Joined(IEnumerable<object> fields) =>
        string.Join("|", fields.Select(f => f is bool b ? (b ? "1" : "0") : Convert.ToString(f, CultureInfo.InvariantCulture)));
    string FirstDifference(string a, string b)
    {
        var x = a.Split('|');
        var y = b.Split('|');
        for (int i = 0; i < Math.Max(x.Length, y.Length); i++)
        {
            if (i >= x.Length || i >= y.Length || x[i] != y[i])
                return $"field {i}: ours '{(i < x.Length ? x[i] : "(none)")}', ib_insync '{(i < y.Length ? y[i] : "(none)")}'";
        }

        return "same";
    }

    var ours = new[]
    {
        Ord(101, true, 100, "LMT", 3.45, double.NaN, "DAY"),
        Ord(102, false, 250, "MKT", double.NaN, double.NaN, "DAY"),
        Ord(103, false, 100, "STP", double.NaN, 3.1, "DAY"),
        Ord(104, true, 300, "STP LMT", 0.1234, 0.12, "GTC"),
    };
    for (int i = 0; i < ours.Length; i++)
    {
        string mine = Joined(IbkrTrader.PlaceOrderFields(ours[i]));
        Check(mine == reference[i], $"placeOrder for {ours[i].Type} matches ib_insync field for field, 115 fields ({FirstDifference(mine, reference[i])})");
    }

    Check(!IbkrClient.AllowedRequests.Overlaps([3, 4, 5, 6, 7, 8, 61, 62]), "the market data connection still can't send an order or ask about the account");
    Check(IbkrTrader.AllowedRequests.SetEquals([71, 61, 62, 5, 7, 3, 4]), "the trading connection can send only startApi, positions, account summary, open orders, fills, place and cancel");
    Check(IbkrTrader.ParseTime("20261111  13:45:00 US/Eastern") == new DateTime(2026, 11, 11, 18, 45, 0, DateTimeKind.Utc), "IBKR's fill time with its zone reads as UTC");

    // 2026-11-11: orders went out as "WHLR | tasty" and IBKR answered 200, no security definition.
    Check(TradingSymbols.ToIbkr("WHLR | tasty") == "WHLR" && TradingSymbols.ToIbkr("BRK/B | tasty") == "BRK B" && TradingSymbols.ToIbkr("NXXT") == "NXXT",
        "Quantower's symbol ID loses its connection tag on the way to IBKR, and a share class gets IBKR's space");
    Check(TradingSymbols.FromIbkr("WHLR", "tasty") == "WHLR | tasty" && TradingSymbols.FromIbkr("BRK B", "tasty") == "BRK/B | tasty",
        "and IBKR's symbol gets it back on the way to Quantower");

    // A paper account.
    using var gateway = new FakeTradingGateway();
    var sink = new TraderTestSink();
    var trader = new IbkrTrader("127.0.0.1", gateway.Port, 79);
    trader.SetSink(sink);
    trader.Start();
    await WaitFor(() => trader.Ready && gateway.Count("7") == 1);
    Check(trader.Ready && trader.Account == "DU000000", "ready on the paper account the Gateway names");
    Check(string.Join(",", gateway.Received.Select(r => r[0])) == "71,61,62,5,7", $"asks only for positions, the account summary, open orders and today's fills ({string.Join(",", gateway.Received.Select(r => r[0]))})");
    Check(gateway.Received.First()[2] == "79", "on its own client ID");

    var (id, error) = trader.Place(new IbkrOrderTicket("NXXT", true, 100, "LMT", 3.45, double.NaN, "DAY"));
    await WaitFor(() => gateway.Count("3") == 1);
    string sent = gateway.Count("3") == 1 ? string.Join("|", gateway.LastOf("3")) : "";
    Check(error == null && id == 101 && sent == reference[0], $"a limit order goes out as ib_insync sends it, on the Gateway's next order ID ({FirstDifference(sent, reference[0])})");
    Check(sink.Orders.Any(o => o.Id == 101 && o.Status == "PendingSubmit"), "and shows at once as pending");

    gateway.Send("5", "101", "0", "NXXT", "STK", "", "0", "", "", "SMART", "USD", "NXXT", "NMS", "BUY", "100", "LMT", "3.45", "0", "DAY", "", "DU000000", "O", "0", "", "79", "123", "1", "0");
    gateway.Send("3", "101", "Submitted", "0", "100", "0", "123", "0", "0", "79", "", "0");
    await WaitFor(() => trader.Order(101)?.Status == "Submitted");
    Check(trader.Order(101)?.Status == "Submitted" && trader.OpenOrders().Count == 1 && trader.Order(101).OutsideRth, "IBKR's open order and status make it working, allowed outside regular hours");

    gateway.Send("11", "-1", "101", "0", "NXXT", "STK", "", "0", "", "", "ISLAND", "USD", "NXXT", "NMS", "0001.01", "20261111 13:45:00 US/Eastern", "DU000000", "ISLAND", "BOT", "100", "3.45", "123", "79", "0", "100", "3.45", "", "", "", "", "1");
    gateway.Send("59", "1", "0001.01", "1.0", "USD", "1.7976931348623157E308", "1.7976931348623157E308", "");
    gateway.Send("3", "101", "Filled", "100", "0", "3.45", "123", "0", "3.45", "79", "", "0");
    gateway.Send("61", "3", "DU000000", "0", "NXXT", "STK", "", "0", "", "", "NASDAQ", "USD", "NXXT", "NMS", "100", "3.46");
    gateway.Send("63", "1", "9001", "DU000000", "NetLiquidation", "1000000.00", "USD");
    await WaitFor(() => trader.Positions().Count == 1 && trader.Fills().Any(f => f.Commission == 1.0) && trader.Order(101).IsDone && sink.Accounts.Any());
    var fill = trader.Fills().FirstOrDefault();
    Check(trader.Order(101).IsDone && trader.OpenOrders().Count == 0, "filled, it's no longer working");
    Check(fill is { Buy: true, Shares: 100, Price: 3.45, Commission: 1.0, OrderId: 101 } && fill.TimeUtc == new DateTime(2026, 11, 11, 18, 45, 0, DateTimeKind.Utc), $"the fill has its commission and IBKR's time ({fill})");
    Check(trader.Positions() is [{ Symbol: "NXXT", Quantity: 100, AvgCost: 3.46 }], "the position is IBKR's");
    Check(sink.Accounts.Any(a => a.Account == "DU000000" && a.Values.GetValueOrDefault("NetLiquidation") == "1000000.00"), "the account's values reach the sink");
    Check(double.IsNaN(fill.RealizedPnl), "a buy that opened has no realized P&L (IBKR sends the largest double for it)");
    gateway.Send("11", "-1", "101", "0", "NXXT", "STK", "", "0", "", "", "ISLAND", "USD", "NXXT", "NMS", "0001.02", "20261111 13:50:00 US/Eastern", "DU000000", "ISLAND", "SLD", "100", "3.60", "123", "79", "0", "100", "3.60", "", "", "", "", "1");
    gateway.Send("59", "1", "0001.02", "1.0", "USD", "13.0", "1.7976931348623157E308", "");
    await WaitFor(() => trader.Fills().Any(f => f.ExecId == "0001.02" && f.Commission == 1.0));
    var closing = trader.Fills().First(f => f.ExecId == "0001.02");
    Check(closing is { Buy: false, Shares: 100, Price: 3.60, Commission: 1.0, RealizedPnl: 13.0 }, $"a sell that closed has IBKR's realized P&L, after commissions ({closing})");
    Check(IbkrTrader.Reported(1.7976931348623157E308) is double.NaN && IbkrTrader.Reported(-42.5) == -42.5, "IBKR's 'not set' reads as no number; a loss reads as itself");
    var day = new DateTime(2026, 11, 19, 15, 0, 0, DateTimeKind.Utc);
    IbkrFill F(string id, int min, bool buy, double shares, string symbol = "SEAT") => new(id, 1, "DU1", symbol, buy, shares, 1.9, day.AddMinutes(min), 1);
    var day1 = new[] { F("a", 0, true, 1000), F("b", 1, false, 400), F("c", 2, false, 600), F("d", 3, false, 300), F("e", 4, true, 300), F("f", 5, true, 200, "VIVS") };
    var closes = IbkrTrader.Closes(day1, new Dictionary<string, double>());
    Check(!closes["a"] && closes["b"] && closes["c"] && !closes["d"] && closes["e"] && !closes["f"],
        $"from flat: buy 1,000 opens, selling 400 and 600 close, then selling 300 opens a short that buying 300 closes; each symbol on its own ({string.Join(" ", closes.Select(c => c.Key + (c.Value ? "=close" : "=open")))})");
    var carried = IbkrTrader.Closes([F("g", 0, false, 500)], new Dictionary<string, double> { [IbkrTrader.PositionKey("DU1", "SEAT")] = 500 });
    Check(carried["g"], "held overnight: the first sell of the day closes");
    double pct = IbkrTrader.PnlPercent(false, 3.60, 100, 14, 13);
    Check(Math.Abs(pct - (13.0 / 346 * 100)) < 1e-9, $"selling 100 at 3.60 for 14 gross, 13 net: the shares cost 346, so +3.76% ({pct:0.00})");
    double cover = IbkrTrader.PnlPercent(true, 3.40, 100, 9, 8);
    Check(Math.Abs(cover - (8.0 / 349 * 100)) < 1e-9 && double.IsNaN(IbkrTrader.PnlPercent(false, 1, 10, 20, 19)),
        $"covering a short at 3.40 for 9 gross: shorted for 349, so +{cover:0.00}%; a cost of nothing or less gives no percentage");

    var (id2, _) = trader.Place(new IbkrOrderTicket("NXXT", false, 100, "LMT", 3.60, double.NaN, "DAY"));
    gateway.Send("3", id2.ToString(CultureInfo.InvariantCulture), "Submitted", "0", "100", "0", "124", "0", "0", "79", "", "0");
    await WaitFor(() => trader.Order(id2)?.Status == "Submitted");
    string modifyError = trader.Modify(id2, 100, 3.65, double.NaN);
    await WaitFor(() => gateway.Count("3") == 3);
    var modified = gateway.LastOf("3");
    Check(id2 == 102 && modifyError == null && modified[1] == "102" && modified[19] == "3.65" && modified[16] == "SELL", "a change goes out on the same order ID with the new price");
    string cancelError = trader.Cancel(id2);
    await WaitFor(() => gateway.Count("4") == 1);
    Check(cancelError == null && string.Join("|", gateway.LastOf("4") ?? []) == "4|1|102|", "a cancel is 4, 1, the ID and an empty time");
    gateway.Send("3", "102", "Cancelled", "0", "100", "0", "124", "0", "0", "79", "", "0");
    await WaitFor(() => sink.Orders.LastOrDefault(o => o.Id == 102)?.Status == "Cancelled");
    Check(trader.Order(102)?.IsDone == true && sink.Orders.Last(o => o.Id == 102).Status == "Cancelled", "cancelled, it's done");

    var (id3, _) = trader.Place(new IbkrOrderTicket("NXXT", true, 100, "LMT", 3.457, double.NaN, "DAY"));
    gateway.Send("4", "2", id3.ToString(CultureInfo.InvariantCulture), "110", "The price does not conform to the minimum price variation for this contract.", "");
    await WaitFor(() => sink.Orders.LastOrDefault(o => o.Id == id3)?.IsDone == true);
    Check(gateway.LastOf("3")[19] == "3.457", "a price goes out exactly as entered, not cut to the display's decimals");
    Check(sink.Messages.Any(m => m.OrderId == id3 && m.Code == 110 && m.Ended) && trader.Order(id3)?.Status == "Inactive",
        "IBKR's 110 on an order it never acknowledged reaches the sink as a refusal, and the order is done");

    // A warning on a working order leaves it working; an error ends it, as in ib_insync.
    var (idWarn, _) = trader.Place(new IbkrOrderTicket("NXXT", true, 100, "LMT", 3.30, double.NaN, "DAY"));
    gateway.Send("3", idWarn.ToString(CultureInfo.InvariantCulture), "Submitted", "0", "100", "0", "126", "0", "0", "79", "", "0");
    await WaitFor(() => trader.Order(idWarn)?.Status == "Submitted");
    gateway.Send("4", "2", idWarn.ToString(CultureInfo.InvariantCulture), "399", "Order Message: warning", "");
    await WaitFor(() => sink.Messages.Any(m => m.OrderId == idWarn));
    Check(sink.Messages.Any(m => m.OrderId == idWarn && !m.Ended) && !trader.Order(idWarn).IsDone, "a warning (399) leaves a working order working");
    gateway.Send("4", "2", idWarn.ToString(CultureInfo.InvariantCulture), "201", "Order rejected - reason: test", "");
    await WaitFor(() => sink.Orders.LastOrDefault(o => o.Id == idWarn)?.IsDone == true);
    Check(trader.Order(idWarn)?.Status == "Cancelled" && sink.Messages.Any(m => m.OrderId == idWarn && m.Code == 201 && m.Ended), "an error (201) ends it");

    Check(trader.Place(new IbkrOrderTicket("NXXT", true, 10.5, "LMT", 3, double.NaN, "DAY")).Error != null, "part shares are refused before anything is sent");
    Check(trader.Place(new IbkrOrderTicket("NXXT", true, 100, "LMT", double.NaN, double.NaN, "DAY")).Error != null, "so is a limit order with no price");
    Check(trader.Place(new IbkrOrderTicket("NXXT", true, 100, "TRAIL", double.NaN, double.NaN, "DAY")).Error != null, "and an order type that isn't built");
    Check(gateway.Count("3") == 5, "none of those went out");

    // A reconnect: what IBKR no longer reports is no longer shown, and order IDs don't repeat.
    var (id4, _) = trader.Place(new IbkrOrderTicket("NXXT", true, 100, "LMT", 3.40, double.NaN, "DAY"));
    gateway.Send("3", id4.ToString(CultureInfo.InvariantCulture), "Submitted", "0", "100", "0", "125", "0", "0", "79", "", "0");
    await WaitFor(() => trader.Order(id4)?.Status == "Submitted");
    gateway.Drop();
    await WaitFor(() => gateway.Connections == 2 && trader.Ready && gateway.Count("7") == 2, 10);
    gateway.Send("62", "1");
    gateway.Send("53", "1");
    await WaitFor(() => trader.Positions().Count == 0 && trader.Order(id4)?.Status == IbkrTrader.GoneStatus);
    Check(sink.States.Any(s => !s.Ready) && trader.Ready, "the trader says when it's down and comes back by itself");
    Check(trader.Positions().Count == 0 && sink.Positions.Last().Quantity == 0, "a position missing from IBKR's list after a reconnect shows as closed");
    Check(trader.Order(id4)?.Status == IbkrTrader.GoneStatus && trader.OpenOrders().Count == 0, "a working order IBKR no longer reports isn't shown as working");
    var (id5, _) = trader.Place(new IbkrOrderTicket("NXXT", true, 100, "LMT", 3.40, double.NaN, "DAY"));
    Check(id5 > id4, $"the Gateway's next ID after a reconnect (101 here) doesn't reuse one already used ({id5})");

    // A position key cancels the stock's working buys first. LVWR, 2026-11-19: Alt+1's IOC buy 393 was still filling
    // on paper when Shift+Z sent sell 394, and IBKR repriced the sell so as not to cross it, and the sell died unfilled.
    string Num(int n) => n.ToString(CultureInfo.InvariantCulture);
    void LvwrStatus(int oid, string status, int filled, int qty) =>
        gateway.Send("3", Num(oid), status, Num(filled), Num(qty - filled), filled > 0 ? "0.647" : "0", "200", "0", "0.647", "79", "", "0");
    void TapeFill(string symbol, int oid, string exec, int shares, string req = "-1") =>
        gateway.Send("11", req, Num(oid), "0", symbol, "STK", "", "0", "", "", "NYSE", "USD", symbol, symbol, exec, "20261119 15:44:41 US/Eastern", "DU000000", "NYSE", "BOT", Num(shares), "0.647", "200", "79", "0", Num(shares), "0.647", "", "", "", "", "1");
    void LvwrPosition(int shares) =>
        gateway.Send("61", "3", "DU000000", "0", "LVWR", "STK", "", "0", "", "", "NYSE", "USD", "LVWR", "LVWR", Num(shares), "0.647");

    var (buyId, _) = trader.Place(new IbkrOrderTicket("LVWR", true, 1000, "LMT", 0.6632, double.NaN, "IOC"));
    LvwrStatus(buyId, "Submitted", 400, 1000);
    TapeFill("LVWR", buyId, "0002.01", 400);
    LvwrPosition(400);
    var (sellId, _) = trader.Place(new IbkrOrderTicket("LVWR", false, 100, "LMT", 0.70, double.NaN, "DAY"));
    LvwrStatus(sellId, "Submitted", 0, 100);
    await WaitFor(() => trader.Positions().Any(p => p is { Symbol: "LVWR", Quantity: 400 }) && trader.Order(buyId)?.Filled == 400 && trader.Order(sellId)?.Status == "Submitted");
    Check(trader.CancelSide("SEAT", true, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1)) == null && gateway.Count("4") == 1,
        "with nothing working on that side, nothing is cancelled");

    var clearing = Task.Run(() => trader.CancelSide("LVWR", true, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2)));
    await WaitFor(() => gateway.Count("4") == 2);
    await Task.Delay(100);
    Check(gateway.Count("4") == 2 && gateway.LastOf("4")[2] == Num(buyId), $"Shift+Z with the buy still filling cancels that buy, not the working sell ({string.Join("|", gateway.LastOf("4") ?? [])})");
    TapeFill("LVWR", buyId, "0002.02", 100);
    LvwrStatus(buyId, "Cancelled", 500, 1000);
    await Task.Delay(200);
    Check(!clearing.IsCompleted, "IBKR's cancel confirmed, with a fill just before it: it still waits for the position to count that fill");
    LvwrPosition(500);
    string said = await clearing;
    Check(said?.Contains($"cancelled working buy {buyId} (500 of 1,000 filled, Cancelled)") == true && said.Contains("; done in ") && said.Contains("position up to date"),
        $"and goes on once the position says 500 ({said})");
    Check(trader.Positions().Single(p => p.Symbol == "LVWR").Quantity == 500 && !trader.Order(sellId).IsDone, "so the sell is sized on 500, and the working sell was left alone");

    var (stuckId, _) = trader.Place(new IbkrOrderTicket("LVWR", true, 1000, "LMT", 0.6632, double.NaN, "IOC"));
    LvwrStatus(stuckId, "Submitted", 0, 1000);
    await WaitFor(() => trader.Order(stuckId)?.Status == "Submitted");
    var waited = Stopwatch.StartNew();
    string stuck = trader.CancelSide("LVWR", true, TimeSpan.FromMilliseconds(300), TimeSpan.FromMilliseconds(300));
    Check(stuck?.Contains("IBKR hadn't confirmed after") == true && stuck.Contains("position up to date 0 ms later") && waited.ElapsedMilliseconds is >= 300 and < 550,
        $"IBKR not confirming: it goes on after its wait; with no fill since the last position, no wait for one ({stuck}; {waited.ElapsedMilliseconds} ms)");
    LvwrStatus(stuckId, "Cancelled", 0, 1000);

    var (lateId, _) = trader.Place(new IbkrOrderTicket("LVWR", true, 1000, "LMT", 0.6632, double.NaN, "IOC"));
    LvwrStatus(lateId, "Submitted", 0, 1000);
    await WaitFor(() => trader.Order(lateId)?.Status == "Submitted" && trader.Order(stuckId)?.IsDone == true);
    var lateClearing = Task.Run(() => trader.CancelSide("LVWR", true, TimeSpan.FromSeconds(2), TimeSpan.FromMilliseconds(200)));
    await WaitFor(() => gateway.LastOf("4")?[2] == Num(lateId));
    TapeFill("LVWR", lateId, "0002.03", 100);
    LvwrStatus(lateId, "Cancelled", 100, 1000);
    string late = await lateClearing;
    Check(late?.Contains("done in ") == true && late.Contains("no position after the last fill within") && trader.Positions().Single(p => p.Symbol == "LVWR").Quantity == 500,
        $"no position after the last fill: it goes on after its wait, by the position it has, never more ({late})");

    TapeFill("OLDY", 7, "0003.01", 100, req: "9002");
    await WaitFor(() => trader.Fills().Any(f => f.ExecId == "0003.01"));
    Check(trader.PositionCountsFills("OLDY"), "today's fills asked for at connect don't count as new: no wait for a position after them");
    TapeFill("OLDY", 7, "0003.02", 100);
    await WaitFor(() => trader.Fills().Any(f => f.ExecId == "0003.02"));
    Check(!trader.PositionCountsFills("OLDY"), "a fill as it happens does, until IBKR's position comes in");
    await trader.StopAsync();

    // A live account: nothing but startApi, and every order refused.
    using var live = new FakeTradingGateway { Accounts = "U1234567" };
    var liveTrader = new IbkrTrader("127.0.0.1", live.Port, 79);
    liveTrader.Start();
    await WaitFor(() => live.Received.Count >= 1);
    await Task.Delay(500);
    var liveOrder = liveTrader.Place(new IbkrOrderTicket("NXXT", true, 100, "LMT", 3.45, double.NaN, "DAY"));
    Check(!liveTrader.Ready && liveOrder.Error != null, $"logged in to a live account it isn't ready and refuses orders ({liveOrder.Error})");
    Check(live.Received.All(r => r[0] == "71"), $"and sends nothing but startApi ({string.Join(",", live.Received.Select(r => r[0]))})");
    await liveTrader.StopAsync();
}

// Computed LULD bands, by the plan's rules, on a made-up session in Eastern time, then IBKR's halt flag and
// odd-lot marker through the stand-in for Gateway.
async Task RunLuld()
{
    var eastern = TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");
    long Et(int h, int m, double s, int day = 11) =>
        TimeZoneInfo.ConvertTimeToUtc(new DateTime(2026, 11, day, h, m, 0, DateTimeKind.Unspecified), eastern).Ticks + (long)(s * TimeSpan.TicksPerSecond);
    string Show(LuldBand b, long t, double prevClose, bool tier1 = false) =>
        b.TryGetBands(t, prevClose, tier1, out var x) ? $"{x.Lower}/{x.Upper} ref {x.Reference:0.####}{(x.Exact ? "" : " ~")}" : "none";

    var luld = new LuldBand();
    luld.OnTrade(9.80, Et(9, 0, 0), true);
    Check(Show(luld, Et(9, 10, 0), 9.50) == "none", "no band in premarket, and premarket prints don't count");

    luld.OnTrade(10.00, Et(9, 30, 5), true);
    Check(Show(luld, Et(9, 30, 6), 9.50) == "9/11 ref 10", $"the first print from 9:30 sets the reference: Tier 2 over $3 is 10% ({Show(luld, Et(9, 30, 6), 9.50)})");
    Check(Show(luld, Et(9, 30, 6), 9.50, tier1: true) == "9.5/10.5 ref 10", "Tier 1 over $3 is 5%");

    for (int i = 0; i < 10; i++)
        luld.OnTrade(10.05, Et(9, 30, 10 + i), true);
    Check(Show(luld, Et(9, 31, 0), 9.50) == "9/11 ref 10", "a mean under 1% away leaves the reference alone");

    for (int i = 0; i < 20; i++)
        luld.OnTrade(10.40, Et(9, 30, 40 + i), false);
    Check(Show(luld, Et(9, 31, 0), 9.50) == "9/11 ref 10", "odd lots don't move it");

    // The mean of the 11 round lots so far is 10.0455. At 10.40 a print, the second takes it to exactly 10.10,
    // 1% away, and the plan moves the reference to that mean at once, not to the last price.
    for (int i = 0; i < 20; i++)
        luld.OnTrade(10.40, Et(9, 31, 0 + i), true);
    string moved = Show(luld, Et(9, 31, 20), 9.50);
    Check(moved == "9.09/11.11 ref 10.1", $"a mean at least 1% away, after 30 s, becomes the reference at once ({moved})");

    for (int i = 0; i < 20; i++)
        luld.OnTrade(11.50, Et(9, 31, 21 + i * 0.1), true);
    Check(Show(luld, Et(9, 31, 25), 9.50) == moved, "each reference stands at least 30 s");
    Check(Show(luld, Et(9, 31, 51), 9.50) != moved, "and moves once the 30 s are up");

    luld.OnTrade(11.00, Et(9, 45, 0), true);
    string later = Show(luld, Et(9, 45, 0), 9.50);
    Check(later == "9.9/12.1 ref 11", $"trades over five minutes old drop out of the mean ({later})");

    luld.OnHalt(true);
    Check(Show(luld, Et(9, 46, 0), 9.50) == "none", "no band during a pause");
    luld.OnHalt(false);
    Check(Show(luld, Et(9, 51, 0), 9.50) == "none", "nor after it until the reopening print");
    luld.OnTrade(13.20, Et(9, 51, 2), true);
    Check(Show(luld, Et(9, 51, 3), 9.50) == "11.88/14.52 ref 13.2", "the reopening print is the new reference");

    // IBKR stopped streaming mid-pause, so the reopening may come and go unseen.
    var unwatched = new LuldBand();
    unwatched.OnTrade(4.00, Et(10, 0, 0), true);
    unwatched.OnHalt(true);
    unwatched.ForgetHalt();
    Check(Show(unwatched, Et(10, 9, 0), 4.00) == "none", "a forgotten halt leaves no band");
    unwatched.OnTrade(5.00, Et(10, 9, 1), true);
    Check(Show(unwatched, Et(10, 9, 2), 4.00) == "4.5/5.5 ref 5 ~", "the next print starts it again, marked rough");
    unwatched.ForgetHalt();
    Check(Show(unwatched, Et(10, 9, 3), 4.00) == "4.5/5.5 ref 5 ~", "forgetting with no halt on changes nothing");

    // NXXT 2026-11-11: the auction's prints came about 0.2 s before the halt flag cleared, then a
    // 3.1812 print off the auction became the reference instead of the auction's 3.45.
    var early = new LuldBand();
    early.OnTrade(3.00, Et(13, 0, 0), true);
    early.OnHalt(true);
    early.OnTrade(3.40, Et(13, 10, 0), true);
    Check(Show(early, Et(13, 10, 0), 0.1865) == "none", "an ordinary print during a pause starts nothing");
    early.OnTrade(3.45, Et(13, 10, 40.18), false, reopening: true);
    early.OnTrade(3.45, Et(13, 10, 40.18), true, reopening: true);
    early.OnTrade(3.1812, Et(13, 10, 40.30), true);
    string reopened = Show(early, Et(13, 10, 40.30), 0.1865);
    Check(reopened == "3.3/3.6 ref 3.45", $"a reopening print (condition 5) restarts the band before the halt flag clears, exact ({reopened})");
    early.OnHalt(false);
    early.OnTrade(3.50, Et(13, 10, 41), true);
    Check(Show(early, Et(13, 10, 41), 0.1865) == "3.3/3.6 ref 3.45", "and the flag clearing after it doesn't restart it again");
    early.OnHalt(true);
    Check(Show(early, Et(13, 11, 0), 0.1865) == "none", "the next pause still stops it");
    early.OnHalt(false);
    early.OnTrade(3.60, Et(13, 16, 0), true);
    Check(Show(early, Et(13, 16, 0), 0.1865) == "3.45/3.75 ref 3.6", "and a reopening that follows the flag still works without condition 5");

    Check(LuldBand.Label(true, true) == "Limit Up" && LuldBand.Label(false, true) == "Limit Down"
          && LuldBand.Label(true, false) == "Limit Up ~" && LuldBand.Label(false, false) == "Limit Down ~", "the rows read Limit Up and Limit Down, with ~ when rough");
    Check(LuldBand.IsLabel("Limit Down ~") && !LuldBand.IsLabel("LULD") && !LuldBand.IsLabel("ARCA"), "and only those count as band rows");

    // No rows past the bands: bids under Limit Down, asks over Limit Up.
    var bidBook = new List<IbkrDepthRow> { new(5.02, 300, "NSDQ"), new(5.00, 500, "ARCA"), new(4.95, 200, "NSDQ"), new(4.94, 100, "EDGX"), new(4.90, 1000, "NSDQ") };
    var askBook = new List<IbkrDepthRow> { new(5.05, 300, "NSDQ"), new(5.50, 400, "ARCA"), new(5.51, 100, "NSDQ"), new(5.60, 900, "NSDQ") };
    var inBand = new List<IbkrDepthRow>();
    string Prices(List<IbkrDepthRow> rows) => string.Join(" ", rows.Select(r => r.Price));
    var exactBand = new LuldBand.Bands(4.95, 5.50, 5.225, true);
    LuldBand.Within(bidBook, exactBand, true, inBand);
    Check(Prices(inBand) == "5.02 5 4.95", $"bids under Limit Down go, the one at it stays ({Prices(inBand)})");
    LuldBand.Within(askBook, exactBand, false, inBand);
    Check(Prices(inBand) == "5.05 5.5", $"asks over Limit Up go, the one at it stays ({Prices(inBand)})");
    var roughBand = exactBand with { Exact = false };
    LuldBand.Within(bidBook, roughBand, true, inBand);
    Check(Prices(inBand) == "5.02 5 4.95 4.94", $"a rough band keeps rows within 1% past it, as the real one may allow them ({Prices(inBand)})");
    LuldBand.Within(askBook, roughBand, false, inBand);
    Check(Prices(inBand) == "5.05 5.5 5.51", $"on both sides ({Prices(inBand)})");
    LuldBand.Within(bidBook, exactBand with { Lower = double.NaN }, true, inBand);
    Check(inBand.Count == bidBook.Count, "with no lower band, the bids all stay");
    Check(PrintFilter.IsReopening("@5 X"u8) && PrintFilter.IsReopening("@5 x"u8) && !PrintFilter.IsReopening("@  o"u8) && !PrintFilter.IsReopening("F I"u8), "condition 5 marks a reopening print");

    var low = new LuldBand();
    low.OnTrade(2.50, Et(9, 30, 1), true);
    Check(Show(low, Et(10, 0, 0), 2.10) == "2/3 ref 2.5", "prev close from $0.75 to $3 is 20%");
    Check(Show(low, Et(15, 36, 0), 2.10) == "1.5/3.5 ref 2.5", "doubled in the last 25 minutes for Tier 2 under $3");
    Check(Show(low, Et(16, 0, 0), 2.10) == "none", "no band from 16:00");

    var big = new LuldBand();
    big.OnTrade(20.00, Et(15, 30, 0), true);
    Check(Show(big, Et(15, 36, 0), 19.00) == "18/22 ref 20 ~", $"Tier 2 over $3 isn't doubled at the close ({Show(big, Et(15, 36, 0), 19.00)})");
    Check(Show(big, Et(15, 36, 0), 19.00, tier1: true) == "18/22 ref 20 ~", "Tier 1 is, 5% to 10%");
    Check(!big.TryGetBands(Et(15, 36, 0), 19.00, false, out var bigBand) || !bigBand.Exact, "a symbol first seen mid-session is marked as rough");

    var penny = new LuldBand();
    penny.OnTrade(0.50, Et(9, 30, 1), true);
    Check(Show(penny, Et(10, 0, 0), 0.48) == "0.35/0.65 ref 0.5", "under $0.75, the lesser of $0.15 or 75%: $0.15 here");
    var subpenny = new LuldBand();
    subpenny.OnTrade(0.10, Et(9, 30, 1), true);
    Check(Show(subpenny, Et(10, 0, 0), 0.09) == "0.03/0.18 ref 0.1", "and 75% here, rounded to the penny");
    Check(Show(subpenny, Et(15, 40, 0), 0.09) == "none/0.25 ref 0.1".Replace("none", "NaN"), "a lower band at or under zero isn't there");

    luld.OnTrade(12.00, Et(9, 30, 3, day: 12), true);
    Check(Show(luld, Et(9, 30, 4, day: 12), 13.0) == "10.8/13.2 ref 12", "a new day starts over");

    // Which side the Res row goes on (2026-11-17): the asks for a halt up, the bids for a halt down.
    var run = new LuldBand();
    run.OnTrade(4.00, Et(10, 0, 0), true);
    run.OnTrade(4.40, Et(10, 0, 10), true);
    Check(ResSide.FromBand(run, 4.40, Et(10, 0, 11)) == ResSide.Up, "a halt with the last trade above the band's reference went up");
    Check(ResSide.FromBand(run, 3.60, Et(10, 0, 11)) == ResSide.Down, "and below it, down");
    Check(ResSide.FromBand(run, 4.00, Et(10, 0, 11)) == ResSide.Unknown && ResSide.FromBand(run, double.NaN, Et(10, 0, 11)) == ResSide.Unknown,
        "at the reference, or with no last trade, it can't say");
    Check(ResSide.FromBand(run, 4.40, Et(10, 5, 11)) == ResSide.Unknown, "nor with no round lot in the last five minutes, when the reference may be from earlier");
    Check(ResSide.FromBand(run, 4.40, Et(9, 0, 0, day: 13)) == ResSide.Unknown, "nor outside 9:30-16:00, where there's no band");
    run.OnTrade(4.40, Et(10, 6, 0), true);
    run.OnHalt(true);
    Check(ResSide.FromBand(run, 4.40, Et(10, 6, 1)) == ResSide.Unknown, "the halt stops the band, so it's asked as the halt begins");
    Check(ResSide.FromAuction(2.25, 3.55) == ResSide.Down, "a halt the band didn't see goes the way its auction points: NXXT paused limit-down at 3.55, indicative 2.25");
    Check(ResSide.FromAuction(4.80, 4.40) == ResSide.Up, "an auction above the last trade points up");
    Check(ResSide.FromAuction(2.24000001, 2.24) == ResSide.Unknown && ResSide.FromAuction(2.25, double.NaN) == ResSide.Unknown,
        "an auction at the last trade, or no last trade, points neither way");
    Check(ResSide.OnAsks(ResSide.Up) && !ResSide.OnBids(ResSide.Up), "halt up: among the asks only");
    Check(ResSide.OnBids(ResSide.Down) && !ResSide.OnAsks(ResSide.Down), "halt down: among the bids only");
    Check(ResSide.OnBids(ResSide.Unknown) && ResSide.OnAsks(ResSide.Unknown), "which way not known: both sides, as before");

    Check(PrintFilter.IsOddLot("F I"u8) && PrintFilter.IsOddLot("I"u8) && !PrintFilter.IsOddLot("F"u8) && !PrintFilter.IsOddLot(""u8), "IBKR's condition I marks an odd lot");

    using var gateway = new FakeGateway();
    var sink = new IbkrTestSink();
    var client = new IbkrClient(new IbkrConfig(true, "127.0.0.1", gateway.Port, 1, false));
    client.SetSink(sink);
    client.Start();
    var timer = Stopwatch.StartNew();
    while (client.State != IbkrState.Live && timer.Elapsed < TimeSpan.FromSeconds(10))
        await Task.Delay(20);
    client.SetQuote("HALT", true);
    client.SetTape("ODD", true);
    while (timer.Elapsed < TimeSpan.FromSeconds(10) && (gateway.TapeRequestId == 0 || !gateway.MarketDataRequests.Any(r => r.Symbol == "HALT")))
        await Task.Delay(20);
    gateway.SendHalted("HALT", 2);
    gateway.SendPrint("F I");
    gateway.SendPrint("F");
    gateway.SendPrint("5");
    gateway.SendAuction("HALT", 35, 2.24000001);
    gateway.SendAuction("HALT", 34, 121932);
    gateway.SendAuction("HALT", 36, -657);
    await Task.Delay(300);
    var smartQuote = gateway.MarketDataRequests.First(r => r.Symbol == "HALT" && r.Exchange == "SMART");
    Check(gateway.GenericTicks.GetValueOrDefault(smartQuote.Id) == "225", "the SMART quote asks for the auction numbers (generic tick 225)");
    Check(sink.Auctions.Contains(("HALT", 35, 2.24000001)) && sink.Auctions.Contains(("HALT", 34, 121932)) && sink.Auctions.Contains(("HALT", 36, -657)),
        $"the auction's price, paired shares and imbalance reach the bridge ({string.Join(", ", sink.Auctions)})");
    Check(sink.Halts.Contains(("HALT", 2)), "IBKR's Halted tick reaches the bridge, 2 for a volatility pause");
    Check(sink.OddLots.GetValueOrDefault("ODD") == 1 && sink.Prints.GetValueOrDefault("ODD")?.Count == 3, "odd lots still reach the tape, marked so the band skips them");
    Check(sink.Reopenings.GetValueOrDefault("ODD") == 1, "IBKR's condition 5 reaches the bridge marked as a reopening print");

    // A DOM moved off a paused symbol: its quote is cancelled, and a new one won't say the pause ended.
    client.SetQuote("HALT", false);
    while (timer.Elapsed < TimeSpan.FromSeconds(10) && !gateway.MarketDataCancels.Contains(smartQuote.Id))
        await Task.Delay(20);
    gateway.SendPrint("F");
    await Task.Delay(300);
    Check(sink.QuoteStops.Contains("HALT"), "a cancelled quote tells the bridge its halt isn't watched any more");
    client.SetTape("ODD", false);
    await Task.Delay(300);
    Check(!sink.QuoteStops.Contains("ODD"), "a cancelled tape doesn't");
    await client.StopAsync();

    // The Res row's countdown: seconds to the next 5-minute mark from a pause seen to start.
    long Sec(double s) => (long)(s * Stopwatch.Frequency);
    Check(PauseClock.Left(0) == 300 && PauseClock.Left(0.4) == 300 && PauseClock.Left(1) == 299, "a pause starts at 300 seconds");
    Check(PauseClock.Left(299.2) == 1 && PauseClock.Left(300) == 300 && PauseClock.Left(301) == 299, "it counts to 1, then an extension starts again at 300");
    Check(PauseClock.Left(-2) == 300, "a stamp a moment before the start reads as the start");
    Check(PauseClock.RowSize(300, 999_999) == 300_000 && PauseClock.RowSize(1, 999_999) == 1_000, "the Res row reads the seconds times 1000, 300,000 down to 1,000");
    Check(PauseClock.RowSize(null, 999_999) == 999_999, "and the fixed size when the reopening time isn't known");
    var pauses = new PauseClock();
    long t0 = Sec(1_000_000);
    pauses.OnHalted("SEEN", 0, 2, t0);
    Check(pauses.SecondsLeft("SEEN", t0 + Sec(60.5)) == 240, "a pause seen to start counts down from its start");
    pauses.OnHalted("SEEN", -1, 2, t0 + Sec(70));
    Check(pauses.SecondsLeft("SEEN", t0 + Sec(70)) == 230, "a reconnect that meets the pause as new keeps its start");
    pauses.OnHalted("SEEN", 2, 0, t0 + Sec(300.1));
    Check(pauses.SecondsLeft("SEEN", t0 + Sec(301)) == null, "trading again ends it");
    pauses.OnHalted("OPENED", -1, 2, t0);
    Check(pauses.SecondsLeft("OPENED", t0 + Sec(10)) == null, "a pause already on when the symbol was opened has no known start");
    pauses.OnHalted("NEWS", 0, 1, t0);
    Check(pauses.SecondsLeft("NEWS", t0 + Sec(10)) == null, "a halt has no set reopening time");
    pauses.OnHalted("MISSED", 0, 2, t0);
    pauses.OnHalted("MISSED", -1, 2, t0 + Sec(7200));
    Check(pauses.SecondsLeft("MISSED", t0 + Sec(7200)) == null, "a start over an hour old, met again with nothing between, is from a reopening the plugin missed");
    pauses.OnHalted("SWITCHED", 0, 2, t0);
    pauses.Forget("SWITCHED");
    pauses.OnHalted("SWITCHED", -1, 2, t0 + Sec(900));
    Check(pauses.SecondsLeft("SWITCHED", t0 + Sec(900)) == null, "a cancelled stream forgets the start: the pause met later may be another one");

    // Opened mid-pause: estimated from the last trade, the pause taken to begin a second after it.
    long lastMs = 1_794_845_271_200; // GDHG's last trade before its pause, 08:07:51.2 local on 11-16
    Check(PauseClock.EstimatedLeft(lastMs, lastMs + 1000) == 300 && PauseClock.EstimatedLeft(lastMs, lastMs + 300) == 300, "a pause opened just after its last trade reads 300");
    Check(PauseClock.EstimatedLeft(lastMs, lastMs + 178_500) == 123, "GDHG opened at 08:10:49.7 reads 123 s, and it traded again at 08:12:52");
    Check(PauseClock.EstimatedLeft(lastMs, lastMs + 421_000) == 180, "7 minutes in, an extension is 3 minutes from the 10-minute mark");
    Check(PauseClock.EstimatedLeft(lastMs, lastMs - 400) == 300, "a last trade a moment ahead of this PC's clock reads as the start");
    Check(PauseClock.EstimatedLeft(0, lastMs) == null && PauseClock.EstimatedLeft(lastMs, lastMs + 3_600_001) == null, "no estimate with no last trade, or one over an hour old");
}

// Order hotkeys: the price, the keys, which stock and size, what a key-down does, then the listener with stand-in
// presses. No order goes anywhere.
async Task RunHotkey()
{
    var alt1 = OrderHotkey.BuyAsk;
    var shiftZ = OrderHotkey.SellAll;
    var shiftX = OrderHotkey.SellHalf;
    Check(alt1 is { Keys: "Alt+1", Buy: true, Shares: 1000, Offset: 0.025, Tif: "IOC", OfPosition: 0 }, "Alt+1 buys 1,000 shares at the ask plus 2.5%, immediate or cancel");
    Check(shiftZ is { Keys: "Shift+Z", Buy: false, Offset: 0.025, Tif: "IOC", OfPosition: 1 }, "Shift+Z sells the whole position at the bid minus 2.5%, immediate or cancel");
    Check(shiftX is { Keys: "Shift+X", Buy: false, Offset: 0.025, Tif: "IOC", OfPosition: 0.5 }, "Shift+X sells half the position at the bid minus 2.5%, immediate or cancel");
    Check(alt1.Describe() == "buy 1,000 shares, IOC (DAY outside 9:30-16:00 ET) limit at the ask plus 2.5%, at most 0.15" && shiftZ.Size() == "the whole position" && shiftX.Size() == "half the position",
        $"each says what it does ({alt1.Describe()}; {shiftZ.Describe()}; {shiftX.Describe()})");

    // IOC in the regular session, DAY outside it: IBKR ignores outsideRth on a SMART IOC (warning 2109, 11-18).
    // 2026-11-18 is a Wednesday in EST, so 9:30 ET is 14:30 UTC.
    DateTime Utc(int day, int h, int min) => new(2026, 11, day, h, min, 0, DateTimeKind.Utc);
    var rth = Utc(18, 15, 0);
    Check(OrderHotkeys.TifAt(alt1, Utc(18, 14, 30)) == "IOC" && OrderHotkeys.TifAt(alt1, rth) == "IOC" && OrderHotkeys.TifAt(alt1, Utc(18, 20, 59)) == "IOC",
        "IOC from 9:30 to just before 16:00 ET");
    Check(OrderHotkeys.TifAt(alt1, Utc(18, 14, 29)) == "DAY" && OrderHotkeys.TifAt(alt1, Utc(18, 21, 0)) == "DAY" && OrderHotkeys.TifAt(alt1, Utc(18, 9, 0)) == "DAY",
        "DAY premarket (4:00 ET as well as 9:29) and from 16:00 after hours");
    Check(OrderHotkeys.TifAt(alt1, Utc(21, 15, 0)) == "DAY", "DAY all weekend, so nothing sits there as an IOC that can't fill");
    Check(OrderHotkeys.TifAt(alt1 with { Tif = "GTC" }, Utc(18, 9, 0)) == "GTC", "a key that isn't IOC keeps its own time in force");
    Check(OrderHotkeys.LimitPrice(0.8804, 0.025) == 0.9024 && OrderHotkeys.LimitPrice(4.00, 0.025) == 4.10 && OrderHotkeys.LimitPrice(6.00, 0.025) == 6.15,
        "the limit is the ask plus 2.5%: 0.8804 makes 0.9024, 4.00 makes 4.10, 6.00 makes 6.15");
    Check(OrderHotkeys.LimitPrice(10, 0.025) == 10.15 && OrderHotkeys.LimitPrice(100, 0.025) == 100.15 && OrderHotkeys.LimitPrice(100, -0.025) == 99.85,
        "but never more than 15 cents through: 10 makes 10.15, 100 makes 100.15 to buy and 99.85 to sell");
    Check(OrderHotkeys.LimitPrice(0.99995, 0.025) == 1.02 && OrderHotkeys.LimitPrice(0.123456, 0.025) == 0.1265 && OrderHotkeys.LimitPrice(4.00, -0.025) == 3.90,
        "rounded to IBKR's tick: a cent from a dollar, a hundredth of a cent below; and minus for a sell");
    Check(OrderHotkeys.LimitPrice(0.2547, 0.025) == 0.2611 && OrderHotkeys.LimitPrice(0.2547, 0.025) < 0.2752686,
        "VIVS's ask of 0.2547 makes 0.2611, inside the 0.2752686 IBKR said was the most it would take (10 cents made 0.3547)");

    Check(OrderHotkeys.TryParseKeys("Alt+1", out uint m, out uint vk) && m == HotkeyListener.Alt && vk == '1', $"Alt+1 is Alt and the 1 key ({m:X}, {vk:X})");
    Check(OrderHotkeys.TryParseKeys("Shift+Z", out m, out vk) && m == HotkeyListener.Shift && vk == 'Z', "Shift+Z is Shift and the Z key");
    Check(OrderHotkeys.TryParseKeys("Ctrl+Shift+F2", out m, out vk) && m == (HotkeyListener.Control | HotkeyListener.Shift) && vk == 0x71, "Ctrl+Shift+F2 parses too");
    Check(!OrderHotkeys.TryParseKeys("1", out _, out _) && !OrderHotkeys.TryParseKeys("Alt+Ctrl", out _, out _) && !OrderHotkeys.TryParseKeys("Alt+1+2", out _, out _) && !OrderHotkeys.TryParseKeys("Alt+F13", out _, out _),
        "a key needs a modifier and exactly one known key");

    var svre = new HotkeyQuote("SVRE", 0.8803, 0.8804, false);
    var (ticket, quote, problem) = OrderHotkeys.Decide(alt1, [svre], rth);
    Check(problem == null && ticket == new IbkrOrderTicket("SVRE", true, 1000, "LMT", 0.9024, double.NaN, "IOC") && quote == 0.8804,
        $"Alt+1, one depth open: buy 1,000 SVRE, IOC limit 0.9024 ({ticket})");
    Check(OrderHotkeys.Decide(alt1, [svre], Utc(18, 12, 0)).Ticket == new IbkrOrderTicket("SVRE", true, 1000, "LMT", 0.9024, double.NaN, "DAY"),
        "the same press at 7:00 ET premarket sends a DAY limit");
    var alt2 = OrderHotkey.BuyTwoThousand;
    Check(alt2 is { Keys: "Alt+2", Buy: true, Shares: 2000, Offset: 0.025, Tif: "IOC", OfPosition: 0 }, "Alt+2 buys 2,000 shares at the ask plus 2.5%, immediate or cancel");
    Check(OrderHotkeys.TryParseKeys("Alt+2", out m, out vk) && m == HotkeyListener.Alt && vk == '2', "Alt+2 is Alt and the 2 key");
    Check(OrderHotkeys.Decide(alt2, [svre], rth).Ticket == new IbkrOrderTicket("SVRE", true, 2000, "LMT", 0.9024, double.NaN, "IOC"),
        "Alt+2, one depth open: buy 2,000 SVRE, IOC limit 0.9024");
    var alt0 = OrderHotkey.BuyOne;
    Check(alt0 is { Keys: "Alt+0", Buy: true, Shares: 1, Offset: 0.025, Tif: "IOC", OfPosition: 0 }, "Alt+0 buys 1 share at the ask plus 2.5%, immediate or cancel");
    Check(OrderHotkeys.TryParseKeys("Alt+0", out m, out vk) && m == HotkeyListener.Alt && vk == '0', "Alt+0 is Alt and the 0 key");
    Check(OrderHotkeys.Decide(alt0, [svre], rth).Ticket == new IbkrOrderTicket("SVRE", true, 1, "LMT", 0.9024, double.NaN, "IOC")
        && OrderHotkeys.Decide(alt0, [svre], Utc(18, 12, 0)).Ticket?.Tif == "DAY",
        "Alt+0, one depth open: buy 1 SVRE, IOC limit 0.9024, DAY premarket");
    var withAlt0 = new List<(uint, uint)> { (HotkeyListener.Alt, '1'), (HotkeyListener.Alt, '2'), (HotkeyListener.Alt, '0') };
    Check(OrderHotkeys.OnKeyDown(withAlt0, '0', HotkeyListener.Alt, false, true, true) == (2, true) && OrderHotkeys.OnKeyDown(withAlt0, '0', 0, false, true, false) == (-1, false),
        "Alt+0 fires as its own key, even with a text cursor; 0 alone types a 0");
    Check(OrderHotkeys.Decide(alt1, []).Problem?.Contains("no market depth") == true, "no depth open: nothing, and says so");
    var two = OrderHotkeys.Decide(alt1, [svre, new HotkeyQuote("CRMN", 1.20, 1.21, false)]);
    Check(two.Ticket == null && two.Problem.Contains("SVRE, CRMN"), $"two depths open: nothing, naming them ({two.Problem})");
    Check(OrderHotkeys.Decide(alt1, [svre with { Halted = true }]).Problem?.Contains("halted") == true, "a halted stock: nothing");

    // A stock IBKR won't let you open (IbkrRestrictions): a buy sends nothing and says why; a closing order still goes.
    const string compliance = "No Opening Trades: Small Cap, Subject to Compliance Restriction";
    var zbao = new HotkeyQuote("ZBAO", 0.1517, 0.1518, false);
    Func<string, string> restricted = s => s == "ZBAO" ? compliance : null;
    var blocked = OrderHotkeys.Decide(alt1, [zbao], rth, restricted);
    Check(blocked.Ticket == null && blocked.Problem == $"IBKR won't let you open a position in ZBAO ({compliance})", $"Alt+1 on restricted ZBAO, flat: nothing, with IBKR's reason ({blocked.Problem})");
    Check(OrderHotkeys.Decide(alt1, [zbao with { Position = 1000 }], rth, restricted).Ticket == null, "adding to a long in it: nothing either");
    Check(OrderHotkeys.Decide(alt1, [zbao with { Position = -1000 }], rth, restricted).Ticket is { Buy: true, Quantity: 1000 }, "a buy that covers a short goes");
    Check(OrderHotkeys.Decide(shiftZ, [zbao with { Position = 1000 }], rth, restricted).Ticket is { Buy: false, Quantity: 1000 }, "Shift+Z selling a long in it goes");
    Check(OrderHotkeys.Decide(alt1, [svre], rth, restricted).Ticket != null && OrderHotkeys.Decide(alt1, [zbao], rth).Ticket != null,
        "a stock without a reason, or no lookup at all, sends as before");
    Check(OrderHotkeys.Decide(alt1, [svre with { Ask = -1 }]).Ticket == null && OrderHotkeys.Decide(alt1, [svre with { Ask = double.NaN }]).Ticket == null,
        "no IBKR ask (a side with no market, or IBKR not quoting): nothing");

    var held = svre with { Position = 1500 };
    var all = OrderHotkeys.Decide(shiftZ, [held], rth);
    Check(all.Problem == null && all.Ticket == new IbkrOrderTicket("SVRE", false, 1500, "LMT", 0.8583, double.NaN, "IOC") && all.Quote == 0.8803,
        $"Shift+Z holding 1,500: sell 1,500 SVRE, IOC limit at the bid 0.8803 minus 2.5% ({all.Ticket})");
    Check(OrderHotkeys.Decide(shiftZ, [held with { WorkingSells = 500 }]).Ticket?.Quantity == 1000, "shares already in working sells are left out");
    Check(OrderHotkeys.Decide(shiftZ, [held with { WorkingSells = 1500 }]).Problem?.Contains("already in working sell orders") == true,
        "with the whole position already in working sells: nothing, so a second press can't go short");
    Check(OrderHotkeys.Decide(shiftZ, [svre]).Problem?.Contains("no long position") == true && OrderHotkeys.Decide(shiftZ, [svre with { Position = -300 }]).Problem?.Contains("no long position") == true,
        "flat or short: nothing to sell");
    Check(OrderHotkeys.Decide(shiftZ, [held with { WorkingBuys = 1000 }]).Ticket?.Quantity == 1500, "working buys don't change what's sold");
    Check(OrderHotkeys.Decide(shiftZ, [held with { Bid = -1 }]).Problem?.Contains("no IBKR bid") == true && OrderHotkeys.Decide(shiftZ, [held with { Halted = true }]).Ticket == null,
        "no bid, or halted: nothing");
    Check(OrderHotkeys.Decide(shiftZ, [held with { Bid = 0.08 }]).Ticket?.LimitPrice == 0.078, "a bid of 8 cents sells at 0.078: a share off never takes a limit to 0, as 10 cents off did");

    var half = OrderHotkeys.Decide(shiftX, [held], rth);
    Check(OrderHotkeys.Decide(shiftX, [held], Utc(18, 22, 0)).Ticket?.Tif == "DAY", "Shift+X after hours sends DAY too");
    Check(half.Problem == null && half.Ticket == new IbkrOrderTicket("SVRE", false, 750, "LMT", 0.8583, double.NaN, "IOC"),
        $"Shift+X holding 1,500: sell 750 SVRE, IOC limit at the bid minus 2.5% ({half.Ticket})");
    Check(OrderHotkeys.Decide(shiftX, [svre with { Position = 1001 }]).Ticket?.Quantity == 500, "half of an odd position rounds down: 1,001 sells 500");
    Check(OrderHotkeys.Decide(shiftX, [held with { WorkingSells = 1000 }]).Ticket?.Quantity == 500,
        "never more than the shares not already being sold: 1,500 held with 1,000 in working sells sells 500");
    Check(OrderHotkeys.Decide(shiftX, [held with { WorkingSells = 500 }]).Ticket?.Quantity == 750, "with enough left free, still half");
    Check(OrderHotkeys.Decide(shiftX, [held with { WorkingSells = 1500 }]).Problem?.Contains("already in working sell orders") == true, "with it all being sold already: nothing");
    Check(OrderHotkeys.Decide(shiftX, [svre with { Position = 1 }]).Problem?.Contains("under one share") == true, "half of 1 share is under a share: nothing");
    Check(OrderHotkeys.Decide(shiftX, [svre]).Problem?.Contains("no long position") == true, "flat: nothing to sell");

    // The price Quantower's order history and chart markers show: the limit while working, the fill once done.
    var akan = new IbkrOrder(54, "DU1", "AKAN", true, 1000, "LMT", 3.34, double.NaN, "IOC", true, "PreSubmitted", 360, 3.24, DateTime.UtcNow);
    Check(akan.ShownPrice == 3.34, "a working order shows its limit, even part filled");
    Check((akan with { Status = "Filled", Filled = 1000 }).ShownPrice == 3.24, "a filled one shows its average fill: AKAN's 3.34 limit filled at 3.24 (order 54)");
    Check((akan with { Status = "Cancelled", Filled = 433, AvgFillPrice = 3.2476 }).ShownPrice == 3.2476, "an IOC cancelled part filled shows its fills' average");
    Check((akan with { Status = "Cancelled", Filled = 0, AvgFillPrice = 0 }).ShownPrice == 3.34, "one cancelled with no fills keeps its limit");
    var market = akan with { Type = "MKT", LimitPrice = double.NaN, Filled = 0, AvgFillPrice = 0 };
    Check(double.IsNaN(market.ShownPrice) && (market with { Status = "Filled", Filled = 1000, AvgFillPrice = 3.25 }).ShownPrice == 3.25,
        "a market order: no price while working, its fill once filled");

    // What a key-down does, for Alt+1 (0), Shift+Z (1) and Shift+X (2).
    var keys = new List<(uint, uint)> { (HotkeyListener.Alt, '1'), (HotkeyListener.Shift, 'Z'), (HotkeyListener.Shift, 'X') };
    Check(OrderHotkeys.TryParseKeys("Shift+X", out m, out vk) && m == HotkeyListener.Shift && vk == 'X', "Shift+X is Shift and the X key");
    Check(OrderHotkeys.OnKeyDown(keys, 'X', HotkeyListener.Shift, false, true, false) == (2, true) && OrderHotkeys.OnKeyDown(keys, 'X', HotkeyListener.Shift, false, true, true) == (-1, false),
        "Shift+X fires with Quantower in front, and types an X while a text box has the cursor");
    var withAlt2 = new List<(uint, uint)>(keys) { (HotkeyListener.Alt, '2') };
    Check(OrderHotkeys.OnKeyDown(withAlt2, '2', HotkeyListener.Alt, false, true, true) == (3, true) && OrderHotkeys.OnKeyDown(withAlt2, '1', HotkeyListener.Alt, false, true, false) == (0, true),
        "Alt+2 fires as its own key, even with a text cursor, and Alt+1 still fires as Alt+1");
    Check(OrderHotkeys.OnKeyDown(keys, 'Z', HotkeyListener.Shift, false, true, false) == (1, true), "Shift+Z with Quantower in front fires, and Quantower doesn't get the Z");
    Check(OrderHotkeys.OnKeyDown(keys, 'Z', HotkeyListener.Shift, false, false, false) == (-1, false), "with another program in front, it's just a capital Z there");
    Check(OrderHotkeys.OnKeyDown(keys, 'Z', HotkeyListener.Shift, false, true, true) == (-1, false), "with a text cursor in Quantower, it types the Z");
    Check(OrderHotkeys.OnKeyDown(keys, '1', HotkeyListener.Alt, false, true, true) == (0, true), "Alt+1 types nothing, so it fires even with a text cursor");
    Check(OrderHotkeys.OnKeyDown(keys, 'Z', HotkeyListener.Shift, true, true, false) == (-1, true), "held down, the repeats send nothing more");
    Check(OrderHotkeys.OnKeyDown(keys, 'Z', 0, false, true, false) == (-1, false) && OrderHotkeys.OnKeyDown(keys, 'Z', HotkeyListener.Shift | HotkeyListener.Alt, false, true, false) == (-1, false),
        "Z alone (or with Caps Lock), or Shift+Alt+Z, isn't Shift+Z");

    // The listener, with the real keyboard hook in, and presses posted to its thread as stand-ins.
    var listener = new HotkeyListener();
    bool front = true, typing = false;
    listener.InFront = () => front;
    listener.Typing = () => typing;
    FeedAlarm.TestPlayer = _ => { };
    var pressed = new System.Collections.Concurrent.ConcurrentQueue<OrderHotkey>();
    Action<OrderHotkey> onPress = pressed.Enqueue;
    const uint F12 = 0x7B;
    const uint AllThree = HotkeyListener.Control | HotkeyListener.Alt | HotkeyListener.Shift;
    var rare = new OrderHotkey("Ctrl+Alt+Shift+F12", true, 1, 0.01, "IOC");
    var rareType = new OrderHotkey("Shift+Q", false, 0, 0.01, "IOC", OfPosition: 1);
    listener.Start([rare, rareType], onPress);
    Check(listener.Hooked, "the keyboard hook goes in");
    listener.Press(F12, AllThree);
    await Task.Delay(300);
    Check(pressed.Count == 1 && pressed.First() == rare, "a press with Quantower in front reaches the handler");
    front = false;
    listener.Press(F12, AllThree);
    await Task.Delay(300);
    Check(pressed.Count == 1, "with another program in front, nothing");
    front = true;
    typing = true;
    listener.Press('Q', HotkeyListener.Shift);
    await Task.Delay(300);
    Check(pressed.Count == 1, "a key that types, with a text cursor, types instead");
    typing = false;
    listener.Press('Q', HotkeyListener.Shift);
    await Task.Delay(300);
    Check(pressed.Count == 2 && pressed.Last() == rareType, "and without one, it fires");
    listener.Release(onPress);
    listener.Press(F12, AllThree);
    await Task.Delay(300);
    Check(pressed.Count == 2, "after the connection lets go, nothing (and a warning sound)");
    listener.Start([rare, rareType], onPress);
    listener.Press(F12, AllThree);
    await Task.Delay(300);
    Check(pressed.Count == 3, "a reconnect's new handler takes over without hooking again");
    listener.Stop();
}

// The slow-update diagnostics: off by default, and slow work matched against garbage collection pauses.
async Task RunStallWatch()
{
    long msTicks = Stopwatch.Frequency / 1000;
    StallWatch.Note("before start", Stopwatch.GetTimestamp() - (50 * msTicks));
    Check(!StallWatch.On && StallWatch.TakeSummary() == null, "off until started, so the hot path only checks a flag");

    StallWatch.Start();
    StallWatch.TakeSummary();
    StallWatch.Note("quick", Stopwatch.GetTimestamp() - msTicks);
    StallWatch.Note("in pause", Stopwatch.GetTimestamp() - (16 * msTicks));
    long utc = DateTime.UtcNow.Ticks;
    StallWatch.AddPause(utc - TimeSpan.FromMilliseconds(18).Ticks, utc - TimeSpan.FromMilliseconds(1).Ticks, 0, TimeSpan.FromMilliseconds(15).Ticks);
    await Task.Delay(200);
    StallWatch.Note("outside", Stopwatch.GetTimestamp() - (12 * msTicks));
    Console.WriteLine("  waiting out the 5 s settle");
    await Task.Delay(5300);
    var summary = StallWatch.TakeSummary();
    Console.WriteLine($"  {summary}");
    Check(summary.Contains("slow(>=5ms) 2 ") && !summary.Contains("quick") && !summary.Contains("before_start"), "only work over 5 ms counts, and only once started");
    Check(summary.Contains("during_gc_pause=1") && summary.Contains("outside=[outside 12."), "slow work during a pause and outside one are told apart");

    // The live listener: a forced collection shows up as a pause from the runtime's own events.
    StallWatch.TakeSummary();
    GC.Collect(2, GCCollectionMode.Forced, blocking: true);
    var heard = Stopwatch.StartNew();
    string after = null;
    long pausesHeard = 0;
    while (heard.Elapsed < TimeSpan.FromSeconds(4) && pausesHeard == 0)
    {
        await Task.Delay(250);
        after = StallWatch.TakeSummary();
        var m = System.Text.RegularExpressions.Regex.Match(after, @"pausesHeard=(\d+)");
        pausesHeard += m.Success ? long.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) : 0;
    }

    Console.WriteLine($"  {after}");
    Check(pausesHeard >= 1, $"a real collection's pause is heard from the runtime's events ({pausesHeard}, {heard.ElapsedMilliseconds} ms after)");
    var (median, max) = StallWatch.MeasureSleep1();
    Console.WriteLine($"  Sleep(1) in this process: median {median:F2} ms, max {max:F2} ms");
}

// The clock the plugin stamps with: Quantower's corrected one when set, the PC's otherwise or if it fails.
async Task RunFeedClock()
{
    double Ahead() => FeedClock.AheadOfPcMs;
    try
    {
        FeedClock.Use(null);
        Check(Math.Abs(Ahead()) < 20, $"unset, it's the PC clock ({Ahead():F1} ms)");

        FeedClock.Use(() => DateTime.UtcNow.AddMilliseconds(650));
        Check(Math.Abs(Ahead() - 650) < 20, $"set, it reads the other clock: Quantower's 650 ms correction of 04:14 ({Ahead():F1} ms)");
        Check(FeedClock.UtcNow.Kind == DateTimeKind.Utc, "its DateTime is marked UTC");

        FeedClock.Use(() => throw new InvalidOperationException("not ready"));
        Check(Math.Abs(Ahead()) < 20, "a clock that throws falls back to the PC's");

        FeedClock.Use(() => default);
        Check(Math.Abs(Ahead()) < 20, "a clock that isn't set yet (year 1) falls back to the PC's");
        FeedClock.Use(() => DateTime.UtcNow.AddHours(1));
        Check(Math.Abs(Ahead()) < 20, "so does one an hour out, far beyond any real correction");

        // An IBKR print's stamp comes from this clock, so Quantower sees it as just arrived.
        FeedClock.Use(() => DateTime.UtcNow.AddMilliseconds(650));
        using var gateway = new FakeGateway();
        var sink = new IbkrTestSink();
        var client = new IbkrClient(new IbkrConfig(true, "127.0.0.1", gateway.Port, 1, false));
        client.SetSink(sink);
        client.Start();
        client.SetTape("STAMP", true);
        var timer = Stopwatch.StartNew();
        while (timer.Elapsed < TimeSpan.FromSeconds(5) && gateway.TapeRequestId == 0)
            await Task.Delay(20);
        gateway.SendPrint("");
        while (timer.Elapsed < TimeSpan.FromSeconds(5) && sink.Prints.GetValueOrDefault("STAMP") is not { Count: > 0 })
            await Task.Delay(10);
        await client.StopAsync();
        var stamped = sink.Prints.GetValueOrDefault("STAMP")?.FirstOrDefault();
        double aheadMs = stamped is { } p ? (p.ArrivalTicks - DateTime.UtcNow.Ticks) / (double)TimeSpan.TicksPerMillisecond : double.NaN;
        Check(aheadMs is > 550 and < 700, $"an IBKR print is stamped on the set clock ({aheadMs:F0} ms ahead of the PC's)");
    }
    finally
    {
        FeedClock.Use(null);
    }
}

// The rules of the merged tape, with no feed involved.
void RunTapeMerge()
{
    long second = TimeSpan.TicksPerSecond;
    long t = 1000 * second;

    var m = new TapeMerge();
    Check(m.OnTastytradePrint(2.38, 100, 500, t) && !m.OnIbkrReportedPrint(2.38, 100, 500, t + second), "a FINRA print tastytrade already showed doesn't show again");
    Check(m.OnIbkrReportedPrint(2.40, 100, 501, t) && !m.OnTastytradePrint(2.40, 100, 501, t + second), "whichever feed brings it first shows it");
    Check(m.OnIbkrReportedPrint(2.41, 300, 501, t) && m.OnIbkrReportedPrint(2.41, 300, 501, t), "a FINRA print tastytrade doesn't have shows, twice if it traded twice");

    m = new TapeMerge();
    m.OnTastytradePrint(2.38, 100, 500, t);
    m.OnTastytradePrint(2.38, 100, 500, t);
    Check(!m.OnIbkrReportedPrint(2.38, 100, 500, t) && !m.OnIbkrReportedPrint(2.38, 100, 500, t), "two identical prints match two");
    Check(m.OnIbkrReportedPrint(2.38, 100, 500, t), "a third identical print is a different trade");

    m = new TapeMerge();
    m.OnTastytradePrint(2.38, 100, 500, t);
    m.OnIbkrCarriedPrint(2.38, 100, 500, t);
    Check(m.OnIbkrReportedPrint(2.38, 100, 500, t), "a Nasdaq print uses up its partner, so a FINRA print can't match it");

    m = new TapeMerge();
    m.OnTastytradePrint(0.0591, 10731, 500, t);
    Check(!m.OnIbkrReportedPrint(0.059, 10731, 500, t), "a sub-penny price rounded differently still matches (0.0591 and 0.059)");
    m.OnTastytradePrint(0.0595, 100, 500, t);
    Check(m.OnIbkrReportedPrint(0.0592, 100, 500, t), "a different price doesn't match");
    m.OnTastytradePrint(5.25, 100, 500, t);
    Check(m.OnIbkrReportedPrint(5.25, 101, 500, t), "a different size doesn't match");
    m.OnTastytradePrint(7.25, 735.378, 500, t);
    Check(!m.OnIbkrReportedPrint(7.25, 735, 500, t), "a fractional size matches its whole shares");
    m.OnTastytradePrint(735.4799, 200, 500, t);
    Check(!m.OnIbkrReportedPrint(735.48, 200, 500, t), "a price IBKR rounded to the cent still matches");
    m.OnTastytradePrint(735.465, 89, 500, t);
    Check(!m.OnIbkrReportedPrint(735.47, 89, 500, t), "so does one rounded from exactly half a cent");
    m.OnTastytradePrint(0.0519, 500, 500, t);
    Check(m.OnIbkrReportedPrint(0.05, 500, 500, t), "under a dollar, being within a cent isn't a match");
    m.OnIbkrCarriedPrint(3.25, 150, 500, t);
    Check(m.OnTastytradePrint(3.25, 150, 500, t) && m.OnIbkrReportedPrint(3.25, 150, 500, t), "a Nasdaq print IBKR brought first can't be taken for a FINRA one when tastytrade's copy comes");
    Check(m.OnIbkrReportedPrint(4.25, 100, 500, t) && m.OnTastytradePrint(4.25, 100, 502, t + (2 * second)), "a later trade at the same price and size on tastytrade isn't taken for an earlier FINRA print");
    m.OnTastytradePrint(735.46, 300, 500, t);
    Check(m.OnIbkrReportedPrint(735.47, 300, 500, t), "a different cent doesn't match");
    Check(m.OnTastytradePrint(190.3988, 0.0677, 500, t), "a print under one share has no partner, and shows");

    m = new TapeMerge();
    m.OnTastytradePrint(2.38, 100, 500, t);
    Check(m.OnIbkrReportedPrint(2.38, 100, 504, t), "4 seconds apart is a different trade");
    Check(!m.OnIbkrReportedPrint(2.38, 100, 503, t), "3 seconds apart is the same trade");
    m.OnTastytradePrint(2.38, 100, 500, t);
    Check(m.OnIbkrReportedPrint(2.38, 100, 500, t + (7 * second)), "nothing is held on to past its time");
}

// The real merge class over two recordings, in the order they arrived on this PC.
void RunMergeReplay(string tapePath, string capturePath)
{
    var ibkr = File.ReadLines(tapePath).Select(l => l.Split('\t')).Where(p => p.Length == 8 && p[2] == "SMART")
        .Select(p => (Arrival: double.Parse(p[0], CultureInfo.InvariantCulture), Symbol: p[1], Seconds: (long)double.Parse(p[3], CultureInfo.InvariantCulture), Price: double.Parse(p[4], CultureInfo.InvariantCulture), Size: double.Parse(p[5], CultureInfo.InvariantCulture), Venue: p[6]))
        .ToList();

    var layouts = new Dictionary<(int, string), string[]>();
    var tasty = new List<(double Arrival, string Symbol, long Seconds, double Price, double Size)>();
    foreach (var line in File.ReadLines(capturePath))
    {
        var parts = line.Split('\t', 2);
        if (parts.Length != 2 || !parts[1].Contains("\"type\":\"FEED_", StringComparison.Ordinal))
            continue;

        JsonNode message;
        try { message = JsonNode.Parse(parts[1]); } catch (JsonException) { continue; }
        if ((string)message["type"] == "FEED_CONFIG" && message["eventFields"] is JsonObject fields)
        {
            foreach (var (name, list) in fields)
                layouts[((int)message["channel"], name)] = list.AsArray().Select(x => (string)x).ToArray();
        }
        else if ((string)message["type"] == "FEED_DATA" && (int)message["channel"] == 1)
        {
            var data = message["data"].AsArray();
            for (int i = 0; i + 1 < data.Count; i += 2)
            {
                string name = (string)data[i];
                if (name != "TimeAndSale" || !layouts.TryGetValue((1, name), out var row))
                    continue;

                var values = data[i + 1].AsArray();
                for (int j = 0; j + row.Length <= values.Count; j += row.Length)
                {
                    int Field(string f) => j + Array.IndexOf(row, f);
                    if (((int?)values[Field("eventFlags")] ?? 0) % 4 >= 2)
                        continue;
                    tasty.Add((double.Parse(parts[0], CultureInfo.InvariantCulture), (string)values[Field("eventSymbol")], (long)values[Field("time")] / 1000, (double)values[Field("price")], (double)values[Field("size")]));
                }
            }
        }
    }

    foreach (var symbol in ibkr.Select(r => r.Symbol).Distinct().OrderBy(s => s, StringComparer.Ordinal))
    {
        var i = ibkr.Where(r => r.Symbol == symbol).ToList();
        var a = tasty.Where(r => r.Symbol == symbol).ToList();
        if (a.Count == 0)
            continue;

        // Well inside both recordings, so no print loses its partner to the edge.
        long begin = Math.Max(i.Min(r => r.Seconds), a.Min(r => r.Seconds)) + 10;
        long end = Math.Min(i.Max(r => r.Seconds), a.Max(r => r.Seconds)) - 10;
        var events = new List<(double Arrival, bool FromIbkr, long Seconds, double Price, double Size, string Venue)>();
        events.AddRange(i.Where(r => r.Seconds >= begin && r.Seconds <= end).Select(r => (r.Arrival, true, r.Seconds, r.Price, r.Size, r.Venue)));
        events.AddRange(a.Where(r => r.Seconds >= begin && r.Seconds <= end).Select(r => (r.Arrival, false, r.Seconds, r.Price, r.Size, "")));
        events.Sort((x, y) => x.Arrival.CompareTo(y.Arrival));

        var merge = new TapeMerge();
        double shownTasty = 0, shownOther = 0, shownFinra = 0, tape = 0, droppedTasty = 0;
        int finraMatched = 0, finraShown = 0;

        // The running difference by print second. A real loss stays in it; one print's two copies
        // landing either side of the window's edge shows only in the last few seconds.
        var gapBySecond = new SortedDictionary<long, double>();
        void Gap(long seconds, double shares) => gapBySecond[seconds] = gapBySecond.GetValueOrDefault(seconds) + shares;
        foreach (var e in events)
        {
            long ticks = (long)(e.Arrival * TimeSpan.TicksPerMillisecond);
            if (!e.FromIbkr)
            {
                if (merge.OnTastytradePrint(e.Price, e.Size, e.Seconds, ticks))
                {
                    shownTasty += e.Size;
                    Gap(e.Seconds, e.Size);
                }
                else
                {
                    droppedTasty += e.Size;
                }

                continue;
            }

            tape += e.Size;
            Gap(e.Seconds, -e.Size);
            if (PrintFilter.IsCarriedByTastytrade(Encoding.ASCII.GetBytes(e.Venue)))
            {
                merge.OnIbkrCarriedPrint(e.Price, e.Size, e.Seconds, ticks);
            }
            else if (e.Venue == PrintFilter.ReportedVenue)
            {
                if (merge.OnIbkrReportedPrint(e.Price, e.Size, e.Seconds, ticks))
                {
                    shownFinra += e.Size;
                    finraShown++;
                    Gap(e.Seconds, e.Size);
                }
                else
                {
                    finraMatched++;
                }
            }
            else
            {
                shownOther += e.Size;
                Gap(e.Seconds, e.Size);
            }
        }

        double running = 0, biggest = 0, atFiveBefore = 0, atFirstTen = 0;
        foreach (var (second, gap) in gapBySecond)
        {
            running += gap;
            if (Math.Abs(running) > Math.Abs(biggest))
                biggest = running;
            if (second <= begin + 10)
                atFirstTen = running;
            if (second <= end - 5)
                atFiveBefore = running;
        }

        Console.WriteLine($"  running merged-tape: after the first 10 s {atFirstTen:F0}, 5 s before the end {atFiveBefore:F0}, at the end {running:F0}, furthest it got {biggest:F0}");

        double total = shownTasty + shownOther + shownFinra;
        Console.WriteLine($"{symbol}: {end - begin} s; merged {total:F0} shares, IBKR's tape {tape:F0}, merged/tape {total / tape:P3}");
        Console.WriteLine($"  from tastytrade {shownTasty:F0}, other exchanges {shownOther:F0}, FINRA added {shownFinra:F0} ({finraShown} prints), FINRA already shown {finraMatched} prints, tastytrade prints IBKR had shown first {droppedTasty:F0} shares");
    }
}

// Bars against a stand-in for Gateway, so they take no real requests.
async Task RunIbkrBars()
{
    Check(IbkrClient.AllowedRequests.SetEquals(new[] { 71, 59, 1, 2, 10, 11, 97, 98, 20, 25 }), "the whitelist is exactly the market-data list plus history");
    Check(!IbkrClient.AllowedRequests.Contains(3) && !IbkrClient.AllowedRequests.Contains(4), "order messages (place 3, cancel 4) aren't sendable");

    Check(IbkrBars.BarSize(60) == "1 min" && IbkrBars.BarSize(10) == "10 secs" && IbkrBars.BarSize(14400) == null, "bar lengths map to IBKR's names, and unsupported ones to none");
    Check(IbkrBars.Duration(3599) == "3600 S" && IbkrBars.Duration(86400) == "86400 S" && IbkrBars.Duration(86401) == "2 D", $"durations are whole minutes, then whole days ({IbkrBars.Duration(3599)}, {IbkrBars.Duration(86401)})");
    var noon = new DateTime(2026, 11, 6, 12, 0, 0, DateTimeKind.Utc);
    Check(IbkrBars.EndTime(noon, noon.AddSeconds(5)) == "" && IbkrBars.EndTime(noon, noon.AddHours(1)) == "20261106-12:00:00", "a span ending now has a blank end, an older one a UTC time");

    BarRow Bar(long seconds, double volume) => new(seconds * 1000, 1, 2, 0.5, 1.5, volume, 3);
    var merged = IbkrBars.Overlay([Bar(0, 10), Bar(60, 20), Bar(240, 7)], [Bar(60, 90), Bar(120, 50), Bar(180, 0), Bar(240, 0)]);
    Check(merged.Select(b => b.TimeMs / 1000).SequenceEqual(new long[] { 0, 60, 120 }), "IBKR's bars join tastytrade's in time order; its empty ones don't, and clear tastytrade's");
    Check(merged[0].Volume == 10 && merged[1].Volume == 90, "IBKR's bar replaces tastytrade's, and tastytrade's stays where IBKR has none");

    using var gateway = new FakeGateway();
    var client = new IbkrClient(new IbkrConfig(true, "127.0.0.1", gateway.Port, 1, false));
    Check((await client.LoadBarsAsync("EARLY", "1 min", "60 S", "", CancellationToken.None)).Count == 0 && gateway.HistoryRequests.Count == 0, "before it's live, nothing is asked");

    client.SetSink(new IbkrTestSink());
    client.Start();
    var timer = Stopwatch.StartNew();
    while (client.State != IbkrState.Live && timer.Elapsed < TimeSpan.FromSeconds(10))
        await Task.Delay(20);

    gateway.HistoryPolicy["GOOD"] = (FakeGateway.Answer.Row, "1793960400\01.5\01.75\01.25\01.625\012300\01.6\041\01793960460\01.625\01.8\01.6\01.7\00\01.7\00\0", 2);
    var bars = await client.LoadBarsAsync("GOOD", "1 min", "3600 S", "", CancellationToken.None);
    Check(bars.Count == 2 && bars[0].TimeMs == 1793960400000 && bars[0].High == 1.75 && bars[0].Close == 1.625 && bars[0].Volume == 12300 && bars[0].Count == 41 && bars[1].Volume == 0, "bars are read field by field");
    var sent = gateway.HistoryRequests[0];
    Check(sent[16] == "1 min" && sent[17] == "3600 S" && sent[15] == "" && sent[18] == "0" && sent[19] == "TRADES" && sent[20] == "2" && sent[21] == "0", "asks for all hours, TRADES, epoch times, not kept up to date");

    int before = gateway.HistoryRequests.Count;
    var again = await client.LoadBarsAsync("GOOD", "1 min", "3600 S", "", CancellationToken.None);
    Check(again.Count == 2 && gateway.HistoryRequests.Count == before, "an identical request within the window isn't sent again");

    timer.Restart();
    gateway.HistoryPolicy["NEXT"] = (FakeGateway.Answer.Row, "1793960400\01.5\01.75\01.25\01.625\0100\01.6\01\0", 1);
    await client.LoadBarsAsync("GOOD", "5 mins", "3600 S", "", CancellationToken.None);
    await client.LoadBarsAsync("NEXT", "1 min", "3600 S", "", CancellationToken.None);
    Check(timer.ElapsedMilliseconds >= 350, $"different requests are spaced ({timer.ElapsedMilliseconds} ms for the second to go)");

    gateway.HistoryPolicy["BAD"] = (FakeGateway.Answer.Refused, "", 0);
    timer.Restart();
    before = gateway.HistoryRequests.Count;
    var refused = await client.LoadBarsAsync("BAD", "1 min", "3600 S", "", CancellationToken.None);
    Check(refused.Count == 0 && timer.Elapsed < TimeSpan.FromSeconds(2), $"a refusal comes back empty, straight away ({timer.ElapsedMilliseconds} ms)");
    await client.LoadBarsAsync("BAD", "1 min", "3600 S", "", CancellationToken.None);
    Check(gateway.HistoryRequests.Count == before + 2, "a refusal isn't remembered as an answer");

    gateway.HistoryPolicy["SILENT"] = (FakeGateway.Answer.Silent, "", 0);
    using (var closing = new CancellationTokenSource(600))
    {
        var closed = await client.LoadBarsAsync("SILENT", "1 min", "3600 S", "", closing.Token);
        await Task.Delay(100);
        Check(closed.Count == 0 && gateway.HistoryCancels.Count == 1, "a chart closing cancels its request, with no error");
    }

    timer.Restart();
    var waited = await client.LoadBarsAsync("SILENT", "1 min", "7200 S", "", CancellationToken.None);
    await Task.Delay(100);
    Check(waited.Count == 0 && timer.Elapsed < TimeSpan.FromSeconds(12) && gateway.HistoryCancels.Count == 2, $"no answer gives up, cancels, and comes back empty ({timer.Elapsed.TotalSeconds:F1} s)");

    var pending = client.LoadBarsAsync("SILENT", "1 min", "1800 S", "", CancellationToken.None);
    await Task.Delay(600);
    timer.Restart();
    client.ForceReconnect("harness");
    var dropped = await pending;
    Check(dropped.Count == 0 && timer.Elapsed < TimeSpan.FromSeconds(3), $"losing the connection comes back empty ({timer.ElapsedMilliseconds} ms)");

    await client.StopAsync();
}

// Real bars next to tastytrade's candles for every chart period: do their times line up, how fast, how much.
async Task RunIbkrBarsLive(int port, string symbol)
{
    var client = new IbkrClient(new IbkrConfig(true, "127.0.0.1", port, 88, IbkrConfig.Load().AllowLiveAccount));
    client.SetSink(new IbkrTestSink());
    client.Start();
    var timer = Stopwatch.StartNew();
    while (client.State != IbkrState.Live && timer.Elapsed < TimeSpan.FromSeconds(15))
        await Task.Delay(50);
    Check(client.State == IbkrState.Live, "live on the real Gateway");

    using var rest = api();
    var history = new HistoryClient(async (force, ct) => await rest.GetQuoteTokenAsync(ct));
    var cases = new[] { (1, "=1s"), (5, "=5s"), (10, "=10s"), (15, "=15s"), (30, "=30s"), (60, "=1m"), (300, "=5m"), (900, "=15m"), (1800, "=30m"), (3600, "=1h") };
    foreach (var (seconds, code) in cases)
    {
        long span = Math.Min(IbkrBars.MaxSeconds(seconds), 3 * 86400);
        var now = DateTime.UtcNow;
        var clock = Stopwatch.StartNew();
        var bars = await client.LoadBarsAsync(symbol, IbkrBars.BarSize(seconds), IbkrBars.Duration(span), "", CancellationToken.None);
        long ibkrMs = clock.ElapsedMilliseconds;
        var candles = await history.LoadAsync(symbol + "{" + code + "}", now.AddSeconds(-span), now, CancellationToken.None);
        var traded = bars.Where(b => b.Volume > 0).ToList();
        var times = candles.ToDictionary(c => c.TimeMs);
        var both = traded.Where(b => times.ContainsKey(b.TimeMs)).ToList();
        long firstCommon = both.Count > 0 ? both.Min(b => b.TimeMs) : 0;
        var ibkrTimes = traded.Select(b => b.TimeMs).ToHashSet();

        // IBKR has nothing from 20:00 to 04:00 ET, so only hours it covers can show bars out of line.
        bool Covered(long ms)
        {
            int hour = DateTimeOffset.FromUnixTimeMilliseconds(ms).ToOffset(TimeSpan.FromHours(-5)).Hour;
            return hour >= 4 && hour < 20;
        }

        int candlesInRange = candles.Count(c => c.TimeMs >= firstCommon && Covered(c.TimeMs));
        int candlesAligned = candles.Count(c => c.TimeMs >= firstCommon && Covered(c.TimeMs) && ibkrTimes.Contains(c.TimeMs));
        bool onTheClock = traded.All(b => b.TimeMs % (seconds * 1000L) == 0);
        double ibkrVolume = both.Sum(b => b.Volume);
        double candleVolume = both.Sum(b => times[b.TimeMs].Volume);
        Console.WriteLine($"{IbkrBars.BarSize(seconds),8} {IbkrBars.Duration(span),9}: IBKR {bars.Count} bars ({traded.Count} traded) in {ibkrMs} ms; candles {candles.Count}; candles in IBKR's hours on an IBKR bar's time {candlesAligned} of {candlesInRange}; same bars' volume IBKR {ibkrVolume:F0}, tastytrade {candleVolume:F0} ({(ibkrVolume > 0 ? candleVolume / ibkrVolume : double.NaN):P1})");

        // On 1-second bars the feeds can put a print in neighbouring seconds, which isn't bars out of line.
        Check(traded.Count > 0 && onTheClock && candlesAligned >= candlesInRange * (seconds == 1 ? 0.95 : 0.99), $"{IbkrBars.BarSize(seconds)} bars line up with tastytrade's");
        await Task.Delay(800);
    }

    await history.StopAsync();
    await client.StopAsync();
}

// The watchdog's clock, then the signal it relies on: any print counts, even one that stays hidden.
async Task RunTapeWatch()
{
    long second = TimeSpan.TicksPerSecond;
    var watch = new TapeWatch();
    long t = 1000 * second;

    Check(!watch.TakeWentQuiet(t + (60 * second)), "nothing expected, so no alarm however long the silence");
    watch.OnExpectedPrint(t);
    Check(!watch.TakeWentQuiet(t + (3 * second)), "no alarm at the limit");
    watch.OnExpectedPrint(t + (2 * second));
    Check(watch.TakeWentQuiet(t + (3 * second) + 1), "a later print doesn't push the limit back");
    Check(!watch.TakeWentQuiet(t + (4 * second)), "says so once per silence");
    watch.OnExpectedPrint(t + (5 * second));
    watch.OnIbkrPrint();
    Check(!watch.TakeWentQuiet(t + (60 * second)), "an IBKR print answers it");

    using var gateway = new FakeGateway();
    var sink = new IbkrTestSink();
    var client = new IbkrClient(new IbkrConfig(true, "127.0.0.1", gateway.Port, 1, false));
    client.SetSink(sink);
    client.Start();
    client.SetTape("TAPE", true);
    var timer = Stopwatch.StartNew();
    while (timer.Elapsed < TimeSpan.FromSeconds(5) && gateway.TapeRequestId == 0)
        await Task.Delay(20);
    Check(gateway.TapeRequestId != 0, "the tape is requested");

    gateway.SendPrint("");
    gateway.SendPrint("I");
    gateway.SendPrint("4");
    await Task.Delay(300);
    Check(sink.TapeActive.GetValueOrDefault("TAPE") == 3, $"every print signals activity ({sink.TapeActive.GetValueOrDefault("TAPE")})");
    Check(sink.Prints.GetValueOrDefault("TAPE")?.Count == 2, "only the ordinary ones reach the tape");

    await client.StopAsync();
}

void RunParse(string path)
{
    var fields = new Dictionary<string, string[]>();
    var sink = new CountingSink();
    var parser = new FeedFrameParser(fields);
    var dataFrames = new List<byte[]>();
    int expectedQuotes = 0, expectedPrints = 0, expectedTrades = 0, expectedCandles = 0, expectedSummaries = 0;
    var widths = new Dictionary<string, int>();

    foreach (var line in File.ReadLines(path))
    {
        var payload = line[(line.IndexOf('\t') + 1)..];
        using var doc = JsonDocument.Parse(payload);
        var root = doc.RootElement;
        var type = root.GetProperty("type").GetString();

        // The capture holds two feed channels with the same layouts, so one parser covers both.
        if (type == "FEED_CONFIG" && root.TryGetProperty("eventFields", out var ef))
        {
            foreach (var p in ef.EnumerateObject())
            {
                var columns = p.Value.EnumerateArray().Select(x => x.GetString()).ToArray();
                parser.UpdateLayout(p.Name, columns);
                widths[p.Name] = columns.Length;
            }
        }
        else if (type == "FEED_DATA")
        {
            dataFrames.Add(Encoding.UTF8.GetBytes(payload));
            var data = root.GetProperty("data");
            for (int i = 0; i + 1 < data.GetArrayLength(); i += 2)
            {
                var name = data[i].GetString();
                if (!widths.TryGetValue(name, out int width))
                    continue;
                int rows = data[i + 1].GetArrayLength() / width;
                switch (name)
                {
                    case "Quote": expectedQuotes += rows; break;
                    case "TimeAndSale": expectedPrints += rows; break;
                    case "Trade": expectedTrades += rows; break;
                    case "Candle": expectedCandles += rows; break;
                    case "Summary": expectedSummaries += rows; break;
                }
            }
        }
    }

    foreach (var frame in dataFrames)
        Check2(parser.TryParseData(frame, sink, DateTime.UtcNow.Ticks));

    Check(sink.Quotes == expectedQuotes, $"quotes parsed {sink.Quotes} = expected {expectedQuotes}");
    Check(sink.Prints == expectedPrints, $"prints parsed {sink.Prints} = expected {expectedPrints}");
    Check(sink.Trades == expectedTrades, $"trades parsed {sink.Trades} = expected {expectedTrades}");
    if (widths.TryGetValue("Trade", out _) && sink.Trades > 0)
        Console.WriteLine($"RESULT last trade: day volume {sink.LastDayVolume:#,0}, dollar volume {sink.LastDayTurnover:#,0}");
    Check(sink.Candles == expectedCandles, $"candles parsed {sink.Candles} = expected {expectedCandles}");
    Check(sink.Summaries == expectedSummaries, $"summaries parsed {sink.Summaries} = expected {expectedSummaries}");
    Check(sink.LastQuote.Bid > 0 && sink.LastQuote.Ask >= sink.LastQuote.Bid, $"last quote sane: {sink.LastQuote}");
    Check(sink.LastPrint.Price > 0 && sink.LastPrint.Time > 1_700_000_000_000, $"last print sane: {sink.LastPrint}");
    Check(sink.LastCandle.Time > 1_700_000_000_000, $"last candle sane: {sink.LastCandle}");
    Check(!parser.TryParseData("""{"type":"KEEPALIVE","channel":0}"""u8, sink, 0), "control frames are left to the slow path");

    // Nested rows, in case the server ever sends them instead of flat ones.
    var nested = new FeedFrameParser(new Dictionary<string, string[]> { ["Quote"] = ["eventType", "eventSymbol", "bidPrice", "askPrice"] });
    var nestedSink = new CountingSink();
    nested.TryParseData("""{"type":"FEED_DATA","channel":1,"data":["Quote",[["Quote","A",1.5,1.6],["Quote","B",2.5,"NaN"]]]}"""u8, nestedSink, 0);
    Check(nestedSink.Quotes == 2 && double.IsNaN(nestedSink.LastQuote.Ask) && nestedSink.LastQuote.Bid == 2.5, "nested rows and NaN handled");

    // Speed and allocation, on frames the parser has already seen.
    var quoteFrames = dataFrames.Where(f => Encoding.UTF8.GetString(f).Contains("\"Quote\",[")).ToList();
    var benchSink = new CountingSink();
    foreach (var f in quoteFrames)
        parser.TryParseData(f, benchSink, 0);
    benchSink = new CountingSink();
    long before = GC.GetAllocatedBytesForCurrentThread();
    var sw = Stopwatch.StartNew();
    const int rounds = 2000;
    for (int r = 0; r < rounds; r++)
    {
        foreach (var f in quoteFrames)
            parser.TryParseData(f, benchSink, 0);
    }

    sw.Stop();
    long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
    Console.WriteLine($"bench: {benchSink.Quotes} quotes, {sw.Elapsed.TotalMilliseconds * 1000 / benchSink.Quotes:F2} us each, {(double)allocated / benchSink.Quotes:F1} bytes allocated each");
    Check(allocated / Math.Max(benchSink.Quotes, 1) < 8, "no per-quote allocation in the parser");

    void Check2(bool parsed)
    {
        if (!parsed)
            failures++;
    }
}

async Task RunRest(string symbol, string search)
{
    using var client = api();
    var token = await client.GetQuoteTokenAsync(CancellationToken.None);
    Console.WriteLine($"quote token: level={token.Level} realtime={token.IsRealTime} url={token.Url}");

    var equity = await client.GetEquityAsync(symbol, CancellationToken.None);
    Console.WriteLine($"equity: {equity.Symbol} streamer={equity.StreamerSymbol} etf={equity.IsEtf} '{equity.Description}'");
    foreach (var band in equity.TickBands)
        Console.WriteLine($"  tick {band.Size} below {band.Below}");
    Check(equity.TickBands.Count == 2 && equity.TickBands[0].Size == 0.0001 && double.IsInfinity(equity.TickBands[1].Below), "tick bands parsed: $0.0001 under $1, $0.01 above");

    var hits = await client.SearchAsync(search, CancellationToken.None);
    Console.WriteLine($"search '{search}': {hits.Count} hits");
    foreach (var hit in hits.Take(6))
        Console.WriteLine($"  {hit.Symbol}  {hit.Description}");
    Check(hits.Count > 0, "search returned results");
    Check(hits.All(h => !h.Symbol.StartsWith('/')), "search excludes futures");

    var raw = await client.GetRawAsync("/symbols/search/" + Uri.EscapeDataString(search), CancellationToken.None);
    using var doc = JsonDocument.Parse(raw);
    var first = doc.RootElement.GetProperty("data").GetProperty("items")[0];
    Console.WriteLine($"search item fields: {string.Join(", ", first.EnumerateObject().Select(p => p.Name))}");
    Console.WriteLine($"first item: {Log.Scrub(first.GetRawText())}");

    try
    {
        var missing = await client.GetEquityAsync("ZZZZZZ", CancellationToken.None);
        Console.WriteLine($"lookup of a fake ticker returned {(missing == null ? "null" : missing.Symbol)}");
    }
    catch (TastytradeApiException ex)
    {
        Console.WriteLine($"lookup of a fake ticker failed cleanly: {ex.Message}");
    }
}

// Today's daily bar when a chart asks for only the current day (2026-11-19: it was dropped, so today's candle had
// no history). The range checks need nothing; the load at the end asks tastytrade, read-only.
async Task RunTodayBar(string symbol)
{
    const long Hour = 3_600_000, Day = 24 * Hour;
    long dayStart = 1_795_000_000_000;
    Check(HistoryClient.InRange(dayStart, Day, dayStart + (14 * Hour), dayStart + (18 * Hour)),
        "a day's bar starting before the range but running into it is kept");
    Check(!HistoryClient.InRange(dayStart, Day, dayStart + Day, dayStart + Day + Hour) && !HistoryClient.InRange(dayStart, Day, dayStart - (3 * Hour), dayStart - Hour),
        "one that ended when the range starts, or starts after it ends, isn't");
    Check(!HistoryClient.InRange(dayStart, 0, dayStart + 1, dayStart + Hour) && HistoryClient.InRange(dayStart, 0, dayStart, dayStart + Hour),
        "with no length given, a bar must start in the range, as before");

    using var client = api();
    var history = new HistoryClient(async (force, ct) => await client.GetQuoteTokenAsync(ct));
    var now = DateTime.UtcNow;
    var before = await history.LoadAsync(symbol + "{=1d,tho=true}", now.AddHours(-1), now, CancellationToken.None);
    var after = await history.LoadAsync(symbol + "{=1d,tho=true}", now.AddHours(-1), now, CancellationToken.None, TimeSpan.FromDays(1));
    string Bar(IReadOnlyList<BarRow> bars) => bars.Count == 0 ? "none" : $"{DateTimeOffset.FromUnixTimeMilliseconds(bars[^1].TimeMs):MM-dd HH:mm} UTC O {bars[^1].Open} H {bars[^1].High} L {bars[^1].Low} C {bars[^1].Close}";
    Console.WriteLine($"{symbol} daily bars for the last hour: without a length {before.Count} ({Bar(before)}), with a day's length {after.Count} ({Bar(after)})");
    Check(after.Count == 1, $"asked for the last hour, today's daily bar comes back with its open, high and low ({Bar(after)})");
}

async Task RunHistory(string symbol)
{
    using var client = api();
    var history = new HistoryClient(async (force, ct) => await client.GetQuoteTokenAsync(ct));
    var now = DateTime.UtcNow;

    async Task<IReadOnlyList<BarRow>> Load(string code, TimeSpan back)
    {
        var sw = Stopwatch.StartNew();
        var bars = await history.LoadAsync(symbol + "{" + code + "}", now - back, now, CancellationToken.None);
        var first = bars.Count > 0 ? DateTimeOffset.FromUnixTimeMilliseconds(bars[0].TimeMs).ToString("MM-dd HH:mm:ss") : "none";
        var last = bars.Count > 0 ? DateTimeOffset.FromUnixTimeMilliseconds(bars[^1].TimeMs).ToString("MM-dd HH:mm:ss") : "none";
        Console.WriteLine($"{code,-16} {back.TotalDays,6:F2}d  {bars.Count,6} bars  {sw.ElapsedMilliseconds,6} ms  {first} to {last} UTC  last close {(bars.Count > 0 ? bars[^1].Close : double.NaN)}");
        return bars;
    }

    var oneMinute = await Load("=1m", TimeSpan.FromDays(3));
    Check(oneMinute.Count > 500, "3 days of 1-minute bars");
    Check(oneMinute.Zip(oneMinute.Skip(1)).All(p => p.First.TimeMs < p.Second.TimeMs), "bars ascending with no duplicates");
    Check(oneMinute.All(b => b.High >= b.Low && !double.IsNaN(b.Open)), "bars valid");

    await Load("=5m", TimeSpan.FromDays(10));
    await Load("=1h", TimeSpan.FromDays(30));
    var daily = await Load("=1d,tho=true", TimeSpan.FromDays(180));
    Check(daily.Count > 50, "daily bars with regular-hours flag");
    await Load("=1d", TimeSpan.FromDays(30));
    await Load("=1w,tho=true", TimeSpan.FromDays(365));
    await Load("=1mo,tho=true", TimeSpan.FromDays(1000));
    Console.WriteLine("--- sub-minute candles ---");
    await Load("=1s", TimeSpan.FromHours(2));
    await Load("=5s", TimeSpan.FromHours(6));
    await Load("=15s", TimeSpan.FromHours(12));
    await history.StopAsync();
}

// The plugin's IBKR client against a running Gateway: quotes, depth, the 3-symbol depth limit,
// bad symbols, churn and reconnects. Reads market data only.
async Task RunIbkr(int port)
{
    Check(!IbkrClient.AllowedRequests.Contains(3) && !IbkrClient.AllowedRequests.Contains(4), "order messages (place 3, cancel 4) aren't sendable");

    var sink = new IbkrTestSink();
    var client = new IbkrClient(new IbkrConfig(true, "127.0.0.1", port, 79, IbkrConfig.Load().AllowLiveAccount));
    client.SetSink(sink);
    client.Start();
    client.SetQuote("SPY", true);
    client.SetQuote("NVDA", true);
    client.SetQuote("AEHL", true);

    var timer = Stopwatch.StartNew();
    while (client.State != IbkrState.Live && timer.Elapsed < TimeSpan.FromSeconds(15))
        await Task.Delay(50);
    Console.WriteLine($"live after {timer.ElapsedMilliseconds} ms, state {client.State}");
    Check(client.State == IbkrState.Live, "client goes live on a real quote");

    await Task.Delay(4000);
    foreach (var (symbol, q) in sink.Quotes)
        Console.WriteLine($"  quote {symbol}: {q.Bid}/{q.Ask} sizes {q.BidSize}/{q.AskSize}, {sink.QuoteCount.GetValueOrDefault(symbol)} updates");
    Check(sink.Quotes.ContainsKey("NVDA") && sink.Quotes["NVDA"].Ask >= sink.Quotes["NVDA"].Bid, "quotes arrive and make sense");

    // Depth on three symbols, then a fourth that must be refused and later admitted.
    client.SetDepth("SPY", true);
    client.SetDepth("NVDA", true);
    client.SetDepth("AEHL", true);
    await Task.Delay(1500);
    client.SetDepth("LABT", true);
    await Task.Delay(3000);

    foreach (var (symbol, d) in sink.Depth)
    {
        Console.WriteLine($"  depth {symbol}: {sink.DepthCount.GetValueOrDefault(symbol)} deliveries, {d.Bids} bids, {d.Asks} asks, venues {string.Join(",", d.Venues)}, top {d.BidTop} x {d.BidTopSize} / {d.AskTop} x {d.AskTopSize}");
        if (sink.Quotes.TryGetValue(symbol, out var q))
            Console.WriteLine($"        same moment's quote: {q.Bid} x {q.BidSize} / {q.Ask} x {q.AskSize}");
    }

    Check(sink.Depth.ContainsKey("SPY") && sink.Depth["SPY"].Bids > 1 && sink.Depth["SPY"].Venues.Count > 1, "smart depth is multi-row and multi-venue");
    // Another client on the same Gateway, like Quantower itself, can hold slots, so which symbols
    // get refused varies. What must hold is that releasing a granted one lets a refused one in.
    string[] depthSymbols = ["SPY", "NVDA", "AEHL", "LABT"];
    var refused = client.WaitingForSlot(IbkrClient.DepthFeed);
    Check(refused.Count > 0 && sink.Problems.Any(p => p.Contains("309")), $"depth requests past the limit are refused with 309 ({string.Join(",", refused)})");

    var released = depthSymbols.First(s => !refused.Contains(s));
    client.SetDepth(released, false);
    var slotClock = Stopwatch.StartNew();
    while (slotClock.Elapsed < TimeSpan.FromSeconds(30) && !refused.Any(s => sink.DepthCount.GetValueOrDefault(s) > 0))
        await Task.Delay(200);
    Check(refused.Any(s => sink.DepthCount.GetValueOrDefault(s) > 0), $"a refused symbol gets a book once a slot frees ({slotClock.Elapsed.TotalSeconds:F1} s)");

    // While a symbol still waits, the retries are requests too, so "sends nothing" can't be judged.
    void CheckQuiet(bool ok, string what)
    {
        if (client.WaitingForSlot(IbkrClient.DepthFeed).Count == 0)
            Check(ok, what);
        else
            Console.WriteLine($"SKIP  {what}: a symbol is still waiting for a slot held by another client");
    }

    // Chart-open churn: quote off and on again within milliseconds costs no requests.
    long before = client.RequestsSent;
    client.SetQuote("NVDA", false);
    client.SetQuote("NVDA", true);
    await Task.Delay(200);
    CheckQuiet(client.RequestsSent == before, "subscription churn sends nothing");

    // The canary stays on whatever the platform asks for.
    before = client.RequestsSent;
    client.SetQuote("SPY", false);
    await Task.Delay(200);
    CheckQuiet(client.RequestsSent == before, "canary stays subscribed when the platform drops it");

    // A symbol that doesn't exist: reported for that symbol, nothing else disturbed.
    client.SetQuote("ZZZZZZ", true);
    await Task.Delay(2500);
    Check(sink.Problems.Any(p => p.Contains("ZZZZZZ")), "a bad symbol is reported");
    Check(client.State == IbkrState.Live, "a bad symbol doesn't disturb the client");

    // Handover to a new vendor instance: what's still wanted must not be re-sent.
    client.SetQuote("ZZZZZZ", false);
    await Task.Delay(100);
    before = client.RequestsSent;
    client.BeginResync(TimeSpan.FromMilliseconds(600));
    client.SetQuote("NVDA", true);
    client.SetQuote("AEHL", true);
    foreach (var symbol in depthSymbols.Where(s => s != released))
        client.SetDepth(symbol, true);
    await Task.Delay(1200);
    CheckQuiet(client.RequestsSent == before, $"a vendor handover re-sends nothing ({client.RequestsSent - before} requests)");

    // Kill the socket and time the recovery.
    int nvdaAtDrop = sink.QuoteCount.GetValueOrDefault("NVDA");
    int depthAtDrop = sink.DepthCount.GetValueOrDefault("NVDA");
    var dropTimer = Stopwatch.StartNew();
    client.ForceReconnect("harness drop test");
    while (client.State == IbkrState.Live && dropTimer.Elapsed < TimeSpan.FromSeconds(3))
        await Task.Delay(5);
    while (client.State != IbkrState.Live && dropTimer.Elapsed < TimeSpan.FromSeconds(20))
        await Task.Delay(10);
    Console.WriteLine($"  back live after {dropTimer.ElapsedMilliseconds} ms");
    Check(client.State == IbkrState.Live, "reconnects by itself");
    await Task.Delay(3000);
    Check(sink.QuoteCount.GetValueOrDefault("NVDA") > nvdaAtDrop, "quotes resume after a reconnect");
    Check(sink.DepthCount.GetValueOrDefault("NVDA") > depthAtDrop, "depth resumes after a reconnect");
    Check(sink.States.Contains(IbkrState.Down), "the sink is told about the outage");

    await client.StopAsync();
    Check(client.State == IbkrState.Stopped, "stops cleanly");
    int afterStop = sink.QuoteCount.Values.Sum();
    await Task.Delay(800);
    Check(sink.QuoteCount.Values.Sum() == afterStop, "no data after stopping");

    // Quantower's reconnects: a new vendor taking over must not touch the Gateway connection,
    // and it must hear the current state of what it asks for straight away.
    var firstVendor = new IbkrTestSink();
    var sessionConfig = new IbkrConfig(true, "127.0.0.1", port, 81, IbkrConfig.Load().AllowLiveAccount);
    var session = IbkrSession.Attach(firstVendor, sessionConfig);
    session.SetQuote("NVDA", true);
    session.SetDepth("NVDA", true);
    timer.Restart();
    while (session.State != IbkrState.Live && timer.Elapsed < TimeSpan.FromSeconds(10))
        await Task.Delay(50);
    await Task.Delay(1500);
    Check(firstVendor.DepthCount.GetValueOrDefault("NVDA") > 0, "session delivers to its first vendor");

    IbkrSession.Detach(firstVendor);
    await Task.Delay(1000);
    var secondVendor = new IbkrTestSink();
    long sentBefore = session.RequestsSent;
    var sameSession = IbkrSession.Attach(secondVendor, sessionConfig);
    Check(ReferenceEquals(session, sameSession), "a new vendor gets the same connection");
    sameSession.SetDepth("NVDA", true);
    sameSession.SetQuote("NVDA", true);
    await Task.Delay(1000);
    Check(secondVendor.DepthCount.GetValueOrDefault("NVDA") > 0, "the new vendor sees the book straight away");
    await Task.Delay(2500);
    Check(sameSession.RequestsSent == sentBefore, $"the takeover sends nothing to Gateway ({sameSession.RequestsSent - sentBefore} requests)");
    Check(sameSession.State == IbkrState.Live, "the connection stays live through a takeover");

    IbkrSession.Detach(secondVendor);
    await Task.Delay(11000);
    Check(sameSession.State == IbkrState.Stopped, "the connection closes when nobody comes back");

    // Gateway not running: must retry quietly, never throw, and stop cleanly.
    var absentSink = new IbkrTestSink();
    var absent = new IbkrClient(new IbkrConfig(true, "127.0.0.1", 4999, 80, false));
    absent.SetSink(absentSink);
    absent.Start();
    absent.SetQuote("SPY", true);
    await Task.Delay(2500);
    Check(absent.State == IbkrState.Down, $"reports down when Gateway isn't there (state {absent.State})");
    await absent.StopAsync();
    Check(absent.State == IbkrState.Stopped, "stops cleanly while retrying");
}

// IBKR's tape in each mode, the slot limit, and how the prints really arrive. Market data only.
async Task RunIbkrTape(int port, int seconds, string[] symbols)
{
    Check(!IbkrClient.AllowedRequests.Contains(3) && !IbkrClient.AllowedRequests.Contains(4), "order messages (place 3, cancel 4) aren't sendable");

    var sink = new IbkrTestSink();
    var client = new IbkrClient(new IbkrConfig(true, "127.0.0.1", port, 83, IbkrConfig.Load().AllowLiveAccount));
    Check(client.TapeMode == TapeMode.Ibkr, "the default tape is IBKR's whole tape");
    client.SetSink(sink);
    client.Start();
    foreach (var symbol in symbols)
    {
        client.SetTape(symbol, true);
        client.SetQuote(symbol, true);
    }

    await Task.Delay(seconds * 1000);

    foreach (var symbol in symbols)
    {
        var name = IbkrClient.ToIbkrSymbol(symbol);
        var prints = sink.Prints.GetValueOrDefault(name) ?? [];
        var shares = client.TapeShares(symbol);
        Console.WriteLine($"{symbol}: {prints.Count} prints, {prints.Sum(p => p.Size):N0} shares; off-market hidden {shares.OffMarket:N0} shares; venues {string.Join(" ", prints.GroupBy(p => p.Venue).OrderByDescending(g => g.Count()).Select(g => $"{g.Key}:{g.Count()}"))}");
        var batches = prints.Select(p => p.ArrivalTicks).Distinct().Order().ToList();
        if (batches.Count > 1)
        {
            var gaps = batches.Zip(batches.Skip(1), (a, b) => (b - a) / (double)TimeSpan.TicksPerMillisecond).Order().ToList();
            Console.WriteLine($"  {batches.Count} deliveries; gap ms p50 {gaps[gaps.Count / 2]:F0}, p99 {gaps[(int)(gaps.Count * 0.99)]:F0}, max {gaps[^1]:F0}");
        }
    }

    var waiting = client.WaitingForSlot(IbkrClient.TapeFeed);
    Console.WriteLine($"waiting for a slot: {string.Join(",", waiting)}");
    foreach (var problem in sink.Problems)
        Console.WriteLine($"problem: {problem}");

    if (symbols.Length > 5)
    {
        Check(waiting.Count == symbols.Length - 5, "tick-by-tick has 5 slots, and the rest wait");
        client.SetTape(symbols[0], false);
        await Task.Delay(4000);
        Check(client.WaitingForSlot(IbkrClient.TapeFeed).Count == waiting.Count - 1, "a waiting symbol gets a slot once one frees");
    }

    long before = client.RequestsSent;
    client.SetTape(symbols[^1], false);
    client.SetTape(symbols[^1], true);
    await Task.Delay(200);
    Check(client.RequestsSent == before, "tape subscription churn sends nothing");
    await client.StopAsync();

    var off = new IbkrClient(new IbkrConfig(true, "127.0.0.1", port, 84, IbkrConfig.Load().AllowLiveAccount, 50, TapeMode.Tastytrade));
    off.SetSink(new IbkrTestSink());
    off.Start();
    off.SetTape("SPY", true);
    await Task.Delay(1500);
    Check(off.TapeShares("SPY") == default, "tastytrade mode takes no tick-by-tick slot");
    await off.StopAsync();
}

// How many depth slots are really free, and how long a freed one takes to come back.
async Task RunIbkrSlots(int port, string[] symbols)
{
    // "burst" as the first symbol asks for them all at once, as a connect or a reconnect does.
    bool burst = symbols[0] == "burst";
    if (burst)
        symbols = symbols[1..];

    var sink = new IbkrTestSink();
    var client = new IbkrClient(new IbkrConfig(true, "127.0.0.1", port, 85, IbkrConfig.Load().AllowLiveAccount));
    client.SetSink(sink);
    client.Start();
    await Task.Delay(1500);
    foreach (var symbol in symbols)
    {
        client.SetDepth(symbol, true);
        if (!burst)
            await Task.Delay(400);
    }

    await Task.Delay(2500);
    var waiting = client.WaitingForSlot(IbkrClient.DepthFeed);
    var granted = symbols.Where(s => !waiting.Contains(s)).ToList();
    Console.WriteLine($"granted {granted.Count}: {string.Join(",", granted)}; refused {waiting.Count}: {string.Join(",", waiting)}");
    if (waiting.Count > 0 && granted.Count > 0)
    {
        // A retry in flight looks like "not waiting" until its refusal comes back, so only a
        // delivered book proves a slot was really given.
        bool GotBook() => waiting.Any(s => sink.DepthCount.GetValueOrDefault(s) > 0);
        var clock = Stopwatch.StartNew();
        client.SetDepth(granted[0], false);
        while (clock.Elapsed < TimeSpan.FromSeconds(90) && !GotBook())
            await Task.Delay(200);
        Console.WriteLine(GotBook()
            ? $"cancelled {granted[0]}: first book for a waiting symbol after {clock.Elapsed.TotalSeconds:F1} s"
            : $"cancelled {granted[0]}: no book for any waiting symbol after {clock.Elapsed.TotalSeconds:F1} s");
    }

    await client.StopAsync();
}

// Switching a DOM's symbol with every slot in use, through the plugin's client. Takes at least 5
// symbols: the first 3 are held, and each round swaps one for the next spare.
async Task RunIbkrSwitch(int port, string[] symbols)
{
    // A number as the first symbol says how many to hold, for when another client has the other slots.
    int holdCount = 3;
    if (int.TryParse(symbols[0], out int asked))
    {
        holdCount = asked;
        symbols = symbols[1..];
    }

    var sink = new IbkrTestSink();
    var client = new IbkrClient(new IbkrConfig(true, "127.0.0.1", port, 86, IbkrConfig.Load().AllowLiveAccount));
    client.SetSink(sink);
    client.Start();
    await Task.Delay(1500);

    var held = symbols.Take(holdCount).ToList();
    foreach (var symbol in held)
    {
        client.SetDepth(symbol, true);
        await Task.Delay(1000);
    }

    Check(held.All(s => sink.DepthCount.GetValueOrDefault(s) > 0), $"{holdCount} slots held: {string.Join(",", held)}");
    var spare = new Queue<string>(symbols.Skip(holdCount));

    async Task<double> Swap(string from, string to, TimeSpan offAfterOn)
    {
        int before = sink.DepthCount.GetValueOrDefault(to);
        var clock = Stopwatch.StartNew();
        client.SetDepth(to, true);
        if (offAfterOn > TimeSpan.Zero)
            await Task.Delay(offAfterOn);
        client.SetDepth(from, false);
        while (clock.Elapsed < TimeSpan.FromSeconds(20) && sink.DepthCount.GetValueOrDefault(to) == before)
            await Task.Delay(10);
        return sink.DepthCount.GetValueOrDefault(to) > before ? clock.Elapsed.TotalMilliseconds : double.NaN;
    }

    for (int round = 0; round < 6; round++)
    {
        // Quantower sends both changes within a millisecond. The late case is the same switch with
        // the new symbol's request reaching Gateway while every slot is still taken.
        var delay = round % 2 == 0 ? TimeSpan.Zero : TimeSpan.FromMilliseconds(300);
        int slot = round % held.Count;
        var (from, to) = (held[slot], spare.Dequeue());
        int refusalsBefore = sink.Problems.Count(p => p.Contains("309"));
        double ms = await Swap(from, to, delay);
        int refusals = sink.Problems.Count(p => p.Contains("309")) - refusalsBefore;
        Console.WriteLine($"  round {round}: {from} -> {to}, off {delay.TotalMilliseconds:F0} ms after on: first book {ms:F0} ms, refusals {refusals}");
        Check(!double.IsNaN(ms), $"{to} gets a book after replacing {from}");
        held[slot] = to;
        spare.Enqueue(from);
        await Task.Delay(1500);
    }

    await client.StopAsync();
}

// TimeAndSale by contract, fromTime unit and lookback, with Candle as the control because its history
// is known to work. One channel per case, one at a time, on one socket. Prints no secrets.
async Task RunTnsMatrix(string symbol, int secondsEach, bool delayedEndpoint)
{
    using var client = api();
    var token = await client.GetQuoteTokenAsync(CancellationToken.None);
    var url = delayedEndpoint ? token.Url.Replace("/realtime", "/delayed", StringComparison.Ordinal) : token.Url;
    Console.WriteLine($"level={token.Level} endpoint={url[(url.LastIndexOf('/') + 1)..]} symbol={symbol} {secondsEach}s per case, run at {DateTime.Now:HH:mm:ss} local");

    using var ws = new ClientWebSocket();
    await ws.ConnectAsync(new Uri(url), CancellationToken.None);
    var sendGate = new SemaphoreSlim(1, 1);
    async Task Send(JsonObject message)
    {
        var bytes = Encoding.UTF8.GetBytes(message.ToJsonString());
        await sendGate.WaitAsync();
        try
        {
            await ws.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None);
        }
        finally
        {
            sendGate.Release();
        }
    }

    var authorized = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
    var opened = new ConcurrentDictionary<int, TaskCompletionSource>();
    var configured = new ConcurrentDictionary<int, TaskCompletionSource>();
    var layouts = new ConcurrentDictionary<(int Channel, string Event), string[]>();
    var results = new ConcurrentDictionary<int, TnsCase>();
    var serverMessages = new ConcurrentBag<string>();
    bool authSent = false;
    using var stop = new CancellationTokenSource();

    void OnData(int channel, JsonElement data, double arrivalMs)
    {
        if (!results.TryGetValue(channel, out var result) || data.ValueKind != JsonValueKind.Array)
            return;
        for (int i = 0; i + 1 < data.GetArrayLength(); i += 2)
        {
            var eventName = data[i].GetString();
            if (!layouts.TryGetValue((channel, eventName), out var fields))
                continue;
            var values = data[i + 1];
            int iTime = Array.IndexOf(fields, "time");
            int iFlags = Array.IndexOf(fields, "eventFlags");
            int iCode = Array.IndexOf(fields, "exchangeCode");
            for (int row = 0; (row + 1) * fields.Length <= values.GetArrayLength(); row++)
            {
                result.Rows++;
                if (result.FirstRowMs == 0)
                    result.FirstRowMs = arrivalMs;
                if (iFlags >= 0 && values[(row * fields.Length) + iFlags].ValueKind == JsonValueKind.Number)
                    result.Flags |= values[(row * fields.Length) + iFlags].GetInt32();
                if (iCode >= 0)
                    result.Codes.Add(values[(row * fields.Length) + iCode].ToString());
                if (iTime >= 0 && values[(row * fields.Length) + iTime].ValueKind == JsonValueKind.Number)
                {
                    double time = values[(row * fields.Length) + iTime].GetDouble();
                    if (time <= 0)
                        continue;
                    result.MinTime = Math.Min(result.MinTime, time);
                    result.MaxTime = Math.Max(result.MaxTime, time);
                    if (arrivalMs - time < 30000)
                        result.LiveRows++;
                    else
                        result.OlderRows++;
                }
            }
        }
    }

    var reader = Task.Run(async () =>
    {
        var buffer = new byte[1 << 20];
        var message = new MemoryStream();
        try
        {
            while (ws.State == WebSocketState.Open)
            {
                message.SetLength(0);
                ValueWebSocketReceiveResult received;
                do
                {
                    received = await ws.ReceiveAsync(buffer.AsMemory(), stop.Token);
                    message.Write(buffer, 0, received.Count);
                } while (!received.EndOfMessage);

                double arrivalMs = DateTime.UtcNow.Subtract(DateTime.UnixEpoch).TotalMilliseconds;
                if (received.MessageType == WebSocketMessageType.Close)
                    break;

                using var doc = JsonDocument.Parse(message.GetBuffer().AsMemory(0, (int)message.Length));
                var root = doc.RootElement;
                var type = root.GetProperty("type").GetString();
                int channel = root.TryGetProperty("channel", out var channelElement) ? channelElement.GetInt32() : 0;
                switch (type)
                {
                    case "AUTH_STATE":
                        var state = root.GetProperty("state").GetString();
                        if (state == "AUTHORIZED")
                        {
                            authorized.TrySetResult(true);
                        }
                        else if (authSent)
                        {
                            authorized.TrySetResult(false);
                        }
                        else
                        {
                            authSent = true;
                            await Send(new JsonObject { ["type"] = "AUTH", ["channel"] = 0, ["token"] = token.Token });
                        }

                        break;
                    case "CHANNEL_OPENED":
                        if (opened.TryGetValue(channel, out var openedSignal))
                            openedSignal.TrySetResult();
                        break;
                    case "FEED_CONFIG":
                        if (root.TryGetProperty("eventFields", out var eventFields) && eventFields.ValueKind == JsonValueKind.Object)
                        {
                            foreach (var entry in eventFields.EnumerateObject())
                                layouts[(channel, entry.Name)] = entry.Value.EnumerateArray().Select(c => c.GetString()).ToArray();
                        }

                        if (configured.TryGetValue(channel, out var configuredSignal))
                            configuredSignal.TrySetResult();
                        break;
                    case "FEED_DATA":
                        OnData(channel, root.GetProperty("data"), arrivalMs);
                        break;
                    case "KEEPALIVE":
                    case "SETUP":
                    case "CHANNEL_CLOSED":
                        break;
                    default:
                        var text = Encoding.UTF8.GetString(message.GetBuffer(), 0, (int)message.Length);
                        serverMessages.Add(Log.Scrub(text.Length > 300 ? text[..300] : text));
                        break;
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (WebSocketException ex)
        {
            Console.WriteLine($"socket error: {ex.Message}");
        }
    });

    var keepalive = Task.Run(async () =>
    {
        try
        {
            while (true)
            {
                await Task.Delay(TimeSpan.FromSeconds(30), stop.Token);
                await Send(new JsonObject { ["type"] = "KEEPALIVE", ["channel"] = 0 });
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or WebSocketException)
        {
        }
    });

    await Send(new JsonObject { ["type"] = "SETUP", ["channel"] = 0, ["version"] = "0.1-ScalpFeed/0.2.0", ["keepaliveTimeout"] = 60, ["acceptKeepaliveTimeout"] = 60 });
    if (!await authorized.Task.WaitAsync(TimeSpan.FromSeconds(10)))
    {
        Console.WriteLine("this endpoint refused the token, stopping without another try");
        stop.Cancel();
        return;
    }

    string Et(double ms) => ms is double.MaxValue or double.MinValue ? "-" : DateTimeOffset.FromUnixTimeMilliseconds((long)ms).ToOffset(TimeSpan.FromHours(-5)).ToString("MM-dd HH:mm:ss");

    int nextChannel = 1;
    async Task RunCase(string eventType, string subscribeSymbol, string contract, string label, Func<long, long?> fromTime)
    {
        int channel = nextChannel;
        nextChannel += 2;
        opened[channel] = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        configured[channel] = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var result = results[channel] = new TnsCase();

        await Send(new JsonObject { ["type"] = "CHANNEL_REQUEST", ["channel"] = channel, ["service"] = "FEED", ["parameters"] = new JsonObject { ["contract"] = contract } });
        await opened[channel].Task.WaitAsync(TimeSpan.FromSeconds(10));
        var accept = new JsonObject
        {
            ["TimeAndSale"] = new JsonArray("eventType", "eventSymbol", "eventFlags", "index", "time", "sequence", "exchangeCode", "price", "size", "validTick", "type"),
            ["Candle"] = new JsonArray("eventType", "eventSymbol", "eventFlags", "index", "time", "count", "open", "close", "volume")
        };
        await Send(new JsonObject { ["type"] = "FEED_SETUP", ["channel"] = channel, ["acceptAggregationPeriod"] = 0, ["acceptDataFormat"] = "COMPACT", ["acceptEventFields"] = accept });
        await configured[channel].Task.WaitAsync(TimeSpan.FromSeconds(10));

        var entry = new JsonObject { ["type"] = eventType, ["symbol"] = subscribeSymbol };
        long nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        if (fromTime(nowMs) is { } from)
            entry["fromTime"] = from;
        double startMs = nowMs;
        await Send(new JsonObject { ["type"] = "FEED_SUBSCRIPTION", ["channel"] = channel, ["reset"] = true, ["add"] = new JsonArray(entry) });
        await Task.Delay(TimeSpan.FromSeconds(secondsEach));
        await Send(new JsonObject { ["type"] = "CHANNEL_CANCEL", ["channel"] = channel });
        await Task.Delay(150);

        string first = result.FirstRowMs == 0 ? "-" : $"{result.FirstRowMs - startMs:F0}ms";
        Console.WriteLine($"{eventType,-11} {contract,-8} {label,-12} rows {result.Rows,6} (live {result.LiveRows,5}, older {result.OlderRows,6})  first {first,8}  times {Et(result.MinTime)} .. {Et(result.MaxTime)} ET  flags 0x{result.Flags:X2}  codes {string.Join("", result.Codes)}");
    }

    foreach (var contract in new[] { "AUTO", "STREAM", "HISTORY" })
    {
        await RunCase("TimeAndSale", symbol, contract, "none", _ => null);
        foreach (var (name, seconds) in new[] { ("30 s", 30L), ("5 min", 300L), ("1 h", 3600L), ("6 h", 21600L) })
        {
            await RunCase("TimeAndSale", symbol, contract, $"{name} ms", now => now - (seconds * 1000));
            await RunCase("TimeAndSale", symbol, contract, $"{name} sec", now => (now / 1000) - seconds);
        }
    }

    Console.WriteLine("--- Candle, the control");
    foreach (var contract in new[] { "AUTO", "HISTORY" })
    {
        await RunCase("Candle", symbol + "{=1m}", contract, "1 h ms", now => now - 3600000);
        await RunCase("Candle", symbol + "{=1m}", contract, "1 h sec", now => (now / 1000) - 3600);
    }

    foreach (var line in serverMessages.Distinct())
        Console.WriteLine($"server said: {line}");

    stop.Cancel();
}

// A Connect whose first attempt fails, as after a wake from standby when DNS isn't back yet.
// The retry succeeds, so the wait must see that instead of running out its time.
async Task RunConnectRetry()
{
    using var client = api();
    int attempts = 0;
    async Task<QuoteToken> GetToken(bool force, CancellationToken ct)
    {
        if (Interlocked.Increment(ref attempts) == 1)
            throw new HttpRequestException("No such host is known (simulated)");
        return await client.GetQuoteTokenAsync(ct);
    }

    var fields = new Dictionary<string, string[]> { ["Quote"] = ["eventType", "eventSymbol", "bidPrice", "askPrice"] };
    var link = new DxLinkLink("connectretry", GetToken, fields, new CountingSink());
    link.Start();

    var clock = Stopwatch.StartNew();
    bool ready = await link.WaitReadyAsync(TimeSpan.FromSeconds(12), CancellationToken.None);
    Console.WriteLine($"wait returned {ready} after {clock.ElapsedMilliseconds} ms; link state {link.State}, attempts {attempts}");
    Check(link.State == LinkState.Ready, "the link itself came up on the retry");
    Check(ready, "the wait sees a retry that succeeds");
    Check(clock.Elapsed < TimeSpan.FromSeconds(5), "the wait ends when the link is ready, not at its timeout");
    await link.StopAsync();
}

// A quote token tastytrade no longer accepts, against a stand-in DXLink server. From 2026-11-19 20:26 tastytrade
// answered the token's AUTH with ERROR UNAUTHORIZED and closed the socket, and the link offered the same token for
// ten hours. Either way of refusing it has to bring a freshly minted one.
async Task RunTokenRefused()
{
    foreach (bool asError in new[] { true, false })
    {
        string how = asError ? "ERROR UNAUTHORIZED, then a closed socket" : "AUTH_STATE UNAUTHORIZED after the AUTH";
        int port;
        {
            var probe = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
            probe.Start();
            port = ((System.Net.IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
        }

        var listener = new System.Net.HttpListener();
        listener.Prefixes.Add($"http://localhost:{port}/");
        listener.Start();
        var offered = new ConcurrentQueue<string>();
        var server = Task.Run(async () =>
        {
            while (listener.IsListening)
            {
                System.Net.HttpListenerContext context;
                try
                {
                    context = await listener.GetContextAsync();
                }
                catch (Exception ex) when (ex is System.Net.HttpListenerException or ObjectDisposedException or InvalidOperationException)
                {
                    break;
                }

                _ = Task.Run(() => FakeDxLink(context, asError, offered));
            }
        });

        var forced = new ConcurrentQueue<bool>();
        Task<QuoteToken> Token(bool forceNew, CancellationToken ct)
        {
            // Unless asked for a new one, the token in hand: the dead one, as the plugin kept doing.
            forced.Enqueue(forceNew);
            return Task.FromResult(new QuoteToken(forceNew ? "fresh" : "dead", $"ws://localhost:{port}/", "api", DateTime.UtcNow));
        }

        var fields = new Dictionary<string, string[]> { ["Quote"] = ["eventType", "eventSymbol", "bidPrice", "askPrice"] };
        var link = new DxLinkLink("tokenrefused", Token, fields, new CountingSink());
        var clock = Stopwatch.StartNew();
        link.Start();
        bool ready = await link.WaitReadyAsync(TimeSpan.FromSeconds(8), CancellationToken.None);
        long ms = clock.ElapsedMilliseconds;
        await link.StopAsync();
        listener.Stop();
        listener.Close();
        await server;

        Console.WriteLine($"{how}: ready={ready} after {ms} ms, tokens offered [{string.Join(", ", offered)}], forceNew asked [{string.Join(", ", forced)}]");
        Check(ready && ms < 3000, $"{how}: the link is back within a few seconds");
        Check(offered.SequenceEqual(["dead", "fresh"]), $"{how}: it offers the dead token once, then a fresh one");
        Check(forced.SequenceEqual([false, true]), $"{how}: it asks for a new token after the refusal, not the same one again");
    }
}

// The DXLink handshake, as tastytrade's server plays it, for one connection: any token but "fresh" is refused.
async Task FakeDxLink(System.Net.HttpListenerContext context, bool refuseWithError, ConcurrentQueue<string> offered)
{
    System.Net.WebSockets.WebSocket ws = null;
    try
    {
        ws = (await context.AcceptWebSocketAsync(null)).WebSocket;
        var buffer = new byte[64 * 1024];
        Task Send(string json) => ws.SendAsync(Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text, true, CancellationToken.None);
        while (ws.State == WebSocketState.Open)
        {
            var received = await ws.ReceiveAsync(buffer, CancellationToken.None);
            if (received.MessageType == WebSocketMessageType.Close)
                break;

            using var message = JsonDocument.Parse(buffer.AsMemory(0, received.Count));
            var root = message.RootElement;
            switch (root.GetProperty("type").GetString())
            {
                case "SETUP":
                    await Send("""{"type":"SETUP","channel":0,"keepaliveTimeout":60,"acceptKeepaliveTimeout":60,"version":"stand-in"}""");
                    await Send("""{"type":"AUTH_STATE","channel":0,"state":"UNAUTHORIZED"}""");
                    break;
                case "AUTH":
                    string token = root.GetProperty("token").GetString();
                    offered.Enqueue(token);
                    if (token == "fresh")
                    {
                        await Send("""{"type":"AUTH_STATE","channel":0,"state":"AUTHORIZED","userId":"stand-in"}""");
                    }
                    else if (refuseWithError)
                    {
                        await Send("""{"type":"ERROR","channel":0,"error":"UNAUTHORIZED","message":"Authentication failed"}""");
                        await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "Bye", CancellationToken.None);
                    }
                    else
                    {
                        await Send("""{"type":"AUTH_STATE","channel":0,"state":"UNAUTHORIZED"}""");
                    }

                    break;
                case "CHANNEL_REQUEST":
                    await Send("""{"type":"CHANNEL_OPENED","channel":1,"service":"FEED","parameters":{"contract":"AUTO"}}""");
                    break;
                case "FEED_SETUP":
                    await Send("""{"type":"FEED_CONFIG","channel":1,"dataFormat":"COMPACT","aggregationPeriod":0,"eventFields":{"Quote":["eventType","eventSymbol","bidPrice","askPrice"]}}""");
                    break;
            }
        }
    }
    catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or ObjectDisposedException or System.Net.HttpListenerException)
    {
        // The client hung up, or the test is over.
    }
    finally
    {
        ws?.Dispose();
    }
}

// Rejected credentials: asked once, then not again, because too many failures can block the IP.
async Task RunRefusedCredentials()
{
    var path = Path.Combine(Path.GetTempPath(), "scalpfeed-refused-test.json");
    await File.WriteAllTextAsync(path, """{"client_id":"junk","client_secret":"junk","refresh_token":"junk-test-token"}""");
    try
    {
        async Task<(string Message, long Ms)> Attempt()
        {
            var timer = Stopwatch.StartNew();
            using var client = new TastytradeApi(path);
            try
            {
                await client.GetQuoteTokenAsync(default);
                return ("no error", timer.ElapsedMilliseconds);
            }
            catch (TastytradeApiException ex)
            {
                return (ex.Message, timer.ElapsedMilliseconds);
            }
        }

        var first = await Attempt();
        Console.WriteLine($"first:  {first.Ms} ms: {first.Message[..Math.Min(90, first.Message.Length)]}");
        var second = await Attempt();
        Console.WriteLine($"second: {second.Ms} ms: {second.Message[..Math.Min(90, second.Message.Length)]}");
        Check(first.Message.Contains("OAuth token request returned 4", StringComparison.Ordinal), "the first attempt reaches tastytrade and is refused");
        Check(second.Message.Contains("not trying again", StringComparison.Ordinal) && second.Ms < 50, "the next attempt doesn't reach tastytrade");
    }
    finally
    {
        File.Delete(path);
    }
}

// What the tape's filter does to each kind of print, then what it does to real recordings.
void RunPrintFilter(string[] captures)
{
    var fields = new Dictionary<string, string[]>
    {
        ["TimeAndSale"] = ["eventType", "eventSymbol", "eventFlags", "index", "time", "exchangeCode", "price", "size", "bidPrice", "askPrice", "aggressorSide", "extendedTradingHours", "validTick", "type", "exchangeSaleConditions"]
    };

    bool Shows(string conditions, bool valid, double price, double bid, double ask, double last)
    {
        static string Num(double v) => double.IsNaN(v) ? "\"NaN\"" : v.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var sink = new PrintReplaySink(last);
        var frame = $"{{\"type\":\"FEED_DATA\",\"channel\":1,\"data\":[\"TimeAndSale\",[\"TimeAndSale\",\"X\",0,1,1000,\"Q\",{Num(price)},10,{Num(bid)},{Num(ask)},\"UNDEFINED\",false,{(valid ? "true" : "false")},\"NEW\",\"{conditions}\"]]}}";
        new FeedFrameParser(fields).TryParseData(Encoding.UTF8.GetBytes(frame), sink, 0);
        return sink.Counts.Values.Sum(c => c.Valid + c.OddLots) == 1;
    }

    Check(Shows("@   ", true, 10, 9.99, 10.01, double.NaN), "a valid print shows");
    Check(Shows("@  o", false, 16.50, 16.33, 16.54, double.NaN), "a normal odd lot shows");
    Check(Shows("@F o", false, 16.50, 16.33, 16.54, double.NaN), "an intermarket sweep odd lot shows");
    Check(Shows("@ To", false, 16.50, 16.33, 16.54, double.NaN), "an extended-hours odd lot shows");
    Check(Shows("@FTo", false, 16.50, 16.33, 16.54, double.NaN), "an extended-hours sweep odd lot shows");
    foreach (var offMarket in new[] { "@4 o", "@7 o", "@  W", "@4 W", "@7 V", "C  o", "@4 P", "@   " })
        Check(!Shows(offMarket, false, 16.50, 16.33, 16.54, double.NaN), $"an invalid '{offMarket}' print stays hidden");

    Check(Shows("@  o", false, 16.76, 16.33, 16.54, double.NaN), "AEHL's real 1.3% odd lot in a fast move shows");
    Check(!Shows("@  o", false, 772.525, 736.35, 736.4, double.NaN), "SPY's real 4.9% bad odd lot stays hidden");
    Check(!Shows("@  o", false, 700, 736.35, 736.4, double.NaN), "a bad odd lot below the quote stays hidden");
    Check(Shows("@  o", false, 10.1, double.NaN, double.NaN, 10), "with no quote, an odd lot near the last price shows");
    Check(!Shows("@  o", false, 12, double.NaN, double.NaN, 10), "with no quote, an odd lot far from the last price stays hidden");
    Check(!Shows("@  o", false, 10, double.NaN, double.NaN, double.NaN), "with nothing to compare, an odd lot stays hidden");
    Check(Shows("@   ", true, 772.525, 736.35, 736.4, double.NaN), "valid prints aren't held to the guard");

    foreach (var path in captures)
    {
        var parser = new FeedFrameParser(fields);
        var sink = new PrintReplaySink(double.NaN);
        foreach (var line in File.ReadLines(path))
        {
            var payload = line[(line.IndexOf('\t') + 1)..];
            if (payload.Contains("\"FEED_CONFIG\"", StringComparison.Ordinal))
            {
                using var doc = JsonDocument.Parse(payload);
                if (doc.RootElement.TryGetProperty("eventFields", out var ef))
                {
                    foreach (var p in ef.EnumerateObject())
                        parser.UpdateLayout(p.Name, p.Value.EnumerateArray().Select(x => x.GetString()).ToArray());
                }
            }
            else if (payload.Contains("\"TimeAndSale\"", StringComparison.Ordinal))
            {
                parser.TryParseData(Encoding.UTF8.GetBytes(payload), sink, 0);
            }
        }

        Console.WriteLine($"\n{Path.GetFileName(path)}");
        foreach (var (symbol, c) in sink.Counts.OrderByDescending(kv => kv.Value.OddLots))
            Console.WriteLine($"  {symbol,-6} valid {c.Valid,6} + odd lots {c.OddLots,6} = {(c.Valid + c.OddLots) / (double)Math.Max(c.Valid, 1),5:F1}x the prints | too far {c.TooFar,3} | other invalid {c.OtherInvalid,5}");
    }
}

// What the tastytrade token is allowed to do and which feed it gets. Prints no secrets.
async Task RunScope(string credentialsPath)
{
    using var api = new TastytradeApi(credentialsPath);
    Console.WriteLine($"scope: {await api.GetTokenScopeAsync(default)}");
    var quote = await api.GetQuoteTokenAsync(default);
    Console.WriteLine($"quote token: level={quote.Level} url={quote.Url}");
}

// How many book rows Gateway really serves, and how often books reach the sink. Also shows
// whether a symbol's name works on IBKR. Depth is limited to 3 symbols; the rest get quotes only.
async Task RunIbkrRows(int port, int rows, string[] symbols)
{
    var sink = new IbkrTestSink();
    var client = new IbkrClient(new IbkrConfig(true, "127.0.0.1", port, 82, IbkrConfig.Load().AllowLiveAccount, rows));
    client.SetSink(sink);
    client.Start();
    foreach (var symbol in symbols)
        client.SetQuote(symbol, true);
    foreach (var symbol in symbols.Take(3))
        client.SetDepth(symbol, true);

    await Task.Delay(1500);
    var counts = symbols.ToDictionary(s => s, s => sink.DepthCount.GetValueOrDefault(IbkrClient.ToIbkrSymbol(s)));
    const int MeasureSeconds = 15;
    await Task.Delay(MeasureSeconds * 1000);

    foreach (var symbol in symbols)
    {
        var name = IbkrClient.ToIbkrSymbol(symbol);
        var quote = sink.Quotes.TryGetValue(name, out var q) ? $"{q.Bid} x {q.BidSize} / {q.Ask} x {q.AskSize}" : "no quote";
        Console.WriteLine($"{symbol}: quote {quote}");
        if (sink.Depth.TryGetValue(name, out var d))
            Console.WriteLine($"  book {d.Bids} bids, {d.Asks} asks, top {d.BidTop} / {d.AskTop}, {(sink.DepthCount[name] - counts[symbol]) / (double)MeasureSeconds:F1} books a second, venues {string.Join(",", d.Venues)}");
    }

    foreach (var problem in sink.Problems)
        Console.WriteLine($"problem: {problem}");

    await client.StopAsync();
}

// The paper account's positions as IBKR reports them, read-only, on a client ID of its own. IbkrTrader never places
// an order by itself, and this sends none.
async Task RunPositions(int port)
{
    var trader = new IbkrTrader("127.0.0.1", port, 84);
    var reported = new ConcurrentQueue<IbkrPosition>();
    trader.SetSink(new PositionPrinter(reported));
    trader.Start();
    for (int i = 0; i < 40 && !trader.Ready; i++)
        await Task.Delay(250);
    await Task.Delay(2500);
    Console.WriteLine($"ready={trader.Ready} {trader.NotReadyWhy}");
    foreach (var p in reported)
        Console.WriteLine($"  reported {p.Symbol,-6} qty {p.Quantity,8} avg cost {p.AvgCost}");
    foreach (var p in trader.Positions())
        Console.WriteLine($"  open     {p.Symbol,-6} qty {p.Quantity,8} avg cost {p.AvgCost}");
    await trader.StopAsync();
}

// The last few all-hours daily bars of each symbol, read-only.
async Task RunDayRange(string[] symbols)
{
    using var client = api();
    var history = new HistoryClient(async (force, ct) => await client.GetQuoteTokenAsync(ct));
    var now = DateTime.UtcNow;
    foreach (var symbol in symbols)
    {
        var bars = await history.LoadAsync(symbol + "{=1d}", now - TimeSpan.FromDays(6), now, CancellationToken.None);
        foreach (var bar in bars.TakeLast(3))
            Console.WriteLine($"{symbol} {DateTimeOffset.FromUnixTimeMilliseconds(bar.TimeMs):MM-dd} open {bar.Open} high {bar.High} low {bar.Low} close {bar.Close}");
    }

    await history.StopAsync();
}

// One symbol's whole IBKR depth book, every row as the client delivers it, read-only: to see what a book that
// crashed Quantower (VIVS 2026-11-20, YFOR 11-19) holds.
async Task RunIbkrBook(int port, string symbol, int seconds)
{
    var sink = new IbkrTestSink();
    var client = new IbkrClient(new IbkrConfig(true, "127.0.0.1", port, 83, IbkrConfig.Load().AllowLiveAccount, 50));
    client.SetSink(sink);
    client.Start();
    client.SetQuote(symbol, true);
    client.SetDepth(symbol, true);
    await Task.Delay(seconds * 1000);

    var name = IbkrClient.ToIbkrSymbol(symbol);
    Console.WriteLine(sink.Quotes.TryGetValue(name, out var q) ? $"{symbol} quote {q.Bid} x {q.BidSize} / {q.Ask} x {q.AskSize}" : $"{symbol}: no quote");
    if (sink.Books.TryGetValue(name, out var book))
    {
        foreach (var (side, rows) in new[] { ("bid", book.Bids), ("ask", book.Asks) })
        {
            Console.WriteLine($"{rows.Count} {side}s, prices {(rows.Count > 0 ? $"{rows.Min(r => r.Price)} to {rows.Max(r => r.Price)}" : "none")}");
            foreach (var row in rows)
                Console.WriteLine($"  {side} {row.Price,12} x {row.Size,10} {row.Venue}");
        }
    }
    else
    {
        Console.WriteLine("no book");
    }

    foreach (var problem in sink.Problems)
        Console.WriteLine($"problem: {problem}");
    await client.StopAsync();
}

// tastytrade's REST quotes, alongside the live DXLink quote for the same moment, to see
// whether the two sources disagree on the best bid and ask.
async Task RunRestQuote(string symbols, int samples)
{
    using var client = api();
    var fields = new Dictionary<string, string[]> { ["Quote"] = ["eventType", "eventSymbol", "bidPrice", "bidSize", "askPrice", "askSize"] };
    var sink = new LatestQuoteSink();
    var link = new DxLinkLink("restquote", (force, ct) => client.GetQuoteTokenAsync(ct), fields, sink);
    var list = symbols.Split(',');
    link.Ready += () => _ = link.SendSubscriptionAsync(list.Select(s => new FeedKey("Quote", s)).ToList(), null, true);
    link.Start();
    Check(await link.WaitReadyAsync(TimeSpan.FromSeconds(15), CancellationToken.None), "stream ready");
    await Task.Delay(1500);

    var firstKeys = true;
    var spreads = list.ToDictionary(s => s, _ => (FromRest: new List<double>(), FromStream: new List<double>(), Same: 0));
    for (int i = 0; i < samples; i++)
    {
        var raw = await client.GetRawAsync("/market-data/by-type?equity=" + symbols, CancellationToken.None);
        using var doc = JsonDocument.Parse(raw);
        foreach (var item in doc.RootElement.GetProperty("data").GetProperty("items").EnumerateArray())
        {
            if (firstKeys)
            {
                Console.WriteLine("REST fields: " + string.Join(", ", item.EnumerateObject().Select(p => p.Name)));
                firstKeys = false;
            }

            var sym = item.GetProperty("symbol").GetString();
            double rb = double.Parse(item.GetProperty("bid").GetString()), ra = double.Parse(item.GetProperty("ask").GetString());
            var live = sink.Latest.GetValueOrDefault(sym);
            var entry = spreads[sym];
            entry.FromRest.Add(ra - rb);
            entry.FromStream.Add(live.Ask - live.Bid);
            if (Math.Abs(ra - live.Ask) < 1e-6 && Math.Abs(rb - live.Bid) < 1e-6)
                entry.Same++;
            spreads[sym] = entry;
            if (i < 3)
                Console.WriteLine($"  {sym,-5} REST {rb}/{ra}  stream {live.Bid}/{live.Ask}");
        }

        await Task.Delay(1000);
    }

    foreach (var (sym, e) in spreads)
        Console.WriteLine($"RESULT {sym,-5} samples {e.FromRest.Count}: median spread REST {e.FromRest.Order().ElementAt(e.FromRest.Count / 2):F4}, stream {e.FromStream.Order().ElementAt(e.FromStream.Count / 2):F4}; identical bid and ask {e.Same} times");
    await link.StopAsync();
}

// Alarm logic on short timings with the speaker swapped out, then one real play of each sound.
async Task RunAlarmTest()
{
    int warnings = 0, allClears = 0;
    FeedAlarm.TestPlayer = warning =>
    {
        if (warning)
            Interlocked.Increment(ref warnings);
        else
            Interlocked.Increment(ref allClears);
    };

    // Muted by default (your request, 2026-11-23): an alarm still turns on and off, but nothing reaches the speaker.
    Check(FeedAlarm.Muted, "sounds are muted by default");
    FeedAlarm.Raise("muted");
    FeedAlarm.PlayWarning();
    FeedAlarm.Clear();
    FeedAlarm.PlayAllClear();
    Check(warnings == 0 && allClears == 0 && !FeedAlarm.IsActive, "muted, an alarm and its all-clear play nothing");

    // The rest is the alarm's timing with the sound on, as it would be if unmuted.
    FeedAlarm.Muted = false;
    FeedAlarm.RepeatEvery = TimeSpan.FromMilliseconds(100);
    FeedAlarm.GiveUpAfter = TimeSpan.FromMilliseconds(600);

    FeedAlarm.Clear();
    Check(allClears == 0 && !FeedAlarm.IsActive, "clearing with no alarm on is silent");

    FeedAlarm.Raise("test");
    Check(FeedAlarm.IsActive && warnings == 1, "raising sounds once right away");
    FeedAlarm.Raise("test again");
    Check(warnings == 1, "raising again doesn't restart it");

    for (int i = 0; i < 8; i++)
    {
        await Task.Delay(100);
        FeedAlarm.KeepAlive();
    }

    Check(FeedAlarm.IsActive && warnings >= 4, $"repeats while kept alive ({warnings} warnings in 800 ms)");

    FeedAlarm.Clear();
    int warningsAtClear = warnings;
    Check(!FeedAlarm.IsActive && allClears == 1, "clearing sounds the all-clear once");
    FeedAlarm.Clear();
    await Task.Delay(300);
    Check(allClears == 1 && warnings <= warningsAtClear + 1, "no more sounds after clearing");

    allClears = 0;
    FeedAlarm.Raise("left alone");
    await Task.Delay(1000);
    Check(!FeedAlarm.IsActive, "gives up when nothing keeps it alive");
    Check(allClears == 0, "giving up isn't an all-clear");

    // Late in the day this limit isn't what matters; markets shut is what silences alarms.
    Check(FeedHealth.SilenceLimit(new DateTime(2026, 11, 7, 17, 0, 0, DateTimeKind.Utc)) == null, "Saturday counts as markets shut");

    // The tones as sound (ToneWav), since Windows' Beep has no volume: a quarter of full scale, the same pitches and lengths.
    (uint, uint)[] tones = [(1000, 150), (700, 150)];
    var wav = ToneWav.Build(tones, FeedAlarm.Loudness);
    int dataBytes = BitConverter.ToInt32(wav, 40);
    var pcm = Enumerable.Range(0, dataBytes / 2).Select(i => BitConverter.ToInt16(wav, 44 + i * 2)).ToArray();
    Check(Encoding.ASCII.GetString(wav, 0, 4) == "RIFF" && Encoding.ASCII.GetString(wav, 8, 4) == "WAVE" && BitConverter.ToInt16(wav, 22) == 1
          && BitConverter.ToInt32(wav, 24) == ToneWav.SampleRate && BitConverter.ToInt16(wav, 34) == 16 && wav.Length == 44 + dataBytes,
        "the sound is a 16-bit mono 44.1 kHz WAV");
    Check(pcm.Length == ToneWav.SampleRate * 300 / 1000, $"it lasts as long as the tones, 300 ms ({pcm.Length} samples)");
    int loudest = pcm.Max(v => Math.Abs((int)v));
    Check(Math.Abs(loudest - FeedAlarm.Loudness * short.MaxValue) < 30, $"its peak is a quarter of full scale ({loudest} of {short.MaxValue})");
    Check(Math.Abs(pcm[0]) < 5 && Math.Abs(pcm[^1]) < 5, "each end fades from silence, so it doesn't click");
    int Crossings(int from, int count) => Enumerable.Range(from + 1, count - 1).Count(i => (pcm[i - 1] < 0) != (pcm[i] < 0));
    int first = Crossings(0, pcm.Length / 2), second = Crossings(pcm.Length / 2, pcm.Length / 2);
    Check(Math.Abs(first - 300) <= 3 && Math.Abs(second - 210) <= 3, $"the pitches are 1000 Hz then 700 Hz ({first} and {second} zero crossings in 150 ms)");
    // Played at zero loudness, so nothing is heard: Windows takes the format (SND_MEMORY | SND_SYNC | SND_NODEFAULT).
    Check(SilentSound.Play(ToneWav.Build(tones, 0)), "Windows plays it (silently here)");

    // No real sound is played here any more: you asked for none (2026-11-23).
    FeedAlarm.TestPlayer = null;
    FeedAlarm.Muted = true;
}

// Back-to-back loads of very differently priced symbols. A live update left over from the
// previous request must never end up as a bar on the next chart. Needs an active market.
async Task RunCrossTest()
{
    using var client = api();
    var history = new HistoryClient(async (force, ct) => await client.GetQuoteTokenAsync(ct));
    var now = DateTime.UtcNow;
    int badSpyBars = 0, badPlugBars = 0, loads = 0;

    for (int round = 0; round < 12; round++)
    {
        var plug = await history.LoadAsync("PLUG{=1m}", now.AddHours(-3), DateTime.UtcNow, CancellationToken.None);
        var spy = await history.LoadAsync("SPY{=1m}", now.AddHours(-3), DateTime.UtcNow, CancellationToken.None);
        badPlugBars += plug.Count(b => b.Close > 100);
        badSpyBars += spy.Count(b => b.Close < 100);
        loads += 2;
        Check(plug.Count > 0 && spy.Count > 0, $"round {round}: both loads returned bars ({plug.Count} PLUG, {spy.Count} SPY)");
    }

    // Different periods of one symbol, where a wrong bar shows up as a time off the grid.
    var daily = await history.LoadAsync("SPY{=1d}", now.AddDays(-30), DateTime.UtcNow, CancellationToken.None);
    var weekly = await history.LoadAsync("SPY{=1w}", now.AddDays(-400), DateTime.UtcNow, CancellationToken.None);
    var weeklyTimes = weekly.Select(b => DateTimeOffset.FromUnixTimeMilliseconds(b.TimeMs).DayOfWeek).Distinct().ToList();
    Console.WriteLine($"daily bars {daily.Count}, weekly bars {weekly.Count}, weekly bars fall on: {string.Join(",", weeklyTimes)}");
    Check(weeklyTimes.Count == 1, "weekly bars all start on the same weekday, so no daily bar leaked in");

    Console.WriteLine($"{loads} loads: PLUG bars at SPY prices {badPlugBars}, SPY bars at PLUG prices {badSpyBars}");
    Check(badPlugBars == 0 && badSpyBars == 0, "no bars leaked between symbols");
    await history.StopAsync();
}

// Daily Candle volume, to compare with a whole-market figure and see how much of the market candles cover.
async Task RunDayVolume(string[] symbols)
{
    using var client = api();
    var history = new HistoryClient(async (force, ct) => await client.GetQuoteTokenAsync(ct));
    var now = DateTime.UtcNow;
    foreach (var symbol in symbols)
    {
        foreach (var code in new[] { "=1d", "=1d,tho=true" })
        {
            var bars = await history.LoadAsync(symbol + "{" + code + "}", now.AddDays(-6), now, CancellationToken.None);
            var text = string.Join("  ", bars.TakeLast(3).Select(b => $"{DateTimeOffset.FromUnixTimeMilliseconds(b.TimeMs):MM-dd}={b.Volume:F0}"));
            Console.WriteLine($"RESULT {symbol,-5} {code,-13} {text}");
        }

        // Sum Wednesday 2026-11-04's 1-minute bars by session, from 8 PM ET Tuesday to 8 PM ET Wednesday.
        var from = new DateTime(2026, 11, 4, 1, 0, 0, DateTimeKind.Utc);
        var to = new DateTime(2026, 11, 5, 1, 0, 0, DateTimeKind.Utc);
        var minutes = await history.LoadAsync(symbol + "{=1m}", from, to, CancellationToken.None);
        double overnight = 0, pre = 0, regular = 0, post = 0;
        foreach (var bar in minutes)
        {
            var et = DateTimeOffset.FromUnixTimeMilliseconds(bar.TimeMs).ToOffset(TimeSpan.FromHours(-5));
            int m = (et.Hour * 60) + et.Minute;
            if (et.Day == 3 || m < 240)
                overnight += bar.Volume;
            else if (m < 570)
                pre += bar.Volume;
            else if (m < 960)
                regular += bar.Volume;
            else
                post += bar.Volume;
        }

        Console.WriteLine($"RESULT {symbol,-5} 1m sum 11-04: overnight={overnight:F0} pre={pre:F0} regular={regular:F0} post={post:F0} total={overnight + pre + regular + post:F0} bars={minutes.Count}");

        // The plugin merges volume onto regular-hours bars by bar time, so the times must line up.
        foreach (var (code, days) in new[] { ("=1d", 60), ("=1w", 400), ("=1mo", 1000) })
        {
            var regularBars = await history.LoadAsync(symbol + "{" + code + ",tho=true}", now.AddDays(-days), now, CancellationToken.None);
            var allBars = await history.LoadAsync(symbol + "{" + code + "}", now.AddDays(-days), now, CancellationToken.None);
            var times = allBars.Select(b => b.TimeMs).ToHashSet();
            int matched = regularBars.Count(b => times.Contains(b.TimeMs));
            Check(regularBars.Count > 0 && matched == regularBars.Count, $"{symbol} {code}: {matched} of {regularBars.Count} regular-hours bars match an all-hours bar");
        }
    }

    await history.StopAsync();
}

// Stopping while the link is between retries once threw and left the history socket running.
async Task RunStopTest()
{
    using var client = api();
    QuoteToken token = null;
    bool outage = false;
    async Task<QuoteToken> GetToken(bool force, CancellationToken ct)
    {
        if (outage)
            throw new HttpRequestException("test outage");
        return token ??= await client.GetQuoteTokenAsync(ct);
    }

    var fields = new Dictionary<string, string[]> { ["Quote"] = ["eventType", "eventSymbol", "bidPrice", "askPrice"] };
    var link = new DxLinkLink("stoptest", GetToken, fields, new CountingSink());
    link.Start();
    Check(await link.WaitReadyAsync(TimeSpan.FromSeconds(15), CancellationToken.None), "link ready");

    outage = true;
    link.ForceReconnect("harness");
    await Task.Delay(1500);
    Check(link.State == LinkState.Retrying, $"link is between retries (state {link.State})");

    Exception thrown = null;
    try
    {
        await link.StopAsync();
    }
    catch (Exception ex)
    {
        thrown = ex;
    }

    Check(thrown == null, $"stopping between retries doesn't throw ({thrown?.GetType().Name})");
    Check(link.State == LinkState.Stopped, "link reports stopped");

    // Forcing a reconnect after it has stopped must also be harmless.
    link.ForceReconnect("after stop");
    Check(link.State == LinkState.Stopped, "force reconnect after stop is a no-op");
}

async Task RunLive(string[] symbols, int seconds)
{
    using var client = api();
    QuoteToken token = null;
    var tokenFetches = 0;
    async Task<QuoteToken> GetToken(bool force, CancellationToken ct)
    {
        if (token == null || force)
        {
            token = await client.GetQuoteTokenAsync(ct);
            tokenFetches++;
        }

        return token;
    }

    var fields = new Dictionary<string, string[]>
    {
        ["Quote"] = ["eventType", "eventSymbol", "bidTime", "askTime", "bidPrice", "bidSize", "askPrice", "askSize"],
        ["TimeAndSale"] = ["eventType", "eventSymbol", "eventFlags", "index", "time", "exchangeCode", "price", "size", "bidPrice", "askPrice", "aggressorSide", "extendedTradingHours", "validTick", "type", "exchangeSaleConditions"],
        ["Trade"] = ["eventType", "eventSymbol", "time", "price", "size", "dayVolume", "extendedTradingHours"],
        ["Summary"] = ["eventType", "eventSymbol", "dayOpenPrice", "dayHighPrice", "dayLowPrice", "dayClosePrice", "prevDayClosePrice", "prevDayVolume"]
    };

    var sink = new CountingSink();
    var health = new FeedHealth();
    sink.OnCanary = () => health.OnData(Stopwatch.GetTimestamp());
    var book = new SubscriptionBook();
    book.Pin(new FeedKey("Quote", FeedHealth.CanarySymbol));
    foreach (var symbol in symbols)
    {
        foreach (var type in new[] { "Quote", "TimeAndSale", "Trade", "Summary" })
            book.Set(new FeedKey(type, symbol), true);
    }

    var link = new DxLinkLink("live", GetToken, fields, sink);
    var recovered = new TaskCompletionSource<TimeSpan>(TaskCreationOptions.RunContinuationsAsynchronously);
    link.Ready += () => _ = link.SendSubscriptionAsync(book.TakeAll(), null, true);
    link.Recovered += (dropped, back) => recovered.TrySetResult(back - dropped);
    link.Start();

    Check(await link.WaitReadyAsync(TimeSpan.FromSeconds(15), CancellationToken.None), "link ready");
    Check(link.ConfirmedAggregation == 0, $"server confirmed aggregation 0 (got {link.ConfirmedAggregation})");

    await Task.Delay(TimeSpan.FromSeconds(seconds / 3));
    long quotesBefore = sink.Quotes;
    Console.WriteLine($"after {seconds / 3}s: {sink}");
    Check(sink.Quotes > 0, "quotes arriving");

    // Chart-open churn: on, off, on again within milliseconds should send nothing.
    Check(!book.TakeDiff(out _, out _), "no pending changes before churn test");
    var churn = new FeedKey("Quote", symbols[0]);
    book.Set(churn, false);
    book.Set(churn, true);
    Check(!book.TakeDiff(out _, out _), "chart-open churn produces no subscription change");

    // A real change is sent as a diff.
    // Pick a symbol that can't already be in this run's list.
    var extra = new FeedKey("Quote", symbols.Contains("IWM") ? "DIA" : "IWM");
    book.Set(extra, true);
    Check(book.TakeDiff(out var add, out var remove) && add.Count == 1 && remove.Count == 0, "a real change is sent as a one-item diff");
    await link.SendSubscriptionAsync(add, null, false);
    await Task.Delay(1500);
    book.Set(extra, false);
    Check(book.TakeDiff(out add, out remove) && remove.Count == 1, "removal is sent as a diff");
    await link.SendSubscriptionAsync(null, remove, false);

    // The canary stays subscribed even if the platform unsubscribes that symbol.
    book.Set(new FeedKey("Quote", FeedHealth.CanarySymbol), false);
    Check(!book.TakeDiff(out _, out _), "canary stays subscribed when the platform drops it");

    // Present a new token on the open socket.
    var fresh = await client.GetQuoteTokenAsync(CancellationToken.None);
    var quotesAtAuth = sink.Quotes;
    await link.ReauthorizeAsync(fresh);
    await Task.Delay(4000);
    Check(link.State == LinkState.Ready, $"socket survived a re-auth (state {link.State})");
    Check(sink.Quotes > quotesAtAuth, $"quotes kept flowing after re-auth ({sink.Quotes - quotesAtAuth} more)");

    // Kill the socket and time the recovery.
    long quotesAtDrop = sink.Quotes;
    long printsAtDrop = sink.Prints;
    var dropTimer = Stopwatch.StartNew();
    link.ForceReconnect("harness drop test");
    var gap = await recovered.Task.WaitAsync(TimeSpan.FromSeconds(30));
    Console.WriteLine($"reconnect: feed usable after {gap.TotalMilliseconds:F0} ms (wall {dropTimer.ElapsedMilliseconds} ms)");
    Check(gap < TimeSpan.FromSeconds(5), "reconnect under 5 seconds");
    while (sink.Quotes == quotesAtDrop && dropTimer.Elapsed < TimeSpan.FromSeconds(20))
        await Task.Delay(10);
    Console.WriteLine($"first quote after drop: {dropTimer.ElapsedMilliseconds} ms");
    Check(sink.Quotes > quotesAtDrop, "quotes resumed after reconnect");

    await Task.Delay(TimeSpan.FromSeconds(seconds / 3));
    Console.WriteLine($"prints before drop {printsAtDrop}, after drop {sink.Prints - printsAtDrop}");
    Check(sink.Prints > printsAtDrop, "prints resumed after reconnect");
    health.Reset();
    await Task.Delay(1000);
    var age = health.Silence(Stopwatch.GetTimestamp());
    Console.WriteLine($"canary age {age.TotalSeconds:F1}s, limit now {FeedHealth.SilenceLimit(DateTime.UtcNow)}");

    Console.WriteLine($"final: {sink}");
    Console.WriteLine($"frames={link.TakeFrameCount()} maxFrameHandleUs={link.TakeMaxHandleMicros()} tokenFetches={tokenFetches} state={link.State}");
    await link.StopAsync();
    Check(link.State == LinkState.Stopped, "link stops cleanly");

    // Session limits by Eastern time. All of these are Wed 2026-11-04 (EST, UTC-5) unless noted.
    var et = (int hour, int minute, int day) => new DateTime(2026, 11, day, 0, 0, 0, DateTimeKind.Utc).AddHours(hour + 5).AddMinutes(minute);
    Check(FeedHealth.SilenceLimit(et(10, 0, 4)) == TimeSpan.FromSeconds(5), "regular hours limit 5s");
    Check(FeedHealth.SilenceLimit(et(5, 0, 4)) == TimeSpan.FromSeconds(15), "premarket limit 15s");
    Check(FeedHealth.SilenceLimit(et(17, 0, 4)) == TimeSpan.FromSeconds(60), "after-hours limit 60s, now that any data counts");
    Check(FeedHealth.SilenceLimit(et(1, 0, 4)) == TimeSpan.FromSeconds(120), "overnight limit 120s");
    Check(FeedHealth.SilenceLimit(et(12, 0, 7)) == null, "Saturday off");
    Check(FeedHealth.SilenceLimit(et(19, 0, 8)) == null, "Sunday before 8 PM ET off");
    Check(FeedHealth.SilenceLimit(et(21, 0, 6)) == null, "Friday after 8 PM ET off");
}

internal sealed class TnsCase
{
    public int Rows;
    public int LiveRows;
    public int OlderRows;
    public int Flags;
    public double FirstRowMs;
    public double MinTime = double.MaxValue;
    public double MaxTime = double.MinValue;
    public readonly HashSet<string> Codes = new();
}

/// <summary>Applies the tape's filter the way the plugin does, and counts the outcome per symbol.</summary>
internal sealed class PrintReplaySink : IFeedSink
{
    private readonly Dictionary<string, double> last = new();
    private readonly double startingLast;

    public PrintReplaySink(double startingLast) => this.startingLast = startingLast;

    public sealed class Tally
    {
        public int Valid;
        public int OddLots;
        public int TooFar;
        public int OtherInvalid;
    }

    public Dictionary<string, Tally> Counts { get; } = new();

    public void OnPrint(FeedRow row)
    {
        if ((row.Flags & EventFlags.Remove) != 0 || row.Sale != SaleKind.New || double.IsNaN(row.Price))
            return;

        var symbol = row.Symbol.Name;
        if (!this.Counts.TryGetValue(symbol, out var tally))
            this.Counts[symbol] = tally = new Tally();

        if (PrintFilter.Shows(row, this.last.GetValueOrDefault(symbol, this.startingLast)))
        {
            this.last[symbol] = row.Price;
            if (row.ValidTick)
                tally.Valid++;
            else
                tally.OddLots++;
        }
        else if (row.NormalOddLot)
        {
            tally.TooFar++;
        }
        else
        {
            tally.OtherInvalid++;
        }
    }

    public void OnQuote(FeedRow row)
    {
    }

    public void OnTrade(FeedRow row)
    {
    }

    public void OnSummary(FeedRow row)
    {
    }

    public void OnCandle(FeedRow row)
    {
    }
}

internal sealed class IbkrTestSink : IIbkrSink
{
    public readonly System.Collections.Concurrent.ConcurrentDictionary<string, (double Bid, double BidSize, double Ask, double AskSize)> Quotes = new();
    public readonly System.Collections.Concurrent.ConcurrentDictionary<string, int> QuoteCount = new();
    public readonly System.Collections.Concurrent.ConcurrentDictionary<string, DepthView> Depth = new();
    public readonly System.Collections.Concurrent.ConcurrentDictionary<string, int> DepthCount = new();
    public readonly System.Collections.Concurrent.ConcurrentBag<string> Problems = new();
    public readonly System.Collections.Concurrent.ConcurrentBag<IbkrState> States = new();

    public sealed record DepthView(int Bids, int Asks, HashSet<string> Venues, double BidTop, double BidTopSize, double AskTop, double AskTopSize);

    public void OnIbkrQuote(string symbol, double bid, double bidSize, double ask, double askSize)
    {
        this.Quotes[symbol] = (bid, bidSize, ask, askSize);
        this.QuoteCount.AddOrUpdate(symbol, 1, (_, n) => n + 1);
    }

    /// <summary>Each symbol's last whole book, as the client delivered it.</summary>
    public readonly System.Collections.Concurrent.ConcurrentDictionary<string, (List<IbkrDepthRow> Bids, List<IbkrDepthRow> Asks)> Books = new();

    public void OnIbkrDepth(string symbol, List<IbkrDepthRow> bids, List<IbkrDepthRow> asks)
    {
        this.Books[symbol] = (bids.ToList(), asks.ToList());
        var venues = bids.Concat(asks).Select(r => r.Venue).ToHashSet();
        this.Depth[symbol] = new DepthView(
            bids.Count,
            asks.Count,
            venues,
            bids.Count > 0 ? bids[0].Price : double.NaN,
            bids.Count > 0 ? bids[0].Size : double.NaN,
            asks.Count > 0 ? asks[0].Price : double.NaN,
            asks.Count > 0 ? asks[0].Size : double.NaN);
        this.DepthCount.AddOrUpdate(symbol, 1, (_, n) => n + 1);
    }

    /// <summary>Only touched on the client's receive thread until the client has stopped.</summary>
    public Dictionary<string, List<(double Price, double Size, string Venue, long Seconds, long ArrivalTicks)>> Prints { get; } = new();

    public readonly System.Collections.Concurrent.ConcurrentQueue<(string Symbol, int Halted)> Halts = new();

    public void OnIbkrHalted(string symbol, int halted) => this.Halts.Enqueue((symbol, halted));

    public readonly System.Collections.Concurrent.ConcurrentQueue<string> QuoteStops = new();

    public void OnIbkrQuoteStopped(string symbol) => this.QuoteStops.Enqueue(symbol);

    public readonly System.Collections.Concurrent.ConcurrentQueue<IReadOnlyList<string>> Scans = new();

    public void OnIbkrScan(IReadOnlyList<string> symbols) => this.Scans.Enqueue(symbols);

    public readonly System.Collections.Concurrent.ConcurrentQueue<IReadOnlyList<string>> HaltScans = new();

    public void OnIbkrHaltScan(IReadOnlyList<string> symbols) => this.HaltScans.Enqueue(symbols);

    public readonly ConcurrentQueue<(string Symbol, int Tick, double Value)> Auctions = new();

    public bool OnIbkrAuction(string symbol, int tick, double value)
    {
        this.Auctions.Enqueue((symbol, tick, value));
        return true;
    }

    public readonly System.Collections.Concurrent.ConcurrentDictionary<string, int> OddLots = new();

    public readonly System.Collections.Concurrent.ConcurrentDictionary<string, int> Reopenings = new();

    public void OnIbkrPrint(string symbol, double price, double size, long unixSeconds, string venue, long arrivalUtcTicks, bool roundLot, bool reopening)
    {
        if (!roundLot)
            this.OddLots.AddOrUpdate(symbol, 1, (_, n) => n + 1);
        if (reopening)
            this.Reopenings.AddOrUpdate(symbol, 1, (_, n) => n + 1);
        if (!this.Prints.TryGetValue(symbol, out var list))
            this.Prints[symbol] = list = new();
        list.Add((price, size, venue, unixSeconds, arrivalUtcTicks));
    }

    public readonly System.Collections.Concurrent.ConcurrentDictionary<string, int> TapeActive = new();

    public void OnIbkrTapeActive(string symbol, double price, double size, long unixSeconds, bool carried)
    {
        this.TapeActive.AddOrUpdate(symbol, 1, (_, n) => n + 1);
        if (carried)
            this.TapeCarried.AddOrUpdate(symbol, 1, (_, n) => n + 1);
    }

    public readonly System.Collections.Concurrent.ConcurrentDictionary<string, int> TapeCarried = new();

    public void OnIbkrState(IbkrState state, string detail) => this.States.Add(state);

    public void OnIbkrProblem(string symbol, string feed, int code, string message) => this.Problems.Add($"{symbol} {feed} {code}: {message}");
}

/// <summary>
/// Stands in for Gateway on a local port: just enough of its protocol to log what the client sends
/// and answer depth requests the way a test asks. It never talks to IBKR.
/// </summary>
internal sealed class FakeGateway : IDisposable
{
    private readonly System.Net.Sockets.TcpListener listener = new(System.Net.IPAddress.Loopback, 0);
    private readonly CancellationTokenSource stop = new();
    private readonly Stopwatch clock = Stopwatch.StartNew();
    private readonly object gate = new();
    private readonly HashSet<int> unanswered = new();
    private System.Net.Sockets.NetworkStream current;

    public FakeGateway()
    {
        this.listener.Start();
        _ = Task.Run(this.AcceptAsync);
    }

    public enum Answer { Row, Refused, Silent }

    public int Port => ((System.Net.IPEndPoint)this.listener.LocalEndpoint).Port;

    public ConcurrentDictionary<string, (Answer Answer, int DelayMs)> Policy { get; } = new();

    public List<(string Symbol, int Id, long Ms)> DepthRequests { get; } = new();

    public List<(int Id, string IsSmart, long Ms)> DepthCancels { get; } = new();

    public int MostUnanswered { get; private set; }

    public int TapeRequestId { get; private set; }

    /// <summary>By symbol: how to answer a history request, the bars' fields, and how many bars that is.</summary>
    public ConcurrentDictionary<string, (Answer Answer, string Rows, int Count)> HistoryPolicy { get; } = new();

    public List<string[]> HistoryRequests { get; } = new();

    public List<int> HistoryCancels { get; } = new();

    /// <summary>By "SYMBOL@EXCHANGE": a directed quote's answer, or an error code to refuse it with. No entry means no market.</summary>
    public ConcurrentDictionary<string, (double Bid, double BidSize, double Ask, double AskSize, int Error)> VenueQuotes { get; } = new();

    public ConcurrentQueue<(int Id, string Symbol, string Exchange)> MarketDataRequests { get; } = new();

    public ConcurrentQueue<int> MarketDataCancels { get; } = new();

    /// <summary>Each market data request's generic tick list, by request id.</summary>
    public ConcurrentDictionary<int, string> GenericTicks { get; } = new();

    /// <summary>An auction number on the symbol's SMART quote: tick 35 as a tickPrice, 34 and 36 as tickSizes.</summary>
    public void SendAuction(string symbol, int tick, double value)
    {
        int id = this.MarketDataRequests.Last(r => r.Symbol == symbol && r.Exchange == "SMART").Id;
        string text = value.ToString(CultureInfo.InvariantCulture);
        this.Send(tick == 35 ? $"1\06\0{id}\035\0{text}\00\00\0" : $"2\06\0{id}\0{tick}\0{text}\0");
    }

    /// <summary>IBKR's Halted tick (49) on the symbol's SMART quote, as a tickGeneric message.</summary>
    public void SendHalted(string symbol, int value)
    {
        int id = this.MarketDataRequests.Last(r => r.Symbol == symbol && r.Exchange == "SMART").Id;
        this.Send($"45\06\0{id}\049\0{value}\0");
    }

    public void SendVenueQuote(string symbol, string exchange, double bid, double bidSize, double ask, double askSize)
    {
        int id = this.MarketDataRequests.Last(r => r.Symbol == symbol && r.Exchange == exchange).Id;
        this.SendQuote(id, bid, bidSize, ask, askSize);
    }

    public void ClearLog()
    {
        lock (this.gate)
        {
            this.DepthRequests.Clear();
            this.DepthCancels.Clear();
            this.unanswered.Clear();
            this.MostUnanswered = 0;
        }
    }

    public void SendPrint(string conditions) =>
        this.Send($"99\0{this.TapeRequestId}\0{2}\0{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}\0{5.25.ToString(CultureInfo.InvariantCulture)}\0100\00\0NASDAQ\0{conditions}\0");

    public void Dispose()
    {
        this.stop.Cancel();
        this.listener.Stop();
    }

    private void Send(string text)
    {
        var body = Encoding.ASCII.GetBytes(text);
        var frame = new byte[body.Length + 4];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(frame, body.Length);
        body.CopyTo(frame, 4);
        lock (this.gate)
            this.current?.Write(frame);
    }

    private async Task AcceptAsync()
    {
        try
        {
            while (!this.stop.IsCancellationRequested)
            {
                var tcp = await this.listener.AcceptTcpClientAsync(this.stop.Token);
                _ = Task.Run(() => this.ServeAsync(tcp));
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or System.Net.Sockets.SocketException)
        {
        }
    }

    private async Task ServeAsync(System.Net.Sockets.TcpClient tcp)
    {
        using (tcp)
        {
            try
            {
                var net = tcp.GetStream();
                var prefix = new byte[4];
                await net.ReadExactlyAsync(prefix, this.stop.Token);
                await ReadFrameAsync(net, this.stop.Token);
                lock (this.gate)
                {
                    this.current = net;
                    this.unanswered.Clear();
                }

                this.Send("176\020261106 01:00:00\0");
                while (true)
                {
                    var f = (await ReadFrameAsync(net, this.stop.Token)).Split('\0');
                    switch (f[0])
                    {
                        case "71":
                            this.Send("15\01\0DU000000\0");
                            break;
                        case "1":
                            this.GenericTicks[int.Parse(f[2], CultureInfo.InvariantCulture)] = f[16];
                            this.OnMarketDataRequest(int.Parse(f[2], CultureInfo.InvariantCulture), f[4], f[10]);
                            break;
                        case "2":
                            this.MarketDataCancels.Enqueue(int.Parse(f[2], CultureInfo.InvariantCulture));
                            break;
                        case "10":
                            this.OnDepthRequest(int.Parse(f[2], CultureInfo.InvariantCulture), f[4]);
                            break;
                        case "11":
                            lock (this.gate)
                            {
                                int id = int.Parse(f[2], CultureInfo.InvariantCulture);
                                this.unanswered.Remove(id);
                                this.DepthCancels.Add((id, f[3], this.clock.ElapsedMilliseconds));
                            }

                            break;
                        case "97":
                            this.TapeRequestId = int.Parse(f[1], CultureInfo.InvariantCulture);
                            break;
                        case "20":
                            this.OnHistoryRequest(f);
                            break;
                        case "25":
                            lock (this.gate)
                                this.HistoryCancels.Add(int.Parse(f[2], CultureInfo.InvariantCulture));
                            break;
                        case "22":
                            this.ScanRequests.Enqueue(f);
                            break;
                        case "23":
                            this.ScanCancels.Enqueue(int.Parse(f[2], CultureInfo.InvariantCulture));
                            break;
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or EndOfStreamException or OperationCanceledException or ObjectDisposedException)
            {
            }
        }
    }

    /// <summary>Each reqScannerSubscription's fields, as sent.</summary>
    public ConcurrentQueue<string[]> ScanRequests { get; } = new();

    /// <summary>Each cancelScannerSubscription's request id.</summary>
    public ConcurrentQueue<int> ScanCancels { get; } = new();

    /// <summary>A scannerData answer: each row a symbol and its type, ranked in the order given.</summary>
    public void SendScan(int id, params (string Symbol, string Type)[] rows)
    {
        var text = new StringBuilder($"20\03\0{id}\0{rows.Length}\0");
        for (int i = 0; i < rows.Length; i++)
            text.Append($"{i}\0{1000 + i}\0{rows[i].Symbol}\0{rows[i].Type}\0\0\0\0SMART\0USD\0{rows[i].Symbol}\0NMS\0{rows[i].Symbol}\0\0\0\0\0");
        this.Send(text.ToString());
    }

    /// <summary>An error message on a request, as Gateway sends one.</summary>
    public void SendError(int id, int code, string text) =>
        this.Send(string.Join((char)0, "4", "2", id.ToString(CultureInfo.InvariantCulture), code.ToString(CultureInfo.InvariantCulture), text) + (char)0);

    private void OnMarketDataRequest(int id, string symbol, string exchange)
    {
        this.MarketDataRequests.Enqueue((id, symbol, exchange));
        if (exchange == "SMART")
        {
            // A real-looking quote, so the client goes live on its canary.
            this.SendQuote(id, 100, 10, 101, 10);
            return;
        }

        if (!this.VenueQuotes.TryGetValue($"{symbol}@{exchange}", out var answer))
            this.SendQuote(id, -1, 0, -1, 0);
        else if (answer.Error != 0)
            this.Send($"4\02\0{id}\0{answer.Error}\0Requested market data is not subscribed.\0");
        else
            this.SendQuote(id, answer.Bid, answer.BidSize, answer.Ask, answer.AskSize);
    }

    private void SendQuote(int id, double bid, double bidSize, double ask, double askSize)
    {
        string Text(double value) => value.ToString(CultureInfo.InvariantCulture);
        this.Send($"1\06\0{id}\01\0{Text(bid)}\0{Text(bidSize)}\00\0");
        this.Send($"1\06\0{id}\02\0{Text(ask)}\0{Text(askSize)}\00\0");
    }

    private void OnHistoryRequest(string[] f)
    {
        lock (this.gate)
            this.HistoryRequests.Add(f);

        var (answer, rows, count) = this.HistoryPolicy.GetValueOrDefault(f[3], (Answer.Row, "", 0));
        if (answer == Answer.Row)
            this.Send($"17\0{f[1]}\020261106 00:00:00\020261106 01:00:00\0{count}\0{rows}");
        else if (answer == Answer.Refused)
            this.Send($"4\02\0{f[1]}\0162\0Historical Market Data Service error message:no data\0");
    }

    private void OnDepthRequest(int id, string symbol)
    {
        var (answer, delayMs) = this.Policy.GetValueOrDefault(symbol, (Answer.Row, 20));
        lock (this.gate)
        {
            this.DepthRequests.Add((symbol, id, this.clock.ElapsedMilliseconds));

            // A silent book's request gets no answer, so it stops counting once the limit has passed.
            if (answer != Answer.Silent)
                this.unanswered.Add(id);
            this.MostUnanswered = Math.Max(this.MostUnanswered, this.unanswered.Count);
        }

        if (answer == Answer.Silent)
            return;

        _ = Task.Run(async () =>
        {
            await Task.Delay(delayMs);
            lock (this.gate)
                this.unanswered.Remove(id);

            if (answer == Answer.Refused)
                this.Send($"4\02\0{id}\0309\0Max number (3) of market depth requests has been reached\0");
            else
            {
                this.Send($"13\01\0{id}\00\0NASDAQ\00\01\05.00\0100\01\0");
                this.Send($"13\01\0{id}\00\0NASDAQ\00\00\05.05\0300\01\0");
            }
        });
    }

    private static async Task<string> ReadFrameAsync(System.Net.Sockets.NetworkStream net, CancellationToken ct)
    {
        var header = new byte[4];
        await net.ReadExactlyAsync(header, ct);
        var body = new byte[System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(header)];
        await net.ReadExactlyAsync(body, ct);
        return Encoding.ASCII.GetString(body);
    }
}

internal sealed class LatestQuoteSink : IFeedSink
{
    public readonly System.Collections.Concurrent.ConcurrentDictionary<string, (double Bid, double Ask)> Latest = new();

    public void OnQuote(FeedRow row) => this.Latest[row.Symbol.Name] = (row.Bid, row.Ask);

    public void OnPrint(FeedRow row)
    {
    }

    public void OnTrade(FeedRow row)
    {
    }

    public void OnSummary(FeedRow row)
    {
    }

    public void OnCandle(FeedRow row)
    {
    }
}

internal sealed class CountingSink : IFeedSink
{
    public long Quotes;
    public long Prints;
    public long Trades;
    public long Candles;
    public long Summaries;
    public Action OnCanary;
    public (double Bid, double Ask) LastQuote;
    public (string Symbol, double Price, double Size, long Time, char Exchange) LastPrint;
    public (double Close, long Time) LastCandle;

    public void OnQuote(FeedRow row)
    {
        this.Quotes++;
        this.LastQuote = (row.Bid, row.Ask);
        if (this.OnCanary != null && row.Symbol.Name == FeedHealth.CanarySymbol)
            this.OnCanary();
    }

    public void OnPrint(FeedRow row)
    {
        this.Prints++;
        this.LastPrint = (row.Symbol.Name, row.Price, row.Size, row.Time, row.Exchange);
    }

    public double LastDayVolume = double.NaN;
    public double LastDayTurnover = double.NaN;

    public void OnTrade(FeedRow row)
    {
        this.Trades++;
        this.LastDayVolume = row.DayVolume;
        this.LastDayTurnover = row.DayTurnover;
    }

    public void OnSummary(FeedRow row) => this.Summaries++;

    public void OnCandle(FeedRow row)
    {
        this.Candles++;
        this.LastCandle = (row.Close, row.Time);
    }

    public override string ToString() =>
        $"quotes={this.Quotes} prints={this.Prints} trades={this.Trades} summaries={this.Summaries} lastQuote={this.LastQuote} lastPrint={this.LastPrint}";
}

/// <summary>
/// A stand-in for Gateway as the trading connection sees it: answers startApi with an account list
/// and a next order ID, records every message, and sends whatever a test gives it. No orders go anywhere.
/// </summary>
internal sealed class FakeTradingGateway : IDisposable
{
    private readonly System.Net.Sockets.TcpListener listener = new(System.Net.IPAddress.Loopback, 0);
    private readonly CancellationTokenSource stop = new();
    private readonly object gate = new();
    private System.Net.Sockets.NetworkStream current;
    private System.Net.Sockets.TcpClient currentClient;
    private int connections;

    public FakeTradingGateway()
    {
        this.listener.Start();
        _ = Task.Run(this.AcceptAsync);
    }

    public string Accounts { get; init; } = "DU000000";

    public int Port => ((System.Net.IPEndPoint)this.listener.LocalEndpoint).Port;

    public int Connections => Volatile.Read(ref this.connections);

    public ConcurrentQueue<string[]> Received { get; } = new();

    public int Count(string messageId) => this.Received.Count(r => r[0] == messageId);

    public string[] LastOf(string messageId) => this.Received.LastOrDefault(r => r[0] == messageId);

    public void Send(params string[] fields)
    {
        var body = Encoding.ASCII.GetBytes(string.Concat(fields.Select(f => f + "\0")));
        var frame = new byte[body.Length + 4];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(frame, body.Length);
        body.CopyTo(frame, 4);
        lock (this.gate)
            this.current?.Write(frame);
    }

    /// <summary>Closes the current connection, as a Gateway restart would.</summary>
    public void Drop()
    {
        lock (this.gate)
        {
            this.currentClient?.Close();
            this.current = null;
        }
    }

    public void Dispose()
    {
        this.stop.Cancel();
        this.listener.Stop();
        this.Drop();
    }

    private async Task AcceptAsync()
    {
        try
        {
            while (!this.stop.IsCancellationRequested)
            {
                var tcp = await this.listener.AcceptTcpClientAsync(this.stop.Token);
                Interlocked.Increment(ref this.connections);
                _ = Task.Run(() => this.ServeAsync(tcp));
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or System.Net.Sockets.SocketException)
        {
        }
    }

    private async Task ServeAsync(System.Net.Sockets.TcpClient tcp)
    {
        using (tcp)
        {
            try
            {
                var net = tcp.GetStream();
                await net.ReadExactlyAsync(new byte[4], this.stop.Token);
                await ReadFrameAsync(net, this.stop.Token);
                lock (this.gate)
                {
                    this.current = net;
                    this.currentClient = tcp;
                }

                this.Send("176", "20261111 12:00:00 EST");
                while (true)
                {
                    string text = await ReadFrameAsync(net, this.stop.Token);
                    var fields = text.EndsWith('\0') ? text[..^1].Split('\0') : text.Split('\0');
                    this.Received.Enqueue(fields);
                    if (fields[0] == "71")
                    {
                        this.Send("15", "1", this.Accounts);
                        this.Send("9", "1", "101");
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException or EndOfStreamException or System.Net.Sockets.SocketException)
            {
            }
        }
    }

    private static async Task<string> ReadFrameAsync(System.Net.Sockets.NetworkStream net, CancellationToken ct)
    {
        var header = new byte[4];
        await net.ReadExactlyAsync(header, ct);
        var body = new byte[System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(header)];
        await net.ReadExactlyAsync(body, ct);
        return Encoding.ASCII.GetString(body);
    }
}

internal sealed class TraderTestSink : ITraderSink
{
    public readonly ConcurrentQueue<(bool Ready, string Detail)> States = new();
    public readonly ConcurrentQueue<(string Account, IReadOnlyDictionary<string, string> Values)> Accounts = new();
    public readonly ConcurrentQueue<IbkrPosition> Positions = new();
    public readonly ConcurrentQueue<IbkrOrder> Orders = new();
    public readonly ConcurrentQueue<IbkrFill> Fills = new();
    public readonly ConcurrentQueue<(int OrderId, int Code, string Text, bool Ended)> Messages = new();

    public void OnTraderState(bool ready, string detail) => this.States.Enqueue((ready, detail));

    public void OnTraderAccount(string account, IReadOnlyDictionary<string, string> values) => this.Accounts.Enqueue((account, values));

    public void OnTraderPosition(IbkrPosition position) => this.Positions.Enqueue(position);

    public void OnTraderOrder(IbkrOrder order) => this.Orders.Enqueue(order);

    public void OnTraderFill(IbkrFill fill) => this.Fills.Enqueue(fill);

    public void OnTraderOrderMessage(int orderId, int code, string text, bool endedOrder) => this.Messages.Enqueue((orderId, code, text, endedOrder));
}

internal sealed class PositionPrinter(ConcurrentQueue<IbkrPosition> reported) : ITraderSink
{
    public void OnTraderState(bool ready, string detail) { }

    public void OnTraderAccount(string account, IReadOnlyDictionary<string, string> values) { }

    public void OnTraderPosition(IbkrPosition position) => reported.Enqueue(position);

    public void OnTraderOrder(IbkrOrder order) { }

    public void OnTraderFill(IbkrFill fill) { }

    public void OnTraderOrderMessage(int orderId, int code, string text, bool endedOrder) { }
}

internal static class SilentSound
{
    [System.Runtime.InteropServices.DllImport("winmm.dll")]
    private static extern bool PlaySound(byte[] sound, IntPtr module, uint flags);

    public static bool Play(byte[] wav) => PlaySound(wav, IntPtr.Zero, 0x0004 | 0x0002);
}

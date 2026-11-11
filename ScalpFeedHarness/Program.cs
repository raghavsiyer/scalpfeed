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
        Check(usual.Count == 3 && usual[0] == (0.0, 0.10, 0.0001, 4) && usual[1] == (0.10, 1.0, 0.0001, 3) && usual[2] == (1.0, double.PositiveInfinity, 0.01, 2),
            $"4 decimals under 10 cents, 3 to a dollar, 2 from a dollar up, tick sizes untouched ({string.Join(" ", usual)})");
        Check(PriceBands.ForDisplay([]).SequenceEqual(usual), "a lookup with no tick sizes gets the same bands");
        var across = PriceBands.ForDisplay([new TickBand(double.PositiveInfinity, 0.01)]);
        Check(across.Count == 3 && across[0] == (0.0, 0.10, 0.01, 4) && across[1] == (0.10, 1.0, 0.01, 3) && across[2] == (1.0, double.PositiveInfinity, 0.01, 2), "one band across both lines splits at each, keeping its tick size");
        var several = PriceBands.ForDisplay([new TickBand(0.05, 0.0001), new TickBand(0.5, 0.0005), new TickBand(1.0, 0.001), new TickBand(10.0, 0.01), new TickBand(double.PositiveInfinity, 0.05)]);
        Check(several.Select(b => b.Decimals).SequenceEqual(new[] { 4, 4, 3, 3, 2, 2 }) && several.Select(b => b.TickSize).SequenceEqual(new[] { 0.0001, 0.0005, 0.0005, 0.001, 0.01, 0.05 }),
            $"several bands each keep their tick size, and the one across 10 cents splits ({string.Join(" ", several)})");
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
    case "printfilter":
        RunPrintFilter(args[1..]);
        break;
    case "scope":
        await RunScope(args[1]);
        break;
    case "alarm":
        await RunAlarmTest();
        break;
}

Console.WriteLine(failures == 0 ? "\nALL CHECKS PASSED" : $"\n{failures} CHECK(S) FAILED");
return failures == 0 ? 0 : 1;

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
    Check(IbkrClient.AllowedRequests.SetEquals(new[] { 71, 59, 1, 2, 10, 11, 97, 98, 20, 25 }), "the whitelist is unchanged");

    await client.StopAsync();
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

    FeedAlarm.TestPlayer = null;
    Console.WriteLine("Playing the real warning sound");
    FeedAlarm.PlayWarning();
    await Task.Delay(1500);
    Console.WriteLine("Playing the real all-clear sound");
    FeedAlarm.PlayAllClear();
    await Task.Delay(800);
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
    sink.OnCanary = () => health.OnCanaryQuote(Stopwatch.GetTimestamp());
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
    var age = health.CanaryAge(Stopwatch.GetTimestamp());
    Console.WriteLine($"canary age {age.TotalSeconds:F1}s, limit now {FeedHealth.SilenceLimit(DateTime.UtcNow)}");

    Console.WriteLine($"final: {sink}");
    Console.WriteLine($"frames={link.TakeFrameCount()} maxFrameHandleUs={link.TakeMaxHandleMicros()} tokenFetches={tokenFetches} state={link.State}");
    await link.StopAsync();
    Check(link.State == LinkState.Stopped, "link stops cleanly");

    // Session limits by Eastern time. All of these are Wed 2026-11-04 (EST, UTC-5) unless noted.
    var et = (int hour, int minute, int day) => new DateTime(2026, 11, day, 0, 0, 0, DateTimeKind.Utc).AddHours(hour + 5).AddMinutes(minute);
    Check(FeedHealth.SilenceLimit(et(10, 0, 4)) == TimeSpan.FromSeconds(5), "regular hours limit 5s");
    Check(FeedHealth.SilenceLimit(et(5, 0, 4)) == TimeSpan.FromSeconds(15), "premarket limit 15s");
    Check(FeedHealth.SilenceLimit(et(17, 0, 4)) == TimeSpan.FromSeconds(20), "after-hours limit 20s");
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

    public void OnIbkrDepth(string symbol, List<IbkrDepthRow> bids, List<IbkrDepthRow> asks)
    {
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

    public void OnIbkrPrint(string symbol, double price, double size, long unixSeconds, string venue, long arrivalUtcTicks)
    {
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
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or EndOfStreamException or OperationCanceledException or ObjectDisposedException)
            {
            }
        }
    }

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

    public void OnTrade(FeedRow row) => this.Trades++;

    public void OnSummary(FeedRow row) => this.Summaries++;

    public void OnCandle(FeedRow row)
    {
        this.Candles++;
        this.LastCandle = (row.Close, row.Time);
    }

    public override string ToString() =>
        $"quotes={this.Quotes} prints={this.Prints} trades={this.Trades} summaries={this.Summaries} lastQuote={this.LastQuote} lastPrint={this.LastPrint}";
}

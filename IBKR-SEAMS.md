# IBKR in ScalpFeed

Status: bid, ask, depth, the tape, the merged tape and intraday chart bars are built (`Feed/IbkrClient.cs`, `Feed/IbkrSession.cs`, `Feed/PrintFilter.cs`, `Feed/TapeMerge.cs`, `Feed/IbkrBars.cs`, `Ibkr/IbkrBridge.cs`). Quotes and depth were tested on paper and live Gateway, the tape and bars on live. Execution isn't started, and the client can't send order messages: `AllowedRequests` is a whitelist without them (71, 59, 1, 2, 10, 11, 97, 98, and by your decision of 2026-11-06, 20 and 25 for history).

Running since 2026-11-06 05:09 local: release build of commit `3a95473`, merged tape, no test triggers (`analysis\dll_strings.py`). Installed for the next restart: the tape-color build ("Tape colors" below). Earlier builds are parked beside it with `.old` names.

The plugin connects to a live Gateway on port 4001 by your decision of 2026-11-05, for data only. `allowLiveAccount` in `ibkr.json` is what permits that.

**Decisions (2026-11-05, yours):** IBKR goes inside this connection, and it supplies the bid and ask as well as depth and execution. The license question and a separate-connection alternative were raised and set aside.

| Data | Source | Why |
|---|---|---|
| Tape and live chart bars | Both feeds, each trade once, set by `tape` in `ibkr.json` (`merged`, your choice of 2026-11-06). Also `ibkr` or `tastytrade` | See "What each feed really is". IBKR alone is complete but batched and about 0.4 s late |
| Chart history, intraday | IBKR, 04:00-20:00 ET, on tastytrade's candles for overnight and when IBKR is down | tastytrade's candles count only its own valid prints, about half the market |
| Chart history, daily and longer | tastytrade | Its daily candles match the whole market exactly |
| Bid and ask | IBKR | tastytrade's are on a 500 ms timer and very likely not NBBO |
| Depth | IBKR | tastytrade has no Level 2 |
| Execution | IBKR | |

## What IBKR brings

- **Execution.**
- **Level 1 quotes.** Measured 2026-11-05: on a 250 ms grid against tastytrade's 500 ms, with bids and asks from many venues. Against that quote, tastytrade's LABT spread was about 10x wider (median $0.0226 vs $0.0021) and matched it only 4% of the time.
- **Depth.** IBKR consolidates it: `reqMktDepth` with smart depth on returns rows that each name their venue, delivered about every 140 ms. Gateway reports your entitlements as full depth on BATS (Cboe), NASDAQ and IEX; top of book on BYX, PEARL, AMEX, T24X, MEMX, EDGEA, OVERNIGHT, TXSE, CHX, NYSENAT, IBEOS, PSX, LTSE, ISE and DRCTEDGE; and no depth on ARCA, BEX or NYSE. So the DOM's top can differ from the true NBBO when ARCA or NYSE is at the inside. **Limit: 3 depth requests at once** (error 309 on the 4th), but only when requests are spaced: see "Depth slots" below.
- **Possible tape failover:** tick-by-tick, about 520 ms batches and 5 slots by your measurement.

## How the pieces fit

| Piece | Where | Behaviour |
|---|---|---|
| Bid, ask and depth | `TastytradeMarketVendor.SetInterest` forwards `Quote` and `Level2` to `IbkrBridge`, which pushes through the vendor, so MultiVendor translates IDs as it does for tastytrade's messages | A symbol shows IBKR's quote once IBKR has quoted it |
| IBKR's stale check | `IbkrClient` counts any quote, depth change or print as data, using `FeedHealth`'s limits; SPY stays pinned for traffic | No data at all within the limit counts as down. Was SPY's quote alone until 2026-11-11, when an unchanged SPY quote after the close set off false alarms and reconnects every minute or two; after hours now allows 60 s |
| IBKR down or a symbol refused | `IbkrBridge` | Quote goes back to tastytrade's. The depth panel empties, because a frozen book looks real. A warning pop-up after 2 s and one on return, none overnight or when markets are shut. No sound since 2026-11-23 (`FeedAlarm.Muted`) |
| Halts and one-sided books | `IbkrBridge.OnIbkrQuote` | IBKR's -1 becomes an empty side rather than leaving the old price up |
| Quantower's reconnects | `IbkrSession`, static, because Quantower builds a new vendor each time | Gateway connection stays up. Only a difference in subscriptions reaches Gateway, 3 s after the takeover. Closes 10 s after the last vendor leaves |
| LULD bands in the depth | `LuldBand`, fed by `EmitPrint` and IBKR's Halted tick; `IbkrBridge.PushBook` adds the rows | Computed by the plan's rules, 9:30-16:00 ET only. One row a side, `Limit Down` and `Limit Up`, with ` ~` when picked up mid-session. Row colour is Quantower's, not the feed's |
| ARCA, NYSE and BX in the depth | `IbkrClient` asks each venue's own quote with every depth symbol; `VenueTops.Merge` adds them as rows | Smart depth leaves those venues out for this account. One row a side per venue, as fresh as the NBBO; none before the smart book has a row; a refused venue just drops out |
| Slot limits | `IbkrClient` | 3 depth symbols (309) and 5 tick-by-tick symbols (10190), shared by every client on the Gateway. Refused requests wait: retried each second for 10 s after a release, and every 15 s otherwise. A symbol with no tape slot stays on tastytrade's tape |
| Settings | `.scalpfeed\ibkr.json`, optional: `enabled`, `host`, `port`, `clientId`, `allowLiveAccount`, `depthRows`, `tape` | Code defaults: on, port 4002, client 78, paper only, 50 rows, `ibkr` tape. Your file sets port 4002 (paper, since 2026-11-09), allows the live account, and sets the `merged` tape |
| Symbols | `IbkrClient.ToIbkrSymbol` | `BRK/B` becomes `BRK B`; tested on `BRK/B` and `BF/B` |

Still to add:

| Piece | Seam |
|---|---|
| Execution | `RegisterTradingVendor` in `ScalpFeedVendor.Connect`, with its own message whitelist |

## LULD bands (looked into 2026-11-11)

You asked whether BookTrader's LULD bands could be taken from IBKR and shown in the depth, or computed instead.

- **IBKR's API doesn't carry them.** Its tick-type table (current docs, ids up to 110, the newest being odd-lot quotes, generic 787) has no band prices. The nearest is `Halted` (tick 49, sent by default): 1 for a regulatory halt, 2 for a volatility pause, which is how a LULD pause shows. BookTrader's bands come from TWS's own data. Asking for a made-up generic tick no longer returns the list of legal ones: the request is silently dropped, with no ticks and no error.
- **tastytrade's feed doesn't either.** dxFeed's `Profile` has `highLimitPrice` and `lowLimitPrice`, but at 10:45 ET today both were NaN on SPY and SCKT, as on 11-05.
- **The rules, from luldplan.com (permanent plan, 2019):** reference price = arithmetic mean of eligible reported transactions (those that update the last sale) over the prior five minutes; the first of the day is the primary market's opening price, or its previous close if it opens on a quote; it changes at most every 30 s and only when the new mean is at least 1% away; no eligible trades keeps the old one. Band = reference +/- reference x percentage, rounded to the penny. The percentage goes by the previous close and tier: Tier 1 (S&P 500, Russell 1000, some ETPs) 5% over $3; Tier 2 10% over $3; both 20% from $0.75 to $3; under $0.75 the lesser of $0.15 or 75%. Doubled in the last 25 minutes for Tier 1 and for Tier 2 under $3. Only 9:30-16:00 ET, so never in premarket.
- **Computing them isn't faster.** The SIPs compute from the same trades as they happen; the plugin would see them on IBKR's tape about 0.3 s later. Bands move at most every 30 s and only on 1% moves, so the lag barely matters, and correctness is the risk instead:
  - the 1% rule makes the band depend on its history, so a symbol picked up mid-session (or a restart) can be off by up to about 1% of the price until the plugin has its trades since the open. Rebuilding them needs IBKR's historical ticks (message 96, not on the whitelist).
  - the tier needs a list kept up to date; small caps are nearly all Tier 2.
  - eligibility goes by sale condition, and some IBKR prices are rounded to the cent.
  - reopening after a pause resets the reference to the reopening price.
- **Showing them:** Quantower's API has no field for price limits (its docs' only match is `Core.TradingStatus`, which is about its own trading lock). In the depth they'd have to be marked rows, which look like orders and count in totals; on the chart they'd need a separate indicator.
- **Built, by your choice (2026-11-11):** `LuldBand` works the bands out from the tape by the rules above. `EmitPrint` feeds it every print that goes on the tape, which in merged mode is each trade once from whichever feed brought it; odd lots (tastytrade's invalid ticks, IBKR's condition I) don't count. IBKR's Halted tick (49, on the SMART quote) stops the bands during a halt or pause, and the first print after it sets the new reference. The depth gets one row a side, venue `LULD`: the lower band among the bids and the upper among the asks, always shown, never in an empty book. A symbol first seen after 9:31 is `LULD~` until a reopening or the next day, since its band can be off by up to 1%. The log says each band as it changes (`[luld] SYM bands L/U, reference R, Tier, prev close`) and each halt. Tier 1 symbols go in `.scalpfeed\luld-tier1.txt`, read on connect; the rest are Tier 2. Harness `luld`: 25 checks on the rules, the halt tick and odd lots.
- **First day on screen (2026-11-11):** bands logged for CENN, GIPR and NXXT. NXXT's ±$0.15 at $3.29 is right: it closed 11-10 at $0.1865 (Alpha Vantage: crashed from $2.64), so the under-$0.75 rule applies all day, and it paused three times in 7 minutes. One reopening was wrong: the band took 3.1812 instead of the auction. A capture of the next reopening showed why: Nasdaq's auction prints (`@5 X`, round lot, valid; `@5 x`, odd lot) arrived at 13:56:40.190 ET and tastytrade's Profile went ACTIVE at 13:56:40.357, and IBKR's Halted tick clears at about the same time, so the auction's prints were dropped as "during the pause" and the next print off the auction became the reference. Fixed: a print with condition 5 (Re-Opening Prints, the same code on both SIPs) restarts the band itself, whatever its size, and the flag clearing afterwards changes nothing. The rows now read `Limit Up` and `Limit Down` (` ~` when rough) instead of `LULD`. Asked for them in a uniform grey: Quantower's API has no colour on a depth row (`Level2Quote` carries price, size, `Broker`, `NumberOrders`, `ImpliedSize`, `Closed`), and the Market Depth panel colours rows by its own mode (by price level, relative to volume, step to max volume, by rows/cumulative size/price), so the feed can't make one row grey. Not yet compared with the official bands: BookTrader's are the reference to check against, from a TWS logged in to the live account while the Gateway holds paper.
- **Indicative reopening price (2026-11-11):** IBKR sends Nasdaq's halt-cross numbers during a pause when the quote asks for generic tick 225 (auction values): tick 35 auction price, 34 paired shares, 36 imbalance (positive means more to buy), 61 regulatory imbalance. NXXT, paused limit-down at $3.55 since 11:02:16 local, at 11:25: indicative 2.25 then 2.24, about 121,000-128,000 paired, imbalance swinging between -6,700 and +1,600, an update a second, the same on SMART and ISLAND, and IBKR's SMART bid and ask locked at the indicative price. One message type (1) already allowed; only the tick list changes. `analysis\auction_ticks.py` prints them. **Built the same day:** the SMART quote asks for tick 225; during a halt or pause the depth gets a `Res` row on both sides at the indicative price, always shown, never in an empty book, gone when the halt flag clears. Numbers from before a halt (the opening and closing crosses) are dropped. The log says each new price (`[res] SYM reopening auction at P, N paired, imbalance I`), and the IBKR stats line counts `auctionUpdates`. The Limit rows read 1. They read a fixed 999,999 from 2026-11-11 until you asked on 2026-11-13 for them back at 1. **The Res row's size is the seconds until the reopening, times 1000** (your idea, 2026-11-13; `PauseClock`), so it counts 300,000 down to 1,000: a pause is 5 minutes and extends 5 at a time, so the reopening is due at the next 5-minute mark from the pause's start, which is when IBKR's Halted tick turned 2. The 11-11 log bears that out: NXXT reopened 300.2, 599.9 (one extension) and 299.9 s after that tick and WHLR 300.1 s. Two that didn't fit (56 s and 368 s) were the symbol being reopened in Quantower mid-pause and dropped while it reopened. The book goes out again each second during a pause so the count moves, from IBKR's thread when the next data arrives. The start is kept across Quantower's reconnects. **A halt is only believed while IBKR streams the symbol's quote (2026-11-16):** a new stream sends the Halted tick only when it isn't 0, so when GDHG's DOM moved to another symbol mid-pause and came back after the reopening, the Res row stayed on while it traded, with the bands off. Now a cancelled quote stream or a dropped connection forgets the halt, the auction price and the band's wait; the next print restarts the band, marked rough; a cancelled stream also forgets the pause's start, since the pause met later may be another one. **A pause already on when the symbol is opened is estimated from its last trade (your choice, 2026-11-16):** nothing trades during a pause, and on 11-16 GDHG's and GMM's six pauses began 0 to 2 s after their last trade (tastytrade's 1-second candles against IBKR's tick), and GDHG, opened mid-pause twice, reopened about 301 s after its last trade both times. So the pause is taken to begin a second after tastytrade's last Trade, once that stream is current; the log says `[res] SYM opened mid-pause, last trade at T, so the countdown is estimated from that`. When nothing is known (a regulatory halt with no set time, no last trade within the hour, or a start over an hour old met again with nothing between), the row reads 999,999 (`IbkrBridge.MarkerRowSize`) as before, so the Market Depth panel's third size highlight, set at 999,900, colours it only then. Harness `luld` covers the count. Not yet seen in a real pause. Paired shares are in the log only. **The Res row goes on one side (your choice, 2026-11-17; `ResSide`):** among the asks for a halt up and the bids for a halt down, on both sides only while which way isn't known. Which way is the last trade against the band's reference price, asked as IBKR's Halted tick turns on and before it stops the band: a pause at a band sits the band's whole width (5% to 20%, or $0.15) from the reference, so even a rough band tells, and it only counts with a round lot in the last five minutes. When the band can't say (opened mid-halt, or a regulatory halt outside 9:30-16:00), the first auction price that differs from the last trade (tastytrade's Trade stream once current, else the tape's) decides it once for the halt. A pause turning into a halt keeps its way; the halt ending, or being forgotten, clears it. The log says which and why (`[res] SYM halted up at P, so its Res row goes among the asks`, or `[res] SYM which way it halted wasn't seen, so the auction at P, below the last trade at L, puts its Res row among the bids`). Harness `luld` covers the rule.
- **No rows past the bands (your request, 2026-11-18; `LuldBand.Within`):** while a band stands, the depth leaves out bids priced below `Limit Down` and asks above `Limit Up`. They're real resting orders, but none can trade until the band moves, so they only crowd the ladder. Rows at the band price itself stay. A rough band (` ~`) can be off by up to 1% of the price either way, so rows up to 1% past it still show rather than hide orders the real band may allow; once the band is exact (a reopening, or the next day), nothing shows past it. There's no filtering during a halt (no bands then), outside 9:30-16:00 ET, or on the bid side when the lower band would be 0 or less. Harness `luld` covers it.

## Must be true before real orders go through it

1. **A tastytrade failure can't touch execution.** Today a tastytrade link that's down makes `Ping` report failure, and Quantower answers with a full disconnect and reconnect.
2. **The chart reload can't touch execution.** It works by reporting one failed ping, after any gap over 2 seconds, and costs about 1.7 seconds. With IBKR inside, it must be turned off or limited to when you're flat. Charts would then keep small holes after a gap until reloaded by hand.
3. **An IBKR failure can't take down charts and tape,** and either source failing can't stop the other from connecting.
4. **The IBKR connection survives a reconnect.** Quantower builds a new vendor on every reconnect (seen in the plugin log), so anything that must survive one has to live outside the instance.
5. **Tested against an IBKR paper account first:** kill each source while holding a paper position, and confirm orders and positions stay correct.
6. **Order prices aren't cut to the displayed decimals.** The display is now 2 or 3 decimals from 10 cents up while stocks trade finer, for example $0.0001 steps under a dollar. Check that a limit price entered in Quantower reaches the order at the price meant.

Items 1-3 come partly from the DLL inspection I shouldn't have done, so treat them as likely. Confirm them with a stub second source and the plugin log. Quantower's terms prohibit looking inside its DLLs, so don't settle this by disassembly.

## Paper trading (built 2026-11-11, after you said "Yes, start on paper")

- **A connection of its own.** `IbkrTrader` opens a second socket to the same Gateway on client ID 79 (`tradingClientId` in ibkr.json, else the data client's 78 plus 1). The market data client's `AllowedRequests` is unchanged and still has no order or account message; the trading client's is `71, 61, 62, 5, 7, 3, 4` (startApi, reqPositions, reqAccountSummary, reqOpenOrders, reqExecutions, placeOrder, cancelOrder). Our own TWS API client, not Quantower's `InteractiveBrokersVendor`.
- **Paper only, with no setting to change it.** If the Gateway's account list has anything not starting with D, the trader sends nothing after startApi, refuses every order, and retries once a minute in case the Gateway changes. The send path checks again before every message.
- **The order message** is ib_insync 0.9.86's placeOrder for server version 176, field for field: the harness compares all 115 fields of a limit, market, stop and stop-limit order against messages ib_insync itself built (no connection involved). Prices go out in the shortest form that reads back as the same number, so a limit of 3.457 is sent as 3.457 (item 6 above); IBKR's own minimum tick still applies.
- **What Quantower gets:** the paper account (net liquidation as balance, plus cash, buying power, available funds, excess liquidity and day trades left), positions from IBKR's position stream, working orders, today's fills with commission, and place, modify, cancel and close. Order types: limit (Day, GTC, IOC), market (Day, IOC), stop and stop-limit (Day, GTC). No brackets or OCO yet.
- **Premarket and after hours:** limit and stop-limit orders go with outsideRth set, so they work outside regular hours; market and stop orders keep to regular hours, where IBKR fills them. Closing a position from Quantower sends a market order and is refused outside 9:30-16:00 ET, with a message to close with a limit order.
- **Errors:** as in ib_insync, an error on an order that isn't one of its warning codes (110, 165, 202, 399, 404, 434, 492, 10167, 2100-2199) means the order is dead, and Quantower is told so with a refusal; a warning shows as information and the order keeps working. Beyond ib_insync, a 110 (price increment) on an order IBKR never acknowledged counts as refused.
- **Closed trades carry their P&L (2026-11-19).** IBKR's commission report (message 59) has each fill's realized P&L after commissions, and the largest double for a fill that opened; only the commission was read, so paper trades showed no P&L (your report). A fill that closed shares now goes to Quantower with that as its net P&L and the P&L plus the fill's commission as its gross. Quantower works out an open position's P&L itself from its price; the position message has no P&L field. The Trades panel has no P&L % column, so a closing trade's comment carries its net P&L as a share of what the closed shares cost (the sale less the gross P&L, or a cover's cost plus it), "+3.76%" (your request, the same day). Harness `trader` checks the reading. Not yet seen in Quantower.
- **A finished order shows its fill (2026-11-18):** Quantower's order history and the chart's order markers both take an order's price, which was its limit, so every hotkey order looked filled at the ±$0.10 offset while IBKR filled at the NBBO (Alt+1 order 54: limit 3.34, all 1,000 filled at 3.24; trades and positions were right all along). Now an order that's finished with fills reports its average fill as its price (`IbkrOrder.ShownPrice`), and its limit in its comment ("limit 3.34"). A working order still shows its limit, where it rests. Orders from before this build keep their old rows. Not yet seen in Quantower.
- **Reconnects:** the trader lives outside the vendor (`TraderSession`, like `IbkrSession`), so Quantower's reconnects, including the chart-reload one, don't drop it. After its own reconnect it asks for positions and open orders again: a position missing from IBKR's list shows as closed, and a working order IBKR no longer reports stops showing as working. Order IDs never repeat one used before the reconnect.
- **Tested** against a stand-in for the Gateway (harness `trader`, 36 checks). Items 1-5 above haven't been tested. You place any test orders.
- **First run on the real paper Gateway (12:25 local):** the connection came up on the paper account (next order 1). reqOpenOrders got 321, "The API interface is currently in Read-Only mode": the Gateway's Read-Only API box is ticked, and positions and the account summary still answered. Your five test orders on WHLR (three limits, two stops) all got 200, no security definition: Quantower names a symbol "WHLR | tasty" at the connection level (the workspace file stores it that way), and that went to IBKR as the symbol. Fixed with `TradingSymbols`: the tag comes off on the way to IBKR and goes back on for Quantower. All five ended as refused, as they should.
## Choosing the IBKR client (settle with a paper-account test)

- **Quantower's `InteractiveBrokersVendor`:** already written, and it's a public class, but it isn't documented for this use and may not run nested. Its own license check would still run.
- **Our own client on IBKR's TWS API:** more work, but full control over reconnects and over smart depth, which is IBKR's name for consolidated depth. `ScalpFeedIbkrProbe` already speaks this protocol for read-only market data, against Gateway on port 4002 (paper) with no IBKR library.

## What each feed really is

Worked out 2026-11-06 after you pointed out that the missing trades were being treated as noise. "About 98%" was a measurement of a wrong rule, not an understanding of the tape.

**tastytrade's tape is a defined product, not a partial feed.** dxFeed describes Nasdaq Last Sale as "all securities traded in Nasdaq systems and the FINRA Trade Reporting Facility ... operated in partnership with Nasdaq". The data fits that exactly. That tastytrade uses this product is inferred, since neither page says so. The support email could confirm it.

| An IBKR print on | Is it on tastytrade's tape? | So in the merged tape |
|---|---|---|
| NASDAQ (BEX and PSX by definition, and in the 2026-11-05 midday measurement) | Always | Never shown from IBKR |
| ARCA, BATS, DRCTEDGE, MEMX, PEARL, T24X and the rest | Never | Always shown from IBKR |
| FINRA | Usually. IBKR's one name for every TRF, and the part not on tastytrade's tape varies widely by symbol: 0.3-5.2% of shares on most so far, 22% on NVDA, 26.5% on DBGI | Shown unless tastytrade has the same print |

- **The FINRA part it lacks would be the FINRA/NYSE TRF,** by elimination. Neither feed names the facility, so only matching the print can tell them apart, and nothing about size, price or condition separates them.
- **`dayVolume` is the whole market, while the prints and intraday candles are Nasdaq Last Sale.** That's why the candles were short and the daily bar wasn't.
- **IBKR's tape is whole shares, and tastytrade's isn't.** tastytrade sends 2.67 shares as 2 and 0.67, and IBKR has the 2. Prints under one share exist only on tastytrade (94 of 980 on NVDA, 15 of 72 on SPY) and do count in `dayVolume`.
- **IBKR rounds some prices to the cent:** 735.4799 becomes 735.48. None of its prints at $12.51, $16.15, $189 or $735 had finer prices, while $2 stocks keep theirs, so it starts somewhere in between. On RCT at $0.05 the feeds can differ by 0.0001. That, with fractional sizes, is what an earlier note here misread as IBKR "rounding or combining" FINRA prints.

Every tastytrade print across these recordings has an IBKR partner or is under one share. Replaying the real `TapeMerge` over the recordings, in arrival order (`ScalpFeedHarness mergereplay`):

| Recording, session | Symbol | IBKR's tape, shares | Merged, shares | Merged / tape |
|---|---|---|---|---|
| 05:21-05:36 ET | CIIT | 246,071 | 246,071 | 100.000% |
| | RCT | 23,651,212 | 23,650,212 | 99.996% |
| | VSA | 537,973 | 537,873 | 99.981% |
| 06:35-06:40 ET | RCT | 8,500,565 | 8,500,565 | 100.000% |
| | VSA | 101,753 | 101,753 | 100.000% |
| About 07:00 ET | NVDA | 96,222 | 96,249 | 100.028% |
| | SPY | 6,826 | 6,829 | 100.038% |
| About 07:05 ET | F | 8,795 | 8,796 | 100.009% |
| | SOFI | 6,204 | 6,204 | 100.006% |
| 07:28-07:31 ET, with the new build running in Quantower | DBGI | 238,203 | 238,203 | 100.000% |
| | LABT | 146,765 | 146,765 | 100.000% |

- **Above 100% is the fractions:** IBKR's tape doesn't have them, and the merged tape does.
- **Below 100%:** 1,000 shares on RCT and 100 on VSA, about one print each in 15 minutes. Not traced. A trade at the same price and size within a second on another facility would do it.
- **Not checked yet:** the open, or anything with heavy FINRA fractional flow in regular hours. All of this is premarket. SPY, NVDA, F and SOFI were thin at those times.
- **Timing:** tastytrade's prints show at its own speed, and everything only IBKR has arrives about 0.4 s later.

**First minutes inside Quantower** (release build, from 07:25 ET): no problems logged, 24% of one core. 17.5% of FINRA prints were added from IBKR, which was the symbol mix, not duplicates: DBGI alone is 26.5%. The plugin's counts balance: of 1,916 prints shown in one minute, 1,104 came from IBKR and 812 from tastytrade (743 FINRA, 45 Nasdaq, 24 with no partner). That's accounting and a replay, not a print-by-print check of what Quantower drew.

### Tape colors

You wanted a proper tape: gray between the bid and ask, green or red text at the ask or bid, and a green or red highlight above the ask or below the bid. Worked out 2026-11-06, 05:20-05:32 local (08:20-08:32 ET), premarket on CIIT and VSA.

**Why a rule on Last against Ask did nothing:** the T&S Ask column is blank, because a print in Quantower's public API has no bid or ask field to fill (tried by compiling). How that column is ever filled isn't documented.

**Why rows were green or red with some white:** tastytrade labels every print BUY or SELL (`aggressorSide`), yet 66-97% of labelled prints hit between its own bid and ask, and the plugin trusted the label. IBKR's rows have no label, so theirs came from the quote, and mid prints were white.

**Now:** the side comes from where the print hit the NBBO (`PrintPlace`, `QuoteWindow`). Quantower's coloring by aggressor gives gray, green and red with no rules, confirmed on your screen. Delta tools that use the side now count between-the-market prints as neither.

**The highlights took a second try.** The first build put above-ask and below-bid in Tick direction, but Quantower shows Up or Down on every print there, gray ones included, so it works that field out itself. The flag now goes in Buyer and Seller, as `ABOVE` or `BELOW`, and is otherwise left empty. A table rule on Buyer works, confirmed on your screen.

**Decimals:** 4 under 10 cents (your choice of 2026-11-11), 3 from there to a dollar, and 2 from a dollar up (2026-11-06), in `PriceBands`, instead of 4 everywhere. Under 10 cents that's every digit, since steps there are $0.0001; the paragraph below is about the 2- and 3-decimal bands. You confirmed the 4 decimals on screen on 2026-11-11. It's the precision on each price band, which Quantower's docs describe only as formatting. You confirmed it in the table: a rule of Last = a displayed price colored only some of the rows showing that price, so Quantower keeps full prices there. The chart gets the same prices but has no equally direct check. Tick sizes were untouched until 2026-11-24. Since then 10 cents to a dollar has a $0.001 tick (your choice), because 3 decimals with tastytrade's $0.0001 tick is what made Quantower's price stepping recurse until it closed (`PriceBands`), so price steps, chart bucketing and prices entered in Quantower go by $0.001 there; not yet seen in Quantower. The plugin sends the same full prices, so its merge, sides and flags still use them. What changes is how prices read: 2.7188 reads 2.72, so a gray row can look equal to the ask. In a table a 0.4759 and a 0.4761 both read 0.476. (Before the 4-decimal band, each displayed step on a $0.05 stock was about 2%.)

**A bare comparison with IBKR's quote gives false highlights**, because the quote reaches us late. Share of prints counted as outside the market (`analysis\quote_rules.py`):

| | Recording | Bare NBBO | Plugin's rule |
|---|---|---|---|
| tastytrade rows, CIIT | 08:25-08:28 ET | 10.2% | 3.2% |
| tastytrade rows, CIIT | 08:29-08:31 ET | 7.0% | 0% |
| tastytrade rows, VSA | 08:25-08:28 ET | 21.6% | 0.2% |
| tastytrade rows, VSA, moving fast | 08:29-08:31 ET | 40.7% | 17.5% |
| IBKR-only rows, CIIT | 08:25-08:28 ET | 9.6% | 7.0% |
| IBKR-only rows, VSA | 08:25-08:28 ET | 15.0% | 5.4% |

- **The rule:** a print is outside only if it's beyond the NBBO now, the quote it came with (tastytrade's rows), and every quote standing in the last 800 ms. Short of that, it counts as at that side.
- **VSA's 17.5% isn't settled.** Those prints are beyond every quote we had, so they're real or the quotes were behind in a fast move. Nothing here can tell which.
- **Judging IBKR's late rows by an earlier quote made them worse,** so they use the quote on arrival.
- **Tick-by-tick BidAsk isn't worth its slots.** Against `reqMktData` it was -18 to +81 ms at the median and gave nearly the same shares outside (CIIT 7.0% against 7.0%, VSA 38.5% against 40.7%), on 2 of the 5 tape slots. This settles that item on the recheck list for premarket.
- **Between-the-market share differs by feed for a real reason:** tastytrade's rows include dark midpoint prints (33-51% between), while IBKR-only rows are exchange prints, mostly at the quote (9-26% between).

**Depth sizes:** the market depth panel shows whole hundreds, and leaves out rows of 100 shares or fewer behind the best price, by your choice (`DepthSizes`): 2870 shares reads 28, 101-199 reads 1. Rows at the book's best price or IBKR's quoted price always show, so hiding them can't show a wrong spread, and a row under 100 shares there reads 1. The plugin changes the rows it sends for depth, so anything in Quantower that reads depth gets the same, including the panel's totals. The quote, tape, volume and bars are unaffected.

**The book holds top-of-book from many venues, not just the three with full depth** (you pointed this out, and I'd said otherwise). Gateway's 2152 lists full depth from NASDAQ, BATS and IEX and top of book from BYX, AMEX, PEARL, T24X, MEMX, EDGEA, TXSE, CHX, NYSENAT, IBEOS, PSX, LTSE, ISE and DRCTEDGE. The rows also show Nasdaq market makers (VIRT, IMCC, ETMM and others). On 2026-11-06 at 07:17-07:21 local, the venues at the best price included DRCTEDGE, EDGEA, MEMX, PEARL and TXSE as well as NSDQ, BATS and IEX (`analysis\depth_vs_nbbo.py`). ARCA, BEX and NYSE appear in neither list, so a best price there could still be missing from the book.

By time, CIIT's book best matched the NBBO 100% of a 60 s run. LABT's didn't: for 33 s the book's best bid sat at 0.3354 against an NBBO bid of 0.3901, and for 2.7 s its best ask at 0.456 against 0.3932. That isn't explained. It could be my script rebuilding the book wrongly, a venue the book doesn't have, or a stale book. It's the "wrong spread" case, so it needs a recheck. An earlier count by samples rather than time, 39.7% "book better than the NBBO" on CIIT, didn't hold up when measured by time.

**ARCA, NYSE and BX rows (2026-11-09, paper, premarket).** You saw ARCA in TWS but not in the DOM. The account can't have their full books (ARCA's is refused with 354), but each venue's own best bid and ask comes through `reqMktData` with the exchange set (`analysis\venue_quotes.py`). On CKX, ARCA's own quote equalled the NBBO when IBKR named ARCA at the best, and IBKR's best-venue strings (ticks 32 and 33) often named ARCA alone. Now each depth symbol also asks for those three quotes (three market data lines, no depth slot), and `VenueTops.Merge` puts each as a row in price order, after rows already at that price. A venue with no market adds nothing, and the rows never show before the smart book has a row, so a symbol without a depth slot doesn't get a book of only these. A venue's change sends the book again. On OMH over 90 s (`analysis\venue_book_check.py`, about 18,000 book row changes):

| Best price vs the NBBO, by time | Book alone | With the venue rows |
|---|---|---|
| Bid the same | 53.6% | 95.2% |
| Ask the same | 33.5% | 95.8% |
| Bid / ask worse | 44.7% / 63.9% | 3.1% / 1.6% |
| Bid / ask better | 1.7% / 2.6% | 1.7% / 2.6% |

- **The venue rows are exactly as fresh as the NBBO quote:** ARCA's own quote reached each new price at the same moment the NBBO did (median 0 ms over 88 prices, later in 6). Both come on `reqMktData`'s cadence, about 3 updates a second here, against the book's 200 row changes a second, so in a fast move these rows can trail the book's other rows the way the NBBO does.
- **They never put the book ahead of the NBBO for any length of time:** "better" time is the same with and without them. The 225 samples where they did were within a single burst from Gateway, which the plugin's 30 ms settle hides.
- **What's left of "worse"** is time when IBKR named NASDAQ, EDGX or BZX at the NBBO, venues the book already has, so it's the book and the quote moving at different moments, not a missing venue.
- **NYSE and BX** had no quote on OMH or CKX premarket (price -1), so they add nothing yet. Recheck at the open.
- This probably explains the LABT gap above, but venues weren't recorded then, so that's a guess.
- **Installed and confirmed on screen** at 06:19 local, OMH: ARCA rows show in the depth. The plugin's first minute had 100 venue quote updates and no refusals.

### Latency readings and the PC clock (2026-11-11)

You saw new latency spikes in Quantower. They come from the PC clock, not from the feeds:

- **Quantower keeps its own clock.** Its log (`C:\Quantower\Logs\Serilog\<date>.slog`) records "Time was synchronized. Current offset (ms): 650" at 04:14 local, 658 at 04:23 and 239 at 05:26. Quantower's latency reading uses that corrected time.
- **The PC clock was 0.24-0.66 s slow** all morning. `w32tm /stripchart` against time.windows.com and time.cloudflare.com gave +0.23 to +0.27 s at 05:55, and Windows reported itself not synchronized (leap indicator 3). It wanders: 171 ms fast on 11-05, and 1.5 s fast on 11-09 until Windows stepped it back by 1,501 ms at 06:19:30. That step, not the restart 30 s before it, is what ended Monday's flat 1,573 ms `lastPrintDelayMs`.
- **The plugin stamps IBKR prints, IBKR quotes and books with the PC clock** (`DateTime.UtcNow` on arrival), so to Quantower they look as old as the PC clock is slow, about 240 ms now and 650 ms at start. tastytrade prints carry exchange times and read their real delay. In the merged tape the reading jumps between the two. When the PC clock runs fast, the same stamps look early and the reading hides it. Prints stamped early can also land in the previous bar at a bar's start until the chart reloads.
- **Fixed the same day:** `FeedClock` is what the plugin now stamps with. The vendor points it at Quantower's corrected clock (`Core.Instance.TimeUtils.DateTimeUtcNow`) on connect: IBKR print arrivals, IBKR quotes and books, tastytrade frame arrivals, the tape merge's windows, the quote window that judges a print's side, and the tape watch. Interval timers stay on the PC clock, since Quantower's correction jumps when it re-syncs. If Quantower's clock throws or reads more than 5 minutes from the PC's, the PC's is used. The log now says the correction at connect, with the cost of a read, and `clockAheadMs` on each tastytrade stats line; `lastPrintDelayMs` becomes the real delay. Harness `feedclock` covers the fallbacks and an IBKR print's stamp. Installed 06:18 local: the log said "Quantower's is 247 ms ahead of the PC's, 225 ns a read" (Quantower's own sync said 247 at 06:18:14), and `lastPrintDelayMs` went from -196 to 49-50 ms. You confirmed the latency reading steady on screen.
- **The feeds themselves are unchanged.** tastytrade's newest-print delay was a steady -184 to -190 ms by the PC clock from 05:29 to 05:58, so about 50 ms real once the 240 ms is added back, the same as 11-05's corrected 57-62 ms.
- **Handling spikes did grow, but stay small** (`analysis\latency_trend.py`): the worst tastytrade frame and the worst IBKR flush in a minute were usually under 1-4 ms on 11-05 and 11-06, and are 10-16 ms in most minutes since 11-09 (p90 10.6 ms and 15.5 ms today, max 17.3 ms every day). The ceiling sits at one Windows timer tick (15.6 ms), so a thread is being made to wait for a tick. It started before the venue rows (11-09 premarket p90 11.6 ms), and books delivered rose from 154 to 267 a minute with them. Not yet explained; far below what shows as a latency spike.

### The blinking latency and the tape's micro-pauses

You saw Quantower's latency measure blink 100-200 ms with the tape seeming to pause, and noted it sits steadily at 100-200 ms on an IBKR-only tape.

**The number was the plugin's own doing.** It stamped IBKR's rows 175 ms in the past, and Quantower's measure evidently reads the stamp's age. Merged, the rows alternate between those and tastytrade's (23-27 ms old), so it blinked. That 175 ms was a guess, and IBKR's real lag is about 0.4 s. Rows are now stamped on arrival, so the measure no longer reflects IBKR's lag at all. `analysis\print_age.py` shows the difference.

**What fixed the pauses, by your eye** (about 08:05-08:15 ET, one session): they were there on merged with the old stamps, stopped on tastytrade alone, and were gone on merged with the new stamps. The feeds and rows were the same, so the backdated stamps look like the cause of the pauses as well as the number. How the stamps made Quantower pause is unknown: its display thread never stalled, and its internals aren't something to look into. Recheck at the open, when both feeds run much faster.

**Corrected:** I first concluded below that the pauses were just moments when no row had arrived. Your test contradicts that, at least for what you were seeing. The measurements below still stand as measurements, and the gaps are real, but they aren't what you were describing. Spreading IBKR's batches isn't needed.

**Nothing froze, on anything measured** (07:30-08:01 ET, merged with the old stamps, DBGI and LABT open):

- **Plugin:** tastytrade's frames, including handing prints to Quantower and any wait on IBKR, took at most 0.06-1.3 ms per minute. IBKR's quote and depth flush peaked at 16.8 ms, once.
- **Quantower's main display thread** (`analysis\ui_stall.py`, a do-nothing window message every 10 ms): 8,632 answers in 90 s, median 0.06 ms, p99 0.71 ms, slowest 11.8 ms, none near 100 ms. Quantower used 33% of one core. That also rules out the network ping blocking the screen. Panels drawn on another thread wouldn't show here, though only one display thread has visible top-level windows.

**The gaps between rows** (`analysis\tape_rhythm.py`, premarket):

| | DBGI | LABT | RCT | VSA |
|---|---|---|---|---|
| Gaps of 500+ ms a minute, tastytrade alone | 32.4 | 37.4 | 28.9 | 34.4 |
| Same, merged | 21.8 | 41.0 | 27.3 | 40.6 |
| Gaps of 1+ s a minute, tastytrade alone | 5.1 | 23.3 | 10.4 | 19.9 |
| Same, merged | 1.5 | 20.7 | 7.3 | 18.9 |
| Gaps of 300+ ms ended by an IBKR lump | 41% | 21% | 30% | 34% |
| IBKR-only rows per delivery | 5.1 | 3.1 | 5.6 | 3.8 |

Merging mostly removes long gaps, though on thin names one long gap can split into two shorter ones. Most gaps end with a tastytrade row, so they're the market or tastytrade's own flow. When an IBKR lump ends one, 95-96% carry a print from a second overlapping the gap, so trades did happen while IBKR was holding them. IBKR stamps whole seconds, so that test is coarse.

**Filling a gap means showing rows that have already arrived, later.** Spreading each IBKR batch evenly, simulated:

| Spread | Gaps of 300+ ms a minute: DBGI, LABT, RCT, VSA | IBKR-only rows wait a further, median / p90 |
|---|---|---|
| None, today | 63.3, 61.7, 52.7, 53.8 | 0 |
| 150 ms | 54.7, 61.2, 46.2, 52.2 | 50-75 / 125-199 ms |
| 300 ms | 42.0, 58.6, 40.8, 50.7 | 100-150 / 250-470 ms |
| 600 ms | 32.4, 57.6, 30.8, 49.2 | 205-4,453 / 520-11,775 ms, because the queue backs up on busy names |

Not built, and not needed now: it helps busy names modestly, barely changes thin ones, and makes rows later on a tape used for speed. IBKR's delivery was also much quicker at midday (a gap of 179-274 ms and about 115 ms behind on 2026-11-05 against 570-710 ms and about 440 ms in premarket).

## Why tastytrade's tape can't be made complete by settings

Tested 2026-11-05, after claims that `AUTO` drops prints and `STREAM` or `fromTime` delivers all of them:

- **`AUTO` and `STREAM`, side by side, after hours, 74 s, 7 symbols:** the same 965 prints, none missing from either.
- **`STREAM` with `fromTime`:** no prints at all. dxFeed's spec allows `fromTime` only on `HISTORY` or `AUTO` channels, but those deliver nothing for TimeAndSale either: see "TimeAndSale backfill" below.
- **Every print, valid or not, against the `dayVolume` rise:** 49-69% under `STREAM` and 54-98% under `AUTO` in regular hours, 43-73% after hours. All exchange codes are `Q`. IBKR's tape over the same span was 100-101%.
- **How one recording splits (AEHL and SPY, 84 s):** tastytrade's valid prints 43% and 58% of the `dayVolume` rise, its odd lots 19% and 5%, and venues it doesn't carry 37% and 36%.

Regular-hours `AUTO` and `STREAM` weren't run side by side. A burst heavy enough to overflow a buffer wasn't tested either, though valid prints matched candle volume exactly in earlier regular-hours runs.

## tastytrade's tape against IBKR's, midday

Measured against IBKR's tick-by-tick tape, recorded together on 2026-11-05 (SPY and AEHL, 76 s, midday):

| | tastytrade | IBKR tick-by-tick |
|---|---|---|
| Share of IBKR's shares | 61-63% | 100% |
| Venues | Nasdaq, BX, PSX and nearly all of FINRA (96.9-98.6% of IBKR's shares on those four) | All 18 |
| Median gap between deliveries | 35 ms (SPY), 98 ms (AEHL) | 179 ms (SPY), 274 ms (AEHL) |
| Shared prints | First 90-95% of the time | Later by a median of about 115 ms, p99 about 400 ms |

tastytrade's tape misses ARCA (15-23% of these shares), Cboe, NYSE, IEX, MEMX and more, so charts built from it can miss highs, lows and volume.

An earlier "about half of FINRA" was wrong. It came from matching FINRA prints by exact price and size, which fails on IBKR's cent-rounded prices and whole-share sizes (tastytrade has 735.378 and 19.55 shares where IBKR has 735 and 19), and that overcounted by 12-18%.

Replay of the same recording through the old venue rule, which dropped all of IBKR's FINRA prints and is replaced by `TapeMerge` (`simulate_merge.py`, 84 s):

| | AEHL | SPY |
|---|---|---|
| Merged shares vs IBKR's tape | 98.0% | 99.1% |
| Valid prints only | 98.3% | 99.1% |
| 10-second buckets, min / median / max | 88% / 99% / 102% | 96% / 99% / 174% |

The 174% is one bucket. A large print landing in different buckets on the two feeds would explain it, but that isn't confirmed.

What still applies to merged mode:

- **Overnight isn't merged.** SMART `AllLast` has no overnight prints, and tastytrade's `O` prints aren't IBKR's BLUEOCEAN prints, so the overnight tape is tastytrade's and partial: see "Overnight tape" below.
- **IBKR's rows arrive late and batched,** so rows can be out of exchange order by a few hundred ms. They need arrival-time stamps, or Quantower's strictly rising per-symbol times will push them forward anyway.
- **Each merged symbol uses a tick-by-tick slot,** 5 by your measurement.
- **IBKR's prints have no side,** so aggressor has to come from IBKR's own quote.
- **Off-market prints have to be filtered from IBKR by condition** (4, 7, W, V, B) to match `PrintFilter`, which keeps normal odd lots (I on IBKR) and hides the rest, with the same 3% guard against the quote.

## Measured 2026-11-05 evening

Overnight session (21:45-22:15 ET), Quantower closed, live Gateway on 4001. Books had 1-2 venues and volume was thin, so recheck all of this in premarket and at the open. Scripts: `analysis\ibkr_raw.py`, `depth_slot_trials.ps1`, `depth_burst_repeat.ps1`, `overnight_tape.py`, `overnight_compare.py`, `history_channel_scan.py`; harness `ibkrswitch`, `connectretry`, `tnsmatrix`.

### The PC sleeps

Every drop in the evening's log matches Windows Modern Standby (Kernel-Power 506/507, reason "Idle Timeout"): 16:34, 16:38, 17:00-17:04, 17:10-17:32, 18:03-18:20, 18:20-18:29. The Balanced plan sleeps after 5 minutes idle on AC and 3 on battery, and an open Quantower doesn't prevent it, so watching charts without touching anything for 5 minutes takes the feed down. Gateway's 1100/1102 messages also follow wakes.

The 18:20 sequence: wake at 18:20:10; the DXLink socket had already timed out; `Ping` reported Disconnected, so Quantower disconnected at 18:20:12. Its Connect at 18:20:13 hit a DNS failure on the first attempt (network not back). The retry was ready at 18:20:21, but a bug left Connect waiting until its 20 s timeout, so it failed at 18:20:33 and Quantower reconnected at 18:20:39.

The bug: `DxLinkLink.RunAsync` replaced `readyTcs` after a failed attempt while `WaitReadyAsync` was awaiting the old one. Fixed in the working copy; `connectretry` failed before and passes after. Not installed yet.

Still open: `IbkrBridge.Start` runs only after tastytrade is ready, so a tastytrade Connect that takes over 10 s closes the IBKR link ("nobody came back", 18:20:22). That's harmless for data but breaks condition 1 above before execution.

### Depth slots

- **Spaced requests get exactly 3**, then 309, repeatably. A refusal returns in about 60 ms; a granted request's first row takes 460-660 ms.
- **A burst gets past the limit.** 5 requests in the same millisecond were all granted, with rows arriving (seen repeatedly, also after 40 s idle). Don't treat this as extra depth: it looks like a race in the limit, and IBKR may not tolerate it.
- **The count is simple and cancels are immediate.** A request sent in the same millisecond as a cancel is granted. But a waiter gets in only when the count is under 3, so after a burst to 5, two cancels still left requests refused and the third let one in.
- **The cancel flag matters.** `11,1,id,0` on a smart request doesn't free the slot and rows keep coming. The plugin's `11,1,id,1` is right.
- **Several ids for one symbol share one slot.**
- **After a client exits with no cancels,** the next client got its 3 slots 0.6 s later, so nothing stuck.
- **Why the tests disagreed:** `ibkr` sends 3 in one burst, `ibkrslots` 400 ms apart. With another client holding a slot, a raw burst of 3 made 4 in use (8 of 8 runs), so one cancel left the count at 3 and the waiter refused: the failure seen at 16:23 and 16:26. Those runs logged a 309 for AEHL in the burst, so the hidden fourth there is inferred. The plugin's `ibkr` run with a holder passed overnight (1.0 s). To settle it, repeat `depth_burst_repeat.ps1` and `ibkr` with a holder on multi-venue books in premarket.
- **Switching a DOM (`ibkrswitch`):** in the same tick, as Quantower sends it: first book in 754-959 ms, no refusal. New symbol 300 ms ahead of the cancel: 1.07-1.59 s, because the waiter sits until the 1-second retry.
- **Risk:** the plugin sends depth requests in a burst on connect and reconnect. With 4 panels open, all 4 can be granted, and then switching one symbol is refused until another panel closes. The comment in `IbkrClient` saying Gateway takes up to a second to free a slot isn't supported by these runs.

### Overnight tape

- **SMART `AllLast` has no overnight prints:** 0 on NVDA and TSLA in 90 s, against 249 and 62 with exchange=OVERNIGHT (venues BLUEOCEAN and IBEOS, empty conditions, batches on what looks like a 1.5 s timer). SMART quotes and depth do follow the overnight book.
- **That muted the tape in `ibkr` mode.** `IbkrTape` stays set after the last IBKR print, so tastytrade's prints were dropped: 17:05-17:10 shows `prints=0` with frames arriving and `lastPrintDelayMs` still changing. Tastytrade's prints returned at 17:32 only because the wake's `Down` cleared the flag. Without that accident the tape would be blank from 20:00 to 04:00 ET.
- **tastytrade overnight:** every print has code `O`, `validTick=true`, empty conditions, side UNDEFINED. That's why `oddLots=0` all evening. Prints equal the `dayVolume` rise (AAPL, AMD, TSLA to the share, NVDA 99.6%), but `dayVolume` looks like it counts only this set, so that doesn't show completeness.
- **`O` isn't IBKR's BLUEOCEAN.** Over about 3 minutes tastytrade's shares were 0.49x (NVDA) to 3.3x (AAPL) of BLUEOCEAN's, at different prices in the same seconds. Which venue is behind `O` is unknown. `OVERNIGHT` in `PrintFilter.IsCarriedByTastytrade` matches no venue name seen.

### TimeAndSale backfill

- **Any `fromTime` on TimeAndSale gives 0 rows and no live prints,** with no error, on AUTO, STREAM and HISTORY, in seconds or milliseconds, for 30 s to 6 h (NVDA, 6 s per case). Without `fromTime`, all three contracts deliver live prints.
- **`fromTime` is milliseconds.** The Candle control returned exactly the requested hour in ms. A seconds value is read as 1970: 8,000 rows back to 09-23 with the snip flag. dxFeed's spec shows milliseconds; tastytrade's says seconds.
- **The delayed feed's "dozen prints" weren't history.** In the old recordings they're about 10 of the latest prints arriving together in the first second, then ordinary live prints at 900 s delay, with no snapshot flags, whatever lookback was asked. The delayed endpoint now returns 404 for this token, so it can't be rerun.
- **So there's no tick backfill from tastytrade on this token.** Bars come back from Candle. Only tastytrade or dxFeed can say why, so it belongs in the support email.

## Fixes from those measurements (2026-11-06, working copy, not installed)

- **Depth pacing.** `IbkrClient` sends one depth request at a time, the next after a first row, a refusal, or 1 s, so a burst can't go past the limit. A same-tick switch isn't delayed (1 ms in `depthpacing`). Tested against a stand-in Gateway (`depthpacing`) and the real one on multi-venue books ("Depth slots on the real Gateway" below). Not yet run inside Quantower.
- **Overnight prints.** tastytrade's code `O` prints show whichever tape is on, so the tape can't go blank overnight. It stays partial.
- **Tape watchdog (`TapeWatch`).** A fresh tastytrade print not followed by any IBKR print on that symbol within 3 s hands the tape to tastytrade until IBKR prints again. A premarket dry run on real volume gave no false trips ("IBKR against tastytrade" below); the open is still to check. Not yet run inside Quantower. Test build trigger: `test-ibkr-tape-mute`.
- **Stay-awake (`StayAwake`).** Restarts Windows' idle-sleep timer once a second while connected and markets are open. Windows accepts the call; whether it really stops Modern Standby hasn't been tested. To test: Quantower connected on the new build, no input for 10 minutes, then look for Kernel-Power 506.
- **Connect wait.** The `readyTcs` fix above.

## Measured 2026-11-06 premarket

- **Two Gateways, 00:57 onward.** The one started 2026-11-05 16:44 was stuck on "Attempt N: Authenticating..." but still held 127.0.0.1:4001. The one logged in at 00:56:49 had no API port at all, because 4001 was taken. Quantower connected to the old one: 2110, 2103, 2105 and 2157 on connect, no quotes, state `Connecting`, and the warning fired at 01:00:00, the first moment the session's silence limit allowed it. A raw request on 4001 got 0 quotes. Closing both and logging in once is the fix.
- **Late prints at 04:00 ET.** At 01:00-01:01 local the plugin logged prints up to 13,592 s old and 1,337 odd lots refused as too far from the quote (LABT 0.5142 against 0.4568/0.47). They look like overnight trades reported in a batch when the day's reporting opens, but that's a guess: no recording covers that minute. To settle it, record from 00:58 local. The valid ones among them reach the tape stamped "now" at their old price, because times are forced to increase.
- **tastytrade's set in premarket.** CIIT, RCT, VSA, 25 minutes from 01:11 local (`analysis\tasty_session.py`): every print code `Q`, no `O`, median 90-93 ms from print time to arrival, including the PC's clock error. All shares against the `dayVolume` rise: CIIT 52.6% of 708,534; RCT 62.9% of 99.6M, about 4M shares a minute; VSA 49.9% of 2.39M. No print over 5 s late in that window.
- **Nothing on this token widens the tape** (RCT, some runs with CIIT and VSA, 01:38-01:43 local, real volume; `analysis\symbol_event_scan.py`, `aggregation_compare.py`):
  - **Regional symbols:** `RCT&Q`, `&P`, `&Z`, `&K`, `&V`, `&D`, `&H`, `&Y`, `&J`, `&A`, `&N`, `&X`, `&B`, `&U`, `&L`, `&M` gave 0 rows on every event type and no error. `&Q` being silent too, on the venue tastytrade does carry, means the notation isn't served here, or isn't written this way. It doesn't show which venues exist.
  - **Order sources:** of 41 names tried (from memory and dxFeed's docs), only `COMPOSITE`, `COMPOSITE_BID` and `COMPOSITE_ASK` answered, all code `Q`. `NTV` and `ntv` were silent. dxFeed's separate `DOM` service wasn't tried.
  - **`TradeETH`:** no data and no `FEED_CONFIG`, at aggregation 0 and 0.1, though real premarket volume was trading. The spec's field list for it wasn't sent exactly, but `Trade` accepted the same fields, so the list is an unlikely cause.
  - **`ethVolume` and `ethTurnover`:** the server drops them from `Trade`'s accepted fields.
  - **Aggregation 0.1 changes timing, not the tape.** Side by side for about 70 s, the two periods delivered identical prints: CIIT 121, RCT 739, VSA 373, with none in only one. 0.1 is about 45-50 ms later and batches at about 100 ms (RCT's median gap between deliveries 100 ms against 56). The plugin's 0 is right.
  - **Still open:** whether tastytrade's own platform shows other venues. That needs your login, or the support email.
- **Depth permissions.** Gateway's 2152 at 02:08:36 lists depth from NASDAQ, BATS and IEX; top of book only from MEMX, PEARL, EDGEA and others; and "Need additional market data permissions" for ARCA, BEX and NYSE. So the DOM leaves out ARCA, which printed 16-22% of the shares below.

### Depth slots on the real Gateway

03:31-03:36 local (06:31-06:36 ET), with Quantower open and holding 2 of the 3 slots, on AAPL, TSLA, AMD, MSFT, SPY and others, with books from several venues. A spaced raw probe first found 1 slot free.

| 4 depth requests at once | Run 1 | Run 2 | Run 3 | Run 4 | Run 5 |
|---|---|---|---|---|---|
| Raw client, no pacing: granted | 1 | 4 | 4 | 4 | 4 |
| Plugin's client, paced (`ibkrslots 4001 burst ...`): granted | 1 | 1 | 1 | 1 | 1 |

- **A burst does go past the limit on multi-venue books.** 4 of 5 raw runs got all 4 requests, so 6 slots were in use against a limit of 3. Pacing held the count at exactly 3 every time. That settles the risk. The 16:26 failure itself still can't be replayed, but a count above 3 is the mechanism that produces its symptom.
- **After a cancel,** a waiting symbol's first book took 1.6-1.8 s through `ibkrslots`, and 0.8 s in `ibkr`. The range comes from waiting for the 1-second retry tick plus about 0.6 s for the first row.
- **Switching a DOM** (`ibkrswitch 4001 1 ...`) through the paced client: same tick, as Quantower sends it, first book in 638-720 ms with no refusals. New symbol 300 ms ahead of the cancel: 1.1-1.7 s with 1-2 refusals.
- **`ibkr 4001` passed** with 3 skipped checks, which can't be judged while another client's slots leave a symbol waiting. No problems in the plugin's log throughout, so Quantower's DOMs weren't disturbed.

### Tape speed by source

03:35-03:40 local (06:35-06:40 ET), recorded together with Quantower open (`analysis\tape_speed.py`). RCT traded 8.7M shares in those 5 minutes, VSA 105,251, so VSA's numbers are thinner.

| | Share of the `dayVolume` rise | Median gap between deliveries | Rows per delivery | Behind tastytrade on shared prints: median / p90 / p99 |
|---|---|---|---|---|
| tastytrade TimeAndSale, RCT | 56.0% | 111 ms | 1.1 | - |
| tastytrade TimeAndSale, VSA | 55.7% | 548 ms | 1.0 | - |
| IBKR `AllLast`, RCT | 100.1% | 569 ms | 6.8 | 377 / 665 / 736 ms |
| IBKR `AllLast`, VSA | 101.4% | 711 ms | 3.0 | 485 / 730 / 738 ms |
| IBKR `Last`, RCT | 99.9% | 569 ms | 6.0 | 377 / 659 / 736 ms |
| IBKR RT Volume (generic tick 233), RCT | 100.9% | 251 ms | 1.0, but combined | 185 / 328 / 3,077 ms |
| IBKR RT Volume (generic tick 233), VSA | 101.7% | 502 ms | 1.0, but combined | 212 / 1,783 / 3,570 ms |

- **The slower tape is the feed.** Print time to tastytrade's arrival was a median of 23-27 ms, with this PC's clock about 30 ms off by `w32tm`. IBKR's tick-by-tick tape comes in batches and is 0.38-0.49 s behind. The mode changed at 02:08, when Gateway came back: from 00:57 to 02:08 the tape was tastytrade's, and after that IBKR's.
- **Not Quantower's load, as far as can be seen from outside.** It used 22% of one core at 03:35, with 107 threads, and was responding. The plugin passes prints straight through. Quantower's own painting isn't measurable from here.
- **No other IBKR tick-by-tick type helps.** `Last` has `AllLast`'s timing. RT Volume is about 190 ms sooner, but it combines prints (731 rows against 3,129 on RCT), and only 184 of 1,385 prints matched singly, so that number is a biased sample. Generic tick 375 (RT trade volume) was 88.9% on VSA.
- **Merged mode is the one that changes it:** tastytrade's roughly 56% at its own speed, and the rest about 0.4 s later, for about 98% of the volume. Not yet run in Quantower with these symbols.

**Again on Ethernet, 2026-11-11 07:45-07:48 local (10:45 ET), paper Gateway**, after both feeds' connections moved off Wi-Fi (confirmed by their local address). Same scripts, SCKT and SPY for 180 s.

| | Behind tastytrade on shared prints: median / p90 / p99 | IBKR delivery every (median / p90) | Rows per delivery |
|---|---|---|---|
| SCKT, 3,029 of 3,090 shared prints matched | 237 / 484 / 663 ms | 510 / 599 ms | 12.2 |
| SPY, 1,082 of 2,722 matched | 174 / 465 / 603 ms | 515 / 602 ms | 9.5 |

- **IBKR's tape delay is its batching.** Prints come about every 0.5 s, so a print waits about half that for its batch, which is SCKT's 237 ms median. With tastytrade's own delay of about 45 ms (its print-to-arrival median was -1,038 ms against a PC clock 1.08 s slow), IBKR's prints reach this PC about 0.28 s after the trade at the median and 0.53 s at p90.
- **Ethernet itself saves little:** TCP handshakes from this PC took 56 ms to IBKR's server and 67 ms to tastytrade's over Ethernet, against 59 and 70 ms over Wi-Fi, and were steadier. That's about 1.5 ms each way. The drop from 11-06's 377-485 ms median comes from a shorter batch interval (510 ms against 569-711), with different symbols and a different hour, not from the network.
- **SPY's matching is weaker:** many identical prints, so only 40% matched and the p10 of -964 ms shows wrong pairs. SCKT's numbers are the ones to use.
- **In SCKT's merged tape,** tastytrade carried 68% of the shares. The rest (ARCA 19%, EDGA 6%, MEMX and others) came from IBKR about a quarter second later at the median.

### Candle volume

tastytrade's 1-minute candle volume is exactly its own valid prints, in all 42 minutes tested on CIIT, RCT and VSA (05:22-05:35 ET, `analysis\candle_volume.py`), so the candles leave out what its tape leaves out, and odd lots too.

| 05:22-05:35 ET | CIIT | RCT | VSA |
|---|---|---|---|
| Candle volume against IBKR's tape, total | 53.1% | 55.4% | 50.3% |
| Candle against tastytrade's valid prints | 100.0% | 100.0% | 100.0% |
| Minutes where IBKR's high or low went past the candle's, of 14 | 7 | 3 | 9 |

- **Scaling can't fix it.** The ratio moves from minute to minute: CIIT's ranged from 37.4% to 76.3%.
- **No other candle gets more** (`analysis\candle_periods.py`): `=5m`, `=15m`, `=1h` and `price=last` add up to the same total as `=1m`. `tho=true` and `a=s` showed nothing from today's premarket. `price=mark` and `price=bid` gave no rows.
- **Daily candles are right.** `{=d}` equals `prevDayVolume` exactly (RCT 108,628,849, CIIT 6,817,988). That day's 1-minute bars summed to only 32-41% of it. Today so far they're 46-67% of `dayVolume`.
- **Live bars and reloaded bars disagree.** A forming bar is built from the tape, so with IBKR's tape it should be complete, but a reload swaps it for the partial candle. That's from reading the code, not checked on a chart.
- **Other route not used:** bars saved from IBKR's tape need no new message, but only exist from when a symbol first had a tape slot, so not for a runner just opened.

### IBKR's historical bars

Tested 03:45 local (06:45 ET) with your OK, from a separate script that can send only messages 71, 20 and 25 (`analysis\ibkr_history.py`, compared by `history_vs_tape.py`). The tape comparison covers 05:22-05:35 and 06:36-06:39 ET: premarket only, real volume on RCT and moderate on CIIT and VSA. Recheck at the open.

| Share of IBKR's tape, same bars | CIIT | RCT | VSA |
|---|---|---|---|
| IBKR 1-minute bars | 81.4% | 99.9% | 87.4% |
| tastytrade 1-minute candles | 53.1% | 55.1% | 50.0% |

- **IBKR's bars are the tape without odd lots, exactly.** 98 of 98 bars (50 at 1 minute, 48 at 10 seconds) equal the tape's prints without condition `I`, to the share. So the shortfall is only odd lots, large on low-share names (CIIT 19%, VSA 13%) and tiny on RCT (0.1%).
- **The range follows the same rule.** The bar's high or low never went past the tape's. The tape printed past the bar's range in 3 of 14 bars on CIIT, 2 of 18 on RCT and 8 of 18 on VSA at 1 minute, and 12 of 24 on VSA at 10 seconds. Those would be odd lots.
- **Fast enough for chart opens.** 3 days of 1-minute bars (2,086 bars) took 279 ms, 4 hours took 261-984 ms, 30 minutes of 10-second bars took 189-550 ms, and daily bars took about 120 ms. tastytrade's candles took 70-326 ms in the 00:57 log.
- **No overnight bars.** Nothing between 20:00 and 04:00 ET on SMART, so those hours would stay on tastytrade's `O` candles. Bars run 04:00-20:00 ET, with zero-volume bars for quiet minutes.
- **Daily bars are lower than tastytrade's.** Against `prevDayVolume`: RCT 99.8%, CIIT 84.3%, VSA 77.1%. Today against `dayVolume`: RCT 98.0%, CIIT 78.9%, VSA 86.9%. tastytrade's daily candles match the whole market, so daily bars should stay on tastytrade.
- **Live and reloaded bars still differ.** Forming bars include odd lots, and reloaded ones don't, so a reload lowers a bar's volume by the odd-lot share and can narrow its range. Bars carry no odd lots from either source, so history can't be made exact from bars. From memory, the consolidated tape's rules keep odd lots from setting the last price, high or low, which would be why both feeds treat them this way. IBKR's tick history (message 96, not on the whitelist and not tested) is the one other place they might be.

**Built 2026-11-06,** after you approved adding 20 and 25 to `AllowedRequests`. `IbkrClient.LoadBarsAsync` asks, `IbkrBars.Overlay` puts IBKR's bars over tastytrade's, and a chart gets tastytrade's alone if IBKR is down, refuses or takes over 10 s. Not yet run inside Quantower.

- **Periods from IBKR:** 1, 5, 10, 15 and 30 seconds, 1, 5, 15 and 30 minutes, 1 hour. 4-hour, daily and longer stay on tastytrade, because tastytrade's 4-hour bars start at 03:00 ET, and IBKR's alignment for that length wasn't tested.
- **Limits measured** on RCT at 03:52 local: 1 s takes 1,800 S and refuses 3,600 S ("invalid step"); 5 s took 7,200 S; 10 s took 28,800 S but in 4.9 s, so the plugin asks for 14,400 S; 15 s took 28,800 S in 1.4 s, so also 14,400 S; 30 s took 2 D; 1 minute to 1 hour took 30 D in under a second. Older spans work with an end time in UTC. Anything older than one request's span shows tastytrade's bars.
- **Bars line up.** Every bar sits on the clock, and of tastytrade's candles in IBKR's hours, 1,242 of 1,245 fall on an IBKR bar at 1 second and all but one elsewhere (RCT, `ibkrbarslive`). The misses are the two feeds putting a print in neighbouring seconds, which is why an empty IBKR bar clears tastytrade's bar at that time.
- **Pacing is from memory, not tested:** 6 requests per contract in 2 seconds, no identical request within 15 seconds, 60 per 10 minutes for bars of 30 seconds or less. The plugin spaces requests 400 ms apart, and answers an identical repeat from the last result. Going past the 60 would come back as an error, and that chart would show tastytrade's bars.

### IBKR against tastytrade, head to head

CIIT, RCT, VSA, 02:21:43-02:36:43 local (05:21-05:36 ET), recorded together with Quantower open (`analysis\premarket_compare.py`, `probe\ibkr-premarket-20261106.tsv`). Volume was real on RCT, moderate on VSA and CIIT, and the open is still to come.

| | CIIT | RCT | VSA |
|---|---|---|---|
| `dayVolume` rise | 256,711 | 23,883,873 | 541,476 |
| IBKR's tape, share of the rise | 100.1% | 100.0% | 100.0% |
| tastytrade's tape, share of the rise | 59.8% | 55.1% | 54.9% |
| tastytrade prints also on IBKR's tape (same price and size within 3 s) | 1,024 of 1,024 | 2,987 of 3,029 | 1,749 of 1,749 |
| IBKR later than tastytrade on those: median / p90 / p99 / max | 441 / 696 / 735 / 816 ms | 430 / 697 / 736 / 3,540 ms | 449 / 720 / 733 / 813 ms |
| Tape after a venue merge, share of IBKR's shares (worked out from venue totals) | 97.8% | 98.0% | 97.8% |

- **IBKR's tape is complete here.** That's the first premarket measure, and it matches the midday result.
- **tastytrade carries roughly FINRA plus Nasdaq.** Its shares are 96% of IBKR's FINRA and NASDAQ prints on all three. Exchange code `Q` covers both. The matching ignores venue, so the few "matches" to ARCA and others are probably coincidences of price and size.
- **What tastytrade misses:** ARCA 16-22% of shares, DRCTEDGE 9-15%, MEMX, PEARL, BATS.
- **IBKR is slower than at midday.** It was about 115 ms behind on SPY and AEHL at midday, and about 440 ms here. p90 and p99 sit close together near 700-735 ms, which looks like a timer of about 700 ms, though that isn't proven. The overnight tape looked like 1.5 s.
- **Rounding (item 6):** on these whole-share small caps, 5,760 of 5,802 tastytrade prints have an IBKR print of the same price and size, so IBKR isn't rounding or combining them here. The earlier evidence for rounding came from fractional FINRA sizes on SPY, and that's still unexplained. RCT's 42 unmatched prints (287,818 shares) weren't examined.
- **Bad prints:** none past the 3% guard on tastytrade's own quote, and no IBKR print with an off-market condition. Conditions were only `T`, `TI`, `FT` and `FTI`, written with no spaces.
- **Watchdog dry run:** no trips at 2, 3 or 5 s on any symbol. The longest wait from a tastytrade print to IBKR's next print was 781-1,566 ms, so 3 s has about twice the margin needed in this session. Recheck at the open.
- **The plugin's 175 ms lag allowance is too small for premarket.** It's harmless for bars, because prints stay clamped inside the second IBKR gives them. It does mean a print's displayed time can sit up to about a second late within the tape.

## Top gainers (2026-11-16)

- **IBKR's market scanner, kept as a Quantower symbol list.** Set up as momo-algo's (your choice, 2026-11-16): a one-off `reqScannerSubscription` (message 22) every 2 seconds from 4:00 to 20:00 ET (every minute outside), cancelled (`cancelScannerSubscription`, 23) as soon as it answers, so it's re-run each time rather than waiting for a subscription's ~30-second updates. Both are in `AllowedRequests`; still no order messages. 25 rows on `STK.US.MAJOR`, $0.10 to $25, over 50,000 shares, and up 10% (`changePercAbove`, or `afterHoursChangePercAbove` on the after-hours scan, since the plain one would filter by the regular session's change). Gateway answers each cancel with 162, which is ignored on a scan's id only (162 is also a real historical-data error); other errors end that scan, are logged once per code, and the next goes on time; an answer that never comes is given up after 10 s. The connection looks each symbol up, leaves out ETFs, and replaces the list `IBKR Top Gainers` (`Core.ReplaceSymbolList`) when it changes. `topGainers` in ibkr.json turns it off (`false`) or sets `scanCode`, `afterHoursScanCode`, `location`, `rows`, `priceAbove`, `priceBelow`, `volumeAbove`, `changeAbove` and `intervalSeconds`.
- **A least dollar volume (2026-11-24, your request):** `dollarVolumeAbove` in ibkr.json's `topGainers`, in dollars and off unless set, goes to IBKR as the scanner filter `usdVolumeAbove`, on the regular and after-hours scans alike. Read from the Gateway's scanner parameters, and tried on the live scans at 16:11-16:12 ET that day against tastytrade's day turnover: at 250,000 the regular one left out $0.22M and kept $0.30M, at 5,000,000 left out $4.54M and kept $5.51M, and the after-hours one at 250,000 left out $0.03M, $0.07M and $0.22M and kept $2.95M. A stock charted earlier stays in the list only while its day's dollars are over it too. Not yet seen in premarket. Harness `scan` covers the request.
- **By session (your request, 2026-11-16):** Top % Gainers keeps the regular session's list after the close, so from 16:00 to 4:00 ET the client cancels it (`cancelScannerSubscription`, message 23, now allowed too) and asks for `TOP_AFTER_HOURS_PERC_GAIN` under a new id; at 4:00 it switches back. The same Quantower list carries whichever is running. IBKR's scanner parameters (`reqScannerParameters`, read once by a separate script, not the plugin) have no premarket scan and no premarket filter, only after-hours ones, so premarket is Top % Gainers, as momo-algo found: run live in premarket on 2026-08-05 (07:1x ET), Top % Gainers returned the live premarket leaderboard (change against the prior close on premarket trades) and the after-hours scan returned 0 rows, even unfiltered. `afterHoursScanCode` in ibkr.json changes the after-hours one. A list deleted in Quantower is made again with the next update.
- **Each stock's last move in the scan, in the Mark size column (your request, 2026-11-19).** You asked for a red or green arrow for stocks moving up or down the gainers list; a Watchlist column takes only numbers, so each row's day bar carries, as its mark size, the places the stock climbed, net, over the last 5 minutes: its place now against the newest scan 5 minutes old or more (in the scanner's first 5 minutes, the first scan), +3 up three places, -2 down two, 0 back where it was; a stock new to the list counts from one place below that scan's last row; blank off the list or before a second scan (`ScanRanks`). Versions the same day compared against a minute earlier (mostly 0, your feedback) and showed each stock's last move; you chose the 5-minute total. Harness `ranks` checks it. It went first in the Funding rate column (a `DayBar` field stocks leave empty, found by compiling against the public API), which showed it times 100 with 5 decimals; you chose Mark size for whole numbers, shared with a halt's countdown, which takes the column over until the halt ends. Mark size is cleared to 0 once after a stock leaves the list. Not yet seen in Quantower: Mark size showing a negative number, and colouring up green and down red.
- **Charted stocks join the list (your request, 2026-11-19).** A stock opened in a chart today goes at the end of `IBKR Top Gainers` when the scan hasn't got it, ETFs included, most recent first, at most 10, cleared each Eastern day (`ChartedSymbols`). A chart is told apart from a Watchlist row by asking for intraday bars: in the 2026-11-19 log the stocks only in the Watchlist got daily bars alone, and each charted one 4-hour, 5-minute, 1-minute and 10-second bars too. Harness `charted` checks the list. Not yet seen in Quantower.
- **Each symbol is sent from this connection before it's looked up (2026-11-19).** After a restart the first gainers list, looked up through `Core.GetSymbol` about 5 s after connecting, resolved 7 of 25 names, and the 4 of those the Watchlist hadn't already got (CHSN, SEAT, MPU, NKLR) were never quoted: no subscription or history request reached the connection, and a chart opened from those rows showed no data, while a search for SEAT worked. Both scan lists now send each symbol from the connection first (`MakeSymbol`, the tastytrade lookup a search uses, once a session; a stock tastytrade hasn't got is left out and asked about again after 10 minutes), wait for the connection to be listed as connected, and send the list again with the next scan when Quantower gives a symbol back as null, logging those names once. Why the first lookups went wrong isn't known; this is the fix to check after the next restart.
- **With momo-algo's filters, 16:46 ET:** 5 lists in 9.8 s, 10 and then 11 after-hours stocks (AYTU, XXII, FCUV, SPAI ...).
- **First run, unfiltered subscription, paper Gateway, 16:22 ET:** two identical 50-row lists 30 s apart, GMM and GDHG on top, with rights (AXINR, RIBBR), leveraged ETFs (SOXS, UVIX, MSTZ) and a preferred (FBYDP) among them. Harness `scan` checks the request and the answer against the stand-in, and `scan 4002` runs it on the real Gateway. Not yet seen in premarket, or in Quantower: whether a Watchlist follows the list as it's replaced.

## Halts (2026-11-17)

- **A second scan, kept as the `IBKR Halts` symbol list (your request, 2026-11-17).** IBKR's scanner has one halt scan, `HALTED` (the only scan code containing HALT in the paper Gateway's scanner parameters). It takes turns with the gainers, one scan in flight at a time, each every 2 s (every minute outside 4:00-20:00 ET): same exchanges and $0.10-$25 as the gainers, up to 50 rows, no change filter, and a volume over 0. Unfiltered at 11:20 ET on 11-17 its 19 rows were all halts with no trading that day (SPAC units like AFNXU and NIAAU, names halted for weeks), every one Halted 1 with volume 0, and a volume over 0 left none of them. `halts` in `ibkr.json` takes the same settings as `topGainers`, and false turns it off. Stocks only, like the gainers. The log says each change of the list (`[scan] IBKR Halts: 2 stocks of 2 rows, ...`).
- **An empty scan answers 165 "Historical Market Data Service query message:no items retrieved"**, not a list with no rows (HALTED with those filters, 11:03 ET on 11-17). That now empties the list, for the gainers too; any other 165 is still a failure that leaves the list alone, as on 11-16 when a competing TWS login gave 165 "Trading TWS session is connected from a different IP address".
- **Res price and countdown in Watchlist columns.** Opening the list in a Watchlist subscribes each row's quote, so IBKR's Halted tick and auction price arrive as for a DOM. During a halt the row's day bar carries the Res price as the mark price and the seconds until the reopening as the mark size, counted as the depth's Res row counts them (PauseClock, or estimated from the last trade for a pause already on when the row was opened), plain seconds rather than times 1000. A regulatory halt has no countdown. The row goes out again each second during a halt so the count moves, and once after it with both at 0, in case a blank leaves the last value up. The columns are Quantower's Mark price and Mark size, chosen from its public API and the help pages' symbol fields (`{Mark}` Mark price, `{MarkSize}` Mark size): Ticks and Trades were out because the plugin lets Quantower count them itself. Not yet seen in Quantower: whether a Watchlist shows a stock's day-bar mark price and size, and what a 0 looks like after the halt.
- Harness `scan` covers the turns, each answer reaching its own list, and both kinds of 165; `scan 4002` runs both scans on the real Gateway.

## Depth feeds and Nasdaq's small rows (2026-11-18)

- **Checked before market (8:31-8:37 ET):** Gateway refused connections from Quantower's start at 8:23 until 8:31, then the 2152 notice listed the same entitlements as both of 11-17's: full depth on BATS, NASDAQ and IEX, top of book on BYX, AMEX, PEARL, T24X, MEMX, EDGEA, OVERNIGHT, TXSE, CHX, NYSENAT, IBEOS, PSX, LTSE, ISE and DRCTEDGE, and ARCA, BEX and NYSE still needing permissions. The first minute had 1,542 depth row changes, 29 books and 22 venue-quote updates with no venue refused. Harness `ibkrrows` on SVRE: 44 bids and 45 asks from DRCTEDGE, ARCA, BATS, MEMX, EDGEA, NSDQ, PEARL, T24X and two Nasdaq market makers, TSSM and MLCO.
- **IBKR's DRCTEDGE shows as EDGX** in the depth (your request, 2026-11-24; `VenueTops.Label`). Only the name shown changes.
- **Orders over 10% from their side's best price are left out** (your request, 2026-11-24; `DepthSizes.InRange`): bids under 90% of the best bid in the book, asks over 110% of the best ask. Each side goes by its own best, so a wide spread never hides the best prices, and a row at IBKR's quoted price always shows. The `Limit Up`, `Limit Down` and `Res` rows stay however far off they are: they're marks, not orders, and 556 of the 709 bands in the log by then had their upper price over 10% off. The panel's totals count only what's shown. Harness `pricebands` covers it. Not yet seen in Quantower.
- **Nasdaq's own rows (venue NSDQ) behind the best price are left out up to 500 shares, 5 lots** (your choice, 2026-11-18; `DepthSizes`). At the best price, the book's or the quote's, they show at any size, as every venue does. Other venues and Nasdaq's market makers keep the 100-share line. Harness `pricebands` covers it.

## Order hotkeys (2026-11-18)

- **Alt+1 buys 1,000 shares, Alt+2 2,000 (added the same day) and Alt+3 4,000 (2026-11-24), limit at the ask plus 2.5%; Shift+Z sells the whole position and Shift+X half of it, limit at the bid minus 2.5%.** The sells are immediate or cancel from 9:30 to 16:00 ET, and the buys work for a second there (next point), so nothing is left working; all are DAY limits outside it (your requests; `OrderHotkeys`, `HotkeyListener`). Quantower Free has no order placing strategies (its license table), and its own hotkeys send an order at the plain ask or a fixed preset order from Order Entry, whose symbol and price are set when the hotkey is made (help pages, "Custom hotkeys"). So the plugin catches the keys itself and sends the order through `IbkrTrader`, the paper account's own connection, the way an order from the order entry goes; Quantower sees it as a working order like any other.
- **Which stock:** the one with a market depth open, since the DOM follows the stock you pick. With none open, or more than one, nothing is sent, with a refusal message naming them (no sound since 2026-11-23).
- **The price:** IBKR's NBBO at the moment of the key, the ask plus 2.5% to buy and the bid minus 2.5% to sell, but never more than 15 cents (your requests, the same day, after 20; so from $6 up), rounded to IBKR's tick ($0.01 from $1, $0.0001 below). It was 10 cents until 2026-11-19, when IBKR refused every VIVS buy at $0.25 with a 202 cancel whose reason read "We cannot accept an order at a limit price at or more aggressive than 0.2752686" (about 8% past the ask; 10 cents was 40%); you chose 5% for every price, inside that, then 2.5%. Such a cancel with a reason pops up, unlike an IOC's plain 202 for its unfilled rest. Never tastytrade's quote, which isn't the NBBO: a stock IBKR isn't quoting, a side with no market or a halt sends nothing. Limit orders go with outsideRth for premarket and after hours. **IBKR ignores that on an IOC sent to SMART:** each of the 12 hotkey orders you placed on paper from 10:05 to 10:22 ET on 11-18 (AKAN and CLRO, all filled or correctly refused) got warning 2109, "Attribute 'Outside Regular Trading Hours' is ignored based on the order type and destination". It made no difference then, but premarket an IOC would very likely be cancelled unfilled. So outside 9:30-16:00 ET on weekdays the keys send DAY limits instead (your choice, 2026-11-18; `OrderHotkeys.TifAt`), which work premarket with outsideRth and stay working until they fill or you cancel them; Shift+Z and Shift+X count such a working sell against what's left to sell. Not yet pressed premarket. Early closes (13:00 ET) aren't known to the plugin, so from 13:00 to 16:00 on those days it would still send IOC.
- **Shift+Z's and Shift+X's size:** all, or half rounded down to whole shares (1,001 sells 500), of the paper account's long position in the stock as IBKR's position stream last said, and never more than the shares not already in working sell orders, so presses while sells are working can't go past the position. Flat or short, or half of a single share, sends nothing. IBKR's position follows a fill within moments, so a second press inside that gap goes by the position before the fill: Shift+X twice quickly sells half and then half again of the same 1,000, which is the whole position, not three quarters.
- **Buys work for 1 second in regular hours (2026-11-24, your choice, on paper; `HotkeyBuys`).** They were immediate or cancel. On a fast stock IBKR caps a buy's limit at about the last trade (message 2161: "initially cap (or limit) the price of your Limit Order"), so an IOC can find nothing offered at that price and be cancelled within a fraction of a second. Now each is a DAY limit that the plugin cancels a second after it goes, asking again every second while IBKR doesn't confirm. One is open at a time in a stock: a press cancels the one before it if it's still open, waits up to a second for IBKR to confirm, and sends nothing if it doesn't; presses take turns, so five at once leave one order, not five. The new order is priced after that wait, and isn't sent if the depth has moved to another stock or a sell key was pressed meanwhile. Buys already filled stand, and a resting buy of your own in the stock is left alone. Closing Quantower inside the second can leave the order working. Not yet known: whether IBKR's cap follows the price while an order rests, on paper or live. Harness `trader` covers it. Not yet pressed in Quantower.
- **Shift+Z and Shift+X cancel the stock's working buys first (2026-11-19, your choice).** On paper an IOC can stay working for seconds while IBKR's simulator fills it a piece at a time, at the best price only (its docs: fills are simulated from the top of the book, with no deep book): LVWR's Alt+1 order 393 took 3.2 s, and 33 of 397 hotkey IOCs took over a second. IBKR also reprices an order so it doesn't cross your own resting one, so Shift+Z's order 394, sent 2.7 s into 393, got 399 "repriced so as not to cross a related resting order" and died unfilled. Now a position key first cancels every working order in the stock on the other side (a sell key's buys, from a hotkey or the order entry). It waits up to 1 s for IBKR to say each is done, then up to 0.5 s for IBKR's position to come in after their last fill, then sizes and prices the sell from that position and the NBBO at that moment. If IBKR hasn't confirmed in time, the sell goes anyway, as before. If no position comes, the size goes by the last one, which can leave the last fill's shares but never sells more than you hold. The log says what was cancelled and how long each wait took (`[hotkey] Shift+Z: cancelled working buy N (F of Q filled, Cancelled); done in X ms; position up to date Y ms later`). Live, an IOC finishes at once, but a DAY buy left working premarket would block a sell the same way. Harness `trader` covers it. Not yet pressed in Quantower, so how long IBKR takes to send the position after a fill is still to be seen in the log.
- **The keys, a keyboard hook since Shift+Z:** Alt+1 was first registered with Windows (`RegisterHotKey`), which takes a key from every program while Quantower runs; for Shift+Z that would have taken capital Z everywhere. A low-level keyboard hook on the plugin's own thread acts on a key only while a Quantower window is in front, and only then keeps the key from Quantower; every other program gets every key. Held down, a key sends once. The modifiers must be exactly the key's: Z with Caps Lock, or Shift+Alt+Z, isn't Shift+Z. A Quantower hotkey of your own on the same keys won't fire while Quantower is in front.
- **Typing guard:** a key that types a character (Shift+Z and Shift+X, not Alt+1) types it instead of trading while Windows reports a text cursor in the window in front, so a capital Z or X in a Quantower symbol box doesn't sell. Not yet seen whether Quantower's text boxes report one: the log says the first time the guard steps in. Until that's seen, test the shift keys flat, where they can't send anything.
- **The log** says each press (`[hotkey] Alt+1: order N, buy 1,000 SYM IOC limit P (ask A + 0.1)`, or why nothing was sent). Two presses of Alt+1 send two orders outside regular hours; in them the second cancels the first if it's still open.
- **Paper only**, like every order through `IbkrTrader`. Quantower's order confirmation doesn't apply, since the order doesn't go through Quantower's order entry.
- **Orders from Quantower on a halted stock are held until it trades again (2026-11-24, your request; `HeldOrders`).** IBKR's paper simulator fills an order placed in a halt from quotes it still has, while nothing trades. So an order from the order entry, a chart or a close button on a stock IBKR says is halted is kept in the plugin, shown as working with the comment "held until the halt ends", and sent when IBKR's Halted tick says 0, checked every 0.2 s. It can be changed and cancelled meanwhile, and a second close that would go past the position is refused. The stock's IBKR quote stays on while one is held, so the end is heard with the stock off screen. If IBKR's data connection drops mid-halt, the new stream says Halted only while it is, so 5 s of quotes without it counts as trading. Held orders outlive a Quantower reconnect, not its closing; one whose Eastern day is over isn't sent, nor a market or stop order whose stock trades again only outside 9:30-16:00 ET, which IBKR would keep for the next open. They count as working for Shift+Z and Shift+X. Not held: orders already at IBKR when a halt begins, hotkeys (refused in a halt, as before), and a stock the plugin isn't quoting, whose halt it doesn't know. Harness `held` covers it. Not yet seen in Quantower.
- **Restricted stocks (2026-11-23):** IBKR refused all 21 of your ZBAO buys that day with 201 "No Trading Permission, Customer Ineligible; No Opening Trades: Small Cap, Subject to Compliance Restriction". Its contract details give that reason beforehand, as a list of ineligibility reasons, but only from API version 186 (176 has none, 185 an empty slot). `IbkrRestrictions` asks for them on a short connection of its own, client ID data + 2, whose whitelist is startApi and reqContractDetails. It asks once a day per stock, for Top Gainers and halted stocks as they arrive and a charted stock first. A buy key on a stock that has a reason sends nothing and says why, unless it closes a short. Opening it in a chart pops up the reason once a day, with no sound. Gateway drops a connection whose request comes before nextValidId, so the lookup waits for it. On 11-23, 5 of 46 Top Gainers stocks had a reason: ZBAO and AHMA (small cap compliance), BYAH and CIGL (closing only, margin), and RAYA (closing only, regulatory). An order from the order entry or the DOM isn't stopped. IBKR refuses it as before.
- Harness `hotkey` covers the prices, the keys, which stock, Shift+Z's and Shift+X's sizes, what each key-down does, and the listener with the real hook in and stand-in presses posted to its thread (rare keys; no order goes anywhere). Not yet pressed in Quantower.

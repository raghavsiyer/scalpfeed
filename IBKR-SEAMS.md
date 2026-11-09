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
| IBKR's canary | `IbkrClient` pins SPY's quote, using `FeedHealth`'s limits | No SPY quote within the limit counts as down |
| IBKR down or a symbol refused | `IbkrBridge` | Quote goes back to tastytrade's. The depth panel empties, because a frozen book looks real. One warning sound after 2 s, one all-clear on return, none overnight or when markets are shut |
| Halts and one-sided books | `IbkrBridge.OnIbkrQuote` | IBKR's -1 becomes an empty side rather than leaving the old price up |
| Quantower's reconnects | `IbkrSession`, static, because Quantower builds a new vendor each time | Gateway connection stays up. Only a difference in subscriptions reaches Gateway, 3 s after the takeover. Closes 10 s after the last vendor leaves |
| ARCA, NYSE and BX in the depth | `IbkrClient` asks each venue's own quote with every depth symbol; `VenueTops.Merge` adds them as rows | Smart depth leaves those venues out for this account. One row a side per venue, as fresh as the NBBO; none before the smart book has a row; a refused venue just drops out |
| Slot limits | `IbkrClient` | 3 depth symbols (309) and 5 tick-by-tick symbols (10190), shared by every client on the Gateway. Refused requests wait: retried each second for 10 s after a release, and every 15 s otherwise. A symbol with no tape slot stays on tastytrade's tape |
| Settings | `.scalpfeed\ibkr.json`, optional: `enabled`, `host`, `port`, `clientId`, `allowLiveAccount`, `depthRows`, `tape` | Code defaults: on, port 4002, client 78, paper only, 50 rows, `ibkr` tape. Your file sets port 4002 (paper, since 2026-11-09), allows the live account, and sets the `merged` tape |
| Symbols | `IbkrClient.ToIbkrSymbol` | `BRK/B` becomes `BRK B`; tested on `BRK/B` and `BF/B` |

Still to add:

| Piece | Seam |
|---|---|
| Execution | `RegisterTradingVendor` in `ScalpFeedVendor.Connect`, with its own message whitelist |

## Must be true before real orders go through it

1. **A tastytrade failure can't touch execution.** Today a tastytrade link that's down makes `Ping` report failure, and Quantower answers with a full disconnect and reconnect.
2. **The chart reload can't touch execution.** It works by reporting one failed ping, after any gap over 2 seconds, and costs about 1.7 seconds. With IBKR inside, it must be turned off or limited to when you're flat. Charts would then keep small holes after a gap until reloaded by hand.
3. **An IBKR failure can't take down charts and tape,** and either source failing can't stop the other from connecting.
4. **The IBKR connection survives a reconnect.** Quantower builds a new vendor on every reconnect (seen in the plugin log), so anything that must survive one has to live outside the instance.
5. **Tested against an IBKR paper account first:** kill each source while holding a paper position, and confirm orders and positions stay correct.
6. **Order prices aren't cut to the displayed decimals.** The display is now 2 or 3 decimals while stocks trade finer, for example $0.0001 steps under a dollar. Check that a limit price entered in Quantower reaches the order at the price meant.

Items 1-3 come partly from the DLL inspection I shouldn't have done, so treat them as likely. Confirm them with a stub second source and the plugin log. Quantower's terms prohibit looking inside its DLLs, so don't settle this by disassembly.

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

**Decimals:** 3 under a dollar and 2 from a dollar up, by your choice (`PriceBands`), instead of 4 everywhere. It's the precision on each price band, which Quantower's docs describe only as formatting. You confirmed it in the table: a rule of Last = a displayed price colored only some of the rows showing that price, so Quantower keeps full prices there. The chart gets the same prices but has no equally direct check. Tick sizes are untouched, so price steps and chart bucketing shouldn't change, and the plugin sends the same full prices, so its merge, sides and flags still use them. What changes is how prices read: 2.7188 reads 2.72, so a gray row can look equal to the ask. On a $0.05 stock each displayed step is about 2%, and in a table a 0.4759 and a 0.4761 both read 0.476.

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

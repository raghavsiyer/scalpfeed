# IBKR's AllLast against tastytrade's TimeAndSale over the seconds both were recording, by arrival on this
# PC's clock, so the PC's clock error doesn't matter. Includes a dry run of the plugin's tape watchdog.
#   python -I premarket_compare.py <ibkr.tsv from ibkr_raw.py --capture> <capture.jsonl from ScalpFeedProbe>
import bisect
import json
import sys
from collections import Counter, defaultdict

OFF_MARKET = set("47WVBPCNRUZ")  # PrintFilter.IsOffMarket


def pct(values, p):
    return values[min(int(len(values) * p), len(values) - 1)] if values else float("nan")


ibkr = defaultdict(list)  # arrival ms, seconds, price, size, venue, conditions
for line in open(sys.argv[1], encoding="utf-8"):
    parts = line.rstrip("\n").split("\t")
    if len(parts) == 8:
        ibkr[parts[1]].append((float(parts[0]), int(parts[3]), float(parts[4]), float(parts[5]), parts[6], parts[7]))

tasty = defaultdict(list)  # arrival ms, time ms, price, size, code, valid, conditions, bid, ask
volumes = defaultdict(list)
layouts = {}
for line in open(sys.argv[2], encoding="utf-8"):
    try:
        arrival, payload = line.split("\t", 1)
        message = json.loads(payload)
    except ValueError:
        continue
    kind = message.get("type")
    if kind == "FEED_CONFIG" and "eventFields" in message:
        for name, fields in message["eventFields"].items():
            layouts[(message["channel"], name)] = fields
    elif kind == "FEED_DATA":
        data = message["data"]
        for i in range(0, len(data) - 1, 2):
            fields = layouts.get((message["channel"], data[i]))
            if not fields:
                continue
            for j in range(0, len(data[i + 1]), len(fields)):
                row = dict(zip(fields, data[i + 1][j : j + len(fields)]))
                if data[i] == "TimeAndSale" and not row.get("eventFlags", 0) & 2:
                    tasty[row["eventSymbol"]].append((float(arrival), row["time"], row["price"], float(row["size"]), row["exchangeCode"], row["validTick"], row.get("exchangeSaleConditions") or "", row.get("bidPrice"), row.get("askPrice")))
                elif data[i] == "Trade" and isinstance(row.get("dayVolume"), (int, float)):
                    volumes[row["eventSymbol"]].append((float(arrival), row["dayVolume"]))

for symbol in sorted(ibkr):
    i_rows = sorted(ibkr[symbol])
    t_rows = sorted(tasty.get(symbol, []))
    if not i_rows or not t_rows:
        print(f"{symbol}: nothing to compare (IBKR {len(i_rows)}, tastytrade {len(t_rows)})")
        continue
    begin = max(i_rows[0][0], t_rows[0][0])
    end = min(i_rows[-1][0], t_rows[-1][0])
    i_in = [r for r in i_rows if begin <= r[0] <= end]
    t_in = [r for r in t_rows if begin <= r[0] <= end]
    print(f"{symbol}: {(end - begin) / 1000:.0f} s overlap")

    i_shares = Counter()
    i_counts = Counter()
    for r in i_in:
        i_shares[r[4]] += r[3]
        i_counts[r[4]] += 1
    i_total = sum(i_shares.values())
    off = [r for r in i_in if set(r[5]) & OFF_MARKET]  # Codes are one character each, with or without spaces.
    t_codes = Counter()
    for r in t_in:
        t_codes[r[4]] += r[3]
    t_total = sum(t_codes.values())
    t_valid = sum(r[3] for r in t_in if r[5])
    vol = [v for a, v in volumes[symbol] if begin <= a <= end]
    rise = vol[-1] - vol[0] if len(vol) > 1 else float("nan")
    print(f"  dayVolume rise {rise:.0f}")
    print(f"  IBKR {len(i_in)} prints, {i_total:.0f} shares ({i_total / rise:.1%} of the rise), off-market by condition {len(off)} prints, {sum(r[3] for r in off):.0f} shares")
    print(f"    by venue, shares: {dict(i_shares.most_common())}")
    print(f"    by venue, prints: {dict(i_counts.most_common())}")
    print(f"    conditions: {dict(Counter(r[5] for r in i_in).most_common(10))}")
    print(f"  tastytrade {len(t_in)} prints, {t_total:.0f} shares ({t_total / rise:.1%} of the rise), valid {t_valid:.0f}, by code {dict(t_codes.most_common())}")
    print(f"    tastytrade shares / IBKR's NASDAQ {t_total / i_shares['NASDAQ'] if i_shares['NASDAQ'] else float('nan'):.3f}, / IBKR's whole tape {t_total / i_total:.3f}")

    # Same price and size within 3 s: is tastytrade's set inside IBKR's, and who gets there first?
    unused = defaultdict(list)
    for r in i_in:
        unused[(round(r[2], 4), r[3])].append(r)
    matched = []
    unmatched = []
    for r in t_in:
        bucket = unused.get((round(r[2], 4), r[3]), [])
        hit = next((x for x in bucket if abs(x[1] - r[1] / 1000) <= 3), None)
        if hit:
            bucket.remove(hit)
            matched.append((hit[0] - r[0], hit[4]))
        else:
            unmatched.append(r)
    leads = sorted(m[0] for m in matched)
    print(f"    matched to an IBKR print: {len(matched)} of {len(t_in)} ({len(matched) / len(t_in):.1%}), IBKR venues {dict(Counter(m[1] for m in matched).most_common(5))}")
    if leads:
        print(f"    IBKR arrival minus tastytrade arrival: median {pct(leads, 0.5):.0f} ms, p10 {pct(leads, 0.1):.0f}, p90 {pct(leads, 0.9):.0f}, p99 {pct(leads, 0.99):.0f}, max {leads[-1]:.0f}")
    if unmatched:
        print(f"    unmatched tastytrade: {sum(r[3] for r in unmatched):.0f} shares, sample {[(r[2], r[3]) for r in unmatched[:5]]}")

    # How IBKR's prints are bunched on arrival.
    arrivals = sorted({r[0] for r in i_in})
    gaps = sorted(y - x for x, y in zip(arrivals, arrivals[1:]))
    if gaps:
        print(f"  IBKR gaps between arrivals: n={len(gaps)} median {pct(gaps, 0.5):.0f} ms, p90 {pct(gaps, 0.9):.0f}, p99 {pct(gaps, 0.99):.0f}, max {gaps[-1]:.0f}")

    # Prints past the quote carried on tastytrade's own prints, for the 3% guard.
    far = []
    for r in t_in:
        if r[7] and r[8] and r[7] > 0 and r[8] > 0 and (r[2] > r[8] * 1.03 or r[2] < r[7] * 0.97):
            far.append(r)
    print(f"  tastytrade prints over 3% past their own quote: {len(far)}, of which valid {sum(1 for r in far if r[5])}")

    # Dry run of the watchdog: each shown tastytrade print starts a clock unless one is running, and any
    # IBKR print stops it. Going over the limit would hand the tape to tastytrade until IBKR's next print.
    i_arrivals = [r[0] for r in i_in]
    waits = []
    for r in t_in:
        if r[4] == "O":
            continue
        k = bisect.bisect_right(i_arrivals, r[0])
        waits.append(i_arrivals[k] - r[0] if k < len(i_arrivals) else None)
    known = sorted(w for w in waits if w is not None)
    print(f"  wait from a tastytrade print to IBKR's next print: median {pct(known, 0.5):.0f} ms, p99 {pct(known, 0.99):.0f}, max {known[-1] if known else 0:.0f}")
    for limit in (2000, 3000, 5000):
        trips = 0
        waiting_since = None
        k = 0
        for r in t_in:
            if r[4] == "O":
                continue
            while k < len(i_arrivals) and i_arrivals[k] <= r[0]:
                if waiting_since is not None and i_arrivals[k] - waiting_since > limit:
                    trips += 1
                waiting_since = None
                k += 1
            if waiting_since is None:
                waiting_since = r[0]
        print(f"    watchdog limit {limit} ms: would trip {trips} times")

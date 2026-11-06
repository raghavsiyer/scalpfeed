# Which of IBKR's prints are missing from tastytrade, print by print, and what the union of the two comes to.
# A tastytrade print is matched to an IBKR print with the same price and whole-share size in the nearest
# second, venues that could carry it first. Each IBKR print is used once.
#   python -I missing_from_tasty.py <ibkr.tsv from ibkr_raw.py --capture> <capture.jsonl from ScalpFeedProbe>
import json
import sys
from collections import Counter, defaultdict
from math import floor

SHARED = ("NASDAQ", "BEX", "PSX", "FINRA")
PRICE_TOLERANCE = float(sys.argv[3]) if len(sys.argv) > 3 else 0.00011  # A sub-penny price can be rounded differently.


def pct(values, p):
    return values[min(int(len(values) * p), len(values) - 1)] if values else float("nan")


def same_price(a, b):
    # IBKR rounds some stocks' prices to the cent, and the feeds can round a sub-penny price differently.
    return abs(a - b) <= PRICE_TOLERANCE or abs(round(a, 2) - b) < 1e-7 or abs(round(b, 2) - a) < 1e-7


ibkr = defaultdict(list)
for line in open(sys.argv[1], encoding="utf-8"):
    p = line.rstrip("\n").split("\t")
    if len(p) == 8 and p[2] == "SMART":
        ibkr[p[1]].append({"arrival": float(p[0]), "s": int(float(p[3])), "price": float(p[4]), "size": float(p[5]), "venue": p[6], "cond": p[7], "used": False})

tasty = defaultdict(list)
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
    elif kind == "FEED_DATA" and message.get("channel") == 1:
        data = message["data"]
        for i in range(0, len(data) - 1, 2):
            fields = layouts.get((1, data[i]))
            if not fields:
                continue
            for j in range(0, len(data[i + 1]), len(fields)):
                row = dict(zip(fields, data[i + 1][j : j + len(fields)]))
                if data[i] == "TimeAndSale" and not row.get("eventFlags", 0) & 2:
                    tasty[row["eventSymbol"]].append({"arrival": float(arrival), "ms": row["time"], "price": row["price"], "size": float(row["size"]), "valid": row["validTick"], "cond": row.get("exchangeSaleConditions") or "", "matched": None})
                elif data[i] == "Trade" and isinstance(row.get("dayVolume"), (int, float)):
                    volumes[row["eventSymbol"]].append((float(arrival), row["dayVolume"]))

for symbol in sorted(ibkr):
    i_all = sorted(ibkr[symbol], key=lambda r: r["arrival"])
    t_all = sorted(tasty.get(symbol, []), key=lambda r: r["arrival"])
    if not t_all:
        continue
    # Whole seconds well inside both recordings, so a print can't lose its partner to the edge.
    begin = max(i_all[0]["s"], t_all[0]["ms"] / 1000) + 10
    end = min(i_all[-1]["s"], t_all[-1]["ms"] / 1000) - 10
    t_in = [r for r in t_all if begin <= r["ms"] / 1000 <= end]
    i_in = [r for r in i_all if begin - 5 <= r["s"] <= end + 5]
    index = defaultdict(list)
    for r in i_in:
        index[floor(r["size"])].append(r)
    for r in t_in:
        candidates = [x for x in index.get(floor(r["size"]), []) if not x["used"] and abs(x["s"] - r["ms"] // 1000) <= 5 and same_price(x["price"], r["price"])]
        candidates.sort(key=lambda x: (x["venue"] not in SHARED, abs(x["price"] - r["price"]) > 1e-7, abs(x["s"] - r["ms"] // 1000)))
        if candidates:
            candidates[0]["used"] = True
            r["matched"] = candidates[0]
            if abs(candidates[0]["price"] - r["price"]) > 1e-7:
                r["price_differs"] = True
    inner = [r for r in i_in if begin <= r["s"] <= end]
    arrival_begin = min((r["arrival"] for r in t_in), default=0)
    arrival_end = max((r["arrival"] for r in t_in), default=0)
    vol = [v for a, v in volumes[symbol] if arrival_begin <= a <= arrival_end]
    rise = vol[-1] - vol[0] if len(vol) > 1 else float("nan")

    print(f"\n{symbol}: {end - begin:.0f} s, dayVolume rise {rise:.0f}")
    matched = [r for r in t_in if r["matched"]]
    fractional = [r for r in t_in if r["size"] != floor(r["size"])]
    print(f"  tastytrade {len(t_in)} prints, {sum(r['size'] for r in t_in):.0f} shares; found on IBKR's tape {len(matched)}; not found {len(t_in) - len(matched)}, {sum(r['size'] for r in t_in if not r['matched']):.0f} shares; fractional sizes {len(fractional)}")
    print(f"  matched prints by IBKR venue: {dict(Counter(r['matched']['venue'] for r in matched).most_common())}")
    differing = [r for r in matched if r.get("price_differs")]
    if differing:
        print(f"  matched with a slightly different price: {len(differing)}, first 6 (tastytrade, IBKR): {[(r['price'], r['matched']['price']) for r in differing[:6]]}")
    behind = sorted(r["matched"]["arrival"] - r["arrival"] for r in matched)
    print(f"  IBKR arrival minus tastytrade arrival on matches: min {behind[0] if behind else 0:.0f} ms, p1 {pct(behind, 0.01):.0f}, median {pct(behind, 0.5):.0f}, p99 {pct(behind, 0.99):.0f}, max {behind[-1] if behind else 0:.0f}; IBKR first on {sum(1 for b in behind if b < 0)}")
    print(f"  print second, IBKR minus tastytrade: {dict(sorted(Counter(r['matched']['s'] - int(r['ms'] // 1000) for r in matched).items()))}")
    print("  IBKR by venue: venue, prints, shares, also on tastytrade (shares), missing from tastytrade (prints, shares, %)")
    venues = sorted({r["venue"] for r in inner}, key=lambda v: -sum(r["size"] for r in inner if r["venue"] == v))
    for venue in venues:
        rows = [r for r in inner if r["venue"] == venue]
        used = [r for r in rows if r["used"]]
        missing = [r for r in rows if not r["used"]]
        shares = sum(r["size"] for r in rows)
        gone = sum(r["size"] for r in missing)
        print(f"    {venue:9} {len(rows):>6} {shares:>12.0f} {sum(r['size'] for r in used):>12.0f}   {len(missing):>6} {gone:>12.0f} {gone / shares:7.1%}")
    finra = [r for r in inner if r["venue"] == "FINRA"]
    gone = [r for r in finra if not r["used"]]
    kept = [r for r in finra if r["used"]]
    if gone and kept:
        def describe(rows):
            sizes = sorted(r["size"] for r in rows)
            sub = sum(1 for r in rows if abs(r["price"] * 100 - round(r["price"] * 100)) > 1e-6)
            return f"{len(rows)} prints, median size {pct(sizes, 0.5):.0f}, p90 {pct(sizes, 0.9):.0f}, odd lots {sum(1 for r in rows if 'I' in r['cond']) / len(rows):.1%}, sub-penny {sub / len(rows):.1%}, conditions {dict(Counter(r['cond'] for r in rows).most_common(5))}"
        print(f"  FINRA also on tastytrade: {describe(kept)}")
        print(f"  FINRA missing from it:    {describe(gone)}")
    union = sum(r["size"] for r in t_in) + sum(r["size"] for r in inner if not r["used"])
    ibkr_shares = sum(r["size"] for r in inner)
    print(f"  union of both: {union:.0f} shares; IBKR's tape {ibkr_shares:.0f}; union/tape {union / ibkr_shares:.3%}; union/dayVolume rise {union / rise:.3%}")
    unmatched = [r for r in t_in if not r["matched"]]
    under_one = [r for r in unmatched if r["size"] < 1]
    whole_part = sum(floor(r["size"]) for r in t_in if r["matched"])
    fraction = sum(r["size"] - floor(r["size"]) for r in t_in)
    print(f"  tastytrade prints with no IBKR partner: {len(unmatched)}, of which under one share {len(under_one)}; their shares {sum(r['size'] for r in unmatched):.1f}; fractions of a share across all tastytrade prints {fraction:.1f}")
    decimals = sum(1 for r in inner if abs(r["price"] * 100 - round(r["price"] * 100)) > 1e-6)
    t_decimals = sum(1 for r in t_in if abs(r["price"] * 100 - round(r["price"] * 100)) > 1e-6)
    print(f"  prices finer than a cent: IBKR {decimals} of {len(inner)}, tastytrade {t_decimals} of {len(t_in)}; typical price {pct(sorted(r['price'] for r in inner), 0.5)}")
    venue_rule = sum(r["size"] for r in t_in) + sum(r["size"] for r in inner if r["venue"] not in SHARED)
    print(f"  today's venue rule: {venue_rule:.0f} shares, {venue_rule / ibkr_shares:.3%} of the tape")
    buckets = defaultdict(lambda: [0.0, 0.0])
    for r in inner:
        buckets[r["s"] // 10][0] += r["size"]
        if not r["used"]:
            buckets[r["s"] // 10][1] += r["size"]
    for r in t_in:
        buckets[int(r["ms"] // 10000)][1] += r["size"]
    ratios = sorted(b[1] / b[0] for b in buckets.values() if b[0] > 0)
    print(f"  union/tape per 10 s: p1 {pct(ratios, 0.01):.1%}, p10 {pct(ratios, 0.1):.1%}, median {pct(ratios, 0.5):.1%}, p90 {pct(ratios, 0.9):.1%}, p99 {pct(ratios, 0.99):.1%}")
    not_found = [r for r in t_in if not r["matched"]]
    if not_found:
        print(f"  tastytrade prints not found on IBKR, first 8 (price, size, valid, cond): {[(r['price'], r['size'], r['valid'], r['cond']) for r in not_found[:8]]}")

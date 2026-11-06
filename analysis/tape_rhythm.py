# What the tape's pauses are made of: the gaps between rows reaching Quantower in merged mode, what ended each
# gap, whether trades happened during it, and what evenly spacing IBKR's batches would do and cost.
#   python -I tape_rhythm.py <tape.tsv from ibkr_raw.py --capture> <capture.jsonl from ScalpFeedProbe>
import json
import sys
from collections import Counter, defaultdict
from math import floor

CARRIED = {"NASDAQ", "BEX", "PSX"}


def pct(values, p):
    return values[min(int(len(values) * p), len(values) - 1)] if values else float("nan")


def same_price(a, b):
    return abs(a - b) <= 0.00011 or (b >= 1 and abs(a - b) <= 0.00501 and abs(b * 100 - round(b * 100)) < 1e-6) or (a >= 1 and abs(a - b) <= 0.00501 and abs(a * 100 - round(a * 100)) < 1e-6)


ibkr = defaultdict(list)
for line in open(sys.argv[1], encoding="utf-8"):
    p = line.rstrip("\n").split("\t")
    if len(p) == 8 and p[2] == "SMART":
        ibkr[p[1]].append({"arrival": float(p[0]), "s": int(float(p[3])), "price": float(p[4]), "size": float(p[5]), "venue": p[6], "used": False})

tasty = defaultdict(list)
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
            if data[i] != "TimeAndSale" or not fields:
                continue
            for j in range(0, len(data[i + 1]), len(fields)):
                row = dict(zip(fields, data[i + 1][j : j + len(fields)]))
                if not row.get("eventFlags", 0) & 2:
                    tasty[row["eventSymbol"]].append({"arrival": float(arrival), "ms": row["time"], "price": row["price"], "size": float(row["size"])})


def gaps_of(times):
    times = sorted(times)
    return sorted(y - x for x, y in zip(times, times[1:]))


def describe(times, span_s):
    gaps = gaps_of(times)
    long = {limit: sum(1 for g in gaps if g >= limit) for limit in (200, 300, 500, 1000)}
    per_min = {k: round(v / span_s * 60, 1) for k, v in long.items()}
    return f"gap median {pct(gaps, 0.5):.0f} ms, p90 {pct(gaps, 0.9):.0f}, p99 {pct(gaps, 0.99):.0f}; a minute, gaps of 200+ ms {per_min[200]}, 300+ {per_min[300]}, 500+ {per_min[500]}, 1 s+ {per_min[1000]}"


for symbol in sorted(ibkr):
    i_rows = sorted(ibkr[symbol], key=lambda r: r["arrival"])
    t_rows = sorted(tasty.get(symbol, []), key=lambda r: r["arrival"])
    if not t_rows:
        continue
    begin = max(i_rows[0]["arrival"], t_rows[0]["arrival"]) + 5000
    end = min(i_rows[-1]["arrival"], t_rows[-1]["arrival"]) - 5000
    index = defaultdict(list)
    for r in i_rows:
        index[floor(r["size"])].append(r)
    for r in t_rows:
        options = [x for x in index.get(floor(r["size"]), []) if not x["used"] and abs(x["s"] - r["ms"] // 1000) <= 3 and same_price(x["price"], r["price"])]
        options.sort(key=lambda x: (x["venue"] not in CARRIED and x["venue"] != "FINRA", abs(x["s"] - r["ms"] // 1000)))
        if options:
            options[0]["used"] = True
    t_times = [r["arrival"] for r in t_rows if begin <= r["arrival"] <= end]
    only = [r for r in i_rows if not r["used"] and r["venue"] not in CARRIED and begin <= r["arrival"] <= end]
    i_times = [r["arrival"] for r in only]
    span = (end - begin) / 1000
    merged = sorted([(a, "tastytrade") for a in t_times] + [(a, "IBKR") for a in i_times])
    print(f"\n{symbol}: {span:.0f} s; rows from tastytrade {len(t_times)}, only on IBKR {len(i_times)}, in {len(set(i_times))} deliveries ({len(i_times) / max(len(set(i_times)), 1):.1f} rows each)")
    print(f"  tastytrade alone: {describe(t_times, span)}")
    print(f"  merged tape now:  {describe([m[0] for m in merged], span)}")

    # What ended each pause, and did anything trade during it?
    ended = Counter()
    traded_during = 0
    long_gaps = 0
    rows_after = []
    for (a, _), (b, source) in zip(merged, merged[1:]):
        if b - a < 300:
            continue
        long_gaps += 1
        ended[source] += 1
        if source == "IBKR":
            lump = [r for r in only if r["arrival"] == b]
            rows_after.append(len(lump))
            # IBKR stamps whole seconds, so this asks whether a print's second overlaps the gap.
            if any(r["s"] * 1000 + 999 >= a and r["s"] * 1000 <= b for r in lump):
                traded_during += 1
    if long_gaps:
        print(f"  gaps of 300+ ms: {long_gaps}; ended by IBKR {ended['IBKR']} ({ended['IBKR'] / long_gaps:.0%}), by tastytrade {ended['tastytrade']}")
        if rows_after:
            print(f"    those IBKR lumps: median {pct(sorted(rows_after), 0.5)} rows; carrying a print from a second that overlaps the gap: {traded_during} of {ended['IBKR']} ({traded_during / ended['IBKR']:.0%})")

    # Spacing each IBKR batch out evenly: rows wait their turn, so nothing shows sooner than it does now.
    deliveries = sorted(Counter(i_times).items())
    for spread in (150, 300, 600):
        released = []
        waits = []
        free_at = 0.0
        for arrival, count in deliveries:
            step = spread / count
            start = max(arrival, free_at)
            for n in range(count):
                moment = start + n * step
                released.append(moment)
                waits.append(moment - arrival)
            free_at = start + count * step
        waits.sort()
        print(f"  batches spread over {spread} ms: {describe(sorted(t_times + released), span)}; IBKR-only rows wait a further median {pct(waits, 0.5):.0f} ms, p90 {pct(waits, 0.9):.0f}")

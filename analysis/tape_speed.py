# How fast each source delivers the same prints, by arrival on this PC's clock, so its error doesn't matter
# when comparing sources. Each IBKR source is matched to tastytrade's prints by price and size within 3 s.
#   python -I tape_speed.py <tsv from ibkr_raw.py --capture> <capture.jsonl from ScalpFeedProbe>
import json
import sys
from collections import Counter, defaultdict


def pct(values, p):
    return values[min(int(len(values) * p), len(values) - 1)] if values else float("nan")


sources = defaultdict(list)  # (symbol, source) -> arrival ms, print seconds, price, size, venue
for line in open(sys.argv[1], encoding="utf-8"):
    parts = line.rstrip("\n").split("\t")
    if len(parts) == 8 and parts[4] and parts[5]:
        sources[(parts[1], parts[2])].append((float(parts[0]), float(parts[3]), float(parts[4]), float(parts[5]), parts[6]))

tasty = defaultdict(list)  # symbol -> arrival ms, print ms, price, size
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
                    tasty[row["eventSymbol"]].append((float(arrival), row["time"], row["price"], float(row["size"])))
                elif data[i] == "Trade" and isinstance(row.get("dayVolume"), (int, float)):
                    volumes[row["eventSymbol"]].append((float(arrival), row["dayVolume"]))

for symbol in sorted(tasty):
    t_rows = sorted(tasty[symbol])
    mine = {s: sorted(rows) for (sym, s), rows in sources.items() if sym == symbol}
    if not mine:
        continue
    begin = max([t_rows[0][0]] + [r[0][0] for r in mine.values()])
    end = min([t_rows[-1][0]] + [r[-1][0] for r in mine.values()])
    t_in = [r for r in t_rows if begin <= r[0] <= end]
    vol = [v for a, v in volumes[symbol] if begin <= a <= end]
    rise = vol[-1] - vol[0] if len(vol) > 1 else float("nan")
    t_arrivals = sorted({r[0] for r in t_in})
    t_gaps = sorted(y - x for x, y in zip(t_arrivals, t_arrivals[1:]))
    t_lag = sorted(r[0] - r[1] for r in t_in)
    print(f"{symbol}: {(end - begin) / 1000:.0f} s overlap, dayVolume rise {rise:.0f}")
    print(f"  tastytrade: {len(t_in)} prints, {sum(r[3] for r in t_in):.0f} shares ({sum(r[3] for r in t_in) / rise:.1%}), deliveries {len(t_arrivals)}, gap median {pct(t_gaps, 0.5):.0f} ms, p99 {pct(t_gaps, 0.99):.0f}; print time to arrival median {pct(t_lag, 0.5):.0f} ms")
    for source, rows in sorted(mine.items()):
        inside = [r for r in rows if begin <= r[0] <= end]
        arrivals = sorted({r[0] for r in inside})
        gaps = sorted(y - x for x, y in zip(arrivals, arrivals[1:]))
        batch = len(inside) / len(arrivals) if arrivals else 0
        unused = defaultdict(list)
        for r in inside:
            unused[(round(r[2], 4), r[3])].append(r)
        behind = []
        for r in t_in:
            bucket = unused.get((round(r[2], 4), r[3]), [])
            hit = next((x for x in bucket if abs(x[1] - r[1] / 1000) <= 3), None)
            if hit:
                bucket.remove(hit)
                behind.append(hit[0] - r[0])
        behind.sort()
        shares = sum(r[3] for r in inside)
        print(f"  {source}: {len(inside)} rows, {shares:.0f} shares ({shares / rise:.1%}), deliveries {len(arrivals)} ({batch:.1f} rows each), gap median {pct(gaps, 0.5):.0f} ms, p90 {pct(gaps, 0.9):.0f}, p99 {pct(gaps, 0.99):.0f}")
        if behind:
            print(f"    behind tastytrade on {len(behind)} of {len(t_in)} shared prints: median {pct(behind, 0.5):.0f} ms, p10 {pct(behind, 0.1):.0f}, p90 {pct(behind, 0.9):.0f}, p99 {pct(behind, 0.99):.0f}, max {behind[-1]:.0f}")
        ahead = sum(1 for b in behind if b < 0)
        if behind:
            print(f"    ahead of tastytrade on {ahead} ({ahead / len(behind):.1%})")

"""Which volume reaches Trade.dayVolume but not the TimeAndSale tape?

python -I analyze_volume.py <capture.jsonl> <symbols,comma,separated>
"""
import json
import sys
from collections import defaultdict
from datetime import datetime, timedelta, timezone

ET = timezone(timedelta(hours=-5))  # November data, EST.
LIVE = 1
MATCH_MS = 3


def et(ms):
    return datetime.fromtimestamp(ms / 1000, ET).strftime("%H:%M:%S.%f")[:-3]


fields = {}
trades = defaultdict(list)
prints = defaultdict(list)
for line in open(sys.argv[1], encoding="utf-8"):
    arrival, payload = line.split("\t", 1)
    msg = json.loads(payload)
    if msg.get("channel") != LIVE:
        continue
    if msg["type"] == "FEED_CONFIG":
        fields.update(msg.get("eventFields", {}))
    elif msg["type"] == "FEED_DATA":
        data = msg["data"]
        for i in range(0, len(data) - 1, 2):
            name, values = data[i], data[i + 1]
            if name not in ("Trade", "TimeAndSale"):
                continue
            f = fields[name]
            for j in range(0, len(values) - len(f) + 1, len(f)):
                row = dict(zip(f, values[j : j + len(f)]))
                row["_arrival"] = float(arrival)
                (trades if name == "Trade" else prints)[row["eventSymbol"]].append(row)

for sym in sys.argv[2].split(","):
    tr = [t for t in trades[sym] if isinstance(t["dayVolume"], (int, float))]
    tape = prints[sym]
    total = tr[-1]["dayVolume"] - tr[0]["dayVolume"]
    tape_total = sum(p["size"] for p in tape)
    valid_total = sum(p["size"] for p in tape if p["validTick"])

    deltas = [(b["dayVolume"] - a["dayVolume"], b) for a, b in zip(tr, tr[1:]) if b["dayVolume"] > a["dayVolume"]]
    deltas.sort(key=lambda d: -d[0])

    # Volume jumps with no print of about that size arriving at the same moment.
    arrivals = sorted((p["_arrival"], p["size"]) for p in tape)
    unmatched = 0.0
    biggest = []
    for delta, row in deltas:
        lo, hi = row["_arrival"] - MATCH_MS, row["_arrival"] + MATCH_MS
        near = sum(s for t, s in arrivals if lo <= t <= hi) if delta > 2000 else None
        if near is not None and near < delta * 0.5:
            unmatched += delta
            biggest.append((delta, row, near))

    top10 = sum(d for d, _ in deltas[:10])
    print(f"\n{sym}: dayVolume rose {total:,.0f} | tape all {tape_total:,.0f} ({tape_total / total:.0%}), valid {valid_total:,.0f} ({valid_total / total:.0%})")
    print(f"  jumps: {len(deltas)}, 10 largest = {top10:,.0f} ({top10 / total:.0%} of the rise); jumps over 2000 shares with no matching print = {unmatched:,.0f} ({unmatched / total:.0%})")
    for delta, row, near in biggest[:6]:
        print(f"    +{delta:,.0f} at {et(row['_arrival'])} ET; Trade px {row['price']} sz {row['size']} time {et(row['time'])}; tape within {MATCH_MS} ms: {near:,.0f}")

    # Sizes in the tape versus dayVolume over a burst of small steps, the ordinary case.
    small = sum(d for d, _ in deltas if d <= 2000)
    print(f"  jumps of 2000 or less sum to {small:,.0f} ({small / total:.0%}); tape prints of 2000 or less sum to {sum(p['size'] for p in tape if p['size'] <= 2000):,.0f}")
    big_prints = sorted((p["size"] for p in tape), reverse=True)[:5]
    print(f"  largest tape prints: {[int(s) for s in big_prints]}")

# Two ScalpFeedProbe recordings of the same symbols at different aggregation periods, compared over the
# seconds both were running: does the period change which TimeAndSale prints arrive?
#   python -I aggregation_compare.py <capture A.jsonl> <capture B.jsonl>
import json
import sys
from collections import Counter, defaultdict


def load(path):
    layouts = {}
    prints = defaultdict(list)
    volumes = defaultdict(list)
    period = None
    for line in open(path, encoding="utf-8"):
        try:
            arrival, payload = line.split("\t", 1)
            message = json.loads(payload)
        except ValueError:
            continue
        kind = message.get("type")
        if kind == "FEED_CONFIG":
            if message.get("channel") == 1:
                period = message.get("aggregationPeriod", period)
            for name, fields in message.get("eventFields", {}).items():
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
                        prints[row["eventSymbol"]].append((float(arrival), row["time"], row["price"], row["size"], row["sequence"]))
                    elif data[i] == "Trade" and isinstance(row.get("dayVolume"), (int, float)):
                        volumes[row["eventSymbol"]].append((float(arrival), row["dayVolume"]))
    return period, prints, volumes


period_a, a, vol_a = load(sys.argv[1])
period_b, b, vol_b = load(sys.argv[2])
print(f"aggregation A={period_a} B={period_b}")
for symbol in sorted(set(a) & set(b)):
    begin = max(a[symbol][0][1], b[symbol][0][1])
    end = min(a[symbol][-1][1], b[symbol][-1][1])
    in_a = [p for p in a[symbol] if begin <= p[1] <= end]
    in_b = [p for p in b[symbol] if begin <= p[1] <= end]
    keys_a = Counter((p[1], p[4], p[2], p[3]) for p in in_a)
    keys_b = Counter((p[1], p[4], p[2], p[3]) for p in in_b)
    gaps_a = sorted(y - x for x, y in zip(sorted({p[0] for p in in_a}), sorted({p[0] for p in in_a})[1:]))
    gaps_b = sorted(y - x for x, y in zip(sorted({p[0] for p in in_b}), sorted({p[0] for p in in_b})[1:]))
    lag_a = sorted(p[0] - p[1] for p in in_a)
    lag_b = sorted(p[0] - p[1] for p in in_b)
    print(f"{symbol}: {(end - begin) / 1000:.0f} s by print time")
    print(f"  A {len(in_a)} prints, {sum(p[3] for p in in_a):.0f} shares; B {len(in_b)} prints, {sum(p[3] for p in in_b):.0f} shares")
    print(f"  in A only {sum((keys_a - keys_b).values())}, in B only {sum((keys_b - keys_a).values())}, in both {sum((keys_a & keys_b).values())}")
    if lag_a and lag_b:
        print(f"  arrival minus print time, median: A {lag_a[len(lag_a) // 2]:.0f} ms, B {lag_b[len(lag_b) // 2]:.0f} ms; p99 A {lag_a[int(len(lag_a) * 0.99)]:.0f}, B {lag_b[int(len(lag_b) * 0.99)]:.0f}")
    if gaps_a and gaps_b:
        print(f"  gaps between deliveries, median: A {gaps_a[len(gaps_a) // 2]:.0f} ms, B {gaps_b[len(gaps_b) // 2]:.0f} ms")

# What tastytrade's TimeAndSale looked like in one recording, per symbol: exchange codes, how late
# prints arrived, what the plugin's filter would do with them, and dayVolume's rise against the shares seen.
#   python -I tasty_session.py <capture.jsonl from ScalpFeedProbe>
import json
import sys
from collections import Counter, defaultdict

layouts = {}
prints = defaultdict(list)
volumes = defaultdict(list)
for line in open(sys.argv[1], encoding="utf-8"):
    try:
        arrival, payload = line.split("\t", 1)
        message = json.loads(payload)
    except ValueError:
        continue  # The last line of a file still being written.
    arrival = float(arrival)
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
                    prints[row["eventSymbol"]].append((arrival, row))
                elif data[i] == "Trade" and isinstance(row.get("dayVolume"), (int, float)):
                    volumes[row["eventSymbol"]].append((arrival, row["dayVolume"]))

for symbol, rows in sorted(prints.items()):
    span = (rows[-1][0] - rows[0][0]) / 1000
    shares = Counter()
    counts = Counter()
    for _, r in rows:
        shares[r["exchangeCode"]] += r["size"]
        counts[r["exchangeCode"]] += 1
    lags = sorted(a - r["time"] for a, r in rows)
    late = [(a - r["time"], r) for a, r in rows if a - r["time"] > 5000]
    valid = sum(1 for _, r in rows if r["validTick"])
    conditions = Counter(r.get("exchangeSaleConditions") for _, r in rows)
    print(f"{symbol}: {len(rows)} prints in {span:.0f} s, {sum(shares.values()):.0f} shares, valid {valid}")
    print(f"  by code: {dict(counts.most_common())}")
    print(f"  shares:  {dict(shares.most_common())}")
    print(f"  arrival minus print time: median {lags[len(lags) // 2]:.0f} ms, p99 {lags[int(len(lags) * 0.99)]:.0f} ms, max {lags[-1]:.0f} ms")
    print(f"  conditions: {dict(conditions.most_common(8))}")
    if late:
        print(f"  late by over 5 s: {len(late)} prints, {sum(r['size'] for _, r in late):.0f} shares, codes {dict(Counter(r['exchangeCode'] for _, r in late))}, conditions {dict(Counter(r.get('exchangeSaleConditions') for _, r in late).most_common(5))}, oldest {max(l for l, _ in late) / 1000:.0f} s")
    vs = volumes[symbol]
    if len(vs) > 1:
        first = next((a for a, _ in vs), None)
        seen = sum(r["size"] for a, r in rows if a >= first and r["validTick"] and a <= vs[-1][0])
        print(f"  dayVolume rise {vs[-1][1] - vs[0][1]:.0f}, valid shares seen over the same time {seen:.0f}")

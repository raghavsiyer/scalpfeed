"""Match individual prints between the tastytrade and IBKR tapes, recorded together.

Shows which venues tastytrade's tape carries, and which feed delivered each shared print first.
Both feeds were stamped on arrival by the same PC clock, so the clock's own error cancels out.

python -I compare_tape.py <tastytrade capture.jsonl> <ibkr capture.tsv>
"""
import json
import sys
from bisect import bisect_left
from collections import defaultdict

WINDOW_MS = 4000


def pct(values, p):
    s = sorted(values)
    return s[min(len(s) - 1, int(p * len(s)))] if s else float("nan")


fields = {}
tasty = defaultdict(list)
for line in open(sys.argv[1], encoding="utf-8"):
    arrival, payload = line.split("\t", 1)
    msg = json.loads(payload)
    if msg.get("channel") != 1:
        continue
    if msg["type"] == "FEED_CONFIG":
        fields.update(msg.get("eventFields", {}))
    elif msg["type"] == "FEED_DATA":
        data = msg["data"]
        for i in range(0, len(data) - 1, 2):
            if data[i] != "TimeAndSale":
                continue
            f = fields["TimeAndSale"]
            values = data[i + 1]
            for j in range(0, len(values) - len(f) + 1, len(f)):
                r = dict(zip(f, values[j : j + len(f)]))
                if isinstance(r["price"], (int, float)):
                    tasty[r["eventSymbol"]].append((float(arrival), round(r["price"], 4), round(r["size"], 2), r["validTick"]))

ibkr = defaultdict(list)
for line in open(sys.argv[2], encoding="utf-8"):
    p = line.rstrip("\n").split("\t")
    if p[1] == "T":
        ibkr[p[2]].append((float(p[0]), round(float(p[3]), 4), round(float(p[4]), 2), p[5], p[6]))

for sym in sorted(set(tasty) & set(ibkr)):
    tp, ip = tasty[sym], ibkr[sym]
    start, stop = max(tp[0][0], ip[0][0]) + WINDOW_MS, min(tp[-1][0], ip[-1][0]) - WINDOW_MS
    window_t = [x for x in tp if start <= x[0] <= stop]
    window_i = [x for x in ip if start <= x[0] <= stop]

    # Each tastytrade print claims the nearest unused IBKR print with the same price and size.
    by_key = defaultdict(list)
    for k, x in enumerate(ip):
        by_key[(x[1], x[2])].append(k)
    used = set()
    deltas = []
    matched_i = set()
    for t, price, size, _ in window_t:
        best, gap = None, WINDOW_MS + 1
        for k in by_key.get((price, size), ()):
            if k not in used and abs(ip[k][0] - t) < gap:
                best, gap = k, abs(ip[k][0] - t)
        if best is not None:
            used.add(best)
            matched_i.add(best)
            deltas.append(ip[best][0] - t)

    total = defaultdict(lambda: [0, 0, 0.0, 0.0])
    for k, x in enumerate(ip):
        if start <= x[0] <= stop:
            row = total[x[3]]
            row[0] += 1
            row[2] += x[2]
            if k in matched_i:
                row[1] += 1
                row[3] += x[2]

    print(f"\n{sym}: {(stop - start) / 1000:.0f} s, tastytrade {len(window_t)} prints, IBKR {len(window_i)} prints; {len(deltas)} tastytrade prints ({len(deltas) / max(len(window_t), 1):.0%}) found on IBKR's tape")
    print(f"  {'venue':10} {'IBKR prints':>11} {'on tastytrade':>14} {'share':>7} {'IBKR shares':>12}")
    for venue, row in sorted(total.items(), key=lambda kv: -kv[1][2]):
        print(f"  {venue:10} {row[0]:11} {row[1]:14} {row[1] / row[0]:7.0%} {row[2]:12,.0f}")

    if deltas:
        first = sum(1 for d in deltas if d > 0) / len(deltas)
        print(f"  head to head on shared prints: tastytrade arrived first {first:.0%}; IBKR later by median {pct(deltas, .5):.0f} ms, p10 {pct(deltas, .1):.0f}, p90 {pct(deltas, .9):.0f}, p99 {pct(deltas, .99):.0f}")

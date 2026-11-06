"""IBKR and tastytrade recorded at the same moment, stamped by the same PC clock.

python -I compare_feeds.py <tastytrade capture.jsonl> <ibkr capture.tsv>
"""
import json
import sys
from bisect import bisect_right
from collections import Counter, defaultdict

SAMPLE_MS = 100
WINDOW_MS = 3000


def pct(values, p):
    s = sorted(values)
    return s[min(len(s) - 1, int(p * len(s)))] if s else float("nan")


fields = {}
tasty_quotes = defaultdict(list)
tasty_prints = defaultdict(list)
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
            name, values = data[i], data[i + 1]
            if name not in ("Quote", "TimeAndSale"):
                continue
            f = fields[name]
            for j in range(0, len(values) - len(f) + 1, len(f)):
                r = dict(zip(f, values[j : j + len(f)]))
                if name == "Quote" and isinstance(r["bidPrice"], (int, float)) and isinstance(r["askPrice"], (int, float)):
                    tasty_quotes[r["eventSymbol"]].append((float(arrival), r["bidPrice"], r["askPrice"]))
                elif name == "TimeAndSale" and isinstance(r["price"], (int, float)):
                    tasty_prints[r["eventSymbol"]].append((float(arrival), r["price"], r["size"], r["validTick"]))

ib_quotes = defaultdict(list)
ib_ticks = defaultdict(list)
ib_prints = defaultdict(list)
for line in open(sys.argv[2], encoding="utf-8"):
    p = line.rstrip("\n").split("\t")
    t, kind, sym = float(p[0]), p[1], p[2]
    if kind == "Q":
        bid, ask = float(p[3]), float(p[4])
        if bid > 0 and ask > 0 and bid == bid and ask == ask:
            ib_quotes[sym].append((t, bid, ask))
    elif kind == "B":
        ib_ticks[sym].append((t, float(p[3]), float(p[4])))
    elif kind == "T":
        ib_prints[sym].append((t, float(p[3]), float(p[4]), p[5], p[6]))


def compare(label, a_name, a, b_name, b):
    """Sample both on one timeline and compare what each showed at that instant."""
    if not a or not b:
        print(f"    {label}: not enough data")
        return
    start, stop = max(a[0][0], b[0][0]), min(a[-1][0], b[-1][0])
    at, bt = [x[0] for x in a], [x[0] for x in b]
    same = bwider = awider = total = 0
    spread_a, spread_b = [], []
    t = start
    while t < stop:
        x = a[bisect_right(at, t) - 1]
        y = b[bisect_right(bt, t) - 1]
        sa, sb = round(x[2] - x[1], 4), round(y[2] - y[1], 4)
        if sa > 0 and sb > 0:
            spread_a.append(sa)
            spread_b.append(sb)
            total += 1
            if abs(x[1] - y[1]) < 1e-6 and abs(x[2] - y[2]) < 1e-6:
                same += 1
            if sa > sb + 1e-6:
                awider += 1
            elif sb > sa + 1e-6:
                bwider += 1
        t += SAMPLE_MS
    if not total:
        print(f"    {label}: no comparable samples")
        return
    print(f"    {label}: identical bid and ask {same / total:.0%} of the time | {a_name} wider {awider / total:.0%}, {b_name} wider {bwider / total:.0%}, equal {1 - (awider + bwider) / total:.0%} | median spread {a_name} {pct(spread_a, .5):.4f}, {b_name} {pct(spread_b, .5):.4f}")


def lead(a_name, a, b_name, b):
    """When both show the same new bid and ask, who showed it first, and by how much?"""
    changes = lambda xs: [x for k, x in enumerate(xs) if k == 0 or x[1:3] != xs[k - 1][1:3]]
    ca, cb = changes(a), changes(b)
    deltas = []
    for t, bid, ask in ca:
        near = [u for u, b2, a2 in cb if abs(u - t) <= WINDOW_MS and abs(b2 - bid) < 1e-6 and abs(a2 - ask) < 1e-6]
        if near:
            nearest = min(near, key=lambda u: abs(u - t))
            deltas.append(nearest - t)
    if deltas:
        first = sum(1 for d in deltas if d > 0) / len(deltas)
        print(f"    {a_name} vs {b_name}: {a_name} changes {len(ca)}, {b_name} changes {len(cb)}; matched {len(deltas)}; {a_name} first {first:.0%}; {b_name} minus {a_name} median {pct(deltas, .5):.0f} ms, p10 {pct(deltas, .1):.0f}, p90 {pct(deltas, .9):.0f}")


for sym in sorted(set(tasty_quotes) | set(ib_quotes)):
    print(f"\n{sym}")
    t, q, ba = tasty_quotes.get(sym, []), ib_quotes.get(sym, []), ib_ticks.get(sym, [])
    compare("tastytrade vs IBKR quote", "tasty", t, "ibkr", q)
    if ba:
        compare("tastytrade vs IBKR tick-by-tick bid/ask", "tasty", t, "ibkrTBT", ba)
        compare("IBKR quote vs IBKR tick-by-tick bid/ask", "ibkr", q, "ibkrTBT", ba)
    if t and q:
        lead("tasty", t, "ibkr", q)
    if t and ba:
        lead("tasty", t, "ibkrTBT", ba)

    tp, ip = tasty_prints.get(sym, []), ib_prints.get(sym, [])
    if tp and ip:
        start, stop = max(tp[0][0], ip[0][0]), min(tp[-1][0], ip[-1][0])
        tw = [x for x in tp if start <= x[0] <= stop]
        iw = [x for x in ip if start <= x[0] <= stop]
        print(f"    tape over the same {(stop - start) / 1000:.0f} s:")
        print(f"      tastytrade: {len(tw)} prints, {sum(x[2] for x in tw):,.0f} shares; valid {sum(1 for x in tw if x[3])} prints, {sum(x[2] for x in tw if x[3]):,.0f} shares")
        print(f"      IBKR:       {len(iw)} prints, {sum(x[2] for x in iw):,.0f} shares")
        by = Counter()
        for x in iw:
            by[x[3]] += x[2]
        total = sum(by.values()) or 1
        print("      IBKR volume by venue: " + ", ".join(f"{v} {n / total:.0%}" for v, n in by.most_common(10)))
        ratio = sum(x[2] for x in tw) / total
        print(f"      tastytrade shares as a share of IBKR's: {ratio:.0%}")
        print(f"      delivery: tastytrade median gap {pct([b[0] - a[0] for a, b in zip(tw, tw[1:]) if b[0] - a[0] > 2], .5):.0f} ms, IBKR median gap {pct([b[0] - a[0] for a, b in zip(iw, iw[1:]) if b[0] - a[0] > 2], .5):.0f} ms")

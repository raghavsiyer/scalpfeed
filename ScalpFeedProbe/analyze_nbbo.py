"""Does the feed's bid and ask behave like the true NBBO?

A regular whole-cent print should rarely land strictly between the true best bid and ask:
a better displayed price would have to exist, and if it did, it would be the best bid or
ask. Hidden orders explain a few, not a large share. Sub-penny prints are left out, since
midpoint and price-improved trades land inside any spread. Uses the bid and ask carried on
each print, so the 500 ms quote timer doesn't affect the result.

python -I analyze_nbbo.py <capture.jsonl> [<capture.jsonl> ...]
"""
import json
import sys
from collections import defaultdict

LIVE = 1
EPS = 1e-6


def num(x):
    return x if isinstance(x, (int, float)) else float("nan")


for path in sys.argv[1:]:
    fields = {}
    prints = defaultdict(list)
    quotes = defaultdict(list)
    for line in open(path, encoding="utf-8"):
        _, payload = line.split("\t", 1)
        msg = json.loads(payload)
        if msg.get("channel") != LIVE:
            continue
        if msg["type"] == "FEED_CONFIG":
            fields.update(msg.get("eventFields", {}))
        elif msg["type"] == "FEED_DATA":
            data = msg["data"]
            for i in range(0, len(data) - 1, 2):
                name, values = data[i], data[i + 1]
                if name not in ("TimeAndSale", "Quote"):
                    continue
                f = fields[name]
                for j in range(0, len(values) - len(f) + 1, len(f)):
                    row = dict(zip(f, values[j : j + len(f)]))
                    (prints if name == "TimeAndSale" else quotes)[row["eventSymbol"]].append(row)

    print(f"\n{path.split(chr(92))[-1]}")
    print(f"  {'':5} {'whole-cent':>10} {'at bid':>7} {'at ask':>7} {'INSIDE':>7} {'outside':>8} | {'spread 1c':>9} {'2c':>6} {'3c+':>6} {'median':>8}")
    for sym in sorted(prints):
        at_bid = at_ask = inside = outside = 0
        for r in prints[sym]:
            px, bid, ask = num(r["price"]), num(r["bidPrice"]), num(r["askPrice"])
            if not r["validTick"] or px != px or bid != bid or ask != ask or bid <= 0 or ask <= bid or px < 1:
                continue
            if abs(px * 100 - round(px * 100)) > 1e-4:
                continue
            if px < bid - EPS or px > ask + EPS:
                outside += 1
            elif abs(px - bid) < EPS:
                at_bid += 1
            elif abs(px - ask) < EPS:
                at_ask += 1
            else:
                inside += 1
        n = at_bid + at_ask + inside + outside
        spreads = sorted(round(num(q["askPrice"]) - num(q["bidPrice"]), 4) for q in quotes[sym] if num(q["askPrice"]) > num(q["bidPrice"]) > 0)
        if not n or not spreads:
            print(f"  {sym:5} too few whole-cent prints at $1 or more")
            continue
        one = sum(1 for s in spreads if s <= 0.0101) / len(spreads)
        two = sum(1 for s in spreads if 0.0101 < s <= 0.0201) / len(spreads)
        print(f"  {sym:5} {n:10} {at_bid / n:7.1%} {at_ask / n:7.1%} {inside / n:7.1%} {outside / n:8.1%} | {one:9.0%} {two:6.0%} {1 - one - two:6.0%} {spreads[len(spreads) // 2]:8.2f}")

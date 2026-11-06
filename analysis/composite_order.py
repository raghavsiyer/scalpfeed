"""Is Order with source COMPOSITE anything more than Quote in another shape?

python -I composite_order.py <capture.jsonl>
"""
import json
import sys
from collections import Counter, defaultdict

fields = {}
events = defaultdict(list)
for line in open(sys.argv[1], encoding="utf-8"):
    arrival, payload = line.split("\t", 1)
    msg = json.loads(payload)
    if msg["type"] == "FEED_CONFIG" and "eventFields" in msg:
        fields.update(msg["eventFields"])
    if msg["type"] != "FEED_DATA":
        continue
    data = msg["data"]
    for i in range(0, len(data) - 1, 2):
        name = data[i]
        if name not in ("Order", "Quote") or name not in fields:
            continue
        f = fields[name]
        values = data[i + 1]
        for j in range(0, len(values) - len(f) + 1, len(f)):
            r = dict(zip(f, values[j : j + len(f)]))
            events[r["eventSymbol"]].append((float(arrival), name, r))

for sym, ev in sorted(events.items()):
    ev.sort(key=lambda e: e[0])
    print(f"\n{sym}: {sum(1 for e in ev if e[1] == 'Order')} Order rows, {sum(1 for e in ev if e[1] == 'Quote')} Quote rows")
    print("  order codes:", Counter((e[2].get("exchangeCode"), e[2].get("marketMaker"), e[2].get("scope")) for e in ev if e[1] == "Order").most_common(5))

    t0 = ev[0][0]
    bid = ask = None
    matches = 0
    compared = 0
    quote_bid = quote_ask = None
    for t, name, r in ev:
        if name == "Quote":
            quote_bid, quote_ask = r.get("bidPrice"), r.get("askPrice")
        else:
            if r.get("orderSide") == "BUY":
                bid = r.get("price")
            elif r.get("orderSide") == "SELL":
                ask = r.get("price")
            if quote_bid is not None and bid is not None and ask is not None:
                compared += 1
                matches += bid == quote_bid and ask == quote_ask
    print(f"  order bid/ask equal to the latest Quote: {matches} of {compared}")

    arrivals = sorted({round(e[0]) for e in ev if e[1] == "Order"})
    gaps = [b - a for a, b in zip(arrivals, arrivals[1:])]
    qarrivals = sorted({round(e[0]) for e in ev if e[1] == "Quote"})
    qgaps = [b - a for a, b in zip(qarrivals, qarrivals[1:])]
    print(f"  gaps between deliveries, ms: Order {sorted(gaps)[:12]}")
    print(f"                               Quote {sorted(qgaps)[:12]}")

    for t, name, r in ev[:10]:
        if name == "Order":
            print(f"    +{(t - t0) / 1000:6.2f}s Order {r.get('orderSide')} {r.get('price')} x {r.get('size')} code {r.get('exchangeCode')} flags {r.get('eventFlags')} index {r.get('index')} time {r.get('time')}")
        else:
            print(f"    +{(t - t0) / 1000:6.2f}s Quote {r.get('bidPrice')} x {r.get('bidSize')} ({r.get('bidExchangeCode')}) / {r.get('askPrice')} x {r.get('askSize')} ({r.get('askExchangeCode')})")

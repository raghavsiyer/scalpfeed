"""Which events and symbols did the server actually serve in one capture?

python -I served_check.py <capture.jsonl>
"""
import json
import sys
from collections import Counter, defaultdict

fields = {}
rows = Counter()
codes = defaultdict(Counter)
shares = Counter()
profiles = {}
orders = defaultdict(Counter)
others = Counter()
samples = []

for line in open(sys.argv[1], encoding="utf-8"):
    arrival, payload = line.split("\t", 1)
    msg = json.loads(payload)
    kind = msg["type"]
    if kind == "FEED_CONFIG" and "eventFields" in msg:
        fields.update(msg["eventFields"])
    if kind != "FEED_DATA":
        if kind not in ("KEEPALIVE", "FEED_CONFIG", "SETUP", "AUTH_STATE", "CHANNEL_OPENED"):
            others[kind] += 1
            if len(samples) < 6:
                samples.append(payload.strip()[:240])
        continue
    data = msg["data"]
    for i in range(0, len(data) - 1, 2):
        name = data[i]
        f = fields.get(name)
        if not f:
            rows[(name, "(no layout)")] += 1
            continue
        values = data[i + 1]
        for j in range(0, len(values) - len(f) + 1, len(f)):
            r = dict(zip(f, values[j : j + len(f)]))
            sym = r.get("eventSymbol")
            rows[(name, sym)] += 1
            if name == "TimeAndSale":
                codes[sym][r.get("exchangeCode")] += 1
                if isinstance(r.get("size"), (int, float)):
                    shares[sym] += r["size"]
            elif name == "Profile":
                profiles[sym] = r
            elif name == "Order":
                orders[sym][(r.get("source"), r.get("scope"))] += 1

print("events the server accepted:", sorted(fields))
print("\nrows by event and symbol:")
for (name, sym), n in sorted(rows.items(), key=lambda kv: (str(kv[0][1]), kv[0][0])):
    if name == "Candle":
        continue
    print(f"  {str(sym):12} {name:12} {n}")
print("\nT&S exchange codes and shares:")
for sym, c in sorted(codes.items()):
    print(f"  {sym:12} {dict(c)}  {shares[sym]:,.0f} shares")
print("\nOrder rows:", {k: dict(v) for k, v in orders.items()} or "none")
print("\nProfile:")
for sym, r in sorted(profiles.items()):
    print(f"  {sym:12} status {r.get('tradingStatus')} reason {r.get('statusReason')} | SSR {r.get('shortSaleRestriction')} | halt {r.get('haltStartTime')}..{r.get('haltEndTime')} | limits {r.get('lowLimitPrice')}..{r.get('highLimitPrice')}")
print("\nother messages:", dict(others))
for s in samples:
    print("  ", s)

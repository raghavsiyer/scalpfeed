"""Do tastytrade sessions with different contracts deliver different prints? And how much of
the whole market's volume does any of them carry?

python -I contract_compare.py label=capture.jsonl [label=capture.jsonl ...]
"""
import json
import sys
from collections import Counter, defaultdict

runs = {}
for arg in sys.argv[1:]:
    label, path = arg.split("=", 1)
    fields = {}
    prints = defaultdict(dict)
    volumes = defaultdict(list)
    first = last = None
    contract = None
    aggregation = None
    for line in open(path, encoding="utf-8"):
        arrival, payload = line.split("\t", 1)
        msg = json.loads(payload)
        kind = msg["type"]
        if kind == "CHANNEL_OPENED" and msg.get("channel") == 1:
            contract = msg.get("parameters", {}).get("contract")
        if kind == "FEED_CONFIG":
            if msg.get("channel") == 1:
                aggregation = msg.get("aggregationPeriod", aggregation)
            if "eventFields" in msg:
                fields.update(msg["eventFields"])
        if kind != "FEED_DATA" or msg.get("channel") != 1:
            continue
        data = msg["data"]
        for i in range(0, len(data) - 1, 2):
            name = data[i]
            f = fields.get(name)
            if not f or name not in ("TimeAndSale", "Trade"):
                continue
            values = data[i + 1]
            for j in range(0, len(values) - len(f) + 1, len(f)):
                r = dict(zip(f, values[j : j + len(f)]))
                t = float(arrival)
                first = t if first is None else min(first, t)
                last = t if last is None else max(last, t)
                if name == "Trade":
                    if isinstance(r.get("dayVolume"), (int, float)):
                        volumes[r["eventSymbol"]].append((t, r["dayVolume"]))
                elif isinstance(r.get("price"), (int, float)) and isinstance(r.get("size"), (int, float)):
                    key = (r.get("time"), r.get("sequence"), r["price"], r["size"])
                    prints[r["eventSymbol"]][key] = r
    runs[label] = {"prints": prints, "volumes": volumes, "first": first, "last": last, "contract": contract, "aggregation": aggregation}

start = max(r["first"] for r in runs.values())
stop = min(r["last"] for r in runs.values())
print(f"common window: {(stop - start) / 1000:.0f} s")
for label, r in runs.items():
    print(f"  {label}: server says contract {r['contract']}, aggregation {r['aggregation']}")

symbols = sorted({s for r in runs.values() for s in r["prints"]})
for sym in symbols:
    in_window = {}
    for label, r in runs.items():
        in_window[label] = {k: v for k, v in r["prints"][sym].items() if k[0] and start <= k[0] <= stop - 2000}
    union = set().union(*[set(v) for v in in_window.values()])
    if not union:
        continue
    print(f"\n{sym}: {len(union)} distinct prints in the window")
    for label, prints in in_window.items():
        valid = [p for p in prints.values() if p.get("validTick")]
        shares = sum(p["size"] for p in prints.values())
        vshares = sum(p["size"] for p in valid)
        print(f"  {label:18} {len(prints):6} prints, missing {len(union - set(prints)):4} | {shares:12,.1f} shares, of which valid {vshares:12,.1f}")

    any_run = next(iter(runs.values()))
    all_prints = {k: v for prints in in_window.values() for k, v in prints.items()}
    print(f"  exchange codes: {Counter(p.get('exchangeCode') for p in all_prints.values()).most_common(6)}")
    print(f"  conditions: {Counter(p.get('exchangeSaleConditions') for p in all_prints.values()).most_common(8)}")
    v = [x for x in any_run["volumes"][sym] if start <= x[0] <= stop - 2000]
    tape = sum(p["size"] for p in all_prints.values())
    if len(v) >= 2 and v[-1][1] > v[0][1]:
        rise = v[-1][1] - v[0][1]
        print(f"  dayVolume rise over the same span {rise:,.0f} | whole tape {tape:,.1f} = {tape / rise:.0%}")

"""How far outside the bid and ask carried on the print do normal odd lots land?
Decides the guard's width. Real-time recordings only, since delayed ones aren't what will run.

python -I odd_lot_guard.py <capture.jsonl> [<capture.jsonl> ...]
"""
import json
import sys
from collections import defaultdict

WIDTHS = [0.005, 0.01, 0.02, 0.03, 0.05]


def pct(values, p):
    s = sorted(values)
    return s[min(len(s) - 1, int(p * len(s)))] if s else float("nan")


def normal_odd_lot(cond):
    return len(cond) == 4 and cond[3] == "o" and cond[1] in " F" and cond[2] in " T"


outside = defaultdict(list)
totals = defaultdict(int)
no_ref = defaultdict(int)
extremes = defaultdict(list)
valid_out = defaultdict(list)
valid_total = defaultdict(int)

for path in sys.argv[1:]:
    fields = {}
    for line in open(path, encoding="utf-8"):
        arrival, payload = line.split("\t", 1)
        if '"TimeAndSale"' not in payload and "FEED_CONFIG" not in payload:
            continue
        msg = json.loads(payload)
        if msg.get("channel") != 1:
            continue
        if msg["type"] == "FEED_CONFIG":
            fields.update(msg.get("eventFields", {}))
            continue
        if msg["type"] != "FEED_DATA":
            continue
        data = msg["data"]
        for i in range(0, len(data) - 1, 2):
            if data[i] != "TimeAndSale" or "TimeAndSale" not in fields:
                continue
            f = fields["TimeAndSale"]
            values = data[i + 1]
            for j in range(0, len(values) - len(f) + 1, len(f)):
                r = dict(zip(f, values[j : j + len(f)]))
                price = r.get("price")
                if not isinstance(price, (int, float)):
                    continue
                sym = r["eventSymbol"]
                cond = str(r.get("exchangeSaleConditions"))
                valid = bool(r.get("validTick"))
                if not valid and not normal_odd_lot(cond):
                    continue
                bid, ask = r.get("bidPrice"), r.get("askPrice")
                if not (isinstance(bid, (int, float)) and isinstance(ask, (int, float)) and bid > 0 and ask > 0):
                    if not valid:
                        no_ref[sym] += 1
                        totals[sym] += 1
                    continue
                beyond = max(bid - price, price - ask, 0.0) / ((bid + ask) / 2)
                if valid:
                    valid_total[sym] += 1
                    valid_out[sym].append(beyond)
                else:
                    totals[sym] += 1
                    outside[sym].append(beyond)
                    if beyond > 0.01:
                        extremes[sym].append((cond, price, bid, ask, r.get("size")))

print("Normal odd lots: share landing more than X beyond the bid/ask on the print, as a count")
print("symbol    total  no-quote | " + " ".join(f">{w:.1%}".rjust(7) for w in WIDTHS) + " | max beyond | valid prints beyond the same widths")
for sym, values in sorted(outside.items(), key=lambda kv: -totals[kv[0]]):
    cells = " ".join(f"{sum(1 for v in values if v > w):7}" for w in WIDTHS)
    vcells = " ".join(f"{sum(1 for v in valid_out[sym] if v > w):5}" for w in WIDTHS)
    print(f"{sym:8} {totals[sym]:6} {no_ref[sym]:8} | {cells} | {max(values):9.2%} | of {valid_total[sym]:6}: {vcells}")
    if extremes[sym]:
        print(f"    samples (cond, price, bid, ask, size): {extremes[sym][:4]}")

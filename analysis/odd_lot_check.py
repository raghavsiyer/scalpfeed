"""If normal odd lots went on the tape, what would change? Prices, lateness and chart bars.

python -I odd_lot_check.py <capture.jsonl> [<capture.jsonl> ...]
"""
import json
import sys
from collections import Counter, defaultdict


def pct(values, p):
    s = sorted(values)
    return s[min(len(s) - 1, int(p * len(s)))] if s else float("nan")


def normal_odd_lot(cond):
    return len(cond) == 4 and cond[3] == "o" and cond[1] in " F" and cond[2] in " T"


census = Counter()
per = defaultdict(lambda: {"valid": 0, "valid_s": 0.0, "added": 0, "added_s": 0.0, "hidden": 0, "delays": [], "dist": [], "late": [], "far": []})
bars_total = Counter()
bars_changed = Counter()
worst_bar = defaultdict(float)

for path in sys.argv[1:]:
    fields = {}
    bars = defaultdict(lambda: [None, None, None, None, 0.0, 0.0])
    last_valid = {}
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
                price, size = r.get("price"), r.get("size")
                if not isinstance(price, (int, float)) or not isinstance(size, (int, float)):
                    continue
                sym = r["eventSymbol"]
                cond = str(r.get("exchangeSaleConditions"))
                valid = bool(r.get("validTick"))
                t = r.get("time") or 0
                census[(cond, valid)] += 1
                p = per[sym]
                minute = (sym, int(t // 60000))
                b = bars[minute]
                if valid:
                    p["valid"] += 1
                    p["valid_s"] += size
                    last_valid[sym] = price
                    b[0] = price if b[0] is None else max(b[0], price)
                    b[1] = price if b[1] is None else min(b[1], price)
                    b[2] = price if b[2] is None else max(b[2], price)
                    b[3] = price if b[3] is None else min(b[3], price)
                    b[4] += size
                    b[5] += size
                elif normal_odd_lot(cond):
                    p["added"] += 1
                    p["added_s"] += size
                    delay = float(arrival) - t
                    p["delays"].append(delay)
                    if delay > 5000:
                        p["late"].append((cond, price, size, round(delay / 1000, 1)))
                    ref = last_valid.get(sym)
                    if ref:
                        d = abs(price - ref) / ref
                        p["dist"].append(d)
                        if d > 0.01:
                            p["far"].append((cond, price, ref, size))
                    b[2] = price if b[2] is None else max(b[2], price)
                    b[3] = price if b[3] is None else min(b[3], price)
                    b[5] += size
                else:
                    p["hidden"] += 1
    for (sym, _), b in bars.items():
        if b[0] is None:
            continue
        bars_total[sym] += 1
        if b[2] != b[0] or b[3] != b[1]:
            bars_changed[sym] += 1
            mid = (b[0] + b[1]) / 2
            worst_bar[sym] = max(worst_bar[sym], abs(b[2] - b[0]) / mid, abs(b[3] - b[1]) / mid)

print("All invalid print conditions (including overnight and delayed recordings):")
for (cond, valid), n in census.most_common(40):
    if not valid:
        print(f"  {cond!r:10} {n}")

print("\nsymbol: valid prints -> added | shares added vs valid | still hidden | delay p50, p99, max s | late over 5 s | distance from last valid p99, max | over 1% away | 1-min bars with changed high or low")
for sym, p in sorted(per.items(), key=lambda kv: -kv[1]["added"]):
    if p["added"] == 0:
        continue
    print(
        f"  {sym:6} {p['valid']:7} -> +{p['added']:6} | +{p['added_s'] / max(p['valid_s'], 1):4.0%} shares | hidden {p['hidden']:5} | "
        f"{pct(p['delays'], .5) / 1000:6.2f} {pct(p['delays'], .99) / 1000:7.2f} {max(p['delays']) / 1000:9.1f} | late {len(p['late']):4} | "
        f"{pct(p['dist'], .99):6.2%} {max(p['dist'] or [0]):6.2%} | far {len(p['far']):4} | "
        f"bars {bars_changed[sym]}/{bars_total[sym]}, worst {worst_bar[sym]:.2%}"
    )
    if p["late"]:
        print(f"         late samples: {p['late'][:3]}")
    if p["far"]:
        print(f"         far samples (cond, price, last valid, size): {p['far'][:3]}")

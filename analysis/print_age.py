# How old each print's time stamp would be when the plugin hands it to Quantower in merged mode, using the
# plugin's own stamping rules, replayed over two recordings by arrival. Also how evenly each feed's rows come.
#   python -I print_age.py <tape.tsv from ibkr_raw.py --capture> <capture.jsonl from ScalpFeedProbe> [old]
# With "old", uses the rule before 2026-11-06: IBKR's rows backdated 175 ms and held to their own second.
# Otherwise they're stamped on arrival. The plugin's stamps can only rise, in both cases.
import json
import sys
from collections import defaultdict

OLD_RULE = len(sys.argv) > 3 and sys.argv[3] == "old"
BACKDATE_MS = 175
CARRIED = {"NASDAQ", "BEX", "PSX"}


def pct(values, p):
    return values[min(int(len(values) * p), len(values) - 1)] if values else float("nan")


events = defaultdict(list)  # symbol -> arrival ms, source, print ms or seconds, venue, price, size
for line in open(sys.argv[1], encoding="utf-8"):
    p = line.rstrip("\n").split("\t")
    if len(p) == 8 and p[2] == "SMART":
        events[p[1]].append((float(p[0]), "ibkr", int(float(p[3])), p[6], float(p[4]), float(p[5])))

layouts = {}
for line in open(sys.argv[2], encoding="utf-8"):
    try:
        arrival, payload = line.split("\t", 1)
        message = json.loads(payload)
    except ValueError:
        continue
    kind = message.get("type")
    if kind == "FEED_CONFIG" and "eventFields" in message:
        for name, fields in message["eventFields"].items():
            layouts[(message["channel"], name)] = fields
    elif kind == "FEED_DATA" and message.get("channel") == 1:
        data = message["data"]
        for i in range(0, len(data) - 1, 2):
            fields = layouts.get((1, data[i]))
            if data[i] != "TimeAndSale" or not fields:
                continue
            for j in range(0, len(data[i + 1]), len(fields)):
                row = dict(zip(fields, data[i + 1][j : j + len(fields)]))
                if not row.get("eventFlags", 0) & 2:
                    events[row["eventSymbol"]].append((float(arrival), "tasty", row["time"], "", row["price"], float(row["size"])))

for symbol, rows in sorted(events.items()):
    rows.sort()
    if not any(r[1] == "ibkr" for r in rows) or not any(r[1] == "tasty" for r in rows):
        continue
    last = 0.0
    ages = {"tasty": [], "ibkr": [], "ibkr, stamped by the 175 ms rule": [], "ibkr, stamped just after tastytrade's last": [], "ibkr, held to its own second": []}
    previous_arrival = {"tasty": None, "ibkr": None, "shown": None}
    gaps = {"tasty": [], "ibkr": [], "shown": []}
    for arrival, source, stamp, venue, price, size in rows:
        if source == "ibkr" and venue in CARRIED:
            continue
        if source == "tasty":
            wanted = stamp
            rule = None
        elif OLD_RULE:
            backdated = arrival - BACKDATE_MS
            end_of_second = (stamp + 1) * 1000 - 0.0001
            wanted = min(backdated, end_of_second)
            rule = "ibkr, stamped by the 175 ms rule" if backdated <= end_of_second else "ibkr, held to its own second"
        else:
            wanted = arrival
            rule = None
        final = wanted if wanted > last else last + 0.0001
        if source == "ibkr" and wanted <= last:
            rule = "ibkr, stamped just after tastytrade's last"
        last = final
        age = arrival - final
        ages[source].append(age)
        if rule:
            ages[rule].append(age)
        for key in (source, "shown"):
            if previous_arrival[key] is not None and arrival - previous_arrival[key] > 0.5:
                gaps[key].append(arrival - previous_arrival[key])
            previous_arrival[key] = arrival
    print(f"\n{symbol}: {len(rows)} rows over {(rows[-1][0] - rows[0][0]) / 1000:.0f} s")
    for name, values in ages.items():
        if not values:
            continue
        values.sort()
        band = sum(1 for v in values if 100 <= v <= 200)
        print(f"  age on hand-over, {name}: n={len(values)}, median {pct(values, 0.5):.0f} ms, p10 {pct(values, 0.1):.0f}, p90 {pct(values, 0.9):.0f}, max {values[-1]:.0f}; in the 100-200 ms band {band} ({band / len(values):.0%})")
    for name, values in gaps.items():
        values.sort()
        print(f"  gaps between deliveries, {name}: median {pct(values, 0.5):.0f} ms, p90 {pct(values, 0.9):.0f}, p99 {pct(values, 0.99):.0f}, max {values[-1] if values else 0:.0f}")

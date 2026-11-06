# What the HISTORY channel's TimeAndSale rows in old recordings really were: how old each row was on
# arrival, and its flags. Channel 3 is the probe's HISTORY channel, subscribed with fromTime.
#   python -I history_channel_scan.py <capture.jsonl> [...]
import json
import os
import sys
from collections import Counter, defaultdict

for path in sys.argv[1:]:
    layouts = {}
    level = None
    rows = defaultdict(list)
    subscribed_from = None
    first_arrival = None
    with open(path, encoding="utf-8") as handle:
        for line in handle:
            try:
                arrival, payload = line.split("\t", 1)
                message = json.loads(payload)
            except ValueError:
                continue
            arrival = float(arrival)
            first_arrival = first_arrival or arrival
            kind = message.get("type")
            if kind == "FEED_CONFIG" and "eventFields" in message:
                for name, fields in message["eventFields"].items():
                    layouts[(message["channel"], name)] = fields
            elif kind == "FEED_DATA" and message.get("channel") == 3:
                data = message["data"]
                for i in range(0, len(data) - 1, 2):
                    if data[i] != "TimeAndSale":
                        continue
                    fields = layouts.get((3, "TimeAndSale"))
                    if not fields:
                        continue
                    for j in range(0, len(data[i + 1]), len(fields)):
                        row = dict(zip(fields, data[i + 1][j : j + len(fields)]))
                        rows[row["eventSymbol"]].append((arrival - row["time"], row.get("eventFlags", 0), arrival - first_arrival, row.get("exchangeCode")))

    total = sum(len(v) for v in rows.values())
    print(f"{os.path.basename(path)}: HISTORY-channel TimeAndSale rows {total}")
    for symbol, values in sorted(rows.items()):
        ages = sorted(v[0] / 1000 for v in values)
        flags = Counter(v[1] for v in values)
        arrivals = sorted(v[2] / 1000 for v in values)
        print(f"  {symbol}: {len(values)} rows, age on arrival min {ages[0]:.1f} s, median {ages[len(ages) // 2]:.1f} s, max {ages[-1]:.1f} s; flags {dict(flags)}; arrived {arrivals[0]:.1f}..{arrivals[-1]:.1f} s into the run; codes {dict(Counter(v[3] for v in values))}")

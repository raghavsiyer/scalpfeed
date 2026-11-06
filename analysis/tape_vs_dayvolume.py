"""The second paste's own test: does every TimeAndSale print add up to the dayVolume rise?
Counts every print, valid or not, per contract.

python -I tape_vs_dayvolume.py <capture.jsonl> [...]
"""
import json
import os
import sys
from collections import Counter, defaultdict

for path in sys.argv[1:]:
    fields = {}
    tape = defaultdict(list)
    volumes = defaultdict(list)
    contract = None
    kinds = Counter()
    for line in open(path, encoding="utf-8"):
        arrival, payload = line.split("\t", 1)
        msg = json.loads(payload)
        kind = msg["type"]
        if kind == "CHANNEL_OPENED" and msg.get("channel") == 1:
            contract = msg.get("parameters", {}).get("contract")
        if kind == "FEED_CONFIG" and "eventFields" in msg:
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
                if name == "Trade":
                    if isinstance(r.get("dayVolume"), (int, float)):
                        volumes[r["eventSymbol"]].append((t, r["dayVolume"]))
                elif isinstance(r.get("size"), (int, float)):
                    tape[r["eventSymbol"]].append((t, r["size"], r.get("exchangeCode"), r.get("type")))
                    kinds[r.get("type")] += 1

    print(f"\n{os.path.basename(path)}: contract {contract}, print types {dict(kinds)}")
    for sym in sorted(tape):
        v = volumes[sym]
        if len(v) < 2 or v[-1][1] <= v[0][1]:
            continue
        rows = [e for e in tape[sym] if v[0][0] <= e[0] <= v[-1][0]]
        rise = v[-1][1] - v[0][1]
        total = sum(e[1] for e in rows)
        print(f"  {sym:6} {(v[-1][0] - v[0][0]) / 1000:5.0f} s | all prints {total:12,.0f} | dayVolume rise {rise:12,.0f} | {total / rise:4.0%} | codes {sorted({str(e[2]) for e in rows})}")

"""What did the feed really send for Profile and TradeETH in the recordings?

python -I profile_check.py <capture.jsonl> [<capture.jsonl> ...]
"""
import json
import os
import sys
from collections import Counter

for path in sys.argv[1:]:
    fields = {}
    rows = {}
    counts = Counter()
    accepted = None
    for line in open(path, encoding="utf-8"):
        arrival, payload = line.split("\t", 1)
        msg = json.loads(payload)
        if msg["type"] == "FEED_CONFIG" and "eventFields" in msg:
            fields.update(msg["eventFields"])
            accepted = sorted(fields)
        if msg["type"] != "FEED_DATA":
            continue
        data = msg["data"]
        for i in range(0, len(data) - 1, 2):
            name = data[i]
            counts[name] += 1
            if name != "Profile" or name not in fields:
                continue
            f = fields[name]
            values = data[i + 1]
            for j in range(0, len(values) - len(f) + 1, len(f)):
                r = dict(zip(f, values[j : j + len(f)]))
                rows.setdefault(r["eventSymbol"], []).append(r)

    print(f"\n{os.path.basename(path)}")
    print(f"  events the server accepted: {accepted}")
    print(f"  frames with data: {dict(counts)}")
    for sym, rs in sorted(rows.items()):
        r = rs[-1]
        print(
            f"  {sym:6} rows {len(rs):3} | status {r.get('tradingStatus')} reason {r.get('statusReason')} | SSR {r.get('shortSaleRestriction')} | "
            f"halt {r.get('haltStartTime')}..{r.get('haltEndTime')} | limits {r.get('lowLimitPrice')}..{r.get('highLimitPrice')} | 52w {r.get('low52WeekPrice')}..{r.get('high52WeekPrice')}"
        )

# Every Profile row in ScalpFeedProbe recordings: trading status and halt range, with times in ET.
#   python -I profile_scan.py <capture.jsonl> [...]
import json
import os
import sys
from datetime import datetime, timedelta, timezone

ET = timezone(timedelta(hours=-5))  # Eastern standard time, which applies in November.


def when(ms):
    if not isinstance(ms, (int, float)) or ms <= 0:
        return str(ms)
    return datetime.fromtimestamp(ms / 1000, ET).strftime("%m-%d %H:%M:%S")


for path in sys.argv[1:]:
    layouts = {}
    print(os.path.basename(path))
    for line in open(path, encoding="utf-8"):
        try:
            arrival, payload = line.split("\t", 1)
            message = json.loads(payload)
        except ValueError:
            continue
        kind = message.get("type")
        if kind == "FEED_CONFIG" and "eventFields" in message:
            for name, fields in message["eventFields"].items():
                layouts[(message["channel"], name)] = fields
        elif kind == "FEED_DATA":
            data = message["data"]
            for i in range(0, len(data) - 1, 2):
                if data[i] != "Profile":
                    continue
                fields = layouts.get((message["channel"], "Profile"))
                if not fields:
                    continue
                for j in range(0, len(data[i + 1]), len(fields)):
                    r = dict(zip(fields, data[i + 1][j : j + len(fields)]))
                    print(f"  at {when(float(arrival))} {r['eventSymbol']}: status={r.get('tradingStatus')} reason={r.get('statusReason')} ssr={r.get('shortSaleRestriction')} halt {when(r.get('haltStartTime'))} to {when(r.get('haltEndTime'))} limits {r.get('lowLimitPrice')}/{r.get('highLimitPrice')}")

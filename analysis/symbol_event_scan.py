# What came back for each subscription in a ScalpFeedProbe recording: rows per event type and symbol,
# shares and exchange codes, the fields the server accepted, and anything it complained about.
#   python -I symbol_event_scan.py <capture.jsonl> [extra field names to show the last value of]
import json
import sys
from collections import Counter, defaultdict

layouts = {}
rows = Counter()
shares = Counter()
codes = defaultdict(Counter)
sources = defaultdict(Counter)
last = {}
accepted = {}
other = Counter()
extra = sys.argv[2:]
for line in open(sys.argv[1], encoding="utf-8"):
    try:
        _, payload = line.split("\t", 1)
        message = json.loads(payload)
    except ValueError:
        continue
    kind = message.get("type")
    if kind == "FEED_CONFIG" and "eventFields" in message:
        for name, fields in message["eventFields"].items():
            layouts[(message["channel"], name)] = fields
            accepted[(message["channel"], name)] = fields
    elif kind == "FEED_DATA":
        data = message["data"]
        for i in range(0, len(data) - 1, 2):
            fields = layouts.get((message["channel"], data[i]))
            if not fields:
                continue
            for j in range(0, len(data[i + 1]), len(fields)):
                row = dict(zip(fields, data[i + 1][j : j + len(fields)]))
                key = (message["channel"], data[i], row.get("eventSymbol"))
                rows[key] += 1
                if isinstance(row.get("size"), (int, float)):
                    shares[key] += row["size"]
                if row.get("exchangeCode") is not None:
                    codes[key][row["exchangeCode"]] += 1
                if row.get("source") is not None:
                    sources[key][row["source"]] += 1
                last[key] = row
    elif kind not in ("KEEPALIVE", "SETUP", "AUTH_STATE", "CHANNEL_OPENED", "FEED_CONFIG"):
        other[f"{kind}: {json.dumps(message)[:200]}"] += 1

for key in sorted(rows, key=lambda k: (k[0], k[1], str(k[2]))):
    channel, event, symbol = key
    text = f"ch{channel} {event:12} {symbol:12} rows={rows[key]}"
    if shares[key]:
        text += f" shares={shares[key]:.0f}"
    if codes[key]:
        text += f" codes={dict(codes[key].most_common(8))}"
    if sources[key]:
        text += f" sources={dict(sources[key].most_common(8))}"
    for name in extra:
        if name in last[key]:
            text += f" {name}={last[key][name]}"
    print(text)
for (channel, name), fields in sorted(accepted.items()):
    if name in ("Trade", "TradeETH"):
        print(f"accepted ch{channel} {name}: {fields}")
for text, count in other.most_common(10):
    print(f"other x{count}: {text}")

# Where tastytrade's prints hit against the quote they carry, set against the side it labels them with, and what
# a five-way tape coloring would give: above the ask, at the ask, between, at the bid, below the bid.
#   python -I print_sides.py <capture.jsonl from ScalpFeedProbe> [...]
import json
import os
import sys
from collections import Counter, defaultdict


def place(price, bid, ask):
    if not isinstance(bid, (int, float)) or not isinstance(ask, (int, float)) or bid != bid or ask != ask or bid <= 0 or ask <= 0:
        return "no quote"
    if price > ask:
        return "above ask"
    if price == ask:
        return "at ask"
    if price < bid:
        return "below bid"
    if price == bid:
        return "at bid"
    return "between"


ORDER = ["above ask", "at ask", "between", "at bid", "below bid", "no quote"]
for path in sys.argv[1:]:
    layouts = {}
    table = defaultdict(lambda: defaultdict(Counter))
    for line in open(path, encoding="utf-8"):
        try:
            _, payload = line.split("\t", 1)
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
                    r = dict(zip(fields, data[i + 1][j : j + len(fields)]))
                    if not r.get("eventFlags", 0) & 2:
                        table[r["eventSymbol"]][r.get("aggressorSide")][place(r["price"], r.get("bidPrice"), r.get("askPrice"))] += 1
    print(os.path.basename(path))
    for symbol, sides in sorted(table.items()):
        total = sum(sum(c.values()) for c in sides.values())
        print(f"  {symbol}: {total} prints. tastytrade's label, then where the print hit its own quote")
        for side, places in sorted(sides.items(), key=lambda kv: str(kv[0])):
            count = sum(places.values())
            text = ", ".join(f"{p} {places[p]} ({places[p] / count:.0%})" for p in ORDER if places[p])
            print(f"    {side}: {count} ({count / total:.0%}): {text}")

# What tastytrade's TimeAndSale carries in a capture: conditions, exchange codes, valid flags, what
# PrintFilter would do with each print, and how the prints compare with the dayVolume rise.
#   python -I overnight_tape.py <capture.jsonl>
import json
import sys
from collections import Counter, defaultdict


def normal_odd_lot(conditions):
    return (
        len(conditions) == 4
        and conditions[0] == "@"
        and conditions[1] in " F"
        and conditions[2] in " T"
        and conditions[3] == "o"
    )


layouts = {}
stats = defaultdict(
    lambda: {
        "n": 0,
        "shares": 0.0,
        "shown": 0,
        "shown_shares": 0.0,
        "codes": Counter(),
        "kinds": Counter(),
        "sides": Counter(),
        "first_vol": None,
        "last_vol": None,
        "sample": [],
    }
)

for line in open(sys.argv[1], encoding="utf-8"):
    payload = line.split("\t", 1)[1]
    message = json.loads(payload)
    kind = message.get("type")
    if kind == "FEED_CONFIG" and "eventFields" in message:
        for name, fields in message["eventFields"].items():
            layouts[(message["channel"], name)] = fields
    elif kind == "FEED_DATA":
        data = message["data"]
        for i in range(0, len(data) - 1, 2):
            name, values = data[i], data[i + 1]
            fields = layouts.get((message["channel"], name))
            if not fields:
                continue
            for j in range(0, len(values), len(fields)):
                row = dict(zip(fields, values[j : j + len(fields)]))
                symbol = row.get("eventSymbol")
                s = stats[symbol]
                if name == "TimeAndSale":
                    if row.get("eventFlags", 0) & 2:
                        continue
                    conditions = row.get("exchangeSaleConditions") or ""
                    size = float(row["size"]) if row["size"] != "NaN" else 0.0
                    valid = bool(row.get("validTick"))
                    shows = valid or normal_odd_lot(conditions)
                    s["n"] += 1
                    s["shares"] += size
                    s["codes"][row.get("exchangeCode")] += 1
                    s["kinds"][(conditions, valid, row.get("type"), row.get("extendedTradingHours"))] += 1
                    s["sides"][row.get("aggressorSide")] += 1
                    if shows:
                        s["shown"] += 1
                        s["shown_shares"] += size
                    if len(s["sample"]) < 3:
                        s["sample"].append((row.get("time"), row.get("price"), size, row.get("bidPrice"), row.get("askPrice"), conditions))
                elif name == "Trade":
                    volume = row.get("dayVolume")
                    if isinstance(volume, (int, float)):
                        if s["first_vol"] is None:
                            s["first_vol"] = volume
                        s["last_vol"] = volume

for symbol, s in sorted(stats.items()):
    rise = (s["last_vol"] - s["first_vol"]) if s["first_vol"] is not None else None
    print(f"{symbol}: prints {s['n']}, shares {s['shares']:.0f}; PrintFilter shows {s['shown']} ({s['shown_shares']:.0f} shares); dayVolume rise {rise}")
    print(f"  exchange codes {dict(s['codes'])}, sides {dict(s['sides'])}")
    for key, count in s["kinds"].most_common(8):
        print(f"  x{count} conditions={key[0]!r} valid={key[1]} type={key[2]} eth={key[3]}")
    print(f"  samples (time, price, size, bid, ask, conditions): {s['sample']}")

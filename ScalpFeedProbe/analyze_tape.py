"""What the TimeAndSale tape actually contains: which prints count, whether it's the whole
market, and where the feed stalls.

python -I analyze_tape.py <capture.jsonl> [<capture.jsonl> ...]
"""
import json
import sys
from collections import Counter, defaultdict
from datetime import datetime, timedelta, timezone

ET = timezone(timedelta(hours=-5))  # November data, EST.
LIVE = 1


def et(ms):
    return datetime.fromtimestamp(ms / 1000, ET).strftime("%H:%M:%S.%f")[:-3]


def load(path):
    fields = defaultdict(dict)
    rows = defaultdict(list)
    all_arrivals = []
    for line in open(path, encoding="utf-8"):
        arrival, payload = line.split("\t", 1)
        arrival = float(arrival)
        msg = json.loads(payload)
        if msg["type"] == "FEED_CONFIG":
            for name, f in msg.get("eventFields", {}).items():
                fields[msg["channel"]][name] = f
        elif msg["type"] == "FEED_DATA":
            if msg["channel"] == LIVE:
                all_arrivals.append(arrival)
            data = msg["data"]
            for i in range(0, len(data) - 1, 2):
                name, values = data[i], data[i + 1]
                f = fields[msg["channel"]][name]
                for j in range(0, len(values) - len(f) + 1, len(f)):
                    row = dict(zip(f, values[j : j + len(f)]))
                    row["_arrival"] = arrival
                    rows[(msg["channel"], name)].append(row)
    return rows, all_arrivals


def num(x):
    return x if isinstance(x, (int, float)) else float("nan")


for path in sys.argv[1:]:
    print(f"\n################ {path.split(chr(92))[-1]}")
    rows, arrivals = load(path)
    prints = rows[(LIVE, "TimeAndSale")]
    candles = rows[(LIVE, "Candle")]
    trades = rows[(LIVE, "Trade")]

    print("\n=== Sale condition vs validTick, all symbols ===")
    table = defaultdict(lambda: [0, 0, 0.0, 0, 0])
    for r in prints:
        t = table[str(r["exchangeSaleConditions"])]
        t[0] += 1
        t[1] += 1 if r["validTick"] else 0
        t[2] += num(r["size"])
        bid, ask, px = num(r["bidPrice"]), num(r["askPrice"]), num(r["price"])
        if bid == bid and ask == ask and (px < bid - 1e-9 or px > ask + 1e-9):
            t[3] += 1
        cents = px * 100
        if abs(cents - round(cents)) > 1e-6:
            t[4] += 1
    print(f"  {'cond':8} {'prints':>8} {'valid':>8} {'avg size':>9} {'outside bid/ask':>16} {'sub-penny':>10}")
    for cond, t in sorted(table.items(), key=lambda kv: -kv[1][0]):
        print(f"  {cond!r:8} {t[0]:8} {t[1]:8} {t[2] / t[0]:9.1f} {t[3]:16} {t[4]:10}")

    print("\n=== Where valid prints fall against the feed's own quote, by symbol ===")
    for sym in sorted({r["eventSymbol"] for r in prints}):
        at_bid = at_ask = inside = outside = subpenny = 0
        for r in prints:
            if r["eventSymbol"] != sym or not r["validTick"]:
                continue
            bid, ask, px = num(r["bidPrice"]), num(r["askPrice"]), num(r["price"])
            if bid != bid or ask != ask:
                continue
            if px < bid - 1e-9 or px > ask + 1e-9:
                outside += 1
            elif abs(px - bid) < 1e-9:
                at_bid += 1
            elif abs(px - ask) < 1e-9:
                at_ask += 1
            else:
                inside += 1
            if abs(px * 100 - round(px * 100)) > 1e-6:
                subpenny += 1
        n = at_bid + at_ask + inside + outside
        if n:
            print(f"  {sym:5} n={n:6}  at bid {at_bid / n:6.1%}  at ask {at_ask / n:6.1%}  between {inside / n:6.1%}  outside {outside / n:6.1%}  sub-penny {subpenny / n:6.1%}")

    print("\n=== Is the tape the whole market? Prints vs Candle volume, same whole minutes ===")
    for sym in sorted({r["eventSymbol"] for r in prints}):
        ps = [r for r in prints if r["eventSymbol"] == sym]
        first, last = min(r["time"] for r in ps), max(r["time"] for r in ps)
        start = (first // 60000 + 1) * 60000
        stop = last // 60000 * 60000
        bars = {}
        for c in candles:
            if c["eventSymbol"].startswith(sym + "{") and start <= c["time"] < stop and not int(c["eventFlags"]) & 2:
                bars[c["time"]] = c  # Later rows supersede earlier ones.
        candle_vol = sum(num(b["volume"]) for b in bars.values())
        window = [r for r in ps if start <= r["time"] < stop]
        all_vol = sum(num(r["size"]) for r in window)
        valid_vol = sum(num(r["size"]) for r in window if r["validTick"])
        tr = sorted((t for t in trades if t["eventSymbol"] == sym and num(t["dayVolume"]) == num(t["dayVolume"])), key=lambda t: t["_arrival"])
        day_delta = num(tr[-1]["dayVolume"]) - num(tr[0]["dayVolume"]) if len(tr) > 1 else float("nan")
        tape_all = sum(num(r["size"]) for r in ps)
        ratio = all_vol / candle_vol if candle_vol else float("nan")
        print(f"  {sym:5} {len(bars):2} min: candle vol {candle_vol:11.0f}  all prints {all_vol:11.0f} ({ratio:6.1%})  valid only {valid_vol:11.0f} ({valid_vol / candle_vol if candle_vol else float('nan'):6.1%})"
              f"  | whole run: Trade dayVolume rose {day_delta:11.0f}, tape {tape_all:11.0f}")

    print("\n=== Delay above the 900 s offset, all symbols ===")
    delays = sorted(r["_arrival"] - r["time"] - 900000 for r in prints)
    floor = delays[0]
    at = lambda p: delays[min(len(delays) - 1, int(p * len(delays)))]
    print(f"  floor {floor:.0f} ms, p50 {at(.5):.0f}, p90 {at(.9):.0f}, p99 {at(.99):.0f}, p99.9 {at(.999):.0f}, max {delays[-1]:.0f}")
    for over in (20, 50, 100, 250, 500, 1000):
        share = sum(1 for d in delays if d - floor > over) / len(delays)
        print(f"  more than {over:4} ms above floor: {share:7.2%}")

    print("\n=== Worst silences on the whole socket ===")
    gaps = sorted(((b - a, a) for a, b in zip(arrivals, arrivals[1:])), reverse=True)[:6]
    for gap, start in gaps:
        print(f"  {gap:7.0f} ms from {et(start)} ET")

    print("\n=== Print delay spikes, worst seconds ===")
    per_second = defaultdict(float)
    for r in prints:
        sec = int(r["_arrival"] // 1000)
        per_second[sec] = max(per_second[sec], r["_arrival"] - r["time"] - 900000)
    worst = sorted(per_second.items(), key=lambda kv: -kv[1])[:6]
    for sec, delay in sorted(worst):
        print(f"  arrived {et(sec * 1000)} ET: {delay:.0f} ms")

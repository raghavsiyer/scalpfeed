"""Summarize a ScalpFeedProbe capture.

python -I analyze_capture.py <capture.jsonl> <tastytrade.json>
"""
import json
import sys
from collections import Counter, defaultdict
from datetime import datetime, timedelta, timezone
from zoneinfo import ZoneInfo

try:
    ET = ZoneInfo("America/New_York")
except Exception:
    # Windows Python without tzdata. Correct for EST only (November to March).
    ET = timezone(timedelta(hours=-5))

LIVE, HISTORY = 1, 3
TOP = 12


def et(ms):
    return datetime.fromtimestamp(ms / 1000, ET).strftime("%m-%d %H:%M:%S.%f")[:-3]


def session(ms):
    d = datetime.fromtimestamp(ms / 1000, ET)
    m = d.hour * 60 + d.minute
    if m < 240 or m >= 1200:
        return "overnight"
    return "pre" if m < 570 else "regular" if m < 960 else "post"


def pct(values, unit="ms", digits=1):
    if not values:
        return "none"
    s = sorted(values)
    at = lambda p: s[min(len(s) - 1, int(p * len(s)))]
    return f"n={len(s)} min={s[0]:.{digits}f} p50={at(.5):.{digits}f} p90={at(.9):.{digits}f} p99={at(.99):.{digits}f} max={s[-1]:.{digits}f} {unit}"


def top(counter):
    return ", ".join(f"{k}:{v}" for k, v in counter.most_common(TOP)) or "none"


def arrival_pattern(times):
    gaps = [b - a for a, b in zip(times, times[1:])]
    edges = [0, 1, 5, 10, 25, 50, 100, 250, 500, 1000, float("inf")]
    hist = [sum(1 for g in gaps if lo <= g < hi) for lo, hi in zip(edges, edges[1:])]
    labels = [f"{lo}-{hi}" for lo, hi in zip(edges[:-1], edges[1:])]
    mod100 = Counter(int(t % 100 // 10) * 10 for t in times)
    print(f"    gaps: {pct(gaps)}")
    print("    gap histogram ms: " + ", ".join(f"{l}:{n}" for l, n in zip(labels, hist) if n))
    print(f"    arrival ms mod 100 (flat means no 100 ms timer): {sorted(mod100.items())}")


path, config_path = sys.argv[1], sys.argv[2]
secrets = json.load(open(config_path, encoding="utf-8-sig"))
text = open(path, encoding="utf-8").read()
print("secrets in capture:", [k for k in ("client_secret", "refresh_token") if secrets[k] in text] or "none")

fields = defaultdict(dict)
rows = defaultdict(list)
frames = defaultdict(lambda: defaultdict(list))
errors = []

for line in text.splitlines():
    arrival, payload = line.split("\t", 1)
    arrival = float(arrival)
    msg = json.loads(payload)
    kind = msg["type"]
    if kind == "FEED_CONFIG":
        for name, f in msg.get("eventFields", {}).items():
            fields[msg["channel"]][name] = f
    elif kind == "ERROR":
        errors.append(payload[:300])
    elif kind == "FEED_DATA":
        data = msg["data"]
        for i in range(0, len(data) - 1, 2):
            name, values = data[i], data[i + 1]
            f = fields[msg["channel"]][name]
            seen = set()
            for j in range(0, len(values) - len(f) + 1, len(f)):
                row = dict(zip(f, values[j : j + len(f)]))
                row["_arrival"] = arrival
                rows[(msg["channel"], name)].append(row)
                seen.add(row["eventSymbol"])
            for symbol in seen:
                frames[(msg["channel"], name)][symbol].append(arrival)

if errors:
    print("server errors:", *errors, sep="\n  ")

all_rows = [r for rs in rows.values() for r in rs]
print(f"data frames span {et(min(r['_arrival'] for r in all_rows))} to {et(max(r['_arrival'] for r in all_rows))} ET")

print("\n=== TimeAndSale, live ===")
tns = rows[(LIVE, "TimeAndSale")]
for sym in sorted({r["eventSymbol"] for r in tns}):
    rs = [r for r in tns if r["eventSymbol"] == sym]
    span_min = max((rs[-1]["_arrival"] - rs[0]["_arrival"]) / 60000, 1 / 60)
    print(f"\n  {sym}: {len(rs)} prints, {len(rs) / span_min:.1f}/min, event times {et(rs[0]['time'])} to {et(rs[-1]['time'])}")
    print(f"    sessions: {top(Counter(session(r['time']) for r in rs))}")
    print(f"    exchange codes: {top(Counter(r['exchangeCode'] for r in rs))}")
    print(f"    aggressorSide: {top(Counter(r['aggressorSide'] for r in rs))}  validTick: {top(Counter(str(r['validTick']) for r in rs))}  type: {top(Counter(r['type'] for r in rs))}")
    print(f"    sale conditions: {top(Counter(str(r['exchangeSaleConditions']) for r in rs))}")
    print(f"    delay vs exchange time: {pct([(r['_arrival'] - r['time']) / 1000 for r in rs], 's', 3)}")
    print(f"    prints per frame: {len(rs) / max(len(frames[(LIVE, 'TimeAndSale')][sym]), 1):.2f}")
    arrival_pattern(frames[(LIVE, "TimeAndSale")][sym])

print("\n=== Trade vs TimeAndSale: does Trade follow the prints? ===")
trades = rows[(LIVE, "Trade")]
for sym in sorted({r["eventSymbol"] for r in trades} | {r["eventSymbol"] for r in tns}):
    tr = [r for r in trades if r["eventSymbol"] == sym]
    pr = [r for r in tns if r["eventSymbol"] == sym]
    if not tr:
        print(f"  {sym}: no Trade rows")
        continue
    moved = sum(1 for a, b in zip(tr, tr[1:]) if a["price"] != b["price"] or a["time"] != b["time"])
    line = f"  {sym}: {len(tr)} Trade rows, {moved} changed price or time, Trade time {et(tr[0]['time'])} to {et(tr[-1]['time'])}, last px {tr[-1]['price']}"
    if pr:
        line += f"; {len(pr)} prints, last print {et(pr[-1]['time'])} px {pr[-1]['price']}"
        line += f"; Trade behind last print by {(pr[-1]['time'] - tr[-1]['time']) / 1000:.1f}s"
    print(line)
print(f"  TradeETH rows: {len(rows[(LIVE, 'TradeETH')])}, offered in FEED_CONFIG: {'TradeETH' in fields[LIVE]}")

print("\n=== Quote ===")
quotes = rows[(LIVE, "Quote")]
for sym in sorted({r["eventSymbol"] for r in quotes}):
    rs = [r for r in quotes if r["eventSymbol"] == sym]
    print(f"\n  {sym}: {len(rs)} quotes, with timestamps: {sum(1 for r in rs if r['bidTime'] or r['askTime'])}, bid venues {top(Counter(r['bidExchangeCode'] for r in rs))}, ask venues {top(Counter(r['askExchangeCode'] for r in rs))}")
    spreads = [r["askPrice"] - r["bidPrice"] for r in rs if isinstance(r["bidPrice"], (int, float)) and isinstance(r["askPrice"], (int, float)) and r["askPrice"] > 0]
    if spreads:
        print(f"    spread: {pct(spreads, '$', 4)}, last bid/ask {rs[-1]['bidPrice']}/{rs[-1]['askPrice']}")
    arrival_pattern(frames[(LIVE, "Quote")][sym])

print("\n=== TimeAndSale, HISTORY request ===")
hist = rows[(HISTORY, "TimeAndSale")]
for sym in sorted({r["eventSymbol"] for r in hist}):
    rs = [r for r in hist if r["eventSymbol"] == sym]
    print(f"  {sym}: {len(rs)} prints, oldest {et(min(r['time'] for r in rs))}, newest {et(max(r['time'] for r in rs))}, flags {top(Counter(r['eventFlags'] for r in rs))}")

print("\n=== Candle ===")
candles = rows[(LIVE, "Candle")]
for sym in sorted({r["eventSymbol"] for r in candles}):
    rs = [r for r in candles if r["eventSymbol"] == sym]
    end = [r for r in rs if int(r["eventFlags"]) & 8]
    bars = {r["time"]: r for r in rs if not int(r["eventFlags"]) & 2}
    load = f"{end[0]['_arrival'] - rs[0]['_arrival']:.0f} ms" if end else "no snapshot end"
    print(f"  {sym}: {len(rs)} rows, {len(bars)} bars, snapshot load {load}, flags {top(Counter(r['eventFlags'] for r in rs))}")
    by_day = defaultdict(Counter)
    for t in bars:
        by_day[datetime.fromtimestamp(t / 1000, ET).strftime("%a %m-%d")][session(t)] += 1
    for day, c in by_day.items():
        print(f"    {day}: {dict(c)}")

print("\n=== Profile and Summary, last seen ===")
for name in ("Profile", "Summary"):
    latest = {r["eventSymbol"]: r for r in rows[(LIVE, name)]}
    for sym, r in sorted(latest.items()):
        print(f"  {name} {sym}: " + ", ".join(f"{k}={v}" for k, v in r.items() if k not in ("eventType", "eventSymbol", "eventTime", "_arrival")))

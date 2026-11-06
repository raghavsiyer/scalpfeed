# IBKR's historical bars against IBKR's own complete tape and tastytrade's candles, per bar.
#   python -I history_vs_tape.py <bars.tsv from ibkr_history.py> <candle capture.jsonl> <tape.tsv from ibkr_raw.py> [more tape.tsv]
import json
import sys
from collections import defaultdict
from datetime import datetime, timedelta, timezone

ET = timezone(timedelta(hours=-5))  # Eastern standard time, which applies in November.
SECONDS = {"1 min": 60, "10 secs": 10, "5 secs": 5, "30 secs": 30}


def pct(values, p):
    return values[min(int(len(values) * p), len(values) - 1)] if values else float("nan")


history = defaultdict(dict)  # (symbol, bar size) -> time -> row
for line in open(sys.argv[1], encoding="utf-8"):
    p = line.rstrip("\n").split("\t")
    if len(p) == 10:
        history[(p[0], p[1])][p[2]] = {"open": float(p[3]), "high": float(p[4]), "low": float(p[5]), "close": float(p[6]), "volume": float(p[7]), "count": int(p[9])}

prints = defaultdict(list)  # symbol -> seconds, price, size, conditions
covered = defaultdict(lambda: [float("inf"), 0])
for path in sys.argv[3:]:
    for line in open(path, encoding="utf-8"):
        p = line.rstrip("\n").split("\t")
        if len(p) == 8 and p[2] == "SMART":
            s = int(float(p[3]))
            prints[p[1]].append((s, float(p[4]), float(p[5]), p[7]))

candles = defaultdict(dict)
day_volume = {}
prev_day_volume = {}
layouts = {}
for line in open(sys.argv[2], encoding="utf-8"):
    try:
        _, payload = line.split("\t", 1)
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
            fields = layouts.get((message["channel"], data[i]))
            if not fields:
                continue
            for j in range(0, len(data[i + 1]), len(fields)):
                row = dict(zip(fields, data[i + 1][j : j + len(fields)]))
                if data[i] == "Candle" and row["eventSymbol"].endswith("{=m}") and not row.get("eventFlags", 0) & 2 and isinstance(row.get("volume"), (int, float)):
                    candles[row["eventSymbol"].split("{")[0]][int(row["time"] // 1000)] = row
                elif data[i] == "Trade" and isinstance(row.get("dayVolume"), (int, float)):
                    day_volume[row["eventSymbol"]] = row["dayVolume"]
                elif data[i] == "Summary" and isinstance(row.get("prevDayVolume"), (int, float)):
                    prev_day_volume[row["eventSymbol"]] = row["prevDayVolume"]

for (symbol, bar_size), rows in sorted(history.items()):
    if bar_size not in SECONDS:
        continue
    step = SECONDS[bar_size]
    tape = defaultdict(float)
    tape_round = defaultdict(float)
    high = {}
    low = {}
    seen = set()
    for s, price, size, conditions in prints[symbol]:
        key = s - s % step
        seen.add(s // 60)
        tape[key] += size
        if "I" not in conditions:
            tape_round[key] += size
        high[key] = max(high.get(key, price), price)
        low[key] = min(low.get(key, price), price)
    # Whole bars only: drop the first and last minute of each recording.
    minutes = sorted(seen)
    inner = {m for m in minutes if m - 1 in seen and m + 1 in seen}
    common = sorted(int(t) for t in rows if int(t) // 60 in inner and (tape.get(int(t), 0) > 0 or rows[t]["volume"] > 0))
    if not common:
        print(f"{symbol} {bar_size}: no bars overlap the tape")
        continue
    hv = tv = rv = cv = 0.0
    ratios = []
    past = 0
    wider = 0
    if step == 60:
        print(f"\n{symbol} {bar_size}: minute ET, IBKR history, IBKR tape, tape without odd lots, tastytrade candle, trades in bar")
    for t in common:
        bar = rows[str(t)]
        v, a, r = bar["volume"], tape.get(t, 0), tape_round.get(t, 0)
        c = candles[symbol].get(t, {}).get("volume", float("nan"))
        hv += v
        tv += a
        rv += r
        if c == c:
            cv += c
        if a:
            ratios.append(v / a)
        if t in high and (high[t] > bar["high"] + 1e-9 or low[t] < bar["low"] - 1e-9):
            past += 1
        if t in high and (bar["high"] > high[t] + 1e-9 or bar["low"] < low[t] - 1e-9):
            wider += 1
        if step == 60:
            print(f"  {datetime.fromtimestamp(t, ET):%H:%M}  {v:>11.0f} {a:>11.0f} {r:>11.0f} {c:>11.0f}  {bar['count']:>5}   hist/tape {v / a if a else float('nan'):6.1%}")
    ratios.sort()
    print(f"{symbol} {bar_size}: {len(common)} bars; history {hv:.0f}, tape {tv:.0f} ({hv / tv:.1%}), tape without odd lots {rv:.0f} (history is {hv / rv if rv else float('nan'):.1%} of it), tastytrade candles {cv:.0f} ({cv / tv:.1%} of the tape)")
    print(f"  per-bar history/tape: p10 {pct(ratios, 0.1):.1%}, median {pct(ratios, 0.5):.1%}, p90 {pct(ratios, 0.9):.1%}; tape printed past the bar's range in {past} bars, bar's range past the tape in {wider}")

print()
for (symbol, bar_size), rows in sorted(history.items()):
    if bar_size == "1 day":
        text = ", ".join(f"{t}: {r['volume']:.0f}" for t, r in sorted(rows.items())[-3:])
        print(f"{symbol} daily bars, last 3: {text}; tastytrade prevDayVolume {prev_day_volume.get(symbol)}, dayVolume {day_volume.get(symbol)}")
    if bar_size == "1 min":
        session = [r for t, r in rows.items() if datetime.fromtimestamp(int(t), ET).date() == datetime.fromtimestamp(max(int(x) for x in rows), ET).date() and datetime.fromtimestamp(int(t), ET).hour >= 4]
        print(f"{symbol} 1-minute bars since 04:00 ET today: {sum(r['volume'] for r in session):.0f} shares in {len(session)} bars")

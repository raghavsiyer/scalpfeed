# Does any candle period or attribute carry more volume than the 1-minute candles? Totals today's volume
# per candle symbol (from 04:00 ET) and sets it against dayVolume, which counts the whole market.
#   python -I candle_periods.py <capture.jsonl from ScalpFeedProbe with --candle-codes>
import json
import sys
from collections import defaultdict
from datetime import datetime, timedelta, timezone

ET = timezone(timedelta(hours=-5))  # Eastern standard time, which applies in November.
layouts = {}
bars = defaultdict(dict)
day_volume = {}
prev_day_volume = {}
arrivals = []
for line in open(sys.argv[1], encoding="utf-8"):
    try:
        arrival, payload = line.split("\t", 1)
        message = json.loads(payload)
    except ValueError:
        continue
    arrivals.append(float(arrival))
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
                if data[i] == "Candle" and not row.get("eventFlags", 0) & 2 and isinstance(row.get("volume"), (int, float)):
                    bars[row["eventSymbol"]][row["time"]] = row
                elif data[i] == "Trade" and isinstance(row.get("dayVolume"), (int, float)):
                    day_volume[row["eventSymbol"]] = row["dayVolume"]
                elif data[i] == "Summary" and isinstance(row.get("prevDayVolume"), (int, float)):
                    prev_day_volume[row["eventSymbol"]] = row["prevDayVolume"]

now = datetime.fromtimestamp(arrivals[-1] / 1000, ET)
today = now.replace(hour=4, minute=0, second=0, microsecond=0).timestamp() * 1000
for symbol, rows in sorted(bars.items()):
    base = symbol.split("{")[0]
    recent = [r for t, r in rows.items() if t >= today]
    # Bars opened before 04:00 ET, such as a daily bar, are shown by their own volume.
    latest = rows[max(rows)]
    whole = day_volume.get(base)
    total = sum(r["volume"] for r in recent)
    print(f"{symbol:24} bars {len(rows):>5}, since 04:00 ET {len(recent):>4} bars, {total:>14.0f} shares; latest bar {datetime.fromtimestamp(latest['time'] / 1000, ET):%m-%d %H:%M} volume {latest['volume']:.0f}; dayVolume {whole}")

# Yesterday: the daily bar and Summary's whole-market figure against the sum of that day's 1-minute bars.
# Today: dayVolume against the 1-minute bars since the same point in the session.
for base in sorted({s.split("{")[0] for s in bars}):
    daily = bars.get(f"{base}{{=d}}")
    minutes = bars.get(f"{base}{{=m}}")
    if not daily or not minutes:
        continue
    start = max(daily)
    end = start + 24 * 3600 * 1000
    day_bar = daily[start]["volume"]
    day_minutes = sum(r["volume"] for t, r in minutes.items() if start <= t < end)
    today_minutes = sum(r["volume"] for t, r in minutes.items() if t >= end)
    prev = prev_day_volume.get(base)
    print(f"{base}: daily bar opening {datetime.fromtimestamp(start / 1000, ET):%m-%d %H:%M} ET volume {day_bar:.0f}; prevDayVolume {prev}; that day's 1-minute bars sum to {day_minutes:.0f} ({day_minutes / day_bar if day_bar else float('nan'):.1%} of the daily bar)")
    if day_volume.get(base):
        print(f"  today so far: 1-minute bars sum to {today_minutes:.0f}, dayVolume {day_volume[base]:.0f} ({today_minutes / day_volume[base]:.1%})")

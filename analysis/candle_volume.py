# tastytrade's 1-minute Candle volume against the same minutes on IBKR's tape and tastytrade's own prints.
#   python -I candle_volume.py <ibkr.tsv from ibkr_raw.py --capture> <capture with Candle snapshot> <capture with TimeAndSale>
import json
import sys
from collections import defaultdict
from datetime import datetime, timedelta, timezone

ET = timezone(timedelta(hours=-5))  # Eastern standard time, which applies in November.


def rows_of(path):
    layouts = {}
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
                fields = layouts.get((message["channel"], data[i]))
                if not fields:
                    continue
                for j in range(0, len(data[i + 1]), len(fields)):
                    yield data[i], dict(zip(fields, data[i + 1][j : j + len(fields)]))


ibkr = defaultdict(lambda: defaultdict(float))
ibkr_high = defaultdict(dict)
ibkr_low = defaultdict(dict)
for line in open(sys.argv[1], encoding="utf-8"):
    parts = line.rstrip("\n").split("\t")
    if len(parts) != 8:
        continue
    symbol, minute, price, size = parts[1], int(float(parts[3]) // 60), float(parts[4]), float(parts[5])
    ibkr[symbol][minute] += size
    ibkr_high[symbol][minute] = max(ibkr_high[symbol].get(minute, price), price)
    ibkr_low[symbol][minute] = min(ibkr_low[symbol].get(minute, price), price)

candles = defaultdict(dict)
for event, row in rows_of(sys.argv[2]):
    if event == "Candle" and row["eventSymbol"].endswith("{=m}") and not row.get("eventFlags", 0) & 2 and isinstance(row.get("volume"), (int, float)):
        candles[row["eventSymbol"].split("{")[0]][int(row["time"] // 60000)] = row

prints_all = defaultdict(lambda: defaultdict(float))
prints_valid = defaultdict(lambda: defaultdict(float))
for event, row in rows_of(sys.argv[3]):
    if event == "TimeAndSale" and not row.get("eventFlags", 0) & 2:
        minute = int(row["time"] // 60000)
        prints_all[row["eventSymbol"]][minute] += row["size"]
        if row["validTick"]:
            prints_valid[row["eventSymbol"]][minute] += row["size"]

for symbol in sorted(ibkr):
    minutes = sorted(ibkr[symbol])[1:-1]  # The first and last minutes are only part-recorded.
    print(f"\n{symbol}: minute ET, Candle volume, IBKR tape, tastytrade valid prints, Candle/IBKR, Candle/valid, Candle high/low vs IBKR's")
    total = [0.0, 0.0, 0.0]
    wider = 0
    for minute in minutes:
        bar = candles[symbol].get(minute)
        if bar is None:
            print(f"  {datetime.fromtimestamp(minute * 60, ET):%H:%M}  no candle, IBKR {ibkr[symbol][minute]:.0f}")
            continue
        volume, tape, valid = bar["volume"], ibkr[symbol][minute], prints_valid[symbol][minute]
        total[0] += volume
        total[1] += tape
        total[2] += valid
        if ibkr_high[symbol][minute] > bar["high"] or ibkr_low[symbol][minute] < bar["low"]:
            wider += 1
        print(f"  {datetime.fromtimestamp(minute * 60, ET):%H:%M}  {volume:>11.0f} {tape:>11.0f} {valid:>11.0f}  {volume / tape if tape else float('nan'):6.1%} {volume / valid if valid else float('nan'):6.1%}  {bar['high']}/{bar['low']} vs {ibkr_high[symbol][minute]}/{ibkr_low[symbol][minute]}")
    if total[1]:
        print(f"  total: Candle {total[0]:.0f}, IBKR {total[1]:.0f}, valid prints {total[2]:.0f}; Candle/IBKR {total[0] / total[1]:.1%}, Candle/valid {total[0] / total[2] if total[2] else float('nan'):.1%}; IBKR's range went past the candle's in {wider} of {len(minutes)} minutes")

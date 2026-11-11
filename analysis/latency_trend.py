# Latency trend from the plugin's once-a-minute stats lines, by day and session, so a new spike or a new steady
# delay shows against earlier days. Reads scalpfeed.log only.
#   tastytrade: lastPrintDelayMs (the newest print's arrival minus its exchange time, includes PC clock error),
#               maxFrameHandleUs (longest time handling one DXLink frame in the minute)
#   IBKR:       maxFlushUs (longest quote/book flush to Quantower in the minute), booksDelivered, venueQuoteUpdates
#   python -I latency_trend.py <scalpfeed.log> [days]
import re
import sys
from collections import defaultdict
from datetime import datetime

path = sys.argv[1]
days = int(sys.argv[2]) if len(sys.argv) > 2 else 7
TASTY = re.compile(r"^(\S+) (\S+) stats 60s: .*?prints=(\d+) .*?maxFrameHandleUs=(\d+) lastPrintDelayMs=(-?\d+)")
IBKR = re.compile(r"^(\S+) (\S+) \[ibkr\] stats 60s: state=(\w+) quoteUpdates=(\d+)(?: venueQuoteUpdates=(\d+))? depthRowChanges=(\d+) booksDelivered=(\d+) .*?maxFlushUs=(\d+)")


def session(clock):
    # Local time on this PC (UTC-8): premarket 01:00-06:30, regular 06:30-13:00, after hours 13:00-17:00.
    h = int(clock[:2]) + int(clock[3:5]) / 60
    if 1 <= h < 6.5:
        return "pre"
    if 6.5 <= h < 13:
        return "regular"
    if 13 <= h < 17:
        return "after"
    return "overnight"


tasty = defaultdict(lambda: {"delay": [], "frame": [], "prints": 0})
ibkr = defaultdict(lambda: {"flush": [], "books": [], "venue": []})
with open(path, encoding="utf-8", errors="replace") as f:
    for line in f:
        m = TASTY.match(line)
        if m:
            day, clock = m.group(1), m.group(2)
            key = (day, session(clock))
            if int(m.group(3)) > 0:  # a minute with prints, so the delay is current
                tasty[key]["delay"].append(int(m.group(5)))
            tasty[key]["frame"].append(int(m.group(4)))
            tasty[key]["prints"] += int(m.group(3))
            continue
        m = IBKR.match(line)
        if m and m.group(3) == "Live":
            key = (m.group(1), session(m.group(2)))
            ibkr[key]["flush"].append(int(m.group(8)))
            ibkr[key]["books"].append(int(m.group(7)))
            ibkr[key]["venue"].append(int(m.group(5) or 0))


def q(values, p):
    if not values:
        return "-"
    s = sorted(values)
    return s[min(len(s) - 1, int(len(s) * p))]


keys = sorted(set(tasty) | set(ibkr))
recent_days = sorted({k[0] for k in keys})[-days:]
print("tastytrade, per minute with prints: newest print's delay (ms) median / p90 / max; frame handling (us) median / p90 / max")
for day in recent_days:
    for sess in ("pre", "regular", "after", "overnight"):
        t = tasty.get((day, sess))
        if not t or not t["delay"]:
            continue
        d, fr = t["delay"], t["frame"]
        print(f"  {day} {sess:9} {len(d):4} min  delay {q(d, .5)} / {q(d, .9)} / {max(d)}   frame {q(fr, .5)} / {q(fr, .9)} / {max(fr)}   prints {t['prints']}")
print("IBKR, per live minute: longest flush (us) median / p90 / max; books delivered median; venue quote updates median")
for day in recent_days:
    for sess in ("pre", "regular", "after", "overnight"):
        b = ibkr.get((day, sess))
        if not b or not b["flush"]:
            continue
        fl = b["flush"]
        print(f"  {day} {sess:9} {len(fl):4} min  flush {q(fl, .5)} / {q(fl, .9)} / {max(fl)}   books {q(b['books'], .5)}   venue {q(b['venue'], .5)}")

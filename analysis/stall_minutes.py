# Are the ~16 ms slow updates one cause hitting both feeds, and do they follow load? Pairs each minute's
# tastytrade stats line with the IBKR stats line that follows it (they're written about 1.5 s apart and cover
# nearly the same 60 s), then compares how often each feed had a slow one with how often both did.
#   python -I stall_minutes.py <scalpfeed.log> <date> [slow_us]
import re
import sys
from collections import Counter

path, day = sys.argv[1], sys.argv[2]
slow_us = int(sys.argv[3]) if len(sys.argv) > 3 else 8000
TASTY = re.compile(r"^(\S+) (\S+) stats 60s: quotes=\d+ prints=(\d+) .*?frames=(\d+) maxFrameHandleUs=(\d+)")
IBKR = re.compile(r"^(\S+) (\S+) \[ibkr\] stats 60s: state=Live quoteUpdates=(\d+)(?: venueQuoteUpdates=(\d+))? depthRowChanges=(\d+) booksDelivered=(\d+) .*?maxFlushUs=(\d+)")

minutes = []
pending = None
with open(path, encoding="utf-8", errors="replace") as f:
    for line in f:
        if not line.startswith(day):
            continue
        m = TASTY.match(line)
        if m:
            pending = {"clock": m.group(2)[:8], "prints": int(m.group(3)), "frames": int(m.group(4)), "frame_us": int(m.group(5))}
            continue
        m = IBKR.match(line)
        if m and pending is not None:
            pending.update({"quotes": int(m.group(3)), "venue": int(m.group(4) or 0), "rows": int(m.group(5)),
                            "books": int(m.group(6)), "flush_us": int(m.group(7))})
            minutes.append(pending)
            pending = None

n = len(minutes)
if not n:
    raise SystemExit("no paired minutes")
t = sum(1 for m in minutes if m["frame_us"] >= slow_us)
i = sum(1 for m in minutes if m["flush_us"] >= slow_us)
both = sum(1 for m in minutes if m["frame_us"] >= slow_us and m["flush_us"] >= slow_us)
print(f"{day}: {n} paired minutes; a slow one (>= {slow_us} us) in tastytrade {t} ({t / n:.0%}), IBKR {i} ({i / n:.0%}), "
      f"both {both} ({both / n:.0%}); if unrelated, both would be about {t * i / n:.1f}")


def bucket(values, key):
    ordered = sorted(minutes, key=lambda m: m[key])
    third = len(ordered) // 3
    for name, part in (("low", ordered[:third]), ("mid", ordered[third:2 * third]), ("high", ordered[2 * third:])):
        if not part:
            continue
        slow_t = sum(1 for m in part if m["frame_us"] >= slow_us) / len(part)
        slow_i = sum(1 for m in part if m["flush_us"] >= slow_us) / len(part)
        print(f"    {key} {name} third ({part[0][key]}-{part[-1][key]}): slow tastytrade {slow_t:.0%}, slow IBKR {slow_i:.0%}")


print("  by load, in thirds of the minutes:")
for key in ("books", "prints", "rows"):
    bucket(minutes, key)
sizes = Counter(min(m["flush_us"], m["frame_us"]) // 1000 for m in minutes if m["flush_us"] >= slow_us and m["frame_us"] >= slow_us)
print(f"  in minutes slow on both, the smaller of the two maxima, by ms: {dict(sorted(sizes.items()))}")

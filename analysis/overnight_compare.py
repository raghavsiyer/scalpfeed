# Overnight: IBKR's exchange=OVERNIGHT tape against tastytrade's TimeAndSale, over the seconds both were
# recording, compared by exchange time so that different start times can't distort the totals.
#   python -I overnight_compare.py <ibkr.tsv from ibkr_raw.py --capture> <capture.jsonl from ScalpFeedProbe>
import json
import sys
from collections import Counter, defaultdict

ibkr = defaultdict(list)  # symbol -> (seconds, price, size, venue, arrival ms)
for line in open(sys.argv[1], encoding="utf-8"):
    arrival, symbol, exchange, seconds, price, size, venue, _ = line.rstrip("\n").split("\t")
    ibkr[symbol].append((int(seconds), float(price), float(size), venue, float(arrival)))

tasty = defaultdict(list)  # symbol -> (seconds, price, size, code, arrival ms, valid)
volumes = defaultdict(list)  # symbol -> (arrival ms, dayVolume)
layouts = {}
for line in open(sys.argv[2], encoding="utf-8"):
    arrival, payload = line.split("\t", 1)
    message = json.loads(payload)
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
                if data[i] == "TimeAndSale" and not row.get("eventFlags", 0) & 2:
                    tasty[row["eventSymbol"]].append((int(row["time"] // 1000), row["price"], float(row["size"]), row["exchangeCode"], float(arrival), row["validTick"]))
                elif data[i] == "Trade" and isinstance(row.get("dayVolume"), (int, float)):
                    volumes[row["eventSymbol"]].append((float(arrival), row["dayVolume"]))

for symbol in sorted(ibkr):
    begin = max(min(p[0] for p in ibkr[symbol]), min((p[0] for p in tasty[symbol]), default=0))
    end = min(max(p[0] for p in ibkr[symbol]), max((p[0] for p in tasty[symbol]), default=0))
    if end <= begin:
        print(f"{symbol}: no overlap")
        continue
    in_ibkr = [p for p in ibkr[symbol] if begin <= p[0] <= end]
    in_tasty = [p for p in tasty[symbol] if begin <= p[0] <= end]
    shares = Counter()
    prints = Counter()
    for p in in_ibkr:
        shares[p[3]] += p[2]
        prints[p[3]] += 1
    tasty_shares = sum(p[2] for p in in_tasty)
    blue = shares["BLUEOCEAN"]
    # Prints with the same second, price and size on both sides.
    a = Counter((p[0], round(p[1], 4), p[2]) for p in in_ibkr if p[3] == "BLUEOCEAN")
    b = Counter((p[0], round(p[1], 4), p[2]) for p in in_tasty)
    matched = sum((a & b).values())
    # The same price and size within a few seconds, in case the two feeds stamp a print differently.
    for tolerance in (3, 10):
        unused = sorted(in_tasty, key=lambda p: p[0])
        found = 0
        found_shares = 0.0
        for p in sorted((x for x in in_ibkr if x[3] == "BLUEOCEAN"), key=lambda x: x[0]):
            for k, q in enumerate(unused):
                if abs(q[0] - p[0]) <= tolerance and abs(q[1] - p[1]) < 1e-6 and q[2] == p[2]:
                    found += 1
                    found_shares += p[2]
                    del unused[k]
                    break
        print(f"  within {tolerance} s: {found} prints, {found_shares:.0f} shares match")
    prices_ibkr = Counter()
    prices_tasty = Counter()
    for p in in_ibkr:
        prices_ibkr[round(p[1], 2)] += p[2]
    for p in in_tasty:
        prices_tasty[round(p[1], 2)] += p[2]
    common = sum(min(prices_ibkr[x], prices_tasty[x]) for x in prices_ibkr)
    print(f"  shares traded at the same price over the window, whichever second: {common:.0f}")
    volume_rise = [v for t, v in volumes[symbol]]
    rise = volume_rise[-1] - volume_rise[0] if len(volume_rise) > 1 else None
    delays = sorted(p[4] - (p[0] * 1000) for p in in_ibkr)
    tasty_delays = sorted(p[4] - (p[0] * 1000) for p in in_tasty)
    print(f"{symbol}: {end - begin} s overlap")
    print(f"  IBKR prints {dict(prints)} shares {dict(shares)}")
    print(f"  tastytrade prints {len(in_tasty)} shares {tasty_shares:.0f}, codes {dict(Counter(p[3] for p in in_tasty))}; whole-run dayVolume rise {rise}")
    print(f"  tastytrade shares / IBKR BLUEOCEAN {tasty_shares / blue if blue else float('nan'):.3f}, / IBKR all {tasty_shares / sum(shares.values()):.3f}")
    print(f"  exact matches (second, price, size) {matched} of IBKR BLUEOCEAN {sum(a.values())}, tastytrade {sum(b.values())}")
    if delays and tasty_delays:
        print(f"  arrival minus start of print's second, median: IBKR {delays[len(delays) // 2]:.0f} ms, tastytrade {tasty_delays[len(tasty_delays) // 2]:.0f} ms; p99 IBKR {delays[int(len(delays) * 0.99)]:.0f}, tastytrade {tasty_delays[int(len(tasty_delays) * 0.99)]:.0f}")

    batches = sorted({p[4] for p in in_ibkr})
    gaps = sorted(y - x for x, y in zip(batches, batches[1:]) if y - x > 20)
    if gaps:
        print(f"  IBKR gaps between batches (over 20 ms): n={len(gaps)} p50 {gaps[len(gaps) // 2]:.0f} ms, min {gaps[0]:.0f}, max {gaps[-1]:.0f}")

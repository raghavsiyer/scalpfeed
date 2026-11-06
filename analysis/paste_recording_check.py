"""Against the regular-hours recording: how much of the gap to IBKR's tape is odd lots, and how
much is venues? Counts shares by bucket, for the paste's claim that filtering explains the gap.

python -I paste_recording_check.py <capture.jsonl> <ibkr.tsv>
"""
import json
import sys
from collections import defaultdict

FAMILY = {"NASDAQ", "BEX", "PSX", "FINRA"}
fields = {}
tasty = defaultdict(list)
volumes = defaultdict(list)
for line in open(sys.argv[1], encoding="utf-8"):
    arrival, payload = line.split("\t", 1)
    msg = json.loads(payload)
    if msg["type"] == "FEED_CONFIG" and "eventFields" in msg:
        fields.update(msg["eventFields"])
    if msg["type"] != "FEED_DATA" or msg.get("channel") != 1:
        continue
    data = msg["data"]
    for i in range(0, len(data) - 1, 2):
        name = data[i]
        f = fields.get(name)
        if not f or name not in ("TimeAndSale", "Trade"):
            continue
        values = data[i + 1]
        for j in range(0, len(values) - len(f) + 1, len(f)):
            r = dict(zip(f, values[j : j + len(f)]))
            if name == "Trade":
                if isinstance(r.get("dayVolume"), (int, float)):
                    volumes[r["eventSymbol"]].append((float(arrival), r["dayVolume"]))
            elif isinstance(r.get("size"), (int, float)):
                tasty[r["eventSymbol"]].append((float(arrival), r["size"], bool(r.get("validTick")), r.get("exchangeCode")))

ibkr = defaultdict(list)
for line in open(sys.argv[2], encoding="utf-8"):
    p = line.rstrip("\n").split("\t")
    if p[1] == "T":
        ibkr[p[2]].append((float(p[0]), float(p[4]), p[5]))

for sym in sorted(ibkr):
    tt, ii = tasty.get(sym), ibkr[sym]
    if not tt:
        continue
    start, stop = max(tt[0][0], ii[0][0]), min(tt[-1][0], ii[-1][0])
    tt = [e for e in tt if start <= e[0] <= stop]
    ii = [e for e in ii if start <= e[0] <= stop]
    v = [e for e in volumes[sym] if start <= e[0] <= stop]
    rise = v[-1][1] - v[0][1] if len(v) > 1 else 0
    valid = sum(e[1] for e in tt if e[2])
    invalid = sum(e[1] for e in tt if not e[2])
    ib_total = sum(e[1] for e in ii)
    ib_family = sum(e[1] for e in ii if e[2] in FAMILY)
    print(f"\n{sym}: {(stop - start) / 1000:.0f} s, codes {sorted({e[3] for e in tt})}")
    print(f"  dayVolume rise                       {rise:12,.0f}")
    print(f"  IBKR full tape                       {ib_total:12,.0f} = {ib_total / rise:.0%} of the rise" if rise else f"  IBKR full tape {ib_total:,.0f}")
    print(f"  tastytrade valid prints              {valid:12,.0f}")
    print(f"  tastytrade invalid (odd lots mostly) {invalid:12,.0f}")
    print(f"  tastytrade all prints                {valid + invalid:12,.0f} = {(valid + invalid) / ib_total:.0%} of IBKR's tape")
    print(f"  IBKR's Nasdaq, BX, PSX and FINRA     {ib_family:12,.0f}, tastytrade all vs that {(valid + invalid) / ib_family:.1%}")
    print(f"  IBKR's other venues                  {ib_total - ib_family:12,.0f} = {(ib_total - ib_family) / ib_total:.0%} of IBKR's tape")

"""Which prints does the plugin's tape show, against IBKR's full tape, by count and by shares?

python -I missed_prints.py <tastytrade capture.jsonl> <ibkr capture.tsv>
"""
import json
import sys
from collections import Counter, defaultdict

FAMILY = {"NASDAQ", "BEX", "PSX", "FINRA"}

fields = {}
tasty = defaultdict(list)
for line in open(sys.argv[1], encoding="utf-8"):
    arrival, payload = line.split("\t", 1)
    msg = json.loads(payload)
    if msg.get("channel") != 1:
        continue
    if msg["type"] == "FEED_CONFIG":
        fields.update(msg.get("eventFields", {}))
    elif msg["type"] == "FEED_DATA":
        data = msg["data"]
        for i in range(0, len(data) - 1, 2):
            if data[i] != "TimeAndSale":
                continue
            f = fields["TimeAndSale"]
            values = data[i + 1]
            for j in range(0, len(values) - len(f) + 1, len(f)):
                r = dict(zip(f, values[j : j + len(f)]))
                if isinstance(r["price"], (int, float)):
                    tasty[r["eventSymbol"]].append((float(arrival), float(r["size"]), bool(r["validTick"]), str(r.get("exchangeSaleConditions"))))

ibkr = defaultdict(list)
for line in open(sys.argv[2], encoding="utf-8"):
    p = line.rstrip("\n").split("\t")
    if p[1] == "T":
        ibkr[p[2]].append((float(p[0]), float(p[4]), p[5], p[6] if len(p) > 6 else ""))

for sym in sorted(ibkr):
    tt, ii = tasty.get(sym, []), ibkr[sym]
    if not tt or not ii:
        continue
    start, stop = max(tt[0][0], ii[0][0]), min(tt[-1][0], ii[-1][0])
    tt = [e for e in tt if start <= e[0] <= stop]
    ii = [e for e in ii if start <= e[0] <= stop]
    if not tt or not ii:
        continue

    shown = [e for e in tt if e[2]]
    hidden = [e for e in tt if not e[2]]
    i_n, i_s = len(ii), sum(e[1] for e in ii)
    print(f"\n{sym}: {(stop - start) / 1000:.0f} s")
    print(f"  IBKR full tape:             {i_n:6} prints {i_s:10,.0f} shares")
    print(f"  tastytrade, all:            {len(tt):6} prints {sum(e[1] for e in tt):10,.0f} shares")
    print(f"  plugin shows (valid only):  {len(shown):6} prints {sum(e[1] for e in shown):10,.0f} shares = {len(shown) / i_n:.0%} of IBKR's prints, {sum(e[1] for e in shown) / i_s:.0%} of its shares")
    print(f"  plugin hides (invalid):     {len(hidden):6} prints {sum(e[1] for e in hidden):10,.0f} shares")
    print(f"    their conditions: {Counter(e[3] for e in hidden).most_common(6)}")
    print(f"    their sizes under 100: {sum(1 for e in hidden if e[1] < 100)} of {len(hidden)}")

    by_venue = defaultdict(lambda: [0, 0.0])
    for e in ii:
        by_venue[e[2]][0] += 1
        by_venue[e[2]][1] += e[1]
    other_n = sum(v[0] for k, v in by_venue.items() if k not in FAMILY)
    other_s = sum(v[1] for k, v in by_venue.items() if k not in FAMILY)
    print(f"  on IBKR from venues tastytrade lacks: {other_n} prints ({other_n / i_n:.0%}), {other_s:,.0f} shares ({other_s / i_s:.0%})")
    top = sorted(by_venue.items(), key=lambda kv: -kv[1][0])[:10]
    print("    " + ", ".join(f"{k} {v[0]}" for k, v in top))
    odd = [e for e in ii if e[1] < 100]
    print(f"  IBKR prints under 100 shares: {len(odd)} of {i_n} ({len(odd) / i_n:.0%})")
    gone_n = len(hidden) + other_n
    print(f"  so of IBKR's {i_n} prints: about {len(shown)} show, about {len(hidden)} hidden by the valid-only filter, about {other_n} never reach tastytrade")

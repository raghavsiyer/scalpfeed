"""Replay a recording of both tapes through the proposed merge, in arrival order.

Rule: tastytrade's prints show at once. From IBKR, drop venues tastytrade carries, pass venues
it doesn't, and for FINRA (on both tapes, split, no shared ID) pass a print only if tastytrade
hasn't shown the same price and size recently. If IBKR's copy got there first, the later
tastytrade copy is dropped instead.

python -I simulate_merge.py <tastytrade capture.jsonl> <ibkr capture.tsv>
"""
import json
import sys
from collections import Counter, defaultdict, deque

CARRIED = {"NASDAQ", "BEX", "PSX"}
SHARED = {"FINRA"}
MATCH_WINDOW_MS = 2000
ODD_OR_OFF_MARKET = set("I47WV")


def pct(values, p):
    s = sorted(values)
    return s[min(len(s) - 1, int(p * len(s)))] if s else float("nan")


fields = {}
events = defaultdict(list)
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
                    events[r["eventSymbol"]].append((float(arrival), "tasty", round(r["price"], 4), round(r["size"], 2), bool(r["validTick"]), str(r["exchangeSaleConditions"])))

ib_symbols = set()
for line in open(sys.argv[2], encoding="utf-8"):
    p = line.rstrip("\n").split("\t")
    if p[1] == "T":
        cond = p[6] if len(p) > 6 else ""
        events[p[2]].append((float(p[0]), "ibkr", round(float(p[3]), 4), round(float(p[4]), 2), p[5], cond))
        ib_symbols.add(p[2])

for sym in sorted(ib_symbols):
    ev = sorted(events[sym], key=lambda e: e[0])
    tt = [e for e in ev if e[1] == "tasty"]
    ii = [e for e in ev if e[1] == "ibkr"]
    start, stop = max(tt[0][0], ii[0][0]), min(tt[-1][0], ii[-1][0])
    ev = [e for e in ev if start <= e[0] <= stop]

    recent_tasty = defaultdict(deque)
    recent_ibkr = defaultdict(deque)
    shown = Counter()
    shares = Counter()
    fin_passed_lag = []
    delays = []

    def expire(q, t):
        while q and t - q[0] > MATCH_WINDOW_MS:
            q.popleft()

    for e in ev:
        t, source, price, size = e[0], e[1], e[2], e[3]
        key = (price, size)
        if source == "tasty":
            q = recent_ibkr[key]
            expire(q, t)
            if q:
                q.popleft()
                shown["tasty dropped: IBKR's FINRA copy got there first"] += 1
                shares["dropped late tasty"] += size
                continue
            recent_tasty[key].append(t)
            shown["tasty shown"] += 1
            shares["tasty shown"] += size
        else:
            venue = e[4]
            shares["ibkr total"] += size
            if venue in CARRIED:
                shown["ibkr dropped: venue tastytrade carries"] += 1
                shares["ibkr carried"] += size
            elif venue in SHARED:
                q = recent_tasty[key]
                expire(q, t)
                if q:
                    q.popleft()
                    shown["ibkr FINRA dropped: matched tastytrade"] += 1
                    shares["ibkr finra matched"] += size
                else:
                    recent_ibkr[key].append(t)
                    shown["ibkr FINRA added"] += 1
                    shares["ibkr finra added"] += size
            else:
                shown["ibkr added: venue tastytrade lacks"] += 1
                shares["ibkr other venues added"] += size

    merged = shares["tasty shown"] + shares["ibkr finra added"] + shares["ibkr other venues added"]
    total = shares["ibkr total"] or 1
    print(f"\n{sym}: {(stop - start) / 1000:.0f} s")
    for k, v in shown.most_common():
        print(f"  {v:6} {k}")
    print(f"  IBKR's tape {total:,.0f} shares | tastytrade alone {sum(e[3] for e in ev if e[1] == 'tasty'):,.0f} | merged {merged:,.0f} = {merged / total:.1%} of IBKR's")
    print(f"    tastytrade shown {shares['tasty shown']:,.0f}, FINRA added {shares['ibkr finra added']:,.0f}, other venues added {shares['ibkr other venues added']:,.0f}")

    # What doesn't line up? Tastytrade prints with no same price and size anywhere on IBKR's tape.
    ib_keys = Counter((e[2], e[3]) for e in ev if e[1] == "ibkr")
    orphans = []
    for e in ev:
        if e[1] == "tasty":
            key = (e[2], e[3])
            if ib_keys[key] > 0:
                ib_keys[key] -= 1
            else:
                orphans.append(e)
    tasty_n = sum(1 for e in ev if e[1] == "tasty")
    print(f"  tastytrade prints with no same price and size on IBKR's tape: {len(orphans)} of {tasty_n} ({len(orphans) / max(tasty_n, 1):.0%}), {sum(e[3] for e in orphans):,.0f} shares")
    print(f"    their conditions: {Counter(e[5] for e in orphans).most_common(6)}")
    print(f"    their sizes: {Counter(int(e[3]) if e[3] == int(e[3]) else 'fractional' for e in orphans).most_common(8)}")
    print(f"    samples (price, size, valid): {[(e[2], e[3], e[4]) for e in orphans[:6]]}")
    ib_sizes = Counter(int(e[3]) if e[3] == int(e[3]) else "fractional" for e in ev if e[1] == "ibkr")
    print(f"    IBKR's most common sizes: {ib_sizes.most_common(8)}")

    # Simpler rule with no matching: everything from tastytrade, plus IBKR's venues outside
    # Nasdaq's exchanges and FINRA. Duplicates can't come from matching errors because none happens.
    tasty_only = {"NASDAQ", "BEX", "PSX", "FINRA"}
    tasty_shares = sum(e[3] for e in ev if e[1] == "tasty")
    added = sum(e[3] for e in ev if e[1] == "ibkr" and e[4] not in tasty_only)
    ib_all = sum(e[3] for e in ev if e[1] == "ibkr")
    ib_family = sum(e[3] for e in ev if e[1] == "ibkr" and e[4] in tasty_only)
    print(f"  PARTITION RULE: tastytrade {tasty_shares:,.0f} + IBKR other venues {added:,.0f} = {tasty_shares + added:,.0f} = {(tasty_shares + added) / ib_all:.1%} of IBKR's tape")
    print(f"    tastytrade vs IBKR's Nasdaq family plus FINRA: {tasty_shares:,.0f} vs {ib_family:,.0f} ({tasty_shares / ib_family:.1%})")

    # Is that steady, or two errors cancelling out? Compare in short buckets.
    BUCKET_MS = 10000
    buckets = defaultdict(lambda: [0.0, 0.0, 0.0, 0.0])
    for e in ev:
        b = buckets[int(e[0] // BUCKET_MS)]
        if e[1] == "tasty":
            b[0] += e[3]
        else:
            b[1] += e[3]
            if e[4] in tasty_only:
                b[2] += e[3]
            else:
                b[3] += e[3]
    ratios = [(b[0] + b[3]) / b[1] for b in buckets.values() if b[1] > 0]
    family = [b[0] / b[2] for b in buckets.values() if b[2] > 0]
    print(f"    in 10 s buckets, merged vs IBKR: min {min(ratios):.0%}, median {pct(ratios, .5):.0%}, max {max(ratios):.0%} | tastytrade vs its IBKR venues: min {min(family):.0%}, median {pct(family, .5):.0%}, max {max(family):.0%}")

    # The chart only uses valid prints, so check the same rule without odd lots and off-market trades.
    ib_ok = lambda e: not (set(e[5].split()) & ODD_OR_OFF_MARKET) and "B" not in e[5].split()
    tv = sum(e[3] for e in ev if e[1] == "tasty" and e[4])
    ivf = sum(e[3] for e in ev if e[1] == "ibkr" and e[4] in tasty_only and ib_ok(e))
    iva = sum(e[3] for e in ev if e[1] == "ibkr" and e[4] not in tasty_only and ib_ok(e))
    print(f"    valid prints only: tastytrade {tv:,.0f} vs IBKR's same venues {ivf:,.0f} ({tv / max(ivf, 1):.1%}); merged {tv + iva:,.0f} vs IBKR's all venues {ivf + iva:,.0f} ({(tv + iva) / max(ivf + iva, 1):.1%})")

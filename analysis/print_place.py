# Where prints hit the NBBO, and which quote to judge them against. tastytrade's prints arrive about when they
# trade, so they show how much a print's place changes if it's judged against an earlier or later quote. IBKR's
# only rows are judged by the quote when they arrive, and by the quote about when they traded.
#   python -I print_place.py <tsv from ibkr_raw.py --capture, with quotes> <capture.jsonl from ScalpFeedProbe>
import bisect
import json
import sys
from collections import Counter, defaultdict
from math import floor

ORDER = ["above ask", "at ask", "between", "at bid", "below bid", "no quote"]
CARRIED = {"NASDAQ", "BEX", "PSX"}


def place(price, bid, ask):
    if bid is None or ask is None or bid <= 0 or ask <= 0:
        return "no quote"
    if price > ask + 1e-9:
        return "above ask"
    if abs(price - ask) <= 1e-9:
        return "at ask"
    if price < bid - 1e-9:
        return "below bid"
    if abs(price - bid) <= 1e-9:
        return "at bid"
    return "between"


def same_price(a, b):
    return abs(a - b) <= 0.00011 or (b >= 1 and abs(a - b) <= 0.00501) or (a >= 1 and abs(a - b) <= 0.00501)


quotes = defaultdict(lambda: {"BID": ([], []), "ASK": ([], [])})
ibkr = defaultdict(list)
for line in open(sys.argv[1], encoding="utf-8"):
    p = line.rstrip("\n").split("\t")
    if len(p) != 8:
        continue
    if p[2] in ("BID", "ASK"):
        times, prices = quotes[p[1]][p[2]]
        times.append(float(p[0]))
        prices.append(float(p[4]))
    elif p[2] == "SMART":
        ibkr[p[1]].append({"arrival": float(p[0]), "s": int(float(p[3])), "price": float(p[4]), "size": float(p[5]), "venue": p[6], "used": False})

tasty = defaultdict(list)
layouts = {}
for line in open(sys.argv[2], encoding="utf-8"):
    try:
        arrival, payload = line.split("\t", 1)
        message = json.loads(payload)
    except ValueError:
        continue
    kind = message.get("type")
    if kind == "FEED_CONFIG" and "eventFields" in message:
        for name, fields in message["eventFields"].items():
            layouts[(message["channel"], name)] = fields
    elif kind == "FEED_DATA" and message.get("channel") == 1:
        data = message["data"]
        for i in range(0, len(data) - 1, 2):
            fields = layouts.get((1, data[i]))
            if data[i] != "TimeAndSale" or not fields:
                continue
            for j in range(0, len(data[i + 1]), len(fields)):
                r = dict(zip(fields, data[i + 1][j : j + len(fields)]))
                if not r.get("eventFlags", 0) & 2:
                    tasty[r["eventSymbol"]].append({"arrival": float(arrival), "ms": r["time"], "price": r["price"], "size": float(r["size"]), "bid": r.get("bidPrice"), "ask": r.get("askPrice"), "side": r.get("aggressorSide")})


def quote_at(symbol, moment):
    out = []
    for name in ("BID", "ASK"):
        times, prices = quotes[symbol][name]
        k = bisect.bisect_right(times, moment) - 1
        out.append(prices[k] if k >= 0 else None)
    return out


def share(counter):
    total = sum(counter.values())
    return ", ".join(f"{name} {counter[name] / total:.0%}" for name in ORDER if counter[name]) if total else "none"


for symbol in sorted(ibkr):
    rows = tasty.get(symbol, [])
    if not rows or not quotes[symbol]["BID"][0]:
        print(f"{symbol}: no quotes or no tastytrade prints")
        continue
    bid_changes = len(quotes[symbol]["BID"][0]) + len(quotes[symbol]["ASK"][0])
    print(f"\n{symbol}: {len(rows)} tastytrade prints, {len(ibkr[symbol])} IBKR prints, {bid_changes} quote updates")
    print(f"  tastytrade prints against tastytrade's own quote: {share(Counter(place(r['price'], r['bid'], r['ask']) for r in rows))}")
    now = {r["arrival"]: None for r in rows}
    base = [place(r["price"], *quote_at(symbol, r["arrival"])) for r in rows]
    print(f"  tastytrade prints against IBKR's NBBO on arrival:  {share(Counter(base))}")
    for shift in (-450, -200, 200, 450):
        other = [place(r["price"], *quote_at(symbol, r["arrival"] + shift)) for r in rows]
        same = sum(1 for a, b in zip(base, other) if a == b)
        print(f"    judged by the quote {abs(shift)} ms {'earlier' if shift < 0 else 'later'}: {share(Counter(other))}; same place as on arrival {same / len(rows):.0%}")
    labelled = Counter((r["side"], b) for r, b in zip(rows, base))
    wrong = sum(v for (s, b), v in labelled.items() if (s == "BUY" and b in ("at bid", "below bid")) or (s == "SELL" and b in ("at ask", "above ask")))
    mid = sum(v for (s, b), v in labelled.items() if b == "between")
    print(f"  tastytrade's label: colored green or red today but between the NBBO {mid / len(rows):.0%}; labelled against where it hit {wrong / len(rows):.0%}")

    index = defaultdict(list)
    for r in ibkr[symbol]:
        index[floor(r["size"])].append(r)
    for r in rows:
        options = [x for x in index.get(floor(r["size"]), []) if not x["used"] and abs(x["s"] - r["ms"] // 1000) <= 3 and same_price(x["price"], r["price"])]
        if options:
            options[0]["used"] = True
    only = [r for r in ibkr[symbol] if not r["used"] and r["venue"] not in CARRIED]
    at_arrival = [place(r["price"], *quote_at(symbol, r["arrival"])) for r in only]
    earlier = [place(r["price"], *quote_at(symbol, r["arrival"] - 450)) for r in only]
    if only:
        same = sum(1 for a, b in zip(at_arrival, earlier) if a == b)
        print(f"  IBKR-only rows, {len(only)}: by the NBBO on arrival {share(Counter(at_arrival))}")
        print(f"    by the NBBO 450 ms before arrival, about when they traded: {share(Counter(earlier))}; same place {same / len(only):.0%}")

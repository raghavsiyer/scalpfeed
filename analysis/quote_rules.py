# Which quote to judge a print against, and how often it says the print was outside the market. tastytrade's
# prints arrive about when they trade and carry a quote of their own; IBKR-only rows arrive about 0.4 s late.
#   python -I quote_rules.py <tsv from ibkr_raw.py --capture> <capture.jsonl from ScalpFeedProbe>
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


quotes = defaultdict(lambda: defaultdict(lambda: ([], [])))
ibkr = defaultdict(list)
for line in open(sys.argv[1], encoding="utf-8"):
    p = line.rstrip("\n").split("\t")
    if len(p) != 8:
        continue
    if p[2] in ("BID", "ASK", "BIDTBT", "ASKTBT"):
        times, prices = quotes[p[1]][p[2]]
        if not prices or prices[-1] != float(p[4]):
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
                    tasty[r["eventSymbol"]].append({"arrival": float(arrival), "ms": r["time"], "price": r["price"], "size": float(r["size"]), "bid": r.get("bidPrice"), "ask": r.get("askPrice")})


def at(symbol, name, moment):
    times, prices = quotes[symbol][name]
    k = bisect.bisect_right(times, moment) - 1
    return prices[k] if k >= 0 else None


def over(symbol, name, begin, end):
    times, prices = quotes[symbol][name]
    k = bisect.bisect_right(times, begin) - 1
    j = bisect.bisect_right(times, end)
    return prices[max(k, 0) : j] if prices else []


def share(counter):
    total = sum(counter.values())
    return ", ".join(f"{name} {counter[name] / total:.1%}" for name in ORDER if counter[name]) if total else "none"


def judged(rows, source, shift):
    return Counter(place(r["price"], at(sym, "BID" + source, r["arrival"] + shift), at(sym, "ASK" + source, r["arrival"] + shift)) for r in rows)


for sym in sorted(set(ibkr) | set(tasty)):
    rows = tasty.get(sym, [])
    if not rows or not quotes[sym]["BID"][0]:
        continue
    print(f"\n{sym}: {len(rows)} tastytrade prints; quote changes: reqMktData {len(quotes[sym]['BID'][0]) + len(quotes[sym]['ASK'][0])}, tick-by-tick {len(quotes[sym]['BIDTBT'][0]) + len(quotes[sym]['ASKTBT'][0])}")
    print("  tastytrade prints:")
    print(f"    its own quote                 {share(Counter(place(r['price'], r['bid'], r['ask']) for r in rows))}")
    for shift in (0, 100, 200, 300):
        print(f"    reqMktData, {shift:>3} ms after print {share(judged(rows, '', shift))}")
    if quotes[sym]["BIDTBT"][0]:
        for shift in (0, 100, 200, 300):
            print(f"    tick-by-tick, {shift:>3} ms after    {share(judged(rows, 'TBT', shift))}")

        # How far behind each other the two sources are, by the same price appearing in both.
        for name in ("BID", "ASK"):
            times, prices = quotes[sym][name]
            t_times, t_prices = quotes[sym][name + "TBT"]
            leads = []
            for a, price in zip(times, prices):
                candidates = [b for b, q in zip(t_times, t_prices) if q == price and abs(b - a) <= 2000]
                if candidates:
                    leads.append(min(candidates, key=lambda b: abs(b - a)) - a)
            leads.sort()
            if leads:
                print(f"    {name} on tick-by-tick minus reqMktData: median {leads[len(leads) // 2]:.0f} ms, p10 {leads[len(leads) // 10]:.0f}, p90 {leads[len(leads) * 9 // 10]:.0f} ({len(leads)} matched)")

    # The rule: judged by the NBBO now, but it's only outside if it's beyond its own quote as well.
    result = Counter()
    for r in rows:
        bid, ask = at(sym, "BID", r["arrival"]), at(sym, "ASK", r["arrival"])
        p = place(r["price"], bid, ask)
        if p == "above ask" and r["ask"] and r["ask"] > 0 and r["price"] <= r["ask"] + 1e-9:
            p = "at ask"
        if p == "below bid" and r["bid"] and r["bid"] > 0 and r["price"] >= r["bid"] - 1e-9:
            p = "at bid"
        result[p] += 1
    print(f"    the rule, NBBO and its own    {share(result)}")

    # What the plugin does: also not outside if any quote standing in the last 800 ms covers it.
    result = Counter()
    for r in rows:
        bid, ask = at(sym, "BID", r["arrival"]), at(sym, "ASK", r["arrival"])
        p = place(r["price"], bid, ask)
        asks = over(sym, "ASK", r["arrival"] - 800, r["arrival"])
        bids = over(sym, "BID", r["arrival"] - 800, r["arrival"])
        if p == "above ask" and ((r["ask"] and r["ask"] > 0 and r["price"] <= r["ask"] + 1e-9) or (asks and r["price"] <= max(asks) + 1e-9)):
            p = "at ask"
        if p == "below bid" and ((r["bid"] and r["bid"] > 0 and r["price"] >= r["bid"] - 1e-9) or (bids and r["price"] >= min(bids) - 1e-9)):
            p = "at bid"
        result[p] += 1
    print(f"    the plugin's rule, with 800 ms {share(result)}")

    if sym not in ibkr:
        continue
    index = defaultdict(list)
    for r in ibkr[sym]:
        index[floor(r["size"])].append(r)
    for r in rows:
        options = [x for x in index.get(floor(r["size"]), []) if not x["used"] and abs(x["s"] - r["ms"] // 1000) <= 3 and same_price(x["price"], r["price"])]
        if options:
            options[0]["used"] = True
    only = [r for r in ibkr[sym] if not r["used"] and r["venue"] not in CARRIED]
    print(f"  IBKR-only rows, {len(only)}:")
    for shift in (0, -100, -200, -300, -400, -500):
        print(f"    quote {abs(shift):>3} ms before arrival    {share(judged(only, '', shift))}")
    for main, window in ((0, 800), (-200, 500), (-200, 800), (-300, 800), (-300, 1000)):
        result = Counter()
        for r in only:
            moment = r["arrival"] + main
            p = place(r["price"], at(sym, "BID", moment), at(sym, "ASK", moment))
            asks = over(sym, "ASK", r["arrival"] - window, r["arrival"])
            bids = over(sym, "BID", r["arrival"] - window, r["arrival"])
            if p == "above ask" and asks and r["price"] <= max(asks) + 1e-9:
                p = "at ask"
            if p == "below bid" and bids and r["price"] >= min(bids) - 1e-9:
                p = "at bid"
            result[p] += 1
        print(f"    rule: {abs(main)} ms back, outside only if beyond the last {window} ms: {share(result)}")

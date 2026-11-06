"""Do the bid and ask carried on TimeAndSale prints update sooner than the Quote stream?

python -I analyze_quote_lead.py <capture.jsonl> [<capture.jsonl> ...]
"""
import json
import sys
from bisect import bisect_right
from collections import defaultdict

LIVE = 1
WINDOW_MS = 2000


def pct(values, p):
    s = sorted(values)
    return s[min(len(s) - 1, int(p * len(s)))]


for path in sys.argv[1:]:
    fields = {}
    quotes = defaultdict(list)
    prints = defaultdict(list)
    for line in open(path, encoding="utf-8"):
        arrival, payload = line.split("\t", 1)
        msg = json.loads(payload)
        if msg.get("channel") != LIVE:
            continue
        if msg["type"] == "FEED_CONFIG":
            fields.update(msg.get("eventFields", {}))
        elif msg["type"] == "FEED_DATA":
            data = msg["data"]
            for i in range(0, len(data) - 1, 2):
                name, values = data[i], data[i + 1]
                if name not in ("Quote", "TimeAndSale"):
                    continue
                f = fields[name]
                for j in range(0, len(values) - len(f) + 1, len(f)):
                    r = dict(zip(f, values[j : j + len(f)]))
                    book = (r["bidPrice"], r["askPrice"])
                    if isinstance(book[0], (int, float)) and isinstance(book[1], (int, float)):
                        (quotes if name == "Quote" else prints)[r["eventSymbol"]].append((float(arrival), book))

    print(f"\n{path.split(chr(92))[-1]}")
    print(f"  {'':5} {'Quote changes':>13} {'print changes':>13} {'previewed':>10} {'lead p50':>9} {'lead p90':>9} {'behind quote':>13}")
    for sym in sorted(quotes):
        qs, ps = quotes[sym], prints[sym]
        q_changes = [(t, b) for k, (t, b) in enumerate(qs) if k == 0 or b != qs[k - 1][1]]
        p_changes = [(t, b) for k, (t, b) in enumerate(ps) if k == 0 or b != ps[k - 1][1]]

        # For each new Quote value, did a print already show that same bid and ask, and how early?
        leads = []
        for t, book in q_changes[1:]:
            earlier = [pt for pt, pb in p_changes if t - WINDOW_MS <= pt <= t and pb == book]
            if earlier:
                leads.append(t - earlier[0])

        # For each print, was its bid and ask older than the Quote stream's latest?
        q_times = [t for t, _ in qs]
        stale = 0
        for t, book in ps:
            k = bisect_right(q_times, t) - 1
            if k >= 0 and qs[k][1] != book:
                stale += 1

        share = len(leads) / max(len(q_changes) - 1, 1)
        behind = stale / max(len(ps), 1)
        p50 = pct(leads, 0.5) if leads else float("nan")
        p90 = pct(leads, 0.9) if leads else float("nan")
        print(f"  {sym:5} {len(q_changes):13} {len(p_changes):13} {share:10.0%} {p50:7.0f}ms {p90:7.0f}ms {behind:13.0%}")

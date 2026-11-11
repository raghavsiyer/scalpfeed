# Does the smart depth book's best price match the NBBO, and what are its rows made of? Keeps the book from
# IBKR's row updates next to reqMktData's quote, sampling both whenever either changes. Market data only: the
# same whitelist idea as ibkr_raw.py, and the account message is never printed.
#   python -I depth_vs_nbbo.py <port> <client> <symbol> <seconds>
import socket
import struct
import sys
import time
from collections import Counter

ALLOWED = {71, 59, 1, 10}
port, client, symbol, seconds = int(sys.argv[1]), int(sys.argv[2]), sys.argv[3], float(sys.argv[4])
sock = socket.create_connection(("127.0.0.1", port), timeout=10)
sock.setsockopt(socket.IPPROTO_TCP, socket.TCP_NODELAY, 1)


def frame(text):
    body = text.encode("ascii")
    return struct.pack(">I", len(body)) + body


def send(*fields):
    if int(fields[0]) not in ALLOWED:
        raise SystemExit(f"message {fields[0]} is not allowed")
    sock.sendall(frame("".join(("1" if f is True else "0" if f is False else str(f)) + "\0" for f in fields)))


def read_exact(count):
    data = b""
    while len(data) < count:
        chunk = sock.recv(count - len(data))
        if not chunk:
            raise EOFError
        data += chunk
    return data


def read_frame():
    return read_exact(struct.unpack(">I", read_exact(4))[0]).decode("utf-8", "replace").split("\0")


sock.sendall(b"API\0" + frame("v100..176"))
read_frame()
send(71, 2, client, "")
time.sleep(0.3)
send(59, 1, 1)
contract = [0, symbol, "STK", "", "0.0", "", "", "SMART", "", "USD", "", ""]
send(1, 11, 9001, *contract, False, "", False, False, "")
send(10, 5, 9002, *contract, 50, True, "")

books = {0: [], 1: []}  # 0 ask, 1 bid, as IBKR numbers them
quote = {"bid": None, "ask": None}
samples = Counter()
differ = []
top_venues = {"bid": Counter(), "ask": Counter()}
venues_all = Counter()
venue_shares = Counter()
sizes_at_top = {"bid": Counter(), "ask": Counter()}
errors = []
updates = 0
sock.settimeout(0.5)
stop_at = time.time() + seconds


clock = {"last": None, "state": None}
timed = Counter()
episodes = {"bid": [], "ask": []}
open_episode = {"bid": None, "ask": None}
first_seen = {"bid": {}, "ask": {}}
leads = {"bid": [], "ask": []}
last_nbbo = {"bid": None, "ask": None}


def track(name, book_price, nbbo_price, now, worse):
    # A stretch where the book's best is worse than the NBBO, and how long it lasted.
    first_seen[name].setdefault(book_price, now)
    if nbbo_price != last_nbbo[name]:
        if last_nbbo[name] is not None and nbbo_price in first_seen[name]:
            leads[name].append(now - first_seen[name][nbbo_price])
        last_nbbo[name] = nbbo_price
    if worse:
        if open_episode[name] is None:
            open_episode[name] = (now, book_price, nbbo_price)
    elif open_episode[name] is not None:
        begun, book, nbbo = open_episode[name]
        episodes[name].append((now - begun, book, nbbo))
        open_episode[name] = None


def sample():
    bids, asks = books[1], books[0]
    if not bids or not asks or quote["bid"] is None or quote["ask"] is None:
        return
    best_bid = max(r[0] for r in bids)
    best_ask = min(r[0] for r in asks)
    now = time.time()
    if clock["last"] is not None:
        span = now - clock["last"]
        timed["all"] += span
        for key in clock["state"]:
            timed[key] += span
    clock["last"] = now
    bid_state = "bid same" if abs(best_bid - quote["bid"]) < 1e-9 else "bid worse" if best_bid < quote["bid"] else "bid better"
    ask_state = "ask same" if abs(best_ask - quote["ask"]) < 1e-9 else "ask worse" if best_ask > quote["ask"] else "ask better"
    clock["state"] = (bid_state, ask_state)
    track("bid", best_bid, quote["bid"], now, bid_state == "bid worse")
    track("ask", best_ask, quote["ask"], now, ask_state == "ask worse")
    for name in ("bid", "ask"):
        for price in [p for p, t in first_seen[name].items() if now - t > 5]:
            del first_seen[name][price]
    samples["all"] += 1
    samples["bid same"] += abs(best_bid - quote["bid"]) < 1e-9
    samples["ask same"] += abs(best_ask - quote["ask"]) < 1e-9
    samples["both same"] += abs(best_bid - quote["bid"]) < 1e-9 and abs(best_ask - quote["ask"]) < 1e-9
    samples["book bid worse"] += best_bid < quote["bid"] - 1e-9
    samples["book bid better"] += best_bid > quote["bid"] + 1e-9
    samples["book ask worse"] += best_ask > quote["ask"] + 1e-9
    samples["book ask better"] += best_ask < quote["ask"] - 1e-9
    for name, rows, best in (("bid", bids, best_bid), ("ask", asks, best_ask)):
        for r in rows:
            if abs(r[0] - best) < 1e-9:
                top_venues[name][r[2] or "(blank)"] += 1
                sizes_at_top[name]["100 or fewer" if r[1] <= 100 else "over 100"] += 1
    if abs(best_bid - quote["bid"]) > 1e-9 or abs(best_ask - quote["ask"]) > 1e-9:
        if len(differ) < 8:
            # A clock time, so it can't be read as a price (a bare seconds count once was).
            differ.append((time.strftime("%H:%M:%S", time.localtime(now)) + f".{int(now % 1 * 10)}", best_bid, quote["bid"], best_ask, quote["ask"]))


try:
    while time.time() < stop_at:
        try:
            f = read_frame()
        except (TimeoutError, socket.timeout):
            continue
        kind = f[0]
        if kind == "1" and int(f[2]) == 9001 and f[3] in ("1", "2"):
            quote["bid" if f[3] == "1" else "ask"] = float(f[4])
            sample()
        elif kind == "13" and int(f[2]) == 9002:
            position, venue, operation, side, price, size = int(f[3]), f[4], int(f[5]), int(f[6]), float(f[7]), float(f[8])
            rows = books[side]
            if operation == 0 and position <= len(rows):
                rows.insert(position, (price, size, venue))
            elif operation == 1 and position < len(rows):
                rows[position] = (price, size, venue)
            elif operation == 2 and position < len(rows):
                rows.pop(position)
            updates += 1
            sample()
        elif kind == "4":
            code = int(f[3])
            if code not in (2104, 2106, 2158, 2119):
                errors.append(f"{code}: {f[4][:210]}")
except EOFError:
    errors.append("socket closed")
sock.close()

print(f"{symbol}, {seconds:.0f} s: {updates} depth updates, {samples['all']} samples")
for line in errors[:4]:
    print(f"  {line}")
total = samples["all"] or 1
print(f"  book's best bid = NBBO bid {samples['bid same'] / total:.1%}; ask {samples['ask same'] / total:.1%}; both {samples['both same'] / total:.1%}")
print(f"  book worse than NBBO: bid {samples['book bid worse'] / total:.1%}, ask {samples['book ask worse'] / total:.1%}; book better: bid {samples['book bid better'] / total:.1%}, ask {samples['book ask better'] / total:.1%}")
span = timed["all"] or 1
print(f"  by time: bid same {timed['bid same'] / span:.1%}, book worse {timed['bid worse'] / span:.1%}, book better {timed['bid better'] / span:.1%}; ask same {timed['ask same'] / span:.1%}, book worse {timed['ask worse'] / span:.1%}, book better {timed['ask better'] / span:.1%}")
for name in ("bid", "ask"):
    lasting = sorted(e[0] for e in episodes[name])
    long_ones = [e for e in episodes[name] if e[0] >= 1]
    middle = lasting[len(lasting) // 2] * 1000 if lasting else 0
    print(f"  {name}: book worse than the NBBO {len(lasting)} times, median {middle:.0f} ms; lasting 1 s or more {len(long_ones)}, {sum(e[0] for e in long_ones):.1f} s in all; longest (s, book, NBBO) {sorted(episodes[name], reverse=True)[:3]}")
    gaps = sorted(leads[name])
    if gaps:
        print(f"    a new NBBO {name} had already been the book's best for: median {gaps[len(gaps) // 2] * 1000:.0f} ms, p90 {gaps[len(gaps) * 9 // 10] * 1000:.0f} ms ({len(gaps)} of the NBBO's changes)")
print(f"  venues at the best bid: {dict(top_venues['bid'].most_common(8))}")
print(f"  venues at the best ask: {dict(top_venues['ask'].most_common(8))}")
print(f"  sizes at the best price: bid {dict(sizes_at_top['bid'])}, ask {dict(sizes_at_top['ask'])}")
for side, rows in (("bid", books[1]), ("ask", books[0])):
    named = Counter(r[2] or "(blank)" for r in rows)
    shares = Counter()
    for r in rows:
        shares[r[2] or "(blank)"] += r[1]
    print(f"  book at the end, {side}: {len(rows)} rows, by venue {dict(named.most_common())}, shares {dict((k, int(v)) for k, v in shares.most_common())}")
    print(f"    first rows: {[(r[0], int(r[1]), r[2] or '(blank)') for r in rows[:6]]}")
print(f"  when they differed, first few (time, book bid, NBBO bid, book ask, NBBO ask): {differ}")

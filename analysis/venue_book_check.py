# Does adding ARCA's, NYSE's and BX's own quotes to IBKR's smart depth make the book's best match the NBBO,
# and do those quotes trail it? Keeps the smart book, the SMART quote (with IBKR's strings naming the venues at
# the best, ticks 32 and 33) and each venue's directed quote, and compares by time:
#   book alone vs the NBBO, and book plus venue rows vs the NBBO;
#   for each price both the NBBO and ARCA's own quote reached, how much later ARCA's got there.
# Market data only (71, 59, 1, 2, 10, 11); the account message is never printed. Holds one depth slot.
#   python -I venue_book_check.py <port> <client> <symbol> <seconds>
import socket
import struct
import sys
import time
from collections import Counter, defaultdict

ALLOWED = {71, 59, 1, 2, 10, 11}
VENUES = ["ARCA", "NYSE", "BEX"]
LETTERS = {"A": "AMEX", "B": "BX", "C": "NSX", "H": "PEARL", "I": "ISE", "J": "EDGA", "K": "EDGX", "L": "LTSE",
           "M": "CHX", "N": "NYSE", "P": "ARCA", "Q": "NASDAQ", "T": "NASDAQ", "S": "TXSE", "U": "MEMX", "V": "IEX",
           "X": "PSX", "Y": "BYX", "Z": "BZX"}
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


def contract(exchange):
    return [0, symbol, "STK", "", "0.0", "", "", exchange, "", "USD", "", ""]


sock.sendall(b"API\0" + frame("v100..176"))
read_frame()
send(71, 2, client, "")
time.sleep(0.3)
send(59, 1, 1)
NBBO_ID, DEPTH_ID = 8001, 8002
venue_ids = {8010 + i: v for i, v in enumerate(VENUES)}
send(1, 11, NBBO_ID, *contract("SMART"), False, "", False, False, "")
for rid, venue in venue_ids.items():
    send(1, 11, rid, *contract(venue), False, "", False, False, "")
send(10, 5, DEPTH_ID, *contract("SMART"), 50, True, "")

book = {0: [], 1: []}  # IBKR's sides: 0 ask, 1 bid
nbbo = {"bid": None, "ask": None}
exch = {"bid": "", "ask": ""}
tops = {v: {"bid": -1.0, "bidSize": 0.0, "ask": -1.0, "askSize": 0.0} for v in VENUES}
timed = Counter()
state = None
last = None
first_seen = {"nbbo": {"bid": {}, "ask": {}}, "ARCA": {"bid": {}, "ask": {}}}
lags = {"bid": [], "ask": []}
paired = {"bid": set(), "ask": set()}
missing_at_best = Counter()
venue_rows_ahead = Counter()
errors = []
counts = Counter()


def best(rows, bids):
    prices = [r[0] for r in rows if r[0] > 0 and r[1] > 0]
    return (max(prices) if bids else min(prices)) if prices else None


def merged_rows(side_name):
    side = 1 if side_name == "bid" else 0
    rows = list(book[side])
    have = {r[2] for r in rows}
    for v in VENUES:
        if v not in have:
            rows.append((tops[v][side_name], tops[v][side_name + "Size"], v))
    return rows


def compare(price, quote, bids):
    if price is None or quote is None or quote <= 0:
        return "n/a"
    if abs(price - quote) < 1e-9:
        return "same"
    return "worse" if (price < quote if bids else price > quote) else "better"


def note_first(source, side, price, now):
    seen = first_seen[source][side]
    for p in [p for p, t in seen.items() if now - t > 5]:
        del seen[p]
    seen.setdefault(price, now)
    other = first_seen["ARCA" if source == "nbbo" else "nbbo"][side]
    if price in other and price not in paired[side]:
        t_nbbo = seen[price] if source == "nbbo" else other[price]
        t_arca = seen[price] if source == "ARCA" else other[price]
        if abs(t_arca - t_nbbo) < 2:
            lags[side].append(t_arca - t_nbbo)
            paired[side].add(price)
    if len(paired[side]) > 200:
        paired[side].clear()


def sample(now):
    global state, last
    if nbbo["bid"] is None or nbbo["ask"] is None or not book[0] or not book[1]:
        return
    if last is not None:
        span = now - last
        timed["all"] += span
        for key in state:
            timed[key] += span
        if "merged bid worse" in state:
            for letter in exch["bid"]:
                missing_at_best["bid " + LETTERS.get(letter, letter)] += span
        if "merged ask worse" in state:
            for letter in exch["ask"]:
                missing_at_best["ask " + LETTERS.get(letter, letter)] += span
    last = now
    keys = []
    for side_name, bids in (("bid", True), ("ask", False)):
        side = 1 if bids else 0
        alone = compare(best(book[side], bids), nbbo[side_name], bids)
        merged_best = best(merged_rows(side_name), bids)
        merged = compare(merged_best, nbbo[side_name], bids)
        keys.append(f"book {side_name} {alone}")
        keys.append(f"merged {side_name} {merged}")
        if merged == "better" and alone != "better":
            for v in VENUES:
                if tops[v][side_name] > 0 and abs(tops[v][side_name] - merged_best) < 1e-9:
                    venue_rows_ahead[f"{side_name} {v}"] += 1
    state = tuple(keys)


sock.settimeout(0.5)
stop_at = time.time() + seconds
try:
    while time.time() < stop_at:
        try:
            f = read_frame()
        except (TimeoutError, socket.timeout):
            continue
        now = time.time()
        kind = f[0]
        if kind == "1":
            rid, tick, price = int(f[2]), int(f[3]), float(f[4])
            side = {1: "bid", 2: "ask"}.get(tick)
            if side is None:
                continue
            size = float(f[5]) if f[5] not in ("", "-1") else 0.0
            if rid == NBBO_ID:
                nbbo[side] = price
                counts["nbbo"] += 1
                if price > 0:
                    note_first("nbbo", side, price, now)
            elif rid in venue_ids:
                venue = venue_ids[rid]
                tops[venue][side] = price
                tops[venue][side + "Size"] = size
                counts[venue] += 1
                if venue == "ARCA" and price > 0:
                    note_first("ARCA", side, price, now)
            sample(now)
        elif kind == "2":
            rid, tick = int(f[2]), int(f[3])
            if rid in venue_ids and tick in (0, 3):
                tops[venue_ids[rid]]["bidSize" if tick == 0 else "askSize"] = float(f[4])
                sample(now)
        elif kind == "46":
            rid, tick = int(f[2]), int(f[3])
            if rid == NBBO_ID and tick in (32, 33):
                exch["bid" if tick == 32 else "ask"] = f[4]
        elif kind == "13" and int(f[2]) == DEPTH_ID:
            position, venue, operation, side, price, size = int(f[3]), f[4], int(f[5]), int(f[6]), float(f[7]), float(f[8])
            rows = book[side]
            if operation == 0 and position <= len(rows):
                rows.insert(position, (price, size, venue))
            elif operation == 1 and position < len(rows):
                rows[position] = (price, size, venue)
            elif operation == 2 and position < len(rows):
                rows.pop(position)
            counts["depth"] += 1
            sample(now)
        elif kind == "4":
            code = int(f[3])
            if code not in (2104, 2106, 2158, 2119, 2108, 2152):
                errors.append(f"{f[2]} {code}: {f[4][:160]}")
except EOFError:
    errors.append("socket closed")
send(2, 2, NBBO_ID)
for rid in venue_ids:
    send(2, 2, rid)
send(11, 1, DEPTH_ID, True)
time.sleep(0.3)
sock.close()

span = timed["all"] or 1
print(f"{symbol}, {seconds:.0f} s: depth row changes {counts['depth']}, NBBO ticks {counts['nbbo']}, venue ticks " +
      ", ".join(f"{v} {counts[v]}" for v in VENUES))
for line in errors[:5]:
    print(f"  error {line}")
for side_name in ("bid", "ask"):
    alone = {k: timed[f"book {side_name} {k}"] / span for k in ("same", "worse", "better")}
    merged = {k: timed[f"merged {side_name} {k}"] / span for k in ("same", "worse", "better")}
    print(f"  {side_name}, by time vs the NBBO: book alone same {alone['same']:.1%} worse {alone['worse']:.1%} better {alone['better']:.1%}"
          f" | with venue rows same {merged['same']:.1%} worse {merged['worse']:.1%} better {merged['better']:.1%}")
for side_name in ("bid", "ask"):
    gaps = sorted(lags[side_name])
    if gaps:
        later = sum(1 for g in gaps if g > 0.001)
        print(f"  ARCA's own {side_name} reaching a price the NBBO reached: {len(gaps)} prices, median {gaps[len(gaps) // 2] * 1000:+.0f} ms,"
              f" p10 {gaps[len(gaps) // 10] * 1000:+.0f} ms, p90 {gaps[len(gaps) * 9 // 10] * 1000:+.0f} ms; ARCA later in {later}")
print(f"  while the book with venue rows was worse than the NBBO, venues IBKR named at the NBBO (s): " +
      str({k: round(v, 1) for k, v in missing_at_best.most_common(8)}))
print(f"  samples where a venue row put the book ahead of the NBBO: {dict(venue_rows_ahead)}")
print(f"  at the end: NBBO {nbbo['bid']} / {nbbo['ask']}; venues " + ", ".join(f"{v} {tops[v]['bid']} x {tops[v]['bidSize']:.0f} / {tops[v]['ask']} x {tops[v]['askSize']:.0f}" for v in VENUES))

# Which venues' quotes does this IBKR account get, and how? For each symbol, asks four ways:
#   SMART quote: the NBBO, plus IBKR's strings naming the venues at the best bid and ask (ticks 32 and 33).
#   Directed quotes: reqMktData with the exchange set to ARCA, NYSE, BEX (Nasdaq BX) and PEARL (a control
#   venue that the smart book does carry).
#   ARCA's own book: reqMktDepth with exchange ARCA, not smart.
# Market data only (71, 59, 1, 2, 10, 11), and the account message is never printed. One depth slot is held for
# the length of the run, so keep it short.
#   python -I venue_quotes.py <port> <client> <seconds> <symbol> [symbol ...]
import socket
import struct
import sys
import time
from collections import Counter, defaultdict

ALLOWED = {71, 59, 1, 2, 10, 11}
port, client, seconds = int(sys.argv[1]), int(sys.argv[2]), float(sys.argv[3])
symbols = sys.argv[4:]
# Exchange letters in ticks 32/33 are the SIP's participant codes.
LETTERS = {"A": "AMEX", "B": "BX", "C": "NSX", "H": "PEARL", "I": "ISE", "J": "EDGA", "K": "EDGX", "L": "LTSE",
           "M": "CHX", "N": "NYSE", "P": "ARCA", "Q": "NASDAQ", "T": "NASDAQ", "S": "TXSE", "U": "MEMX", "V": "IEX",
           "X": "PSX",
           "Y": "BYX", "Z": "BZX", "D": "FINRA"}
DIRECTED = ["ARCA", "NYSE", "BEX", "PEARL"]

sock = socket.create_connection(("127.0.0.1", port), timeout=10)


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


def contract(symbol, exchange):
    return [0, symbol, "STK", "", "0.0", "", "", exchange, "", "USD", "", ""]


sock.sendall(b"API\0" + frame("v100..176"))
read_frame()
send(71, 2, client, "")
time.sleep(0.3)
send(59, 1, 1)

requests = {}  # id -> (symbol, kind)
next_id = 7100
for symbol in symbols:
    requests[next_id] = (symbol, "SMART quote")
    send(1, 11, next_id, *contract(symbol, "SMART"), False, "", False, False, "")
    next_id += 1
    for venue in DIRECTED:
        requests[next_id] = (symbol, f"{venue} quote")
        send(1, 11, next_id, *contract(symbol, venue), False, "", False, False, "")
        next_id += 1
depth_ids = []
for symbol in symbols[:1]:
    requests[next_id] = (symbol, "ARCA book")
    send(10, 5, next_id, *contract(symbol, "ARCA"), 10, False, "")
    depth_ids.append(next_id)
    next_id += 1

quotes = defaultdict(dict)  # id -> latest bid/ask/sizes
ticks = Counter()
exch_strings = defaultdict(Counter)  # (id, "bid"/"ask") -> string counts
letter_seen = defaultdict(Counter)
errors = defaultdict(list)
other = []
book = defaultdict(lambda: {0: [], 1: []})
book_updates = Counter()
sock.settimeout(0.5)
stop_at = time.time() + seconds
try:
    while time.time() < stop_at:
        try:
            f = read_frame()
        except (TimeoutError, socket.timeout):
            continue
        kind = f[0]
        if kind == "1":
            rid, tick, price = int(f[2]), int(f[3]), float(f[4])
            if rid in requests:
                name = {1: "bid", 2: "ask", 4: "last", 6: "high", 7: "low", 9: "close", 14: "open"}.get(tick, f"tick{tick}")
                quotes[rid][name] = price
                ticks[rid] += 1
        elif kind == "2":
            rid, tick = int(f[2]), int(f[3])
            if rid in requests:
                name = {0: "bidSize", 3: "askSize", 5: "lastSize", 8: "volume"}.get(tick, f"size{tick}")
                quotes[rid][name] = f[4]
        elif kind == "46":
            rid, tick, value = int(f[2]), int(f[3]), f[4]
            if rid in requests and tick in (32, 33):
                side = "bid" if tick == 32 else "ask"
                exch_strings[(rid, side)][value] += 1
                for letter in value:
                    letter_seen[(rid, side)][LETTERS.get(letter, letter)] += 1
        elif kind in ("12", "13"):
            rid = int(f[2])
            if kind == "12":
                position, operation, side, price, size, venue = int(f[3]), int(f[4]), int(f[5]), float(f[6]), f[7], "ARCA"
            else:
                position, venue, operation, side, price, size = int(f[3]), f[4], int(f[5]), int(f[6]), float(f[7]), f[8]
            rows = book[rid][side]
            if operation == 0 and position <= len(rows):
                rows.insert(position, (price, size, venue))
            elif operation == 1 and position < len(rows):
                rows[position] = (price, size, venue)
            elif operation == 2 and position < len(rows):
                rows.pop(position)
            book_updates[rid] += 1
        elif kind == "4":
            rid, code = int(f[2]), int(f[3])
            if code not in (2104, 2106, 2158, 2119, 2108):
                errors[rid].append(f"{code}: {f[4][:230]}")
        elif kind not in ("81", "58", "45", "50", "15", "9"):  # 15 is the account list: never printed
            if len(other) < 5:
                other.append(f[:4])
except EOFError:
    errors[-1].append("socket closed")
for rid, (symbol, what) in requests.items():
    if rid in depth_ids:
        send(11, 1, rid, False)
    else:
        send(2, 2, rid)
time.sleep(0.3)
sock.close()

print(f"{seconds:.0f} s run")
for rid, (symbol, what) in requests.items():
    q = quotes.get(rid, {})
    shown = {k: q[k] for k in ("bid", "bidSize", "ask", "askSize", "last") if k in q}
    print(f"{symbol} {what}: {ticks[rid]} price ticks, latest {shown}")
    for line in errors.get(rid, [])[:3]:
        print(f"    error {line}")
    for side in ("bid", "ask"):
        if (rid, side) in exch_strings:
            print(f"    venues at the best {side}, how often each was named: {dict(letter_seen[(rid, side)].most_common())}")
            print(f"      most common strings: {dict(exch_strings[(rid, side)].most_common(5))}")
    if rid in depth_ids:
        print(f"    book updates {book_updates[rid]}; bids {book[rid][1][:3]}; asks {book[rid][0][:3]}")
for line in errors.get(-1, []):
    print(f"error (connection) {line}")
if other:
    print(f"other messages seen: {other}")

# Does IBKR send an indicative price while a stock is halted or paused? Asks for the SMART quote and
# Nasdaq's own quote with generic tick 225 (auction values: tick 34 auction volume, 35 auction price,
# 36 auction imbalance, 61 regulatory imbalance), and prints every one as it comes, with the halt flag.
# Market data only (71, 59, 1, 2), and the account message is never printed.
#   python -I auction_ticks.py <port> <client> <seconds> <symbol> [symbol ...]
import socket
import struct
import sys
import time
from collections import Counter

ALLOWED = {71, 59, 1, 2}
port, client, seconds = int(sys.argv[1]), int(sys.argv[2]), float(sys.argv[3])
symbols = sys.argv[4:]
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

requests = {}
next_id = 7300
for symbol in symbols:
    for exchange in ("SMART", "ISLAND"):
        requests[next_id] = f"{symbol} {exchange}"
        send(1, 11, next_id, *contract(symbol, exchange), False, "225", False, False, "")
        next_id += 1

PRICES = {1: "bid", 2: "ask", 4: "last", 35: "AUCTION PRICE"}
SIZES = {0: "bidSize", 3: "askSize", 5: "lastSize", 34: "AUCTION VOLUME", 36: "AUCTION IMBALANCE", 61: "REGULATORY IMBALANCE"}
seen = Counter()
start = time.time()
sock.settimeout(0.5)


def stamp():
    return time.strftime("%H:%M:%S", time.localtime()) + f".{int((time.time() % 1) * 1000):03d}"


try:
    while time.time() - start < seconds:
        try:
            f = read_frame()
        except (TimeoutError, socket.timeout):
            continue
        kind = f[0]
        if kind == "1" and int(f[2]) in requests:
            name = PRICES.get(int(f[3]), f"price{f[3]}")
            seen[(int(f[2]), name)] += 1
            if int(f[3]) in (35, 1, 2) or seen[(int(f[2]), name)] <= 2:
                print(stamp(), requests[int(f[2])], name, f[4])
        elif kind == "2" and int(f[2]) in requests:
            name = SIZES.get(int(f[3]), f"size{f[3]}")
            seen[(int(f[2]), name)] += 1
            if int(f[3]) in (34, 36, 61) or seen[(int(f[2]), name)] <= 2:
                print(stamp(), requests[int(f[2])], name, f[4])
        elif kind == "45" and int(f[2]) in requests:
            print(stamp(), requests[int(f[2])], {"49": "HALTED", "88": "shortable"}.get(f[3], f"generic{f[3]}"), f[4])
        elif kind == "4":
            code = int(f[3])
            if code not in (2104, 2106, 2158, 2119, 2108):
                print(stamp(), "error", requests.get(int(f[2]), f[2]), code, f[4][:200])
except EOFError:
    print("socket closed")
for rid in requests:
    send(2, 2, rid)
time.sleep(0.3)
sock.close()
print("counts:", {f"{requests[r]} {n}": c for (r, n), c in sorted(seen.items())})

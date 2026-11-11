# Is there a venue at the NBBO whose quote we can't get at all? IBKR names the venues at the best bid and ask by
# their exchange letter (SMART quote, ticks 32 and 33). This asks every lit exchange IBKR knows for its own quote
# (reqMktData with the exchange set) and, each time IBKR names a letter, notes which venues' own quotes sit at that
# price. A letter that never lines up with any venue's quote is a venue we have nothing from.
# Market data only (71, 59, 1, 2); the account message is never printed. Uses about 20 market data lines, no depth.
#   python -I venue_letters.py <port> <client> <seconds> <symbol>
import socket
import struct
import sys
import time
from collections import Counter, defaultdict

ALLOWED = {71, 59, 1, 2}
port, client, seconds, symbol = int(sys.argv[1]), int(sys.argv[2]), float(sys.argv[3]), sys.argv[4]
# IBKR's names for every day-session venue in its 2152 lists, plus the three the smart book leaves out.
VENUES = ["NASDAQ", "BATS", "IEX", "BYX", "AMEX", "PEARL", "T24X", "MEMX", "EDGEA", "TXSE", "CHX", "NYSENAT", "PSX",
          "LTSE", "ISE", "DRCTEDGE", "ARCA", "NYSE", "BEX"]
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


def contract(exchange):
    return [0, symbol, "STK", "", "0.0", "", "", exchange, "", "USD", "", ""]


sock.sendall(b"API\0" + frame("v100..176"))
read_frame()
send(71, 2, client, "")
time.sleep(0.3)
send(59, 1, 1)
NBBO_ID = 9500
ids = {9510 + i: v for i, v in enumerate(VENUES)}
send(1, 11, NBBO_ID, *contract("SMART"), False, "", False, False, "")
for rid, venue in ids.items():
    send(1, 11, rid, *contract(venue), False, "", False, False, "")

nbbo = {"bid": None, "ask": None}
own = {v: {"bid": None, "ask": None} for v in VENUES}
quoted = Counter()  # venue -> ticks with a real price
named = Counter()  # letter -> times named
matched = defaultdict(Counter)  # letter -> venue -> times its own quote sat at that price
errors = {}
pending = []  # (side, string) waiting a moment for the prices that come with it


judged = Counter()  # "all", and venue -> times its own quote was at the NBBO when any letter was named


def judge(side, letters):
    price = nbbo[side]
    if price is None or price <= 0:
        return
    at = [v for v in VENUES if own[v][side] is not None and abs(own[v][side] - price) < 1e-9]
    judged["all"] += 1
    for v in at:
        judged[v] += 1
    for letter in set(letters):
        named[letter] += 1
        for v in at:
            matched[letter][v] += 1


sock.settimeout(0.2)
stop_at = time.time() + seconds
try:
    while time.time() < stop_at:
        try:
            f = read_frame()
        except (TimeoutError, socket.timeout):
            f = None
        now = time.time()
        # Judge each named string 150 ms after it came, so the prices sent with it have arrived.
        while pending and now - pending[0][0] > 0.15:
            _, side, letters = pending.pop(0)
            judge(side, letters)
        if f is None:
            continue
        kind = f[0]
        if kind == "1":
            rid, tick, price = int(f[2]), int(f[3]), float(f[4])
            side = {1: "bid", 2: "ask"}.get(tick)
            if side is None:
                continue
            if rid == NBBO_ID:
                nbbo[side] = price
            elif rid in ids:
                own[ids[rid]][side] = price if price > 0 else None
                if price > 0:
                    quoted[ids[rid]] += 1
        elif kind == "46":
            rid, tick = int(f[2]), int(f[3])
            if rid == NBBO_ID and tick in (32, 33) and f[4]:
                pending.append((now, "bid" if tick == 32 else "ask", f[4]))
        elif kind == "4":
            rid, code = int(f[2]), int(f[3])
            if rid in ids and code not in (2104, 2106, 2158, 2119, 2108):
                errors[ids[rid]] = f"{code}: {f[4][:120]}"
except EOFError:
    print("socket closed")
for rid in [NBBO_ID, *ids]:
    send(2, 2, rid)
time.sleep(0.3)
sock.close()

print(f"{symbol}, {seconds:.0f} s, at {time.strftime('%H:%M:%S')}")
print("  venues with a quote: " + ", ".join(f"{v} {quoted[v]}" for v in VENUES if quoted[v]))
print("  venues with no quote: " + (", ".join(v for v in VENUES if not quoted[v] and v not in errors) or "none"))
for v, e in errors.items():
    print(f"  {v} refused: {e}")
# The SIP's participant letters, as IBKR names those exchanges. Nasdaq is Q on Nasdaq-listed stocks and T on the
# rest. Any letter not here is a venue this script doesn't know, so it's the one to look into.
OWN = {"A": "AMEX", "B": "BEX", "C": "NYSENAT", "H": "PEARL", "I": "ISE", "J": "EDGEA", "K": "DRCTEDGE", "L": "LTSE",
       "M": "CHX", "N": "NYSE", "P": "ARCA", "Q": "NASDAQ", "T": "NASDAQ", "U": "MEMX", "V": "IEX", "X": "PSX",
       "Y": "BYX", "Z": "BATS"}
print("  each letter IBKR named at the NBBO: how often that venue's own quote was at the price (against how often")
print("  it was there when any letter was named):")
for letter, count in named.most_common():
    venue = OWN.get(letter)
    if venue:
        usual = judged[venue] / judged["all"] if judged["all"] else 0
        print(f"    {letter} = {venue}: named {count} times, its quote there {matched[letter][venue] / count:.0%} (usually {usual:.0%})")
        continue

    # Unknown letter: the venue that sits at the price far more often when it's named than otherwise.
    def lift(v):
        usual = judged[v] / judged["all"] if judged["all"] else 0
        return (matched[letter][v] / count) / usual if usual else 0
    ranked = sorted(matched[letter], key=lift, reverse=True)[:2]
    shown = ", ".join(f"{v} {matched[letter][v] / count:.0%} (usually {judged[v] / judged['all']:.0%})" for v in ranked)
    print(f"    {letter} = UNKNOWN: named {count} times; " + (shown or "no venue's quote was at the price"))

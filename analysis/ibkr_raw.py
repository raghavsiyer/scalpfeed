# A scriptable raw client for IB Gateway, for finding out what Gateway really does: every request and
# every reply is timestamped, nothing is retried or hidden behind client logic. Market data only: Send()
# refuses anything outside the same whitelist as IbkrClient, which has no order messages. The account
# message is never printed.
#
#   python -I ibkr_raw.py --port 4001 --client 91 --script "depth CIIT; wait 60; canceldepth CIIT; wait 2"
#
# Steps, separated by semicolons:
#   depth SYM [plain]          reqMktDepth under a new id. plain = isSmartDepth 0, on SMART
#   canceldepth SYM [plain]    cancels the newest depth id for SYM. plain sends isSmartDepth 0
#   cancelgranted              cancels every depth id that wasn't refused
#   quote SYM [EXCH]           reqMktData
#   tape SYM [EXCH] [KIND]     reqTickByTickData, AllLast by default
#   canceltape SYM
#   wait SECONDS
#   retrybook SYM EVERY MAX    asks again under a new id every EVERY seconds until a depth row arrives
#   exit                       closes the socket with no cancels, like a client dying
import argparse
import socket
import struct
import sys
import threading
import time
from collections import Counter, defaultdict

ALLOWED = {71, 59, 1, 2, 10, 11, 97, 98}
QUIET_CODES = {2104, 2106, 2158, 2103, 2105, 2108, 2157, 2119}

parser = argparse.ArgumentParser()
parser.add_argument("--port", type=int, default=4001)
parser.add_argument("--client", type=int, default=91)
parser.add_argument("--script", required=True)
parser.add_argument("--rows", type=int, default=50)
parser.add_argument("--quiet-rows", action="store_true", help="print only the first row per id")
parser.add_argument("--print-ticks", type=int, default=40, help="how many tape prints to echo per id")
parser.add_argument("--capture", default="", help="write every tape print to this TSV: arrival ms, symbol, exchange, seconds, price, size, venue, conditions")
args = parser.parse_args()
capture = open(args.capture, "w", encoding="utf-8") if args.capture else None

start = time.perf_counter()
state_lock = threading.Lock()
next_id = [args.client * 1000]
ids = {}  # id -> (kind, symbol, exchange)
newest = {}  # (kind, symbol) -> id
rows_by_id = Counter()
first_row_at = {}
first_row_text = {}
refused_at = {}
errors = Counter()
prints_by_id = defaultdict(list)
quotes_by_id = defaultdict(lambda: {"bid": None, "ask": None, "n": 0})
sock_closed = threading.Event()


def stamp():
    wall = time.strftime("%H:%M:%S") + (".%03d" % int((time.time() % 1) * 1000))
    return f"{wall} +{time.perf_counter() - start:7.3f}"


def say(text):
    print(f"{stamp()} [c{args.client}] {text}", flush=True)


sock = socket.create_connection(("127.0.0.1", args.port), timeout=10)
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
    length = struct.unpack(">I", read_exact(4))[0]
    return read_exact(length).decode("utf-8", "replace").split("\0")


def contract(symbol, exchange="SMART"):
    return [0, symbol, "STK", "", "0.0", "", "", exchange, "", "USD", "", ""]


def new_id(kind, symbol, exchange):
    with state_lock:
        next_id[0] += 1
        rid = next_id[0]
        ids[rid] = (kind, symbol, exchange)
        newest[(kind, symbol)] = rid
    return rid


def receive():
    try:
        while True:
            f = read_frame()
            now = time.perf_counter() - start
            kind = f[0]
            if kind == "4":
                rid, code, message = int(f[2]), int(f[3]), f[4]
                errors[code] += 1
                if code in QUIET_CODES:
                    continue
                label = ids.get(rid, ("", "", ""))
                if code == 309:
                    refused_at[rid] = now
                say(f"ERR id={rid} {label[1]} code={code}: {message[:150]}")
            elif kind in ("12", "13"):
                rid = int(f[2])
                rows_by_id[rid] += 1
                if rid not in first_row_at:
                    first_row_at[rid] = now
                    label = ids.get(rid, ("", "?", ""))
                    say(f"FIRST ROW id={rid} {label[1]} frame={'|'.join(f[3:9])}")
                elif not args.quiet_rows and rows_by_id[rid] <= 3:
                    say(f"row id={rid} {'|'.join(f[3:9])}")
            elif kind == "99":
                rid = int(f[1])
                if f[2] in ("1", "2"):
                    record = (float(f[3]), f[4], f[5], f[7], f[8])
                    prints_by_id[rid].append(record)
                    if capture:
                        label = ids.get(rid, ("", "?", ""))
                        capture.write(f"{time.time() * 1000:.0f}\t{label[1]}\t{label[2]}\t{f[3]}\t{f[4]}\t{f[5]}\t{f[7]}\t{f[8]}\n")
                    if len(prints_by_id[rid]) <= args.print_ticks:
                        label = ids.get(rid, ("", "?", ""))
                        say(f"PRINT id={rid} {label[1]}@{label[2]} t={f[3]} px={f[4]} sz={f[5]} venue={f[7]} cond={f[8]!r}")
            elif kind == "1":
                rid, tick, price = int(f[2]), int(f[3]), f[4]
                quote = quotes_by_id[rid]
                if tick == 1:
                    quote["bid"] = price
                elif tick == 2:
                    quote["ask"] = price
                if tick in (1, 2):
                    quote["n"] += 1
    except (EOFError, OSError):
        sock_closed.set()


sock.sendall(b"API\0" + frame("v100..176"))
hello = read_frame()
say(f"server version {hello[0]}")
send(71, 2, args.client, "")
threading.Thread(target=receive, daemon=True).start()
time.sleep(0.3)
send(59, 1, 1)


def request_depth(symbol, smart=True):
    rid = new_id("depth", symbol, "SMART")
    send(10, 5, rid, *contract(symbol), args.rows, smart, "")
    say(f"-> reqMktDepth id={rid} {symbol} smart={smart}")
    return rid


for raw_step in args.script.split(";"):
    parts = raw_step.split()
    if not parts:
        continue
    step = parts[0]
    if step == "wait":
        time.sleep(float(parts[1]))
    elif step == "depth":
        request_depth(parts[1], len(parts) < 3 or parts[2] != "plain")
    elif step == "canceldepth":
        rid = newest[("depth", parts[1])]
        smart = len(parts) < 3 or parts[2] != "plain"
        send(11, 1, rid, smart)
        say(f"-> cancelMktDepth id={rid} {parts[1]} smart={smart}")
    elif step == "cancelgranted":
        with state_lock:
            live = [r for r, (k, _, _) in ids.items() if k == "depth" and r not in refused_at]
        for rid in live:
            send(11, 1, rid, True)
        say(f"-> cancelled {len(live)} granted depth ids")
    elif step == "quote":
        exchange = parts[2] if len(parts) > 2 else "SMART"
        rid = new_id("quote", parts[1], exchange)
        send(1, 11, rid, *contract(parts[1], exchange), False, "", False, False, "")
        say(f"-> reqMktData id={rid} {parts[1]}@{exchange}")
    elif step == "tape":
        exchange = parts[2] if len(parts) > 2 else "SMART"
        tape_kind = parts[3] if len(parts) > 3 else "AllLast"
        rid = new_id("tape", parts[1], exchange)
        send(97, rid, *contract(parts[1], exchange), tape_kind, 0, False)
        say(f"-> reqTickByTick id={rid} {parts[1]}@{exchange} {tape_kind}")
    elif step == "canceltape":
        rid = newest[("tape", parts[1])]
        send(98, rid)
        say(f"-> cancelTickByTick id={rid} {parts[1]}")
    elif step == "retrybook":
        symbol, every, limit = parts[1], float(parts[2]), float(parts[3])
        begun = time.perf_counter()
        attempts = 0
        got = None
        while time.perf_counter() - begun < limit and got is None:
            rid = request_depth(symbol)
            attempts += 1
            deadline = time.perf_counter() + every
            while time.perf_counter() < deadline and got is None:
                if first_row_at.get(rid) is not None:
                    got = rid
                time.sleep(0.01)
        say(f"retrybook {symbol}: {'book' if got else 'NO book'} after {time.perf_counter() - begun:.3f} s, {attempts} attempts")
    elif step == "exit":
        say("closing the socket with no cancels")
        break
    else:
        raise SystemExit(f"unknown step {step}")

time.sleep(0.2)
for rid, (kind, symbol, exchange) in sorted(ids.items()):
    if kind == "depth":
        verdict = "refused" if rid in refused_at else f"rows={rows_by_id[rid]}" if rows_by_id[rid] else "silent (no refusal, no rows)"
        print(f"  depth id={rid} {symbol}: {verdict}")
    elif kind == "tape":
        prints = prints_by_id[rid]
        venues = Counter(p[3] for p in prints)
        conditions = Counter(p[4] for p in prints)
        shares = Counter()
        for p in prints:
            shares[p[3]] += float(p[2])
        print(f"  tape id={rid} {symbol}@{exchange}: {len(prints)} prints, venues {dict(venues)}, shares {dict(shares)}, conditions {dict(conditions)}")
    elif kind == "quote":
        quote = quotes_by_id[rid]
        print(f"  quote id={rid} {symbol}@{exchange}: {quote['bid']}/{quote['ask']} updates={quote['n']}")
sock.close()
if capture:
    capture.close()
sys.stdout.flush()

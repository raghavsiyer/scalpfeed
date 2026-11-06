# Asks Gateway for historical TRADES bars and saves them, timing each request. Kept apart from ibkr_raw.py
# and the plugin on purpose: it can send only the login message, reqHistoricalData and its cancel, so
# IbkrClient.AllowedRequests doesn't change by testing this. It reads data only, and the account message
# is never printed. Requests run one at a time and spaced, to stay inside IBKR's pacing limits.
#
#   python -I ibkr_history.py --out bars.tsv --requests "RCT|14400 S|1 min; RCT|1800 S|10 secs; RCT|5 D|1 day"
#
# Saved columns: symbol, bar size, bar time (epoch seconds, or yyyymmdd for days), open, high, low, close,
# volume, wap, trade count.
import argparse
import socket
import struct
import time

ALLOWED = {71, 20, 25}
QUIET = {2104, 2106, 2158, 2103, 2105, 2108, 2157, 2119}

parser = argparse.ArgumentParser()
parser.add_argument("--port", type=int, default=4001)
parser.add_argument("--client", type=int, default=96)
parser.add_argument("--out", required=True)
parser.add_argument("--requests", required=True)
parser.add_argument("--gap", type=float, default=2.5, help="seconds between requests")
parser.add_argument("--timeout", type=float, default=60, help="seconds before a request is cancelled")
args = parser.parse_args()

sock = socket.create_connection(("127.0.0.1", args.port), timeout=10)
sock.setsockopt(socket.IPPROTO_TCP, socket.TCP_NODELAY, 1)


def frame(text):
    body = text.encode("ascii")
    return struct.pack(">I", len(body)) + body


def send(*fields):
    if int(fields[0]) not in ALLOWED:
        raise SystemExit(f"message {fields[0]} is not allowed")
    sock.sendall(frame("".join(str(f) + "\0" for f in fields)))


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


sock.sendall(b"API\0" + frame("v100..176"))
server = int(read_frame()[0])
print(f"server version {server}, requests may be 71, 20 and 25 only")
send(71, 2, args.client, "")

# Up to the account message. Its contents are skipped, not read out.
sock.settimeout(10)
while read_frame()[0] != "15":
    pass

out = open(args.out, "w", encoding="utf-8")
request_id = args.client * 1000
for raw in args.requests.split(";"):
    if not raw.strip():
        continue
    fields = [p.strip() for p in raw.split("|")]
    symbol, duration, bar_size = fields[:3]
    end = fields[3] if len(fields) > 3 else ""  # Blank means now; otherwise yyyyMMdd-HH:mm:ss in UTC.
    request_id += 1
    begun = time.perf_counter()
    send(20, request_id, 0, symbol, "STK", "", "0.0", "", "", "SMART", "", "USD", "", "", 0, end, bar_size, duration, 0, "TRADES", 2, 0, "")
    sock.settimeout(1)
    answer = None
    while time.perf_counter() - begun < args.timeout:
        try:
            f = read_frame()
        except TimeoutError:
            continue
        if f[0] == "17" and int(f[1]) == request_id:
            # Before server version 196 the bars come after a start and an end date.
            at = 4 if server < 196 else 2
            count = int(f[at])
            at += 1
            total = 0.0
            for _ in range(count):
                date, o, h, l, c, v, wap, trades = f[at : at + 8]
                at += 8
                out.write("\t".join((symbol, bar_size, date, o, h, l, c, v, wap, trades)) + "\n")
                total += float(v)
            answer = f"{count} bars, volume {total:.0f}"
            break
        if f[0] == "4":
            rid, code, message = int(f[2]), int(f[3]), f[4]
            if code in QUIET or rid != request_id:
                continue
            answer = f"error {code}: {message[:160]}"
            break
    if answer is None:
        send(25, 1, request_id)
        answer = "no answer, cancelled"
    print(f"{symbol} {duration} {bar_size} end='{end}': {answer} in {(time.perf_counter() - begun) * 1000:.0f} ms", flush=True)
    out.flush()
    time.sleep(args.gap)
out.close()
sock.close()

# How long Quantower's windows take to answer, from outside and with no effect on it: a do-nothing message
# (WM_NULL) every 10 ms to one window per display thread, timed. A busy or frozen display thread answers late.
# With a tape recording from ibkr_raw.py, also checks whether the stalls follow IBKR's batches.
#   python -I ui_stall.py <process id> <seconds> [tape.tsv]
import ctypes
import sys
import threading
import time
from collections import Counter
from ctypes import wintypes

pid = int(sys.argv[1])
seconds = float(sys.argv[2])
tape_path = sys.argv[3] if len(sys.argv) > 3 else None

user32 = ctypes.WinDLL("user32", use_last_error=True)
WNDENUMPROC = ctypes.WINFUNCTYPE(wintypes.BOOL, wintypes.HWND, wintypes.LPARAM)
user32.SendMessageTimeoutW.argtypes = [wintypes.HWND, wintypes.UINT, ctypes.c_size_t, ctypes.c_ssize_t, wintypes.UINT, wintypes.UINT, ctypes.POINTER(ctypes.c_size_t)]
user32.SendMessageTimeoutW.restype = ctypes.c_ssize_t
user32.GetWindowThreadProcessId.argtypes = [wintypes.HWND, ctypes.POINTER(wintypes.DWORD)]
user32.GetWindowThreadProcessId.restype = wintypes.DWORD
user32.IsWindowVisible.argtypes = [wintypes.HWND]
user32.EnumWindows.argtypes = [WNDENUMPROC, wintypes.LPARAM]
user32.GetWindowTextLengthW.argtypes = [wintypes.HWND]

windows = {}  # display thread -> first visible window on it


def visit(hwnd, _):
    owner = wintypes.DWORD()
    thread = user32.GetWindowThreadProcessId(hwnd, ctypes.byref(owner))
    if owner.value == pid and user32.IsWindowVisible(hwnd) and thread not in windows:
        windows[thread] = hwnd
    return True


user32.EnumWindows(WNDENUMPROC(visit), 0)
print(f"visible top-level windows on {len(windows)} display thread(s) of process {pid}")
samples = {thread: [] for thread in windows}
stop_at = time.time() + seconds


def probe(thread, hwnd):
    result = ctypes.c_size_t()
    rows = samples[thread]
    while time.time() < stop_at:
        begun = time.time()
        clock = time.perf_counter()
        answered = user32.SendMessageTimeoutW(hwnd, 0, 0, 0, 0, 2000, ctypes.byref(result))
        rows.append((begun * 1000, (time.perf_counter() - clock) * 1000, bool(answered)))
        time.sleep(0.01)


threads = [threading.Thread(target=probe, args=item) for item in windows.items()]
for t in threads:
    t.start()
for t in threads:
    t.join()


def pct(values, p):
    return values[min(int(len(values) * p), len(values) - 1)] if values else float("nan")


batches = []
if tape_path:
    batches = sorted({float(line.split("\t")[0]) for line in open(tape_path, encoding="utf-8") if line.count("\t") == 7})

for thread, rows in samples.items():
    durations = sorted(r[1] for r in rows)
    bands = Counter()
    for d in durations:
        for limit in (10, 20, 50, 100, 200, 500):
            if d >= limit:
                bands[limit] += 1
    print(f"thread {thread}: {len(rows)} probes, median {pct(durations, 0.5):.2f} ms, p90 {pct(durations, 0.9):.2f}, p99 {pct(durations, 0.99):.2f}, max {durations[-1] if durations else 0:.1f}; unanswered {sum(1 for r in rows if not r[2])}")
    print(f"  answered late: {dict(sorted(bands.items()))} (of {len(rows)})")
    stalls = [r for r in rows if r[1] >= 50]
    if stalls:
        span = (rows[-1][0] - rows[0][0]) / 1000
        print(f"  stalls of 50 ms or more: {len(stalls)} in {span:.0f} s, {len(stalls) / span:.2f} a second, {sum(r[1] for r in stalls) / span / 10:.1f}% of the time")
        gaps = sorted(b[0] - a[0] for a, b in zip(stalls, stalls[1:]))
        if gaps:
            print(f"  time between them: median {pct(gaps, 0.5):.0f} ms, p10 {pct(gaps, 0.1):.0f}, p90 {pct(gaps, 0.9):.0f}")
    if stalls and batches:
        begin, end = rows[0][0], rows[-1][0]
        inside = [b for b in batches if begin <= b <= end]
        window = 150.0
        covered = 0.0
        previous = None
        for b in inside:
            covered += window if previous is None or b - previous >= window else b - previous
            previous = b
        chance = covered / (end - begin) if end > begin else 0
        import bisect

        follow = 0
        for r in stalls:
            k = bisect.bisect_right(inside, r[0]) - 1
            if k >= 0 and r[0] - inside[k] <= window:
                follow += 1
        print(f"  IBKR deliveries in that time: {len(inside)}, {len(inside) / ((end - begin) / 1000):.2f} a second. Stalls starting within {window:.0f} ms after one: {follow} of {len(stalls)} ({follow / len(stalls):.0%}); by chance {chance:.0%}")

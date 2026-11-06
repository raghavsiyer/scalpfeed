# Confirms a plugin build's contents by searching our own DLL's bytes for strings, as ASCII and as UTF-16 at
# both byte alignments, so it can't be missed by where a string happens to start. Only ever for ScalpFeedVendor.dll.
#   python -I dll_strings.py <ScalpFeedVendor.dll>
import hashlib
import os
import sys
from datetime import datetime

path = sys.argv[1]
data = open(path, "rb").read()
print(f"{path}: {len(data)} bytes, written {datetime.fromtimestamp(os.path.getmtime(path)):%Y-%m-%d %H:%M:%S}, sha256 {hashlib.sha256(data).hexdigest()[:16]}")


def count(term):
    text = term.encode("ascii")
    wide = term.encode("utf-16-le")
    even = odd = 0
    start = 0
    while (at := data.find(wide, start)) >= 0:
        if at % 2 == 0:
            even += 1
        else:
            odd += 1
        start = at + 1
    return data.count(text), even, odd


# Not "TESTHOOKS": it stays once in every build, as the argument of [Conditional("TESTHOOKS")] on the empty
# ThrowIfTestOutage. That attribute removes the calls at compile time, and the method's body is compiled out.
MUST_BE_ABSENT = ["test-drop", "test-block", "test-stale", "test-no-prints", "test-ibkr-block", "test-ibkr-halt", "test-ibkr-tape-mute", "test hook", "test outage", "testMute"]
MUST_BE_PRESENT = ["[ibkr] merge 60s", "[ibkr] bars ", "tape went quiet", "Windows refused the request to stay awake"]

bad = False
for term in MUST_BE_ABSENT:
    ascii_hits, even, odd = count(term)
    print(f"  absent?  {term:24} ascii {ascii_hits}, utf-16 even {even}, odd {odd}")
    bad = bad or ascii_hits + even + odd > 0
for term in MUST_BE_PRESENT:
    ascii_hits, even, odd = count(term)
    print(f"  present? {term:42} ascii {ascii_hits}, utf-16 even {even}, odd {odd}")
    bad = bad or ascii_hits + even + odd == 0
print("RESULT:", "PROBLEM" if bad else "no test strings, and this is the new build")
sys.exit(1 if bad else 0)

"""Which venues and condition codes does IBKR's AllLast actually send?

python -I ibkr_conditions.py <ibkr capture.tsv> [...]
"""
import sys
from collections import Counter, defaultdict

FAMILY = {"NASDAQ", "BEX", "PSX", "FINRA"}
venues = Counter()
conds = Counter()
cond_other = Counter()
tokens = Counter()
token_shares = Counter()
samples = {}
raw = []
far = defaultdict(list)

for path in sys.argv[1:]:
    for line in open(path, encoding="utf-8"):
        p = line.rstrip("\n").split("\t")
        if len(raw) < 3 and p[1] == "T":
            raw.append(p)
        if p[1] != "T":
            continue
        price, size, venue = float(p[3]), float(p[4]), p[5]
        cond = p[6] if len(p) > 6 else ""
        venues[venue] += 1
        conds[cond] += 1
        if venue not in FAMILY:
            cond_other[cond] += 1
            for t in cond.split() or ["(none)"]:
                tokens[t] += 1
                token_shares[t] += size
                samples.setdefault(t, (p[2], price, size, venue))

print("sample rows:", raw)
print("venues:", venues.most_common())
print("\nall condition strings:", conds.most_common(30))
print("\nvenues tastytrade lacks, by condition string:", cond_other.most_common(30))
print("\nby single code (venues tastytrade lacks):")
for t, n in tokens.most_common():
    print(f"  {t!r:10} {n:6} prints {token_shares[t]:10,.0f} shares  sample {samples[t]}")

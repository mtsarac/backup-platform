"""Summarise results.jsonl rows from line N+1 on (default: the start recorded in docs/test-environment/run-20261105/results-start.txt): python3 run-summary.py [start_line] [prefix]. The last row per scenario wins."""
import json, sys, pathlib
root = pathlib.Path(__file__).resolve().parents[2] / "docs/test-environment"
start = int(sys.argv[1]) if len(sys.argv) > 1 else int((root / "run-20261105/results-start.txt").read_text().split()[0])
prefix = sys.argv[2] if len(sys.argv) > 2 else ""
last = {}
for l in (root / "results.jsonl").read_text().splitlines()[start:]:
    d = json.loads(l)
    if d["scenario"].startswith(prefix): last[d["scenario"]] = d
for n, d in last.items():
    print(d["result"], n, "" if d["result"] == "PASS" else str({k: v for k, v in d.items() if k not in ("scenario", "result", "at")})[:300])
c = {}
for d in last.values(): c[d["result"]] = c.get(d["result"], 0) + 1
print(c)

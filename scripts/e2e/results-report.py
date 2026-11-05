"""Builds the e2e results report from results.jsonl and the triage file; nothing in the report is typed by hand.

Usage: python3 results-report.py [--results FILE] [--row-offset N] [--triage FILE] [--out FILE]
  --results     rows to read; default scripts/e2e/evidence/run-20261105/results.jsonl (rows 454-1040 of the lab's results.jsonl, see SHA256SUMS there)
  --row-offset  rows of the lab file that come before the first row of --results, so row numbers match it (default 453)
  --triage      JSON object scenario -> {class, issue, priority, evidence, supersededBy}; default scripts/e2e/triage-run-20261105.json
Reproduce: cd scripts/e2e/evidence/run-20261105 && sha256sum -c SHA256SUMS && cd ../../../.. && python3 scripts/e2e/results-report.py
The last row of a scenario is its result. Every scenario whose last row is not PASS needs a triage entry; the exit code is 1 when one is missing,
when a row has no string result, or when a triage entry contradicts the rows (stale, or "superseded" by a scenario that is not PASS).
The verdict is NO-GO while any product defect, undecided case or unclassified row is open, otherwise GO."""
import argparse, collections, json, pathlib, sys

ROOT = pathlib.Path(__file__).resolve().parents[2]
RESULTS = ROOT / "scripts/e2e/evidence/run-20261105/results.jsonl"
CLASSES = {"product-defect": "issue", "decision-pending": "issue", "test-defect": None, "environment": None, "accepted": None, "superseded": "supersededBy", "not-applicable": None}
OPEN = ("product-defect", "decision-pending")


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--results", default=str(RESULTS))
    ap.add_argument("--row-offset", type=int, default=453)
    ap.add_argument("--triage", default=str(ROOT / "scripts/e2e/triage-run-20261105.json"))
    ap.add_argument("--out")
    args = ap.parse_args()
    start = args.row_offset
    rows = [json.loads(line) for line in pathlib.Path(args.results).read_text().splitlines()]
    triage = json.loads(pathlib.Path(args.triage).read_text())

    last, history, invalid = {}, collections.defaultdict(list), []
    for number, row in enumerate(rows, start + 1):
        if not isinstance(row.get("result"), str):
            invalid.append((number, row.get("scenario"))); continue
        last[row["scenario"]] = (number, row); history[row["scenario"]].append(row["result"])
    counts = collections.Counter(row["result"] for _, row in last.values())
    invalid = [(n, s) for n, s in invalid if s not in last or last[s][0] < n]  # an unreadable row is harmless once a later valid row exists
    # "flaky": the product defect shows on some runs, so a PASS last row must not hide it.
    open_rows = {name: v for name, v in last.items() if v[1]["result"] != "PASS" or triage.get(name, {}).get("flaky")}

    problems, table, unclassified = [], collections.defaultdict(list), []
    for name, (number, row) in sorted(open_rows.items()):
        entry = triage.get(name)
        if entry is None: unclassified.append(name); continue
        kind = entry.get("class"); need = CLASSES.get(kind, "?")
        if kind not in CLASSES: problems.append(f"{name}: unknown class {kind!r}")
        elif need and not entry.get(need): problems.append(f"{name}: class {kind} needs '{need}'")
        elif not entry.get("evidence"): problems.append(f"{name}: no evidence")
        if kind == "superseded" and entry.get("supersededBy"):
            sup = [entry["supersededBy"]] if isinstance(entry["supersededBy"], str) else entry["supersededBy"]
            bad = [s for s in sup if (last.get(s) or (0, {}))[1].get("result") != "PASS"]
            if bad: problems.append(f"{name}: superseding scenario is not PASS: {', '.join(bad)}")
            entry = {**entry, "supersededBy": "<br>".join(f"`{s}`" for s in sup)}
        table[kind].append((name, number, row["result"], entry))
    stale = sorted(name for name in triage if name not in open_rows)
    problems += [f"stale triage entry (the scenario is now PASS or absent): {name}" for name in stale]
    problems += [f"row {n} has no string result: {s}" for n, s in invalid]
    problems += [f"{name}: no triage entry" for name in unclassified]
    verdict = "NO-GO" if any(table[k] for k in OPEN) or unclassified or invalid else "GO"

    out = [f"# E2E results report (generated from results.jsonl rows {start + 1}-{start + len(rows)})", "",
           f"**Verdict: {verdict}**", "",
           "| Result | Scenarios |", "|---|---|", *[f"| {k} | {v} |" for k, v in sorted(counts.items())], f"| total | {sum(counts.values())} |", ""]
    for kind in CLASSES:
        if not table[kind]: continue
        out += [f"## {kind} ({len(table[kind])})", "", "| Scenario | Row | Result | Issue / superseded by | Priority | Evidence |", "|---|---|---|---|---|---|"]
        for name, number, result, e in table[kind]:
            out.append(f"| `{name}` | {number} | {result} | {e.get('issue') or e.get('supersededBy') or ''} | {e.get('priority', '')} | {e.get('evidence', '').replace('|', '/')} |")
        out.append("")
    reruns = sorted((n, h) for n, h in history.items() if last[n][1]["result"] == "PASS" and "FAIL" in h)
    out += [f"## Failed first, passed on a later row ({len(reruns)})", "", "These count as PASS only because a later row of the same scenario passed; the failing rows stay in results.jsonl.", ""]
    out += [f"- `{n}`: {' > '.join(h)}" for n, h in reruns]
    if problems: out += ["", "## Problems", ""] + [f"- {p}" for p in problems]
    text = "\n".join(out) + "\n"
    if args.out: pathlib.Path(args.out).write_text(text)
    else: sys.stdout.write(text)
    sys.exit(1 if problems else 0)


if __name__ == "__main__":
    main()

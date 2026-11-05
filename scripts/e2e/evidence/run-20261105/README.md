Rows 454-1040 of the lab's `results.jsonl` (the 2026-11-05/06 run; earlier rows belong to the first matrix runs). Rows carry ids and statuses only; no credentials are written by the harness.
Verify and rebuild the report: `sha256sum -c SHA256SUMS` here, then `python3 scripts/e2e/results-report.py` from the repository root (exit 0, no "Problems" section).

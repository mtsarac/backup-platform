#!/usr/bin/env python3
"""Summarise (and optionally redact) dotnet-test TRX files for a GitHub Actions job.

usage: trx_report.py [--redact-env NAME]... [--require-passed] PATH...

PATH is a .trx file or a directory searched recursively. For every TRX the script prints a one-line
verdict per suite plus the failed tests with their error text, writes the same as Markdown to
$GITHUB_STEP_SUMMARY, and emits up to ten ::error annotations naming the failing tests.

--redact-env NAME   before reporting, replace the value of environment variable NAME with *** inside the
                    TRX files themselves, so the uploaded artifact cannot carry a disposable test password.
--require-passed    exit non-zero when a TRX has no passed test (a bad filter or an all-skipped suite
                    must not look green).
"""
import argparse
import collections
import html
import os
import sys
import xml.etree.ElementTree as ET
from pathlib import Path

NS = {"t": "http://microsoft.com/schemas/VisualStudio/TeamTest/2010"}
MAX_ERROR_CHARS = 1500
MAX_FAILURES_LISTED = 50
MAX_ANNOTATIONS = 10


def find_trx(paths):
    for raw in paths:
        path = Path(raw)
        if path.is_dir():
            yield from sorted(path.rglob("*.trx"))
        elif path.is_file():
            yield path


def redact(path, secrets):
    text = path.read_text(encoding="utf-8")
    redacted = text
    for secret in secrets:
        redacted = redacted.replace(secret, "***")
    if redacted != text:
        path.write_text(redacted, encoding="utf-8")


def first_text(node, xpath):
    found = node.find(xpath, NS)
    return (found.text or "").strip() if found is not None else ""


def read_suite(path):
    root = ET.parse(path).getroot()
    names = {
        d.get("id"): d.get("name")
        for d in root.findall("t:TestDefinitions/t:UnitTest", NS)
    }
    outcomes = collections.Counter()
    failures = []
    skips = collections.Counter()
    for result in root.findall("t:Results/t:UnitTestResult", NS):
        outcome = result.get("outcome", "Unknown")
        outcomes[outcome] += 1
        name = result.get("testName") or names.get(result.get("testId"), "?")
        if outcome in ("Failed", "Timeout", "Aborted", "Error"):
            failures.append(
                (
                    name,
                    first_text(result, "t:Output/t:ErrorInfo/t:Message"),
                    first_text(result, "t:Output/t:ErrorInfo/t:StackTrace"),
                )
            )
        elif outcome == "NotExecuted":
            reason = first_text(result, "t:Output/t:ErrorInfo/t:Message") or first_text(
                result, "t:Output/t:StdOut"
            )
            skips[(reason or "no reason recorded")[:300]] += 1
    return outcomes, failures, skips


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--redact-env", action="append", default=[])
    parser.add_argument("--require-passed", action="store_true")
    parser.add_argument("paths", nargs="+")
    args = parser.parse_args()

    secrets = [
        os.environ[name]
        for name in args.redact_env
        if len(os.environ.get(name, "")) >= 8
    ]
    files = list(find_trx(args.paths))
    if not files:
        print("::error title=No test results::no .trx file was produced")
        return 1

    lines = ["| Suite | Passed | Failed | Skipped | Total |", "|---|---:|---:|---:|---:|"]
    details = []
    status = 0
    annotations = 0
    for path in files:
        redact(path, secrets)
        outcomes, failures, skips = read_suite(path)
        suite = path.stem
        passed = outcomes["Passed"]
        skipped = outcomes["NotExecuted"]
        failed = len(failures)
        total = sum(outcomes.values())
        lines.append(f"| {suite} | {passed} | {failed} | {skipped} | {total} |")
        print(f"{suite}: passed={passed} failed={failed} skipped={skipped} total={total}")
        if args.require_passed and passed == 0:
            print(f"::error title=Suite {suite} ran nothing::no test passed ({total} results)")
            status = 1
        if skips:
            details.append(f"#### {suite}: skipped tests")
            for reason, count in skips.most_common():
                details.append(f"- {count} x {reason}")
                print(f"  skipped {count} x {reason}")
        if failures:
            details.append(f"#### {suite}: failed tests")
            for name, message, stack in failures[:MAX_FAILURES_LISTED]:
                body = (message + "\n" + stack).strip()[:MAX_ERROR_CHARS]
                details.append(f"<details><summary>{html.escape(name)}</summary>\n\n```\n{body}\n```\n</details>")
                print(f"  FAILED {name}\n{body}\n")
                if annotations < MAX_ANNOTATIONS:
                    annotations += 1
                    first_line = (message.splitlines() or [""])[0][:200]
                    print(f"::error title=Test failed ({suite})::{name}: {first_line}")
            if len(failures) > MAX_FAILURES_LISTED:
                details.append(f"... and {len(failures) - MAX_FAILURES_LISTED} more (see the TRX artifact).")

    summary = os.environ.get("GITHUB_STEP_SUMMARY")
    if summary:
        with open(summary, "a", encoding="utf-8") as out:
            out.write("\n".join(lines + [""] + details) + "\n")
    return status


if __name__ == "__main__":
    sys.exit(main())

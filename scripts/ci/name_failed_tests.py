"""Name the tests that failed, from .trx result files, as GitHub error annotations.

Runs after a failed test step. It never changes the job's result: it always exits 0.
GitHub shows only a limited number of annotations per step, so the first annotation
gives the total and says whether the list below it is complete; the full list goes to
the job summary.
"""
from __future__ import annotations

import html
import os
import re
import sys
from pathlib import Path

FAILED = re.compile(r'<UnitTestResult\b[^>]*\btestName="([^"]*)"[^>]*\boutcome="Failed"')
ANNOTATIONS = 10  # Error annotations GitHub shows for one step; the first one is the count.


def failed_tests(results: Path) -> list[str]:
    """Distinct names of failed tests in every .trx file under the directory."""
    names: set[str] = set()
    for path in sorted(results.rglob("*.trx")) if results.is_dir() else []:
        text = path.read_text(encoding="utf-8", errors="replace")
        names.update(html.unescape(match.group(1)) for match in FAILED.finditer(text))
    return sorted(names)


def escaped(message: str) -> str:
    """Escape a workflow-command message so that it stays one line of data."""
    return message.replace("%", "%25").replace("\r", "%0D").replace("\n", "%0A")


def report(names: list[str]) -> tuple[list[str], str]:
    """The annotation lines to print, and the Markdown for the job summary."""
    if not names:
        line = ("::error title=Failed tests::The test step failed, but no result file names a failed test "
                "(a build failure or a crashed test host). Read the job log.")
        return [line], "### Failed tests\n\nNo result file names a failed test. Read the job log.\n"
    shown = names[:ANNOTATIONS - 1]
    scope = "all named below" if len(shown) == len(names) else (
        f"the first {len(shown)} are named below; the full list is in the job summary")
    lines = [f"::error title=Failed tests::{len(names)} test(s) failed, {scope}."]
    lines += [f"::error title=Failed test::{escaped(name)}" for name in shown]
    summary = f"### Failed tests ({len(names)})\n\n" + "".join(f"- `{name}`\n" for name in names)
    return lines, summary


def main(argv: list[str]) -> int:
    names = failed_tests(Path(argv[1] if len(argv) > 1 else "test-results"))
    lines, summary = report(names)
    print("\n".join(lines))
    summary_path = os.environ.get("GITHUB_STEP_SUMMARY")
    if summary_path:
        with open(summary_path, "a", encoding="utf-8") as handle:
            handle.write(summary)
    return 0


if __name__ == "__main__":
    raise SystemExit(main(sys.argv))

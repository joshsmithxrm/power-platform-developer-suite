"""The Linux unit job names its failed tests from .trx files without changing the job's result."""
from __future__ import annotations

import importlib.util
import os
import subprocess
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
SCRIPT = ROOT / "scripts/ci/name_failed_tests.py"
spec = importlib.util.spec_from_file_location("name_failed_tests", SCRIPT)
namer = importlib.util.module_from_spec(spec)
spec.loader.exec_module(namer)


def result(name: str, outcome: str) -> str:
    # The attribute order the trx logger writes: testName before outcome, other attributes between and after.
    return (f'<UnitTestResult executionId="e" testId="t" testName="{name}" computerName="c" duration="00:00:01" '
            f'startTime="s" endTime="e" testType="x" outcome="{outcome}" testListId="l" relativeResultsDirectory="r">'
            "<Output><ErrorInfo><Message>boom</Message></ErrorInfo></Output></UnitTestResult>")


def trx(path: Path, *results: str) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text('<?xml version="1.0"?><TestRun><Results>' + "".join(results) + "</Results></TestRun>",
                    encoding="utf-8")


def run(directory: Path, summary: Path | None = None) -> subprocess.CompletedProcess:
    env = {key: value for key, value in os.environ.items() if key != "GITHUB_STEP_SUMMARY"}
    if summary is not None:
        env["GITHUB_STEP_SUMMARY"] = str(summary)
    return subprocess.run([sys.executable, str(SCRIPT), str(directory)], env=env,
                          capture_output=True, text=True, timeout=60, check=False)


def test_names_exactly_the_failed_tests_once_across_result_files(tmp_path):
    trx(tmp_path / "a/net8.trx", result("Ns.A.Passes", "Passed"), result("Ns.A.Fails", "Failed"))
    trx(tmp_path / "a/net10.trx", result("Ns.A.Fails", "Failed"), result("Ns.A.Skipped", "NotExecuted"))
    trx(tmp_path / "b/net10[1].trx", result("Ns.B.AlsoFails", "Failed"))
    summary = tmp_path / "summary.md"
    done = run(tmp_path, summary)
    assert done.returncode == 0 and done.stderr == ""
    assert done.stdout.splitlines() == [
        "::error title=Failed tests::2 test(s) failed, all named below.",
        "::error title=Failed test::Ns.A.Fails",
        "::error title=Failed test::Ns.B.AlsoFails",
    ]
    assert summary.read_text(encoding="utf-8") == "### Failed tests (2)\n\n- `Ns.A.Fails`\n- `Ns.B.AlsoFails`\n"


def test_a_long_list_says_it_is_cut_and_the_summary_holds_all_of_it(tmp_path):
    trx(tmp_path / "many.trx", *(result(f"Ns.T.Case{index:02d}", "Failed") for index in range(25)))
    summary = tmp_path / "summary.md"
    lines = run(tmp_path, summary).stdout.splitlines()
    assert lines[0] == ("::error title=Failed tests::25 test(s) failed, the first 9 are named below; "
                        "the full list is in the job summary.")
    assert len(lines) == namer.ANNOTATIONS == 10  # The count plus nine names: what one step can show.
    assert lines[1:] == [f"::error title=Failed test::Ns.T.Case{index:02d}" for index in range(9)]
    assert summary.read_text(encoding="utf-8").count("\n- `Ns.T.Case") == 25


def test_theory_names_are_decoded_and_cannot_break_out_of_the_annotation_line(tmp_path):
    encoded = "Ns.T.Theory(value: &quot;a&lt;b&gt;&amp;c&quot;, pct: &quot;100%0A::error::x&quot;)"
    trx(tmp_path / "t.trx", result(encoded, "Failed"))
    lines = run(tmp_path).stdout.splitlines()
    assert lines[1] == '::error title=Failed test::Ns.T.Theory(value: "a<b>&c", pct: "100%250A::error::x")'
    assert len(lines) == 2
    assert namer.escaped("a%b\r\nc") == "a%25b%0D%0Ac"


def test_a_failure_with_no_named_test_points_to_the_job_log_and_still_exits_zero(tmp_path):
    trx(tmp_path / "ok.trx", result("Ns.A.Passes", "Passed"))
    for directory in (tmp_path, tmp_path / "missing"):
        done = run(directory)
        assert done.returncode == 0
        assert done.stdout.splitlines() == [
            "::error title=Failed tests::The test step failed, but no result file names a failed test "
            "(a build failure or a crashed test host). Read the job log."]

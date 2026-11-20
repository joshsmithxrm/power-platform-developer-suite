"""Differential tests of .claude/hooks/shakedown-safety.py against the things it imitates.

Oracle 1 (argument level) needs the parse-only helper in ParseOracle/ (a .NET 10 SDK to build it).
Oracle 2 (statement level) needs real bash and pwsh; for modes other than the unlisted-environment
gate it also needs oracle 1's helper. Each test skips, with the reason, when its tools are missing;
set PPDS_DIFF_REQUIRE=1 to make a missing tool a failure instead.

At the commit these tests were written against (3164e825), the requirement-severity tests fail: that
is the point. ``expected_failures.json`` names the failure groups known at that commit and the
finding each one belongs to; with PPDS_DIFF_XFAIL=1 a test whose failures all belong to known groups
is reported as xfail instead (a failure in any other group still fails). See README.md.
"""
from __future__ import annotations

import json
import os
import shutil
import sys
from pathlib import Path

import pytest

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE))

import differential  # noqa: E402
from _harness import MODES, OracleUnavailable, build_oracle  # noqa: E402

SIZE_ARGS = int(os.environ.get("PPDS_DIFF_SIZE_ARGS", "1500"))
SIZE_SHELL = int(os.environ.get("PPDS_DIFF_SIZE_SHELL", "700"))
TIMING_LENGTHS = tuple(int(x) for x in os.environ.get("PPDS_DIFF_TIMING_LENGTHS", "50000").split(","))
STRICT = os.environ.get("PPDS_DIFF_STRICT") == "1"
REQUIRE = os.environ.get("PPDS_DIFF_REQUIRE") == "1"
XFAIL = os.environ.get("PPDS_DIFF_XFAIL") == "1"
CASES = os.environ.get("PPDS_DIFF_CASES") or None
EXPECTED = json.loads((HERE / "expected_failures.json").read_text(encoding="utf-8"))


def _missing(reason: str):
    if REQUIRE:
        pytest.fail(reason)
    pytest.skip(reason)


@pytest.fixture(scope="module")
def args_report():
    try:
        build_oracle()
    except OracleUnavailable as exc:
        _missing(f"argument oracle unavailable: {exc}")
    return differential.run_argument_oracle(SIZE_ARGS, 1814, CASES, STRICT)


@pytest.fixture(scope="module")
def shell_report():
    if not shutil.which("bash") or not shutil.which("pwsh"):
        _missing("statement oracle needs real bash and pwsh")
    return differential.run_statement_oracle(SIZE_SHELL, 1780, CASES, STRICT)


@pytest.fixture(scope="module")
def timing_report():
    return differential.run_timing_oracle(TIMING_LENGTHS, ("shakedown",), 110, CASES)


def _check(report: dict, mode: str) -> None:
    sev = ("requirement",) + (("policy", "requirement-approx", "policy-approx") if STRICT else ("requirement-approx",))
    bad = [f for f in report["failures"] if f["mode"] == mode and f["severity"] in sev]
    if not bad:
        return
    groups = {}
    for f in bad:
        groups.setdefault(f["label"], []).append(f)
    lines = [f"{len(bad)} hook passes that ground truth says are harmful ({report['oracle']} oracle, mode {mode}):"]
    for label, fs in sorted(groups.items(), key=lambda kv: -len(kv[1])):
        f = fs[0]
        lines.append(f"  [{label}] x{len(fs)}  {f['tool']}: {f['command'][:160]!r}  -> {f['cause']}: {f['detail'][:120]}")
    known = set(EXPECTED["labels"])  # keys
    if XFAIL and all(set(label.split("+")) <= known for label in groups):
        pytest.xfail("\n".join(["known at 3164e825 (expected_failures.json):"] + lines))
    pytest.fail("\n".join(lines))


@pytest.mark.parametrize("mode", MODES)
def test_argument_oracle_hook_never_passes_a_harmful_vector(args_report, mode):
    _check(args_report, mode)


@pytest.mark.parametrize("mode", MODES)
def test_statement_oracle_hook_never_passes_a_harmful_text(shell_report, mode):
    if shell_report["argument_truth"] != "parse oracle" and mode != "gate-unlisted":
        _missing("argument oracle unavailable; only the unlisted-environment gate can be judged")
    _check(shell_report, mode)


def test_argument_oracle_renders_each_failing_vector_exactly(args_report):
    """Every failing vector was re-run in the real shell of its tool, with stand-ins, and the program
    received exactly that vector (otherwise the failure would rest on our quoting, not the hook)."""
    check = args_report["render_check"]
    if check is None or "skipped" in check:
        _missing(f"rendering check not run: {check}")
    assert check["dropped"] == 0, check


def test_argument_oracle_covers_every_command_path(args_report):
    """The generator reached every runnable command path of the real tree at least once."""
    assert args_report["shapes"].get("path", 0) >= 200, args_report["shapes"]


def test_statement_oracle_starts_stand_ins_under_both_shells(shell_report):
    started = shell_report["texts_starting_a_standin"]
    assert started.get("bash", 0) > SIZE_SHELL // 5 and started.get("pwsh", 0) > SIZE_SHELL // 5, started


def test_timing_long_inputs_are_refused_inside_the_budget(timing_report):
    """Texts of 50,000 characters and more, valid in both shells: refused inside the documented budget
    (read from the hook) plus a start-up allowance, and never after the registered timeout (read from
    .claude/settings.json)."""
    _check(timing_report, "shakedown")


def test_timing_texts_are_valid_in_both_shells(timing_report):
    bad = [r for r in timing_report["rows"] if r["valid_bash"] is False or r["valid_pwsh"] is False]
    assert not bad, bad[:3]

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
from _harness import MODES, OracleUnavailable, build_oracle, truth_mode  # noqa: E402

SIZE_ARGS = int(os.environ.get("PPDS_DIFF_SIZE_ARGS", "1500"))
SIZE_SHELL = int(os.environ.get("PPDS_DIFF_SIZE_SHELL", "700"))
TIMING_LENGTHS = tuple(int(x) for x in os.environ.get("PPDS_DIFF_TIMING_LENGTHS", "50000").split(","))
STRICT = os.environ.get("PPDS_DIFF_STRICT") == "1"
REQUIRE = os.environ.get("PPDS_DIFF_REQUIRE") == "1"
XFAIL = os.environ.get("PPDS_DIFF_XFAIL") == "1"
CASES = os.environ.get("PPDS_DIFF_CASES") or None
# A fresh seed for every generator (default: the fixed seeds the expected-failure labels were taken at).
SEED = int(os.environ["PPDS_DIFF_SEED"]) if os.environ.get("PPDS_DIFF_SEED") else None
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
    return differential.run_argument_oracle(SIZE_ARGS, 1814 if SEED is None else SEED, CASES, STRICT)


@pytest.fixture(scope="module")
def shell_report():
    if not shutil.which("bash") or not shutil.which("pwsh"):
        _missing("statement oracle needs real bash and pwsh")
    return differential.run_statement_oracle(SIZE_SHELL, 1780 if SEED is None else SEED, CASES, STRICT)


@pytest.fixture(scope="module")
def timing_report():
    return differential.run_timing_oracle(TIMING_LENGTHS, ("shakedown",), 110 if SEED is None else SEED, CASES)


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
    if shell_report["argument_truth"] != "parse oracle" and truth_mode(mode) != "gate-unlisted":
        _missing("argument oracle unavailable; only the unlisted-environment gate can be judged")
    _check(shell_report, mode)


def test_argument_oracle_renders_each_failing_vector_exactly(args_report):
    """Every failing vector was re-run in the real shell of its tool, with stand-ins, and the program
    received exactly that vector (otherwise the failure would rest on our quoting, not the hook)."""
    check = args_report["render_check"]
    if check is None or "skipped" in check:
        _missing(f"rendering check not run: {check}")
    assert check["dropped"] == 0, check


def test_argument_oracle_judges_every_write_path_as_a_write(args_report):
    """Every command path that can write (not in read_paths.json, or a conditional one) has at least
    one generated vector that the real parser runs and ground truth judges a write in the shakedown."""
    integ = args_report["integrity"]
    assert not integ["write_paths_not_judged"], integ["write_paths_not_judged"]


def test_argument_oracle_harness_integrity(args_report):
    """No parse-oracle errors, no skipped rendering check, no dropped renderings."""
    assert args_report["integrity"]["ok"], args_report["integrity"]["problems"]


def test_statement_oracle_harness_integrity(shell_report):
    """Every text has a recorded outcome under bash and under pwsh; timeouts and unreproduced
    pwsh-only failures stay under the stated limits (differential.MAX_*)."""
    assert shell_report["integrity"]["ok"], shell_report["integrity"]["problems"]


@pytest.mark.parametrize("fake", ["records-nothing", "hangs"])
def test_shell_lab_integrity_fails_when_pwsh_does_not_record(tmp_path, fake):
    """The integrity check must not go green on a PowerShell that runs nothing: a pwsh that exits at
    once fails the self-test and the canaries; one that hangs fails the timeout limit after its retry."""
    if not shutil.which("bash") or not shutil.which("pwsh"):
        _missing("the shell lab needs real bash and pwsh")
    from _harness import Sandbox
    from _shells import HarnessError, ShellLab
    script = tmp_path / "pwsh"
    script.write_text("#!/bin/sh\n" + ("exit 0\n" if fake == "records-nothing" else "exec sleep 60\n"), encoding="utf-8")
    script.chmod(0o755)
    sandbox = Sandbox()
    try:
        lab = ShellLab(sandbox)
        lab.pwsh = str(script)
        lab.first_timeout, lab.retry_timeout = 1.0, 2.0
        if fake == "records-nothing":
            with pytest.raises(HarnessError):
                lab.self_test()
        texts = ["ppds env list", "echo a ; ppds data delete --entity account"] * 4
        lab.run_all(texts)
        problems, info = differential._shell_integrity(lab, len(texts))
        assert problems, info
        if fake == "hangs":
            assert info["pwsh_timeouts"] == len(texts) and info["pwsh_retried"] == len(texts), info
        else:
            assert info["canary_failures"], info
    finally:
        sandbox.cleanup()


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


ORDINARY_LIMIT = int(os.environ.get("PPDS_DIFF_SIZE_ORDINARY", "120"))
RAW_LENGTHS = tuple(int(x) for x in os.environ.get("PPDS_DIFF_RAW_LENGTHS", "200000").split(","))


@pytest.fixture(scope="module")
def ordinary_report():
    if not shutil.which("bash") or not shutil.which("pwsh"):
        _missing("the ordinary-mention check needs real bash and pwsh")
    return differential.run_ordinary_oracle(ORDINARY_LIMIT, 7 if SEED is None else SEED, CASES)


@pytest.fixture(scope="module")
def raw_report():
    return differential.run_raw_timing_oracle(RAW_LENGTHS, 0, CASES)


def test_ordinary_oracle_harness_integrity(ordinary_report):
    assert ordinary_report["integrity"]["ok"], ordinary_report["integrity"]["problems"]


def test_ordinary_mentions_pass_with_a_listed_environment(ordinary_report):
    """The cost rule: with a listed environment and no shakedown, a command that starts no program
    under either shell and names no switch or target (a search, a process query) is not refused."""
    _check(ordinary_report, "gate-listed")


def test_raw_text_far_above_the_caps_is_answered_in_time(raw_report):
    """Raw text of one repeated character (or short unit), with and without a mention: an answer
    inside the timeout registered in .claude/settings.json."""
    _check(raw_report, "shakedown")


def test_ci_cannot_skip_the_differential_oracles():
    """The Python tests job sets up the pinned SDK and PPDS_DIFF_REQUIRE=1, so the argument oracle and
    the listed and shakedown modes of the statement oracle cannot skip there."""
    import yaml
    wf = yaml.safe_load((HERE.parents[2] / ".github" / "workflows" / "workflow-tests.yml").read_text(encoding="utf-8"))
    steps = wf["jobs"]["workflow-tests"]["steps"]
    dotnet = [st for st in steps if str(st.get("uses", "")).startswith("actions/setup-dotnet@")]
    assert dotnet and dotnet[0].get("with") == {"global-json-file": "global.json"}, steps
    run = next(st for st in steps if st.get("run") == "python -m pytest tests/ -q")
    assert str((run.get("env") or {}).get("PPDS_DIFF_REQUIRE")) == "1", run
    assert steps.index(dotnet[0]) < steps.index(run)

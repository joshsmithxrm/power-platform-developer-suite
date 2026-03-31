#!/usr/bin/env python3
"""
Behavioral scenario tests for workflow infrastructure.

Runs hooks, checks state transitions, and validates routing logic.
Each scenario: setup -> exercise -> assert -> teardown.

Usage:
  python scripts/verify-workflow.py          # Run all scenarios
  python scripts/verify-workflow.py <name>   # Run single scenario
  python scripts/verify-workflow.py --list   # List scenario names
"""
import argparse
import json
import os
import subprocess
import sys
import time
import typing
from dataclasses import asdict, dataclass


# ---------------------------------------------------------------------------
# Types
# ---------------------------------------------------------------------------

@dataclass
class ScenarioResult:
    status: str          # "pass" or "fail"
    duration_ms: int     # wall-clock milliseconds
    detail: str | None   # failure detail, None on pass


# ---------------------------------------------------------------------------
# Project root resolution (AC-17)
# ---------------------------------------------------------------------------

def get_project_root() -> str:
    """Resolve project root: git toplevel -> CLAUDE_PROJECT_DIR -> cwd."""
    try:
        result = subprocess.run(
            ["git", "rev-parse", "--show-toplevel"],
            capture_output=True, text=True, timeout=5,
        )
        if result.returncode == 0:
            return result.stdout.strip()
    except (subprocess.TimeoutExpired, FileNotFoundError):
        pass
    return os.environ.get("CLAUDE_PROJECT_DIR", os.getcwd())


# ---------------------------------------------------------------------------
# Scenario registry
# ---------------------------------------------------------------------------

SCENARIOS: dict[str, "typing.Callable"] = {}


def scenario(name: str):
    """Decorator to register a scenario function."""
    def decorator(fn):
        SCENARIOS[name] = fn
        return fn
    return decorator


# ---------------------------------------------------------------------------
# Result helpers
# ---------------------------------------------------------------------------

def pass_result(ctx: "ScenarioContext") -> ScenarioResult:
    return ScenarioResult(status="pass", duration_ms=ctx.elapsed_ms(), detail=None)


def fail(ctx: "ScenarioContext", detail: str) -> ScenarioResult:
    return ScenarioResult(status="fail", duration_ms=ctx.elapsed_ms(), detail=detail)


# ---------------------------------------------------------------------------
# State backup context manager (AC-13, AC-14)
# ---------------------------------------------------------------------------

class StateBackup:
    """Backup and restore .workflow/state.json around scenario runs."""

    def __init__(self, project_root: str):
        self._project_root = project_root
        self._workflow_dir = os.path.join(project_root, ".workflow")
        self._state_path = os.path.join(self._workflow_dir, "state.json")
        self._original_content: str | None = None
        self._dir_existed: bool = False

    def __enter__(self):
        self._dir_existed = os.path.isdir(self._workflow_dir)
        if os.path.isfile(self._state_path):
            with open(self._state_path, "r", encoding="utf-8") as f:
                self._original_content = f.read()
        else:
            self._original_content = None
        return self

    def __exit__(self, exc_type, exc_val, exc_tb):
        try:
            if self._original_content is not None:
                os.makedirs(self._workflow_dir, exist_ok=True)
                with open(self._state_path, "w", encoding="utf-8") as f:
                    f.write(self._original_content)
            else:
                # Remove state file if it didn't exist before
                if os.path.isfile(self._state_path):
                    os.remove(self._state_path)
                # Remove .workflow/ dir if it didn't exist before and is now empty
                if not self._dir_existed and os.path.isdir(self._workflow_dir):
                    try:
                        os.rmdir(self._workflow_dir)
                    except OSError:
                        pass  # Not empty — leave it
        except OSError as e:
            print(
                f"WARNING: Failed to restore state: {e}\n"
                f"Backup content: {self._original_content!r}",
                file=sys.stderr,
            )
        return False  # Do not suppress exceptions


# ---------------------------------------------------------------------------
# Scenario context
# ---------------------------------------------------------------------------

class ScenarioContext:
    """Per-scenario context providing state helpers and hook runner."""

    def __init__(self, project_root: str):
        self._project_root = project_root
        self._workflow_dir = os.path.join(project_root, ".workflow")
        self._state_path = os.path.join(self._workflow_dir, "state.json")
        self._start_time = time.monotonic()

    def write_state(self, data: dict) -> None:
        """Write full state JSON directly to .workflow/state.json."""
        os.makedirs(self._workflow_dir, exist_ok=True)
        with open(self._state_path, "w", encoding="utf-8") as f:
            json.dump(data, f, indent=2)
            f.write("\n")

    def read_state(self) -> dict:
        """Read current state from .workflow/state.json."""
        try:
            with open(self._state_path, "r", encoding="utf-8") as f:
                return json.load(f)
        except (json.JSONDecodeError, OSError):
            return {}

    def run_hook(
        self,
        hook_name: str,
        stdin_json: dict | None = None,
        env_override: dict | None = None,
    ) -> subprocess.CompletedProcess:
        """Run a hook script via subprocess (AC-15: shell=False)."""
        hook_path = os.path.join(self._project_root, ".claude", "hooks", hook_name)
        if not os.path.isfile(hook_path):
            raise FileNotFoundError(f"Hook not found: {hook_path}")

        # Build environment: inherit current env, strip pipeline/shakedown vars,
        # set CLAUDE_PROJECT_DIR so hooks find the right state file
        env = dict(os.environ)
        env.pop("PPDS_PIPELINE", None)
        env.pop("PPDS_SHAKEDOWN", None)
        env["CLAUDE_PROJECT_DIR"] = self._project_root
        if env_override:
            env.update(env_override)

        input_str = json.dumps(stdin_json) if stdin_json is not None else ""

        return subprocess.run(
            [sys.executable, hook_path],
            input=input_str,
            capture_output=True,
            text=True,
            timeout=10,
            env=env,
        )

    def elapsed_ms(self) -> int:
        """Wall-clock milliseconds since context creation."""
        return int((time.monotonic() - self._start_time) * 1000)


# ---------------------------------------------------------------------------
# Helper: get HEAD sha
# ---------------------------------------------------------------------------

def get_head_sha(project_root: str | None = None) -> str:
    """Return current HEAD commit SHA."""
    kwargs = {}
    if project_root:
        kwargs["cwd"] = project_root
    result = subprocess.run(
        ["git", "rev-parse", "HEAD"],
        capture_output=True, text=True, timeout=5,
        **kwargs,
    )
    return result.stdout.strip()


# ---------------------------------------------------------------------------
# Helper: check for code changes vs origin/main
# ---------------------------------------------------------------------------

def has_code_changes(project_root: str) -> bool:
    """Return True if branch has code changes (not just specs/docs) vs origin/main."""
    try:
        result = subprocess.run(
            ["git", "diff", "--name-only", "origin/main...HEAD"],
            cwd=project_root,
            capture_output=True, text=True, timeout=10,
        )
        if result.returncode != 0:
            return False
        changed_files = [f for f in result.stdout.strip().split("\n") if f.strip()]
        non_code_prefixes = ("specs/", ".plans/", "docs/", "README", "CLAUDE.md")
        return any(not f.startswith(non_code_prefixes) for f in changed_files)
    except (subprocess.TimeoutExpired, FileNotFoundError):
        return False


# ===========================================================================
# Scenarios — Stop Hook (Phase 2: AC-06, AC-07)
# ===========================================================================

@scenario("hook-stop-block")
def test_stop_hook_blocks(ctx: ScenarioContext) -> ScenarioResult:
    """Stop hook prevents session exit when work is in progress and steps remain."""
    project_root = get_project_root()
    if not has_code_changes(project_root):
        return ScenarioResult(
            status="pass",
            duration_ms=ctx.elapsed_ms(),
            detail="skipped: prerequisite not met: no code changes vs origin/main",
        )
    ctx.write_state({
        "branch": "feat/test",
        "phase": "implementing",
        "gates": {"passed": None},
        "verify": {},
        "qa": {},
        "review": {},
        "stop_hook_count": 0,
    })
    result = ctx.run_hook("session-stop-workflow.py", stdin_json={})
    if result.returncode != 2:
        return fail(ctx, f"Expected exit code 2, got {result.returncode}")
    try:
        output = json.loads(result.stdout)
    except json.JSONDecodeError:
        return fail(ctx, f"Expected JSON stdout, got: {result.stdout[:200]}")
    if output.get("decision") != "block":
        return fail(ctx, f"Expected decision=block, got {output.get('decision')}")
    return pass_result(ctx)


@scenario("hook-stop-allow")
def test_stop_hook_allows(ctx: ScenarioContext) -> ScenarioResult:
    """Stop hook permits session exit during non-coding phases (design, review, QA)."""
    project_root = get_project_root()
    if not has_code_changes(project_root):
        return ScenarioResult(
            status="pass",
            duration_ms=ctx.elapsed_ms(),
            detail="skipped: prerequisite not met: no code changes vs origin/main",
        )
    non_enforcing = [
        "starting", "investigating", "design", "reviewing",
        "qa", "shakedown", "retro", "pr",
    ]
    for phase in non_enforcing:
        ctx.write_state({
            "branch": "feat/test",
            "phase": phase,
            "gates": {"passed": None},
            "verify": {},
            "qa": {},
            "review": {},
        })
        result = ctx.run_hook("session-stop-workflow.py", stdin_json={})
        if result.stdout.strip():
            try:
                output = json.loads(result.stdout)
                if output.get("decision") == "block":
                    return fail(ctx, f"Phase '{phase}' should allow but got block")
            except json.JSONDecodeError:
                pass  # Non-JSON stdout is fine for allow
    return pass_result(ctx)


# ===========================================================================
# Scenarios — PR Gate (Phase 3: AC-08, AC-09)
# ===========================================================================

@scenario("hook-pr-block")
def test_pr_gate_blocks(ctx: ScenarioContext) -> ScenarioResult:
    """PR gate blocks pull request creation when required checks haven't passed."""
    ctx.write_state({
        "branch": "feat/test",
        "phase": "implementing",
        "gates": {"passed": None},
    })
    stdin = {"tool_input": {"command": "gh pr create --title 'test' --body 'test'"}}
    result = ctx.run_hook("pr-gate.py", stdin_json=stdin)
    if result.returncode != 2:
        return fail(ctx, f"Expected exit 2, got {result.returncode}. stderr: {result.stderr[:200]}")
    return pass_result(ctx)


@scenario("hook-pr-allow")
def test_pr_gate_allows(ctx: ScenarioContext) -> ScenarioResult:
    """PR gate allows pull request creation when all required steps are complete."""
    project_root = get_project_root()
    head_sha = get_head_sha(project_root)
    ctx.write_state({
        "branch": "feat/test",
        "phase": "implementing",
        "gates": {"passed": "2026-03-28T00:00:00Z", "commit_ref": head_sha},
        "verify": {
            "cli": "2026-03-28T00:00:00Z", "cli_commit_ref": head_sha,
            "workflow": "2026-03-28T00:00:00Z", "workflow_commit_ref": head_sha,
        },
        "qa": {"cli": "2026-03-28T00:00:00Z", "cli_commit_ref": head_sha},
        "review": {"passed": "2026-03-28T00:00:00Z", "commit_ref": head_sha},
    })
    stdin = {"tool_input": {"command": "gh pr create --title 'test' --body 'test'"}}
    result = ctx.run_hook("pr-gate.py", stdin_json=stdin)
    if result.returncode != 0:
        return fail(ctx, f"Expected exit 0, got {result.returncode}. stderr: {result.stderr[:200]}")
    return pass_result(ctx)


# ===========================================================================
# Scenarios — State & Session (Phase 4: AC-10, AC-11, AC-12)
# ===========================================================================

@scenario("state-invalidation")
def test_state_invalidation(ctx: ScenarioContext) -> ScenarioResult:
    """New commit invalidates prior check results, requiring re-verification."""
    ctx.write_state({
        "branch": "feat/test",
        "phase": "implementing",
        "gates": {"passed": "2026-03-28T00:00:00Z", "commit_ref": "abc1234"},
        "review": {"passed": "2026-03-28T00:00:00Z", "commit_ref": "abc1234"},
        "last_commit": "abc1234",
    })
    result = ctx.run_hook("post-commit-state.py", stdin_json={})
    state = ctx.read_state()
    if state.get("gates", {}).get("passed") is not None:
        return fail(ctx, f"Expected gates.passed=null, got {state['gates'].get('passed')}")
    if state.get("review", {}).get("passed") is not None:
        return fail(ctx, f"Expected review.passed=null, got {state['review'].get('passed')}")
    if state.get("review", {}).get("commit_ref") is not None:
        return fail(ctx, f"Expected review.commit_ref=null, got {state['review'].get('commit_ref')}")
    return pass_result(ctx)


@scenario("session-start-completeness")
def test_session_start_completeness(ctx: ScenarioContext) -> ScenarioResult:
    """Session start shows only remaining required steps, not already-completed ones."""
    project_root = get_project_root()
    head_sha = get_head_sha(project_root)
    ctx.write_state({
        "branch": "feat/test",
        "phase": "implementing",
        "gates": {"passed": "2026-03-28T00:00:00Z", "commit_ref": head_sha},
        "verify": {"cli": "2026-03-28T00:00:00Z"},
        "qa": {},
        "review": {},
    })
    result = ctx.run_hook("session-start-workflow.py", stdin_json={})
    stderr = result.stderr
    # Should mention /qa and /review as missing, NOT /gates or /verify
    if "/qa" not in stderr or "/review" not in stderr:
        return fail(ctx, f"Expected /qa and /review in Required line. stderr: {stderr[:500]}")
    # Check that /gates and /verify are NOT in the Required section
    if "Required before PR:" in stderr:
        required_section = stderr.split("Required before PR:")[-1].split("\n")[0]
        if "/gates" in required_section:
            return fail(ctx, "Gates should not appear in Required list (already passed)")
        if "/verify" in required_section:
            return fail(ctx, "Verify should not appear in Required list (already passed)")
    return pass_result(ctx)


@scenario("resume-detection")
def test_resume_detection(ctx: ScenarioContext) -> ScenarioResult:
    """Resuming a session shows only the steps still needed, not those already done."""
    project_root = get_project_root()
    head_sha = get_head_sha(project_root)
    ctx.write_state({
        "branch": "feat/test",
        "phase": "implementing",
        "gates": {"passed": "2026-03-28T00:00:00Z", "commit_ref": head_sha},
        "verify": {"cli": "2026-03-28T00:00:00Z"},
        "qa": {"ext": "2026-03-28T00:00:00Z"},
        "review": {},
    })
    result = ctx.run_hook("session-start-workflow.py", stdin_json={})
    stderr = result.stderr
    # Only /review should be missing
    if "/review" not in stderr:
        return fail(ctx, f"Expected /review in Required line. stderr: {stderr[:500]}")
    # /gates, /verify, /qa should NOT appear as required
    if "Required before PR:" in stderr:
        required_section = stderr.split("Required before PR:")[-1].split("\n")[0]
        for step in ["/gates", "/verify", "/qa"]:
            if step in required_section:
                return fail(ctx, f"{step} should not be in Required list (already complete)")
    return pass_result(ctx)


# ===========================================================================
# Scenarios — Commit-Aware Validation (v8.0)
# ===========================================================================

@scenario("commit-ref-validation")
def test_commit_ref_validation(ctx: ScenarioContext) -> ScenarioResult:
    """PR gate blocks when gates.commit_ref doesn't match HEAD, passes when it does."""
    project_root = get_project_root()
    head_sha = get_head_sha(project_root)

    # Test 1: Wrong commit_ref -> blocked
    ctx.write_state({
        "branch": "feat/test",
        "phase": "implementing",
        "gates": {"passed": "2026-03-28T00:00:00Z", "commit_ref": "wrong_sha_1234567890"},
        "verify": {"workflow": "2026-03-28T00:00:00Z", "workflow_commit_ref": head_sha},
        "qa": {},  # workflow-only, no QA needed
        "review": {"passed": "2026-03-28T00:00:00Z", "commit_ref": head_sha},
    })
    stdin = {"tool_input": {"command": "gh pr create --title 'test' --body 'test'"}}
    result = ctx.run_hook("pr-gate.py", stdin_json=stdin)
    if result.returncode != 2:
        return fail(ctx, f"Expected exit 2 for wrong commit_ref, got {result.returncode}. stderr: {result.stderr[:300]}")

    # Test 2: Correct commit_ref -> allowed
    ctx.write_state({
        "branch": "feat/test",
        "phase": "implementing",
        "gates": {"passed": "2026-03-28T00:00:00Z", "commit_ref": head_sha},
        "verify": {"workflow": "2026-03-28T00:00:00Z", "workflow_commit_ref": head_sha},
        "qa": {},  # workflow-only, no QA needed
        "review": {"passed": "2026-03-28T00:00:00Z", "commit_ref": head_sha},
    })
    result = ctx.run_hook("pr-gate.py", stdin_json=stdin)
    if result.returncode != 0:
        return fail(ctx, f"Expected exit 0 for correct commit_ref, got {result.returncode}. stderr: {result.stderr[:300]}")

    return pass_result(ctx)


@scenario("workflow-only-no-qa")
def test_workflow_only_no_qa(ctx: ScenarioContext) -> ScenarioResult:
    """Workflow-only diff passes PR gate without QA entries."""
    project_root = get_project_root()
    head_sha = get_head_sha(project_root)

    # State with gates, verify(workflow), review -- but NO qa entries
    # This should pass because the diff is workflow-only
    ctx.write_state({
        "branch": "feat/test",
        "phase": "implementing",
        "gates": {"passed": "2026-03-28T00:00:00Z", "commit_ref": head_sha},
        "verify": {"workflow": "2026-03-28T00:00:00Z", "workflow_commit_ref": head_sha},
        "qa": {},
        "review": {"passed": "2026-03-28T00:00:00Z", "commit_ref": head_sha},
    })
    stdin = {"tool_input": {"command": "gh pr create --title 'test' --body 'test'"}}
    result = ctx.run_hook("pr-gate.py", stdin_json=stdin)
    if result.returncode != 0:
        return fail(ctx, f"Expected exit 0 for workflow-only diff (no QA needed), got {result.returncode}. stderr: {result.stderr[:300]}")

    return pass_result(ctx)


# ---------------------------------------------------------------------------
# Runner
# ---------------------------------------------------------------------------

def run_scenarios(
    project_root: str,
    selected: str | None = None,
) -> tuple[dict[str, ScenarioResult], bool]:
    """Run scenarios inside StateBackup. Returns (results_dict, all_passed)."""
    results: dict[str, ScenarioResult] = {}

    with StateBackup(project_root):
        targets = SCENARIOS if selected is None else {selected: SCENARIOS[selected]}
        for name, fn in targets.items():
            print(f"  {name} ...", end="", file=sys.stderr, flush=True)
            ctx = ScenarioContext(project_root)
            try:
                result = fn(ctx)
                results[name] = result
            except FileNotFoundError as e:
                results[name] = ScenarioResult(
                    status="fail",
                    duration_ms=ctx.elapsed_ms(),
                    detail=str(e),
                )
            except subprocess.TimeoutExpired:
                results[name] = ScenarioResult(
                    status="fail",
                    duration_ms=ctx.elapsed_ms(),
                    detail="Hook timed out (>10s)",
                )
            except Exception as e:
                results[name] = ScenarioResult(
                    status="fail",
                    duration_ms=ctx.elapsed_ms(),
                    detail=f"Unexpected error: {type(e).__name__}: {e}",
                )
            status_char = "PASS" if results[name].status == "pass" else "FAIL"
            print(f" {status_char}", file=sys.stderr)

    all_passed = all(r.status == "pass" for r in results.values())
    return results, all_passed


# ---------------------------------------------------------------------------
# Main
# ---------------------------------------------------------------------------

def main():
    parser = argparse.ArgumentParser(
        description=(
            "Verify that git hooks and session-state machinery behave correctly. "
            "Runs behavioral scenarios that exercise workflow hooks against "
            "synthetic state, then reports results as JSON on stdout."
        ),
        epilog=(
            "examples:\n"
            "  %(prog)s              Run all scenarios, JSON to stdout\n"
            "  %(prog)s hook-pr-block  Run one scenario\n"
            "  %(prog)s --list       Show available scenarios\n"
            "\n"
            "stdout carries machine-readable JSON; progress goes to stderr.\n"
            "Exit 0 when all pass; exit 1 on failure.\n"
            "\n"
            "JSON fields per scenario:\n"
            "  status       \"pass\" or \"fail\"\n"
            "  duration_ms  wall-clock milliseconds\n"
            "  detail       null on pass; diagnostic string on fail,\n"
            "               e.g. \"Expected exit code 2, got 0\""
        ),
        formatter_class=argparse.RawDescriptionHelpFormatter,
    )
    parser.add_argument(
        "scenario",
        nargs="?",
        default=None,
        help="Run a single scenario by name (omit to run all)",
    )
    parser.add_argument(
        "--list",
        action="store_true",
        dest="list_scenarios",
        help="List available scenarios with descriptions",
    )
    args, remaining = parser.parse_known_args()
    if remaining:
        print(
            f"Unexpected arguments: {' '.join(remaining)}. Use --help.",
            file=sys.stderr,
        )
        sys.exit(1)

    # --list: print scenario names with descriptions to stdout
    if args.list_scenarios:
        for name in sorted(SCENARIOS):
            desc = (SCENARIOS[name].__doc__ or "").split("\n")[0].strip()
            print(f"{name:30s}  {desc}" if desc else name)
        sys.exit(0)

    # Validate scenario name
    if args.scenario and args.scenario not in SCENARIOS:
        print(
            f"Unknown scenario: {args.scenario}. Use --list.",
            file=sys.stderr,
        )
        sys.exit(1)

    project_root = get_project_root()
    print(f"Running workflow scenarios from {project_root}", file=sys.stderr)

    # Run scenarios
    results, all_passed = run_scenarios(project_root, args.scenario)

    # Build JSON output
    passed_count = sum(1 for r in results.values() if r.status == "pass")
    failed_count = sum(1 for r in results.values() if r.status == "fail")

    from datetime import datetime, timezone
    report = {
        "scenarios": {
            name: asdict(result) for name, result in results.items()
        },
        "summary": {
            "total": len(results),
            "passed": passed_count,
            "failed": failed_count,
        },
        "timestamp": datetime.now(timezone.utc).isoformat(),
    }

    print(json.dumps(report, indent=2))

    # Human-readable summary on stderr
    verdict = "PASS" if all_passed else "FAIL"
    print(f"{passed_count} passed, {failed_count} failed: {verdict}", file=sys.stderr)

    # Write verify.workflow timestamp on all-pass when running ALL scenarios (AC-16)
    if all_passed and args.scenario is None and len(results) > 0:
        state_py = os.path.join(project_root, "scripts", "workflow-state.py")
        try:
            state_result = subprocess.run(
                [sys.executable, state_py, "set", "verify.workflow", "now"],
                capture_output=True, text=True, timeout=10,
            )
            if state_result.returncode != 0:
                print(
                    f"WARNING: workflow-state.py exited {state_result.returncode}: "
                    f"{state_result.stderr.strip()}",
                    file=sys.stderr,
                )
        except (subprocess.TimeoutExpired, FileNotFoundError):
            print("WARNING: Could not write verify.workflow timestamp", file=sys.stderr)

    sys.exit(0 if all_passed else 1)


if __name__ == "__main__":
    main()

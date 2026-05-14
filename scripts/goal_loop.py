"""Goal-driven verification loop for /implement.

See specs/goal-driven-implement.md (Core Requirements 1-10, ACs 01-16).

Usage:
    from goal_loop import Goal, GoalLoopOutcome, read_goal_from_spec, run_until_green

    goal = read_goal_from_spec("specs/my-feature.md")
    if goal.verification_command is None:
        # spec opted out — skip the loop
        return
    result = run_until_green(goal, attempt_fix=lambda r: dispatch_subagent(r))
    if result.outcome is not GoalLoopOutcome.GREEN:
        # surface result.outcome and result.last_stdout/stderr to operator
        ...

Public API:
    Goal (frozen dataclass)
    GoalLoopOutcome (Enum)
    LastVerificationResult (frozen dataclass)
    GoalLoopResult (dataclass)
    read_goal_from_spec(spec_path) -> Goal
    run_until_green(goal, *, attempt_fix, max_iterations=None) -> GoalLoopResult
"""
from __future__ import annotations

import collections
import hashlib
import re
import subprocess
import sys
from dataclasses import dataclass, field
from enum import Enum
from pathlib import Path
from typing import Callable, Deque, Optional, Union

# claude_dispatch lives next to this module. Import lazily inside the function
# that needs it so unit tests can stub the exception type if claude_dispatch
# is unavailable (e.g. in a minimal sandbox).
try:
    from claude_dispatch import BlockedSessionError
except ImportError:  # pragma: no cover - exercised only when sibling import fails
    class BlockedSessionError(Exception):  # type: ignore[no-redef]
        def __init__(self, short: str = "", needs: str = "") -> None:
            super().__init__(f"session {short} blocked: {needs}")
            self.short = short
            self.needs = needs


_FRONTMATTER_RE = re.compile(r"^\*\*Verification:\*\*\s*`([^`]*)`", re.MULTILINE)
_MAX_ITER_RE = re.compile(
    r"^\*\*Verification Max Iterations:\*\*\s*(-?\d+)", re.MULTILINE
)
_STUCK_THRESHOLD = 3


class GoalLoopOutcome(Enum):
    GREEN = "green"
    ITERATION_CAP = "iteration_cap"
    BLOCKED_HARD = "blocked_hard"
    STUCK_OUTPUT = "stuck_output"
    FIX_ERROR = "fix_error"


@dataclass(frozen=True)
class Goal:
    verification_command: Optional[str]
    max_iterations: int = 10


@dataclass(frozen=True)
class LastVerificationResult:
    exit_code: int
    stdout: str
    stderr: str
    iteration: int


@dataclass
class GoalLoopResult:
    outcome: GoalLoopOutcome
    iterations: int
    last_exit_code: Optional[int]
    last_stdout: str
    last_stderr: str
    stuck_hash: Optional[str] = None
    blocked_needs: Optional[str] = None
    error: Optional[BaseException] = None


def _extract_frontmatter(text: str) -> str:
    """Return the prelude before the first standalone `---` line.

    The SPEC-TEMPLATE places frontmatter (Status/Last Updated/Code/Surfaces)
    before the first `---` separator. We only scan that prelude so a
    `**Verification:**` line in an example block deeper in the spec does not
    accidentally count as the goal.
    """
    parts = re.split(r"(?m)^---\s*$", text, maxsplit=1)
    return parts[0]


def read_goal_from_spec(spec_path: Union[str, Path]) -> Goal:
    """Parse `**Verification:**` and `**Verification Max Iterations:**` from spec frontmatter.

    Returns Goal(verification_command=None, max_iterations=10) when the
    `**Verification:**` line is absent — that opts the spec out of the loop.

    Raises ValueError if the verification command is present but empty after
    strip(), or if max_iterations is present but <= 0.
    """
    text = Path(spec_path).read_text(encoding="utf-8")
    frontmatter = _extract_frontmatter(text)

    cmd_match = _FRONTMATTER_RE.search(frontmatter)
    verification_command: Optional[str]
    if cmd_match is None:
        verification_command = None
    else:
        raw = cmd_match.group(1).strip()
        if not raw:
            raise ValueError("verification_command must be non-empty")
        verification_command = raw

    max_match = _MAX_ITER_RE.search(frontmatter)
    if max_match is None:
        max_iterations = 10
    else:
        max_iterations = int(max_match.group(1))
        if max_iterations < 1:
            raise ValueError("verification_max_iterations must be >= 1")

    return Goal(verification_command=verification_command, max_iterations=max_iterations)


def _hash_output(exit_code: int, stdout: str, stderr: str) -> str:
    return hashlib.sha256(f"{exit_code}|{stdout}|{stderr}".encode("utf-8")).hexdigest()


def _emit_progress(iteration: int, cap: int, exit_code: int, hash_hex: str) -> None:
    short = hash_hex[:8] if hash_hex else "--------"
    sys.stderr.write(
        f"goal-loop iter={iteration}/{cap} exit={exit_code} hash={short}\n"
    )
    sys.stderr.flush()


def run_until_green(
    goal: Goal,
    *,
    attempt_fix: Callable[[LastVerificationResult], object],
    max_iterations: Optional[int] = None,
) -> GoalLoopResult:
    """Iterate verification → fix → re-verify until a mechanical stop trips.

    See spec Core Requirement 3 for the exit conditions and Core Requirement 4
    for the empty-needs blocked tolerance (mirror of PR #1051 d1bfe877).
    """
    if not callable(attempt_fix):
        raise TypeError("attempt_fix must be callable")

    cap = max_iterations if max_iterations is not None else goal.max_iterations
    if cap < 1:
        raise ValueError("max_iterations must be >= 1")
    if goal.verification_command is None:
        raise ValueError("Goal has no verification_command; caller must skip the loop")

    hashes: Deque[str] = collections.deque(maxlen=_STUCK_THRESHOLD)
    last: Optional[LastVerificationResult] = None

    for iteration in range(1, cap + 1):
        proc = subprocess.run(
            goal.verification_command,
            shell=True,
            capture_output=True,
            text=True,
        )
        last = LastVerificationResult(
            exit_code=proc.returncode,
            stdout=proc.stdout,
            stderr=proc.stderr,
            iteration=iteration,
        )
        hash_hex = _hash_output(proc.returncode, proc.stdout, proc.stderr)
        _emit_progress(iteration, cap, proc.returncode, hash_hex)

        if proc.returncode == 0:
            return GoalLoopResult(
                outcome=GoalLoopOutcome.GREEN,
                iterations=iteration,
                last_exit_code=0,
                last_stdout=proc.stdout,
                last_stderr=proc.stderr,
            )

        hashes.append(hash_hex)
        if len(hashes) == _STUCK_THRESHOLD and len(set(hashes)) == 1:
            return GoalLoopResult(
                outcome=GoalLoopOutcome.STUCK_OUTPUT,
                iterations=iteration,
                last_exit_code=proc.returncode,
                last_stdout=proc.stdout,
                last_stderr=proc.stderr,
                stuck_hash=hash_hex,
            )

        if iteration == cap:
            return GoalLoopResult(
                outcome=GoalLoopOutcome.ITERATION_CAP,
                iterations=iteration,
                last_exit_code=proc.returncode,
                last_stdout=proc.stdout,
                last_stderr=proc.stderr,
            )

        try:
            attempt_fix(last)
        except BlockedSessionError as exc:
            # PR #1051 commit d1bfe877: empty `needs` = daemon transient,
            # not a real "stage asked a question". Keep iterating.
            if not (getattr(exc, "needs", "") or "").strip():
                continue
            return GoalLoopResult(
                outcome=GoalLoopOutcome.BLOCKED_HARD,
                iterations=iteration,
                last_exit_code=proc.returncode,
                last_stdout=proc.stdout,
                last_stderr=proc.stderr,
                blocked_needs=exc.needs,
            )
        except BaseException as exc:  # noqa: BLE001 — surfacing arbitrary fix errors is the contract
            return GoalLoopResult(
                outcome=GoalLoopOutcome.FIX_ERROR,
                iterations=iteration,
                last_exit_code=proc.returncode,
                last_stdout=proc.stdout,
                last_stderr=proc.stderr,
                error=exc,
            )

    # Unreachable: the loop above returns ITERATION_CAP on its final pass.
    assert last is not None
    return GoalLoopResult(
        outcome=GoalLoopOutcome.ITERATION_CAP,
        iterations=last.iteration,
        last_exit_code=last.exit_code,
        last_stdout=last.stdout,
        last_stderr=last.stderr,
    )

# Goal-Driven /implement

**Status:** Draft
**Last Updated:** 2026-05-14
**Code:** [scripts/goal_loop.py](../scripts/goal_loop.py) | [.claude/skills/implement/SKILL.md](../.claude/skills/implement/SKILL.md)
**Surfaces:** N/A (workflow tooling)

---

## Overview

`/implement` currently runs a phased plan to completion and then exits. Long autonomous sessions still need a human to spot when "the build is green" actually means "the feature works" — the operator reruns the verification command, sees the next bug, opens a new session, repeats. PR #1051 Phase B exhibited this exact pattern: seven operator-triggered fix sessions in ~4.5 hours, each one paying context-rebuild + cache-miss costs.

This spec adds a **goal-driven loop** to `/implement`: each spec declares a single shell command that must exit `0` for the work to be considered done. After each phase commit (and at the end of the run), `/implement` invokes that command. If it exits non-zero, the skill dispatches a debug-then-fix subagent and iterates. The loop only stops on real blockers — verification green, iteration cap, hard `BlockedSessionError`, or stuck-output detection.

The loop itself runs inside the existing `--bg` `/implement` session; no new daemon, no nested `claude_dispatch.spawn()` from inside the loop driver. The reusable helper is a pure-Python module (`scripts/goal_loop.py`) so the protocol is unit-testable without any `claude` subprocess.

### Goals

- **Single verification contract.** One shell command per spec, exit-code semantics, declared as frontmatter.
- **Mechanical stop conditions.** Every loop exit is detectable from data the script already collects (exit code, iteration count, output hash, subagent state).
- **Mirror PR #1051 blocked-with-empty-needs tolerance.** Hard-fail only when a subagent's `BlockedSessionError.needs` is populated; an empty-needs blocked transition is treated as still-working (precedent: commit `d1bfe877`).
- **No new dependencies.** Stdlib + existing `pyproject.toml` only.

### Non-Goals

- **Multi-command verification.** This PR ships one command per goal. Composite verification (multiple commands ANDed, fail-fast ordering, per-surface verification gates) is a follow-on.
- **Mid-loop goal redefinition.** `verification_command` is read once at loop entry; changing it mid-loop is explicitly forbidden.
- **Generic CI runner.** `goal_loop.py` is not a CI replacement; it is the inner loop of `/implement`. CI continues to run gates independently.
- **Replacing `/gates` and `/verify`.** Those continue to run as the Step 6 tail of `/implement`. The goal loop happens in Step 5 (per-phase) and an additional sweep before Step 6 starts.

---

## Architecture

```
                            +-------------------------------+
                            |  /implement (SKILL.md)        |
                            |  step 5: phase loop           |
                            |                               |
   spec frontmatter ----->  |  Step 5.G (NEW): run          |
   verification_command:    |    goal_loop.run_until_green  |
   verification_max_iter:   |    after each phase commit    |
                            +---------------+---------------+
                                            |
                                            v
                            +-------------------------------+
                            |  scripts/goal_loop.py (NEW)   |
                            |  - run_until_green(...)       |
                            |  - GoalLoopResult dataclass   |
                            |  - GoalLoopOutcome enum       |
                            |  - read_goal_from_spec(path)  |
                            +---------------+---------------+
                                            |
                       +--------------------+--------------------+
                       |                                         |
                       v                                         v
       run verification_command via subprocess         dispatch fix subagent
       (shell=True, capture stdout+stderr)             (caller passes a closure;
       hash exit_code+stdout+stderr → loop key         goal_loop is agnostic to
       detect 3-in-a-row → STUCK_OUTPUT                HOW a fix is attempted)
```

### Components

| Component | Responsibility |
|-----------|----------------|
| `scripts/goal_loop.py` | Pure-Python helper. Reads `verification_command` from spec frontmatter, runs it, hashes output, calls back into a caller-supplied `attempt_fix()` callable on failure, enforces stop conditions. No `claude` subprocess work — that's the caller's job. |
| `.claude/skills/implement/SKILL.md` | New Step 5.G ("Goal Verification") after each phase commit, and a pre-tail sweep before Step 6. Reads the spec's `verification_command` and invokes `goal_loop.run_until_green` with a fix-dispatch closure. |
| `tests/scripts/test_goal_loop.py` | Pytest suite mapping each AC to a behavioral test. Uses fakes for the verification command and for the fix-attempt closure — no real subprocess, no real `claude`. |

### Dependencies

- Uses patterns from: [dispatch-routing.md](./dispatch-routing.md) — `BlockedSessionError`/`needs` semantics are mirrored, not reused (the helper itself does not call `claude_dispatch.spawn`).
- Depends on: [architecture.md](./architecture.md) — Constitution A2 single-code-path applies (loop logic lives in one module, used by `/implement`).

---

## Specification

### Core Requirements

1. **Goal declaration via spec frontmatter.** The spec's frontmatter (the leading prelude before the first `---` separator) declares:
   - `**Verification:**` line with a single shell command in backticks, e.g.:
     ``**Verification:** `pytest tests/scripts/test_goal_loop.py -q` ``
   - Optional `**Verification Max Iterations:**` line with a positive integer; default `10`.

   The frontmatter is parsed by `goal_loop.read_goal_from_spec(path) -> Goal`. A spec missing `**Verification:**` causes `read_goal_from_spec` to return `Goal(verification_command=None, ...)`; `/implement` then **skips** the goal loop (backward compatible — existing specs continue to work).

2. **Single shell command per goal.** `verification_command` is a single string passed verbatim to `subprocess.run(..., shell=True, ...)`. Multi-command pipelines are the operator's problem (shell features like `&&`, `;`, `|` are not parsed — the operator can write `cmd1 && cmd2`, but the loop sees one exit code). Composite, fail-fast, multi-command verification with structured output is a follow-on PR (Roadmap).

3. **Loop exit conditions (mechanical, mutually exclusive).** `run_until_green` returns a `GoalLoopResult` with one of these outcomes:
   - `GREEN` — verification_command exited `0`.
   - `ITERATION_CAP` — `max_iterations` reached without GREEN.
   - `BLOCKED_HARD` — `attempt_fix()` raised `BlockedSessionError` with `needs` populated. The `needs` text is propagated in the result.
   - `STUCK_OUTPUT` — the SHA-256 hash of `(exit_code, stdout, stderr)` was identical for **3 consecutive** non-zero iterations.
   - `FIX_ERROR` — `attempt_fix()` raised any other exception (propagated as `result.error`).

4. **Mirror PR #1051 `d1bfe877` empty-needs tolerance.** If `attempt_fix()` raises `BlockedSessionError` whose `needs.strip()` is empty, the loop treats it as a still-working transient and continues to the next iteration (no `BLOCKED_HARD` exit). Only `BlockedSessionError` with non-empty `needs` produces `BLOCKED_HARD`. Mirrors `BgHandle.wait` in `scripts/claude_dispatch.py` (commit `d1bfe877`).

5. **Loop-prevention via output hashing.** After each verification run, compute `sha256(f"{exit_code}|{stdout}|{stderr}".encode("utf-8")).hexdigest()`. Maintain a deque of the last three non-zero-exit hashes. If all three are identical, return `STUCK_OUTPUT`. The hash deque is only appended on non-zero exits; a zero exit returns `GREEN` before any hashing. Hashing only kicks in once we have three non-zero hashes; with `max_iterations=2` the deque never fills.

6. **Goal immutability.** `Goal` is a frozen dataclass; `run_until_green` reads it once at loop entry and never reloads from disk. Callers cannot mutate the verification command mid-loop. If a phase's work changes the spec's verification command, that takes effect on the next `/implement` invocation, not the current one.

7. **Stdout / stderr discipline.** `goal_loop.py` writes progress lines to **stderr** (Constitution I1 applies even though this is workflow tooling — stdout is reserved for structured JSON if a future caller wants to consume it). Each iteration emits one stderr line: `goal-loop iter={n}/{cap} exit={code} hash={short8}`.

8. **No new dependencies.** `goal_loop.py` uses only `subprocess`, `hashlib`, `dataclasses`, `enum`, `collections`, `pathlib`, `re`, `sys`, `typing` from the stdlib. No additions to `pyproject.toml`.

9. **--bg interaction.** The loop runs **inside** the already-`--bg`'d `/implement` session. It does not spawn nested `--bg` sessions. When `/implement` decides to dispatch a fix subagent it does so via its normal `Agent` tool (foreground subagent within the `/implement` orchestrator); `attempt_fix()` is just the closure wrapping that dispatch. The `BlockedSessionError` semantics in Core Requirement 4 apply if a subagent's underlying `claude_dispatch` raises it.

10. **Skill integration — phase-level and pre-tail.** `.claude/skills/implement/SKILL.md` invokes the goal loop at two points:
    - **Step 5.G (NEW)** — after each phase commit, run `goal_loop.run_until_green` once with `max_iterations=1` (a single verification pass — no fix attempt at this level since the per-phase gate already covers build/test). This is fast-feedback per-phase verification that surfaces if the phase regressed the overall goal.
    - **Step 5.5 (NEW)** — after all phases commit but before the Step 6 tail, run `goal_loop.run_until_green` with the spec's full `verification_max_iterations` cap. This is the "is the feature actually done" sweep that PR #1051 Phase B needed.

    Both points reuse the same helper; only the iteration cap differs.

### Primary Flows

**Goal-driven loop, GREEN path:**

1. `/implement` calls `goal_loop.read_goal_from_spec("specs/foo.md") -> Goal(verification_command="pytest tests/...", max_iterations=10)`.
2. Calls `goal_loop.run_until_green(goal, attempt_fix=lambda r: dispatch_fix_agent(r))`.
3. Iteration 1: `subprocess.run("pytest tests/...", shell=True, capture_output=True)` exits `0`.
4. `run_until_green` returns `GoalLoopResult(outcome=GREEN, iterations=1, last_exit_code=0, ...)`.
5. `/implement` advances to next phase / Step 6 tail.

**Goal-driven loop, fix-then-green path:**

1. Iteration 1: exits `1`, stdout shows one failing test.
2. `run_until_green` calls `attempt_fix(last_run)`. The closure dispatches a subagent that reads the failure, edits source, commits.
3. Iteration 2: re-run command, exits `0`. Return `GREEN`.

**Goal-driven loop, STUCK_OUTPUT:**

1. Iterations 1, 2, 3: all exit `1` with identical stdout/stderr. Hash deque holds three identical hashes after iteration 3.
2. `run_until_green` returns `GoalLoopResult(outcome=STUCK_OUTPUT, iterations=3, last_exit_code=1, stuck_hash="abc12345...")` without calling `attempt_fix` a fourth time.
3. `/implement` surfaces the stuck output to the operator (stage log + PR description for the post-merge reviewer).

**Goal-driven loop, BLOCKED_HARD:**

1. Iteration 2's `attempt_fix(...)` raises `BlockedSessionError(short="abcd1234", needs="confirm whether table X is read-only")`.
2. `run_until_green` returns `GoalLoopResult(outcome=BLOCKED_HARD, blocked_needs="confirm whether table X is read-only", ...)`.
3. `/implement` halts and the operator is notified via the standard `--bg` `state=blocked` mechanism.

**Goal-driven loop, BLOCKED with empty needs (PR #1051 tolerance):**

1. Iteration 2's `attempt_fix(...)` raises `BlockedSessionError(short="abcd1234", needs="")`.
2. `run_until_green` swallows the exception, treats it as still-working, advances to iteration 3.
3. Loop continues until another exit condition trips.

### Constraints

- The loop never **modifies** the verification command, the spec file, or any other input data. It only reads.
- `attempt_fix` is invoked at most `max_iterations - 1` times (the last iteration just verifies; it does not get a "fix" budget). If iteration 1 fails and `max_iterations=10`, up to 9 fix attempts.
- The hash deque uses `collections.deque(maxlen=3)`. Hashes are only appended for non-zero exit codes.
- Iteration `0` is illegal; `max_iterations` must be `>= 1`. `read_goal_from_spec` raises `ValueError` on `**Verification Max Iterations:** 0` or negative.

### Validation Rules

| Field | Rule | Error |
|-------|------|-------|
| `verification_command` (frontmatter) | If present, must be a non-empty string after `strip()` | `ValueError("verification_command must be non-empty")` |
| `verification_max_iterations` (frontmatter) | Must parse as int >= 1 | `ValueError("verification_max_iterations must be >= 1")` |
| `max_iterations` (kwarg) | int >= 1 | `ValueError` at call site |
| `attempt_fix` (kwarg) | callable accepting one `LastVerificationResult`, returning anything (or raising) | `TypeError` at call site if not callable |

---

## Acceptance Criteria

| ID | Criterion | Test | Status |
|----|-----------|------|--------|
| AC-01 | `read_goal_from_spec` extracts `verification_command` from a `**Verification:** \`...\`` frontmatter line | `test_goal_loop.test_read_goal_from_spec_extracts_verification_command` | 🔲 |
| AC-02 | `read_goal_from_spec` returns `Goal(verification_command=None, ...)` for a spec with no `**Verification:**` line (backward compatible) | `test_goal_loop.test_read_goal_from_spec_returns_none_when_missing` | 🔲 |
| AC-03 | `read_goal_from_spec` extracts `verification_max_iterations` from frontmatter and defaults to `10` when absent | `test_goal_loop.test_read_goal_from_spec_max_iterations_default_and_override` | 🔲 |
| AC-04 | `run_until_green` returns `GoalLoopResult(outcome=GREEN, iterations=1)` when verification_command exits 0 on first try | `test_goal_loop.test_run_until_green_returns_green_on_first_pass` | 🔲 |
| AC-05 | `run_until_green` invokes `attempt_fix(last_result)` once per non-zero iteration up to `max_iterations - 1` times, then re-verifies | `test_goal_loop.test_run_until_green_invokes_fix_then_reverifies` | 🔲 |
| AC-06 | `run_until_green` returns `outcome=ITERATION_CAP` when `max_iterations` is exhausted without GREEN | `test_goal_loop.test_run_until_green_returns_iteration_cap` | 🔲 |
| AC-07 | `run_until_green` returns `outcome=STUCK_OUTPUT` when the SHA-256 hash of `(exit_code, stdout, stderr)` is identical for 3 consecutive non-zero iterations | `test_goal_loop.test_run_until_green_returns_stuck_output_on_three_identical_hashes` | 🔲 |
| AC-08 | `STUCK_OUTPUT` does **not** trip when consecutive non-zero iterations have *different* output (progress is being made) | `test_goal_loop.test_stuck_output_does_not_trip_when_output_changes` | 🔲 |
| AC-09 | `run_until_green` returns `outcome=BLOCKED_HARD` and propagates `needs` when `attempt_fix` raises `BlockedSessionError` with non-empty `needs` | `test_goal_loop.test_run_until_green_blocked_hard_with_needs` | 🔲 |
| AC-10 | `run_until_green` swallows `BlockedSessionError` with empty `needs` and continues to the next iteration (mirrors PR #1051 commit `d1bfe877`) | `test_goal_loop.test_run_until_green_tolerates_empty_needs_blocked` | 🔲 |
| AC-11 | `run_until_green` returns `outcome=FIX_ERROR` and propagates the exception when `attempt_fix` raises any non-`BlockedSessionError` exception | `test_goal_loop.test_run_until_green_returns_fix_error` | 🔲 |
| AC-12 | `Goal` is a frozen dataclass — attempting to mutate `verification_command` after construction raises `FrozenInstanceError` | `test_goal_loop.test_goal_dataclass_is_frozen` | 🔲 |
| AC-13 | `run_until_green` emits one stderr progress line per iteration in the format `goal-loop iter={n}/{cap} exit={code} hash={short8}` | `test_goal_loop.test_run_until_green_emits_stderr_progress_lines` | 🔲 |
| AC-14 | `read_goal_from_spec` raises `ValueError` for `**Verification Max Iterations:** 0` or a negative integer | `test_goal_loop.test_read_goal_from_spec_rejects_nonpositive_max_iterations` | 🔲 |
| AC-15 | `run_until_green` rejects `max_iterations < 1` with `ValueError` at call time | `test_goal_loop.test_run_until_green_rejects_invalid_max_iterations` | 🔲 |
| AC-16 | `.claude/skills/implement/SKILL.md` documents Step 5.G (per-phase) and Step 5.5 (pre-tail) and references `scripts/goal_loop.py` by path | `test_goal_loop.test_implement_skill_documents_goal_loop_steps` | 🔲 |

Status key: ✅ covered by passing test · ⚠️ test exists but failing · ❌ no test yet · 🔲 not yet implemented

### Edge Cases

| Scenario | Input | Expected Output |
|----------|-------|-----------------|
| Empty spec file | `""` | `Goal(verification_command=None, max_iterations=10)` |
| `**Verification:**` with empty backticks | ``**Verification:** `` `` `` | `ValueError("verification_command must be non-empty")` |
| `**Verification:**` line outside frontmatter (after first `---`) | (body content) | Ignored — only the frontmatter prelude is scanned |
| Verification command hangs (no `timeout` kwarg in v1) | (long-running command) | Loop hangs on `subprocess.run` — operator may wrap with `timeout 30s pytest …`. Roadmap covers a native timeout. |
| Spec frontmatter declares `max_iterations=1` | (one iteration) | If iteration 1 fails, return `ITERATION_CAP` immediately; `attempt_fix` is never called |
| `attempt_fix` returns normally but next verification still fails with identical output | (no progress) | Counts toward `STUCK_OUTPUT` |

### Test Examples

```python
# AC-04: green on first try
def test_run_until_green_returns_green_on_first_pass(monkeypatch):
    captured_runs: list[str] = []
    def fake_run(cmd, **kw):
        captured_runs.append(cmd)
        return subprocess.CompletedProcess(cmd, 0, "ok\n", "")
    monkeypatch.setattr(goal_loop.subprocess, "run", fake_run)
    goal = Goal(verification_command="pytest -q", max_iterations=10)
    result = goal_loop.run_until_green(goal, attempt_fix=lambda r: None)
    assert result.outcome is GoalLoopOutcome.GREEN
    assert result.iterations == 1
    assert captured_runs == ["pytest -q"]
```

```python
# AC-07: stuck output after 3 identical non-zero iterations
def test_run_until_green_returns_stuck_output_on_three_identical_hashes(monkeypatch):
    def always_red(cmd, **kw):
        return subprocess.CompletedProcess(cmd, 1, "same error\n", "")
    monkeypatch.setattr(goal_loop.subprocess, "run", always_red)
    fix_calls: list[int] = []
    def attempt_fix(r):
        fix_calls.append(r.exit_code)
    goal = Goal(verification_command="pytest -q", max_iterations=10)
    result = goal_loop.run_until_green(goal, attempt_fix=attempt_fix)
    assert result.outcome is GoalLoopOutcome.STUCK_OUTPUT
    assert result.iterations == 3
    assert len(fix_calls) == 2  # fix attempted after iter1 and iter2; stuck trips after iter3
```

```python
# AC-10: empty-needs blocked is tolerated (PR #1051 d1bfe877 mirror)
def test_run_until_green_tolerates_empty_needs_blocked(monkeypatch):
    call_count = {"n": 0}
    def fake_run(cmd, **kw):
        call_count["n"] += 1
        return subprocess.CompletedProcess(cmd, 0 if call_count["n"] >= 2 else 1, "out\n", "")
    monkeypatch.setattr(goal_loop.subprocess, "run", fake_run)
    raised = {"once": False}
    def attempt_fix(r):
        if not raised["once"]:
            raised["once"] = True
            raise BlockedSessionError(short="abcd1234", needs="")
    goal = Goal(verification_command="pytest -q", max_iterations=5)
    result = goal_loop.run_until_green(goal, attempt_fix=attempt_fix)
    assert result.outcome is GoalLoopOutcome.GREEN
    assert result.iterations == 2
```

---

## Core Types

### Goal

```python
@dataclass(frozen=True)
class Goal:
    verification_command: Optional[str]
    max_iterations: int = 10
```

### GoalLoopOutcome

```python
class GoalLoopOutcome(Enum):
    GREEN = "green"
    ITERATION_CAP = "iteration_cap"
    BLOCKED_HARD = "blocked_hard"
    STUCK_OUTPUT = "stuck_output"
    FIX_ERROR = "fix_error"
```

### LastVerificationResult

```python
@dataclass(frozen=True)
class LastVerificationResult:
    exit_code: int
    stdout: str
    stderr: str
    iteration: int  # 1-based
```

### GoalLoopResult

```python
@dataclass
class GoalLoopResult:
    outcome: GoalLoopOutcome
    iterations: int
    last_exit_code: Optional[int]
    last_stdout: str
    last_stderr: str
    stuck_hash: Optional[str] = None     # populated when outcome == STUCK_OUTPUT
    blocked_needs: Optional[str] = None  # populated when outcome == BLOCKED_HARD
    error: Optional[BaseException] = None  # populated when outcome == FIX_ERROR
```

### Public API

```python
def read_goal_from_spec(spec_path: Path | str) -> Goal: ...

def run_until_green(
    goal: Goal,
    *,
    attempt_fix: Callable[[LastVerificationResult], object],
    max_iterations: Optional[int] = None,  # overrides goal.max_iterations if set
) -> GoalLoopResult: ...
```

---

## Error Handling

### Error Types

| Error | Condition | Recovery |
|-------|-----------|----------|
| `ValueError` | Frontmatter parsing / invalid kwargs | Fix the spec or call site; never raised at loop runtime |
| `BlockedSessionError` (from `claude_dispatch`) | Raised by caller's `attempt_fix` closure | If `needs` empty → tolerated; if populated → `BLOCKED_HARD` |
| Other `Exception` | Raised by `attempt_fix` | Caught and surfaced as `FIX_ERROR` with the original exception in `result.error` |

### Recovery Strategies

- **STUCK_OUTPUT:** the loop has done what it can; the operator decides. PR description surfaces the stuck command + last output.
- **BLOCKED_HARD:** standard `--bg` blocked-state path; the daemon's `state.json` carries `needs` text for the operator.
- **ITERATION_CAP:** typically means the verification command is too broad (e.g. running the whole suite when one file would do) or the spec's `max_iterations` is set too low.

---

## Design Decisions

### Why frontmatter over inline `/implement --goal=...`?

**Context:** Goal must travel with the spec across sessions (operator A designs the spec; operator B runs `/implement` later; the goal must not be forgotten).

**Decision:** A `**Verification:** \`<command>\`` line in the spec's frontmatter.

**Alternatives considered:**

- **Inline argument** (`/implement specs/foo.md --goal="pytest tests/foo.py"`): rejected. The argument lives in shell history, not the spec. Sessions resumed via `/start ... --resume` would lose it. Re-running with the same spec must give the same goal.
- **Hard-coded default** (e.g. `dotnet test PPDS.sln`): rejected. Most specs touch only a slice of the suite; running the whole solution per fix iteration is wasteful and noisy. The default for specs without `**Verification:**` is **no goal loop at all** (backward compatible).
- **Separate `.verification` file alongside the spec**: rejected. Two files to keep in sync; trivially diverge.

**Consequences:**

- Positive: goal is in source control with the spec. `/implement` becomes a pure function of the spec.
- Negative: operators have to know to add the frontmatter line. The SPEC-TEMPLATE.md update in the implementation phase covers documentation.

### Why a single shell command (not a list)?

**Context:** A future spec might want "build green AND tests green AND lint green" verification.

**Decision:** v1 ships single-command. Operators can chain with shell features (`pytest -q && eslint .`). Composite verification with structured per-step output is a follow-on (see Roadmap).

**Alternatives considered:**

- **List of commands, AND-semantics, fail-fast:** rejected for v1 — out of scope per task brief. Would inflate the spec scope and the implementation surface.
- **Custom Python entry-point (caller imports a module that returns a result):** rejected — couples the loop to PPDS Python, breaks the "any command, any language" promise.

**Consequences:**

- Positive: implementation is ~150 lines of Python. Easy to test.
- Negative: aggregating verification across surfaces requires shell glue. The follow-on PR can introduce a structured format without breaking AC-01 through AC-15.

### Why hash-based loop-prevention instead of "same test name 3x"?

**Context:** PR #1051 retrospective surfaced cases where a fix changed *which* test failed without making net progress — the loop should still detect that as progress (because the symptom changed).

**Decision:** Hash the *full* `(exit_code, stdout, stderr)` of the verification command. Three identical hashes in a row = no progress. Any change = continue.

**Alternatives considered:**

- **Track failing test names:** brittle; requires parsing pytest/dotnet/eslint output formats; not language-agnostic.
- **Track exit code only:** false positives — five different failures with the same exit code 1 would all hash to "1" and trip after 3 iterations even though real progress is being made.

**Consequences:**

- Positive: language-agnostic, format-agnostic.
- Negative: a non-deterministic verification command (timestamps, random test order) will never hash-collide and will run to `ITERATION_CAP` instead of `STUCK_OUTPUT`. Acceptable; the cap is the backstop.

### Why mirror PR #1051's empty-needs tolerance instead of importing the check?

**Context:** `scripts/claude_dispatch.py:BgHandle.wait` handles empty-needs blocked transitions. We could `import` that check.

**Decision:** Mirror the semantic (catch `BlockedSessionError`, inspect `.needs`, tolerate if empty) directly in `goal_loop`. We do not depend on `BgHandle.wait` because the loop's caller chooses how to surface fix attempts — it might not use a `BgHandle` at all (e.g. a foreground subagent dispatch).

**Alternatives considered:**

- **Re-export the tolerance check from `claude_dispatch`:** rejected — increases coupling for one `if not needs.strip():` line.

**Consequences:**

- Positive: `goal_loop` has zero runtime dependencies on `claude_dispatch` (only an `import` for the exception type).
- Negative: two places enforce the same semantic. AC-10's test pins the semantic; if PR #1051's tolerance is ever revised, both sites must update.

### Why a closure (`attempt_fix`) instead of building the agent dispatch into `goal_loop`?

**Context:** `goal_loop` could call `claude_dispatch.spawn(...)` directly. That would make the helper self-contained but couple it to the dispatcher.

**Decision:** Take a `Callable` from the caller. `/implement` passes a closure that dispatches an `Agent` subagent; tests pass a fake function.

**Consequences:**

- Positive: unit-testable with no subprocess at all.
- Negative: `/implement` has to write a small dispatch closure. The SKILL.md update covers it.

---

## Configuration

| Setting | Type | Required | Default | Description |
|---------|------|----------|---------|-------------|
| `verification_command` (spec frontmatter) | string | No | (none — loop disabled) | Single shell command that exits 0 when the goal is met |
| `verification_max_iterations` (spec frontmatter) | int >= 1 | No | 10 | Total verification passes including the final one |

---

## Related Specs

- [dispatch-routing.md](./dispatch-routing.md) — source of `BlockedSessionError` and the `needs`-tolerance precedent (commit `d1bfe877`)
- [architecture.md](./architecture.md) — Constitution A2 (single-code-path)

---

## Changelog

| Date | Change |
|------|--------|
| 2026-05-14 | Initial spec (closes #1055) |

---

## Roadmap

- **Multi-command verification.** Structured list of commands with fail-fast / always-run semantics and per-step result reporting.
- **Per-surface verification.** `extension`, `tui`, `mcp`, `cli` keys mapping to their own commands; results aggregated.
- **Timeout per command.** Currently the operator has to wrap with `timeout`; native support is a clean follow-on.
- **Goal-loop telemetry.** JSONL append (similar to `.claude/state/sdk-spend.jsonl`) recording every loop's outcome so retros can mine for stuck-output patterns.

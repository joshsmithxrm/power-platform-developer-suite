# Stop-Hook Bypass for In-Flight Pipeline

**Status:** Draft
**Last Updated:** 2026-05-15
**Code:** [scripts/pipeline.py](../scripts/pipeline.py) | [.claude/hooks/session-stop-workflow.py](../.claude/hooks/session-stop-workflow.py)
**Surfaces:** N/A

---

## Overview

While `scripts/pipeline.py` runs as a background process, the parent Claude Code session's stop-hook (`session-stop-workflow.py`) fires on every assistant turn and incorrectly blocks exit. The pipeline's `/implement` subprocess overwrites `phase=pipeline` with `phase=implementing`; the stop-hook sees `implementing` + commits ahead and blocks, demanding `/pr` be run.

This spec introduces a `pipeline.in_flight` flag in `.workflow/state.json` — independent of `phase` — that the stop-hook reads to bypass enforcement while the pipeline orchestrator is actively running.

### Goals

- **Silence stop-hook noise during pipeline runs**: No more spurious `BLOCKED — commits ahead` messages while `pipeline.py` is orchestrating in the background.
- **Reliable cleanup**: Flag clears automatically via `finally` block, even on crash or `KeyboardInterrupt`.
- **No phase coupling**: The fix is orthogonal to `phase`; `/implement` can continue setting `phase=implementing` normally.

### Non-Goals

- Whether the stop hook is too aggressive overall (separate question).
- The CLAUDE.md "MUST run /pr to ship" rule itself — correct in spirit, this fixes only the legitimate pipeline bypass.
- Phase 1b supervisor pattern (#1069) — not implemented here.

---

## Architecture

```
┌─────────────────────────┐      ┌──────────────────────────┐
│  pipeline.py (bg proc)  │      │  Parent Claude Session   │
│                         │      │                          │
│  1. worktree created    │      │  stop-hook fires:        │
│  2. set pipeline.       │─────▶│  reads pipeline.         │
│     in_flight=true      │      │    in_flight == true?    │
│  3. run stages...       │      │  → bypass (exit 0)       │
│  4. [finally]           │      │                          │
│     set pipeline.       │      └──────────────────────────┘
│     in_flight=false     │
└─────────────────────────┘
         │
         ▼
   .workflow/state.json
   { "pipeline": { "in_flight": true } }
```

### Components

| Component | Responsibility |
|-----------|----------------|
| `scripts/pipeline.py` | Writes `pipeline.in_flight=true` at startup; clears in `finally` |
| `.claude/hooks/session-stop-workflow.py` | Reads flag; exits 0 (bypass) when `true` |
| `.workflow/state.json` | Shared state file, read by hook from parent session |

### Dependencies

- Depends on: [workflow-enforcement.md](./workflow-enforcement.md)

---

## Specification

### Core Requirements

1. `pipeline.py` MUST write `pipeline.in_flight=true` to `.workflow/state.json` as soon as the worktree is available (immediately after confirming the worktree exists or creating it, before any stage runs).
2. `pipeline.py` MUST clear the flag (`pipeline.in_flight=false`) in its `finally` block, so it clears on success, normal failure, `PipelineFailure`, and `KeyboardInterrupt`.
3. `session-stop-workflow.py` MUST exit 0 (allow stop) when `state["pipeline"]["in_flight"]` is truthy. This check runs after loading state and before the phase-based and commit-count gates.
4. The flag MUST be a boolean `true`/`false` in JSON (not a string), written via `workflow-state.py set pipeline.in_flight true/false`.

### Flag Write Locations in pipeline.py

**Location A — pre-loop (worktree exists before stage loop)**: After the existing `phase=pipeline` set at line ~1649, when `worktree_path and os.path.exists(worktree_path)` is already true.

**Location B — worktree stage handler**: After `create_worktree` succeeds or `worktree EXISTS` is logged (lines ~1682–1686), before copying spec/plan files.

Both locations use the same subprocess call pattern:
```python
subprocess.run(
    [sys.executable, "scripts/workflow-state.py", "set", "pipeline.in_flight", "true"],
    cwd=worktree_path, capture_output=True, text=True,
    encoding="utf-8", errors="replace", timeout=10,
)
```

**Clear location — `finally` block** (~line 1945): After releasing the lock, if `worktree_path` is set:
```python
subprocess.run(
    [sys.executable, "scripts/workflow-state.py", "set", "pipeline.in_flight", "false"],
    cwd=worktree_path, capture_output=True, text=True,
    encoding="utf-8", errors="replace", timeout=10,
)
```

### Stop-Hook Bypass in session-stop-workflow.py

After state is loaded (after line ~65), add:
```python
# Bypass: pipeline orchestrator is actively running
if state.get("pipeline", {}).get("in_flight"):
    sys.exit(0)
```

This runs before the `stop_hook_count` escape valve, the phase-aware bypass, and the commit-count gate.

### Constraints

- The flag write uses `workflow-state.py set pipeline.in_flight true` (existing mechanism — coerces `"true"` string to boolean `True`).
- The subprocess call must use `capture_output=True` (non-blocking, no stdout pollution).
- The clear call in `finally` must be best-effort only — wrapped in a condition `if worktree_path`.

---

## Acceptance Criteria

| ID | Criterion | Test | Status |
|----|-----------|------|--------|
| AC-197 | When `pipeline.in_flight=true` in state.json, stop-hook exits 0 (bypass) even with commits ahead and `phase=implementing` | `TestStopHookPipelineInFlight.test_bypass_when_in_flight` | ❌ |
| AC-198 | When `pipeline.in_flight=false` (explicitly cleared), stop-hook still blocks with commits ahead and `phase=implementing` | `TestStopHookPipelineInFlight.test_no_bypass_when_not_in_flight` | ❌ |
| AC-199 | When `pipeline.in_flight` is absent from state, stop-hook behavior is unchanged (blocks when commits ahead) | `TestStopHookPipelineInFlight.test_no_bypass_when_flag_absent` | ❌ |
| AC-200 | pipeline.py writes `pipeline.in_flight=true` to state before the implement stage runs | `TestPipelineInFlightFlag.test_flag_written_before_implement` | ❌ |
| AC-201 | pipeline.py clears `pipeline.in_flight=false` in the `finally` block on normal completion | `TestPipelineInFlightFlag.test_flag_cleared_on_success` | ❌ |
| AC-202 | pipeline.py clears `pipeline.in_flight=false` in the `finally` block on `PipelineFailure` | `TestPipelineInFlightFlag.test_flag_cleared_on_failure` | ❌ |

### Edge Cases

| Scenario | Input | Expected Output |
|----------|-------|-----------------|
| Flag set to string `"true"` (not bool) | `state["pipeline"]["in_flight"] = "true"` | Hook bypasses (truthy string) |
| `pipeline` key absent from state | state has no `pipeline` key | `.get("pipeline", {})` returns `{}` → no bypass |
| worktree not yet created when pipeline crashes early | worktree_path is None in finally | Clear call skipped (guarded by `if worktree_path`) |
| `state.json` deleted mid-run | hook reads no file | Existing "no state file" path exits 0 — not affected |

---

## Design Decisions

### Why a separate `pipeline.in_flight` flag vs. adding `implementing` to bypass phases?

**Context:** The stop-hook has a `PR_INVOCATION_BYPASS_PHASES` set; one option is adding `implementing` to it.

**Decision:** Separate flag, not phase bypass.

**Alternatives considered:**
- Add `implementing` to bypass phases: Rejected — `implementing` can occur in interactive (non-pipeline) sessions too; bypassing globally would hide legitimate non-pipeline "forgot to /pr" cases.
- Check for `pipeline.lock` file existence: Rejected — lock file cleanup is best-effort; a crash that leaves a stale lock would bypass the hook forever.
- Check `PPDS_PIPELINE` env var in parent: Rejected — env var is set in subprocess env (pipeline's children), not the parent session's env.

**Consequences:**
- Positive: Bypass is scoped to actual orchestrator runs; cleared reliably via `finally`.
- Negative: Slightly more state written to state.json; but dotted-key pattern is already established.

---

## Related Specs

- [workflow-enforcement.md](./workflow-enforcement.md) — the broader workflow enforcement system this fix belongs to.

---

## Changelog

| Date | Change |
|------|--------|
| 2026-05-15 | Initial spec (Phase 1a, closes #1068) |

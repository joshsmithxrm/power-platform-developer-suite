# Workflow Verify Tool

**Status:** Draft
**Last Updated:** 2026-03-28
**Code:** [scripts/verify-workflow.py](../scripts/verify-workflow.py), [scripts/verify_workflow_checks.py](../scripts/verify_workflow_checks.py)
**Surfaces:** N/A

---

## Overview

A Python script that runs behavioral scenario tests against the workflow infrastructure — hooks, state management, and routing logic. Each scenario sets up state, exercises a hook or script, and asserts the result. Pure mechanical verification with no AI sessions involved.

Fills the behavioral testing gap between structural validation (`/verify workflow` — files parse, schemas valid) and integration testing (`/shakedown-workflow` — full pipeline in throwaway worktrees).

### Goals

- **Behavioral verification**: Test that hooks fire correctly, state transitions work, and routing logic matches work types
- **Fast feedback**: Each scenario runs in milliseconds — no pipeline, no AI sessions, no worktrees
- **Pass/fail per scenario**: Structured JSON output with clear status, like a test suite
- **Composable**: Called by `/verify workflow` (as an additional check) and `/shakedown-workflow` (inside throwaway worktrees)

### Non-Goals

- **Full pipeline execution**: That's `/shakedown-workflow`'s job
- **Structural validation**: That's `/verify workflow`'s job (file parsing, schema checks)
- **Modifying hooks or scripts**: This tool tests them, doesn't change them
- **AI session testing**: Scenarios are mechanical — no `claude -p` invocations

---

## Architecture

```
Claude Code (or /shakedown-workflow)
    │
    │  python scripts/verify-workflow.py [scenario]
    ▼
┌─────────────────────────────────┐
│  verify-workflow.py              │
│                                  │
│  For each scenario:              │
│    1. Setup (state, env vars)    │
│    2. Exercise (run hook/script) │
│    3. Assert (check exit/state)  │
│    4. Teardown (restore state)   │
│                                  │
│  Output: JSON to stdout          │
│  Exit: 0 all pass, 1 any fail   │
└─────────────────────────────────┘
         │                    │
         ▼                    ▼
┌──────────────┐   ┌──────────────────┐
│ Hook scripts │   │ workflow-state.py │
│ (.claude/    │   │ (state mutations) │
│  hooks/*.py) │   │                   │
└──────────────┘   └──────────────────┘
```

No daemon, no long-lived process. Each scenario is a plain Python function that runs in-process or spawns a subprocess for the hook under test. State setup and teardown use `workflow-state.py` directly.

### Components

| Component | Responsibility |
|-----------|----------------|
| `scripts/verify-workflow.py` | CLI entry point, scenario registry, runner loop, JSON output, exit code |
| Scenario functions | Individual test functions — each tests one behavioral assertion |
| State helpers | Setup/teardown functions that write and restore `.workflow/state.json` |

### Dependencies

- Depends on: [workflow-enforcement.md](./workflow-enforcement.md)
- Uses: `scripts/workflow-state.py`, `.claude/hooks/*.py`

---

## Specification

### Core Requirements

1. Each scenario is an isolated test: setup → exercise → assert → teardown. No scenario depends on another's state.
2. State setup uses `workflow-state.py` commands. Teardown restores the original state file (or removes it if none existed).
3. Hook testing provides JSON to the hook script's stdin via `subprocess.run(..., input=json_string, text=True)` and checks exit code + stderr/stdout.
4. All subprocess spawns use `shell=False` (Constitution S2).
5. Output is JSON to stdout. Status messages go to stderr.
6. Exit code 0 if all scenarios pass, 1 if any fail.
7. On all-pass when running all scenarios (no args or explicit `<name>`), writes `verify.workflow` timestamp via `workflow-state.py`. Single-scenario runs do NOT write the timestamp — only a full pass counts.
8. The script must work in both the main repo and worktrees. Project root resolution uses `git rev-parse --show-toplevel` (matching `workflow-state.py`), falling back to `CLAUDE_PROJECT_DIR`, then cwd.

### Command Interface

| Command | Purpose |
|---------|---------|
| `python scripts/verify-workflow.py` | Run all scenarios |
| `python scripts/verify-workflow.py <name>` | Run a single scenario by name |
| `python scripts/verify-workflow.py --list` | List available scenario names |

### Scenarios

#### hook-stop-block

**Tests:** Stop hook returns `{"decision":"block"}` on stdout when phase is `implementing` and required steps are incomplete.

**Prerequisites:** Must run on a branch with code changes relative to `origin/main` (the hook checks `git diff --name-only origin/main...HEAD` and skips enforcement if only non-code files changed). Env vars `PPDS_PIPELINE` and `PPDS_SHAKEDOWN` must NOT be set.

**Setup:** Write state with `phase=implementing`, `gates.passed=null`, `verify={}`, `qa={}`, `review={}`, `stop_hook_count=0`. Provide stdin JSON without `stop_hook_active` field (the hook reads this from stdin, not env vars — if present, it's a re-entry guard that causes immediate exit 0).

**Exercise:** Run `python .claude/hooks/session-stop-workflow.py` with stdin JSON `{}` (empty object, no `stop_hook_active`).

**Assert:** Exit code 2. Stdout JSON contains `"decision": "block"`. (The stop hook exits 2 when blocking, communicating the block decision via both exit code and JSON `decision` field on stdout.)

**Teardown:** Restore original state.

#### hook-stop-allow

**Tests:** Stop hook allows (no block decision) for all non-enforcing phases: `starting`, `investigating`, `design`, `reviewing`, `qa`, `shakedown`, `retro`, `pr`.

**Prerequisites:** Same as hook-stop-block (branch with code changes, no `PPDS_PIPELINE`/`PPDS_SHAKEDOWN`).

**Setup:** For each of the 8 non-enforcing phases, write state with that phase value and incomplete steps (gates/verify/qa/review all empty).

**Exercise:** Run stop hook with stdin JSON `{}` for each phase.

**Assert:** For each phase: stdout JSON does NOT contain `"decision": "block"` (hook exits without enforcement).

**Teardown:** Restore original state.

#### hook-pr-block

**Tests:** PR gate hook exits 2 (block) when gates/verify/qa/review are not all current.

**Setup:** Write state with `gates.passed=null` (or stale `commit_ref`).

**Exercise:** Run `python .claude/hooks/pr-gate.py` with stdin JSON: `{"tool_input": {"command": "gh pr create --title 'test' --body 'test'"}}`. The hook reads `hook_input["tool_input"]["command"]` and checks for `"gh pr create"` substring.

**Assert:** Exit code 2. Stderr contains reason for block.

**Teardown:** Restore original state.

#### hook-pr-allow

**Tests:** PR gate hook exits 0 when all required steps are current.

**Setup:** Write complete state: `gates.passed` with current HEAD as `commit_ref`, `verify`, `qa`, `review` all with timestamps.

**Exercise:** Run PR gate hook with stdin JSON: `{"tool_input": {"command": "gh pr create --title 'test' --body 'test'"}}`.

**Assert:** Exit code 0.

**Teardown:** Restore original state.

#### state-invalidation

**Tests:** Post-commit hook clears `gates.passed` after a commit.

**Setup:** Write state with `gates.passed` set and a `commit_ref`.

**Exercise:** Run `python .claude/hooks/post-commit-state.py` with stdin simulating a successful `git commit` tool result.

**Assert:** Read state — `gates.passed` is null.

**Teardown:** Restore original state.

#### session-start-completeness

**Tests:** Session-start hook correctly reports which steps are missing from the "Required before PR" line based on state.

**Setup:** Write state with `phase=implementing`, `gates.passed` set (current commit ref), `verify.cli` set, but `qa={}` and `review={}`.

**Exercise:** Run `python .claude/hooks/session-start-workflow.py` and capture stderr.

**Assert:** Stderr "Required before PR" line includes `/qa` and `/review` but omits `/gates` and `/verify` (since those are already marked complete). Note: the session-start hook does not implement work-type-aware routing — it reports all incomplete steps regardless of work type. Work-type routing is handled by the `/start` skill's context file, not by this hook.

**Teardown:** Restore original state.

#### resume-detection

**Tests:** Session-start hook lists only the remaining incomplete steps when resuming from partial state.

**Setup:** Write state with `gates.passed` (current commit ref), `verify.cli` (timestamp), `qa.ext` (timestamp), but `review={}`.

**Exercise:** Run session-start hook, capture stderr.

**Assert:** Stderr "Required before PR" line includes `/review` but omits `/gates`, `/verify`, and `/qa` (all marked complete). This validates that a resumed session sees only what's left, not the full checklist.

**Teardown:** Restore original state.

### Primary Flow

**Run all scenarios:**

1. **Backup:** Copy `.workflow/state.json` to memory (or note its absence).
2. **Discover:** Collect all registered scenario functions.
3. **Execute:** For each scenario in order:
   a. Print scenario name to stderr.
   b. Run setup, exercise, assert, teardown.
   c. Record result: pass/fail, duration, detail on failure.
4. **Report:** Print JSON result object to stdout.
5. **Restore:** Write back original state (or delete if none existed).
6. **State write:** If all passed and running all scenarios (no args), call `workflow-state.py set verify.workflow now`. Single-scenario runs skip this.
7. **Exit:** 0 if all pass, 1 if any fail.

### Constraints

- No `shell=True` in any subprocess call (Constitution S2).
- No modification of hook scripts — test them as-is.
- State backup/restore must be atomic — if the script crashes mid-scenario, original state must be recoverable.
- Scenarios must not create git commits, branches, or worktrees. State-only.
- The script must handle missing `.workflow/` directory gracefully (create it for testing, remove on teardown if it didn't exist).

---

## Acceptance Criteria

| ID | Criterion | Test | Status |
|----|-----------|------|--------|
| AC-01 | `verify-workflow.py` with no args runs all registered scenarios and exits 0 when all pass | Manual: run with all hooks correct | 🔲 |
| AC-02 | `verify-workflow.py` exits 1 when any scenario fails | Manual: temporarily break a hook, run, verify exit 1 | 🔲 |
| AC-03 | `verify-workflow.py <name>` runs only the named scenario | Manual: run single scenario, verify only one result in output | 🔲 |
| AC-04 | `verify-workflow.py --list` prints scenario names to stdout, one per line | Manual: run --list, verify output | 🔲 |
| AC-05 | JSON output to stdout matches schema: `{scenarios: {name: {status, duration_ms, detail}}, summary: {total, passed, failed}, timestamp}` | Manual: pipe output through `python -m json.tool` | 🔲 |
| AC-06 | `hook-stop-block` scenario: stop hook blocks when phase=implementing and steps incomplete | Automated: scenario function assertion | 🔲 |
| AC-07 | `hook-stop-allow` scenario: stop hook allows for each non-enforcing phase (starting, investigating, design, reviewing, qa, shakedown, retro, pr) | Automated: scenario function assertion | 🔲 |
| AC-08 | `hook-pr-block` scenario: PR gate exits 2 when gates not current | Automated: scenario function assertion | 🔲 |
| AC-09 | `hook-pr-allow` scenario: PR gate exits 0 when all steps current | Automated: scenario function assertion | 🔲 |
| AC-10 | `state-invalidation` scenario: post-commit hook clears gates.passed | Automated: scenario function assertion | 🔲 |
| AC-11 | `session-start-completeness` scenario: session-start output lists only incomplete steps in "Required before PR" line | Automated: scenario function assertion | 🔲 |
| AC-12 | `resume-detection` scenario: session-start output identifies correct next pending step from partial state | Automated: scenario function assertion | 🔲 |
| AC-13 | Original `.workflow/state.json` is restored after all scenarios complete, even if a scenario fails | Manual: verify state unchanged after run | 🔲 |
| AC-14 | If no `.workflow/` directory exists before run, it is created for testing and removed on teardown | Manual: delete .workflow/, run, verify cleanup | 🔲 |
| AC-15 | No subprocess uses `shell=True` | Code review: grep for `shell=True` in verify-workflow.py | 🔲 |
| AC-16 | On all-pass with no args (all scenarios), writes `verify.workflow` timestamp to state via `workflow-state.py`; single-scenario runs do NOT write timestamp | Manual: run all, check state.json; run single, verify no timestamp written | 🔲 |
| AC-17 | Script works from worktree paths, not just main repo root | Manual: run from a .worktrees/ path | 🔲 |
| AC-18 | Status messages (scenario names, progress) go to stderr, not stdout | Manual: redirect stderr, verify stdout is pure JSON | 🔲 |
| AC-19 | Each scenario completes in under 5 seconds | Manual: check duration_ms in output | 🔲 |
| AC-20 | Failed scenario detail includes expected vs actual values | Manual: break a hook, verify detail field | 🔲 |

### Edge Cases

| Scenario | Input | Expected Output |
|----------|-------|-----------------|
| No `.workflow/` directory | Fresh repo, no state | Creates temp directory, runs scenarios, cleans up |
| Hook script missing | `.claude/hooks/session-stop-workflow.py` deleted | Scenario fails with clear error: "Hook not found: {path}" |
| Invalid scenario name | `verify-workflow.py nonexistent` | Exit 1, stderr: "Unknown scenario: nonexistent. Use --list." |
| No code changes on branch | Stop hook scenarios on docs-only branch | Stop hook scenarios skip with "prerequisite not met: no code changes vs origin/main" |
| Hook returns unexpected exit code | Exit 137 (killed) | Scenario fails with "Unexpected exit code: 137" |

---

## Core Types

### Scenario Result

```python
@dataclass
class ScenarioResult:
    status: str          # "pass" or "fail"
    duration_ms: int     # wall-clock milliseconds
    detail: str | None   # failure detail, None on pass
```

### Report

```python
@dataclass
class Report:
    scenarios: dict[str, ScenarioResult]
    summary: Summary
    timestamp: str       # ISO 8601

@dataclass
class Summary:
    total: int
    passed: int
    failed: int
```

### Usage Pattern

```python
# Register a scenario
@scenario("hook-stop-block")
def test_stop_hook_blocks(ctx: ScenarioContext) -> ScenarioResult:
    ctx.write_state({"phase": "implementing", "gates": {"passed": None}, "stop_hook_count": 0})
    result = ctx.run_hook("session-stop-workflow.py", stdin_json={})
    # Stop hook always exits 0; block/allow communicated via JSON on stdout
    output = json.loads(result.stdout)
    assert output.get("decision") == "block"
    return ScenarioResult(status="pass", duration_ms=ctx.elapsed_ms(), detail=None)
```

---

## Error Handling

### Error Types

| Error | Condition | Recovery |
|-------|-----------|----------|
| Hook not found | Script path doesn't exist | Scenario fails, reports missing path |
| State write failure | Permission or disk error | Fail fast, attempt state restore |
| Subprocess timeout | Hook hangs beyond 10s | Kill process, scenario fails with timeout detail |
| JSON parse error | Hook stdout isn't valid JSON | Scenario fails, includes raw stdout in detail |

### Recovery Strategies

- **State restore:** Always runs in a `finally` block. If restore fails, prints warning to stderr with the backup content so the user can manually recover.
- **Subprocess timeout:** 10-second timeout per hook invocation. Uses `subprocess.run(timeout=10)`.

---

## Design Decisions

### Why no daemon?

**Context:** ext-verify and tui-verify use a daemon pattern because they manage a long-lived process (VS Code, TUI PTY) across multiple commands.

**Decision:** No daemon. Each scenario is a short-lived subprocess invocation against a hook script.

**Alternatives considered:**
- Daemon pattern (matching ext-verify): Rejected — no long-lived process to manage. Adds complexity for no benefit.

**Consequences:**
- Positive: Simpler architecture, no orphan cleanup, no session file management
- Negative: Cannot test behaviors that require a live claude session (those belong in `/shakedown-workflow`)

### Why test hooks via subprocess, not import?

**Context:** Hook scripts could be tested by importing them as Python modules.

**Decision:** Test via `subprocess.run` — the same way Claude Code invokes them.

**Alternatives considered:**
- Import and call functions directly: Rejected — hooks are designed as standalone scripts with stdin/stdout/stderr contracts. Testing via subprocess validates the actual invocation path.

**Consequences:**
- Positive: Tests the real execution path including exit codes, stderr, env vars
- Negative: Slightly slower than in-process calls (negligible at <100ms per scenario)

### Why write verify.workflow on pass?

**Context:** The `/verify` skill checks `verify.workflow` timestamp in state to know if workflow verification has been done.

**Decision:** `verify-workflow.py` writes the timestamp itself when all scenarios pass with `--all`.

**Alternatives considered:**
- Let `/verify workflow` write it after calling the script: Rejected — the script knows its own result. Writing the timestamp at the source is more reliable.

**Consequences:**
- Positive: Single responsibility — the tool reports its own completion
- Negative: Script must know about `workflow-state.py` (acceptable coupling — it already tests state management)

---

## Integration Points

### `/verify workflow` (structural layer)

After its existing 7 structural checks, `/verify workflow` calls `python scripts/verify-workflow.py` as check 8. If the behavioral tests fail, `/verify workflow` reports the failure.

### `/workflow-verify` skill

The skill document is updated to reference the tool:
- "Run `python scripts/verify-workflow.py` for automated behavioral tests"
- Manual testing patterns (hook testing, pipeline dry-run) remain for ad-hoc use

### `/shakedown-workflow` (integration layer)

Calls `python scripts/verify-workflow.py` inside each throwaway worktree. The worktree inherits the current branch's hooks and scripts, so the scenarios test the modified code.

---

## Related Specs

- [workflow-enforcement.md](./workflow-enforcement.md) — The system being tested
- [ext-verify-tool.md](./ext-verify-tool.md) — Pattern precedent (extension surface verification)
- [tui-verify-tool.md](./tui-verify-tool.md) — Pattern precedent (TUI surface verification)

---

## Changelog

| Date | Change |
|------|--------|
| 2026-03-28 | Initial spec |

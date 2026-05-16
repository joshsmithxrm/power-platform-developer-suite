# bg-spawn Autonomy — `--permission-mode bypassPermissions` across all spawn paths

**Spec version:** 1.1  
**Issue:** #1067 (epic #1066 Phase 1a)  
**Code:** `scripts/claude_dispatch.py`, `scripts/start-bg-spawn.py`, `scripts/pr_monitor.py`, `scripts/triage_common.py`, `.claude/skills/start/SKILL.md`, `.claude/hooks/pr-gate.py`

---

## Background

Supervisor-worker autonomy (epic #1066) requires background sessions to run without operator permission prompts. Claude Code exposes `--permission-mode bypassPermissions` for this purpose. PR #1075 added opt-in `permission_mode` to `start-bg-spawn.spawn()`. This spec covers the remaining delta: plumbing the parameter through `claude_dispatch.spawn()`, updating all bg-dispatch call sites, and fixing two related pr-gate hook detection gaps.

---

## Problem Statements

### P-1: `claude_dispatch.spawn()` cannot express `--permission-mode`

`spawn()` has a `dangerous=True` escape hatch that emits `--dangerously-skip-permissions`. It does not expose `--permission-mode <mode>`. Sub-agents dispatched via `run_triage`, `run_retro`, and `dispatch_subagent` cannot receive `bypassPermissions` and will pause at permission prompts, surfacing as `BlockedSessionError` with no operator to respond.

### P-2: `/start` skill spawns workers without `bypassPermissions`

The `/start` SKILL.md step 6c invokes `start-bg-spawn.py` without `--permission-mode bypassPermissions`. Workers spawned by `/start` pause at the first permission prompt.

### P-3: pr-gate hook misses two agent-context patterns

`_is_agent_context()` uses `os.getcwd()` to detect worktree context. Claude Code invokes hook subprocesses with CWD = main repo root; the session's actual working directory lives in `CLAUDE_PROJECT_DIR`. Two patterns fail to be detected:

**P-3a — Nested bg-spawned worktrees (primary issue scope):** A bg session in `.worktrees/<outer>` spawns a sub-agent whose worktree is `.worktrees/<outer>/worktree-<inner>`. The hook process CWD is the main repo root; `CLAUDE_PROJECT_DIR` is the nested path (which contains `/.worktrees/`). Without checking `CLAUDE_PROJECT_DIR`, `_is_agent_context()` returns False.

**P-3b — Foreground sessions in `.claude/worktrees/<name>` (observed in PR #1095):** Same root cause; `CLAUDE_PROJECT_DIR` contains `/.claude/worktrees/` but `os.getcwd()` is the main repo root. Observed when a foreground Claude Code session in `.claude/worktrees/peaceful-bartik-86c8cc` ran raw `gh pr create` and was not blocked.

Both gaps are fixed by the same one-line change: also check `CLAUDE_PROJECT_DIR` in `_is_agent_context()`.

---

## Acceptance Criteria

| AC | Description | Test |
|----|-------------|------|
| AC-01 | `claude_dispatch.spawn()` accepts a `permission_mode: Optional[str]` keyword parameter | `tests/scripts/test_claude_dispatch.py::test_spawn_permission_mode_threaded` |
| AC-02 | When `permission_mode` is non-None, `--permission-mode <value>` appears in the `claude --bg` argv before `--` | `tests/scripts/test_claude_dispatch.py::test_spawn_permission_mode_threaded` |
| AC-03 | When `permission_mode` is None (default), `--permission-mode` does NOT appear in the argv | `tests/scripts/test_claude_dispatch.py::test_spawn_permission_mode_absent_when_none` |
| AC-04 | `claude_dispatch.spawn()` `dangerous` parameter continues to work unchanged (backward compat) | `tests/scripts/test_claude_dispatch.py::test_spawn_dangerous_still_works` |
| AC-05 | `pr_monitor.run_triage` spawns with `permission_mode='bypassPermissions'` | `tests/test_pr_monitor.py::test_run_triage_uses_bypassPermissions` |
| AC-06 | `pr_monitor.run_retro` spawns with `permission_mode='bypassPermissions'` | `tests/test_pr_monitor.py::test_run_retro_uses_bypassPermissions` |
| AC-07 | `triage_common.dispatch_subagent` spawns with `permission_mode='bypassPermissions'` | `tests/test_triage_common.py::test_dispatch_subagent_uses_bypassPermissions` |
| AC-08 | `/start` SKILL.md step 6c includes `--permission-mode bypassPermissions` in the `start-bg-spawn.py` invocation | `tests/scripts/test_start_skill_text.py::test_start_skill_spawn_includes_permission_mode` |
| AC-09 | `_is_agent_context()` returns True when `CLAUDE_PROJECT_DIR` contains `/.claude/worktrees/` even if `os.getcwd()` does not (P-3b) | `tests/hooks/test_pr_gate_agent_enforcement.py::test_is_agent_context[project_dir_claude_worktrees]` |
| AC-10 | `_is_agent_context()` returns True when `CLAUDE_PROJECT_DIR` contains `/.worktrees/` even if `os.getcwd()` does not (P-3a/P-3b shared) | `tests/hooks/test_pr_gate_agent_enforcement.py::test_is_agent_context[project_dir_worktrees]` |
| AC-11 | `_is_agent_context()` returns True when `CLAUDE_PROJECT_DIR` is a nested-bg-worktree path `.worktrees/<outer>/worktree-<inner>` and `os.getcwd()` is the main repo root (P-3a primary scope) | `tests/hooks/test_pr_gate_agent_enforcement.py::test_is_agent_context[nested_bg_worktree]` |
| AC-12 | Foreground session in `.claude/worktrees/<name>`: hook called with CWD=main-root, `CLAUDE_PROJECT_DIR`=worktree → blocked with exit 2 when no `/pr` skill marker | `tests/hooks/test_pr_gate_agent_enforcement.py::test_foreground_worktree_via_project_dir_blocked` |
| AC-13 | `PPDS_PR_GATE_HUMAN=1` override continues to suppress agent enforcement even when `CLAUDE_PROJECT_DIR` is a worktree path | `tests/hooks/test_pr_gate_agent_enforcement.py::test_human_override_beats_project_dir` |
| AC-14 | **Smoke test (manual QA):** A worker spawned by `/start` with `bypassPermissions` completes `/implement → /gates → /verify → /pr` end-to-end without any operator permission prompts and without bypassing PR creation via raw `gh pr create` | Manual — verify via `/shakedown workflow` or a real throwaway worktree after pipeline passes |

---

## Design

### D-1: `claude_dispatch.spawn()` — add `permission_mode` parameter

Add `permission_mode: Optional[str] = None` to the interactive-mode code path only (headless sessions use the `-p` API which does not accept `--permission-mode`). Insert the flag before `--` in argv construction:

```python
argv = ["claude", "--bg", "--name", name]
if permission_mode:
    argv.extend(["--permission-mode", permission_mode])
if dangerous:
    argv.append("--dangerously-skip-permissions")
argv.extend(["--", prompt])
```

`dangerous` is kept for backward compatibility. No caller should need both simultaneously, but there is no validation error if both are passed.

### D-2: Update `pr_monitor.run_triage` and `run_retro`

Both call `claude_dispatch.spawn(dangerous=True, ...)`. Change to `permission_mode='bypassPermissions'` and remove `dangerous=True`. These are fully-automated sessions where permission prompts surface as `BlockedSessionError` with no operator to respond.

### D-3: Update `triage_common.dispatch_subagent`

Same as D-2: replace `dangerous=True` with `permission_mode='bypassPermissions'`.

### D-4: `/start` skill step 6c

Append `--permission-mode bypassPermissions` to the `start-bg-spawn.py` CLI invocation in SKILL.md:

```bash
python scripts/start-bg-spawn.py \
  --worktree-abs "<worktree-absolute-path>" \
  --branch "<branch>" \
  --prompt-file "<temp-path>" \
  --permission-mode bypassPermissions
```

### D-5: pr-gate `_is_agent_context()` — check `CLAUDE_PROJECT_DIR`

Root cause (both P-3a and P-3b): Claude Code spawns hook subprocesses with CWD = main repo root, but sets `CLAUDE_PROJECT_DIR` to the session's actual working directory. The fix checks `CLAUDE_PROJECT_DIR` in addition to `os.getcwd()`:

```python
# Also check CLAUDE_PROJECT_DIR — hook subprocess CWD may be the main repo
# root rather than the session worktree (covers nested bg-spawned worktrees
# at .worktrees/<outer>/worktree-<inner> and foreground .claude/worktrees/<n>).
proj_dir = (env if env is not None else os.environ).get("CLAUDE_PROJECT_DIR", "")
if proj_dir:
    norm_proj = proj_dir.replace("\\", "/")
    if "/.worktrees/" in norm_proj or "/.claude/worktrees/" in norm_proj:
        return True
```

This covers all three patterns in a single check:
- `.worktrees/<name>` (existing pattern, now also via CLAUDE_PROJECT_DIR)
- `.worktrees/<outer>/worktree-<inner>` (nested bg-worktree, P-3a — `/.worktrees/` matches the outer segment)
- `.claude/worktrees/<name>` (foreground Claude Code worktrees, P-3b)

The `env` parameter already exists on `_is_agent_context()`; the fix uses it consistently.

---

## Non-goals

- Changing `dangerous=True` semantics or removing it — backward compat preserved.
- Adding `permission_mode` to the headless (`-p`) path — headless sessions have no permission dialog.
- Supervisor-worker topology (#1069) — separate epic phase.

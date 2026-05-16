# fix-1122-subagent-hang

**Status:** Draft
**Last Updated:** 2026-05-16
**Issue:** #1122 (epic #1066, Phase 2)
**Code:** `scripts/pr_monitor.py` | `scripts/test_pr_monitor.py`
**Surfaces:** N/A (workflow tooling)

---

## Overview

`pr_monitor.run_triage` spawns the gemini-triage sub-agent using interactive mode (`claude --bg`). After the sub-agent completes its work, the `claude --bg` daemon does not reliably transition `state=done` in its state.json. `BgHandle.wait()` polls for `state=done`, which may never arrive, causing a 30-minute hang followed by a `DispatchError` timeout. The fix is to force headless mode (`claude -p`) for `run_triage`, which exits cleanly after model output.

---

## Root Cause Investigation

### Evidence — Session 23b24e75 (PR #1119, 2026-05-16)

| Timestamp | Event |
|---|---|
| 07:50:39 | Session spawned — `claude --bg --name gemini-triage --permission-mode bypassPermissions` |
| 07:50:45–07:51:18 | Sub-agent reads file, applies fix, commits (`838ba35a3`), git pushes (success) |
| 07:51:21 | Model emits final JSON output |
| 07:51:23 | `stop_hook_summary`: 3 hooks ran, `preventedContinuation: false`, `hookErrors: []` |
| 07:51:23 | `turn_duration`: 42382ms — session turn complete |
| 07:51:23–08:20:41 | **`state.json` frozen at `state=working`. No further daemon updates.** |
| 08:20:41 | `BgHandle.wait` raised `DispatchError("timed out after 1800s for 23b24e75")` |
| 08:23:32 | Session eventually transitioned to `state=stopped` (unknown external trigger) |

**Three additional sessions** show the same pattern: `07f386cb`, `1071e54d`, `2112af19` — completed transcripts (`turn_duration` emitted), daemon state frozen at `working`, `firstTerminalAt: None`.

### Hypotheses Evaluated

**H1 — gh api / git push call blocked**: Ruled out. Transcript confirms git push returned at 07:51:18, model output at 07:51:21, turn complete at 07:51:23. No blocking call after model output.

**H2 — stdout/stdin pipe EOF**: Not applicable. Interactive mode uses no pipe; this hypothesis applies only to headless mode.

**H3 — Interactive prompt despite bypassPermissions**: Ruled out. `preventedContinuation: false` — no hook blocked continuation. Transcript contains no additional model turn after the first.

**H4 — claude --bg daemon lifecycle (CONFIRMED)**: After the model's first turn completes, the `claude --bg` daemon does not reliably transition `state` → `done`. The daemon is designed for multi-turn interaction and remains alive waiting for the next turn. `BgHandle.wait()` polling for `state=done` is not a reliable termination signal for single-turn dispatches. The `state=done` transition is non-deterministic — it occurs for some sessions immediately and never for others.

### Secondary Bug

`run_triage` catches `BlockedSessionError` and `subprocess.TimeoutExpired` but not `claude_dispatch.DispatchError`. When `BgHandle.wait()` times out it raises `DispatchError`, which propagates up to `_step_triage` as a generic exception rather than being handled cleanly. This causes the triage step to be marked `error` without calling `handle.terminate()`.

---

## Fix

Force `run_triage` to use `mode="headless"` when calling `claude_dispatch.spawn()`. Headless mode runs `claude -p`, which exits after the model produces its output. `HeadlessHandle.wait(timeout=1800)` uses `proc.wait(timeout=1800)`, raising `subprocess.TimeoutExpired` on timeout (already caught).

- `--agent gemini-triage` IS passed in headless mode — tool restrictions from the agent profile apply.
- `permission_mode` is not applicable in headless mode (no permission dialog); it is silently ignored by `spawn()`.
- `stage_log` is already provided in `run_triage` and is used by headless mode to capture stdout.
- The post-wait transcript copy (shutil.copyfile) remains; in headless mode `handle.transcript_path == stage_jsonl_path`, so the `if` condition is false and the copy is skipped — correct behavior.

No changes to `claude_dispatch.BgHandle`, `BgHandle.wait()`, or any caller outside `run_triage`.

---

## Acceptance Criteria

| # | AC | Test |
|---|----|----|
| AC-01 | `run_triage` calls `claude_dispatch.spawn(mode="headless", ...)` | `test_triage_uses_headless_mode` |
| AC-02 | `run_triage` calls spawn with `agent="gemini-triage"` and `model="haiku"` (unchanged) | `test_triage_uses_headless_mode` + `test_triage_uses_haiku` |
| AC-03 | When the headless process exits normally (exit 0), `run_triage` returns the parsed triage results | `test_triage_uses_headless_mode` |
| AC-04 | When the headless process times out, `subprocess.TimeoutExpired` is caught and `None` is returned | `test_triage_timeout_returns_none` |
| AC-05 | All existing `test_pr_monitor.py` tests pass without modification | run test suite |
| AC-06 | CI-timeout default (PR #1089, `ci_timeout_sec`) is unaffected — separate concern, no shared state | code review |
| AC-07 | `run_retro` calls `claude_dispatch.spawn(mode="headless", ...)` | `test_retro_uses_headless_mode` |
| AC-08 | `dispatch_subagent` calls `claude_dispatch.spawn(mode="headless", ...)` regardless of the `mode` parameter | `test_dispatch_subagent_uses_headless_mode` |

---

## Out of Scope

- The latent `DispatchError` exception handling gap in `run_triage` / `run_retro` (uncaught timeout from `BgHandle.wait`) becomes moot after the headless switch — `BgHandle.wait` is no longer called from these paths. Note for retro.
- Changes to `BgHandle.wait()` or `claude_dispatch.py` are not needed for this fix.
- No changes to `CLAUDE.md`, `CONSTITUTION.md`, or `interaction-patterns.md`.

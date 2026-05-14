# Start Session Launch

**Status:** Draft
**Last Updated:** 2026-05-13
**Code:** [.claude/skills/start/](../.claude/skills/start/), [scripts/start-bg-spawn.py](../scripts/start-bg-spawn.py)
**Surfaces:** N/A (workflow tooling)

---

## Overview

`/start` creates a worktree and launches a fresh Claude Code session with the operator's task as the inline prompt. This spec covers the **launch** half of that flow — how the new session is spawned, how its identity is captured, and how it becomes discoverable.

Today's launcher (`scripts/launch-claude-session.py`) spawns a PowerShell window with a here-string-wrapped prompt. That path carries every scar from issue #799: PowerShell `'@` line-start terminator traps, `-File` vs `-Command` TTY behavior, fnm/nvm absolute-path resolution. It is Windows-only with a manual-paste fallback on macOS/Linux/WSL2.

Claude Code 2.1.139+ ships a documented background-launch primitive — `claude --bg "<prompt>"` — that backgrounds the new session into the local Claude daemon and surfaces it natively in Agent View (the multi-session dashboard introduced in the same release). This spec replaces the PowerShell path with that primitive.

### Goals

- **Native Agent View integration**: new sessions spawned by `/start` appear as Agent View rows on every platform Claude Code supports
- **Single cross-platform code path**: no PowerShell-specific spawn, no per-platform fallback prose
- **Eliminate #799 fragility classes**: no here-string escaping, no TTY-via-`-Command` bug, no shim-resolution surprises
- **Identity capture**: the new session's daemon short ID is recorded in the cross-session registry so sibling sessions detect overlap

### Non-Goals

- Changing what `/start` does before launch (parsing, worktree creation, work-type classification, conflict check) — see `.claude/skills/start/SKILL.md` Steps 1–5 and the existing `scripts/worktree-create.py`
- Changing `scripts/pipeline.py` headless calls — they keep using `claude -p` per epic #1025 decision
- Opening any new interactive terminal window — operators drive the new session from Agent View. Ad-hoc shell at the worktree path remains operator-initiated (`cd .worktrees/<name>`)
- Supporting Claude Code < 2.1.139 — older versions get a clear upgrade-required error

---

## Architecture

```
operator types /start
   │
   ▼
.claude/skills/start/SKILL.md
   │ Steps 1–5  (unchanged: parse, classify, propose, conflict-check, worktree-create.py)
   │
   ▼
scripts/start-bg-spawn.py    ← NEW (small helper, ~80 LOC)
   │
   │  1. _require_min_version("2.1.139")
   │  2. subprocess.run(["claude","--bg","--name",<branch>,"--",<prompt>],
   │                    cwd=<worktree-abs>, capture_output=True)
   │  3. _parse_banner(stdout)              → short_hint (may be None)
   │  4. _identify_session(short_hint, cwd) → state.json (5s poll)
   │  5. assert state["cwd"] == cwd; else `claude stop` and fail
   │  6. emit {"short": ..., "sessionId": ..., "cwd": ...} on stdout
   │
   ▼
scripts/inflight-register.py --session <daemonShort> ...   (unchanged)
   │
   ▼
.claude/state/in-flight-issues.json updated
   │
   ▼
Operator sees new row in Agent View (any surface: CLI / desktop / IDE / Slack)
```

### Components

| Component | Role | Change |
|---|---|---|
| `.claude/skills/start/SKILL.md` | Skill instructions | Steps 6, 6a–6d, 7 rewritten; Rule 8 (file vs inline handoff) reworded to reflect the new spawn |
| `scripts/start-bg-spawn.py` | Launch helper | **New.** Version gate, spawn, banner parse, state-file lookup |
| `scripts/launch-claude-session.py` | Old PowerShell launcher | **Deleted** |
| Tests for the old launcher | (paths discovered during /implement) | **Deleted** |
| `scripts/inflight-register.py` | Cross-session registry write | Unchanged — `--session <8-hex>` already accepts the daemon short ID |
| `scripts/worktree-create.py` | Worktree creation | Unchanged |
| `~/.claude/jobs/<short>/state.json` | Daemon-owned per-session state | **Read-only consumed.** Source of truth for `sessionId` (full UUID) and `cwd` post-spawn |

### Dependencies

- Depends on: Claude Code ≥2.1.139 (provides `claude --bg`, `claude attach`, `claude stop`, `claude logs`, and the `~/.claude/jobs/` daemon state layout)
- Sibling: epic #1025 (Adopt Claude Agents Dashboard) — Agent View declared the observability surface
- Sibling: #1026 (CLAUDE.md / interaction-patterns Agent View doc) — best landed first but not a hard block

---

## Specification

### Core Requirements

1. The launch helper spawns the new session via `claude --bg --name <branch> -- <prompt>` with `cwd=<worktree-abs-path>`. The `--` separator terminates flag parsing so prompts beginning with `-` (e.g., `--help test`) are not interpreted as options. No PowerShell, no shell interpolation, no `.ps1` artifact.
2. The prompt is passed as a single argv element. No escaping is applied — the prompt is delivered byte-for-byte.
3. The helper identifies the new session via a single 5-second poll loop (100 ms interval): on each tick, if the spawn's stdout banner yielded a short ID, check `~/.claude/jobs/<short>/state.json`; otherwise scan `~/.claude/jobs/*/state.json` for an entry whose `cwd` matches the requested worktree path and whose `createdAt` is within the last 10 seconds. First match wins.
4. The helper reads `~/.claude/jobs/<short>/state.json` to obtain the canonical full UUID `sessionId` and to verify `cwd` matches the requested worktree path.
5. The helper emits a single JSON object on stdout: `{"short": "<8-hex>", "sessionId": "<uuid>", "cwd": "<abs-path>"}`. Diagnostics go to stderr (Constitution I1).
6. The skill calls `inflight-register.py --session <short> ...` to record the session in `.claude/state/in-flight-issues.json`.
7. No interactive terminal window is opened. The skill's Step 7 summary directs the operator to Agent View.
8. If `claude --version` parses below `2.1.139`, the helper exits with a non-zero code and a one-line stderr message instructing the operator how to upgrade. No fallback path executes.
9. `scripts/launch-claude-session.py` and its tests are removed from the repository.

### Primary Flow

**Spawn:**

1. **Version probe** — `claude --version` → parse "X.Y.Z" out of "X.Y.Z (Claude Code)". Reject if `< 2.1.139`.
2. **Spawn** — `subprocess.run(["claude","--bg","--name",branch,"--",prompt], cwd=worktree_abs, capture_output=True, text=True, timeout=30)`.
3. **Parse banner (best effort)** — strip ANSI sequences from `proc.stdout` (regex `\x1b\[[0-9;]*m` → empty), then match `r"backgrounded\s+·\s+([0-9a-f]{8})\b"`. On match: `short_hint = <id>`. On miss: `short_hint = None`.
4. **Identify session (5 s poll, 100 ms interval):**
   - If `short_hint` set, look up `~/.claude/jobs/<short_hint>/state.json` and parse if present.
   - Otherwise scan `~/.claude/jobs/*/state.json`, pick the entry whose `cwd` (normalized) matches `worktree_abs` AND whose `createdAt` is within the last 10 seconds.
   - First successful parse with `sessionId` populated wins. Exhausting the 5-second budget → `StateFileTimeout`.
5. **cwd assert** — `state["cwd"]` (normalized: forward slashes, no trailing separator) must equal `worktree_abs` (same normalization). Mismatch → call `claude stop <short>`, exit 2 with `CwdMismatch`.
6. **Emit** — write JSON result to stdout, exit 0.

### Surface-Specific Behavior

N/A — this is workflow tooling, not a user-facing surface. The skill (`/start`) is the only caller.

### Constraints

- The helper must not use `shell=True` (Constitution S2). `subprocess.run(argv_list, ...)` only.
- Stdout reserved for the result JSON. All status, errors, and diagnostics go to stderr (Constitution I1).
- The helper must not write any file under `.plans/`, the worktree, or anywhere else as a handoff. The prompt is delivered solely via the `claude --bg` argv. (`~/.claude/jobs/<short>/` is daemon-owned; we read it, we don't write it.)
- Banner parse must tolerate ANSI color sequences. Today's banner emits cyan (`\x1b[36m...\x1b[39m`) around the short ID.
- The 5-second `state.json` poll budget must not be tightened — on cold-cache daemon starts the file can take ~2 seconds to appear.
- The version comparator must understand pre-release suffixes (e.g., `2.1.139-beta.1` is acceptable; `2.1.138` is not). Use a numeric tuple compare on the leading `MAJOR.MINOR.PATCH`.

### Validation Rules

This table covers argument and pre-spawn validation only — its rows are a subset of the canonical Error Types table further down (which also enumerates runtime / daemon failures). Exit 1 = caller / pre-spawn errors (operator can fix and rerun). Exit 2 = post-spawn / daemon errors (unrecoverable in-process).

| Field | Rule | Error message | Exit |
|-------|------|---------------|------|
| `--worktree-abs` | Must be an absolute path that exists and is a directory | `worktree path does not exist: <path>` | 1 |
| `--branch` | Non-empty; matches `^[A-Za-z0-9/_.-]+$` (passed to `--name`) | `--branch must be non-empty and contain only [A-Za-z0-9/_.-]` | 1 |
| `--prompt-file` | File must exist and contain non-empty UTF-8 text after strip | `prompt file is empty or missing` | 1 |
| `claude --version` output | Parse `MAJOR.MINOR.PATCH` from prefix; `≥ 2.1.139` (numeric tuple compare; pre-release suffixes ignored) | `/start requires Claude Code ≥2.1.139 (found <X.Y.Z>). Update via 'npm i -g @anthropic-ai/claude-code' and rerun.` | 1 |
| `state.json.cwd` post-spawn | Normalized (forward slashes, no trailing separator) equality with `--worktree-abs` | `daemon cwd mismatch: expected <a>, got <b>` | 2 |

---

## Acceptance Criteria

| ID | Criterion | Test | Status |
|----|-----------|------|--------|
| AC-01 | `start-bg-spawn.py` rejects with exit 1 and a stderr message naming both the minimum version (`2.1.139`) and the upgrade command (`npm i -g @anthropic-ai/claude-code`) when `claude --version` parses below 2.1.139 | `test_start_bg_spawn.py::test_rejects_old_version` (mocks `subprocess.run(['claude','--version'])`; asserts both substrings appear in stderr) | ✅ |
| AC-02 | `start-bg-spawn.py` invokes `subprocess.run` with argv exactly `["claude","--bg","--name",<branch>,"--",<prompt>]` and `cwd=<worktree-abs>`, never `shell=True`. The `--` separator is mandatory so prompts starting with `-` are not parsed as flags. | `test_start_bg_spawn.py::test_spawn_argv` (mocks subprocess, asserts call signature includes `--` immediately before prompt) | ✅ |
| AC-03 | Banner parser extracts the 8-hex daemon short ID from `backgrounded · <id>` output, both with and without ANSI color codes, via ANSI-strip-then-match (single strategy, no branching) | `test_start_bg_spawn.py::test_parse_banner_{plain,ansi}` | ✅ |
| AC-04 | When the banner is unparseable, the identify-session step scans `~/.claude/jobs/*/state.json` and selects the entry whose `cwd` matches the requested worktree path and whose `createdAt` is within the last 10 seconds | `test_start_bg_spawn.py::test_fallback_id_lookup_{mtime,created_at}` (uses tmp_path with fixture state.json files exercising both the mtime-fallback path and the `createdAt`-ISO-8601 path) | ✅ |
| AC-05 | The identify-session step polls for up to 5 seconds at 100 ms intervals; if no matching state.json appears within that budget, the helper exits 2 with `StateFileTimeout` in stderr | `test_start_bg_spawn.py::test_state_json_poll_timeout` (timer-mocked; verifies exit code and stderr substring) | ✅ |
| AC-06 | Post-spawn `state.cwd` mismatch causes `claude stop <short>` to be invoked and the helper to exit 2 with `CwdMismatch` in stderr | `test_start_bg_spawn.py::test_cwd_mismatch_stops_session` (asserts subprocess.run called with `["claude","stop",<short>]`) | ✅ |
| AC-07 | The helper emits exactly one line to stdout — a JSON object with keys `short`, `sessionId`, `cwd` — and all other output goes to stderr | `test_start_bg_spawn.py::test_stdout_is_json_only` (asserts `len(stdout.splitlines())==1` and `json.loads(stdout)` returns dict with the three keys) | ✅ |
| AC-08 | Spawning with a 5,000-character prompt containing `'@` at line start, apostrophes, `$prompt`, and backticks results in the spawned session's `~/.claude/jobs/<short>/state.json` `intent` field being byte-identical to the input prompt | `test_start_bg_spawn_integration.py::test_prompt_verbatim_5k` (marker `requires_claude_bg`; reads `state.json` and asserts `state["intent"] == prompt`) | ⚠️ |
| AC-09 | `.claude/skills/start/SKILL.md` Step 6 contains at least one Bash code-fence block invoking `python scripts/start-bg-spawn.py`, and Step 7's summary template names `Agent View` as the place the operator drives the new session | `test_start_skill_text.py::test_step6_invokes_helper` (parses SKILL.md, locates the `### Step 6` heading — H3, matching existing convention — asserts at least one ```bash``` fence within that section contains `python scripts/start-bg-spawn.py`) and `::test_step7_names_agent_view` (parses `### Step 7`, asserts the summary template literal contains `Agent View`) | ✅ |
| AC-10 | After implementation, `scripts/launch-claude-session.py` does not exist and no file under `.claude/`, `scripts/`, `tests/`, or `specs/` (excluding this spec file and any Changelog rows) contains the literal substring `launch-claude-session` | `test_start_skill_text.py::test_launcher_purged` (Python-driven check: `assert not Path("scripts/launch-claude-session.py").exists()`, then walks the four scoped directories with explicit excludes and asserts zero matches outside the spec file) | ✅ |
| AC-11 | The daemon short ID returned from `start-bg-spawn.py` is recorded as the `session_id` field of the new `.claude/state/in-flight-issues.json` entry created by `/start` | `test_start_skill_integration.py::test_session_id_recorded` (end-to-end with mocked spawn; asserts the registry entry's `session_id` equals the daemon short ID, not a random hex token) | ✅ |
| AC-12 | When the version probe raises `FileNotFoundError` (i.e., `claude` is not on PATH), `start-bg-spawn.py` exits 1 and the stderr message contains both the literal `Claude Code` and the install command `npm i -g @anthropic-ai/claude-code` | `test_start_bg_spawn.py::test_claude_not_on_path` (mocks `subprocess.run(['claude','--version'])` to raise `FileNotFoundError`; asserts both substrings appear in stderr and exit code is 1) | ✅ |

Status key: ✅ covered by passing test · ⚠️ test exists but failing · ❌ no test yet · 🔲 not yet implemented

**I6 compliance gate:** Every AC above must flip from ❌ to ✅ before this spec ships. Constitution I6 prohibits untested ACs at merge time — Draft status acknowledges the work is incomplete; the gate applies when the spec leaves Draft.

### Edge Cases

| Scenario | Input | Expected Output |
|----------|-------|-----------------|
| Empty prompt file | 0-byte or whitespace-only `--prompt-file` | Exit 1 — `PromptEmpty` |
| `claude` not on PATH | `subprocess.run(["claude","--version"])` raises `FileNotFoundError` | Exit 1 — `ClaudeNotFound`; stderr names install command |
| `--bg` rejected by old `claude` | Spawn returns non-zero with stderr mentioning unknown option (version probe should have caught this; defense in depth) | Exit 2 — `SpawnFailed`; stderr verbatim |
| `state.json` never appears within 5 s | Daemon crashed or disk full | Exit 2 — `StateFileTimeout`; stderr names the short ID (if banner parsed) or the worktree path (if not) |
| Banner contains ANSI codes the regex misses | Future `claude` change to banner format | Identify-session step's cwd-scan branch succeeds via `~/.claude/jobs/*/state.json`; behavior identical |
| Two `/start` invocations targeting **different** worktrees within 10 s | Each spawn writes its own `~/.claude/jobs/<short>/` entry | Identify-session step's cwd-scan matches on `state.cwd` — paths are necessarily distinct because each `/start` invocation creates a fresh worktree path under `.worktrees/<unique-name>` |
| Two `/start` invocations targeting the **same** worktree path | Step 4 (Check for Existing Worktree) in `.claude/skills/start/SKILL.md` already refuses or offers resume before reaching the launch step | Not reachable from `start-bg-spawn.py`; the skill is the gate |
| Worktree path contains spaces or apostrophes | `cwd="C:/Users/O'Brien/repo/.worktrees/x"` | Works — argv-based spawn, no shell quoting |
| Prompt contains literal `'@` at line start | (#799 trap) | Works — argv-based spawn, no PowerShell here-string |
| Prompt begins with `-` or `--` | e.g., prompt literally starts `--help debug this`  | The `--` separator in `["claude","--bg","--name",branch,"--",prompt]` terminates flag parsing; prompt arrives verbatim (verified by probe 2026-05-13) |

### Test Examples

```python
# tests/scripts/test_start_bg_spawn.py
def test_parse_banner_ansi():
    raw = "backgrounded · \x1b[36mabc12345\x1b[39m\n  claude attach abc12345...\n"
    assert parse_banner(raw) == "abc12345"

def test_rejects_old_version(monkeypatch):
    monkeypatch.setattr(subprocess, "run", lambda *a, **kw: CompletedProcess(a, 0, "2.1.138 (Claude Code)\n", ""))
    with pytest.raises(SystemExit) as exc:
        require_min_version("2.1.139")
    assert exc.value.code == 1

def test_spawn_argv(monkeypatch, tmp_path):
    seen = {}
    def fake_run(argv, **kw):
        seen["argv"] = argv
        seen["cwd"] = kw.get("cwd")
        seen["shell"] = kw.get("shell", False)
        return CompletedProcess(argv, 0, "backgrounded · abc12345\n", "")
    monkeypatch.setattr(subprocess, "run", fake_run)
    spawn(worktree_abs=str(tmp_path), branch="feat/x", prompt="hello")
    assert seen["argv"] == ["claude","--bg","--name","feat/x","--","hello"]
    assert seen["cwd"] == str(tmp_path)
    assert seen["shell"] is False
```

---

## Core Types

### SpawnResult

```python
# scripts/start-bg-spawn.py
@dataclass(frozen=True)
class SpawnResult:
    short: str        # 8-hex daemon short ID, e.g. "abc12345"
    sessionId: str    # full UUID from state.json, e.g. "abc12345-..."
    cwd: str          # absolute worktree path, normalized
```

Emitted as JSON on stdout for the skill to parse:

```json
{"short":"abc12345","sessionId":"abc12345-0000-0000-0000-000000000000","cwd":"C:/repos/foo/.worktrees/bar"}
```

### Daemon state.json (read-only contract with Claude Code)

`~/.claude/jobs/<short>/state.json` is owned and written by the Claude Code daemon (CLI ≥2.1.139). The helper depends on this subset of fields and treats other fields as opaque:

| Field | Type | Used for |
|-------|------|----------|
| `sessionId` | string (UUID) | Canonical full session identifier; emitted in `SpawnResult` |
| `daemonShort` | string (8 hex) | Cross-check against banner-parsed short ID |
| `cwd` | string (abs path) | Post-spawn cwd assertion |
| `intent` | string | Verbatim copy of the spawn-time positional prompt (AC-08); read by integration tests |
| `createdAt` | string (ISO 8601) | Fallback identify-session disambiguation when banner parse fails |
| `cliVersion` | string (semver) | Diagnostic only; not used for gating (the helper's own `claude --version` probe owns version-gating) |

If a future Claude Code release renames or removes any of these fields, this spec must be revised and the min-version floor bumped. The helper raises a clear diagnostic (rather than silently misbehaving) if the field is absent.

### Usage Pattern

```python
# inside .claude/skills/start/SKILL.md Step 6
result = json.loads(subprocess.run(
    ["python", "scripts/start-bg-spawn.py",
     "--worktree-abs", worktree_abs,
     "--branch", branch,
     "--prompt-file", prompt_path],
    capture_output=True, text=True, check=True,
).stdout)

subprocess.run([
    "python", "scripts/inflight-register.py",
    "--session", result["short"],
    "--branch", branch,
    "--worktree", worktree_rel,
    "--intent", intent,
    *issue_flags,
], check=True)
```

---

## Error Handling

### Error Types (canonical exit-code table)

| Error | Condition | Exit | Recovery |
|-------|-----------|------|----------|
| `VersionTooOld` | `claude --version` < 2.1.139 | 1 | Operator upgrades Claude Code, reruns |
| `ClaudeNotFound` | `claude` not on PATH (`FileNotFoundError` from version probe) | 1 | Operator installs Claude Code |
| `PromptEmpty` | `--prompt-file` empty or missing | 1 | Skill bug — fix caller |
| `InvalidArg` | `--worktree-abs` / `--branch` validation fails | 1 | Skill bug — fix caller |
| `SpawnFailed` | `claude --bg` returned non-zero | 2 | Surface stderr verbatim; operator diagnoses |
| `StateFileTimeout` | No matching `state.json` within the 5-second identify-session poll budget | 2 | Surface diagnostic; operator inspects `~/.claude/jobs/` and `claude logs` |
| `CwdMismatch` | Post-spawn `state.cwd` ≠ requested worktree | 2 | Helper calls `claude stop <short>` before exiting |

The Validation Rules table above and the Edge Cases table below cross-reference these rows via the exit code.

### Recovery Strategies

- **Version / install errors** are operator-fixable. Print the exact command to fix.
- **Daemon errors** (state file timeout, cwd mismatch) are unrecoverable in-process; the skill stops, the operator sees a clear diagnostic and chooses whether to retry.
- **No automatic retry** — `/start` is interactive; retries belong to the operator.

### Edge Cases

| Scenario | Expected Behavior |
|----------|-------------------|
| Concurrent `/start` calls in different worktrees | Each spawn produces its own `~/.claude/jobs/<short>/` dir; fallback ID lookup uses `state.cwd` to disambiguate |
| Daemon process not running | `claude --bg` itself starts the daemon if needed; transparent to caller |
| `~/.claude/jobs/` does not exist | Created by `claude --bg` on first spawn; caller does not pre-create |

---

## Design Decisions

### Why `claude --bg` over today's PowerShell launcher?

**Context:** Today's launcher (`scripts/launch-claude-session.py`) writes a PowerShell here-string `.ps1` file and spawns `pwsh -Command "Start-Process pwsh -ArgumentList '-NoExit','-File','<script>'"`. Issue #799 documents three concrete failure classes: `'@` line-start terminator, `-File` vs `-Command` TTY bug, fnm/nvm shim resolution in spawned shells. The launcher is also Windows-only — macOS/Linux/WSL2 print a manual-paste fallback.

**Decision:** Use `claude --bg "<prompt>"` invoked via `subprocess.run(argv, cwd=...)`. This is the same primitive Agent View uses internally, so the new session appears in Agent View on every Claude Code surface (CLI, desktop, IDE, claude.ai/code, Slack) on every platform.

**Test results (probed 2026-05-13 in this worktree on Windows, Claude Code 2.1.140):**

| Prompt size | Spawn result | `state.intent` length | Verbatim? |
|----|----|----|----|
| 1,024 chars | exit 0 | 1024 | yes |
| 5,000 chars | exit 0 | 5000 | yes |
| 8,000 chars | exit 0 | 8000 | yes |
| 16,384 chars | exit 0 | 16384 | yes |
| 30,000 chars | exit 0 | 30000 | yes |

Current `/start` targets 1–5K char prompts with a 30K hard cap (PowerShell `CreateProcess` limit). The new path retains the same headroom but via a different mechanism (Windows argv list limit), and the limit is no longer per-shell.

**Alternatives considered:**

- **Keep PowerShell, add `claude --bg` after the fact**: would mean both spawn paths run, polluting Agent View with empty rows and not fixing any #799 fragility.
- **Use `--bg` with fallback to PowerShell on old Claude Code versions**: keeps `launch-claude-session.py` alive and tested indefinitely. The maintenance burden of a dual code path exceeds the cost of a clear version-gate error.
- **Use `claude --worktree`**: the `-w/--worktree` flag combines worktree creation with launch, but it does not use our `worktree-create.py` safety net (origin/main fetch, stranded-directory detection — issue #799 fixes). Splitting concerns keeps `worktree-create.py` authoritative for worktree creation and `start-bg-spawn.py` authoritative for launch.

**Consequences:**

- Positive: single cross-platform code path; #799 fragility classes eliminated as a category, not patched one-by-one; new sessions visible in Agent View on every surface.
- Negative: hard version floor at Claude Code 2.1.139. Operators on older versions get a clear error rather than a silent fallback.

### Why hard-fail on old Claude Code instead of falling back to PowerShell?

**Context:** Two failure modes for old Claude Code: (a) `claude` is on PATH but predates 2.1.139, (b) `claude` is missing entirely.

**Decision:** Both surfaces emit a one-line stderr message naming the required version and the install command, then exit non-zero. No fallback path runs.

**Alternatives considered:**

- **Fall back to today's PowerShell launcher**: would keep `launch-claude-session.py` (and its tests) in the repo forever, since the dispatch point cannot delete itself. The launcher's #799 scars would silently re-surface for any operator who hadn't upgraded.
- **Warn but continue with `--bg` and hope it works**: silent failure when the spawn fails; we'd lose the diagnostic precision.

**Consequences:**

- Positive: zero legacy code paths to maintain; clear operator action on the rare version-skew case.
- Negative: operators must upgrade Claude Code before using `/start`. The upgrade is a single `npm i -g @anthropic-ai/claude-code`; the floor is already implicit in the rest of the workflow (Agent View, `claude attach`, `claude logs`).

### Why a separate `start-bg-spawn.py` helper instead of inlining?

**Context:** The new flow is small — version probe, spawn, banner parse, state read. We could inline this in the skill via repeated Bash tool calls.

**Decision:** Extract a Python helper. It is unit-testable in isolation, the version comparator and banner parser benefit from explicit tests, and the cwd-normalization logic deserves a single locus.

**Alternatives considered:**

- **Inline in the skill via shell**: brittle across platforms; banner parsing in shell would re-introduce escape hell.
- **Bake the logic into `worktree-create.py`**: violates the script's single responsibility (worktree creation is not session launch).

**Consequences:**

- Positive: clear unit-test boundary; the skill stays declarative.
- Negative: one more file in `scripts/`. Net repo line count drops (~298 LOC of `launch-claude-session.py` deleted, ~80 LOC of `start-bg-spawn.py` added).

### Why read `~/.claude/jobs/<short>/state.json` for the full UUID?

**Context:** `inflight-register.py --session` accepts an 8-hex short ID and persists it as `session_id` in `.claude/state/in-flight-issues.json`. Future tooling (Agent View correlation, retros) may want the full UUID.

**Decision:** Read `state.json` and emit both `short` and `sessionId` (full UUID) from `start-bg-spawn.py`. Today only `short` flows to `inflight-register`; tomorrow's tooling can pick up the UUID without revisiting the spawn site.

**Alternatives considered:**

- **Only parse the banner**: loses the full UUID forever — Agent View's URL scheme uses the full UUID, so cross-tool correlation needs it.
- **Treat `state.json` as private daemon state**: it is daemon-owned, but the path layout is consistent across `cliVersion` in 2.1.139+. The cost of breakage is contained to this helper.

**Consequences:**

- Positive: forward-compatible identity capture; cwd post-condition verifiable.
- Negative: small dependency on a daemon implementation detail. Tolerated because the path is stable in supported versions.

### Why not open a terminal window at the worktree path?

**Context:** Today's `/start` opens an interactive PowerShell window in the worktree so the operator has a shell for ad-hoc commands.

**Decision:** Stop opening a terminal. Agent View is the new home; the operator opens a shell themselves with `cd .worktrees/<name>` from any existing terminal if they need one.

**Alternatives considered:**

- **Opt-in `--with-terminal` flag**: adds a code path to maintain and a per-platform spawn matrix to test. Defers the cleanup goal.
- **Always open a terminal**: defeats the simplification.

**Consequences:**

- Positive: one less platform-specific spawn to maintain. The terminal is no longer the operator's primary surface for the new session.
- Negative: operators who relied on the auto-opened shell must change their habit. Mitigated by Agent View's own terminal-attach support (`claude attach <short>` from any shell).

---

## Configuration

| Setting | Type | Required | Default | Description |
|---------|------|----------|---------|-------------|
| `claude` on PATH | binary | Yes | — | Claude Code CLI; min version 2.1.139 |
| Min Claude Code version | semver | Yes | `2.1.139` | Hardcoded in `start-bg-spawn.py`; bumped if/when daemon contract changes |

---

## Related Specs

- [skill-fixes-cleanup-start.md](./skill-fixes-cleanup-start.md) — Trigger phrases for `/start` discoverability and `/cleanup` orphan sweep. Disjoint from this spec (that one is about prose; this one is about spawn mechanics).
- [workflow-enforcement.md](./workflow-enforcement.md) — Session-stop hooks and code-change detection. Touches `claude --bg` only tangentially.
- [investigation.md](./investigation.md) — `/investigate` is a sibling skill; its launch mechanism is unchanged (interactive in the parent session).

---

## Changelog

| Date | Change |
|------|--------|
| 2026-05-13 | Initial spec (issue #1032, epic #1025) |

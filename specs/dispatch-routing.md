# Dispatch Routing

**Status:** Draft
**Last Updated:** 2026-05-14
**Code:** [scripts/claude_dispatch.py](../scripts/claude_dispatch.py) | [scripts/bg_transcript.py](../scripts/bg_transcript.py) | [scripts/triage_common.py](../scripts/triage_common.py) | [scripts/pipeline.py](../scripts/pipeline.py) | [scripts/pr_monitor.py](../scripts/pr_monitor.py) | [scripts/start-bg-spawn.py](../scripts/start-bg-spawn.py) | [.claude/hooks/sdk-spend-warn.py](../.claude/hooks/sdk-spend-warn.py)
**Surfaces:** N/A (workflow tooling, not a product surface — internal Python scripts and a PreToolUse hook)

---

## Overview

Every PPDS subprocess that invokes `claude` routes through a single primitive that selects between interactive (`claude --bg`) and headless (`claude -p`) modes. Interactive is the default; headless is opt-in and emits a loud warning at dispatch plus a JSONL audit record. After Anthropic's 2026-06-15 billing split, headless invocations draw from the metered Agent SDK credit pool; interactive invocations stay on the subscription pool. This spec defines the routing primitive, the completion-detection protocol for backgrounded sessions, the observability hook, and the daemon posture for pr_monitor.

**Premise validation:** Anthropic has not explicitly documented whether `claude --bg` from a Python daemon (no human attached) counts as interactive pool. The strong inference (the SDK-pool documentation does not list `--bg`; the Agent View blog says "standard rate limits apply") is yes, but this is unconfirmed until a real interactive-mode pipeline run lands and the operator inspects spend (see AC-27 + AC-28). If post-implementation inspection refutes the premise, this spec's rationale needs revisiting; the dispatcher itself remains sound (it would just be a more elaborate path to the same metered pool).

### Goals

- **Single routing point.** All `claude` subprocess invocations from `pipeline.py`, `pr_monitor.py`, `triage_common.py`, and `start-bg-spawn.py` go through `scripts/claude_dispatch.py`. Constitution A2.
- **Interactive default, headless opt-in.** Every dispatch site accepts `--mode {interactive,headless}` (and `PPDS_DISPATCH_MODE` env override); default is `interactive`.
- **Loud on metered crossings.** Every `claude -p` invocation produces a stderr warning at dispatch and an append-only JSONL record. A PreToolUse hook duplicates the warning when invoked from interactive sessions (defence-in-depth).
- **Headless still works.** Headless mode is preserved for cases where it is genuinely the right tool; both modes are tested.
- **No new SDK dependencies.** CLI-only. No `claude_agent_sdk`, `@anthropic-ai/claude-agent-sdk`, or anthropic Python/TS SDK imports anywhere.
- **Hard-fail on missing prerequisites.** Installed `claude` < 2.1.139 errors out at dispatcher startup; no silent fallback to `-p`.
- **Companion cleanup.** Dead GitHub Action (`.github/workflows/claude.yml`) deleted; `docs/BACKLOG.md` label-reference tables replaced with `gh` references (GitHub is the source of truth).

### Non-Goals

- **Spend dashboard or burn-rate reporting.** Out of scope; the JSONL audit record is the foundation, a dashboard is a follow-up.
- **Token-accurate cost estimation.** The JSONL records a rough input-token estimate only; precise accounting is deferred.
- **Auth-expiry mid-run recovery.** Daemon halts loudly; operator restarts. Mid-run reauth is a separate concern.
- **Migration to Managed Agents.** Tracked in #1030, #1031.
- **WORKFLOW.md restructure.** A new dispatch section is added with required subsection headings; a broader restructure is a separate `docs:` issue.

---

## Architecture

```
                +--------------------------------------------+
                |  scripts/claude_dispatch.py (NEW)           |
                |  - MIN_VERSION, require_min_version()       |
                |  - spawn(mode, prompt, caller, ...) -> Handle|
                |  - BgHandle    (mode=interactive)           |
                |  - HeadlessHandle (mode=headless)           |
                |  - DispatchError, BlockedSessionError,      |
                |    DispatchFallbackError                    |
                |  - resolves PPDS_DISPATCH_MODE env override |
                |  - emits sdk-spend-warn on headless         |
                +--------------------------------------------+
                       ^          ^           ^           ^
                       |          |           |           |
   +-------------------+          |           |           +--------------+
   |                              |           |                          |
+--+---------------+  +-----------+-------+  ++----------------------+  ++-----------------+
| pipeline.run_    |  | triage_common.    |  | pr_monitor.run_triage |  | start-bg-spawn.  |
| claude(...)      |  | dispatch_subagent |  | + run_retro           |  | spawn(...)       |
| (heartbeat loop) |  | (short-lived)     |  | (daemon)              |  | (foreground /    |
|                  |  |                   |  |                       |  |  start launch)   |
+------------------+  +-------------------+  +-----------------------+  +------------------+

   scripts/bg_transcript.py (NEW)            .claude/hooks/sdk-spend-warn.py (NEW)
   - parse_outcome(path) -> str               PreToolUse on Bash
   - iter_assistant_text(path, offset)        - matches command running `claude -p`
   (subsumes pipeline.extract_text_from_jsonl - stderr warn
    which becomes a re-export)                - append .claude/state/sdk-spend.jsonl
```

### Components

| Component | Responsibility |
|-----------|----------------|
| `scripts/claude_dispatch.py` | Sole site that constructs `claude` argv. Decides mode, applies `--dangerously-skip-permissions`, version-checks, returns a `DispatchHandle` (`BgHandle` or `HeadlessHandle`). Defines `DispatchError`, `BlockedSessionError`, `DispatchFallbackError`. |
| `DispatchHandle` (abstract) | Uniform interface: `poll() -> "working" \| "done" \| "blocked" \| "error"`, `terminate()`, `wait(timeout)`, `transcript_path -> Path`, `output() -> str`, `bytes_consumed -> int`. |
| `BgHandle` | Wraps `~/.claude/jobs/<short>/state.json` polling and `state["linkScanPath"]` transcript path. |
| `HeadlessHandle` | Wraps `subprocess.Popen` over `claude -p ... --output-format stream-json`; the stage_log path doubles as transcript. |
| `scripts/bg_transcript.py` | JSONL transcript reader. Parses `type:"result"` (preferred) or assembled `type:"assistant"` text blocks. Format is shared between `-p` stream-json and `--bg` session transcripts. |
| `scripts/triage_common.py:dispatch_subagent` | Becomes a thin wrapper: `claude_dispatch.spawn(caller="triage_common.dispatch_subagent", ...) + wait() + read transcript`. Adds `mode` parameter; preserves payload+model+agent signature. |
| `scripts/pipeline.py:run_claude` | Replaces inline `subprocess.Popen([claude, -p, ...])` with `claude_dispatch.spawn(mode=..., caller="pipeline.run_claude", ...)`. Heartbeat / stall / hard-ceiling loop unchanged but pointed at `handle.transcript_path`. |
| `scripts/pr_monitor.py:run_triage` / `run_retro` | Delete `_build_triage_cmd` / `_build_retro_cmd`; call through `dispatch_subagent` instead. Pass `mode="interactive"` by default; on non-zero `--bg` exit raise `DispatchFallbackError` instead of silently switching pools. |
| `scripts/start-bg-spawn.py` | Imports `MIN_VERSION` and `require_min_version` from `claude_dispatch`. No behaviour change. |
| `.claude/hooks/sdk-spend-warn.py` | PreToolUse on Bash; matches commands invoking `claude -p`. Emits stderr warning, appends to `.claude/state/sdk-spend.jsonl`. Does not fire on `claude --bg` or foreground `claude`. |

### Dependencies

- Depends on: [pipeline-observability.md](./pipeline-observability.md) (heartbeat, stall/hard-ceiling, `extract_text_from_jsonl` lineage)
- Depends on: [start-launch.md](./start-launch.md) (precedent: `--bg` spawn pattern, MIN_VERSION, banner parsing)
- Depends on: [workflow-enforcement.md](./workflow-enforcement.md) (pipeline + workflow state)
- Uses patterns from: [architecture.md](./architecture.md) (Constitution A2 single-code-path)

---

## Specification

### Core Requirements

1. **Single dispatcher.** `scripts/claude_dispatch.py` is the sole place that constructs `claude` subprocess argv. All other call sites import from it. A grep regression test in CI (under `tests/scripts/test_claude_dispatch_routing.py`) fails if `["claude", "-p"` or `["claude", "--bg"` appears in any file outside an explicit allowlist (the dispatcher itself, the regression test, this spec).
2. **Mode flag and env override.** `pipeline.py`, `pr_monitor.py`, and `triage_common.dispatch_subagent` accept `--mode {interactive,headless}` (CLI) or `mode=` (Python kwarg). The `PPDS_DISPATCH_MODE` environment variable overrides when the flag is unset; default is `interactive`. Invalid values raise `DispatchError` at CLI boundary (exit 1).
3. **Version gate.** `claude_dispatch.require_min_version()` raises `DispatchError` (mapped to exit 1 at CLI boundaries) if installed `claude` < 2.1.139. Called once per process at first `spawn()`. No silent fallback. Version-string parser: matches leading `(\d+)\.(\d+)\.(\d+)` from `claude --version` stdout (mirrors `start-bg-spawn._parse_version`).
4. **Interactive completion detection.** `BgHandle.poll()` reads `~/.claude/jobs/<short>/state.json` and returns one of `"working"`, `"done"`, `"blocked"`, `"error"` based on `state["state"]`. `"blocked"` includes the `state["needs"]` text in the `BlockedSessionError` raised by `wait()`.
5. **Headless completion detection.** `HeadlessHandle.poll()` calls `Popen.poll()` and returns `"working"` until the process exits, then `"done"` (exit 0) or `"error"` (non-zero).
6. **Transcript path resolution.** `BgHandle.transcript_path` returns `state["linkScanPath"]` (explicit absolute path, no slug derivation). `HeadlessHandle.transcript_path` returns the `.workflow/stages/<stage>.jsonl` path passed in at spawn.
7. **Loud headless warnings — two layers.** (a) `claude_dispatch.spawn(mode="headless", ...)` writes to stderr using the format string:
   ```
   WARN SDK pool: claude -p invoked from {caller} (model={model}, agent={agent}) — counts against monthly Agent SDK credit, not subscription.
   ```
   where `{caller}` is a required kwarg (no frame inspection — every caller passes its own caller string), and `{model}`/`{agent}` default to `none` when not specified. It appends a JSONL record to `.claude/state/sdk-spend.jsonl`. (b) `.claude/hooks/sdk-spend-warn.py` (PreToolUse on Bash) fires the same warning + JSONL append when an interactive session runs a command invoking `claude -p` via the Bash tool. Hook caller field is `bash:<session_id>`.
8. **Daemon posture.** `pr_monitor.py` passes `--dangerously-skip-permissions` to all `--bg` invocations. On non-zero `--bg` exit, it raises `DispatchFallbackError` (defined in `claude_dispatch.py`) with the underlying stderr. No silent `-p` fallback.
9. **Pipeline blocked-state policy.** When `BgHandle.poll()` returns `"blocked"`, `pipeline.run_claude` calls `handle.terminate()` (which runs `claude stop <short>`), logs the `needs` text via the standard stage log, and surfaces `PipelineFailure(stage, reason=f"stage asked question: {needs}")`. Stage prompts are designed for autonomous execution; a `blocked` state is a stage prompt bug, not a runtime branch.
10. **GitHub Actions cleanup.** `.github/workflows/claude.yml` is deleted. The spec documents the manual operator step to revoke the `CLAUDE_CODE_OAUTH_TOKEN` secret.
11. **BACKLOG.md cleanup.** All six label-reference subsection tables in `docs/BACKLOG.md` (type, area, epic, priority, status, Other) are deleted; replaced with one prose paragraph and three `gh` commands. Prose conventions (triage rules, milestone meanings, query examples) are preserved.
12. **WORKFLOW.md addendum.** A new "Dispatch routing" section in `.claude/WORKFLOW.md` contains four required subsection headings: "When interactive", "When headless", "Overrides", "Spend journal". Section length is informational only — the structural ACs assert presence of the headings, not a line count.

### Primary Flows

**Interactive dispatch (default):**

1. Caller invokes `claude_dispatch.spawn(mode="interactive", prompt=..., caller=..., name=<stage-or-agent>, dangerous=True, ...)`.
2. Dispatcher calls `require_min_version()` (cached after first call).
3. Subprocess: `claude --bg --name <name> --dangerously-skip-permissions -- <prompt>` (`subprocess.run`, timeout 30 s).
4. Parse banner matching `backgrounded\s+·\s+([0-9a-f]{8})\b` from stdout (mirrors `start-bg-spawn.BANNER_RE`).
5. Wait up to 5 s for `~/.claude/jobs/<short>/state.json` to appear; read `sessionId`, `cwd`, `linkScanPath`.
6. Return `BgHandle(short, sessionId, state_path, transcript_path)`.
7. Caller polls `handle.poll()` until `"done"` / `"blocked"` / `"error"`; or `handle.wait(timeout)` for blocking calls.
8. On `"done"`, caller reads `bg_transcript.parse_outcome(handle.transcript_path)` for the final result text.

**Headless dispatch (opt-in):**

1. Caller invokes `claude_dispatch.spawn(mode="headless", prompt=..., caller=..., model=..., agent=..., stage_log=...)`.
2. Dispatcher calls `require_min_version()`.
3. Emit stderr warning (per Req #7) and append to `.claude/state/sdk-spend.jsonl`.
4. Subprocess: `claude -p <prompt> --verbose --output-format stream-json [--model M] [--agent A]` via `subprocess.Popen`; stdout streamed to `stage_log` path.
5. Return `HeadlessHandle(proc, stage_log_path)`.
6. Caller polls / waits via existing pipeline heartbeat logic.

**Pipeline `run_claude` (mode=interactive, default):**

1. `claude_dispatch.spawn(mode="interactive", caller="pipeline.run_claude", name=stage, dangerous=True, ...)` → `BgHandle`.
2. Existing heartbeat loop (60 s cadence) reads `state.json` state + `transcript_path` size + git activity. Stall and hard-ceiling logic unchanged.
3. On `state="blocked"`, `terminate()` then `raise PipelineFailure`.
4. On `state="done"`, read `bg_transcript.parse_outcome` for output text.

**pr_monitor daemon (mode=interactive, default):**

1. `dispatch_subagent(profile, payload, mode="interactive", caller="pr_monitor.run_triage", model="sonnet")` → `BgHandle`.
2. Daemon `wait(timeout=1800)` for the bg session.
3. On non-zero exit / `state="error"`, raise `DispatchFallbackError(stderr)`.
4. Operator may invoke `pr_monitor.py --mode headless` for explicit one-off headless runs.

### Surface-Specific Behavior

N/A — this spec governs workflow tooling (Python scripts + hooks), not a product surface. The `--mode` flag is a developer-tooling internal flag on `scripts/pipeline.py` and `scripts/pr_monitor.py`; it is not part of the PPDS user-facing CLI (`ppds` / TUI / Extension / MCP).

### Constraints

- `subprocess.Popen` / `subprocess.run` always invoked with `shell=False` (Constitution S2).
- No imports of `claude_agent_sdk` / `@anthropic-ai/claude-agent-sdk` / `anthropic` SDKs anywhere — enforced by a regression test grepping `requirements*.txt`, `package*.json`, and `**/*.{py,ts,tsx,js}`.
- `.claude/state/sdk-spend.jsonl` is gitignored.
- `claude --bg` requires Claude Code ≥ 2.1.139.
- All `--bg` invocations from unattended daemons (`pipeline.py`, `pr_monitor.py`) pass `--dangerously-skip-permissions`. Foreground `start-bg-spawn.py` does NOT pass `--dangerously-skip-permissions` by default; the human at `/start` is responsible for approving prompts. Callers may opt into a non-default permission mode by passing `--permission-mode <mode>` (one of the six `claude --permission-mode` choices) — this is threaded through to the spawned `claude --bg` argv. Background investigation/audit sessions launched from a parent agent (no human at the worktree) are the primary use case; the opt-in shape preserves the "no surprise bypass" property because the literal flag must be specified at every spawn.
- Any commit touching `CLAUDE.md` must include `[claude-md-reviewed: YYYY-MM-DD]` in the commit message body (enforced by `claudemd-gate.sh` pre-commit hook; see `docs/CLAUDE-MD-GOVERNANCE.md`). This is an external process gate on the commit, not a code-behavior assertion.

### Validation Rules

| Field | Rule | Error |
|-------|------|-------|
| `--mode` CLI flag | One of `{interactive, headless}` | Argparse error, exit 2 |
| `PPDS_DISPATCH_MODE` env | One of `{interactive, headless}` or unset | `DispatchError`; exit 1 at CLI boundary |
| `claude --version` stdout | Parses leading `(\d+)\.(\d+)\.(\d+)` tuple | `DispatchError("could not parse claude --version output")`, exit 1 |
| Parsed version | ≥ `(2, 1, 139)` | `DispatchError("claude < 2.1.139")`, exit 1 |
| `--bg` banner stdout | Matches `backgrounded\s+·\s+([0-9a-f]{8})\b` | Fallback to cwd-scan (mirrors start-bg-spawn); if still no match: `DispatchError` |
| `state.json` file appearance | Present within 5 s of `--bg` spawn | `DispatchError("daemon state file did not appear within 5s")` |
| `state.json["linkScanPath"]` | Non-empty string pointing to existing file (after state="done") | `DispatchError("linkScanPath missing/empty")` |
| `state.json["state"]` value | One of `{working, done, blocked, error}` | Unknown value treated as `"error"`; raw value logged |
| `--name` argument value | Non-empty, matches `^[A-Za-z0-9/_.\-]+$` (mirrors start-bg-spawn) | `DispatchError("invalid --name")` |
| `caller` kwarg | Non-empty string | `TypeError` at call site (Python contract; no runtime guard required) |

---

## Acceptance Criteria

| ID | Criterion | Test | Status |
|----|-----------|------|--------|
| AC-01 | `claude_dispatch.spawn(mode="interactive", caller=..., name=..., dangerous=True, ...)` invokes `["claude", "--bg", "--name", <name>, "--dangerously-skip-permissions", "--", <prompt>]` with `shell=False` | `test_dispatch_interactive_argv` | 🔲 |
| AC-02 | `claude_dispatch.spawn(mode="headless", caller=..., ...)` invokes `["claude", "-p", <prompt>, "--verbose", "--output-format", "stream-json"]` (plus `--model`/`--agent` when provided) with `shell=False` | `test_dispatch_headless_argv` | 🔲 |
| AC-03 | `claude_dispatch.spawn(mode="headless", caller=..., model=..., agent=..., ...)` emits stderr matching the regex `^WARN SDK pool: claude -p invoked from .+ \(model=.+, agent=.+\) — counts against monthly Agent SDK credit, not subscription\.$` with caller/model/agent values interpolated (model/agent default to literal `none` when absent) | `test_dispatch_headless_stderr_warning_format` | 🔲 |
| AC-04 | `claude_dispatch.spawn(mode="headless", caller=..., ...)` appends a JSONL row to `.claude/state/sdk-spend.jsonl` with keys `{ts, caller, model, agent, est_input_tokens}` | `test_dispatch_headless_appends_sdk_spend_jsonl` | 🔲 |
| AC-05 | `claude_dispatch.require_min_version()` raises `DispatchError` when `claude --version` reports < 2.1.139 | `test_require_min_version_rejects_old` | 🔲 |
| AC-06 | `claude_dispatch.require_min_version()` accepts 2.1.139 exactly (boundary) | `test_require_min_version_accepts_boundary` | 🔲 |
| AC-07 | `BgHandle.poll()` returns `"working"` for `state["state"]="working"`, `"done"` for `"done"`, `"blocked"` for `"blocked"`, `"error"` for `"error"` or unknown values | `test_bg_handle_poll_state_mapping` | 🔲 |
| AC-08 | `BgHandle.transcript_path` resolves to `state["linkScanPath"]` (not derived from cwd→slug) | `test_bg_handle_transcript_path` | 🔲 |
| AC-09a | `BgHandle.wait()` raises `BlockedSessionError(needs=…)` when `state["state"]="blocked"` | `test_bg_handle_wait_raises_blocked_with_needs` | 🔲 |
| AC-09b | Before raising on blocked, `BgHandle.wait()` invokes `["claude", "stop", <short>]` via `subprocess.run` (captured argv) | `test_bg_handle_wait_runs_claude_stop_on_blocked` | 🔲 |
| AC-10 | `HeadlessHandle.poll()` returns `"working"` while `Popen.poll()` is None, `"done"` for exit 0, `"error"` for non-zero | `test_headless_handle_poll_exit_mapping` | 🔲 |
| AC-11 | `bg_transcript.parse_outcome(path)` returns the `type:"result"` event text when present | `test_bg_transcript_prefers_result` | 🔲 |
| AC-12 | `bg_transcript.parse_outcome(path)` falls back to assembled `type:"assistant"` text blocks when no result event exists | `test_bg_transcript_assembles_assistant_on_timeout` | 🔲 |
| AC-13 | `pipeline.py` accepts `--mode {interactive,headless}` and threads it to every `run_claude` invocation; default is `interactive`; `PPDS_DISPATCH_MODE` env overrides when flag absent | `test_pipeline_mode_flag_and_env` | 🔲 |
| AC-14 | `pr_monitor.py` accepts `--mode {interactive,headless}`; default `interactive`; threaded to `dispatch_subagent` for both triage and retro | `test_pr_monitor_mode_flag` | 🔲 |
| AC-15 | `triage_common.dispatch_subagent(mode=…)` parametrized over both modes returns the expected stdout/stderr/exit_code shape | `test_dispatch_subagent_both_modes` | 🔲 |
| AC-16 | `pr_monitor.py` raises `DispatchFallbackError` on non-zero `--bg` exit instead of falling back to `-p` | `test_pr_monitor_loud_on_bg_failure` | 🔲 |
| AC-17 | All `pipeline.py` and `pr_monitor.py` `--bg` invocations include `--dangerously-skip-permissions`; `start-bg-spawn.py` does NOT contain that literal flag and does NOT call `claude_dispatch.spawn`. `start-bg-spawn.py` MAY emit `--permission-mode <mode>` in argv when (and only when) its caller explicitly passes `--permission-mode <mode>` on the CLI — no default permission-mode bypass, no implicit fallback. | `test_dangerous_flag_unattended_only` + `test_permission_mode_passthrough` | 🔲 |
| AC-18 | `pipeline.run_claude` translates `BgHandle.poll() == "blocked"` into `PipelineFailure(stage, reason="stage asked question: <needs>")` after calling `handle.terminate()` | `test_pipeline_fails_loud_on_blocked` | 🔲 |
| AC-19 | `.claude/hooks/sdk-spend-warn.py` fires on Bash invocations whose first non-environment-prefix token resolves to `claude` (with optional absolute path / `.exe`) AND whose argv contains `-p` before any redirect; appends a JSONL row to `.claude/state/sdk-spend.jsonl` | `test_sdk_spend_warn_hook_pattern` | 🔲 |
| AC-20 | `.claude/hooks/sdk-spend-warn.py` does NOT fire on `claude --bg`, bare `claude`, `grep "claude -p" file`, or piped chains where `claude -p` is not the first command | `test_sdk_spend_warn_hook_negatives` | 🔲 |
| AC-21 | CI regression: no file outside the allowlist (`scripts/claude_dispatch.py`, `tests/scripts/test_claude_dispatch_routing.py`, `specs/dispatch-routing.md`) contains the literal argv fragments `"claude", "-p"` or `"claude", "--bg"` | `test_no_claude_p_outside_dispatcher` | 🔲 |
| AC-22 | CI regression: no SDK imports (`claude_agent_sdk`, `@anthropic-ai/claude-agent-sdk`, `anthropic`) appear in `requirements*.txt`, `package*.json`, or any source file | `test_no_sdk_dependencies` | 🔲 |
| AC-23 | `.github/workflows/claude.yml` is deleted; no remaining repo file references `CLAUDE_CODE_OAUTH_TOKEN` | `test_no_github_action_or_token_references` | 🔲 |
| AC-24 | `docs/BACKLOG.md` no longer contains the six label-reference subsection tables (type, area, epic, priority, status, Other); contains a `gh label list` reference instead; prose triage rules retained | `test_backlog_tables_removed` | 🔲 |
| AC-25 | `.claude/WORKFLOW.md` "Dispatch routing" section contains all four required subsection headings: `When interactive`, `When headless`, `Overrides`, `Spend journal` | `test_workflow_md_has_dispatch_section_headings` | 🔲 |
| AC-26a | `CLAUDE.md` contains exactly one NEVER line referencing `dispatch_subagent` and the `<!-- enforcement: T2 hook:sdk-spend-warn -->` marker | `test_claudemd_never_line_present` | 🔲 |
| AC-26b | `CLAUDE.md` total line count remains ≤ 100 (enforced by `claudemd-line-cap.py` PreToolUse hook) | `test_claudemd_line_cap` (smoke) + existing hook | 🔲 |
| AC-27 | One real interactive-mode pipeline run completes end-to-end (spec → plan → implement → gates → verify → review → converge → pr → retro) with every stage visible as an Agent View row | Manual — operator runs `python scripts/pipeline.py --worktree <wt> --spec <s> --from implement` and inspects Agent View | 🔲 |
| AC-28 | After the AC-27 pipeline run, `.claude/state/sdk-spend.jsonl` contains zero rows with `caller` values from this PR's pipeline invocations (proves no headless crossings occurred during the validation run) | `test_pipeline_run_produces_no_sdk_spend_entries` (post-run fixture check) | 🔲 |

Status key: ✅ covered by passing test · ⚠️ test exists but failing · ❌ no test yet · 🔲 not yet implemented

### Edge Cases

| Scenario | Input | Expected Output |
|----------|-------|-----------------|
| `--bg` banner missing from stdout | `subprocess.run` returns 0 with no `backgrounded · <short>` line | Fallback to cwd-based scan of `~/.claude/jobs` for sessions younger than 10 s (mirrors `start-bg-spawn._scan_for_cwd`). If still not found, raise `DispatchError`. |
| `state.json` doesn't appear within 5 s of spawn | `subprocess.run` returns 0, banner parsed, but state file never written | `DispatchError("daemon state file did not appear within 5s")`. |
| `claude --bg` exits non-zero | install missing, auth expired, etc. | Caller-specific: pipeline raises `PipelineFailure`; pr_monitor raises `DispatchFallbackError(stderr)`. No silent `-p` fallback. |
| `claude -p` exits non-zero with partial output | Stage log JSONL contains some `assistant` events | `HeadlessHandle.output()` returns assembled `assistant` text (existing behaviour from pipeline-observability.md AC-01). |
| `state["state"]` is a value not in `{working, done, blocked, error}` | future-proofing | `BgHandle.poll()` returns `"error"` and logs the raw value. |
| `state["linkScanPath"]` is absent or empty | older `claude` version that didn't write the field; bug | `DispatchError("linkScanPath missing/empty")`. Mitigated by version gate. |
| Hook on `grep "claude -p" README.md` | False positive risk | Does NOT fire — the command's resolved first token is `grep`, not `claude`. |
| Hook on `/usr/local/bin/claude -p hi` | Absolute path | Fires — match strips path prefix, normalizes to `claude`. |
| Hook on `claude.exe -p hi` | Windows binary extension | Fires — match strips `.exe` suffix. |
| Hook on `FOO=bar claude -p hi` | Env-var prefix | Fires — match strips leading `VAR=value` tokens before resolving first command token. |
| Hook on `echo x | claude -p hi` | Piped: claude -p is the SECOND command | Does NOT fire — hook inspects the original Bash invocation as one string; the first resolved command is `echo`. Documented limitation; the dispatcher's own warning layer catches this from the dispatcher side. |
| Hook fires during the dispatcher's own `claude -p` call (double-warning) | Both layers fire on same invocation | Acceptable — defence-in-depth. Both JSONL rows are valid records; dispatcher row has caller=`<explicit caller>`, hook row has caller=`bash:<session_id>`. No dedup. |
| `BACKLOG.md` cleanup removes labels still referenced by issues | Tables deleted, but real issues still carry `area:workflow` etc. | Expected and correct — GitHub is the source of truth; the documented tables had drifted. No code change required; the labels remain in GitHub. |
| Operator runs `pipeline.py` without `--mode` flag and without `PPDS_DISPATCH_MODE` env | Default path | Mode resolves to `interactive`. |
| Operator runs `pipeline.py --mode headless` | Explicit opt-in | Mode resolves to `headless`; stderr warning fires per Req #7; SDK-spend JSONL row appended. |
| `PPDS_DISPATCH_MODE=banana` set in environment | Invalid env value | `DispatchError("invalid PPDS_DISPATCH_MODE: banana")`; exit 1. |
| `PPDS_DISPATCH_MODE=headless` env + `--mode interactive` CLI flag | Conflict | CLI flag wins; mode resolves to `interactive`. |

### Test Examples

```python
# AC-01 / AC-02: parametrized argv assertion at the dispatcher boundary.
@pytest.mark.parametrize("mode,expected_argv_head", [
    ("interactive", ["claude", "--bg", "--name", "stage"]),
    ("headless",    ["claude", "-p"]),
])
def test_dispatch_argv(mode, expected_argv_head, monkeypatch, tmp_path):
    captured = []
    def fake_popen(argv, **kw):
        captured.append({"argv": list(argv), "shell": kw.get("shell", False)})
        return _FakeProc(exit_code=0)
    def fake_run(argv, **kw):
        captured.append({"argv": list(argv), "shell": kw.get("shell", False)})
        if argv[:2] == ["claude", "--version"]:
            return _FakeCompleted(stdout="2.1.141 (Claude Code)\n", returncode=0)
        if argv[:2] == ["claude", "--bg"]:
            return _FakeCompleted(stdout="backgrounded · abc12345\n", returncode=0)
        return _FakeCompleted(stdout="", returncode=0)
    monkeypatch.setattr(subprocess, "Popen", fake_popen)
    monkeypatch.setattr(subprocess, "run",   fake_run)
    monkeypatch.setattr(claude_dispatch, "_jobs_dir", lambda: tmp_path)
    _seed_state(tmp_path, "abc12345")  # writes state.json so identify_session succeeds
    handle = claude_dispatch.spawn(mode=mode, prompt="hi", caller="test", name="stage")
    spawn_call = next(c for c in captured if c["argv"][:2] in (
        ["claude", "--bg"], ["claude", "-p"]))
    assert spawn_call["argv"][:len(expected_argv_head)] == expected_argv_head
    assert spawn_call["shell"] is False
```

```python
# AC-19 / AC-20: hook fires only on canonical `claude -p` forms.
def test_sdk_spend_warn_hook_pattern(tmp_path):
    state_dir = tmp_path / ".claude" / "state"
    state_dir.mkdir(parents=True)
    cases = [
        ("claude -p 'do thing'",                   True),
        ("  claude -p hi",                         True),   # leading whitespace stripped
        ("/usr/local/bin/claude -p hi",            True),   # absolute path
        ("claude.exe -p hi",                       True),   # Windows .exe
        ("FOO=bar claude -p hi",                   True),   # env prefix
        ("claude --bg --name x -- hi",             False),  # bg mode
        ("claude",                                 False),  # bare claude
        ("grep 'claude -p' README.md",             False),  # first cmd is grep
        ("echo x | claude -p hi",                  False),  # piped — first cmd is echo
    ]
    for cmd, should_fire in cases:
        rc = run_hook(cmd, state_dir=state_dir)
        rows = _read_jsonl(state_dir / "sdk-spend.jsonl")
        last_caller = rows[-1].get("caller", "") if rows else ""
        if should_fire:
            assert last_caller.startswith("bash:"), f"expected fire for: {cmd!r}"
        else:
            assert not last_caller.startswith("bash:") or rows[-1].get("ts") != cmd, \
                f"expected NO fire for: {cmd!r}"
```

---

## Core Types

### DispatchHandle

Abstract base. Both `BgHandle` and `HeadlessHandle` implement it.

```python
class DispatchHandle(abc.ABC):
    transcript_path: Path

    @abc.abstractmethod
    def poll(self) -> Literal["working", "done", "blocked", "error"]: ...

    @abc.abstractmethod
    def terminate(self) -> None: ...

    @abc.abstractmethod
    def wait(self, timeout: float | None = None) -> int:
        """Block until poll() != 'working'. Returns exit code (0 for done, non-zero for error).
        Raises BlockedSessionError when poll() returns 'blocked'."""
```

### BgHandle

```python
@dataclass
class BgHandle(DispatchHandle):
    short: str             # 8-hex-char banner ID
    session_id: str        # UUID
    state_path: Path       # ~/.claude/jobs/<short>/state.json
    transcript_path: Path  # state["linkScanPath"]
```

### HeadlessHandle

```python
@dataclass
class HeadlessHandle(DispatchHandle):
    proc: subprocess.Popen
    transcript_path: Path  # the stage_log path passed at spawn()
```

### DispatchError, BlockedSessionError, DispatchFallbackError

Defined in `claude_dispatch.py`. All inherit from `Exception` (no .NET-style `PpdsException` exists in Python — Python tree uses plain exception classes; see `start-bg-spawn.SpawnError` precedent).

```python
class DispatchError(Exception):
    """Dispatcher-side error: bad version, invalid mode, missing state file, etc."""
    def __init__(self, message: str, exit_code: int = 1):
        super().__init__(message)
        self.exit_code = exit_code

class BlockedSessionError(DispatchError):
    """Raised when a --bg session transitions to state="blocked"."""
    def __init__(self, short: str, needs: str):
        super().__init__(f"session {short} blocked: {needs}", exit_code=1)
        self.short = short
        self.needs = needs

class DispatchFallbackError(DispatchError):
    """Raised by pr_monitor when --bg exits non-zero — operator must intervene."""
    def __init__(self, stderr: str):
        super().__init__(f"claude --bg failed (operator intervention required): {stderr[:500]}")
```

### PipelineFailure

Defined in `pipeline.py` per `pipeline-observability.md`. Signature mirrors existing usage:

```python
class PipelineFailure(Exception):
    def __init__(self, stage: str, reason: str | None = None):
        super().__init__(f"{stage}: {reason}" if reason else stage)
        self.stage = stage
        self.reason = reason
```

### Usage Pattern

```python
# Short-lived subagent call (triage_common)
handle = claude_dispatch.spawn(mode="interactive",
                               prompt=json.dumps(payload),
                               caller="triage_common.dispatch_subagent",
                               name=profile_name,
                               agent=profile_name,
                               model="sonnet")
exit_code = handle.wait(timeout=1800)
output = bg_transcript.parse_outcome(handle.transcript_path)

# Long-running pipeline stage (pipeline.run_claude)
handle = claude_dispatch.spawn(mode=mode,
                               prompt=full_prompt,
                               caller="pipeline.run_claude",
                               name=stage,
                               agent=agent,
                               model=effective_model,
                               dangerous=True,
                               stage_log=stage_jsonl_path)
while True:
    state = handle.poll()
    if state != "working":
        break
    # heartbeat: read transcript size, git activity, stall + hard ceiling checks
    ...
```

---

## Error Handling

### Error Types

| Error | Condition | Recovery |
|-------|-----------|----------|
| `DispatchError("claude < 2.1.139")` | Installed claude version too old | Operator runs `npm i -g @anthropic-ai/claude-code`; no automated recovery. Exit 1 at CLI boundary. |
| `DispatchError("daemon state file did not appear within 5s")` | `--bg` subprocess succeeded but state.json missing | Caller decides (pipeline → `PipelineFailure`; pr_monitor → propagate). |
| `DispatchError("invalid PPDS_DISPATCH_MODE: <v>")` | Env var contains unknown value | Exit 1; operator unsets / corrects the env var. |
| `BlockedSessionError(short, needs)` | `state["state"]="blocked"` | Caller-specific: pipeline fails loudly with the `needs` text via `PipelineFailure`; subagent call surfaces the question to the operator. |
| `DispatchFallbackError(stderr)` | pr_monitor's `--bg` exited non-zero | Operator restarts daemon manually after fixing root cause (auth, install, etc.). |

### Recovery Strategies

- **Hard-fail on missing prerequisites.** No silent fallback to `-p`. The whole design premise depends on operator awareness.
- **Loud-fail on unexpected states.** Unknown `state["state"]` values are treated as `"error"`, not assumed-working.
- **Operator-driven recovery.** Auth expiry, missing install, and network failures are out of scope; the daemon stops loudly and the operator restarts after fixing the underlying issue.

---

## Design Decisions

### Why a New `claude_dispatch.py` Module Instead of Growing `triage_common.dispatch_subagent`?

**Context:** Constitution A2 mandates a single code path. The issue body's literal suggestion was to centralize routing in `triage_common.dispatch_subagent()`. But the pipeline's `run_claude` carries a 200-line heartbeat / stall / hard-ceiling loop that doesn't belong in a triage helper.

**Decision:** Extract `scripts/claude_dispatch.py` as a low-level primitive. `triage_common.dispatch_subagent` becomes a thin caller (mode-aware, short-lived); `pipeline.run_claude` keeps its heartbeat loop and uses the dispatcher's `spawn()` for the subprocess construction step.

**Alternatives considered:**
- Grow `dispatch_subagent` to accept a `heartbeat_callback` for pipeline use — violates Single Responsibility; mixes triage helpers with low-level dispatch.
- Inline `--bg` argv construction at each call site — explicit Constitution A2 violation.

**Consequences:**
- Positive: One file owns `claude` argv construction; the grep regression test has a clean target. The heartbeat code stays where its callers live.
- Negative: One additional module file. Acceptable.

### Why `state["linkScanPath"]` Instead of Deriving the Transcript Path from cwd?

**Context:** The `--bg` session transcript lives at `~/.claude/projects/<slug>/<sessionId>.jsonl`, where `<slug>` is the cwd with `\` and `:` replaced. The slug-derivation logic is not officially documented and is fragile (Windows path separators, drive letters, UNC paths).

**Decision:** Read `state.json["linkScanPath"]` — the daemon writes this explicit absolute path. No slug derivation.

**Test result:** Confirmed empirically from a live `--bg` session created during the design phase:
```
"linkScanPath": "C:\\Users\\josh_\\.claude\\projects\\C--Users-josh--source-repos-ppdsw-ppds--worktrees-dual-mode-dispatch\\6bde78be-f371-4254-94a2-7a345e7b13c4.jsonl"
```

**Alternatives considered:**
- Derive the slug ourselves — fragile on Windows; one symlink or junction breaks it.
- Glob the projects dir by mtime + cwd match — works but slower, and ambiguous when multiple sessions exist for the same cwd.

**Consequences:** Positive: simple, robust, official. Negative: depends on the field continuing to exist — Anthropic could rename it. Mitigated by the version gate (≥ 2.1.139 is known good).

### Why Hard-Fail on `state="blocked"` Instead of Recovering?

**Context:** Pipeline stages are designed to run autonomously. The prompt explicitly says "You are running in headless mode … do not ask clarifying questions — make reasonable decisions and proceed." A `blocked` state means a stage violated that contract.

**Decision:** Terminate the session (`claude stop <short>`) and raise `PipelineFailure(stage, reason=f"stage asked question: {needs}")`. The `needs` text surfaces in the pipeline failure retro.

**Alternatives considered:**
- Treat `blocked` as soft completion if `output.result` is non-empty — risks falsely passing `verify_outcome` checks; partial output is not equivalent to clean completion.
- Auto-retry in headless mode — costs metered SDK credit; defeats the whole design.

**Consequences:**
- Positive: Stage prompt bugs surface immediately and visibly. The retro captures the exact question, making the prompt fix obvious.
- Negative: A flaky stage that asks a marginal question once kills the pipeline. Acceptable — autonomous stages should not be marginal.

### Why Both Hook AND Dispatcher Emit the Same Warning?

**Context:** Two scenarios produce metered `-p` spend: (a) PPDS scripts route through `claude_dispatch.spawn(mode="headless", ...)`, (b) an interactive Claude Code session runs `claude -p ...` via the Bash tool (a developer experimenting, a misbehaving skill).

**Decision:** Defence-in-depth. The dispatcher catches (a); the PreToolUse hook catches (b). Both append to the same JSONL; the dispatcher rows have caller=`<explicit caller>`, the hook rows have caller=`bash:<session_id>`.

**Alternatives considered:**
- Only the hook — misses dispatcher calls when no interactive session is hosting them (overnight pipeline runs).
- Only the dispatcher — misses ad-hoc Bash usage.

**Consequences:**
- Positive: Every `-p` invocation produces a record. Operator can `tail -f .claude/state/sdk-spend.jsonl` for live visibility.
- Negative: Double-warning in the rare case where an interactive session triggers the dispatcher's headless path. Acceptable — both layers are correct.

### Why No New ALWAYS Line in CLAUDE.md?

**Context:** Issue #1048 proposed an ALWAYS line in CLAUDE.md: "Pipeline and monitor dispatches default to `--mode interactive`. Specify `--mode headless` only when the call site cannot host an interactive session." `docs/CLAUDE-MD-GOVERNANCE.md`'s 4-question test gates additions: Q1 = globally relevant in every session.

**Decision:** Drop the ALWAYS line. It fails Q1 (only relevant when editing `pipeline.py` / `pr_monitor.py` / `triage_common.py` / `claude_dispatch.py`). Keep the NEVER line (passes all four — and is hook-enforced via T2). The CI regression test (AC-21) mechanically prevents the failure mode the ALWAYS line was guarding against.

**Alternatives considered:**
- Keep in CLAUDE.md verbatim — fails Q1; risks failing the next governance audit; costs a precious line under the 100-line cap.
- Move to WORKFLOW.md — also fine; the new "Dispatch routing" section (AC-25) covers the guidance in prose where developers will look when editing pipeline code.

**Consequences:**
- Positive: CLAUDE.md stays minimal. The NEVER line + CI regression test + WORKFLOW.md section are sufficient.
- Negative: Slightly less prominent guidance. Mitigated by the test failing loudly in CI on any new `claude -p` call site.

### Why Companion BACKLOG.md Cleanup in This PR Instead of a Separate Doc Issue?

**Context:** `docs/BACKLOG.md` has reference tables for GitHub state (labels, epics, priorities, milestones) that have drifted from reality. The same label drift surfaced during this issue's triage (used `area:workflow` and `epic:agents-dashboard` which aren't in the BACKLOG.md tables). Fixing BACKLOG.md is a 30-minute prose edit that depends on no code; bundling it avoids a second PR cycle.

**Decision:** Fold the BACKLOG.md cleanup into this PR. Delete the six label-reference subsection tables (type, area, epic, priority, status, Other); replace with `gh` commands. Prose conventions preserved.

**Alternatives considered:**
- File a separate `type:docs` issue — adds coordination overhead with no isolation benefit; the cleanup is trivial.
- Skip and let the tables drift further — predictable future bug.

**Consequences:**
- Positive: Removes a known source of drift; GitHub becomes the single source of truth as it should be.
- Negative: PR diff includes a doc change tangential to the dispatch refactor. Mitigated by clear PR description.

### Why Demote the Pool-Inspection Check from an AC to a Premise Note?

**Context:** Whether `claude --bg` from a Python daemon actually counts as interactive pool is an empirical question about Anthropic's billing implementation, not a behavior of PPDS code. A one-shot manual dashboard inspection cannot serve as a regression test.

**Decision:** Move the pool-inspection statement to "Premise validation" in Overview (one-shot operator check). Replace with AC-28, which asserts that `.claude/state/sdk-spend.jsonl` contains zero rows from the AC-27 pipeline invocations — that is the testable proxy (no headless crossing during the validation run).

**Alternatives considered:**
- Keep pool inspection as an AC — fails Constitution I3 (not testable by a single test method).
- Drop the validation entirely — loses the empirical confirmation step.

**Consequences:**
- Positive: AC-28 is automatable; the premise check stays as a documented one-time gate.
- Negative: If Anthropic's billing implementation changes silently, the premise note becomes stale. Mitigated by the dispatcher itself remaining correct under any billing outcome.

---

## Configuration

| Setting | Type | Required | Default | Description |
|---------|------|----------|---------|-------------|
| `PPDS_DISPATCH_MODE` | env (`interactive` \| `headless`) | No | `interactive` | Process-wide override. CLI `--mode` flag takes precedence when both present. |
| `.claude/state/sdk-spend.jsonl` | append-only file | No | created on first headless dispatch or first hook fire | Spend audit log. Gitignored. Schema: `{ts: ISO8601, caller: str, model: str|null, agent: str|null, est_input_tokens: int}`. |
| `MIN_VERSION` | constant in `claude_dispatch.py` | — | `(2, 1, 139)` | Min Claude Code version. Bumped only with empirical evidence. |

---

## Related Specs

- [pipeline-observability.md](./pipeline-observability.md) — Heartbeat, stall/hard-ceiling, transcript parsing lineage. `extract_text_from_jsonl` becomes a re-export from `bg_transcript.py`.
- [start-launch.md](./start-launch.md) — Precedent: `--bg` spawn pattern, MIN_VERSION, banner parsing. `start-bg-spawn.py` imports the now-shared `require_min_version`.
- [workflow-enforcement.md](./workflow-enforcement.md) — Pipeline + workflow state.

---

## Changelog

| Date | Change |
|------|--------|
| 2026-05-14 | Initial spec — dispatch routing primitive before Anthropic's 2026-06-15 subscription / Agent SDK billing split. Issue #1048. |

---

## Roadmap

- **Spend dashboard:** read `.claude/state/sdk-spend.jsonl`, aggregate monthly burn vs budget, surface in `dev status`.
- **Per-call token estimate refinement:** parse `--bg` transcript's `usage` block (when present) for more accurate accounting.
- **Auth-expiry detection:** dispatcher inspects stderr for `unauthenticated` patterns and surfaces a dedicated error code instead of generic `DispatchFallbackError`.
- **Premise re-validation:** if Anthropic's billing implementation changes (e.g., `--bg` from a daemon starts counting as Agent SDK pool), the dispatcher stays correct but the default mode and rationale need revisiting. Surfacing this would be a `type:enhancement` issue, not a refactor.

# Workflow Enforcement

**Status:** Draft (v5.0 — pipeline observability: stream-json, multi-signal activity, lock file)
**Version:** 5.0
**Last Updated:** 2026-03-26
**Code:** [.claude/](../.claude/) | [scripts/pipeline.py](../scripts/pipeline.py) | [.claude/hooks/](../.claude/hooks/) | [.claude/skills/](../.claude/skills/)
**Surfaces:** N/A

---

## Overview

A mechanical enforcement system that ensures AI agents follow the PPDS development workflow — from design through PR creation — without human micromanagement. Skills define the required steps. Hooks enforce that steps actually happened before allowing commits and PRs. A workflow state file tracks progress.

### Goals

- **Process compliance without babysitting**: Gates, QA, verification, and code review happen automatically as part of the workflow. The user is involved at design and final review only.
- **Mechanical enforcement**: Critical checkpoints (commit, PR creation) are blocked if required steps were skipped. Prose instructions are backed by hooks that make non-compliance impossible at exit points.
- **Visibility without interruption**: The user can see workflow progress at any time but is not prompted until the work is ready for review.

### Non-Goals

- **Full state machine orchestration**: We enforce outcomes at exit points, not step-by-step sequencing. The AI can work in any order as long as all required steps complete before committing or creating a PR.
- **CI/CD pipeline changes**: GitHub Actions and external CI are out of scope. This spec covers the local development workflow only.
- **Superpowers plugin replacement**: We disable superpowers for this repo and build our own skills, but we do not modify or fork the superpowers plugin itself.

---

## Architecture

### Interactive Mode

```
┌──────────────────────────────────────────────────────┐
│                   Session Start                       │
│  SessionStart hook injects workflow state + sequence  │
└──────────────────┬───────────────────────────────────┘
                   │
                   ▼
┌──────────────────────────────────────────────────────┐
│              Skills (define the workflow)              │
│                                                       │
│  /design → /implement → /gates → /verify → /qa       │
│                          → /review → /converge → /pr  │
│                                                       │
│  Each skill writes timestamps to workflow-state.json  │
└──────────────────┬───────────────────────────────────┘
                   │
                   ▼
┌──────────────────────────────────────────────────────┐
│     .workflow/state.json (gitignored)                 │
│                                                       │
│  Tracks: gates, verify, qa, review, pr timestamps    │
│  Invalidates on new commits (gates must re-run)      │
└──────────────────┬───────────────────────────────────┘
                   │
                   ▼
┌──────────────────────────────────────────────────────┐
│              Hooks (enforce the workflow)              │
│                                                       │
│  git commit  → warn if gates stale (soft)            │
│  gh pr create → block if steps missing (hard)        │
│  session stop → block if steps missing (blocks)      │
└──────────────────────────────────────────────────────┘
```

### Headless Pipeline Mode

```
┌──────────────────────────────────────────────────────┐
│         Pipeline Orchestrator (pipeline.py)           │
│                                                       │
│  Acquires pipeline.lock (PID-based, exclusive)       │
│  Sets PPDS_PIPELINE=1 in subprocess env              │
│  Spawns one claude -p per stage via Popen            │
│    --output-format stream-json for real-time output  │
│  Monitors via polling loop (5s interval)             │
│  Heartbeat every 60s — multi-signal activity:        │
│    output_bytes (JSONL file size)                     │
│    git_changes (working tree modifications)           │
│    commits (rev-list count ahead of main)             │
│  Per-stage timeout → terminate if exceeded           │
│  Stage output → .workflow/stages/{stage}.jsonl       │
│  Post-process → .workflow/stages/{stage}.log (text)  │
│  Checks state.json between stages                    │
│  Releases pipeline.lock on exit (finally)            │
└──────────────────┬───────────────────────────────────┘
                   │ for each stage:
                   ▼
┌──────────────────────────────────────────────────────┐
│   claude -p "/{stage}" --output-format stream-json   │
│                                                       │
│  SessionStart hook → skips behavioral rules          │
│  Skill runs → writes to workflow-state.json          │
│  Stream-json events → stage JSONL file (real-time)   │
│  Stop hook → detects PPDS_PIPELINE → exits 0         │
│  Process exits → pipeline reads exit code            │
└──────────────────────────────────────────────────────┘
```

### Components

| Component | Responsibility |
|-----------|----------------|
| Workflow State File | Tracks which workflow steps have been completed and when. Invalidates stale entries on new commits. |
| SessionStart Hook | Injects current workflow state and required sequence into AI context at session start. Pipeline-aware: skips behavioral rules when `PPDS_PIPELINE=1`. |
| Pre-Commit Hook (enhanced) | Warns if `/gates` hasn't been run since last code changes. Soft gate — does not block. |
| PR Gate Hook | Blocks `gh pr create` unless gates, verify, QA, and review are all current. Hard gate. |
| Stop Hook | Blocks session end when workflow steps are incomplete. Pipeline-aware: exits immediately when `PPDS_PIPELINE=1`. |
| Skills | Define each workflow step. Write their completion status to the workflow state file. |
| Pipeline Orchestrator | `scripts/pipeline.py` — runs stages as sequential `claude -p --output-format stream-json` sessions. Each stage gets a fresh context window. Multi-signal activity monitoring (output bytes, git changes, commits). Worktree lock file prevents concurrent instances. The script — not the AI — decides what runs next. |

### Dependencies

- Depends on: [CONSTITUTION.md](./CONSTITUTION.md)
- Uses patterns from: [architecture.md](./architecture.md)

---

## Specification

### Workflow State File

**Location:** `.workflow/state.json` (added to `.gitignore`)

**Schema:**

```json
{
  "branch": "feat/import-jobs",
  "spec": "specs/import-jobs.md",
  "issues": [602, 596],
  "plan": ".plans/2026-03-16-import-jobs.md",
  "started": "2026-03-16T15:00:00Z",
  "last_commit": "abc1234",
  "gates": {
    "passed": "2026-03-16T16:00:00Z",
    "commit_ref": "abc1234"
  },
  "verify": {
    "ext": "2026-03-16T16:10:00Z",
    "tui": "2026-03-16T16:15:00Z"
  },
  "qa": {
    "ext": "2026-03-16T16:30:00Z"
  },
  "review": {
    "passed": "2026-03-16T16:45:00Z",
    "findings": 0
  },
  "pr": {
    "url": "https://github.com/joshsmithxrm/ppds/pull/123",
    "created": "2026-03-16T17:00:00Z"
  }
}
```

**Core Requirements:**

1. Created when any skill first writes to it (e.g., `/implement` sets `branch`, `spec`, `plan`).
2. `gates.commit_ref` stores the commit SHA that gates were verified against. Hooks compare this to HEAD.
3. **State invalidation on commit:** The Post-Commit Hook (see below) clears `gates.passed` (sets to `null`) after every successful commit because the codebase has changed since gates last ran. `verify`, `qa`, and `review` timestamps are NOT automatically cleared on commit — they track cumulative coverage across the session.
4. Skills are responsible for writing their own entries. No central coordinator.
5. File is gitignored — it is per-session state, not committed.
6. **Converge cycle handling:** `/converge` clears `gates.passed` before starting its fix cycle. After the final fix cycle completes and all fixes are committed, `/converge` runs `/gates` one final time. The Post-Commit Hook clears `gates.passed` on each fix commit, but `/converge`'s final `/gates` run writes a fresh `gates.passed` + `gates.commit_ref` matching the final HEAD. This prevents deadlock: the sequence is always fix → commit → (gates cleared) → final gates → (gates fresh against HEAD).

### Hook Specifications

#### SessionStart Hook

**Trigger:** Session start on any branch.

**Pipeline mode:** When `PPDS_PIPELINE=1` is set, the hook emits only the workflow state summary (checklist) to stderr. It skips behavioral rules ("Don't ask permission", "Run /gates -> /verify -> ...") because the pipeline orchestrator controls stage sequencing. Injecting interactive behavioral rules into headless sessions causes stage bleed-through — the implement stage runs all subsequent stages in one session instead of letting the pipeline manage them.

**Behavior (interactive mode):**
1. Read `.workflow/state.json` if it exists.
2. Read current branch name.
3. Determine workflow state: which steps have been completed, which are stale, which are pending.
4. Inject into AI context:
   - Current branch and workflow state summary.
   - Required workflow sequence (the decision tree from CLAUDE.md).
   - Any stale entries (e.g., "gates passed but new commits since — gates must re-run").
5. If no workflow state file exists, inject the full required workflow sequence as a reminder.
6. If on `main` or `master`: list active worktrees (via `git worktree list`), suggest `/start` for new work. Do NOT skip — provide guidance.

**Output format (feature branch):**
```
WORKFLOW STATE for branch feat/import-jobs:
  ✓ Gates passed (commit abc1234, current)
  ✓ Extension verified
  ✗ TUI not verified
  ✗ QA not completed
  ✗ Review not completed
  Required before PR: /verify tui, /qa, /review
```

#### Pre-Commit Hook (Enhanced)

**Trigger:** PreToolUse on `Bash(git commit:*)`.

**Relationship to existing hook:** The existing `.claude/hooks/pre-commit-validate.py` runs `dotnet build`, `dotnet test`, and `npm run lint` as a hard gate (exits code 2 on failure). That behavior is unchanged. The workflow state check below is added to the same hook file as an additional, non-blocking check that runs after the existing build/test/lint validation.

**Added behavior (workflow state warning):**
1. Check if files under `src/` are staged.
2. If yes, read `.workflow/state.json`.
3. If `gates.commit_ref` does not match the current staging state (i.e., code has changed since gates last ran), emit a warning.
4. **The workflow state warning does not block the commit.** The existing build/test/lint validation continues to block on failure. This is a soft gate — WIP commits during implementation are expected.

**Output on warn:**
```
⚠ Warning: /gates has not been run since your last changes. Run /gates before creating a PR.
```

#### Post-Commit Hook

**Trigger:** PostToolUse on `Bash(git commit:*)`.

**Behavior:**
1. Read `.workflow/state.json` if it exists.
2. Clear `gates.passed` (set to `null`) — the codebase has changed since gates last ran.
3. Update `last_commit` to current HEAD.
4. Write updated state file.

**Implementation:** `.claude/hooks/post-commit-state.py`, triggered via a new `PostToolUse` section in `.claude/settings.json` (see Implementation Notes below).

This is the component responsible for state invalidation on commit (Core Requirement 3). Without it, stale `gates.passed` timestamps would persist across commits.

#### PR Gate Hook

**Trigger:** PreToolUse on `Bash(gh pr create:*)`.

**Behavior:**
1. Read `.workflow/state.json`.
2. Verify ALL of the following:
   - `gates.commit_ref` matches current HEAD (gates ran against the current code).
   - `verify` has at least one surface with a timestamp (visual verification happened).
   - `qa` has at least one surface with a timestamp (blind verification happened).
   - `review.passed` has a timestamp (code review completed).
3. If any check fails, exit code 2 with a specific message listing missing steps.
4. **This is a hard gate.** PR creation is blocked until all checks pass.

**Implementation:** `.claude/hooks/pr-gate.py`, triggered via `.claude/settings.json` PreToolUse matcher on `Bash(gh pr create:*)`.

**Output on block:**
```
PR blocked. Missing workflow steps:
  ✗ /gates not run against current HEAD (last ran against abc1234, HEAD is def5678)
  ✗ /qa not completed for any surface
Run these before creating a PR.
```

#### Stop Hook

**Trigger:** Session end (Stop event).

**Pipeline mode:** When `PPDS_PIPELINE=1` is set, the hook exits 0 immediately. The pipeline orchestrator handles stage sequencing via `state.json` — the Stop hook's workflow enforcement is redundant and harmful in headless mode. Without this bypass, the hook blocks the implement stage from exiting because gates/verify/review haven't run yet (they are separate pipeline stages), creating an infinite retry loop.

**Behavior (all modes — in order):**
1. **Pipeline check (first):** If `PPDS_PIPELINE=1` is set, exit 0 immediately. All subsequent checks are skipped.
2. **Infinite loop guard:** If `stop_hook_active` is set in hook input, exit 0 (prevents re-entry).
3. **Main branch:** If on `main` or `master`, exit 0.
4. Read `.workflow/state.json` if it exists. If missing, exit 0.
5. **Design-only bypass:** If the only changed files (vs main) are under non-code prefixes (`specs/`, `.plans/`, `docs/`, `.claude/`, `README`, `CLAUDE.md`), exit 0 — this was a design session.
6. Check workflow completion. If steps missing, emit `decision: block` with status and next required step.
7. If all steps complete, emit summary to stderr and exit 0.

**Output:**
```
SESSION END — Workflow status for feat/import-jobs:
  ✓ Gates passed
  ✓ Extension verified
  ✗ QA not completed — /qa was never run
  ✗ Review not completed — /review was never run
  ⚠ PR not created
  ⚠ Uncommitted changes in 3 files
```

### Skill Updates

#### Existing Skills — Workflow State Integration

Each skill writes its own entry to `.workflow/state.json` upon successful completion:

| Skill | Writes to state |
|-------|----------------|
| `/implement` | `branch`, `spec`, `plan`, `started`, `implemented`. Interactive mode mandatory tail: runs `/gates` → `/verify` → `/qa` → `/review` → `/converge` after final phase. In pipeline mode (`PPDS_PIPELINE=1`): skips tail — pipeline orchestrator runs subsequent stages as separate sessions. |
| `/gates` | `gates.passed`, `gates.commit_ref` |
| `/verify` | `verify.{surface}` (ext, tui, mcp, cli) |
| `/qa` | `qa.{surface}` |
| `/review` | `review.passed`, `review.findings` |
| `/converge` | Clears `gates.passed` when starting a fix cycle (code is changing). Re-runs gates at end. |

#### Existing Skills — Renames and Restructuring

| Current Name | New Name | Change |
|-------------|----------|--------|
| `/retrospective` | `/retro` | Rename only. |
| `/webview-cdp` | `/ext-verify` | Rename. Update description for discoverability: "How to interact with VS Code extension webview panels for verification — Playwright Electron, screenshots, clicks, keyboard." |
| `/webview-panels` | `/ext-panels` | Rename. Absorb content from `/panel-design`. |
| `/panel-design` | (deleted) | Content merged into `/ext-panels`. |
| `/implement` | `/implement` | Remove all superpowers references (currently references `superpowers:using-superpowers`, `superpowers:dispatching-parallel-agents`, `superpowers:verification-before-completion`, `superpowers:requesting-code-review`, `superpowers:systematic-debugging`). Replace with PPDS-native equivalents. Add mandatory tail (gates → verify → QA → review → converge). Add workflow state writes. |
| `/review` | `/review` | Remove superpowers reference (currently dispatches via `subagent_type: 'superpowers:code-reviewer'`). Dispatch reviewer as a general-purpose subagent with the same isolation constraints (diff + constitution + ACs only, no implementation context). |
| `/converge` | `/converge` | Retain all existing convergence tracking, cycle evaluation, and stall detection. Additionally: add workflow state integration — clear `gates.passed` on cycle start, run `/gates` after final fix cycle, write fresh `gates.passed` + `gates.commit_ref` on completion. |
| `/debug` | `/debug` | Major rewrite. Absorb systematic-debugging discipline from superpowers: 4-phase process (Root Cause → Pattern Analysis → Hypothesis → Implementation), Iron Law (no fixes without investigation), 3-fix escalation rule, red flags table, root-cause tracing technique, defense-in-depth validation, condition-based waiting. Keep PPDS-specific surface detection and build commands. Remove superpowers references. |

#### New Skills

| Skill | Purpose | Key Behavior |
|-------|---------|-------------|
| `/design` | Brainstorm → spec → plan. Replaces `superpowers:brainstorming`. | **Requires worktree** — errors if on main ("Run `/start` first"). Step 1: Load constitution + spec template + search existing specs for overlapping scope (update existing spec if found). Step 2: Brainstorm (one question at a time, explore 2-3 approaches, converge). Step 3: Write spec, run `/review` against it, present spec + findings + fixes to user. Step 4: On approval, write implementation plan to `.plans/`, run `/review` against it, present plan + findings to user. Step 5: On approval, commit spec (plan is gitignored). Step 6: Handoff — offer headless pipeline (`pipeline.py --worktree <path> --from implement`), interactive (`/implement`), or defer. Do NOT use plan mode. |
| `/pr` | Rebase → draft PR → Gemini triage → mark ready → notify. | **Interactive mode:** Rebases on main. Creates draft PR. Polls for Gemini reviews (30s interval, 90s min wait, 5 min max). Triages each comment (fix valid, dismiss invalid with rationale). Replies to EACH comment individually. Converts draft → ready (`gh pr ready`). Notifies user. Writes `pr.url`, `pr.created`, `pr.gemini_triaged` to workflow state. **Pipeline mode:** Orchestration is scripted in `pipeline.py` (see PR Stage Orchestration). Only the triage step invokes AI via the `gemini-triage` agent profile (Sonnet). |
| `/shakedown` | Multi-surface product validation. | Structured phases: scope declaration → test matrix creation → interactive verification per surface → parity comparison → architecture audit → findings document. Requires explicit test matrix before testing begins. Collaborative (user + AI). Outputs findings to `docs/qa/`. |
| `/write-skill` | Author new skills following PPDS conventions. | Encodes naming convention (`{action}` or `{action}-{qualifier}`, kebab-case). Encodes directory structure (skills/ with SKILL.md + supporting files). Encodes frontmatter patterns. Encodes description writing for AI discoverability. Encodes integration with workflow state (when and how to write state entries). |
| `/mcp-verify` | How to verify MCP tools. | Supporting knowledge for `/verify` and `/qa`. Documents: MCP Inspector usage, direct tool invocation patterns, response validation, session option testing. |
| `/cli-verify` | How to verify CLI commands. | Supporting knowledge for `/verify` and `/qa`. Documents: build and run patterns, stdout (data) vs stderr (status), exit code validation, pipe testing. |
| `/status` | Display current workflow state with live pipeline monitoring. | Reads `.workflow/state.json` and displays the same summary as SessionStart hook. When a pipeline is running (`.workflow/pipeline.lock` exists with live PID): parses `.workflow/pipeline.log` for stage progress and last heartbeat; parses the active stage's `.jsonl` file to show current tool call in progress, files created/modified this stage, commits made, elapsed time, and last activity timestamp. When no pipeline is running: reads `.workflow/stages/{stage}.log` for completed stage summaries. No state writes. |
| `/start` | Bootstrap a feature worktree. | Accepts freeform input (issues, descriptions). AI extracts candidate name + issue numbers, proposes to user for confirmation. Creates worktree at `.worktrees/<name>` with branch `feat/<name>`, initializes `.workflow/state.json` with `branch`, `started`, and `issues` fields, opens new terminal using system default shell in worktree directory. Prints "Run `claude` then `/design`". Handles: existing branch (no `-b`), existing worktree (ask resume or new), platform detection for terminal launch (pwsh on Windows, default shell on Linux/Mac), missing terminal command (prints cd instructions instead). **Worktree-aware:** Works from any branch — if on a feature branch/worktree, resolves the main repo root via `git worktree list` and creates the new worktree from there. |

### Main Branch Bootstrap

The workflow enforcement system must prevent accidental work on main and guide users to feature worktrees.

#### SessionStart Hook on Main (modified)

**Current behavior:** Skip entirely on main/master (exit 0).

**New behavior:** On main/master, show active worktrees and guidance:

```
You are on main. Active worktrees:
  .worktrees/panel-env-connref  [feat/panel-env-connref]
  .worktrees/panel-metadata     [feat/panel-metadata]

To start new work: /start <feature-name>
Planning and exploration are fine on main. Implementation requires a worktree.
```

If no worktrees exist, skip the list, show `/start` guidance only.

#### Pre-Commit Guard on Main (modified)

**Current behavior:** Pre-commit hook runs build/test/lint, then soft workflow warning.

**New behavior:** Before build/test/lint, check branch. If on main/master:

```
❌ Cannot commit to main. Use /start <name> to create a feature worktree.
```

Exit code 2 (hard gate). Build/test/lint do not run.

### Headless Pipeline Mode

The pipeline orchestrator (`scripts/pipeline.py`) runs the full workflow as sequential `claude -p` sessions. Each stage gets a fresh context window. The script — not the AI — decides what runs next.

#### Hook Commands — Relative Paths

All hook commands in `.claude/settings.json` use relative paths:
```
python ".claude/hooks/session-stop-workflow.py"
```

Not:
```
python "${CLAUDE_PROJECT_DIR}/.claude/hooks/session-stop-workflow.py"
```

Claude Code runs hooks with `cwd` set to the project directory, so relative paths resolve correctly. This eliminates the entire class of MSYS `${CLAUDE_PROJECT_DIR}` expansion bugs on Windows — there is no variable to mangle. The `_pathfix.py` module is retained for use inside hook scripts that reference `CLAUDE_PROJECT_DIR` for other purposes (e.g., finding `.workflow/state.json`).

**Root cause this fixes:** Under MSYS bash, Claude expands `${CLAUDE_PROJECT_DIR}` to `/c/VS/...`, which MSYS mangles to `C:\c\VS\...` (double prefix). Python can't find the hook script at the mangled path. The hook fails, failure is sent back to Claude as user input, Claude responds, triggering another stop attempt — infinite loop (observed: 1,000-3,000+ iterations per session, hours of wall time).

#### Pipeline Environment

The pipeline sets these environment variables before spawning `claude -p`:

| Variable | Value | Purpose |
|----------|-------|---------|
| `PPDS_PIPELINE` | `1` | Signals headless pipeline mode to hooks. Stop hook exits 0 immediately. Start hook skips behavioral rules. |
| `MSYS_NO_PATHCONV` | `1` | Prevents MSYS bash from mangling paths in command arguments. |
| `CLAUDE_PROJECT_DIR` | Windows-native path via `Path.resolve()` | Defense-in-depth for hook scripts' internal `_pathfix.get_project_dir()` usage. |

#### Process Management

The pipeline uses `subprocess.Popen` with file redirect and a polling loop — not `subprocess.run` with `capture_output=True`.

**Why:** `subprocess.run(capture_output=True, timeout=None)` buffers all output in memory and blocks until process exit. If the subprocess hangs, the pipeline gets zero output and blocks forever. Observed: 0-byte output files, hours of blocking.

**Subprocess launch:**
```python
proc = subprocess.Popen(
    ["claude", "-p", prompt, "--verbose",
     "--output-format", "stream-json"],
    cwd=worktree_path,
    stdout=stage_log_file,      # File redirect, not PIPE
    stderr=subprocess.STDOUT,    # Merge stderr into stdout
    env=env,
)
```

The `--output-format stream-json` flag is critical: it causes `claude -p` to emit one JSON object per line as events occur (tool calls, text chunks, system messages). Without it, `claude -p` in default text mode buffers the entire response and writes to stdout only at process exit — producing 0-byte stage logs throughout execution regardless of actual agent activity.

stdout and stderr merge into `.workflow/stages/{stage}.jsonl`. No PIPE — eliminates buffer deadlock class of bugs. Stage JSONL is readable in real-time by `/status` or manual `tail -f`.

**Polling loop (5-second interval):**
1. `process.poll()` — detect exit
2. Timeout check — terminate if elapsed > stage timeout
3. Heartbeat every 60s — multi-signal activity detection (see below)

**Multi-signal activity detection:** The heartbeat checks three independent signals every 60s:

| Signal | How | What it proves |
|--------|-----|----------------|
| `output_bytes` | `os.path.getsize(stage_jsonl_path)` | Agent process is running and streaming events |
| `git_changes` | `git status --porcelain \| wc -l` | Agent is modifying files in the worktree |
| `commits` | `git rev-list --count main..HEAD` | Agent has committed work product |

Activity classification:
- **active**: `output_bytes` increased since last heartbeat OR `git_changes` increased OR `commits` increased
- **idle**: none of the three signals changed since last heartbeat
- **stalled**: idle for 3+ consecutive heartbeats (180s) — reported as `activity=stalled` in the heartbeat line (no separate warning entry; the field value itself is the signal)

Git subprocess calls use `timeout=5` to avoid blocking the polling loop. If git times out or errors, that signal is skipped (not treated as idle).

**Post-process stage output:** After the subprocess exits, the pipeline:
1. Parses the JSONL file to extract the final assistant text response
2. Writes the extracted text to `.workflow/stages/{stage}.log` as a human-readable summary
3. Logs the last 20 lines of the human-readable `.log` (not the raw JSONL) to `pipeline.log`

This gives both: raw JSONL for tooling/debugging, and plain text for humans.

**Stream-json event format:** Each line in the JSONL file is a JSON object. The relevant event types for parsing:

```jsonl
{"type":"system","subtype":"init","session_id":"...","tools":[...],"model":"..."}
{"type":"assistant","message":{"role":"assistant","content":[{"type":"tool_use","id":"...","name":"Edit","input":{...}}]}}
{"type":"result","subtype":"success","result":"Final assistant text response here","session_id":"..."}
```

The post-processor extracts text from `type: "result"` events (the `result` field contains the final response text). The `/status` parser reads `type: "assistant"` events to identify in-progress tool calls (content blocks with `type: "tool_use"` and their `name` field). Lines that fail `json.loads` are silently skipped (handles partial writes and non-JSON stderr lines).

**Timeout enforcement:** On timeout, `process.terminate()` sends SIGTERM (Unix) or TerminateProcess (Windows). Wait 30s grace period on Unix, then `process.kill()` if still alive. On Windows, `terminate()` and `kill()` both call TerminateProcess — no grace period.

#### Pipeline Lock File

**Location:** `.workflow/pipeline.lock`

**Purpose:** Prevents concurrent pipeline instances on the same worktree. Observed failure: two `pipeline.py` processes running converge-r1 simultaneously on `plugin-registration`, producing interleaved heartbeats from two PIDs and racing on the same files.

**Behavior:**
1. On startup, check if `.workflow/pipeline.lock` exists.
2. If it exists, read the PID from the file. Check if that PID is alive (`os.kill(pid, 0)` on Unix, `OpenProcess` on Windows via `ctypes` or `psutil`-free approach).
3. If PID is alive: print error message with the existing PID and exit 1. Do not kill the other process.
4. If PID is dead (stale lock): log a warning, delete the stale lock, and proceed.
5. Write current PID to the lock file.
6. Delete the lock file in the `finally` block of `main()`.

**Lock file format:** Single line containing the PID as a plain integer. No JSON, no metadata.

**Cross-platform PID liveness check:**
```python
def is_pid_alive(pid):
    """Check if a process with the given PID is alive. Cross-platform."""
    try:
        os.kill(pid, 0)  # Signal 0 = check existence, don't kill
        return True
    except ProcessLookupError:
        return False
    except PermissionError:
        return True  # Process exists but we can't signal it
    except OSError:
        return False
```

Note: `os.kill(pid, 0)` works on Windows in Python 3.x — it calls `OpenProcess` internally.

#### Default Stage Timeouts

| Stage | Timeout | Rationale |
|-------|---------|-----------|
| implement | 45 min | Largest stage — multiple phases, many commits |
| gates | 15 min | Build + unit tests |
| verify | 20 min | Multi-surface verification |
| qa | 20 min | Blind verification (three-agent) |
| review | 15 min | Code review analysis |
| converge (per round) | 15 min | Fix + re-gate per round |
| pr | 10 min | PR creation and Gemini triage |
| retro | 10 min | Post-mortem analysis |

Overridable via `--stage-timeout <seconds>` CLI flag (applies to all stages).

#### PR Stage Orchestration (Pipeline Mode)

In pipeline mode, the PR stage is split: **Python scripts the orchestration, AI handles only triage judgment.** This replaces the current approach where a single expensive agent session polls for reviews, wastes tokens sleeping, and races on notification timing.

**Pipeline PR flow:**

```
pipeline.py run_pr_stage():
│
├─ 1. Rebase on main
│     git fetch origin main && git rebase origin/main
│     On conflict: log FAILED, exit (no auto-resolve)
│
├─ 2. Read linked issues from state.json
│     python scripts/workflow-state.py get issues
│
├─ 3. Create draft PR
│     gh pr create --draft --title "..." --body "..."
│     Log: PR_CREATED url={url} draft=true
│
├─ 4. Poll for Gemini review (Python, no AI)
│     Loop: gh api repos/.../pulls/{N}/comments --jq 'length'
│     Interval: 30s, min wait: 90s, max wait: 5 min
│     Log: GEMINI_POLL attempt={N} comments={count}
│
├─ 5. Invoke triage agent (AI, focused)
│     claude -p --agent gemini-triage --output-format stream-json
│     Prompt includes: spec content, Gemini comments (JSON),
│       git diff --stat, instruction to output structured result
│     Agent: reads code, fixes or dismisses, commits, pushes
│     Stage log: .workflow/stages/pr-triage.jsonl
│
├─ 6. Post threaded replies (Python, from agent output)
│     For each comment: gh api .../pulls/{N}/comments -F in_reply_to={id} -f body="..."
│     Log: REPLY_POSTED comment_id={id} action={fixed|dismissed}
│
├─ 7. Convert draft → ready
│     gh pr ready {N}
│     Log: PR_READY url={url}
│     (GitHub sends reviewer notification HERE, not at step 3)
│
├─ 8. Write workflow state
│     pr.url, pr.created, pr.gemini_triaged
│
├─ 9. Write result + notify
│     .workflow/pipeline-result.json (summary)
│     python .claude/hooks/notify.py --title "PR Ready" --url {url}
│
└─ 10. Log summary to pipeline.log
```

**Minimum Gemini wait (90s):** The pipeline waits at least 90 seconds before first checking for comments, even if comments appear earlier in the API. This eliminates the race condition where the agent checks before Gemini has finished posting all its comments. Observed: Gemini consistently takes 2-3 minutes but individual comments may appear incrementally.

**Triage agent prompt construction:** Pipeline.py reads the spec path from state.json, fetches the Gemini comments as structured JSON, generates a `git diff --stat` summary, and constructs the triage prompt:

```
Triage these Gemini review comments on PR #{number}.

Spec (read for design rationale): {spec_path}

Comments:
{json array of {id, path, line, body} per comment}

For each comment:
1. Read the referenced file at the specified line
2. Evaluate: is this a valid finding (real bug, correct suggestion)?
3. If valid: fix the code and commit
4. If invalid: compose a brief dismissal rationale

After processing all comments, output this JSON to stdout:
[{"id": <comment_id>, "action": "fixed"|"dismissed", "description": "...", "commit": "<sha>"|null}]
```

**Interactive mode (`/pr` skill):** The `/pr` skill continues to handle everything in a single session for interactive use. Updated to use `--draft` and `gh pr ready` for correct notification timing, but the agent still polls and triages directly. The scripted orchestration only applies to pipeline mode.

**Pipeline failure notification:** When any pipeline stage fails (not just PR), `pipeline.py` writes `.workflow/pipeline-result.json`:

```json
{
  "status": "failed",
  "failed_stage": "converge",
  "duration": 2700,
  "pr_url": null,
  "error": "max converge rounds exceeded",
  "stages": {"implement": "975s", "gates": "300s", "verify": "275s"},
  "timestamp": "2026-03-26T23:30:11Z"
}
```

On success:
```json
{
  "status": "complete",
  "duration": 3600,
  "pr_url": "https://github.com/.../pull/699",
  "stages": {"implement": "975s", "gates": "300s", ...},
  "timestamp": "2026-03-26T23:43:37Z"
}
```

Both success and failure invoke `notify.py` if it exists (best-effort, non-blocking).

#### Gemini Triage Agent Profile

**Location:** `.claude/agents/gemini-triage.md`

```markdown
---
name: gemini-triage
model: sonnet
allowedTools:
  - Read
  - Edit
  - Write
  - Bash
  - Grep
  - Glob
---
You triage Gemini review comments on a PR. You receive structured
comments with file paths and line numbers.

For each comment:
1. Read the referenced file at the specified line
2. Check the spec (path provided in prompt) for design rationale
3. Check CONSTITUTION.md for applicable principles
4. If valid finding: fix the code, commit with message "fix: address Gemini review — {description}"
5. If invalid: note dismissal rationale (e.g., "No generated constant exists for this entity")

After all comments are processed, push fixes and output JSON:
[{"id": <comment_id>, "action": "fixed"|"dismissed", "description": "...", "commit": "<sha>"|null}]

Do not create PRs, post comments, or modify workflow state — the pipeline handles that.
```

**Why Sonnet:** Gemini triage is mechanical — read comment, check code, fix or dismiss. It doesn't require Opus-level reasoning. Sonnet reduces cost per triage by ~5x while maintaining code editing quality. If a triage fix fails gates, the converge loop catches it.

**Version pinning:** `model: sonnet` floats to the latest Sonnet version intentionally. Triage quality is validated by the converge loop (bad fixes get caught by gates/review), so version drift is self-correcting. Pinning would require manual updates and provide minimal stability benefit for this use case.

**Tool restrictions:** No Agent (can't spawn subagents), no web access. The triage agent reads code, edits code, runs git commands. Nothing else.

#### Pipeline Log Format

Existing format preserved. Heartbeat entries extended with multi-signal fields:

```
2026-03-26T20:30:17Z [implement] START
2026-03-26T20:31:17Z [implement] HEARTBEAT elapsed=60s pid=12345 output_bytes=45230 git_changes=3 commits=1 activity=active
2026-03-26T20:32:17Z [implement] HEARTBEAT elapsed=120s pid=12345 output_bytes=102400 git_changes=5 commits=2 activity=active
2026-03-26T20:33:17Z [implement] HEARTBEAT elapsed=180s pid=12345 output_bytes=102400 git_changes=5 commits=2 activity=idle
2026-03-26T20:36:17Z [implement] HEARTBEAT elapsed=360s pid=12345 output_bytes=102400 git_changes=5 commits=2 activity=stalled
2026-03-26T20:45:00Z [implement] OUTPUT line=<last 20 lines of human-readable stage log>
2026-03-26T20:45:00Z [implement] DONE exit=0 duration=887s
```

New fields: `git_changes` (count of modified/untracked files), `commits` (rev-list count ahead of main), `activity` now includes `stalled` state (idle for 3+ consecutive heartbeats).

#### Stage Log Files

**Raw output:** `.workflow/stages/{stage}.jsonl`

- One file per stage invocation (overwritten on retry)
- Contains stream-json output from `claude -p` — one JSON object per line
- Grows incrementally during execution (tool calls, text chunks, system events)
- Readable in real-time via `tail -f` or `/status`
- Gitignored (`.workflow/` is already gitignored)

**Human-readable summary:** `.workflow/stages/{stage}.log`

- Generated after subprocess exits by parsing the JSONL file
- Contains the final assistant text response extracted from stream-json
- Used for the "last 20 lines" capture in pipeline.log
- Used by `/status` for human-readable stage output

### CLAUDE.md Workflow Section Rewrite

Replace the current workflow bullet list with:

```markdown
## Workflow (REQUIRED SEQUENCE)

### New feature or non-trivial change
1. /spec (or verify spec exists with numbered ACs)
2. /spec-audit (verify spec matches codebase reality)
3. Write implementation plan → user approves
4. /implement <plan-path>
5. /gates — STOP on failure, fix before proceeding
6. /verify for EVERY affected surface — you MUST use the product:
   - Extension changed → /ext-verify (screenshots required)
   - TUI changed → /tui-verify (PTY interaction required)
   - MCP changed → /mcp-verify (tool invocation required)
   - CLI changed → /cli-verify (run the command)
7. /qa for at least one affected surface (blind verification)
8. /review → /converge until 0 critical, 0 important
9. /pr (rebase, create PR, monitor CI + reviews)

### Bug fix or small change
1. /gates before committing
2. If UI/output changed → /verify for affected surface
3. /pr when ready

### Enforcement
Steps 5-8 are enforced by hooks. The PR gate hook will block `gh pr create`
if these steps are incomplete. Run `/status` to check current workflow state.

### STOP conditions
- DO NOT skip steps 5-8 because "tests pass." Tests are necessary, not sufficient.
- DO NOT declare work complete without visual verification of affected surfaces.

### Autonomy scope
"Don't ask, just do it" applies to: committing after tasks, running gates,
running verification, running QA, running review, triaging external review
comments (fix valid ones, dismiss invalid ones with rationale).
"Don't ask, just do it" does NOT apply to: skipping any workflow step,
filing/closing issues, creating PRs without passing gates.

After external review: respond to EACH comment individually on the PR with
the action taken (fixed in <commit>, or dismissed with rationale). Include
a summary of all comments and actions in the PR status report.
```

### Superpowers Disposition

After all skills in this spec are implemented:

1. Add to `.claude/settings.json`:
   ```json
   { "enabledPlugins": { "superpowers@claude-plugins-official": false } }
   ```
2. This disables superpowers for the ppds repo only. It remains installed globally for other repos.
3. `/debug` absorbs the only actively-used superpowers skill (`systematic-debugging`, 15 invocations across 12 sessions).

---

## Acceptance Criteria

| ID | Criterion | Test | Status |
|----|-----------|------|--------|
| AC-01 | Workflow state file is created when `/gates` runs and contains `gates.passed` timestamp and `gates.commit_ref` matching HEAD | Manual: run `/gates`, read `.workflow/state.json` | 🔲 |
| AC-02 | Workflow state file is created when `/verify ext` runs and contains `verify.ext` timestamp | Manual: run `/verify ext`, read state file | 🔲 |
| AC-03 | Workflow state file is created when `/qa` runs and contains `qa.{surface}` timestamp | Manual: run `/qa`, read state file | 🔲 |
| AC-04 | Workflow state file is created when `/review` runs and contains `review.passed` timestamp | Manual: run `/review`, read state file | 🔲 |
| AC-05 | `gates.passed` is cleared (set to null) when a new commit is made after gates passed | Manual: run `/gates`, commit, read state file | 🔲 |
| AC-06 | PR Gate hook blocks `gh pr create` when `gates.commit_ref` does not match HEAD | Manual: run `/gates`, commit new code, attempt `gh pr create` | 🔲 |
| AC-07 | PR Gate hook blocks `gh pr create` when `qa` has no entries | Manual: skip `/qa`, attempt `gh pr create` | 🔲 |
| AC-08 | PR Gate hook blocks `gh pr create` when `review.passed` is null | Manual: skip `/review`, attempt `gh pr create` | 🔲 |
| AC-09 | PR Gate hook allows `gh pr create` when all checks pass | Manual: complete full workflow, attempt `gh pr create` | 🔲 |
| AC-10 | SessionStart hook injects workflow state summary into AI context | Manual: start session on feature branch with existing state file | 🔲 |
| AC-11 | SessionStart hook injects required workflow sequence when no state file exists | Manual: start session on new feature branch | 🔲 |
| AC-12 | Stop hook emits workflow completion summary on session end | Manual: end session with incomplete workflow | 🔲 |
| AC-13 | Pre-commit hook warns when gates are stale and `src/` files are staged | Manual: modify src/ file, commit without running `/gates` | 🔲 |
| AC-14 | `/implement` runs `/gates`, `/verify`, `/qa`, `/review`, `/converge` as mandatory tail after final phase (interactive mode only — skipped when `PPDS_PIPELINE=1`) | Manual: run `/implement` on a plan, verify tail steps execute | 🔲 |
| AC-15 | `/pr` responds to each Gemini comment individually on the PR | Manual: create PR with Gemini review, verify per-comment replies | 🔲 |
| AC-16 | `/pr` includes summary of all review comments and actions in status report | Manual: create PR, verify summary output | 🔲 |
| AC-17 | `/design` loads constitution + spec template + searches all `specs/*.md` for overlapping scope before brainstorming; if existing spec found, presents it and proposes update mode | Manual: run `/design` for domain with existing spec, verify search + update-mode proposal | 🔲 |
| AC-18 | `/design` requires worktree (errors on main with "Run `/start` first"), commits spec to worktree branch when approved (plan is gitignored) | Manual: run `/design` on main → error; run in worktree → committed spec + plan | 🔲 |
| AC-19 | Superpowers is disabled for ppds repo after all skills are implemented | Verify `.claude/settings.json` contains `"superpowers@claude-plugins-official": false` | 🔲 |
| AC-20 | `/debug` includes 4-phase systematic debugging process, 3-fix escalation, red flags table | Read `/debug` skill content, verify sections present | 🔲 |
| AC-21 | All renamed skills (`/ext-verify`, `/ext-panels`, `/retro`) are discoverable by AI via natural language | Manual: say "test the extension", verify `/ext-verify` is loaded | 🔲 |
| AC-22 | `/shakedown` requires explicit test matrix before testing begins | Manual: run `/shakedown`, verify matrix creation step | 🔲 |
| AC-23 | `/write-skill` encodes naming convention and outputs skills in correct directory structure | Manual: use `/write-skill` to create a skill, verify output | 🔲 |
| AC-24 | Post-Commit Hook clears `gates.passed` after a successful commit | Manual: run `/gates`, commit, verify `gates.passed` is null in state file | 🔲 |
| AC-25 | `/converge` clears `gates.passed` on fix cycle start | Manual: run `/converge` with findings, verify state cleared before fixes | 🔲 |
| AC-26 | `/converge` writes fresh `gates.passed` and `gates.commit_ref` after final cycle passes | Manual: complete `/converge`, verify state file has fresh gates matching HEAD | 🔲 |
| AC-27 | `.workflow/state.json` is listed in `.gitignore` and not tracked by git | Verify `.gitignore` contains entry, `git status` does not show state file | 🔲 |
| AC-28 | `/implement` contains no superpowers references after rewrite | Read `/implement` skill content, grep for "superpowers" — zero matches | 🔲 |
| AC-29 | `/review` dispatches reviewer without superpowers dependency | Run `/review`, verify subagent launches as general-purpose with isolation constraints | 🔲 |
| AC-30 | `/pr` stops polling after 15 minutes with graceful timeout message | Manual: create PR with slow CI, verify timeout behavior | 🔲 |
| AC-31 | `/status` displays current workflow state summary on demand | Manual: run `/status` mid-session, verify output matches state file | 🔲 |
| AC-32 | `/shakedown` outputs findings document to `docs/qa/` | Manual: complete `/shakedown`, verify findings file created | 🔲 |
| AC-33 | `/shakedown` includes parity comparison across tested surfaces | Manual: run `/shakedown` on 2+ surfaces, verify parity matrix in output | 🔲 |
| AC-34 | `/start` accepts freeform input, AI extracts candidate name + issue numbers, proposes to user for confirmation, then creates `.worktrees/<name>` with branch `feat/<name>` | Manual: run `/start` with description, verify name proposal + worktree creation | 🔲 |
| AC-35 | `/start` initializes `.workflow/state.json` with `branch`, `started`, and `issues` fields | Manual: run `/start`, read state file in worktree | 🔲 |
| AC-36 | `/start` opens a new terminal in the worktree using the system default shell (platform-detected: pwsh on Windows, default shell on Linux/Mac) | Manual: run `/start`, verify terminal opens in worktree directory | 🔲 |
| AC-37 | When a matching worktree already exists, `/start` asks user whether to resume it or create new | Manual: run `/start` with existing worktree name, verify prompt | 🔲 |
| AC-38 | SessionStart hook on main shows active worktrees and `/start` guidance | Manual: start session on main with existing worktrees | 🔲 |
| AC-39 | Pre-commit hook blocks `git commit` on main with guidance message | Manual: attempt commit on main, verify exit code 2 | 🔲 |
| AC-40 | `/start` gracefully handles missing terminal command — prints `cd` + `claude` instructions for user to run manually | Manual: test with terminal launch unavailable | 🔲 |
| AC-41 | `/start` prints "Run `claude` then `/design`" after opening the terminal | Manual: run `/start`, verify guidance message | 🔲 |
| AC-42 | `/design` Step 2 (brainstorm) explicitly explores 2-3 approaches before converging on a direction | Manual: run `/design`, verify multiple approaches proposed | 🔲 |
| AC-43 | `/design` Step 3 writes spec, then runs `/review` against the spec before presenting to user | Manual: complete design brainstorm, verify review runs on spec draft | 🔲 |
| AC-44 | `/design` Step 3 presents spec + review findings + fixes to user (shows what was caught and fixed, not just the clean result) | Manual: verify presentation includes review findings | 🔲 |
| AC-45 | `/design` Step 4 writes implementation plan to `.plans/`, then runs `/review` against the plan before presenting to user | Manual: approve spec, verify plan is written and reviewed | 🔲 |
| AC-46 | `/design` Step 5 presents plan + review findings to user; on approval, commits spec to worktree branch (plan is ephemeral, gitignored) | Manual: approve plan, verify commit in worktree | 🔲 |
| AC-47 | `/design` Step 6 (handoff) offers three options: invoke headless pipeline, continue interactively with `/implement`, or defer | Manual: complete design, verify three options presented | 🔲 |
| AC-48 | `protect-main-branch.py` blocks ALL edits on main — `.plans/` removed from allowed prefixes; only temp dirs and `.worktrees/` writes allowed | Manual: attempt to edit `.plans/` file on main, verify blocked | 🔲 |
| AC-49 | `session-stop-workflow.py` skips workflow enforcement on worktree branches when the only changed files (vs main) are non-code prefixes (`specs/`, `.plans/`, `docs/`, `.claude/`, `README`, `CLAUDE.md`) — design/config sessions end cleanly | Manual: end design session in worktree with only spec changes, verify no enforcement | 🔲 |
| AC-50 | `/design` does not activate plan mode — uses its own incremental approval flow (one question at a time, section-by-section validation) | Manual: run `/design`, verify plan mode is not used | 🔲 |
| AC-51 | All hook commands in settings.json use relative paths `.claude/hooks/` — no `${CLAUDE_PROJECT_DIR}` in any command string | `test_pipeline.py::test_all_hook_commands_use_relative_paths` | 🔲 |
| AC-52 | Stop hook exits 0 immediately when `PPDS_PIPELINE=1` env var is set | `test_pipeline.py::test_stop_hook_exits_in_pipeline_mode` | 🔲 |
| AC-53 | Start hook skips behavioral rules when `PPDS_PIPELINE=1` env var is set (emits only status checklist) | `test_pipeline.py::test_start_hook_skips_rules_in_pipeline_mode` | 🔲 |
| AC-54 | Pipeline sets `PPDS_PIPELINE=1` in subprocess environment | `test_pipeline.py::test_sets_pipeline_env_var` | 🔲 |
| AC-55 | Stage output written to `.workflow/stages/{stage}.log` file (not PIPE) | `test_pipeline.py::test_stage_output_goes_to_file` | 🔲 |
| AC-56 | Pipeline logs heartbeat every 60s with elapsed time, PID, output bytes, and activity status | `test_pipeline.py::test_heartbeat_logging` | 🔲 |
| AC-57 | Per-stage timeout terminates subprocess when exceeded | `test_pipeline.py::test_timeout_kills_subprocess` | 🔲 |
| AC-58 | Exit code logged immediately when subprocess finishes | `test_pipeline.py::test_logs_exit_code_on_completion` | 🔲 |
| AC-59 | Last 20 lines of stage output written to pipeline.log after stage completes | `test_pipeline.py::test_captures_output_tail` | 🔲 |
| AC-60 | `--stage-timeout` CLI flag overrides default stage timeouts | `test_pipeline.py::test_stage_timeout_cli_override` | 🔲 |
| AC-61 | Dry-run mode works with new process management (no subprocess spawned) | `test_pipeline.py::test_dry_run_skips_subprocess` | 🔲 |
| AC-62 | `/start` works from feature branches — resolves main repo root and creates worktree from there | Manual: run `/start` from a worktree, verify new worktree created | 🔲 |
| AC-63 | `/status` shows pipeline stage progress including heartbeat data when pipeline is running | Manual: run `/status` during pipeline execution, verify stage timing shown | 🔲 |
| AC-64 | All `.claude/commands/*.md` files migrated to `.claude/skills/{name}/SKILL.md` with frontmatter | `test_pipeline.py::test_no_commands_directory` | 🔲 |
| AC-65 | Pipeline STAGES includes `qa` between `verify` and `review` | `test_pipeline.py::test_pipeline_stages_include_qa` | 🔲 |
| AC-66 | `/implement` skill skips mandatory tail (gates/verify/qa/review) when `PPDS_PIPELINE=1` is set | `test_pipeline.py::test_implement_skips_tail_in_pipeline_mode` | 🔲 |
| AC-67 | Pipeline spawns `claude -p` with `--output-format stream-json` — stage JSONL file grows incrementally during execution and contains parseable JSON objects with a `type` field | `test_pipeline.py::test_stream_json_output_format` | 🔲 |
| AC-68 | Heartbeat (logged every 60s within the 5s polling loop) includes `git_changes` and `commits` fields alongside `output_bytes` | `test_pipeline.py::test_heartbeat_multi_signal` | 🔲 |
| AC-69 | Activity is `active` when any signal increased, `idle` when none changed, `stalled` after 3+ consecutive idle heartbeats (180s) | `test_pipeline.py::test_activity_classification` | 🔲 |
| AC-70 | After stage exit, JSONL is post-processed to extract assistant text into `.workflow/stages/{stage}.log`; last 20 lines of `.log` (not JSONL) written to pipeline.log | `test_pipeline.py::test_jsonl_post_processing` | 🔲 |
| AC-71 | Pipeline writes current PID to `.workflow/pipeline.lock` on startup | `test_pipeline.py::test_pipeline_lock_write` | 🔲 |
| AC-72 | Pipeline exits with error (exit 1) and message if lock file exists and PID is alive | `test_pipeline.py::test_pipeline_lock_conflict` | 🔲 |
| AC-73 | Pipeline logs warning and removes lock file if lock file exists but PID is dead (stale lock) | `test_pipeline.py::test_pipeline_lock_stale` | 🔲 |
| AC-74 | Pipeline lock is released in `finally` block — released on normal exit, error exit, KeyboardInterrupt, and timeout | `test_pipeline.py::test_pipeline_lock_release` | 🔲 |
| AC-75 | `/status` JSONL parser extracts tool calls, file modifications, and commit count from stream-json fixture data | `test_pipeline.py::test_status_jsonl_parser` | 🔲 |
| AC-76 | `/status` displays elapsed time and last activity timestamp from pipeline.log heartbeat data | `test_pipeline.py::test_status_heartbeat_display` | 🔲 |
| AC-77 | `/status` shows live data when pipeline is running (current tool call, files modified, commits, elapsed time, last activity) | Manual: run `/status` during active pipeline, verify all five data points shown | 🔲 |
| AC-78 | Git subprocess calls in heartbeat use `timeout=5` — a hanging git command does not block the polling loop or delay timeout enforcement | `test_pipeline.py::test_heartbeat_git_timeout` | 🔲 |
| AC-79 | Pipeline PR stage creates PR as draft (`--draft` flag), not ready-for-review | `test_pipeline.py::test_pr_creates_draft` | 🔲 |
| AC-80 | Pipeline PR stage waits minimum 90s before first Gemini comment check — no premature "no comments" | `test_pipeline.py::test_pr_gemini_min_wait` | 🔲 |
| AC-81 | Pipeline PR stage invokes `gemini-triage` agent (Sonnet) with structured prompt containing spec path and comment JSON | `test_pipeline.py::test_pr_triage_agent_invocation` | 🔲 |
| AC-82 | Pipeline PR stage posts threaded replies to each Gemini comment from triage agent output (not top-level PR comment) | `test_pipeline.py::test_pr_threaded_replies` | 🔲 |
| AC-83 | Pipeline PR stage converts draft to ready (`gh pr ready`) only after triage is complete — GitHub notification arrives post-triage | `test_pipeline.py::test_pr_draft_to_ready` | 🔲 |
| AC-84 | `gemini-triage` agent profile exists at `.claude/agents/gemini-triage.md` with model=sonnet and restricted tool set | `test_pipeline.py::test_gemini_triage_agent_exists` | 🔲 |
| AC-85 | Pipeline writes `.workflow/pipeline-result.json` on both success and failure with status, duration, stages, pr_url, and failed_stage (failure includes completed stages up to failure point) | `test_pipeline.py::test_pipeline_result_json` | 🔲 |
| AC-86 | Pipeline invokes `notify.py` on completion (best-effort, non-blocking — failure to notify does not fail the pipeline) | `test_pipeline.py::test_pipeline_notify` | 🔲 |
| AC-87 | Interactive `/pr` skill creates draft PR and converts to ready after triage (same notification timing fix, no agent profile dependency) | Manual: run `/pr` interactively, verify draft→ready flow | 🔲 |
| AC-88 | Pipeline PR stage polls until Gemini comment count is stable on two consecutive 30s polls (after 90s minimum) before invoking triage | `test_pipeline.py::test_pr_gemini_stabilization` | 🔲 |
| AC-89 | Pipeline PR stage stops Gemini polling after 5 minutes maximum, even if comment count is still changing | `test_pipeline.py::test_pr_gemini_max_wait` | 🔲 |
| AC-90 | When Gemini posts no comments within 5 minutes, pipeline converts draft to ready with "Gemini: no review received" in PR body | `test_pipeline.py::test_pr_gemini_timeout_annotation` | 🔲 |
| AC-91 | When triage agent fails or times out, pipeline converts draft to ready with "Gemini triage incomplete — manual review needed" in PR body | `test_pipeline.py::test_pr_triage_failure_annotation` | 🔲 |
| AC-92 | Triage agent pushes fix commits before outputting results — pipeline verifies remote HEAD matches local HEAD before posting replies | `test_pipeline.py::test_pr_triage_push_verify` | 🔲 |

### Edge Cases

| Scenario | Expected Behavior |
|----------|-------------------|
| No workflow state file exists when hook fires | SessionStart: inject full workflow sequence. Pre-commit: no warning. PR gate: block (no evidence of any steps). Stop: no summary. |
| Workflow state file is corrupted/invalid JSON | All hooks: treat as "no state file" — safe default is to block PR and warn. |
| Session is on `main` branch | SessionStart: show active worktrees and `/start` guidance. Pre-commit: block commits (hard gate). PR gate: skip (no PRs from main). |
| Multiple surfaces verified but one is missing | PR gate: passes — requires "at least one" verified surface, not all. The user chose which surfaces to test. |
| User runs `/gates` but doesn't commit — runs `/gates` again | Second run overwrites the first timestamp. No accumulation issues. |
| WIP commit during implementation | Pre-commit warns (soft gate). PR gate is not triggered. Workflow continues. |
| `/converge` fix cycle introduces new commits | `/converge` clears `gates.passed` on start. Post-Commit Hook clears on each fix commit. `/converge` runs final `/gates` after last fix, writing fresh state. No deadlock. |
| Multiple worktrees active simultaneously | Each worktree has its own `.workflow/state.json`. State files are independent — no cross-worktree interference. |
| User runs `/design` on main without a worktree | Errors with "Run `/start` first" message. No spec or plan is created. |
| User runs `/start` with uninterpretable input | AI asks for clarification. If still unclear, proposes a generic name and asks for confirmation. |
| `/pr` CI or Gemini review takes longer than 15 minutes | `/pr` stops polling, reports current status and what's still pending. User can re-check later with natural language. |
| Pipeline stage timeout exceeded | Process terminated, `TIMEOUT` logged to pipeline.log, pipeline exits 1. |
| Pipeline subprocess crashes immediately (exit 1 in <1s) | Exit code logged, pipeline proceeds to failure handling. |
| Pipeline subprocess completes work but process doesn't exit | Timeout fires, process killed, pipeline logs TIMEOUT and continues to next stage if outcome verified. |
| Stage log directory missing | Auto-created before subprocess launch. |
| `claude` command not found on PATH | `FileNotFoundError` caught, `ERROR` logged, pipeline exits 1. |
| `/start` run from worktree | Resolves main repo root via `git worktree list`, creates new worktree from there. Current session unaffected. |
| Hook command with MSYS path mangling | Cannot happen — relative paths have no `${CLAUDE_PROJECT_DIR}` to mangle. |
| Two pipelines run on same worktree simultaneously | Lock file prevents this. Second pipeline prints "Pipeline already running (PID XXXX)" and exits 1. |
| `PPDS_PIPELINE=1` set manually in interactive session | Stop hook exits immediately, skipping workflow enforcement. Start hook skips behavioral rules. User gets a degraded experience — this is intentional only for pipeline use. |
| Stage log file locked by another process (Windows) | Popen fails to open file — caught as OSError, logged as ERROR, pipeline exits 1. |
| Pipeline killed without releasing lock (kill -9, power loss) | Next pipeline run detects stale lock via PID liveness check. Logs warning "Stale lock from dead PID XXXX, removing", deletes lock, proceeds normally. |
| Git not responsive during heartbeat (index.lock, large repo) | Git subprocess calls use `timeout=5`. On timeout, that signal is skipped for this heartbeat. Activity classification uses remaining signals. Polling loop is not blocked. |
| JSONL file contains malformed lines (partial write on crash) | Post-processor skips unparseable lines with `json.loads` in try/except. Partial data is better than no data. |
| Stage produces no assistant text (crash before first response) | Post-processor writes empty `.log` file. Pipeline.log OUTPUT section shows "no output captured". |
| `/status` run when no pipeline active | Shows completed stage summaries from `.log` files and workflow state. No JSONL parsing attempted. |
| `/status` run during active stage | Parses JSONL up to current file size (no file locking needed — append-only writes). Shows last tool call, file count, commit count. |
| JSONL file has a partial (unterminated) last line during active write | `/status` parser and post-processor both use `json.loads` in try/except — incomplete final line is silently skipped. Next read picks up the completed line. |
| Lock file contains PID that was recycled to an unrelated process | Lock is treated as live (false positive). Acceptable trade-off — PID recycling within pipeline lifetime (~1 hour max) is rare. User can manually delete `.workflow/pipeline.lock` if needed. |
| Gemini posts no review comments within 5 minutes | Pipeline logs `GEMINI_TIMEOUT`, skips triage, converts draft → ready. PR body notes "Gemini: no review received." |
| Gemini posts comments incrementally (1 comment at 2 min, 2nd at 3 min) | 90s minimum wait + polling until comment count stabilizes (same count on 2 consecutive polls) ensures all comments are captured before triage. |
| Triage agent fails or times out | Pipeline posts no replies, converts draft → ready anyway. PR body updated with "Gemini triage incomplete — manual review needed." Pipeline continues to retro. |
| Rebase has conflicts in pipeline PR stage | Pipeline logs `REBASE_CONFLICT`, writes result.json with `status: "failed"`, notifies user. Does not auto-resolve. |
| `notify.py` missing or fails | Best-effort — pipeline logs warning and continues. Notification is not a hard dependency. |

---

## Design Decisions

### Why Hook Gates Over Full State Machine?

**Context:** Need to enforce workflow compliance without over-constraining the AI or breaking on edge cases (hotfixes, partial work, WIP commits).

**Decision:** Enforce outcomes at exit points (commit, PR), not step-by-step sequencing.

**Alternatives considered:**
- **Prose-only enforcement (Approach A):** Two retrospectives proved this fails. AI optimizes for completion over compliance and skips steps when not mechanically blocked.
- **Full state machine (Approach C):** Every transition gated, can't proceed without completing current step. Too brittle for real development (hotfixes, partial work, context switches). Over-engineered for the actual failure mode, which is "steps don't happen at all" not "steps happen out of order."

**Consequences:**
- Positive: Simple, targeted, solves the actual problem. AI can work flexibly within phases.
- Negative: Cannot enforce skill invocation order. Cannot verify that screenshots were actually examined vs. opened and closed. Relies on skills honestly writing to state file.

### Why Soft Gate on Commit, Hard Gate on PR?

**Context:** Commits happen frequently during implementation, including WIP saves. PRs are the exit point where work becomes visible to others.

**Decision:** Warn on commit (don't block), block on PR (hard gate).

**Alternatives considered:**
- **Hard gate on every commit:** Would block WIP commits, frustrating during implementation. Would require a "WIP mode" escape hatch, defeating the purpose.
- **No commit gate at all:** Misses the opportunity to remind the AI that gates need re-running.

**Consequences:**
- Positive: Natural workflow — implement freely, enforce before shipping.
- Negative: AI could accumulate many commits without running gates. Mitigated by SessionStart hook showing stale state.

### Why Disable Superpowers Instead of Layering?

**Context:** Superpowers provides generic process skills. PPDS has domain-specific skills that cover the same ground but with codebase awareness.

**Decision:** Disable superpowers for this repo after replacing all used skills.

**Evidence:**
- 74% of superpowers invocations (78/106) are skills we're replacing: `writing-plans` (34), `brainstorming` (24), `subagent-driven-development` (20).
- The "safety net" skills (`verification-before-completion`, `test-driven-development`, `finishing-a-development-branch`) have zero invocations — they never fire.
- Only `systematic-debugging` (15 invocations) provides value we don't replicate, and it's being absorbed into `/debug`.

**Alternatives considered:**
- **Keep as safety net:** Data shows the safety net skills never trigger. Keeping superpowers adds token overhead (session start hook, skill discovery) for zero demonstrated value.

**Consequences:**
- Positive: Full control over skill behavior. No external dependency changes breaking our workflow. Reduced token overhead.
- Negative: Lose automatic access to future superpowers improvements. Mitigated by periodic review of superpowers changelog.

### Why Relative Hook Paths Instead of Fixing `${CLAUDE_PROJECT_DIR}`?

**Context:** `${CLAUDE_PROJECT_DIR}` is expanded by Claude Code's internal system before the shell processes the command. Under MSYS bash on Windows, this produces double-prefixed paths (`/c/VS/...` becomes `C:\c\VS\...`). The ed632d61e fix attempted to set `CLAUDE_PROJECT_DIR` to a Windows-native path in the environment, but Claude Code computes its own project directory from `cwd`, ignoring the env var for hook command expansion.

**Decision:** Use relative paths. Claude runs hooks with `cwd=project_dir`, so `.claude/hooks/foo.py` resolves correctly without any variable expansion. No variable = no mangling.

**Evidence:** ed632d61e fix applied to both cmt-parity and plugin-registration branches. Both still failed with identical path mangling. Transcript analysis: 1,614 stop hook errors in cmt-parity, 515 in plugin-registration, all showing `C:\c\VS\...` mangled path.

**Alternatives considered:**
- Fix path inside `_pathfix.py`: Script never loads — Python can't find the file at the mangled path
- Set `CLAUDE_PROJECT_DIR` in env: Tried in ed632d61e, Claude ignores it for hook expansion
- Hardcode absolute Windows paths: Not portable

### Why File Redirect Instead of PIPE?

**Context:** The original pipeline used `subprocess.run(capture_output=True)`, which buffers ALL output in memory via PIPE. If the process hangs, nothing is returned — the 0-byte output file in the original bug confirms this.

**Decision:** Redirect stdout/stderr to files on disk. Output is visible in real-time. No buffer limits. No deadlock possible.

**Evidence:** Three stuck pipeline instances observed with 0 bytes captured. Python processes at 0.03-0.06s total CPU over hours — completely blocked inside `subprocess.run` waiting for data that never comes.

**Alternatives considered:**
- PIPE with reader threads: Adds threading complexity — the exact class of problems that cause hangs on Windows
- PIPE with `communicate()`: Blocks until exit, same as `subprocess.run`

### Why Stream JSON Instead of Text Output?

**Context:** v4.0 introduced file redirect to solve PIPE deadlock, but stage logs remained 0 bytes throughout execution. Root cause: `claude -p --output-format text` (the default) buffers the entire response in memory and writes to stdout only at process exit. The file redirect solved the pipe problem but not the observability problem — the output simply doesn't exist until the process finishes.

**Evidence:** Four concurrent pipelines observed on 2026-03-26. Every active stage showed `output_bytes=0 activity=idle` in heartbeats despite confirmed agent activity (commits, file modifications, 500-800MB memory usage). Stage logs: tui-refactoring implement.log 0 bytes (8 files modified, 2 commits made), plugin-registration converge-r1.log 0 bytes (3 files modified), v1-polish gates.log 0 bytes, cmt-parity qa.log 0 bytes. 100% false-negative rate on activity detection.

**Decision:** Use `--output-format stream-json`. This flag causes `claude -p` to emit one JSON object per line as events occur — tool calls, text chunks, system messages. The file grows incrementally throughout execution, providing real-time observability.

**Trade-off:** Raw stage output is JSONL, not human-readable. Mitigated by post-processing: after process exit, extract assistant text into a companion `.log` file. Both exist: JSONL for tooling, plain text for humans.

**Alternatives considered:**
- `--verbose` alone: Verbose output may add some stderr, but the core problem is that text-mode stdout is empty until exit. Verbose doesn't change this.
- Git-only monitoring (no output format change): Detects file changes but provides no visibility into agent reasoning, API calls, or tool execution. Also fails for stages that don't modify files (review, qa). Chosen as a complementary signal, not a replacement.

### Why Multi-Signal Activity Detection?

**Context:** Single-signal detection (output bytes only) has a 100% false-negative rate with text output format, and would still miss activity during brief pauses in stream-json output (e.g., waiting for API response).

**Decision:** Three independent signals: output bytes, git working tree changes, git commit count. Activity is `active` if ANY signal increased. This provides resilience — even if one signal fails or stalls, the others catch real work.

**Evidence:** The tui-refactoring pipeline had 0 output bytes but 8 modified files and 2 new commits. Git signals alone would have correctly classified it as `active`. Conversely, review/qa stages may not modify files — output bytes (via stream-json) catches those.

**Alternatives considered:**
- Process CPU/memory monitoring: Platform-specific, noisy (GC spikes look like activity), and doesn't distinguish "agent thinking" from "agent stuck in a loop"
- File system watchers (inotify/ReadDirectoryChanges): Platform-specific, complex, overkill for 60s polling interval

### Why Pipeline Lock File?

**Context:** Two `pipeline.py` instances were observed running simultaneously on the `plugin-registration` worktree. Pipeline.log showed interleaved heartbeats from PIDs 55640 and 25424, both in converge-r1. Both agents were modifying the same files, racing on state.json, and producing corrupted interleaved stage logs.

**Decision:** PID-based lock file (`.workflow/pipeline.lock`). Simple, no external dependencies, cross-platform. Stale lock detection via `os.kill(pid, 0)`.

**Alternatives considered:**
- `fcntl.flock` / `msvcrt.locking`: Not cross-platform without abstraction layer. `flock` doesn't exist on Windows.
- Named mutex (Windows) / POSIX semaphore: Over-engineered for a single-file orchestrator. Requires cleanup on crash.
- Just document "don't do this": We already documented it in v4.0. Users did it anyway. Mechanical enforcement needed.

### Why Script PR Orchestration Instead of Agent-Driven?

**Context:** The v4.0 PR stage ran everything in a single `claude -p "/pr"` session. The agent created the PR, then sat in a `gh api` polling loop waiting for Gemini reviews — burning Opus tokens on `sleep 30`. The agent sometimes checked too early and reported "no comments to address" before Gemini had finished posting. Users received GitHub notifications at PR creation, not after triage was complete.

**Evidence:** PR #699 (plugin-registration, 2026-03-26): User received notification immediately at PR creation, went to review, found un-triaged Gemini comments. Agent eventually fixed both comments 7 minutes later. The notification timing was wrong, and the agent wasted ~5 minutes of Opus tokens sleeping.

**Decision:** Split into scripted orchestration (Python) + focused triage (Sonnet agent). Python handles: draft PR creation, Gemini polling (no AI tokens), reply posting, draft→ready conversion, notification. AI handles only: reading each comment, judging fix vs dismiss, writing code fixes.

**Consequences:**
- Positive: Correct notification timing (draft→ready), no wasted tokens on polling, deterministic Gemini wait (90s minimum eliminates race), cheaper triage (Sonnet vs Opus), full pipeline visibility into each step
- Negative: Two code paths — pipeline (scripted) vs interactive (agent-driven). Mitigated by sharing the draft→ready pattern in both paths.

**Alternatives considered:**
- Keep agent-driven but add `--draft`: Fixes notification timing but still wastes Opus tokens polling. Doesn't fix the premature "no comments" race.
- GitHub webhook instead of polling: Would be ideal long-term but requires webhook infrastructure (server, auth, routing). Deferred to roadmap.

### Why Polling Instead of Threading?

**Context:** Need to monitor subprocess health (heartbeat, timeout) while it runs.

**Decision:** 5-second polling loop with `process.poll()` + `time.sleep()`. Both are non-blocking/trivial. No race conditions. Fully debuggable.

**Alternatives considered:**
- Threading: Complex, harder to debug, potential Windows pipe edge cases
- asyncio: Changes function signatures throughout, overkill for sequential pipeline

### Why Bypass Stop Hook in Pipeline Mode?

**Context:** The Stop hook enforces that all workflow steps (gates, verify, review) are complete before allowing session end. In pipeline mode, stages run as separate `claude -p` sessions — the implement session hasn't run gates/verify/review because those are later stages.

**Decision:** Pipeline sets `PPDS_PIPELINE=1`, Stop hook exits 0 immediately. The pipeline already enforces stage outcomes via `verify_outcome()` and `state.json` reads between stages.

**Evidence:** Even if the path mangling were fixed, the Stop hook would block implement from exiting, emit `decision: block` with "You MUST now run: /gates", forcing Claude to run all stages in one session — breaking the pipeline's stage separation. Observed in plugin-registration: workflow state showed gates/verify/qa/review all passed inside the implement session.

**Alternatives considered:**
- Make Stop hook understand stage boundaries: Over-complex, couples hook to pipeline logic
- Remove Stop hook entirely: Breaks interactive session enforcement

### Why Absorb Systematic Debugging Into /debug?

**Context:** `systematic-debugging` is the only superpowers skill with significant usage (15 invocations) that isn't already replaced by a PPDS command.

**Decision:** Merge its discipline (4-phase process, Iron Law, escalation rules, supporting techniques) into `/debug`, which already has PPDS-specific surface detection and build commands.

**Alternatives considered:**
- **Keep superpowers just for this skill:** Requires keeping the entire plugin enabled for one skill. Adds session start overhead for all sessions.
- **Write a separate `/debug-systematic` skill:** Creates two debugging skills with unclear scoping. Users wouldn't know which to use.

**Consequences:**
- Positive: Single debugging skill with both discipline and domain specificity.
- Negative: We must maintain the debugging discipline content ourselves. Mitigated by the content being stable (debugging fundamentals don't change).

---

## Extension Points

### Adding a New Verification Surface

When a new UI surface is added to PPDS (e.g., a web dashboard):

1. **Create `/surface-verify` skill** in `.claude/skills/surface-verify/SKILL.md` following `/write-skill` conventions.
2. **Update `/verify`** to recognize the new surface as a mode.
3. **Update `/qa`** to support the new surface for blind verification.
4. **Update CLAUDE.md** workflow section to include the new surface in step 6.
5. **No hook changes needed** — hooks check for "at least one surface verified," so new surfaces are automatically accepted.

### Adding a New Workflow Step

When a new required step is added (e.g., security scanning):

1. **Create or update the skill** to write its own entry to `workflow-state.json`.
2. **Update PR Gate hook** to check for the new entry.
3. **Update CLAUDE.md** workflow section.
4. **Update SessionStart hook** output format to include the new step.

---

## Error Handling

| Error | Condition | Recovery |
|-------|-----------|----------|
| Python not available | Hook script cannot execute | Graceful skip — hooks should not block the workflow if Python is missing. Emit a warning and allow the operation. |
| JSON parse failure | `workflow-state.json` exists but is invalid JSON | Treat as "no state file." PR gate blocks (safe default). Emit warning suggesting the file may be corrupted. |
| File system permission error | Cannot read/write `workflow-state.json` | Skills: emit warning, continue without state tracking. Hooks: treat as "no state file" (PR gate blocks). |
| Concurrent access | Two processes write to state file simultaneously | Last-writer-wins. Acceptable for single-session state. The file is per-session, not shared across sessions. |
| Git HEAD cannot be resolved | Detached HEAD or corrupted git state | Hooks: skip enforcement, emit warning. Skills: skip state writes, emit warning. |

---

## Implementation Notes

### settings.json Changes Required

All hook-related `settings.json` changes consolidated:

1. **All hook commands use relative paths** (no `${CLAUDE_PROJECT_DIR}`):
   ```json
   { "type": "command", "command": "python \".claude/hooks/session-stop-workflow.py\"" }
   ```
   Not:
   ```json
   { "type": "command", "command": "python \"${CLAUDE_PROJECT_DIR}/.claude/hooks/session-stop-workflow.py\"" }
   ```

2. **PostToolUse section** (new): Add `PostToolUse` matcher array to `.claude/settings.json`. Currently only `PreToolUse` exists.
   ```json
   "hooks": {
     "PostToolUse": [
       {
         "matcher": "Bash(git commit:*)",
         "hooks": [{ "type": "command", "command": "python \".claude/hooks/post-commit-state.py\"" }]
       }
     ]
   }
   ```

3. **Superpowers disabling** (after all skills are implemented):
   ```json
   { "enabledPlugins": { "superpowers@claude-plugins-official": false } }
   ```

### Skill Location Consolidation

All skills live in `.claude/skills/{name}/SKILL.md`. The `.claude/commands/` directory is deprecated — any remaining commands should be migrated to `.claude/skills/{name}/SKILL.md` with frontmatter.

---

## Related Specs

- [CONSTITUTION.md](./CONSTITUTION.md) — Non-negotiable principles that the workflow enforces
- [architecture.md](./architecture.md) — System architecture that skills reference

---

## Changelog

| Date | Change |
|------|--------|
| 2026-03-20 | Initial spec (v1.0) |
| 2026-03-22 | v2.0 — skill renames, /design, /start, /pr, main branch bootstrap |
| 2026-03-24 | v3.0 — stop hook blocking, converge cycle handling, /shakedown |
| 2026-03-26 | v4.0 — headless pipeline mode: relative hook paths, PPDS_PIPELINE env, Popen + polling, stage timeouts, heartbeats, stage logs. /start from worktree. /status stage log support. Commands-to-skills migration. |
| 2026-03-26 | v5.0 — pipeline observability and PR orchestration: (1) stream-json output for real-time stage logs, (2) multi-signal activity detection, (3) JSONL post-processing, (4) pipeline lock file, (5) /status live JSONL monitoring, (6) scripted PR stage with draft→ready flow, (7) gemini-triage agent profile (Sonnet), (8) pipeline-result.json + notify on completion/failure. |

---

## Roadmap

- **GitHub-triggered pipelines:** Trigger implementation from GitHub issues or webhooks. Requires persisted workflow state and session handoff mechanism.
- **PR monitoring webhook:** Replace Gemini polling with GitHub webhook notification. Eliminates polling entirely.
- **CI status monitoring in PR stage:** Poll CI checks after Gemini triage. Report green/red in pipeline-result.json. Currently deferred — `/gates` already verifies locally.
- **Cross-worktree status aggregation:** `/status` from main shows all active pipelines across worktrees. Currently each worktree's status is independent.
- **Worktree auto-cleanup:** SessionStart hook checks for stale worktrees (no commits in >7 days) and prompts for cleanup.
- **Cross-session workflow continuity:** Persist workflow state to git (not gitignored) so a new session can pick up where a previous session left off.
- **Devcontainer support for `/start`:** Offer to open worktree in devcontainer as alternative to system default shell.

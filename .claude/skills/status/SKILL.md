---
name: status
description: Status
---

# Status

Display current workflow enforcement state for the active branch.

## Usage

`/status` — Show what workflow steps have been completed and what's pending.

## Process

1. Read `.workflow/state.json` in the repo root.
2. If the file does not exist, output: "No workflow state tracked. Start with /gates, /verify, /qa, or /review to begin tracking."
3. If the file exists, read it and display the following:

### Output Format

```
WORKFLOW STATE for branch {branch}:
  {✓|✗} Gates {passed (commit {ref}, current)|passed (commit {ref}, STALE — HEAD is {head})|not run}
  {✓|✗} Extension {verified|not verified}
  {✓|✗} TUI {verified|not verified}
  {✓|✗} MCP {verified|not verified}
  {✓|✗} CLI {verified|not verified}
  {✓|✗} QA {surfaces tested: ext, tui|not completed}
  {✓|✗} Review {passed (N findings)|not completed}
  {✓|✗|⚠} PR {url|not created}
  Required before PR: {list of missing steps}
```

### Staleness Detection

Compare `gates.commit_ref` to current HEAD (`git rev-parse HEAD`). If they differ, gates are **STALE** — the code has changed since gates last ran.

### Verify Surfaces

Check each key in `verify` object (`ext`, `tui`, `mcp`, `cli`). Only show surfaces that have timestamps or are relevant to the current changes.

### Missing Steps

List what the PR gate hook would block on:
- Gates not current → "/gates"
- No verify entries → "/verify for affected surface"
- No QA entries → "/qa"
- No review → "/review"

### Pipeline Status — Live Monitoring

#### Step 1: Check for active pipeline

Check if `.workflow/pipeline.lock` exists and whether the PID inside it is alive:

```bash
# Check if lock file exists and PID is alive
if [ -f ".workflow/pipeline.lock" ]; then
    PID=$(cat .workflow/pipeline.lock)
    # Check if process is running (cross-platform)
    # Linux/Mac: kill -0 $PID 2>/dev/null
    # Windows/Git Bash: tasklist /FI "PID eq $PID" 2>/dev/null | grep -q $PID
fi
```

If the lock file exists and the PID is alive, the pipeline is **ACTIVE**. If the lock file exists but the PID is dead, it is a **STALE LOCK** — note this in the output.

#### Step 2: Parse pipeline.log for stage progress

If `.workflow/pipeline.log` exists, parse it for `START`, `DONE`, and `HEARTBEAT` entries:

1. Each line has format: `{timestamp} [{stage}] {event} key=value key=value ...`
2. `START` marks a stage beginning
3. `DONE` marks completion — extract `exit=` for success/failure, `duration=` for timing
4. `HEARTBEAT` provides live metrics — extract `elapsed=`, `pid=`, `output_bytes=`, `git_changes=`, `commits=`, `activity=`

Show each stage with its status and duration:

```
PIPELINE STATUS:
  ✓ worktree     (2s)
  ✓ implement    (13m 24s)
  ✓ gates        (1m 14s)
  → verify       (running for 2m 30s)
  ○ review       (pending)
  ○ pr           (pending)
  ○ retro        (pending)
```

- `✓` = completed (has both START and DONE with exit=0)
- `→` = in progress (has START but no DONE)
- `○` = pending (no START yet)
- `✗` = failed (DONE with non-zero exit)

Include the overall pipeline duration and plan file path from the first log entry.

#### Step 3: Parse last heartbeat for active stage

When a pipeline is **ACTIVE** (lock file exists, PID alive), find the **last HEARTBEAT line** in `pipeline.log` and extract these fields:

- `elapsed` — how long this stage has been running
- `pid` — the claude process PID
- `output_bytes` — bytes written to the stage JSONL
- `git_changes` — number of modified files in the working tree
- `commits` — commits ahead of main
- `activity` — one of `active`, `idle`, or `stalled`

#### Step 4: Parse active stage JSONL for current tool

When the pipeline is active, read the **last 50 lines** of the active stage's `.workflow/stages/{stage}.jsonl` file. This file contains `stream-json` output from `claude -p`.

Look for JSON objects with `"type": "assistant"` containing a `content` array. Within that array, find blocks with `"type": "tool_use"`. Extract:
- The `name` field — the tool currently being called (e.g., `Read`, `Edit`, `Bash`, `Grep`)
- If the tool input contains a `file_path` or `command` field, extract it for context

Use the **last** `tool_use` block found as the "current tool".

#### Step 5: Display format — Pipeline ACTIVE

When the pipeline is active, display:

```
Pipeline ACTIVE (PID {pid}):
  Stage: {stage} (elapsed: {elapsed})
  Last tool: {tool_name} {file_path if available}
  Git: {git_changes} modified files, {commits} commits ahead of main
  Output: {output_bytes} bytes
  Last activity: {time since last heartbeat}s ago
```

Calculate "time since last heartbeat" by comparing the heartbeat timestamp to the current time. The heartbeat timestamp is the ISO 8601 value at the start of the log line (e.g., `2026-03-26T14:30:00Z`).

#### Step 6: Display format — No active pipeline

When no pipeline is running (no lock file, or stale lock), show completed stage summaries:

1. Parse `.workflow/pipeline.log` for all `START`/`DONE` pairs to show the stage list with durations (same format as above).
2. For the most recently completed stage, show a brief summary from its `.workflow/stages/{stage}.log` file (the human-readable post-processed version, NOT the JSONL):
   - File size
   - Last 5 lines of output

If a stage shows `→` (in progress but pipeline not running), it likely crashed — mark it with `✗` and note "pipeline not running".

#### Step 7: Pipeline result

If `.workflow/pipeline-result.json` exists, display the last pipeline result. The file has this structure:

```json
{
  "status": "complete" | "failed",
  "duration": 1234,
  "stages": ["worktree", "implement", ...],
  "pr_url": "https://github.com/...",
  "failed_stage": "gates",
  "error": "...",
  "timestamp": "2026-03-26T14:30:00Z"
}
```

Display:
```
LAST PIPELINE RESULT:
  Status: {status} ({duration formatted as Xm Ys})
  {PR: {pr_url}  — if status is complete}
  {Failed at: {failed_stage} — {error}  — if status is failed}
  Completed: {timestamp}
```

## Notes

- This command does NOT write to `.workflow/state.json`. It is read-only.
- On `main` branch: output "On main branch — workflow enforcement applies to feature branches only."
- **Simple factual output only.** Do not add suggestions, analysis, or "would you like to..." prompts. Just show the state and stop.
- When pipeline is running, the most useful info is: which stage is active and how long it has been running. Lead with that.

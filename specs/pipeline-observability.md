# Pipeline Observability

**Status:** Draft (v2.0 — pipeline reliability: QA commits, review chunking, converge-on-FAIL, QA/review dedup)
**Last Updated:** 2026-03-27
**Code:** [scripts/pipeline.py](../scripts/pipeline.py) | [scripts/workflow-state.py](../scripts/workflow-state.py) | [tests/PPDS.Tui.E2eTests/tools/tui-verify.mjs](../tests/PPDS.Tui.E2eTests/tools/tui-verify.mjs) | [scripts/dev.ps1](../scripts/dev.ps1)
**Surfaces:** N/A

---

## Overview

Pipeline runs are black boxes once launched. Two failed pipelines (cmt-parity, v1-polish) exposed five observability gaps: timed-out stages produce empty logs, failed pipelines generate no retro, QA partial results are lost on timeout, pipeline-result.json contains no stage output, and retro findings are invisible to the dev dashboard. This spec adds incremental observability to the pipeline without changing its deterministic nature.

### Goals

- **Smarter timeouts**: Kill stuck stages fast (5 min stall), let active stages run (up to 120 min ceiling, overridable per run via `--max-stage-seconds N`). Fixed timeouts are the wrong tool.
- **Survive timeout**: When a stage is killed, extract whatever assistant text exists in the JSONL. Don't lose 20 minutes of work.
- **Failed pipeline retro**: Run a lightweight retro even when a stage fails. "QA timed out at Phase 2 after completing Phase 1" is actionable.
- **Partial QA persistence**: QA writes results per-phase as they complete, not only on clean exit.
- **Richer pipeline-result**: Include last stage output in pipeline-result.json, not just "failed_stage: qa".
- **Peek into live runs**: Lightweight command to see what the AI is doing right now.
- **Surface retro findings**: Dev dashboard shows retro finding count and tier breakdown.
- **Daemon lifecycle**: Detached daemons get idle timeouts and a standard shutdown contract.

### Non-Goals

- **AI decisions in pipeline.py**: Pipeline remains deterministic Python. No LLM calls.
- **Real-time streaming UI**: Peek is a point-in-time snapshot, not a live tail.
- **CI/CD changes**: GitHub Actions and external CI are out of scope.
- **`/prs` skill**: PR triage workflow is a separate concern, not pipeline observability. File as a separate issue.

---

## Architecture

```
Pipeline Timeout Today:
  claude -p → JSONL grows → fixed 1200s TIMEOUT → proc.kill()
              (active)       (doesn't care)      → extract "type":"result" only → 0 found → empty .log
                                                  → write_result: {failed_stage: "qa"} → no detail

Pipeline Timeout After:
  claude -p → JSONL grows → heartbeat: active? → keep running (up to 60 min ceiling)
              (active)       idle 5 min? → STALL_TIMEOUT → proc.kill()
                                                          → extract assistant text → meaningful .log
                                                          → write_result: {last_output: [...]} → actionable
                                                          → run failure retro → retro-findings.json
```

```
QA Skill Today:
  Phase 1 complete → (results in JSONL only)
  Phase 2 in progress → TIMEOUT → state.json has no QA data

QA Skill After:
  Phase 1 complete → write qa.partial to state → (results survive)
  Phase 2 in progress → TIMEOUT → state.json has Phase 1 results
```

```
Daemon Lifecycle Today:
  tui-verify --daemon → runs forever → orphans on worktree cleanup

Daemon Lifecycle After:
  tui-verify --daemon → idle 30min → self-terminate → cleanup removes session file
```

### Components

| Component | Responsibility |
|-----------|----------------|
| `extract_text_from_jsonl()` | Extract assistant text from stream-json JSONL — currently only extracts `type: result` (clean exit). Must also extract from `type: assistant` message events (which contain nested `type: text` content blocks). |
| `write_result()` | Write pipeline-result.json — must include last output lines on failure. |
| Pipeline failure path | Currently skips retro on failure. Must run retro (or lightweight variant) on failure too. |
| Timeout logic | Currently uses fixed per-stage timeouts. Must switch to stall-based + hard ceiling. |
| QA skill | Currently writes state only on full completion. Must write partial results per-phase. |
| `dev peek` | New command — extracts latest assistant text from active stage JSONL. |
| Dev dashboard | Currently doesn't read retro-findings.json. Must surface finding count and tiers. |
| tui-verify daemon | Currently has no idle timeout. Must self-terminate after 30 minutes of inactivity. |
| Cleanup skill | Currently doesn't check for daemon session files. Must detect and shut down daemons before worktree removal. |

---

## Specification

### Tier 1: Partial Stage Output on Timeout

**Root cause**: `extract_text_from_jsonl()` (pipeline.py:262-281) only looks for `"type": "result"` events. These are emitted by `claude -p --output-format stream-json` only on clean exit. When the process is killed via timeout, no result event is written, so the extracted text is empty.

**Fix**: Extract assistant text from `type: assistant` message events in addition to `result` events.

Claude Code's stream-json format (distinct from the Anthropic API streaming format) emits whole-message events, not deltas:

| Event type | Structure | When emitted |
|------------|-----------|-------------|
| `"type":"assistant"` | `{message: {content: [{type: "text", text: "..."}, {type: "tool_use", ...}, ...]}}` | Every assistant turn (continuously throughout session) |
| `"type":"text"` | `{text: "..."}` | Individual text content blocks |
| `"type":"tool_use"` | `{name: "...", input: {...}}` | Tool invocations |
| `"type":"tool_result"` | `{content: "..."}` | Tool outputs |
| `"type":"thinking"` | `{thinking: "..."}` | Extended thinking blocks |
| `"type":"result"` | `{result: "...", subtype: "success", duration_ms: N}` | **Only on clean exit** |

The key insight: `assistant` events contain complete content arrays with `text` blocks — these are emitted on every turn throughout the session. The `result` event is a summary emitted only on clean exit.

**New extraction logic:**

```python
def extract_text_from_jsonl(jsonl_path):
    """Extract assistant text from stream-json JSONL file.

    Prefers 'result' events (clean exit) but falls back to
    assembling text from 'assistant' message events and 'text'
    content blocks (timeout/crash).
    """
    result_text_parts = []
    assistant_text_parts = []

    try:
        with open(jsonl_path, "r", errors="replace") as f:
            for line in f:
                line = line.strip()
                if not line:
                    continue
                try:
                    event = json.loads(line)
                except json.JSONDecodeError:
                    continue

                event_type = event.get("type")

                if event_type == "result":
                    result_text = event.get("result", "")
                    if result_text:
                        result_text_parts.append(result_text)
                elif event_type == "assistant":
                    # assistant events have message.content array
                    content = event.get("message", {}).get("content", [])
                    for block in content:
                        if block.get("type") == "text":
                            text = block.get("text", "")
                            if text:
                                assistant_text_parts.append(text)
    except OSError:
        pass

    # Prefer result (clean exit) over assembled assistant text (timeout)
    if result_text_parts:
        return "\n".join(result_text_parts)
    return "\n\n".join(assistant_text_parts)
```

**Behavior change**: On timeout, `.workflow/stages/qa.log` will contain the full assistant text up to the moment of kill, instead of 0 bytes.

**Validation**: Parse the existing cmt-parity and v1-polish `.jsonl` files with the new logic and verify they produce non-empty output.

### Tier 1: Pipeline-Result Includes Last Stage Output

**Current state**: `write_result()` (pipeline.py:925-959) writes `status`, `duration`, `stages` (per-stage durations), `pr_url`, `timestamp`, `failed_stage`, and `error` — but no stage output text.

**Fix**: After the failed stage's JSONL is post-processed into a .log file, read the last N lines and include them in pipeline-result.json.

**Changes to `write_result()`:**

Add optional `last_output` parameter:

```python
def write_result(worktree_path, status, duration, stages, pr_url=None,
                  failed_stage=None, error=None, last_output=None):
    result = {
        "status": status,
        "duration": duration,
        "stages": stages,
        "pr_url": pr_url,
        "timestamp": timestamp(),
    }
    if failed_stage:
        result["failed_stage"] = failed_stage
    if error:
        result["error"] = error
    if last_output is not None:
        result["last_output"] = last_output
    # ... rest unchanged
```

**At the failure call site**: `write_result` for failures is called in the `finally` block (pipeline.py:1257-1260), not in `_pipeline_fail()` (which only sets nonlocal vars and calls `sys.exit(1)`). The `finally` block should read the last 50 lines of the failed stage's `.log` file and pass as `last_output`.

**Result schema addition:**

```json
{
  "status": "failed",
  "failed_stage": "qa",
  "error": "stall timeout after 5m idle (elapsed 1847s)",
  "last_output": [
    "Phase 1 complete: CLI 7/7 PASS, TUI 8/8 PASS",
    "Phase 2 starting: Extension consistency...",
    "Launching VS Code instance..."
  ]
}
```

### Tier 1: Failed-Pipeline Retro

**Current state**: The retro stage only runs if all prior stages succeed (it's the last stage in the sequence). Failed pipelines produce no retro.

**Fix**: Restructure `_pipeline_fail()` to avoid `sys.exit(1)`. Currently `_pipeline_fail()` (pipeline.py:1073-1077) calls `sys.exit(1)`, which means the `finally` block runs during `SystemExit` exception handling. Running `run_claude()` (subprocess spawn, I/O) during `SystemExit` handling is fragile. Instead:

1. Change `_pipeline_fail()` to set the nonlocal vars (as today) but raise a custom `PipelineFailure` exception instead of calling `sys.exit(1)`.
2. Catch `PipelineFailure` in a new `except PipelineFailure` block before the `finally`.
3. In that `except` block, write pipeline-result.json, run failure retro, then exit.

**Pipeline flow change:**

```python
class PipelineFailure(Exception):
    pass

def _pipeline_fail(stage_name, reason=None):
    nonlocal _failed_stage, _failed_reason
    _failed_stage = stage_name
    _failed_reason = reason
    raise PipelineFailure(f"{stage_name}: {reason}")

# ... in main try block ...

except PipelineFailure:
    duration = int(time.time() - pipeline_start)
    # Read last 50 lines of failed stage log for pipeline-result
    last_output = _read_last_lines(worktree_path, _failed_stage, 50)
    write_result(worktree_path, "failed", duration, stage_durations,
                 failed_stage=_failed_stage, error=_failed_reason,
                 last_output=last_output)
    _result_written = True

    # Best-effort failure retro — outside SystemExit, safe to spawn subprocesses
    # Uses standard stall-based timeout (5 min idle) + hard ceiling (60 min)
    log(logger, "retro", "START", mode="failure-retro")
    try:
        run_claude(worktree_path, "/retro", logger, "retro")
    except Exception:
        log(logger, "retro", "FAILED_NON_BLOCKING")

    sys.exit(1)
```

**Retro skill behavior**: The retro skill in pipeline mode (detected via `PPDS_PIPELINE=1` and presence of `pipeline.log`) collects **mechanical metrics only** — stage timing, session counts, commit breakdown, thrashing incidents. It does NOT perform root-cause analysis or grading. With the improved stage logs (Tier 1 fix above), the retro will capture accurate stage durations and failure events. The resulting `retro-findings.json` will contain metrics like `"failed_stage": "qa"`, stage timing, and compute breakdown — data that helps identify patterns across pipeline runs even without analysis.

**Resolved**: The QA timeout budget problem is addressed by the activity-based timeout (see Tier 1: Activity-Based Stage Timeouts). Failure retro uses the same stall-based logic — no special timeout needed.

### Tier 1: Activity-Based Stage Timeouts

**Problem**: Fixed timeouts can't distinguish "making progress" from "spinning without converging." The cmt-parity QA was actively working (output_bytes growing to 970KB) but got killed at exactly 1200s. The v1-polish QA first two runs were genuinely stuck (0 bytes) but also waited the full 1200s before being killed. Fixed timeouts are too long for stuck processes and too short for active ones.

**Root cause**: The pipeline already tracks activity via heartbeats (pipeline.py:363-391) and classifies it as `active`/`idle`/`stalled` using three signals (output_bytes growth, git changes, commit count). But the timeout check (pipeline.py:352-361) ignores this classification — it only checks `elapsed > timeout`.

**Fix**: Replace fixed timeouts with a hybrid model — stall timeout + hard ceiling:

- **Stall timeout**: Kill the stage after 5 consecutive idle heartbeats (5 minutes of no activity across all signals). Stuck processes die fast.
- **Hard ceiling**: Kill the stage after 7200s (120 min) regardless of activity. Safety net so nothing runs forever. Overridable per-run via the `--max-stage-seconds N` CLI flag on `scripts/pipeline.py` when a specific run needs more (or less) budget — e.g., cross-cutting architectural refactors where 17+ parallel agents are legitimately working for longer than the default.

**Current timeout check** (pipeline.py:352-361):
```python
if timeout is not None and elapsed > timeout:
    log(logger, stage, "TIMEOUT", elapsed=f"{int(elapsed)}s", timeout=f"{timeout}s")
    proc.terminate()
    ...
```

**New timeout check:**
```python
STALL_LIMIT = 5       # consecutive idle heartbeats (5 min at 60s interval)
HARD_CEILING = 7200   # 120 min absolute maximum (override with --max-stage-seconds N)

# In heartbeat block (after consecutive_idle is updated):
if consecutive_idle >= STALL_LIMIT:
    log(logger, stage, "STALL_TIMEOUT",
        elapsed=f"{int(elapsed)}s",
        idle_minutes=consecutive_idle,
        last_output_bytes=current_size)
    proc.terminate()
    try:
        proc.wait(30)
    except subprocess.TimeoutExpired:
        proc.kill()
        proc.wait()
    exit_code = -1
    break

# Hard ceiling check (replaces current fixed timeout):
if elapsed > HARD_CEILING:
    log(logger, stage, "HARD_TIMEOUT",
        elapsed=f"{int(elapsed)}s",
        ceiling=f"{HARD_CEILING}s",
        activity=activity)
    proc.terminate()
    try:
        proc.wait(30)
    except subprocess.TimeoutExpired:
        proc.kill()
        proc.wait()
    exit_code = -1
    break
```

**STAGE_TIMEOUTS replacement**: The per-stage `STAGE_TIMEOUTS` dict (pipeline.py:63-72) is removed. All stages use the same stall-based logic. The differentiation between "short" stages (gates: 15 min) and "long" stages (implement: 45 min) is no longer needed — a stage that's actively producing output keeps running; a stage that stalls for 5 minutes gets killed regardless of which stage it is.

**Behavior change by scenario:**

| Scenario | Before | After |
|----------|--------|-------|
| QA actively working, hits 20 min | Killed (timeout) | Keeps running (active) |
| QA stuck, 0 bytes for 5 min | Waits 20 min to kill | Killed at 5 min (stall) |
| QA active for 55 min | N/A (killed at 20 min) | Keeps running (well under 120 min ceiling) |
| Active stage for 130 min | N/A | Killed at 120 min (hard ceiling) |
| Active stage for 130 min, `--max-stage-seconds 10800` | N/A | Keeps running (override raised ceiling to 180 min) |
| Implement actively coding for 40 min | Keeps running (45 min timeout) | Keeps running (active) |
| Gates stuck on build error | Waits 15 min | Killed at 5 min (stall) |

**Log event differentiation**: `STALL_TIMEOUT` vs `HARD_TIMEOUT` in pipeline.log makes it clear why the stage was killed. The existing `TIMEOUT` event is replaced by these two specific events.

**Effective ceiling logged on stage start**: Each stage's `START` event records the effective ceiling (default or override) as `ceiling=<N>s`. Operators inspecting `pipeline.log` can see which value is in effect for a given run without re-deriving it from the CLI invocation.

**Override flag — `--max-stage-seconds N`**: `scripts/pipeline.py` accepts `--max-stage-seconds N` where `N` is a positive integer number of seconds. When present, `N` replaces `HARD_CEILING` for every stage in that pipeline run (including `run_claude` heartbeat comparisons, the `pr` stage's internal checks, and the `pr_monitor.py` subprocess timeout). The flag is intended for one-off runs where the caller knows the work will exceed the default — cross-cutting architectural refactors, large-batch QA rounds, multi-domain features. The default is deliberately generous enough that most runs will never need it; if a whole class of work consistently hits the ceiling, raise the default rather than normalizing the override.

**What counts as activity** (unchanged, already implemented in heartbeat):
- `output_bytes` grew (JSONL file size increased — AI is producing text)
- `git_changes` grew (working tree has new modifications — AI is writing code)
- `commits` grew (new commits ahead of main — AI committed work)

Any of these three signals resets the idle counter. This already covers the concern about `dotnet build` going quiet (build produces git changes) and VS Code startup delays (output_bytes grow as the agent narrates what it's doing).

### Tier 1: QA Writes Partial Results to State

**Current state**: QA skill writes to `state.json` only on full completion (`python scripts/workflow-state.py set qa.ext now`). If QA times out mid-Phase 2, Phase 1 results are lost.

**Fix**: QA skill writes partial results after each phase completes.

**State schema extension** — new top-level key `qa_partial` (NOT nested under `qa`):

```json
{
  "qa_partial": {
    "phase1_completed": "2026-03-26T10:00:00Z",
    "phase1_checks_passed": 7,
    "phase1_checks_total": 7
  },
  "qa": {
    "ext": null
  }
}
```

**Why `qa_partial` instead of `qa.partial`?** The pipeline's `verify_outcome()` for the `qa` stage (pipeline.py:148-150) checks `any(v for v in qa.values() if v)`. If `qa.partial` were a nested dict under `qa`, it would be truthy, causing `verify_outcome()` to return True — the pipeline would think QA passed when it hasn't. Keeping partial results in a separate top-level key avoids this false-positive.

**QA skill changes**: After Phase 1 completes, before dispatching Phase 2 agents:

```bash
python scripts/workflow-state.py set qa_partial.phase1_completed now
python scripts/workflow-state.py set qa_partial.phase1_checks_passed 7
python scripts/workflow-state.py set qa_partial.phase1_checks_total 7
```

When QA fully passes, the existing `qa.ext` timestamp is written (unchanged). The `qa_partial` key persists as a record of per-phase results.

**Pipeline awareness**: `write_result()` should check for `qa_partial` in state.json and include it in pipeline-result.json under a `partial_results` key when the failed stage is QA.

**Cross-spec dependency**: The `qa_partial` key is a new addition to the workflow state schema. The canonical schema definition in [workflow-enforcement.md](./workflow-enforcement.md) (lines 128-155) should be updated when this spec is implemented.

### Tier 2: Peek Command

**Purpose**: `dev peek <worktree>` extracts the most recent assistant text from the active stage's JSONL. Lightweight real-time window into what the AI is doing.

**Implementation**: New subcommand in `scripts/dev.ps1` (currently in-progress in the `dev-script-ux` worktree — coordinate, don't conflict).

**Flow:**

1. Resolve worktree path from name prefix (existing `dev` convention).
2. Check `.workflow/pipeline.lock` exists (pipeline is running).
3. Read `pipeline.log` tail to determine current stage name.
4. Read `.workflow/stages/{stage}.jsonl` from the end.
5. Extract the last N `assistant` message text blocks.
6. Display as formatted text with stage name and elapsed time header.

**Output:**

```
[qa] 12m34s elapsed (active)
─────────────────────────────
Phase 1 complete: CLI 7/7 PASS, TUI 8/8 PASS
Launching Phase 2 agents...
Phase 2A (Consistency): Opening Solutions panel...
```

**Constraints:**
- Read-only. Never writes to any file.
- Must handle concurrent reads while pipeline is writing (open with shared read, handle partial JSON lines).
- Truncate output to last ~50 lines / 4KB to keep it lightweight.

**Parsing the current stage from pipeline.log:**

```powershell
# Find last START event to determine active stage
$lastStart = Get-Content "$wtPath/.workflow/pipeline.log" -Tail 100 |
    Where-Object { $_ -match '\[(\w+)\] START' } |
    Select-Object -Last 1
$stage = $Matches[1]
```

### Tier 2: Dev Dashboard Surfaces Retro Findings

**Current state**: Dev dashboard reads `state.json` and `pipeline-result.json` but not `retro-findings.json`.

**Fix**: In `dev status <worktree>`, read `.workflow/retro-findings.json` if it exists and display a summary.

**Display format in `dev status`:**

```
Retro Findings: 5 findings (2 auto-fix, 1 draft-fix, 2 issue-only)
  R-01 [auto-fix] Stale references in qa-skill
  R-02 [issue]    Converge ran twice, 38% compute waste
  ...
```

**Display format in `dev` (dashboard list):**

Add finding count to the detail column for worktrees that have retro findings:

```
  analyzers        +12  0 dirty   pr #686 merged · 5 retro findings
```

**Implementation**: Read the file, count findings by tier, format as a one-line summary for the list view and a detailed breakdown for the status view.

### Tier 2: Stage-Level Summary in Pipeline-Result

**Current state**: `pipeline-result.json` has `stages: {"implement": "450s", "gates": "120s"}` — just timing.

**Fix**: Extend per-stage data with exit code and last output line:

```json
{
  "stages": {
    "implement": { "duration": "450s", "exit": 0 },
    "gates": { "duration": "120s", "exit": 0 },
    "qa": { "duration": "1200s", "exit": -1, "last_line": "Phase 2A: Opening Solutions panel..." }
  }
}
```

**Changes**: Track exit codes per stage alongside durations. After each `run_claude()` returns, store `(duration, exit_code, last_line)` instead of just duration string.

### Tier 3: Daemon Idle Timeout

**Current state**: tui-verify daemon (tui-verify.mjs:326-336) has signal handlers for SIGTERM/SIGINT but no idle timeout. Runs forever until explicitly shut down.

**Fix**: Add a 30-minute idle timeout. Every HTTP request to `/execute` or `/health` resets the timer. If no request arrives within 30 minutes, the daemon self-terminates via the existing `cleanup()` function.

**Implementation:**

```javascript
// After server starts (line ~320):
const IDLE_TIMEOUT_MS = 30 * 60 * 1000; // 30 minutes
let idleTimer = setTimeout(cleanup, IDLE_TIMEOUT_MS);

// In request handler (before each endpoint):
function resetIdleTimer() {
  clearTimeout(idleTimer);
  idleTimer = setTimeout(cleanup, IDLE_TIMEOUT_MS);
}

// In each endpoint handler:
server.on('request', (req, res) => {
  resetIdleTimer();
  // ... existing routing
});
```

**Logging**: On idle timeout, write a line to `.tui-verify-daemon.log`: `"Idle timeout (30m) — shutting down"`.

### Tier 3: Cleanup Checks for Daemon Session Files

**Current state**: Cleanup skill removes worktrees via `git worktree remove --force` but doesn't check for running daemons. Orphaned daemons lock directories on Windows.

**Fix**: Before removing a worktree, the cleanup skill scans for `*-session.json` files in known locations and sends shutdown requests.

**Cleanup skill addition:**

```
Before worktree removal:
1. Glob for *-session.json within the worktree path
2. For each session file:
   a. Read daemonPort and daemonPid
   b. POST http://localhost:{port}/shutdown (timeout 5s)
   c. If shutdown fails, kill PID directly (process.kill on Windows)
   d. Delete session file
3. Proceed with git worktree remove
```

**Known session file locations:**
- `tests/PPDS.Tui.E2eTests/tools/.tui-verify-session.json`
- Future daemons should follow the same convention (see Daemon Contract below).

### Tier 3: Daemon Contract

Standard convention for any detached daemon in PPDS:

| Requirement | Convention |
|-------------|------------|
| Session file | `tools/.{name}-session.json` with `{port, pid, logFile}` |
| Health endpoint | `GET /health` → `200 "ok"` |
| Shutdown endpoint | `POST /shutdown` → `200 "ok"`, then async cleanup |
| Idle timeout | 30 minutes default. Configurable via `--idle-timeout` flag |
| Log file | `tools/.{name}-daemon.log` |
| Cleanup on exit | Delete session file + log file |
| Signal handling | SIGTERM/SIGINT → graceful cleanup |

This contract is informational — governs tui-verify today and any future daemons (ext-verify, etc.). Not enforced mechanically.

### Tier 1: QA Commits Its Own Fixes

**Problem:** QA skill finds issues and sometimes fixes them inline, but never commits. Across PRs #731 and #727, QA left 4 files unstaged. When QA times out, the fixes are lost.

**Fix:** Add a commit step to the QA skill. After each phase completes and any fixes are applied:

```
git add -A
git diff --cached --quiet || git commit -m "fix(qa): address QA phase {N} findings"
```

**QA skill prompt addition:**
```
After fixing any issue found during verification:
1. Stage the fix: git add <file>
2. Commit immediately: git commit -m "fix(qa): <brief description>"
3. Do NOT batch fixes — commit after each fix so partial results survive timeout
```

**Interaction with post-commit hook:** Each QA fix commit triggers the post-commit hook, which clears `gates.passed`. This is correct — QA fixes change the code, so gates must re-run. The pipeline's converge stage handles the re-gating after QA.

**Ordering note:** Pipeline stage sequence is `qa → review → converge`. When QA commits a fix and clears gates, review runs next on un-gated code. This is acceptable: review checks code quality and constitution compliance, not build/test status. If the QA fix introduced a build error, converge will catch it when it re-runs gates. Review findings on un-gated code are still valid — they're about code correctness, not compilability. The alternative (re-gating between QA and review) adds latency for no meaningful quality gain.

### Tier 1: Review Chunking for Large Diffs

**Problem:** Review subagent receives the entire diff + constitution + ACs in one prompt. For large changes (30+ files), this overflows the context window, causing stalls (835s idle then 420s hard stall across PRs #732, #730, #734, #712).

**Root cause:** `_build_review_prompt()` concatenates all changed files' diffs into a single prompt. For implement stages that touch 30+ files, this produces 50-100K tokens before the reviewer even starts reading.

**Fix:** Per-file dispatch with cross-file summary pass.

**New review flow:**

```
/review (or pipeline review stage)
│
├─ 1. Compute diff: git diff origin/main...HEAD --stat
│     Group files by directory/component
│
├─ 2. Chunk into review units (one per file, or group small files ≤50 lines)
│     Each unit gets: file diff + constitution + relevant ACs
│     Max chunk size: 8K tokens (fits comfortably in any model context)
│
├─ 3. Dispatch per-file review subagents (parallel, max 5 concurrent)
│     Each agent: Read the file diff, check against constitution + ACs
│     Output: [{finding, severity, file, line, suggestion}]
│     Timeout: 3 min per chunk (stall-based, not fixed)
│     Fallback: if subagent stalls, skip that chunk with "review incomplete" note
│
├─ 4. Collect findings from all chunks
│     Deduplicate (same file+line+finding = single entry)
│
├─ 5. Cross-file consistency pass (single subagent)
│     Input: file manifest + per-file findings + architecture overview
│     Checks: type mismatches between contracts, missing imports,
│             interface changes without callers updated, naming inconsistencies
│     Output: additional cross-file findings
│
├─ 6. Merge all findings, classify severity
│     Write review results to workflow state
│
└─ 7. If critical/important findings > 0: review.passed = null (FAIL)
       If 0 critical + 0 important: review.passed = timestamp (PASS)
```

**Why per-file, not per-directory:** Files are the natural review boundary — each file's diff is self-contained. Directories could still overflow if a single directory has many large files. Per-file guarantees bounded chunk size.

**Cross-file findings that per-file review misses:**
- Interface changes in `IFoo.cs` without updating `FooImpl.cs`
- Type renames that break callers in other files
- New dependencies not wired in DI container
- Naming convention drift across files in same module

The cross-file pass is a lightweight check (manifest + findings, not full diffs) so it doesn't hit the context overflow problem.

### Tier 1: Converge Runs on Review FAIL

**Problem:** Pipeline skips converge when review verdict is FAIL (PR #728). The pipeline's stage sequencing treats any stage failure as terminal, but review FAIL is specifically designed to be followed by converge (fix → re-gate → re-review).

**Root cause:** `verify_outcome("review")` returns False on FAIL, and the pipeline treats False as "stop pipeline." But review FAIL means "findings need fixing," which is exactly what converge does.

**Fix:** After review stage completes, check the review verdict specifically:

```python
# After review stage
review_state = read_state(worktree_path).get("review", {})
if review_state.get("passed"):
    log(logger, "review", "PASSED")
    # Skip converge if no findings
    if review_state.get("findings", 0) == 0:
        log(logger, "converge", "SKIPPED", reason="zero findings")
    else:
        run_stage("converge")
else:
    # Review FAIL → converge is mandatory
    log(logger, "review", "FAILED", findings=review_state.get("findings"))
    run_stage("converge")
```

**Converge loop:** Converge itself handles the fix → re-gate → re-review loop. The pipeline doesn't need to understand the loop — it just runs converge and checks the outcome.

### Tier 2: QA/Review Deduplication via Shared Findings

**Problem:** QA and review both find the same issues independently (PR #729). QA detects "inconsistent panel naming" and review detects "inconsistent panel naming" — same finding, double the fix work in converge.

**Fix:** QA writes its findings to workflow state. Review reads them and skips already-found issues.

**State schema addition:**

```json
{
  "qa_findings": [
    {
      "id": "QA-01",
      "surface": "ext",
      "description": "Panel name 'Data Explorer' uses different casing in sidebar vs title",
      "file": "src/PPDS.Extension/panels/data-explorer/panel.ts",
      "severity": "important",
      "fixed": true,
      "fix_commit": "abc1234"
    }
  ]
}
```

**QA skill changes:** After each finding (whether fixed or not), write to `qa_findings` array in state via `workflow-state.py`.

**Review skill changes:** Before dispatching review subagents, read `qa_findings` from state. Pass them to each review subagent as context: "These issues were already found and fixed by QA. Do not re-report them unless the fix introduced a new problem."

**Dedup algorithm:** Exact match on `(file_path, description[:50])` tuple. Two findings are considered duplicates if they reference the same file AND the first 50 characters of their descriptions are identical. If a QA finding matches a potential review finding and `fixed: true`, the review skips it. If `fixed: false` (QA found it but didn't fix), review still reports it but references the QA finding: "Also found by QA (QA-01), not yet fixed." The dedup is conservative — if the match is ambiguous (different files, or descriptions diverge before char 50), the review reports both.

---

## Acceptance Criteria

| ID | Criterion | Test | Status |
|----|-----------|------|--------|
| AC-01 | `extract_text_from_jsonl()` returns non-empty text from JSONL files where the process was killed before emitting a `result` event (assembles text from `assistant` message events' nested `text` content blocks) | `test_extract_text_from_assistant_messages_when_no_result` | 🔲 |
| AC-02 | `extract_text_from_jsonl()` prefers `result` events over assembled `assistant` message text when both exist (clean exit) | `test_extract_text_prefers_result_when_both_exist` | 🔲 |
| AC-03 | `pipeline-result.json` includes `last_output` (list of strings) when `failed_stage` is set | `test_write_result_includes_last_output_on_failure` | 🔲 |
| AC-04 | Pipeline runs retro stage after `PipelineFailure` (best-effort, non-blocking — retro failure doesn't change pipeline exit code) | `test_pipeline_runs_retro_on_failure` | 🔲 |
| AC-05 | QA skill writes `qa_partial.phase1_completed` to state.json after Phase 1 completes, before dispatching Phase 2 | `test_qa_state_write` (integration: run QA skill on fixture, verify state file contains `qa_partial` key) | 🔲 |
| AC-06 | `pipeline-result.json` includes `partial_results` from state.json when failed stage is QA and `qa_partial` exists in state | `test_write_result_includes_partial_qa_on_qa_timeout` | 🔲 |
| AC-07 | `dev peek <worktree>` displays last ~50 lines of assistant text from active stage JSONL | Manual — PowerShell UI command, tested via `dev peek` during pipeline run | 🔲 |
| AC-08 | `dev status` shows retro finding count and tier breakdown when `.workflow/retro-findings.json` exists | Manual — PowerShell UI command, tested via `dev status` | 🔲 |
| AC-09 | Per-stage entries in `pipeline-result.json` include exit code and last output line (not just duration string) | `test_write_result_stages_include_exit_and_last_line` | 🔲 |
| AC-10 | tui-verify daemon self-terminates after 30 minutes of no HTTP requests | `test_daemon_self_terminates_after_idle_timeout` (Node.js test) | 🔲 |
| AC-11 | Cleanup skill sends `/shutdown` to daemons found via `*-session.json` before worktree removal | Manual — skill behavior, verified via `/cleanup` with active daemon | 🔲 |
| AC-12 | Stage is killed after 5 consecutive idle heartbeats (5 min no activity) with `STALL_TIMEOUT` log event | `test_stall_timeout_kills_after_consecutive_idle` | 🔲 |
| AC-13 | Stage is killed after the effective hard ceiling (default 7200s, overridable via `--max-stage-seconds N`) regardless of activity, with `HARD_TIMEOUT` log event | `test_hard_timeout_kills_at_ceiling` | 🔲 |
| AC-14 | Active stage (output_bytes growing) is NOT killed by stall timeout — idle counter resets on activity | `test_active_stage_not_killed_by_stall_timeout` | 🔲 |
| AC-15 | `STAGE_TIMEOUTS` dict is removed; all stages use stall-based + hard ceiling logic | `test_no_per_stage_fixed_timeouts` | 🔲 |
| AC-16 | QA skill commits each fix immediately after applying it (`git add` + `git commit`) — fixes survive timeout | `test_qa_commits_fixes` (integration: run QA skill on fixture with known issue, verify git log contains QA fix commit) | 🔲 |
| AC-17 | Review dispatches per-file subagents (one per changed file or group of small files ≤50 lines) instead of loading entire diff into one prompt | Manual — observe review stage logs showing per-file dispatch | 🔲 |
| AC-18 | Review per-file subagent timeout is stall-based (3 min idle); on stall, chunk is skipped with "review incomplete" note (not a hard failure) | Manual — trigger review on large diff, observe timeout handling | 🔲 |
| AC-19 | Review includes a cross-file consistency pass after per-file review — checks type mismatches, missing imports, interface/caller drift | Manual — observe cross-file pass in review stage logs | 🔲 |
| AC-20 | Pipeline runs converge stage after review FAIL (not just review PASS) — review FAIL triggers converge, not pipeline exit | `test_pipeline_runs_converge_on_review_fail` | 🔲 |
| AC-21 | Pipeline skips converge when review passes with zero findings | `test_pipeline_skips_converge_on_zero_findings` | 🔲 |
| AC-22 | Pipeline runs converge when review passes with non-zero findings (findings exist but none critical/important) | `test_pipeline_runs_converge_on_pass_with_findings` | 🔲 |
| AC-23 | QA writes findings to `qa_findings` array in workflow state (id, surface, description, file, severity, fixed, fix_commit) | Manual — run QA, read state file, verify `qa_findings` entries | 🔲 |
| AC-24 | Review reads `qa_findings` from state and passes to subagents as "already found by QA" context | Manual — run QA then review, observe review skipping duplicate findings | 🔲 |
| AC-25 | Review does not re-report QA findings that were fixed (`fixed: true`) unless the fix introduced a new problem | Manual — verify dedup in review output | 🔲 |
| AC-26 | `HARD_CEILING` default is 7200 seconds (120 min) | `test_hard_ceiling_default_is_7200` | 🔲 |
| AC-27 | `scripts/pipeline.py --max-stage-seconds N` parses as a positive integer and replaces `HARD_CEILING` for the run; the effective ceiling is threaded into `run_claude`, `run_pr_stage`, and the PR monitor subprocess timeout | `test_max_stage_seconds_flag_overrides_ceiling`, `test_run_pr_stage_threads_ceiling_to_summary_and_monitor`, `test_pr_monitor_uses_effective_ceiling` | 🔲 |
| AC-28 | `run_claude` records the effective ceiling on stage start (`START` log entry includes `ceiling=<N>s`) so operators can see which value is in effect | `test_run_claude_logs_effective_ceiling_on_start_default`, `test_run_claude_logs_effective_ceiling_on_start_override` | 🔲 |

### Edge Cases

| Scenario | Input | Expected Output |
|----------|-------|-----------------|
| JSONL has only malformed lines | Corrupted file | Return empty string (no crash) |
| JSONL has assistant events but no text blocks | Events with empty content arrays or only tool_use blocks | Return empty string |
| Pipeline.lock missing during peek | No active pipeline | Display "No active pipeline in {worktree}" |
| retro-findings.json malformed | Invalid JSON | Skip retro display, no crash |
| Daemon port in use at startup | Port conflict | Existing stale-session detection handles this (kills old PID, relaunches) |
| Daemon idle timer reset by /health | Monitoring tool pings health | Timer resets — daemon stays alive while being monitored |
| JSONL file locked during peek (Windows) | Pipeline writing to JSONL while peek reads it | Open with `FileShare.Read` / shared read mode. Windows file locking is more aggressive than Unix — peek must not use exclusive access |
| QA fix commit triggers post-commit hook clearing gates | Expected — gates must re-run after code changes. Converge handles re-gating. |
| Review subagent stalls on one file | That chunk is skipped with "review incomplete" note. Other files continue. Cross-file pass runs with partial results. |
| All review subagents stall | Review reports "review incomplete for all files" and sets review.passed = null. Converge runs to address. |
| QA and review find same issue but different description | First-50-chars prefix match on file path + description. If ambiguous, review reports it (prefer false positive over missed finding). |
| Review receives 50+ changed files | Files grouped into review units. Max 5 concurrent subagents. Queue remaining. Total review time bounded by stall-based timeout. |
| Converge runs after review FAIL but converge also fails | Pipeline exits with `failed_stage: "converge"`. Failure retro captures both review findings and converge failure. |

---

## Design Decisions

### Why Extract Assistant Messages Instead of Switching to a Different Output Format?

**Context:** Stream-json `result` events only appear on clean exit. We need text from timed-out processes. Claude Code's stream-json format emits `type: "assistant"` events with nested `content` arrays containing `text` blocks on every turn — these are always present regardless of how the process exits.

**Decision:** Parse `assistant` message events as fallback when no `result` events exist. Extract text from the `message.content` array, filtering for `type: "text"` blocks.

**Alternatives considered:**
- Write stage output to a separate file in real-time (adds complexity, another file to manage)
- Use `--output-format text` instead of `stream-json` (loses structured event data needed for future features)
- Pipe stdout to tee (adds subprocess management complexity, platform differences)

**Consequences:**
- Positive: Zero change to Claude invocation. Same JSONL file serves both clean and timeout paths.
- Negative: Assembled text from multiple assistant turns includes all turns (not just the final summary). For the timeout case this is actually better — you see the full progression of work, not just the conclusion.

### Why Activity-Based Timeouts Instead of Longer Fixed Timeouts?

**Context:** QA currently gets 1200s (20 min). Both cmt-parity and v1-polish timed out at exactly this limit. cmt-parity was actively working (970KB output, Phase 1 complete, mid-Phase 2). v1-polish's first two runs were genuinely stuck (0 bytes). A fixed timeout can't distinguish these cases.

**Decision:** Replace fixed per-stage timeouts with stall-based timeout (5 min idle) + hard ceiling (60 min).

**Alternatives considered:**
- Raise fixed timeout to 45 min for QA (brute force — genuinely stuck runs waste 45 min instead of 20, doesn't solve the problem)
- Per-stage stall thresholds (e.g., QA gets 10 min stall, gates gets 3 min — adds complexity without clear benefit since the multi-signal activity detection already handles stage-specific patterns)
- Output-only activity check without git signals (misses implement stage where AI writes code but doesn't produce much output text)

**Evidence from failed pipelines:**

| Pipeline | Run | Output growth | Activity | Fixed timeout | Would stall timeout kill? |
|----------|-----|---------------|----------|---------------|--------------------------|
| cmt-parity | QA #1 | 0 → 0 bytes | idle | Killed at 20 min | Killed at 5 min (faster) |
| cmt-parity | QA #2 | 0 → 514KB | active | Killed at 20 min | NOT killed (would keep running) |
| v1-polish | QA #1 | 0 → 0 bytes | idle | Killed at 20 min | Killed at 5 min (faster) |
| v1-polish | QA #2 | 0 → 0 bytes | idle | Killed at 30 min | Killed at 5 min (faster) |
| v1-polish | QA #3 | 48KB → 2MB | active | Killed at 30 min | NOT killed (would keep running) |

**Consequences:**
- Positive: Stuck processes die 4x faster (5 min vs 20 min). Active processes get as long as they need (up to 60 min). The data already exists — `consecutive_idle` is computed every heartbeat.
- Positive: No per-stage tuning needed. The stall threshold is universal because activity detection is multi-signal.
- Negative: An active-but-not-converging process could run up to 60 min. The partial-results fixes in this spec make this acceptable — even if the ceiling fires, we capture the work done.
- Negative: A stage that produces output but is logically stuck (e.g., infinite retry loop with verbose logging) won't be caught by stall timeout. The hard ceiling catches this.

### Why Run Retro on Failure Instead of a Lightweight Diagnostic?

**Context:** Failed pipelines have the most to learn from but currently produce no retro.

**Decision:** Run the same retro skill with the standard stall-based timeout. The retro skill already handles pipeline mode and reads pipeline.log. Retro is typically short-lived (< 5 min of active work), so the stall timeout naturally handles it.

**Alternatives considered:**
- Build a separate "failure diagnostic" script in Python (duplicates retro analysis, another tool to maintain)
- Only include last output in pipeline-result and skip retro (misses pattern analysis, contributing factors)

**Consequences:**
- Positive: Single retro path for success and failure. Retro's mechanical metrics (stage timing, session counts, thrashing) help identify patterns across failed runs.
- Negative: 5 more minutes of compute on a failed pipeline. Worth it — the retro captures data that would otherwise be lost (e.g., "QA consumed 38% of total compute across 3 retries").
- Implementation note: Requires restructuring `_pipeline_fail()` from `sys.exit(1)` to a `PipelineFailure` exception so retro runs outside `SystemExit` handling (see Tier 1 spec above).

### Why Per-Phase State Writes Instead of Streaming to a Results File?

**Context:** QA results are lost on timeout because they're only written to state on full completion.

**Decision:** QA skill writes `qa_partial.phase1_completed` (top-level key, NOT nested under `qa`) to state.json after Phase 1 completes, using the existing `workflow-state.py set` command.

**Alternatives considered:**
- Write QA results to a separate `qa-results.json` file (adds another file to track, deviates from canonical state pattern)
- Stream results line-by-line to a log file (not structured, hard to query)
- Write to state.json from within each agent (agents are subprocesses — concurrent writes could corrupt state)

**Consequences:**
- Positive: Uses existing state infrastructure. Results are queryable via `workflow-state.py get qa_partial`. Pipeline-result can include them.
- Negative: Only captures phase-level granularity, not per-check. Acceptable — per-check detail is in the JSONL.

### Why 30-Minute Idle Timeout for Daemons?

**Context:** Orphaned tui-verify daemons prevented worktree cleanup. Two stale sessions found on disk.

**Decision:** 30-minute idle timeout with reset on any HTTP request.

**Alternatives considered:**
- Heartbeat file (daemon writes timestamp, external process checks staleness) — more complex, two actors needed
- No timeout, but cleanup skill always kills daemons — doesn't help if cleanup isn't run
- 5-minute timeout — too aggressive for interactive workflows where user pauses between verifications

**Consequences:**
- Positive: Self-healing. Daemons clean up after themselves without external coordination.
- Negative: If a user pauses for 31 minutes and comes back, daemon needs to relaunch (~5s). Acceptable.

---

## Related Specs

- [workflow-enforcement.md](./workflow-enforcement.md) — Pipeline orchestrator, workflow state, hooks
- [tui-verify-tool.md](./tui-verify-tool.md) — TUI verification daemon

---

## Changelog

| Date | Change |
|------|--------|
| 2026-03-26 | Initial spec — design session from retro findings |
| 2026-03-27 | v2.0 — pipeline reliability: (1) QA commits its own fixes, (2) review per-file chunking + cross-file summary pass, (3) converge runs on review FAIL (not just PASS), (4) QA/review dedup via shared findings in workflow state. Addresses issues #731, #727, #732, #730, #734, #712, #728, #729. |

---

## Roadmap

- **Pipeline analytics dashboard**: Aggregate pipeline-result.json across worktrees for success rate, average duration, common failure stages
- **Slack/Teams notification on pipeline failure**: Extend notify.py with richer failure detail from last_output
- **Stage-level retry with backoff**: Instead of fixed retry, use exponential backoff for flaky stages
- **Auto-heal from retro findings**: `auto-fix` tier findings applied automatically before pipeline retry

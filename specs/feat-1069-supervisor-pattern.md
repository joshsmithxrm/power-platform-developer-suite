# Supervisor Pattern

**Status:** Draft
**Last Updated:** 2026-05-16
**Code:** [scripts/goal_supervisor.py](../scripts/goal_supervisor.py) | [.claude/skills/orchestrate/](../.claude/skills/orchestrate/)
**Surfaces:** N/A (workflow tooling)
**Verification:** `python -m pytest tests/scripts/test_goal_supervisor.py -q`
**Verification Max Iterations:** 5

---

## Overview

The supervisor pattern allows one Claude session (the _supervisor_) to spawn N independent worker sessions from a PR-stack envelope, monitor them silently, and escalate only when a worker is blocked or fails. Workers are durable: each runs `pipeline.py` independently and `pr_monitor` ships individual PRs regardless of supervisor health. The supervisor is a best-effort aggregation layer — not a load-bearing dependency.

### Goals

- **Silent monitoring**: supervisor polls workers on a 270s cache-warm interval, producing no noise when things go well.
- **Single-signal escalation**: one clear trigger per worker — `state=blocked AND needs != ""` — fires a `PushNotification` and GitHub comment. No false positives.
- **Worker durability**: workers run `pipeline.py` independently; supervisor crash does not prevent PRs from shipping or individual notifications from firing.
- **Schema forward-compatibility**: goal-envelope.json is a v1.1 additive extension of the #1070-α PR-stack schema; `validate_envelope` in `pr_stack.py` accepts it without changes.

### Non-Goals

- Supervisor-of-supervisor (infinite regress). Session resumption is best-effort via `claude --resume`.
- Automatic retry of failed workers (operator intervenes after escalation).
- Cross-repo or cross-branch stacks.
- Real-time streaming of worker transcripts.

---

## Architecture

```
Operator
   │ /orchestrate .plans/<date>-<name>-stack.json
   ▼
Supervisor Claude session (background, ScheduleWakeup loop)
   │  goal_supervisor.py spawn <stack-json>
   │  creates .workflow/goal-envelope.json (v1.1)
   │  delivers inbox "note" per worker via supervisor_msg.py
   │  spawns N interactive bg workers via claude_dispatch.spawn
   │
   ├──▶ Worker A (claude --bg, worktree .worktrees/pr-<id-a>)
   │      runs /implement → /gates → /verify → /pr
   │      pipeline.py ships PR independently
   │      pr_monitor sends individual PR notification
   │
   ├──▶ Worker B (claude --bg, worktree .worktrees/pr-<id-b>)
   │      same independent pipeline
   │
   └── ScheduleWakeup(270s) → poll loop
            goal_supervisor.py poll
            reads per-worker: state.json + .workflow/state.json + gh pr view
            ambiguous states → Haiku evaluator (headless)
            goal_state=blocked → PushNotification + gh issue comment
            goal_state=all_merged → PushNotification, terminate loop
```

### Components

| Component | Responsibility |
|-----------|----------------|
| `scripts/goal_supervisor.py` | `spawn` subcommand: creates goal-envelope.json + spawns N workers; `poll` subcommand: checks worker states + updates envelope + prints verdict JSON |
| `.workflow/goal-envelope.json` | Runtime state file; v1.1 extension of pr_stack envelope; lives in supervisor's worktree |
| `.claude/skills/orchestrate/SKILL.md` | `/orchestrate` skill: reads stack.json → calls spawn → ScheduleWakeup loop → escalation + notification |
| `scripts/supervisor_msg.py` | Delivers initial "note" inbox message per worker (from #1114) |
| `scripts/claude_dispatch.py` | Worker spawn via `spawn(mode="interactive", permission_mode="bypassPermissions")` |
| Haiku evaluator | Single-turn headless `claude_dispatch.spawn(mode="headless")` for ambiguous states only |

### Dependencies

- Requires: [specs/dispatch-routing.md](./dispatch-routing.md) — `BlockedSessionError`, `BgHandle.poll()`, headless dispatch
- Extends: [specs/feat-1070-pr-stack-alpha.md](./feat-1070-pr-stack-alpha.md) — pr_stack envelope v1.1 (additive fields, same validator)
- Uses: [specs/goal-driven-implement.md](./goal-driven-implement.md) — goal-loop conventions; `run_until_green` pattern

---

## Specification

### goal-envelope.json Schema (v1.1)

The goal-envelope.json is a superset of the #1070-α PR-stack envelope. All existing fields (`schema_version`, `spec`, `created_at`, `stack`, `justification`) are unchanged. The `schema_version` is bumped to `"1.1"` when writing. `validate_envelope` from `pr_stack.py` already accepts any `"1."` prefix, so no changes to the validator are required.

**New envelope-level optional fields** (runtime state):

```json
{
  "schema_version": "1.1",
  "spec": "specs/...",
  "created_at": "<ISO-8601>",
  "stack": [ ... ],
  "supervisor_session_id": "<uuid>",
  "supervisor_worktree": "/abs/path/to/supervisor/worktree",
  "goal_poll_interval_sec": 270,
  "goal_state": "spawning | polling | all_merged | escalated | error"
}
```

**New stack-entry optional fields** (runtime state per worker):

```json
{
  "id": "pr-1",
  "worktree_path": "/abs/path/.worktrees/pr-1",
  "session_short": "a1b2c3d4",
  "session_id": "<uuid>",
  "spawned_at": "<ISO-8601>",
  "goal_state": "pending | spawned | working | blocked | merged | error",
  "blocked_needs": "<needs text when blocked>",
  "pr_number": 1234,
  "pr_url": "https://github.com/...",
  "pr_state": "OPEN | MERGED | CLOSED",
  "last_polled_at": "<ISO-8601>",
  "merged_at": "<ISO-8601>"
}
```

**Goal states (entry-level):**

| State | Meaning |
|-------|---------|
| `pending` | Entry registered, worker not yet spawned |
| `spawned` | `claude --bg` invoked, session starting |
| `working` | Session state=working, no block |
| `blocked` | `state=blocked AND needs != ""` — escalation fires |
| `merged` | `gh pr view` returns state=MERGED |
| `error` | Session state=error or unrecoverable |

### goal_supervisor.py Design

#### `spawn` subcommand

```
python scripts/goal_supervisor.py spawn <stack-json-path>
                                        [--supervisor-worktree <abs-path>]
                                        [--poll-interval <sec>]
```

1. Read and validate stack.json via `pr_stack.validate_envelope`.
2. For each stack entry:
   a. Compute worktree name from `entry.branch_suffix` (e.g., `pr-1`).
   b. Create worktree via `python scripts/worktree-create.py --name <branch_suffix> --branch feat/<branch_suffix>` (runs from repo root). This script performs: `git fetch origin main`, stale-directory detection, `git worktree add`, and a post-create sanity check. Surface stderr and abort on non-zero.
   c. Build worker prompt from `WORKER_PROMPT_TEMPLATE` (see below), writing to a temp file (same-directory atomic write via `tempfile` + `os.replace`).
   d. Spawn via `python scripts/start-bg-spawn.py --worktree-abs <abs-worktree-path> --branch feat/<branch_suffix> --prompt-file <temp-path> --permission-mode bypassPermissions --model sonnet`. Parse single-line stdout JSON for `short` and `sessionId`.
   e. Record `session_short`, `session_id`, `worktree_path`, `spawned_at`, `goal_state="spawned"` on the entry.
   f. Deliver initial inbox note via `supervisor_msg.send(worktree_path, "note", message=<goal_context>)`.
   g. Delete the temp prompt file.
3. Write goal-envelope.json (v1.1) to `<supervisor_worktree>/.workflow/goal-envelope.json`.
4. Print spawn result JSON to stdout (`{"spawned": N, "envelope": "<path>", "entries": [...]}`).
5. Write errors to stderr; exit 0 on success, exit 1 on any spawn failure (remaining entries still attempted; failed entries get `goal_state="error"`).

**Worker prompt template** (`WORKER_PROMPT_TEMPLATE`):

The template embeds the standard workflow contract from `/start` Step 6b (so the worker follows the same protocol as any operator-started session), preceded by a task brief, and followed by a supervisor-specific note appended after the contract block.

```
Task brief — PR stack worker
Entry: {id} — {title}
Worktree: {worktree_path}
Spec: {spec}
Plan: {plan}
Branch: feat/{branch_suffix}
Issues: {issue_numbers}

You are running in headless mode via the goal supervisor. Do not ask
clarifying questions — make reasonable decisions and proceed.

Workflow contract:
1. Read CLAUDE.md, specs/CONSTITUTION.md, .claude/interaction-patterns.md.
2. This worker has a pre-approved plan ({plan}). Skip /design.
   Run `python scripts/pipeline.py --spec {spec} --plan {plan}` directly.
   On failure: python scripts/pipeline.py --resume (or --from <stage>).
3. After `python scripts/pipeline.py` exits successfully (pipeline includes
   /pr internally): launch pr_monitor via Bash run_in_background=true:
     python scripts/pr_monitor.py --worktree {worktree_path} --pr <PR-number>
   Claude Code will re-engage you when pr_monitor exits.
4. At re-engagement: read .workflow/pr-monitor-result.json and produce a final
   summary covering actual PR state (ready / merged / escalated / error /
   blocked). Terminate.

Supervisor note (read first, before any other action):
  python scripts/supervisor_msg.py read --consume
Goal context: {title}. Plan at {plan}. Implement and ship — no design phase needed.
```

#### `poll` subcommand

```
python scripts/goal_supervisor.py poll [--worktree <abs-path>]
```

1. Read `<supervisor_worktree>/.workflow/goal-envelope.json`.
2. For each entry not already `merged`:
   a. Read `~/.claude/jobs/<session_short>/state.json` → session state + needs.
   b. Read `<worktree_path>/.workflow/state.json` → workflow phase + pr_url + pr_number.
   c. If `session_state=blocked AND needs != ""` → set `goal_state="blocked"`, `blocked_needs=<needs>`.
   d. If `session_state=done OR pr_url set`:
      - Run `gh pr view <pr_number> --json state --jq .state` → pr_state.
      - If MERGED → set `goal_state="merged"`, `merged_at=now`.
      - If OPEN or CLOSED and session=done → invoke Haiku evaluator (§Haiku Predicate).
   e. If `session_state=error` → set `goal_state="error"`.
   f. Otherwise → `goal_state="working"` (or retain `spawned` if no change yet).
   g. Update `last_polled_at`.
3. Compute overall `goal_state`:
   - All entries merged → `"all_merged"`.
   - Any entry blocked or error → `"escalated"` (retain until resolved).
   - Otherwise → `"polling"`.
4. Write updated envelope to disk.
5. Print verdict JSON to stdout:
   ```json
   {"goal_state": "all_merged|escalated|polling", "entries": [...]}
   ```
6. Write progress to stderr; exit 0 always (callers inspect the JSON).

### Haiku Predicate Format

Invoked only when a session reports `state=done` but no PR has been created yet (ambiguous: did /pr fail silently, or is /pr still pending?).

**Template** (sent as headless prompt to Haiku via `claude_dispatch.spawn(mode="headless")`):

```
You are a one-turn evaluator. Assess this PR worker's completion state.

Context:
  entry_id: {entry_id}
  title: {title}
  session_state: {session_state}
  workflow_phase: {workflow_phase}
  pr_url: {pr_url}
  pr_state: {pr_state}

"session_state=done" means the Claude session exited. If pr_url is set and pr_state
is MERGED, the worker is definitely done. If session_state=done but no pr_url exists,
something likely went wrong.

Reply with EXACTLY this JSON and nothing else:
{"verdict": "done|blocked|working|error", "confidence": "high|low", "reason": "<one sentence>"}
```

Haiku result is parsed as JSON; `verdict` drives the entry's `goal_state`. On Haiku parse failure, treat as `goal_state="error"` and escalate.

### /orchestrate Skill

#### Step 0: Readiness gate

`python scripts/supervisor_msg.py read --consume` — handle abort/revise per §inbox protocol.
`python scripts/workflow-state.py set phase orchestrating`

#### Step 1: Spawn workers

```bash
python scripts/goal_supervisor.py spawn <stack-json-path>
```

Display a table: entry ID | title | worktree | session short | status.

#### Step 2: Schedule first poll

`ScheduleWakeup(delaySeconds=270, prompt=<poll-prompt>)` — cache-warm interval.

**Poll prompt** (passed verbatim each wakeup):
```
Supervisor poll: python scripts/goal_supervisor.py poll
Read the JSON output and act:
  - "all_merged" → PushNotification("All PRs merged: <summary>") and stop.
  - "escalated" → PushNotification("Blocked worker: <entry_id>: <needs>")
                   gh issue comment <issue_number> -b "<escalation text>"
                   ScheduleWakeup(270s) to keep monitoring unblocked workers.
  - "polling" → ScheduleWakeup(270s) with this same prompt.
```

#### Step 3: Notification behavior

| Trigger | Action |
|---------|--------|
| `goal_state=all_merged` | `PushNotification(title="Supervisor: all merged", body="N PRs merged for <spec>")` + terminate |
| Entry `goal_state=blocked` | `PushNotification(title="Supervisor: worker blocked", body="<entry_id>: <blocked_needs>")` + `gh issue comment` + continue polling |
| Entry `goal_state=error` | `PushNotification(title="Supervisor: worker error", body="<entry_id> session failed")` + continue polling |

### Escalation Rule (Single-Signal)

Escalation fires when **both** conditions hold simultaneously:
- `session_state == "blocked"`
- `needs` field is non-empty after `.strip()`

Empty `needs` (transient daemon flip) does NOT escalate. This mirrors the existing `BgHandle.wait()` semantics in `claude_dispatch.py`.

### Supervisor Crash Tolerance

The supervisor is a best-effort convenience layer (issue #1069 option 3+4):

1. **Workers are independent**: each runs `pipeline.py` end-to-end in its own worktree. Loss of supervisor does not stop workers.
2. **pr_monitor is the durable signal**: each worker's `pr_monitor` sends a `PushNotification` on PR merge. Operator receives individual notifications even without the supervisor.
3. **Resumption is best-effort**: the supervisor session can be resumed via `claude --resume <supervisor_session_id>` (stored in `goal-envelope.json`). `ScheduleWakeup` re-fires on resume.

This design means the supervisor adds aggregation value when healthy, but is never load-bearing.

### supervisor_msg.py Usage

| Direction | When | Kind | Content |
|-----------|------|------|---------|
| Supervisor → Worker | At spawn | `note` | Goal context: entry title, plan path, reminder to read inbox |
| Supervisor → Worker | On abort decision | `abort` | Reason from escalation |
| Worker → Supervisor | Not used | — | Workers do not write to supervisor inbox in this design |

---

## Forward Compatibility

This spec amends [specs/feat-1070-pr-stack-alpha.md](./feat-1070-pr-stack-alpha.md) (per SL2 — specs are living documents) to add a "§Goal-Supervisor Extension (v1.1)" section documenting the runtime fields defined here. The `schema_version` bumps from `"1.0"` to `"1.1"`. The `validate_envelope` function already accepts any `"1."` prefix — no code changes needed to the existing validator.

---

## Acceptance Criteria

| ID | Criterion | Test | Status |
|----|-----------|------|--------|
| AC-01 | `goal_supervisor.py spawn <valid-stack-json>` creates `<supervisor_worktree>/.workflow/goal-envelope.json` with `schema_version="1.1"` and all entries `goal_state="spawned"` | `test_goal_supervisor.test_spawn_creates_envelope` | ✅ |
| AC-02 | `goal_supervisor.py spawn` exits 1 with stderr message when stack.json fails `validate_envelope` | `test_goal_supervisor.test_spawn_rejects_invalid_stack` + `test_cli_spawn_invalid_stack_exits_1` | ✅ |
| AC-03 | `goal_supervisor.py spawn` exits 1 with stderr message when stack.json has `schema_version` major != 1 | `test_goal_supervisor.test_spawn_rejects_wrong_major` | ✅ |
| AC-04 | `goal_supervisor.py poll` reads `goal-envelope.json`, updates `last_polled_at` for each non-merged entry, and writes the updated file | `test_goal_supervisor.test_poll_updates_last_polled_at` | ✅ |
| AC-05 | `goal_supervisor.py poll` sets overall `goal_state="all_merged"` and prints `{"goal_state": "all_merged"}` JSON to stdout when all entries have `goal_state="merged"` | `test_goal_supervisor.test_poll_all_merged` | ✅ |
| AC-06 | `goal_supervisor.py poll` sets entry `goal_state="blocked"` and `blocked_needs=<text>` when the worker's `state.json` has `state=blocked` and non-empty `needs` | `test_goal_supervisor.test_poll_escalation_blocked` | ✅ |
| AC-07 | `goal_supervisor.py poll` sets entry `goal_state="merged"` and `merged_at` when `gh pr view` returns `state=MERGED` | `test_goal_supervisor.test_poll_merged_via_gh` | ✅ |
| AC-08 | `goal_supervisor.py poll` sets entry `goal_state="error"` when the worker's `state.json` has `state=error` | `test_goal_supervisor.test_poll_worker_error` | ✅ |
| AC-09 | `goal_supervisor.py poll` does NOT escalate when `state=blocked` but `needs` is empty or whitespace-only | `test_goal_supervisor.test_poll_no_escalation_empty_needs` | ✅ |
| AC-10 | Haiku predicate template renders valid UTF-8 text containing the context fields; parsed Haiku response `{"verdict": "...", "confidence": "...", "reason": "..."}` drives entry `goal_state` | `test_goal_supervisor.test_haiku_predicate_parses` + `test_haiku_parse_failure_marks_error` | ✅ |
| AC-11 | `goal_supervisor.py` writes all progress to stderr and all result JSON to stdout (Constitution I1) | `test_goal_supervisor.test_stdout_discipline` | ✅ |
| AC-12 | `goal_supervisor.py` uses stdlib only — no imports outside the stdlib or existing project scripts | `test_goal_supervisor.test_no_extra_imports` | ✅ |
| AC-13 | `pr_stack.validate_envelope` accepts a goal-envelope.json with `schema_version="1.1"` and v1.1 runtime fields without raising | `test_goal_supervisor.TestGoalSupervisorCompat.test_validate_v1_1_accepted` | ✅ |
| AC-14 | `.claude/skills/orchestrate/SKILL.md` documents: spawn step calling `goal_supervisor.py spawn`, ScheduleWakeup at 270s, escalation via PushNotification + gh comment, termination when `all_merged` | `test_goal_supervisor.TestOrchestrateSkill.test_skill_documents_spawn` | ✅ |
| AC-15 | `.claude/skills/orchestrate/SKILL.md` documents the single-signal escalation rule: `state=blocked AND needs != ""` | `test_goal_supervisor.TestOrchestrateSkill.test_skill_documents_escalation_rule` | ✅ |
| AC-16 | `.claude/skills/orchestrate/SKILL.md` documents that workers are independent and the supervisor crash does not prevent individual PR notifications | `test_goal_supervisor.TestOrchestrateSkill.test_skill_documents_crash_tolerance` | ✅ |
| AC-17 | Smoke: a goal-envelope.json with 2 entries both having `goal_state="merged"` drives `goal_supervisor.py poll` to output `{"goal_state": "all_merged"}` | `test_goal_supervisor.test_smoke_two_entry_all_merged` | ✅ |
| AC-18 | Smoke: a goal-envelope.json with 1 entry having `goal_state="blocked"` and `blocked_needs="fix the layout"` drives `goal_supervisor.py poll` to output `{"goal_state": "escalated"}` | `test_goal_supervisor.test_smoke_one_blocked` | ✅ |
| AC-19 | Worker prompt template contains all workflow contract elements: (a) skip-design note referencing the pre-approved plan, (b) `pipeline.py --spec --plan` invocation, (c) `--resume` fallback, (d) `pr_monitor.py` via Bash `run_in_background=true`, (e) re-engagement reads `.workflow/pr-monitor-result.json` and terminates | `test_goal_supervisor.test_worker_prompt_contains_workflow_contract` | ✅ |

Status key: ✅ covered by passing test · ⚠️ test exists but failing · ❌ no test yet · 🔲 not yet implemented

### Edge Cases

| Scenario | Expected Behavior |
|----------|-------------------|
| Worker worktree does not exist | Entry `goal_state="error"`, stderr message, poll continues |
| state.json missing (worker just spawned) | Treat as `goal_state="spawned"`, no escalation |
| `gh pr view` fails (no PR yet) | Retry on next poll; no escalation |
| Haiku evaluator parse failure | Set `goal_state="error"`, escalate |
| All workers error | `goal_state="escalated"`, no ScheduleWakeup scheduled (terminate) |

---

## Design Decisions

### Why 270s poll interval?

**Context:** Anthropic prompt cache has a 5-minute (300s) TTL. Sleeping past 300s means the next wakeup reads the full conversation context uncached — slower and more expensive.

**Decision:** 270s — stays inside the cache window. Each poll costs a warm-cache read; the supervisor can run for hours without compounding cache misses.

**Alternatives considered:**
- 60s: too frequent; burns cache 5x per cache window for nothing.
- 300s+: pays the cache miss every time. Wrong side of the TTL cliff.
- 1800s: appropriate for fully idle waits, but supervisors are actively checking worker state.

### Why options 3+4 for crash tolerance?

**Context:** Issue comment surfaced the "what if supervisor crashes?" question. Four options presented.

**Decision:** Option 3 (workers notify independently via pr_monitor) + Option 4 (accept supervisor as best-effort) is the right composition. Each worker runs `pipeline.py` and `pr_monitor` as a fully self-contained unit. The supervisor adds aggregated notification, not durable orchestration.

**Alternatives considered:**
- Option 1 (supervisor resumable): requires storing `supervisor_session_id` in `goal-envelope.json` (we do) and operator knows to run `claude --resume <id>`. This is a free win — we already store the ID.
- Option 2 (watchdog hook): adds complexity for a component that is explicitly best-effort.

### Why Haiku for ambiguous states only?

**Context:** Most worker states are unambiguous (blocked=clear escalation, merged=clear done). Haiku adds latency and cost.

**Decision:** Only invoke Haiku when `session_state=done` but no PR URL is present. This is the one genuinely ambiguous case — did /pr fail silently, or is /pr still running?

**Alternatives considered:**
- Always use Haiku for every state check: wastes tokens on obvious cases.
- Never use Haiku: miss the ambiguous session=done/no-PR case.

---

## Related Specs

- [feat-1070-pr-stack-alpha.md](./feat-1070-pr-stack-alpha.md) — canonical PR-stack envelope schema (v1.0 → v1.1 extended here)
- [dispatch-routing.md](./dispatch-routing.md) — worker spawn via `claude_dispatch.spawn`, `BlockedSessionError` semantics
- [goal-driven-implement.md](./goal-driven-implement.md) — goal-loop conventions followed by workers

---

## Changelog

| Date | Change |
|------|--------|
| 2026-05-16 | Initial spec — Phase 2 capstone of epic #1066 |

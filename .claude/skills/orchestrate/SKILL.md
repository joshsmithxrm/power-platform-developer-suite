---
name: orchestrate
description: Spawn N PR-stack workers from a pre-validated stack envelope, monitor them silently, and escalate only when a worker is blocked or fails. Use when a PR-stack JSON exists at .plans/<date>-<name>-stack.json and you want to run all entries in parallel without baby-sitting them. Workers are durable — supervisor crash does not prevent individual PRs from shipping.
---

# Orchestrate

Drive an N-worker PR-stack run via `scripts/goal_supervisor.py`. The supervisor is a best-effort aggregation layer — workers ship PRs independently via `pipeline.py` + `pr_monitor.py`, so loss of the supervisor never blocks an individual PR notification.

## When to Use

Operator types `/orchestrate <path-to-stack-json>` after `/design` has produced a PR-stack envelope and the operator has reviewed the entries. Skip this skill for single-PR work; use `/implement` directly.

## Process

### Step 0: Readiness Gate

```bash
python scripts/supervisor_msg.py read --consume
```

Handle inbox kinds per `.claude/interaction-patterns.md`:
- `abort` → stop, summarize reason, terminate.
- `revise` → apply feedback before continuing.
- `approve` / `note` / empty → proceed.

```bash
python scripts/workflow-state.py set phase orchestrating
```

### Step 1: Spawn Workers

```bash
python scripts/goal_supervisor.py spawn <stack-json-path>
```

The `spawn` subcommand:
- Validates the stack envelope via `pr_stack.validate_envelope` — rejects wrong major version, missing fields, cycles.
- Creates a worktree per entry under `.worktrees/<branch_suffix>/` via `scripts/worktree-create.py`.
- Builds a worker prompt per entry from `WORKER_PROMPT_TEMPLATE` (pre-approved plan, skip `/design`, run `pipeline.py --spec --plan`, then launch `pr_monitor.py` in the background, then terminate after reading `.workflow/pr-monitor-result.json`).
- Spawns each worker via `scripts/start-bg-spawn.py --permission-mode bypassPermissions --model sonnet`.
- Delivers an initial inbox `note` to each worker via `scripts/supervisor_msg.py send`.
- Persists `<supervisor_worktree>/.workflow/goal-envelope.json` (schema_version `1.1`) with each entry initialized to `goal_state="spawned"`.

Stdout is one-line summary JSON `{"spawned": N, "envelope": "<path>", "entries": [...]}`. Progress and errors go to stderr (Constitution I1).

Display a table to the operator: entry ID | title | worktree | session short | status.

### Step 2: Schedule First Poll

```
ScheduleWakeup(delaySeconds=270, prompt=<poll-prompt-below>)
```

**270s is intentional** — the Anthropic prompt cache TTL is 300s. Polling every 270s keeps the supervisor's context cache warm; polling at 300s+ pays the cache-miss cost every cycle.

**Poll prompt** (pass verbatim each wakeup so the loop survives compaction):

```
Supervisor poll: python scripts/goal_supervisor.py poll
Read the JSON output and act:
  - "all_merged" → PushNotification("All PRs merged: <summary>") and stop.
  - "escalated" → PushNotification("Blocked worker: <entry_id>: <needs>")
                   gh issue comment <issue_number> -b "<escalation text>"
                   ScheduleWakeup(270s) to keep monitoring unblocked workers.
  - "polling" → ScheduleWakeup(270s) with this same prompt.
```

### Step 3: Notification Behavior

| Trigger | Action |
|---------|--------|
| `goal_state=all_merged` | `PushNotification(title="Supervisor: all merged", body="N PRs merged for <spec>")` then terminate the polling loop |
| Entry `goal_state=blocked` | `PushNotification(title="Supervisor: worker blocked", body="<entry_id>: <blocked_needs>")` + `gh issue comment <issue_number> -b "<escalation text>"` + continue polling |
| Entry `goal_state=error` | `PushNotification(title="Supervisor: worker error", body="<entry_id> session failed")` + continue polling |

## Single-Signal Escalation Rule

Escalation fires when **both** conditions hold simultaneously:
- `state=blocked` (from the worker's `~/.claude/jobs/<short>/state.json`)
- `needs != ""` (non-empty after `.strip()`)

Empty `needs` (transient daemon flip) does NOT escalate. This mirrors `BgHandle.wait()` semantics in `claude_dispatch.py` and is the single contract between supervisor and worker — no other state combination triggers escalation.

## Crash Tolerance

The supervisor is best-effort and never load-bearing:

1. **Workers are independent.** Each runs `pipeline.py` end-to-end in its own worktree. Loss of the supervisor does not stop any worker.
2. **`pr_monitor` is the durable signal.** Each worker launches `scripts/pr_monitor.py` in the background after `pipeline.py` exits successfully. `pr_monitor` sends a `PushNotification` on PR merge regardless of supervisor health — the operator receives per-PR notifications even when the supervisor has crashed.
3. **Resumption is best-effort.** The supervisor session ID is recorded in `goal-envelope.json` (`supervisor_session_id`). Operator may resume via `claude --resume <id>`; `ScheduleWakeup` re-fires on resume. No automatic resurrection.

The supervisor adds aggregation value (single "all merged" notification, single "X blocked" escalation) but its absence does not block individual PRs from shipping or individual notifications from firing.

## Termination

The supervisor terminates the polling loop in exactly one case: `goal_state=all_merged`. Send the final `PushNotification("All PRs merged: ...")` and do not call `ScheduleWakeup` again. Escalated and polling states both keep the loop alive — the operator is expected to intervene on blocked workers while merged workers continue to ship.

## Files

- `scripts/goal_supervisor.py` — `spawn` / `poll` subcommands
- `scripts/supervisor_msg.py` — inbox primitive (Supervisor → Worker `note`)
- `scripts/start-bg-spawn.py` — worker spawn via `claude --bg`
- `scripts/worktree-create.py` — per-entry worktree creation
- `<supervisor_worktree>/.workflow/goal-envelope.json` — runtime state (v1.1)

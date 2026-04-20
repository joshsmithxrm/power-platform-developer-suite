---
name: backlog
description: Create, triage, and manage GitHub issues per PPDS backlog conventions. Use when creating issues, grooming the backlog, reviewing what to work on next, or filing bugs.
---

# Backlog

Manage GitHub issues using the label taxonomy, milestone conventions, and triage rules defined in `docs/BACKLOG.md`.

## When to Use

- "Create an issue for X"
- "File a bug for X"
- "Triage the backlog"
- "Groom issues"
- "What should we work on next?"
- "Review backlog priorities"
- "Dispatch parallel worktrees", "kick off the planned waves", "launch the dispatcher session"
- After completing work that reveals follow-up issues

## Reference

Read `docs/BACKLOG.md` at the start of every backlog operation. It is the source of truth for labels, milestones, and rules.

## Process

### 1. Parse Arguments

Check `$ARGUMENTS` for the operation:

- `/backlog create <description>` — create a new issue
- `/backlog triage` — triage untriaged inbox
- `/backlog review` — review backlog for promotion
- `/backlog validate` — verify open issues are still valid
- `/backlog dispatch` — plan and launch parallel worktrees from triage state (see [Subverb: dispatch](#subverb-dispatch) below)
- `/backlog` (no args) — show backlog summary

### 2. Read Rules

```bash
# Always read the reference doc first
cat docs/BACKLOG.md
```

### 3. Execute Operation

#### Create Issue

##### Pre-flight: In-Flight Conflict Check

Before `gh issue create`, ALWAYS check whether a sibling session is
already working on the same area or has already filed a related issue
(prevents the duplicate-work pattern from retro B3 / #802):

```bash
python scripts/inflight-check.py --area <best-guess-area>
```

If exit `1`, surface the conflict to the operator and ASK before filing:

> "Session `<id>` (branch `<branch>`) is actively working on this area
> with intent `<intent>`. They may already be addressing this — file
> anyway, or coordinate first?"

The same check MUST be repeated before `gh issue close`: if a sibling
session has open work referencing the issue, surface the related branch
so the closer does not misattribute the fix to the wrong PR.

##### Steps

1. Determine `type:` label from the description (bug, enhancement, docs, performance, refactor)
2. Determine `area:` label from the affected subsystem (auth, cli, data, extension, plugins, tui)
3. Ask user for milestone or default to no milestone
4. Check if an `epic:` label applies
5. Ask user for `priority:` label if not obvious
6. Create the issue:

```bash
gh issue create --title "<title>" --body "<body>" --label "type:<type>,area:<area>" --milestone "<milestone>"
```

If no milestone, add `status:backlog`:

```bash
gh issue create --title "<title>" --body "<body>" --label "type:<type>,area:<area>,status:backlog"
```

**Rule:** Every issue must have at minimum one `type:` + one `area:` + either a milestone or `status:` label.

#### Triage Inbox

Find untriaged issues (no milestone AND no `status:` label):

```bash
gh issue list --state open --limit 200 --json number,title,labels,milestone --jq '[.[] | select(.milestone == null) | select(.labels | map(.name) | any(startswith("status:")) | not)]'
```

##### Mandatory Verification

Before presenting ANY issue to the user, verify it against the codebase:

1. Dispatch parallel agents to check each issue's premise:
   - Does the referenced code/feature/artifact still exist?
   - Is the problem described still a real problem?
   - Has the issue already been implemented?
   - Does the issue reference deleted artifacts (old ADRs, dead skills, renamed files)?
2. Flag issues with stale premises for rescoping or closure.
3. Only present verified issues to the user.

**Never present unverified issues.** The user should not have to catch stale references or already-implemented features.

##### Presentation Format

For each verified issue, present using context-first format:

1. **Context** (2-3 sentences): What this issue is about, why it was filed, what the current state of the relevant code is.
2. **Recommendation**: Milestone it (which one), backlog it, or close it — with reasoning.
3. **Decision prompt**: Let the user decide.

Do NOT present summary tables without per-issue context. Tables are for recap after decisions are made, not for requesting decisions.

##### Strategic Framing

When a group of issues shares a common theme (e.g., all CMT parity gaps, all testing infrastructure), ask the strategic question first:

> "These N issues are all [theme]. Do you want to treat them as [milestone] blockers as a group, or evaluate individually?"

The user's decision is often driven by a single strategic insight, not N individual evaluations. Surface that framing question before spending time on per-issue analysis.

##### Triage Decisions

For each issue, get a decision:
- **Milestone it** — assign to v1.0, v1.1, or v2.0
- **Backlog it** — add `status:backlog` (optionally with a `priority:` label)
- **Close it** — close with a reason

##### Priority Assignment

After resolving type/area/milestone, check if the issue has a `priority:` label. If not, recommend one (critical/high/medium/low) and let the user decide or skip.

Also check for missing labels:

```bash
# Missing type: label
gh issue list --state open --limit 100 --json number,labels --jq '[.[] | select(.labels | map(.name) | any(startswith("type:")) | not)] | .[].number'

# Missing area: label
gh issue list --state open --limit 100 --json number,labels --jq '[.[] | select(.labels | map(.name) | any(startswith("area:")) | not)] | .[].number'
```

Fix any issues with missing required labels during triage.

#### Review Backlog for Promotion

Pull high-priority backlog items that might be ready for a milestone:

```bash
gh issue list --search 'is:open label:"status:backlog" (label:"priority:high" OR label:"priority:critical")' --json number,title,labels
```

Present each to user: promote to milestone, keep in backlog, or re-prioritize.

#### Validate Open Issues

Verify that open issues are still valid — premises match current codebase, referenced artifacts exist, problems haven't already been solved.

1. Fetch all open issues (or a filtered subset if the user specifies a milestone/label).
2. Dispatch parallel agents to verify each issue's premise against the codebase.
3. Present findings grouped by status:
   - **Still valid** — premise confirmed, no action needed
   - **Already implemented** — recommend closure with evidence
   - **Stale premise** — referenced artifacts deleted, problem description outdated; recommend rescope or closure
4. Let the user decide on each flagged issue.

#### Summary (no args)

Show a quick overview:

```bash
# Counts by milestone
echo "=== By Milestone ==="
gh issue list --state open --milestone "v1.0" --json number --jq 'length'
gh issue list --state open --milestone "v1.1" --json number --jq 'length'
gh issue list --state open --milestone "v2.0" --json number --jq 'length'

# Backlog count
echo "=== Backlog ==="
gh issue list --state open --label "status:backlog" --json number --jq 'length'

# Inbox (untriaged)
echo "=== Inbox (untriaged) ==="
gh issue list --state open --limit 200 --json number,labels,milestone --jq '[.[] | select(.milestone == null) | select(.labels | map(.name) | any(startswith("status:")) | not)] | length'
```

## Subverb: dispatch

Addresses v1-launch retro item #10 / finding B5: the historical
"dispatcher session" pattern (e.g. session `cd0c578e`) was high-leverage
but human-gated — Josh has had to be the human dispatcher every time
because the skill stopped at "here is the plan, please run /start
yourself N times". This subverb closes that loop while preserving every
gate the human was applying tacitly: a durable plan, an in-flight
conflict check per worktree, and an explicit "go" before any
worktree creates.

### Five-phase flow

The dispatch subverb runs as a strict sequence. Do not skip phases —
each one feeds state the next phase reads.

#### Phase A — Plan generation

1. Re-run the **Triage Inbox** flow above to identify what is ready to
   dispatch. Constrain by the user's prompt (e.g. "all v1.0 blockers",
   "the audit-capture epic", "the next 5 high-priority backlog items").
2. Group the candidates into worktrees by **file-overlap analysis** (the
   pattern from session `cd0c578e`'s 6-worktree v1.0 plan): two issues
   that touch the same code area should land in the same worktree so
   the in-flight conflict check does not flag the dispatcher's own
   parallel work as a self-collision. Issues that touch disjoint areas
   become separate worktrees.
3. For each grouped worktree, capture:
   - **worktree name** (kebab-case branch label, e.g. `feat/issue-660-auth-pool` or
     `feat/audit-capture`)
   - **issues** (one or more `#NNN`)
   - **areas** (best-guess code paths the work will touch — used by the
     conflict check below)
   - **intent** (one-line human-readable summary)
4. Persist the plan to `.claude/state/dispatch-plan.md` via the helper
   so it survives session close (the dispatcher session may be killed
   between Phase C and Phase D — Phase E re-reads the file):

   ```bash
   python -c "import sys, json
   sys.path.insert(0, 'scripts')
   from dispatch_plan import build_plan_from_dicts, write_plan
   items = json.loads(sys.stdin.read())
   write_plan(build_plan_from_dicts(items, generator='session-<id>'))
   " <<EOF
   [
     {"worktree": "feat/issue-660", "issues": [660], "areas": ["src/PPDS.Auth/"], "intent": "env var auth"},
     {"worktree": "feat/audit-capture", "issues": [101, 102], "areas": ["src/PPDS.Audit/"], "intent": "audit capture pipeline"}
   ]
   EOF
   ```

   The plan path lives at `.claude/state/dispatch-plan.md` —
   intentionally **not** under `.plans/` (which is gitignored and
   ephemeral) and **not** under `.claude/settings.local.json` (which is
   personal). The `.claude/state/` directory is the established home
   for cross-session durable state (the in-flight registry from
   PR #813 lives next to it).

#### Phase B — Pre-flight per-worktree conflict check

Before surfacing the plan to the operator, walk every entry and call
`scripts/inflight-check.py` for each `(issue, area)` pair. The helper
`annotate_with_conflicts` in `scripts/dispatch_plan.py` does this in one
call:

```bash
python -c "
import sys
sys.path.insert(0, 'scripts')
from dispatch_plan import annotate_with_conflicts, load_plan, write_plan
plan = load_plan()
annotate_with_conflicts(plan)
write_plan(plan)
"
```

Per-entry behavior:

- **No conflict** — entry stays at status `planned`.
- **Conflict** — entry status flips to `conflict`, `Conflict:` line
  records `session <id> on <branch> (<intent>)` for each overlapping
  open work item.

Halt and report rather than auto-resolving. The operator decides
whether to re-scope, wait for the conflicting session to finish, or
override (an override is a manual edit of the plan file flipping
`Status: conflict` back to `Status: planned`).

If `inflight-check.py` itself fails (exit 2 / unexpected non-zero),
surface the failure and stop — a broken gate is not the same as an
empty conflict list.

#### Phase C — User confirmation

Present the plan to the operator with explicit framing:

> "I have a plan for **N worktrees**: M ready to dispatch, K blocked by
> in-flight conflicts. The plan is written to
> `.claude/state/dispatch-plan.md` (durable, survives session close).
>
> Ready to dispatch the M unblocked worktrees? Type `go` to launch, or
> tell me which entries to skip / re-scope first."

Show a per-entry summary in the chat (worktree name, issues, intent,
status). Do **not** create any worktree until the operator types `go`
(or an equivalent unambiguous confirmation). This is the line that
keeps a runaway dispatcher from spawning worktrees the human did not
agree to.

#### Phase D — Dispatch

Two spawn mechanisms are supported. Pick **one per dispatch wave** based
on the lane assignment in `.claude/interaction-patterns.md` §1:

- **D.1 Agent-with-isolation** (recommended for Lane B work — small,
  countable: ≤3 files, ≤200 LOC net, no new abstraction, clearly
  correct fix). The dispatcher calls the `Agent` tool with
  `isolation: "worktree"` per entry and collects returned PR URLs.
  No manual terminals, no user babysitting. See meta-retro #11.
- **D.2 Inline-prompt launch command** (for Lane C work, or when the
  operator wants a live foreground session per worktree). The
  dispatcher still creates the worktree + state, then emits a single
  copy-pasteable `claude … -p "<prompt>"` command per entry. The
  prompt MUST be inline — spawning without a prompt is what forced
  the 5-terminal babysitting of session `cd0c578e` and is now
  forbidden (meta-retro #11).

Decide the lane **before** Phase D starts, encode it by suffixing
each entry's intent with `[lane B]` or `[lane C]` (the `Intent`
field is one of the keys the parser round-trips, so the lane tag
survives subsequent `write_plan` calls), and surface which path this
wave takes in the Phase C confirmation message so the operator knows
whether autonomous dispatch or manual terminal paste is about to
happen.

For each entry whose status is `planned` (in plan-file order):

1. Re-check conflicts immediately before launch — the in-flight state
   may have changed between Phase B and now (e.g. a sibling session
   completed). If a new conflict appears, mark the entry `skipped`
   with the new conflict detail and continue to the next entry.

2. Create the worktree and initialize state (both paths share this):

   ```bash
   git worktree add .worktrees/<name> -b feat/<name>
   python scripts/workflow-state.py init "feat/<name>"
   python scripts/inflight-register.py --branch "feat/<name>" --issue <N> --intent "<intent>"
   ```

   `--issue` is repeatable — for a grouped entry with multiple issues,
   repeat the flag (`--issue 660 --issue 661`). `--issues` is **not** a
   valid argument and will fail with "unrecognized arguments".

   Write `.plans/context.md` inside the new worktree with the issue
   bodies + routing guidance (same content `/start` step 5b writes —
   the launched session / isolated agent reads this file on entry).

3. **D.1 path — Agent-with-isolation.** Call the `Agent` tool once
   per entry with:

   ```
   isolation: "worktree"
   cwd: .worktrees/<name>
   prompt: |
     Read .plans/context.md. Invoke /implement → /verify → /pr for
     issue #NNN (and #MMM if grouped). Return the PR URL when done.
   ```

   The agent returns the PR URL inline. Pass it to `mark_launched`
   via the `session_id` parameter (see step 5) — that is the only
   launch-identifier slot the plan's data model exposes, and the
   parser renders it as a `Session: <value>` line. A freeform
   `PR: <url>` line would be dropped on the next `write_plan` round-
   trip because the parser only recognizes the documented keys. No
   manual terminal is opened.

4. **D.2 path — inline-prompt launch command.** Emit to chat (the
   operator pastes it into a new terminal):

   ```
   cd .worktrees/<name> && claude --dangerously-skip-permissions -p "Read .plans/context.md; invoke /implement → /verify → /pr for issue #NNN."
   ```

   The `-p "<prompt>"` is **mandatory** — a bare `claude` invocation
   drops the operator into an empty session with no context, which
   is the exact failure mode meta-retro #11 flagged. If the plan
   entry has multiple issues, list them all in the prompt.

5. Capture the launch identifier — the session ID for D.2 or the
   agent-returned PR URL for D.1 — and mark the entry launched. The
   `mark_launched` helper (in `scripts/dispatch_plan.py`) takes a
   single `session_id` keyword argument; that slot carries the PR URL
   for D.1 by convention because the plan's data model does not have a
   separate PR field. The value round-trips through the markdown as a
   `Session: <value>` line (see `PlanEntry.as_markdown`).

   ```bash
   python -c "
   import sys
   sys.path.insert(0, 'scripts')
   from dispatch_plan import load_plan, mark_launched, write_plan
   plan = load_plan()
   # session_id carries the D.2 session ID or the D.1 PR URL
   mark_launched(plan, '<worktree>', session_id='<sid-or-pr-url>')
   write_plan(plan)
   "
   ```

6. Report progress in chat after each launch (`[N/M] launched
   feat/<name> via <D.1|D.2> → <sid-or-pr-url>`).

Do not parallelize the launches — each worktree creation must register
in the in-flight state before the next one's pre-launch re-check, or
the second launch may not see the first as a conflict. The D.1 Agent
calls themselves run in parallel from the agent's own perspective,
but the dispatcher initiates them sequentially.

#### Phase E — Post-dispatch summary and async monitor hand-off

After the loop completes:

1. Re-read the plan file (it is the source of truth — Phase D wrote to
   it after every launch).
2. Print a final summary using the helper:

   ```bash
   python scripts/dispatch_plan.py summary
   ```

3. **Schedule an async wake-up to check PR state.** Do NOT enter a
   synchronous `TaskUpdate` / `gh pr view` polling loop from the
   dispatcher session — that burns prompt-cache on every tick and
   forces the operator to keep the dispatcher session alive while
   real work happens elsewhere (meta-retro #12, drift evidence
   D2 §1.1–1.3). Use `ScheduleWakeup` instead:

   ```
   ScheduleWakeup(delay_seconds=1200, reason="dispatch plan check-in")
   ```

   1200 s (20 min) is the default; bump to 3600 s (1 h) for large
   Lane C waves where D.2 sessions are unlikely to converge in the
   first window. On wake, the dispatcher:
   - re-reads `.claude/state/dispatch-plan.md`
   - runs `gh pr list --search "head:feat/<name>" --json number,state,isDraft,statusCheckRollup`
     for each launched entry
   - marks entries `done` (PR merged) or leaves them `in-flight`
     (still working — includes not-yet-merged PRs even when ready to
     merge; the `parse_plan` status vocabulary is
     `planned | conflict | in-flight | done | skipped` and an
     intermediate `ready` would be coerced back to `planned`)
   - if any entries remain `in-flight`, calls `ScheduleWakeup` again
     with the same delay
   - if all entries reached terminal state, exits with a
     `PushNotification` summary

   Why this beats `TaskUpdate` polling: each wake-up is a fresh
   context tick rather than a live session holding tokens, and the
   operator does not see a "Claude is thinking..." indicator for
   20 minutes at a time. See `.claude/interaction-patterns.md`
   monitor-coverage section for the canonical wait-for-PR-ready
   until-condition that should back the `gh pr list` call.

4. Surface to the operator:

   > "Dispatched **M** worktrees, skipped **K** due to conflicts.
   > Plan file at `.claude/state/dispatch-plan.md`. Scheduled a PR
   > state check-in for <delay>; this session is now idle. Each
   > launched worktree is self-contained."

The dispatcher session **does not continue working synchronously**
after Phase E. All subsequent work happens on `ScheduleWakeup` ticks
or via the launched sessions' own `/pr` pipelines.

### Plan file schema

`.claude/state/dispatch-plan.md` (markdown, parseable by
`scripts/dispatch_plan.py::parse_plan`):

```markdown
# Dispatch Plan

Schema: 1
Generated: 2026-04-19T01:30:00Z
Generator: session-<id>

Status legend: planned | conflict | in-flight | done | skipped

## Planned

### Worktree: feat/issue-660
- Issues: #660
- Areas: src/PPDS.Auth/
- Intent: env var auth [lane B]
- Status: planned

### Worktree: feat/audit-capture
- Issues: #101, #102
- Areas: src/PPDS.Audit/
- Intent: audit capture pipeline [lane C]
- Status: in-flight
- Launched: 2026-04-19T01:32:14Z
- Session: https://github.com/org/repo/pull/NNN
```

Allowed status values (exactly as enforced by `parse_plan`):
`planned`, `conflict`, `in-flight`, `done`, `skipped`. Recognized
keys per entry are `Issues`, `Areas`, `Intent`, `Status`, `Conflict`,
`Launched`, `Session` — any other line (e.g. `Lane:`, `PR:`) is
silently dropped on the next `write_plan` round-trip. Encode lane
and PR URL through the supported keys: suffix the intent with
`[lane B|C]`, and store the D.1 PR URL (or D.2 session ID) in the
`Session` field via `mark_launched(..., session_id=...)`. The parser
is forgiving of unrecognized lines so a human can leave ephemeral
notes, but those notes will not survive the next dispatcher write.

### Rules

1. **Plan first, dispatch second.** Phase A always writes the plan
   file before Phase C asks for confirmation. If the dispatcher session
   dies mid-flow, the next session can pick up by reading the plan.
2. **Conflict check is mandatory per entry.** Both pre-confirmation
   (Phase B) and immediately-pre-launch (Phase D step 1). The pre-
   launch re-check catches state changes during operator deliberation.
3. **Never auto-resolve a conflict.** Halt, report, let the operator
   decide. Auto-resolution risks the exact duplicate-work pattern the
   in-flight registry was built to prevent (#802).
4. **Prompt is mandatory.** Every spawn — D.1 Agent call or D.2
   launch command — MUST carry an inline prompt referencing
   `.plans/context.md` and the pipeline to run. A `TaskCreate` / bare
   `claude` without a prompt is forbidden (meta-retro #11 root
   cause: session `cd0c578e` spawned 6 worktrees with no prompt and
   forced 5 terminals of manual paste).
5. **Lane-driven path choice.** D.1 (Agent-with-isolation) is the
   default for Lane B work per `.claude/interaction-patterns.md` §1.
   D.2 (manual launch command) is reserved for Lane C or when the
   operator explicitly wants a foreground session. Record the lane
   on every plan entry.
6. **Sequential dispatch.** Do not parallelize Phase D launches. The
   in-flight registry's race-acceptance contract (PR #813) is "last
   writer wins, both visible" — fine for two siblings, bad for a
   dispatcher launching N at once.
7. **Async monitor only.** Post-dispatch PR state checks use
   `ScheduleWakeup` (Phase E). Synchronous `TaskUpdate` or `gh pr
   view` polling loops from the dispatcher session are forbidden
   (meta-retro #12).
8. **Dispatcher session exits after Phase E.** The live session ends
   with a scheduled wake-up; all subsequent work happens on ticks or
   in the launched worktrees. Do not hold the dispatcher session
   open waiting on PRs.

## Error Handling

| Error | Recovery |
|-------|----------|
| Permission denied on gh command | Run `gh auth login` to re-authenticate |
| Label doesn't exist | Check `docs/BACKLOG.md` for current taxonomy, create if needed |
| Milestone doesn't exist | List milestones with `gh api repos/{owner}/{repo}/milestones`, create if needed |
| `dispatch-plan.md` parse error | Plan was hand-edited into an invalid state. Restore from `git diff` or delete the file and re-run Phase A. |
| `inflight-check.py` returns rc=2 | A required arg is missing — the dispatcher constructed a bad CLI line. Halt; do not silently treat as "no conflict". |
| `mark_launched` raises `KeyError` | The dispatcher passed a worktree name that is not in the plan file (typo, or the file was edited between Phase A and Phase D). Halt and resync. |

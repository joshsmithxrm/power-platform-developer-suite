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

For each entry whose status is `planned` (in plan-file order):

1. Re-check conflicts immediately before launch — the in-flight state
   may have changed between Phase B and now (e.g. a sibling session
   completed). If a new conflict appears, mark the entry `skipped` with
   the new conflict detail and continue to the next entry.
2. Invoke the `/start` skill for that worktree. The Skill tool is the
   inter-skill calling mechanism; pass the entry's data inline so
   `/start` can extract name + issues using its existing parser:

   ```
   /start <intent>; issues <#N> <#M>; suggested branch feat/<name>
   ```

   `/start` already handles: worktree creation, workflow-state init,
   in-flight registration via `inflight-register.py`, and the inline
   prompt handoff to a new claude session. **Do not duplicate any of
   that here.** This subverb's job is to invoke `/start` once per
   entry, not to re-implement what it does.

   If the inter-skill invocation mechanism is unavailable in the
   current runtime (e.g. nested `Skill` tool calls are restricted),
   fall back to printing the exact `/start` prompt the operator should
   paste into a new session. The plan file already records intent so
   the manual fallback is recoverable.

3. Capture the session ID returned by `/start` (or the registered
   session from `.claude/state/in-flight-issues.json` matching the
   branch name).
4. Mark the entry launched in the plan file:

   ```bash
   python -c "
   import sys
   sys.path.insert(0, 'scripts')
   from dispatch_plan import load_plan, mark_launched, write_plan
   plan = load_plan()
   mark_launched(plan, '<worktree>', session_id='<sid>')
   write_plan(plan)
   "
   ```

5. Report progress in chat after each launch (`[N/M] launched
   feat/<name> as session <sid>`).

Do not parallelize the launches — each worktree creation must register
in the in-flight state before the next one's pre-launch re-check, or
the second launch may not see the first as a conflict.

#### Phase E — Post-dispatch summary

After the loop completes:

1. Re-read the plan file (it is the source of truth — Phase D wrote to
   it after every launch).
2. Print a final summary using the helper:

   ```bash
   python scripts/dispatch_plan.py summary
   ```

3. Surface to the operator:

   > "Dispatched **M** worktrees, skipped **K** due to conflicts.
   > Plan file at `.claude/state/dispatch-plan.md`. Each launched
   > session is now self-contained — this dispatcher session's job
   > is done."

The dispatcher session **does not continue working** after Phase E.
The launched sessions handle their own lifecycle (each one runs the
existing `/gates` -> `/verify` -> `/pr` pipeline per `/start`'s routing).

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
- Intent: env var auth
- Status: planned

### Worktree: feat/audit-capture
- Issues: #101, #102
- Areas: src/PPDS.Audit/
- Intent: audit capture pipeline
- Status: in-flight
- Launched: 2026-04-19T01:32:14Z
- Session: a1b2c3d4
```

Allowed status values: `planned`, `conflict`, `in-flight`, `done`,
`skipped`. The parser is forgiving of human edits (unknown lines
inside an entry are ignored) so the operator can leave inline notes
without breaking subsequent dispatch waves.

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
4. **One `/start` invocation per entry.** This subverb does not create
   worktrees, init workflow state, or write inline prompts directly —
   `/start` owns all of that. Bypassing it would re-introduce the
   duplication that caused B5 in the first place.
5. **Sequential dispatch.** Do not parallelize Phase D launches. The
   in-flight registry's race-acceptance contract (PR #813) is "last
   writer wins, both visible" — fine for two siblings, bad for a
   dispatcher launching N at once.
6. **Dispatcher session exits after Phase E.** Do not continue work in
   the dispatcher session after the launches complete. Each launched
   session is autonomous.

## Error Handling

| Error | Recovery |
|-------|----------|
| Permission denied on gh command | Run `gh auth login` to re-authenticate |
| Label doesn't exist | Check `docs/BACKLOG.md` for current taxonomy, create if needed |
| Milestone doesn't exist | List milestones with `gh api repos/{owner}/{repo}/milestones`, create if needed |
| `dispatch-plan.md` parse error | Plan was hand-edited into an invalid state. Restore from `git diff` or delete the file and re-run Phase A. |
| `inflight-check.py` returns rc=2 | A required arg is missing — the dispatcher constructed a bad CLI line. Halt; do not silently treat as "no conflict". |
| `mark_launched` raises `KeyError` | The dispatcher passed a worktree name that is not in the plan file (typo, or the file was edited between Phase A and Phase D). Halt and resync. |

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
- "Dispatch parallel worktrees", "kick off the planned waves"
- After completing work that reveals follow-up issues

## Reference

Read `docs/BACKLOG.md` at the start of every backlog operation - it is the source of truth for labels, milestones, and rules.
Read REFERENCE.md §1 "Label taxonomy" before classifying any issue.

## Process

### 1. Parse Arguments

- `/backlog create <description>` - create a new issue
- `/backlog triage` - triage untriaged inbox
- `/backlog review` - review backlog for promotion
- `/backlog validate` - verify open issues are still valid
- `/backlog dispatch` - plan and launch parallel worktrees
- `/backlog` (no args) - show backlog summary

### 2. Read Rules

```bash
cat docs/BACKLOG.md
```

### 3. Pre-flight: In-Flight Conflict Check

Before `gh issue create`, ALWAYS check whether a sibling session is already working on the same area: <!-- enforcement: T2 hook:inflight-check -->

```bash
python scripts/inflight-check.py --area <best-guess-area>
```

If exit `1`, surface the conflict to the operator and ASK before filing.

The same check MUST be repeated before `gh issue close`: if a sibling session has open work referencing the issue, surface the related branch so the closer does not misattribute the fix to the wrong PR. <!-- enforcement: T2 hook:inflight-check -->

### 4. Execute Operation

#### Create Issue

1. Determine `type:` label (bug/enhancement/docs/performance/refactor)
2. Determine `area:` label (auth/cli/data/extension/plugins/tui)
3. Ask user for milestone or default to none
4. Check if an `epic:` label applies
5. Ask user for `priority:` label if not obvious
6. Create:

```bash
gh issue create --title "<title>" --body "<body>" --label "type:<type>,area:<area>" --milestone "<milestone>"
```

If no milestone, add `status:backlog`. Every issue must have at minimum one `type:` + one `area:` + either a milestone or `status:` label.

#### Triage Inbox

```bash
gh issue list --state open --limit 200 --json number,title,labels,milestone --jq '[.[] | select(.milestone == null) | select(.labels | map(.name) | any(startswith("status:")) | not)]'
```

Read REFERENCE.md §2 "Mandatory verification" for the per-issue checklist before presenting any issue to the user. Read REFERENCE.md §3 "Triage decision rules" for the categorization heuristic.

#### Review Backlog

Surface ready-to-promote issues from `status:backlog` based on completion criteria + dependency satisfaction. See REFERENCE.md §4 "Promotion rules".

#### Validate Open Issues

For each open issue, verify the symptom still reproduces and the area label is still accurate. Close stale duplicates. See REFERENCE.md §5 "Validation checklist".

### 5. Subverb: dispatch

Dispatch plans and launches parallel worktrees from triage state. Read REFERENCE.md §6 "Dispatch heuristics" before sizing waves. Read REFERENCE.md §7 "Inline-prompt examples" for the dispatch prompt format.

Hard rules (MUST in SKILL.md, not REFERENCE.md): <!-- enforcement: T3 -->

- Inline prompt MUST carry the full context for the dispatched worktree - spawning without an inline prompt is what forced retrospective B3. <!-- enforcement: T3 -->
- Launch command MUST carry an inline prompt referencing the issue and the planned scope. <!-- enforcement: T3 -->
- Dispatch wave size: <=3 worktrees by default; ask before exceeding (CLAUDE.md TaskCreate cap is enforced by `taskcreate-cap.py`).
- Dispatch entries are written to `.claude/state/in-flight-issues.json` via `python scripts/inflight-register.py`.

```bash
python scripts/inflight-register.py --branch <branch> --area <area> --intent <intent>
```

### 6. Workflow State

After any operation that creates or closes an issue:

```bash
python scripts/workflow-state.py set backlog.last_operation now
```

## References

- `.claude/skills/backlog/REFERENCE.md` - taxonomy, dispatch heuristics, conflict-resolution examples, meta-retro references, NEVER list. <!-- enforcement: T3 -->
- `docs/BACKLOG.md` - canonical labels and milestones.
- `scripts/inflight-check.py`, `scripts/inflight-register.py` - sibling-conflict tooling.
- `.claude/interaction-patterns.md` §1 (lanes), §3 (DO NOW / DEFER / DROP).

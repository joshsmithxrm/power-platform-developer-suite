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
- `/backlog` (no args) - show backlog summary

### 2. Read Rules

```bash
cat docs/BACKLOG.md
```

### 3. Execute Operation

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

## References

- `.claude/skills/backlog/REFERENCE.md` - taxonomy, conflict-resolution examples, and the never-do list.
- `docs/BACKLOG.md` - canonical labels and milestones.

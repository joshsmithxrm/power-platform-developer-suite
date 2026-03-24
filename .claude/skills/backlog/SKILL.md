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

Read `docs/BACKLOG.md` at the start of every backlog operation. It is the source of truth for labels, milestones, and rules.

## Process

### 1. Parse Arguments

Check `$ARGUMENTS` for the operation:

- `/backlog create <description>` — create a new issue
- `/backlog triage` — triage untriaged inbox
- `/backlog review` — review backlog for promotion
- `/backlog stale` — find stale issues
- `/backlog` (no args) — show backlog summary

### 2. Read Rules

```bash
# Always read the reference doc first
cat docs/BACKLOG.md
```

### 3. Execute Operation

#### Create Issue

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
gh issue list --state open --no-milestone --json number,title,labels --jq '[.[] | select(.labels | map(.name) | any(startswith("status:")) | not)]'
```

For each issue, present to user with context and get a decision:
- **Milestone it** — assign to v1.0, v1.1, or v2.0
- **Backlog it** — add `status:backlog` (optionally with a `priority:` label)
- **Needs design** — add `status:needs-design`
- **Close it** — close with a reason

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
gh issue list --state open --label "status:backlog" --label "priority:high" --json number,title,labels
gh issue list --state open --label "status:backlog" --label "priority:critical" --json number,title,labels
```

Present each to user: promote to milestone, keep in backlog, or re-prioritize.

#### Staleness Check

Find issues with no activity in 6+ months:

```bash
gh issue list --state open --json number,title,updatedAt,milestone,labels --jq '[.[] | select(.updatedAt < "YYYY-MM-DD")] | sort_by(.updatedAt) | .[] | "\(.number) | \(.updatedAt[0:10]) | \(.title)"'
```

Replace `YYYY-MM-DD` with the date 6 months ago.

For each stale issue: close with comment, re-prioritize, or leave open with justification.

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
gh issue list --state open --no-milestone --json number,labels --jq '[.[] | select(.labels | map(.name) | any(startswith("status:")) | not)] | length'
```

### 4. GitHub Account Check

Before any `gh` write operation, verify the correct account is active:

```bash
gh auth status
```

This repo is owned by `joshsmithxrm`. If the wrong account is active, switch first:

```bash
gh auth switch --user joshsmithxrm
```

## Error Handling

| Error | Recovery |
|-------|----------|
| Permission denied on gh command | Check `gh auth status`, switch to `joshsmithxrm` |
| Label doesn't exist | Check `docs/BACKLOG.md` for current taxonomy, create if needed |
| Milestone doesn't exist | List milestones with `gh api repos/{owner}/{repo}/milestones`, create if needed |

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
- `/backlog validate` — verify open issues are still valid
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

## Error Handling

| Error | Recovery |
|-------|----------|
| Permission denied on gh command | Run `gh auth login` to re-authenticate |
| Label doesn't exist | Check `docs/BACKLOG.md` for current taxonomy, create if needed |
| Milestone doesn't exist | List milestones with `gh api repos/{owner}/{repo}/milestones`, create if needed |

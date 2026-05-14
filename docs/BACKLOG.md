# PPDS Backlog Management

Rules for issue tracking, labels, milestones, and triage.

## Labels

Canonical state lives in GitHub. To inspect:

    gh label list --json name,description,color
    gh api repos/:owner/:repo/milestones
    gh issue list --label epic:agents-dashboard

Labels are added/removed via the GitHub UI or `gh label create`. Do not duplicate
them here — past audits found drift between this doc and the live state.

## Milestones

| Milestone | Meaning |
|-----------|---------|
| `v1.0` | Must ship for v1 release |
| `v1.1` | Committed post-v1 near-term work |
| `v2.0` | Enterprise Data Migration Platform + major architecture |

### Triage states

| Milestone | Status Label | Meaning |
|-----------|-------------|---------|
| Any milestone | (none needed) | Committed to that release |
| None | `status:backlog` | Triaged, deliberately parked |
| None | `status:needs-evaluation` | Blocked on investigation |
| None | (no status label) | **Untriaged inbox** — needs a decision |

## Rules

1. **Every issue gets:** one `type:` label + one `area:` label + either a milestone or `status:` label
2. **Inbox zero:** No milestone + no `status:` label = untriaged. The inbox should be empty.
3. **Monthly review:** Check inbox is empty. Review `status:backlog` + `priority:high` for promotion to a milestone.
4. **Epics vs milestones:** Epics track initiatives across areas. Milestones track releases. An issue can have both.
5. **Validity over staleness:** There is no time-based staleness rule. Issues are valid or invalid based on whether their premise still matches the codebase, not their age. Validate during triage.
6. **Pipeline-created issues** land in the inbox unlabeled. The pipeline retro stage files issues for findings but cannot reliably assign `type:` or `area:`. These issues are triaged during the next `/backlog triage` pass.

## Useful Queries

```bash
# Untriaged inbox (should be empty)
gh issue list --state open --limit 200 --json number,labels,milestone,title --jq '[.[] | select(.milestone == null) | select(.labels | map(.name) | any(startswith("status:")) | not)]'

# High-priority backlog candidates for promotion
gh issue list --search 'is:open label:"status:backlog" (label:"priority:high" OR label:"priority:critical")' --json number,title

# All v1.0 blockers
gh issue list --state open --milestone "v1.0" --json number,title,labels

# Issues missing type: label
gh issue list --state open --limit 100 --json number,labels --jq '[.[] | select(.labels | map(.name) | any(startswith("type:")) | not)] | .[].number'

# Issues missing area: label
gh issue list --state open --limit 100 --json number,labels --jq '[.[] | select(.labels | map(.name) | any(startswith("area:")) | not)] | .[].number'
```

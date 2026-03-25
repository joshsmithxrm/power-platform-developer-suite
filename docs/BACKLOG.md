# PPDS Backlog Management

Rules for issue tracking, labels, milestones, and triage.

## Label Taxonomy (23 labels)

### `type:` — What kind of work

| Label | Color | Description |
|-------|-------|-------------|
| `type:bug` | #d73a4a | Something isn't working |
| `type:enhancement` | #a2eeef | New feature or request |
| `type:docs` | #0075ca | Documentation improvements or additions |
| `type:performance` | #fbca04 | Performance improvement |
| `type:refactor` | #c5def5 | Code restructuring without behavior change |

### `area:` — Which subsystem

| Label | Color | Description |
|-------|-------|-------------|
| `area:auth` | #0052CC | Authentication profiles |
| `area:cli` | #0052CC | CLI commands and UX |
| `area:data` | #0052CC | Data import/export/migration |
| `area:extension` | #7057FF | VS Code extension |
| `area:plugins` | #0052CC | Plugin registration, deployment |
| `area:tui` | #0052CC | Terminal UI (interactive mode) |

### `epic:` — Cross-cutting initiatives

| Label | Color | Description |
|-------|-------|-------------|
| `epic:data-migration` | #7057ff | Enterprise Data Migration Platform epic |
| `epic:plugin-registration` | #7B42BC | Full plugin registration CLI support epic |
| `epic:testing` | #1D76DB | Integration and live testing infrastructure |

Epics track initiatives that span areas. They close when the initiative is done. Areas persist indefinitely.

### `priority:` — Urgency

| Label | Color | Description |
|-------|-------|-------------|
| `priority:critical` | #b60205 | Fix immediately |
| `priority:high` | #d93f0b | High priority |
| `priority:medium` | #fbca04 | Medium priority |
| `priority:low` | #0e8a16 | Low priority |

### `status:` — Triage state (for unmilestoned issues)

| Label | Color | Description |
|-------|-------|-------------|
| `status:backlog` | #d4c5f9 | Triaged, deliberately parked — pull forward when ready |
| `status:needs-evaluation` | #d4c5f9 | Needs investigation or evaluation before work can begin |

### Other

| Label | Color | Description |
|-------|-------|-------------|
| `good first issue` | #7057ff | Good for newcomers |
| `dependencies` | #0366d6 | Dependabot: dependency updates |
| `javascript` | #168700 | Dependabot: JavaScript dependency updates |

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

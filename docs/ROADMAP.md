# PPDS Roadmap - Issue Triage & Project Management

**The [PPDS Roadmap](https://github.com/users/joshsmithxrm/projects/3) is a public GitHub Project that tracks work across the PPDS SDK repository.**

This document provides complete guidelines for triaging issues, using project fields, and managing the roadmap effectively.

---

## Quick Reference

| What | Where | Purpose |
|------|-------|---------|
| **All issues** | [GitHub Issues](https://github.com/joshsmithxrm/ppds-sdk/issues) | Discussion, reproduction steps, implementation details |
| **Roadmap** | [PPDS Roadmap Project](https://github.com/users/joshsmithxrm/projects/3) | Organized view with Type, Priority, Size, Status, Target |
| **Triage command** | `/triage` | Systematic issue categorization |
| **Feature roadmap** | [FEATURE_ROADMAP.md](FEATURE_ROADMAP.md) | Phase planning, extension parity epic |
| **This doc** | `docs/ROADMAP.md` | Detailed triage guidelines |

---

## Project Fields

### Type

**Purpose:** Categorize the nature of the work

| Value | When to Use | Examples |
|-------|-------------|----------|
| `feature` | New functionality | Add `/triage` command, support for CSV export |
| `bug` | Something broken | Import fails on M2M relationships |
| `chore` | Maintenance, refactoring | Update dependencies, clean up test structure |
| `docs` | Documentation only | Update README, add ADR, improve XML comments |
| `refactor` | Code improvement without behavior change | Extract service layer, rename for clarity |

**Guideline:** Use the issue title prefix (`feat:`, `bug:`, `chore:`, `docs:`, `refactor:`) as a hint, but Type field is canonical.

---

### Priority

**Purpose:** Urgency and importance of the work

| Value | When to Use | Examples | Response Time |
|-------|-------------|----------|---------------|
| `P0-Critical` | Production broken, security vulnerability, data loss | Users can't authenticate, connection pool deadlock | Immediate |
| `P1-High` | Major feature blocker, significant bug | Can't import data with lookups, CLI crashes on Windows | Within days |
| `P2-Medium` | Important but not blocking | Performance improvement, UX enhancement | Within weeks |
| `P3-Low` | Nice to have, minor improvement | Add color to CLI output, refactor internal helper | When time allows |

**Criteria for P0:**
- Production users are blocked
- Data loss or corruption risk
- Security vulnerability
- No workaround available

**Criteria for P1:**
- Core functionality broken
- Significant performance degradation
- Major feature incomplete
- Workaround exists but difficult

**Criteria for P2:**
- Enhancement to existing feature
- Moderate performance improvement
- Quality of life improvements
- Non-critical bugs with easy workarounds

**Criteria for P3:**
- Stretch goals
- Internal refactoring
- Future-proofing
- Documentation improvements

---

### Size

**Purpose:** Effort estimation for planning

| Value | Effort | When to Use | Examples |
|-------|--------|-------------|----------|
| `XS` | < 4 hours | Typo fix, add single parameter, small doc update | Fix README typo, add --verbose flag |
| `S` | < 1 day | Single file change, simple feature, straightforward bug | Add new CLI option, fix validation bug |
| `M` | < 3 days | Multiple files, moderate complexity | Implement new command group, refactor service |
| `L` | < 1 week | Significant feature, cross-cutting change | Add full CRUD for new entity, major perf optimization |
| `XL` | > 1 week | Epic-level work, architectural change | Daemon architecture, complete migration platform |

**Estimation Guidelines:**
- Include time for tests, documentation, PR review
- If unsure between two sizes, choose the larger
- XL should usually be broken down into smaller issues
- Use past similar issues as reference

**Examples by file count:**
- XS: 1-2 files
- S: 2-5 files
- M: 5-10 files
- L: 10-20 files
- XL: 20+ files or architectural change

---

### Status

**Purpose:** Current state of the work

| Value | Meaning | Who Updates |
|-------|---------|-------------|
| `Todo` | Not started, ready to work on | Set during triage |
| `In Progress` | Actively being worked on | Set when you start work (and assign yourself!) |
| `Done` | Completed and merged | GitHub automation when issue closed |

**Workflow:**
1. Issue created → Status = *(empty)*
2. `/triage` → Status = `Todo`
3. Start work → Status = `In Progress` + Assign yourself
4. PR merged, issue closed → Status = `Done` (automatic)

**Best Practice:** Only ONE issue should be `In Progress` per person at a time. Finish before starting new work.

---

### Target

**Purpose:** Sequencing and planning without rigid dates

**This field uses relative values, not specific dates!**

| Value | Meaning | When to Use |
|-------|---------|-------------|
| `This Week` | Current focus | What you're working on right now |
| `Next` | Prioritized for soon | What comes after current work |
| `Q1 2026`, `Q2 2026` | Rough timeframe | General planning, not committed |
| `CLI v1.0.0` | Before a release | Release blockers |
| `Blocked` | Waiting on dependency | Can't start until something else is done |
| *(empty)* | Backlog | Not prioritized yet |

**Why not specific dates?**
- PPDS moves fast - specific dates become stale quickly
- Relative values are easier to maintain
- Clear sequencing without false precision

**How to use Target + Priority together:**
- P0 + `This Week` = Drop everything
- P1 + `Next` = High priority, coming soon
- P2 + `Q1 2026` = Important, planned for soon-ish
- P3 + *(empty)* = Nice to have, no specific plan

**Updating Target:**
- Move issues from `Next` → `This Week` as you complete work
- Adjust quarterly targets as priorities shift
- Use `Blocked` when dependencies become clear

---

### Parent Issue

**Purpose:** Link child issues to epic/parent issues

**Built-in GitHub Projects field** - creates hierarchy.

**When to use:**
- All issues part of an epic should link to the epic issue
- Subtasks should link to the main issue
- GitHub automatically tracks progress (5 of 12 children completed)

**Example:**
```
Epic #210: Enterprise Data Migration Platform
├── #211: Interactive TUI for data migration
├── #212: Dependency graph visualization
├── #213: PPDS Migration Schema Format
└── #214: CMT schema convert command
```

**How to link:**
1. Via GitHub UI: Edit Parent Issue field in project
2. Via `/triage`: Include Parent Issue in triage data

---

## Label Strategy

### Philosophy

**Labels ≠ Project Fields**

- **Labels** = Grouping, filtering in GitHub search
- **Project Fields** = Triage data, project management

### Label Taxonomy

#### Epics (3)
Group issues by major initiative:

- `epic:cli-daemon` - CLI daemon architecture for extension integration
- `epic:testing` - Integration and live testing infrastructure
- `epic:data-migration` - Enterprise data migration platform

**When to apply:** Issue is part of this major initiative

---

#### Phases (5)
Group issues by sequential delivery phases:

- `phase:1-core` - Solutions, Import Jobs, Env Vars, Users
- `phase:2-connections` - Flows, ConnRefs, Connections, Deployment Settings
- `phase:3-traces` - Plugin Traces
- `phase:4-webresources` - Web Resources
- `phase:5-migration` - Extension CLI Migration

**When to apply:** Issue is part of this delivery phase

**See also:** [FEATURE_ROADMAP.md](FEATURE_ROADMAP.md) for detailed phase planning

---

#### Areas (7)
Group issues by codebase component:

- `area:plugins` - Plugin registration, deployment (`PPDS.Plugins`, deploy commands)
- `area:data` - Data import/export/migration (`PPDS.Migration`, data commands)
- `area:auth` - Authentication profiles (`PPDS.Auth`, auth commands)
- `area:cli` - CLI commands and UX (`PPDS.Cli`, command infrastructure)
- `area:tui` - Terminal UI (interactive mode, `ppds interactive`)
- `area:pooling` - Connection pool (`PPDS.Dataverse` pool classes)
- `area:daemon` - Daemon architecture (serve command, daemon mode)

**When to apply:** Issue primarily affects this component

**Examples:**
- Issue about `ppds data export` → `area:data`
- Issue about connection pool performance → `area:pooling`
- Issue about `ppds auth create` → `area:auth`
- Issue about TUI table navigation → `area:tui`

---

#### Special (3)
Additional context labels:

- `foundation` - Infrastructure: entities, services, DI registration
- `blocked` - Waiting on another issue/PR/dependency
- `performance` - Performance optimization work

**When to apply:**
- `foundation`: Adding early-bound entities, creating services, DI plumbing
- `blocked`: Issue literally cannot start until dependency resolved (use `Blocked` in Target field too)
- `performance`: Primary goal is performance improvement

---

#### Type Hints (3) - OPTIONAL
Quick visual identification in GitHub search:

- `bug` - Duplicates Type field, but useful for `is:issue label:bug is:open` search
- `enhancement` - Duplicates Type field, useful for filtering
- `documentation` - Duplicates Type field

**When to apply:** Optional! Type field is canonical, these are for convenience.

---

### Label Usage Rules

**✅ DO:**
- Apply epic/phase/area labels during triage
- Use area labels for component filtering
- Add `blocked` when dependencies are clear
- Use `performance` for optimization work

**❌ DON'T:**
- Use labels for Priority (use Priority field instead)
- Use labels for Size (use Size field instead)
- Use labels for Status (use Status field instead)
- Apply every label - be selective!

---

## Triage Workflow

### The `/triage` Command

**Purpose:** Systematically categorize issues with project fields and labels

**Usage:**
```bash
/triage                        # Triage all untriaged open issues (up to 50)
/triage --state all            # Include closed issues
/triage --limit 20             # Only first 20
/triage 224 223 222            # Specific issues
```

**What it does:**
1. Fetches all issues from ppds-sdk
2. Cross-references with PPDS Roadmap project
3. Identifies issues missing any field (Type, Priority, Size, Status, Target)
4. Shows batch summary table
5. You edit a markdown table to assign values
6. Validates input
7. Adds issues to project (if needed) + updates fields
8. Shows results

**Process flow:**
```
Run /triage
  ↓
See summary: "15 issues need triage"
  ↓
Edit table with Type, Priority, Size, Status, Target values
  ↓
Paste back
  ↓
Confirm changes
  ↓
Issues updated in project!
```

### Triage Best Practices

**When triaging:**
1. Read the full issue (don't just skim the title)
2. Check for duplicates first
3. Ask clarifying questions if ambiguous
4. Be conservative with P0 (only if truly critical)
5. Break down XL issues into smaller pieces
6. Link to parent issue if part of epic
7. Suggest area labels based on content

**Batch triage strategy:**
1. Start with oldest untriaged issues (--state all)
2. Triage in batches of 20-50
3. Group similar issues together
4. Set Parent Issue for epic children
5. Use `Blocked` target if dependencies exist

**Maintaining quality:**
- Update triage as you learn more (priorities change!)
- Move Status to In Progress when you start (and assign yourself)
- Update Target as work progresses
- Opportunistically improve old issues when you touch them

---

## Project Views

**Create these views in the GitHub Projects UI:**

### View 1: "Next Up" - Your Priority Queue

**Purpose:** See what to work on next

**Configuration:**
```
Filter: Status = Todo AND (Priority = P0-Critical OR Priority = P1-High)
Sort: Priority (ascending), Size (ascending)
Layout: Table
Columns: Title, Type, Priority, Size, Target, Labels
```

**How to use:** This is your work queue. Pick XS/S issues first for quick wins, or tackle important L issues.

---

### View 2: "My Work" - Personal Kanban

**Purpose:** Track what you're assigned to

**Configuration:**
```
Filter: Assignee = @me AND Status != Done
Sort: Status (In Progress first), Priority
Group by: Status
Layout: Board
```

**How to use:** Drag issues from Todo → In Progress when you start. Ideally only 1 item in progress at a time.

---

### View 3: "By Epic" - Epic Progress

**Purpose:** See progress on major initiatives

**Configuration:**
```
Filter: Status != Done
Group by: Labels (epic:*)
Sort: Priority, Size
Layout: Table
```

**How to use:** View all active work grouped by epic. See which epics need attention.

---

### View 4: "Quick Wins" - Low-Hanging Fruit

**Purpose:** Find quick, high-value tasks

**Configuration:**
```
Filter: Status = Todo AND (Size = XS OR Size = S) AND (Priority = P1-High OR Priority = P2-Medium)
Sort: Priority, Size
Layout: Table
Columns: Title, Type, Priority, Size, Target, Area
```

**How to use:** When you have 30 minutes, grab something from here. High impact, low effort.

---

## Best Practices

### For Issue Authors

**When creating an issue:**
- Use the issue template (feature request or bug report)
- Include clear acceptance criteria
- Link to related issues/PRs
- Don't assign Priority/Size yourself (maintainers will triage)

**After creation:**
- Maintainer will triage and add to project
- You'll see it appear in the roadmap
- Comment if priorities/urgency changes

---

### For Maintainers

**Daily workflow:**
1. Check "My Work" view - what am I working on?
2. Check "Next Up" - what's the next priority?
3. Update Status when starting work (and assign yourself)
4. Move Target from `Next` → `This Week` as you progress

**Weekly workflow:**
1. Run `/triage --limit 20` for new issues
2. Review "Next Up" - does Priority still make sense?
3. Check "By Epic" - are epics balanced?
4. Update Target values as plans evolve

**When starting work:**
1. Move Status to In Progress
2. Assign yourself
3. Update Target to `This Week` if not already
4. Check for dependencies (Parent Issue, Blocked)

**When finishing work:**
1. Close the issue (Status → Done automatic)
2. Update related issues if unblocked
3. Pick next item from "Next Up"

---

## Size Estimation Reference

### Real Examples from PPDS

**XS (< 4 hours):**
- Fix typo in CLAUDE.md
- Add `--verbose` flag to existing command
- Update single ADR with clarification

**S (< 1 day):**
- Add `ppds auth delete` command (reuses existing patterns)
- Fix bug in CSV parser (single file, clear fix)
- Create new issue template

**M (< 3 days):**
- Implement `/triage` command (new command group, full workflow)
- Add early-bound entities for 3 tables
- Refactor authentication service layer

**L (< 1 week):**
- Add `ppds solutions` command group (7 subcommands, service, tests)
- Implement bulk operation fallback logic
- Major performance optimization with benchmarking

**XL (> 1 week):**
- Enterprise data migration platform (epic with 10+ child issues)
- Daemon architecture for extension (major architectural change)
- Complete authentication overhaul

---

## Priority Decision Tree

```
Is production broken or security issue?
├─ YES → P0-Critical
└─ NO → Is core functionality blocked?
    ├─ YES → P1-High
    └─ NO → Is it an important enhancement?
        ├─ YES → P2-Medium
        └─ NO → P3-Low
```

---

## Target Field Decision Tree

```
Am I working on this right now?
├─ YES → This Week
└─ NO → Is it my next task after current work?
    ├─ YES → Next
    └─ NO → Is there a specific release it blocks?
        ├─ YES → CLI v1.0.0 (or relevant version)
        └─ NO → Is it blocked on something?
            ├─ YES → Blocked
            └─ NO → Is it planned for a quarter?
                ├─ YES → Q1 2026, Q2 2026
                └─ NO → (leave empty - backlog)
```

---

## Metadata Cheat Sheet

**Complete metadata for a triaged issue:**

```markdown
Issue #137: Add ppds solutions command for solution management

GitHub Metadata:
- State: Open
- Assignees: @joshsmithxrm (when Status=In Progress)
- Created: 2026-01-04
- Updated: 2026-01-05

Project Fields:
- Type: feature
- Priority: P1-High
- Size: L
- Status: In Progress
- Target: Next
- Parent Issue: #210 (epic: Enterprise Data Migration Platform)

Labels:
- phase:1-core
- area:cli
- enhancement
```

---

## Troubleshooting

### "I don't know what Size to pick"

- Compare to similar past issues
- If between two sizes, pick the larger
- Include time for tests and docs
- Ask for help in the issue comments

### "Priority keeps changing"

That's normal! Priorities shift based on:
- User feedback
- Discovered bugs
- New dependencies
- Release planning

Update Priority field when priorities change.

### "Everything is P0!"

Real P0 criteria:
- Production is broken RIGHT NOW
- Users are completely blocked
- Data loss risk
- Security vulnerability

If it doesn't meet these, it's P1 or P2.

### "Target dates keep slipping"

Use relative values instead of dates:
- ❌ `Target: 2026-01-15` (becomes stale)
- ✅ `Target: Next` (stays relevant)

### "Should I use a label or a project field?"

**Labels:** Grouping (epic, phase, area)
**Fields:** Triage data (Type, Priority, Size, Status, Target)

When in doubt, field > label.

---

## Related Documentation

- **Feature roadmap:** [FEATURE_ROADMAP.md](FEATURE_ROADMAP.md) - Phase planning, extension parity epic
- **Issue templates:** `.github/ISSUE_TEMPLATE/`
- **Triage command:** `.claude/commands/triage.md`
- **CLAUDE.md:** Quick reference table
- **Workspace CLAUDE.md:** Cross-repo roadmap link

---

## Questions?

- **For triage questions:** Ask in issue comments
- **For process improvements:** Open an issue with `docs` label
- **For urgent clarification:** Ping maintainer directly

---

**Last updated:** 2026-01-06 (coincides with triage system implementation)

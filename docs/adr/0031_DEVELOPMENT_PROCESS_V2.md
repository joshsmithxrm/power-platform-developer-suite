# ADR-0031: Development Process V2

## Status
Accepted (January 2026)

## Context
The session orchestration architecture (ADR-0030) provides the technical infrastructure for parallel work. This ADR documents the **process** decisions that ensure effective human-AI collaboration within that infrastructure.

Key problems identified:
1. **Alignment drift**: Workers understand differently than intended, discovered at PR review (expensive)
2. **Bot comment overload**: 30+ comments per PR, 2-3 are real issues
3. **Unclear readiness**: Issues get worked on before they're properly designed
4. **Flow state broken**: Too much context-switching between monitoring, reviewing, designing
5. **Inconsistent patterns**: Same feedback given on multiple PRs

## Decision

### 1. Alignment Verification at Planning

Workers must produce plans with explicit structure that proves understanding:

```markdown
## My Understanding
[Restate issue in own words]

## Patterns I'll Follow
[Cite specific ADRs, docs/patterns/, CLAUDE.md rules]

## Approach
[Implementation steps]

## What I'm NOT Doing
[Explicit scope boundaries]

## Questions Before Proceeding
[If any]
```

**Why**: Restating proves understanding. Citations prove context was found. Scope boundaries prevent creep. Questions catch confusion before implementation.

**Human reviews understanding, not plan quality.** The question is: "Did Claude understand what I meant?"

### 2. Pattern Examples in `docs/patterns/`

Canonical code patterns extracted from the codebase:

| Pattern | File | Demonstrates |
|---------|------|--------------|
| Bulk operations | `bulk-operations.cs` | BulkOperationExecutor, parallelism |
| Service layer | `service-pattern.cs` | IProgressReporter, PpdsException |
| TUI panel | `tui-panel-pattern.cs` | Terminal.Gui layout, async updates |
| CLI command | `cli-command-pattern.cs` | Output routing, exit codes |
| Connection pool | `connection-pool-pattern.cs` | Pool acquisition in loops |

**Why**: Rules tell WHAT. Examples tell HOW. Workers cite patterns in plans.

### 3. The Zen Model (Gates Only)

Monitor entry and exit gates, not the autonomous zone:

```
                 ┌─────────────────────────────────┐
                 │     AUTONOMOUS ZONE             │
                 │     (Workers execute)           │
────────────────>│ ENTRY GATE                      │
  Plan approved  │ (alignment verification)        │
                 │                       EXIT GATE │<─────────────
                 │                      (PR review)│ Quality check
                 │     ESCAPE HATCH ──────────────│───> STUCK
                 │     (worker signals)           │
                 └─────────────────────────────────┘
```

**If entry gate is strong**: Workers start aligned → less rework
**If exit gate is strong**: Nothing bad escapes → safe to merge
**If escape hatch is reliable**: You're notified when needed → no surprises

**You don't need to watch the middle.**

### 4. Issue Lifecycle with Labels

```
IDEA → DRAFTED → DESIGNED → READY → IN PROGRESS → PR READY → MERGED
```

| Label | Meaning | Who Adds |
|-------|---------|----------|
| `needs-design` | Needs architecture/UI/API design | You or Triage |
| `designed` | Design complete, attached to issue | You |
| `ready` | Validated, can be assigned to worker | Triage Agent |
| `in-progress` | Worker actively working | Orchestrator |
| `pr-ready` | PR created, awaiting review | Worker |
| `blocked` | Cannot proceed, needs external input | Worker/Triage |

### 5. Full Orchestrator Lifecycle

Orchestrator handles post-approval mechanics:

```
Phase 1: Spawn → Planning → Plan Review
Phase 2: Working → PR Ready → Code Review
Phase 3: Approved → Rebase → CI → Merge → Complete
```

**Key principle**: You review CODE. Orchestrator handles MECHANICS (rebase, CI wait, merge).

### 6. Session Types

| Session Type | Purpose | Your Interaction |
|--------------|---------|------------------|
| Design Studio | Brainstorming, architecture, UI | Active collaboration |
| Orchestrator | Project management, status | Dashboard-style |
| Worker | Executes specific issue | None during execution |

### 7. Review Agent (Planned)

Filter bot comments, categorize by severity:

| Category | Meaning | Your Action |
|----------|---------|-------------|
| MUST FIX | Security, bugs | Fix before merge |
| SHOULD FIX | Pattern violations | Fix or justify |
| CONSIDER | Suggestions | Optional |
| FILTERED | Style noise | Ignored |

### 8. Meta-Monitoring

Signals that indicate process needs refinement:

| Signal | Indicates | Response |
|--------|-----------|----------|
| Workers stuck at same point | Workflow gap | Fix the map |
| PRs need major rework | Design inadequate | Strengthen design gate |
| Same feedback on multiple PRs | Missing pattern | Add to docs/patterns/ |

Use `/refine-process` when signals appear 3+ times.

## Consequences

### Positive
- Alignment verified at planning, not PR review (cheaper)
- Pattern examples reduce repeated feedback
- Clear session types reduce context-switching
- Issue labels provide visibility into pipeline
- Gates model builds trust without constant monitoring

### Negative
- More structure in planning phase (overhead)
- Pattern files need maintenance as codebase evolves

### Neutral
- Process documentation in `.claude/workflows/`
- Terminology standardized across all docs

## Files

| File | Purpose |
|------|---------|
| `.claude/workflows/autonomous-session.md` | Required plan structure |
| `.claude/workflows/terminology.md` | Shared vocabulary |
| `.claude/commands/orchestrate.md` | Full lifecycle commands |
| `.claude/commands/refine-process.md` | Process refinement |
| `docs/patterns/*.cs` | Canonical code patterns |
| `CLAUDE.md` | Plan citation rules |

## References
- ADR-0030: Session Orchestration (architecture)
- ADR-0015: Application Service Layer
- ADR-0025: UI-Agnostic Progress Reporting
- ADR-0026: Structured Error Model

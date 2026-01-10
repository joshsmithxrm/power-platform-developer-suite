# PPDS Development Terminology

Shared vocabulary for human-AI collaboration. When we use these terms, we mean exactly this.

## Sessions

A **Session** is a Claude Code conversation with a specific purpose and context.

| Session Type | Purpose | Your Interaction | Output |
|--------------|---------|------------------|--------|
| **Design Studio** | Brainstorming, architecture, UI design, feature planning | Active collaboration - you're thinking with Claude | GitHub issues with designs, ADRs, wireframes |
| **Orchestrator** | Project management - spawns workers, monitors progress, surfaces status | Dashboard-style - you review status, answer questions, review PRs | Managed workers, progress reports |
| **Worker** | Executes a specific issue - plan, implement, test, ship | None during execution - you see output at PR stage | Commits, PRs |

**Key distinction:**
- Design Studio = *thinking together* (creative, exploratory)
- Orchestrator = *managing execution* (operational, status-focused)
- Worker = *doing the work* (autonomous, task-focused)

## Agents

An **Agent** is a specialized Claude capability that runs with a specific focus.

| Agent | Trigger | What It Does | Output |
|-------|---------|--------------|--------|
| **Triage Agent** | Issue has `needs-triage` label | Validates issue readiness | Adds `ready` label or comments with gaps |
| **Design Agent** | Issue has `needs-design` label | Creates architecture/UI proposal | Design comment on issue |
| **Review Agent** | PR created | Filters bot noise, categorizes by severity | Summary: "2 must-fix, 25 filtered" |
| **Docs Agent** | PR merged with user-facing changes | Creates documentation PR | Draft PR in ppds-docs |

**Agent vs Session:**
- Session = interactive, you're participating
- Agent = automated, runs on trigger, you review output

## Issue Lifecycle

```
IDEA → DRAFTED → DESIGNED → READY → IN PROGRESS → PR READY → MERGED
```

| Label | Meaning | Who Adds | Next Action |
|-------|---------|----------|-------------|
| `needs-design` | Needs architecture/UI/API design | You or Triage | Design Studio session |
| `needs-triage` | Needs validation and refinement | Auto on creation | Triage Agent processes |
| `designed` | Design complete, attached to issue | You | Triage Agent validates |
| `ready` | Validated, can be picked up | Triage Agent | Orchestrator assigns |
| `in-progress` | Worker actively working | Orchestrator | Wait for completion |
| `pr-ready` | PR created, awaiting review | Worker | You review |
| `blocked` | Cannot proceed, needs input | Worker or Triage | You unblock |

## Worker States

| State | Meaning | Visible To Orchestrator | Your Action |
|-------|---------|------------------------|-------------|
| `registered` | Worktree created, starting | "spawning" | None |
| `planning` | Exploring codebase | "planning" | None |
| `planning_complete` | Plan ready | "needs plan review" | Review, approve/redirect |
| `working` | Implementing | "working" | None |
| `stuck` | Needs input | "STUCK: {reason}" | Provide guidance |
| `pr_ready` | PR created | "PR ready" | Review PR |
| `merging` | Post-approval mechanics | "merging" | None |
| `complete` | PR merged | "complete" | None |
| `cancelled` | Work cancelled | "cancelled" | None |

## Design Artifacts

| Artifact Type | When Needed | Format | Lives In |
|---------------|-------------|--------|----------|
| Architecture Design | New services, data flow | ADR or issue comment | `docs/adr/` or issue |
| API Design | New CLI commands, services | Interface definition | Issue comment |
| UI Wireframe | TUI panels, Extension views | ASCII art | Issue comment |
| No Design Needed | Bug fixes, clear scope | N/A | Issue description |

## Commands

| Command | Used In | Purpose |
|---------|---------|---------|
| `/design` | Design Studio | Start feature design conversation |
| `/design-ui` | Design Studio | Reference-driven UI design |
| `/orchestrate` | Orchestrator | Manage workers, view status, spawn work |
| `/start-work` | Worker | Begin work on assigned issue |
| `/commit` | Worker | Phase-aware checkpoint commit |
| `/test` | Worker | Run appropriate tests |
| `/ship` | Worker | Full PR workflow |
| `/triage` | Orchestrator | Batch process issues for readiness |
| `/prune` | Maintenance | Clean up merged branches/worktrees |
| `/refine-process` | Any | Lightweight process refinement |

## The Zen Model (Gates Only)

Monitor the GATES, not the ZONE.

```
                 ┌─────────────────────────────────┐
                 │     AUTONOMOUS ZONE             │
                 │     (Workers execute)           │
────────────────>│ ENTRY GATE                      │
  Plan approved  │ (alignment verification)        │
                 │                       EXIT GATE │<────────────
                 │                      (PR review)│ Quality check
                 │     ESCAPE HATCH ──────────────│───> STUCK
                 │     (worker signals)           │
                 └─────────────────────────────────┘
```

| Gate | Purpose | Strong When |
|------|---------|-------------|
| Entry (plan review) | Verify alignment | Claude's understanding matches your intent |
| Exit (PR review) | Verify quality | Code follows patterns, tests pass |
| Escape (stuck) | Surface problems | Workers reliably signal when blocked |

**You don't need to watch the middle.**

## Meta-Monitoring

Signals that indicate workflow needs adjustment:

| Signal | Indicates | Response |
|--------|-----------|----------|
| Workers frequently stuck at same point | Workflow gap | Fix the map, not the worker |
| PRs need major rework after review | Design phase inadequate | Strengthen design gate |
| You check on workers frequently | Trust not calibrated | Build confidence or acknowledge gap |
| Claude does unexpected things | Terminology mismatch | Add examples, not more rules |
| Same feedback on multiple PRs | Missing pattern | Add to docs/patterns/ |

Run `/refine-process` when signals appear 3+ times.

## Trust Calibration

Start tight, loosen with evidence.

| Agent/Gate | Initial State | Earned Autonomy | Criteria |
|------------|---------------|-----------------|----------|
| Worker planning | Full review every plan | Batch-approve similar | 5 PRs with no misalignment |
| Review Agent | Human reviews summary | Trust FILTERED | 10 accurate categorizations |
| Triage Agent | Human approves labels | Auto-apply labels | 20 accurate triages |

## Documentation Standards

| Content Type | Repo | Location |
|--------------|------|----------|
| ADRs | ppds | `docs/adr/` |
| CLAUDE.md | ppds | root |
| Rules | ppds | `.claude/rules/` |
| Workflow docs | ppds | `.claude/workflows/` |
| Commands | ppds | `.claude/commands/` |
| Pattern examples | ppds | `docs/patterns/` |
| User guides | ppds-docs | `docs/guides/` |
| Reference | ppds-docs | `docs/reference/` |

## Review Agent Categories

| Category | Meaning | Your Action |
|----------|---------|-------------|
| **MUST FIX** | Security issues, bugs | Fix before merge |
| **SHOULD FIX** | Pattern violations | Fix or justify |
| **CONSIDER** | Suggestions | Optional |
| **FILTERED** | Style noise, false positives | Ignored |

## Related

- [ADR-0030: Session Orchestration](../../docs/adr/0030_SESSION_ORCHESTRATION.md) (architecture)
- [ADR-0031: Development Process V2](../../docs/adr/0031_DEVELOPMENT_PROCESS_V2.md) (process)
- [Autonomous Session](./autonomous-session.md)
- [Parallel Work](./parallel-work.md)

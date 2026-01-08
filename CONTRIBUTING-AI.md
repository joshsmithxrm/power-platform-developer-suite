# Agentic AI Workflow

Patterns and practices for AI-assisted software development in the PPDS ecosystem.

This document captures lessons learned from building PPDS with agentic AI. It's a living document that evolves with practice.

---

## Philosophy

### Why This Exists

A single architect can now design and implement substantial systems by leveraging AI agents. This changes the economics of software development - but only if you work *with* the AI effectively.

This isn't about replacing human judgment. It's about:
- Offloading mechanical work to AI
- Maintaining architectural control
- Building systems that are maintainable long-term
- Creating foundations that support iteration

### Core Principles

1. **AI executes, you architect** - You make design decisions. AI implements them.
2. **Context is king** - AI without context produces generic solutions. Invest in CLAUDE.md.
3. **Iterate from use** - Don't over-design upfront. Build, use, refine.
4. **Document decisions, not just code** - Future you (and future AI) need to know *why*.

---

## CLAUDE.md Patterns

CLAUDE.md is not documentation - it's **instructions**. It tells the AI how to work in your codebase.

### What Makes a Good CLAUDE.md

| Section | Purpose | Example |
|---------|---------|---------|
| **Project Overview** | 1-2 sentences on what this is | "NuGet package for plugin registration" |
| **Tech Stack** | Technologies and versions | .NET 4.6.2/6.0/8.0, C# |
| **Project Structure** | Directory tree | Where things live |
| **Commands** | Copy-paste ready | `dotnet build`, `npm test` |
| **Conventions** | Naming, patterns | `Verb-Dataverse<Noun>` |
| **NEVER** | Anti-patterns | Things the AI should never do |
| **ALWAYS** | Required patterns | Things the AI must do |
| **Testing Requirements** | What's required before PR | Coverage targets, test commands |
| **Decision Presentation** | How to present choices | Lead with recommendation |

### NEVER/ALWAYS Structure

The most valuable sections. These prevent mistakes.

```markdown
## NEVER (Non-Negotiable)

| Rule | Why |
|------|-----|
| `Console.WriteLine` in plugins | Sandbox blocks it; use `ITracingService` |
| Hardcoded GUIDs | Breaks across environments |

## ALWAYS (Required Patterns)

| Rule | Why |
|------|-----|
| `ITracingService` for debugging | Only way to get runtime output |
| Early-bound entities | Compile-time type checking |
```

### Evolution Pattern

CLAUDE.md grows from corrections, not upfront design:

1. AI makes a mistake
2. You correct it
3. You add a rule to prevent recurrence
4. Repeat

Don't try to anticipate everything. Add rules when you see patterns of mistakes.

---

## Working With Agents

### Agent Usage by Phase

| Phase | Use Agents? | Why |
|-------|-------------|-----|
| **Exploration** | Yes | Gather context efficiently before or during design |
| **Design/Alignment** | No | Design is conversation - requires iteration and judgment |
| **Writing Spec** | No | You're capturing the conversation you just had |
| **Implementation** | Sometimes | Parallel independent work benefits from agents |
| **Review** | Yes | Code review agents can help |

### Why Not Agents for Design?

Design is fundamentally a **conversation**. It requires:
- Back-and-forth iteration
- Clarifying questions you need to answer
- Human judgment on trade-offs
- Building on previous context

Agents can't do this. They execute a task and return - no iteration, no follow-up questions.

### When Agents Excel

- **Exploration**: "Find all authentication handlers across repos"
- **Parallel implementation**: Independent tasks that don't depend on each other
- **Cross-repo work**: Multiple agents working in different repos simultaneously
- **Mechanical tasks**: Renaming, reformatting, applying patterns

---

## Plan Mode (2-3x Success Rate)

Use Plan Mode (Shift+Tab 2x) before complex implementations:

1. Enter plan mode
2. Iterate on the plan with Claude until you're aligned
3. Exit plan mode and execute

This 2-3x improves success rates for non-trivial tasks. Don't skip straight to implementation.

### When to Enter Plan Mode

Use Plan Mode when:
- Task requires multiple logical phases
- Architectural decisions need alignment
- Scope is unclear or needs exploration
- You want 2-3x higher success rate

Skip Plan Mode only for:
- Single-file fixes
- Typos, obvious bugs
- Tasks with explicit step-by-step instructions

---

## Context Preservation

The biggest challenge with AI-assisted development: alignment conversations eat context, leaving nothing for execution.

### The Pattern That Solves This

**Write decisions to a file before executing.**

```
1. Alignment conversation → decisions made
2. Write EXECUTION_SPEC.md capturing decisions
3. Execute against the spec
4. Agents can READ the spec for context
5. Delete spec when done (git history preserves it)
```

### When to Write a Spec

| Scenario | Write Spec? |
|----------|-------------|
| Trivial task (< 30 min) | No |
| Non-trivial, single session, won't forget | Optional |
| Non-trivial, might span sessions | Yes |
| Delegating to agents | Yes |
| Cross-repo coordination | Yes |

---

## Session Startup Checklist

Before starting a non-trivial implementation session:

### Pre-Flight

- [ ] **Clear task scope** - Do you have issue numbers, requirements, or a clear goal?
- [ ] **Right location** - Are you in the correct repo/worktree for this work?
- [ ] **Clean state** - No uncommitted changes from previous work (`git status`)?
- [ ] **Branch strategy** - Are you on the right branch or need a new one?
- [ ] **CLAUDE.md current** - Have you reviewed project-specific instructions?

### First Actions

1. **Fetch requirements** - If referencing issues, fetch them via WebFetch
2. **Explore codebase** - Understand current state before proposing changes
3. **Identify scope** - How many files? What patterns exist?
4. **Propose decision points** - What needs user input before implementation?

---

## Commit Cadence

### Commit Per Phase

```
Phase 1: Infrastructure/Foundation
    └── Commit: "feat: Add structured error handling infrastructure (#77)"

Phase 2: Core Implementation
    └── Commit: "feat: Add global CLI options (#76, #77)"

Phase 3-N: Migrations/Updates
    └── Commit: "refactor: Migrate auth commands to structured output (#76)"

Final: Tests & Documentation
    └── Commit: "test: Add unit tests for structured error handling"
    └── Commit: "docs: Add ADR-0008 for CLI output architecture"
```

### Guidelines

| Scenario | Commit Strategy |
|----------|-----------------|
| Building foundation + features | Commit foundation first, then features |
| Migrating multiple files | Commit logical groups (e.g., all auth commands) |
| Adding tests | Can be with implementation or separate commit |
| Documentation | Separate commit unless trivial |

---

## Anti-Patterns

### What Doesn't Work

| Anti-Pattern | Why It Fails | Instead |
|--------------|--------------|---------|
| **Elaborate upfront templates** | Projects differ too much | Minimal start, iterate |
| **Generic documentation** | AI needs specific instructions | CLAUDE.md with concrete rules |
| **Hoping AI knows your patterns** | It doesn't | Document in NEVER/ALWAYS |
| **Long alignment, no execution** | Context exhausted | Write spec file, then execute |
| **Treating AI as autonomous** | You're the architect | Clear direction, review output |
| **Using agents for design** | Design needs conversation | Do design directly with Claude |

### Signs You're Off Track

- AI keeps making the same mistakes → Missing CLAUDE.md rules
- Spending more time correcting than building → Step back, document patterns
- Losing context mid-task → Write spec files
- AI solutions don't fit your architecture → Need clearer constraints

---

## Evolution

This document will evolve. Update it when:

- You discover a pattern that works
- You find an anti-pattern to avoid
- Your workflow changes
- You learn something from a mistake

The best practices are the ones you actually use.

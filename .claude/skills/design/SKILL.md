---
name: design
description: Brainstorm ideas into specs and plans through collaborative dialogue. Use when starting a new feature, exploring an idea, or designing a system — before any implementation.
---

# Design

Collaborative design sessions that produce reviewed specs and implementation plans. Brainstorm → spec → review → plan → review → handoff. Work on a feature branch, not on main.

## When to Use

- Starting any new feature or non-trivial change
- "I have an idea for..." / "Let's design..." / "We need to figure out how to..."
- Read REFERENCE.md §1 for full guidance and anti-patterns.

## Process

### Step 1: Load Context and Search

**Gate:** Check current branch. If on `main` or `master`, error immediately:
> You're on main. Work on a feature branch (a worktree keeps parallel sessions isolated) before designing.

Before asking any questions, read `specs/CONSTITUTION.md` and `specs/SPEC-TEMPLATE.md`.

**Search for existing specs:** Grep all `specs/*.md` for overlapping scope — check file names, overviews, and Code frontmatter. If found, present it and ask: update or new spec?

**Check for design context:** Check for `.plans/context.md` in the current working directory.

If found: read it, summarize it, ask "Proceed to spec writing, or brainstorm further?" Proceed → skip Step 2. Brainstorm → use context as starting input. Apply constraint checking (Read REFERENCE.md §3).

If not found: continue with Step 2.

### Step 2: Brainstorm

**Understand the idea** — ask clarifying questions **one at a time**: prefer multiple choice; focus on purpose, constraints, success criteria.

**Multi-Concern Checkpoint**

After the clarifying questions, evaluate the clarified scope. The heuristic trips if **either** is true:

- The scope contains **more than 3 sub-features**, OR
- **Two or more sub-features could ship independently** (one does not require the other to be valuable)

When the heuristic trips, ask the human:

> You raised N concerns: {bulleted list}. Are these:
> 1. One cohesive feature with a shared trigger event (continue the design), or
> 2. N separate features that share a trigger event but ship independently?

On (1) — cohesive (false positive): continue with "Explore approaches".

On (2) — separate features: recommend filing N issues. Ask:

> (a) File issues via `/backlog` (recommended)
> (b) Continue with this design anyway

On (2)(a) — split: invoke `/backlog` via Skill tool; exit `/design`; instruct the user to start a fresh branch/worktree per issue.

On (2)(b) — proceed anyway: continue, flag as a deliberate multi-concern design.

**Explore approaches:** propose 2-3 different approaches with trade-offs; lead with your recommended option.

**Present design** — in sections, scaled to complexity; ask after each "Does this look right?". Read REFERENCE.md §4 for section coverage checklist.

### Step 3: Write Spec and Review

When the design is approved:

**A. Write the spec** to `specs/<name>.md` using the spec template. Include numbered ACs (Constitution I3). Preserve unchanged sections if updating.

**B. Review the spec (bias-isolated / design-fidelity):** invoke `/review` — reviewer gets ONLY spec content, constitution, and spec template. Fix critical and important findings.
**B.2. Scope-conformance review:** see REFERENCE.md §9 for full protocol. If the design traces to linked issues, fetch each body (`gh issue view <N> --json title,body --template '# {{.title}}\n\n{{.body}}'`), spawn a reviewer with the issue body + spec. Block on `missing`/`reframed` items; revise the spec or add to `### Non-Goals` with rationale. Re-run B.2 after each revision (and re-run B if changes are substantial), until all items `covered` or `in-non-goals`.
**C. Present:** Present the spec, show review findings (fixed vs. dismissed with rationale). Wait for approval.

### Step 4: Write Plan and Review

**A. Write the plan** to `.plans/<date>-<name>.md`: phased, each phase maps to spec ACs, sequential vs parallel identified, file paths and commands included.

**B. Review the plan:** invoke `/review` — reviewer checks plan against spec ACs for gaps. Fix findings.

**C. Present to user:** present plan with summary table, show review findings. Wait for approval.

### Step 5: Commit

On user approval:

```bash
git add specs/<name>.md
git commit -m "spec: <name>

Co-Authored-By: {use the format from the system prompt}"
```

Note: `.plans/` is gitignored — the plan lives in the worktree only.

### Step 6: Handoff

Present two options:

```text
Spec committed. Choose next step:

  1. Continue interactively
     → /implement

  2. Defer (pick up later)
     → Spec is committed on the current feature branch. Resume anytime.
```

If option 1: invoke `/implement` immediately.
If option 2: note the spec path and stop.

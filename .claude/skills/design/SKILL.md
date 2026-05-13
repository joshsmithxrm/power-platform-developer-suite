---
name: design
description: Brainstorm ideas into specs and plans through collaborative dialogue. Use when starting a new feature, exploring an idea, or designing a system — before any implementation. Requires a worktree (run /start first).
---

# Design

Collaborative design sessions that produce reviewed specs and implementation plans. Brainstorm → spec → review → plan → review → handoff. Runs in a worktree, not on main.

## When to Use

- Starting any new feature or non-trivial change
- "I have an idea for..." / "Let's design..." / "We need to figure out how to..."
- Read REFERENCE.md §1 for full guidance and anti-patterns.

## Process

### Step 0: Set Phase

```bash
python scripts/workflow-state.py set phase design
```

### Step 1: Load Context and Search

**Gate:** Check current branch. If on `main` or `master`, error immediately:
> You're on main. Run `/start` first to create a worktree.

Before asking any questions, read `specs/CONSTITUTION.md` and `specs/SPEC-TEMPLATE.md`.

**Search for existing specs:** Grep all `specs/*.md` for overlapping scope — check file names, overviews, and Code frontmatter. If found, present it and ask: update or new spec?

**Check for design context:** Check for `.plans/context.md` in the current working directory.

If found: read it, summarize it, ask "Proceed to spec writing, or brainstorm further?" Proceed → skip Step 2. Brainstorm → use context as starting input. Apply constraint checking (Read REFERENCE.md §3).

If not found: continue with Step 2.

### Step 2: Brainstorm

**Understand the idea** — ask clarifying questions **one at a time**: prefer multiple choice; focus on purpose, constraints, success criteria.

**Multi-Concern Checkpoint**

<!-- enforcement: T3 advisory — see specs/skill-routing-gates.md and issue #1023 -->

After the clarifying questions, evaluate the clarified scope. The heuristic trips if **either** is true:

- The scope contains **more than 3 sub-features**, OR
- **Two or more sub-features could ship independently** (one does not require the other to be valuable)

When the heuristic trips, emit:

```bash
python scripts/workflow-state.py bump routing_gates.design.fired_count
```

Ask the human:

> You raised N concerns: {bulleted list}. Are these:
> 1. One cohesive feature with a shared trigger event (continue the design), or
> 2. N separate features that share a trigger event but ship independently?

On (1) — cohesive (false positive): continue with "Explore approaches".

```bash
python scripts/workflow-state.py bump routing_gates.design.cohesion_confirmed_count
```

On (2) — separate features: recommend filing N issues. Ask:

> (a) File issues via `/backlog` (recommended)
> (b) Continue with this design anyway

On (2)(a) — split: emit the bump FIRST (the Skill tool transfers execution, so anything after it never runs):

```bash
python scripts/workflow-state.py bump routing_gates.design.split_count
```

Then invoke `/backlog` via Skill tool; exit `/design`; instruct user to `/start` a new worktree per issue.

On (2)(b) — proceed anyway: continue, flag as deliberate multi-concern design (cross-reference issue #989).

```bash
python scripts/workflow-state.py bump routing_gates.design.proceed_anyway_count
```

**Explore approaches:** propose 2-3 different approaches with trade-offs; lead with your recommended option.

**Present design** — in sections, scaled to complexity; ask after each "Does this look right?". Read REFERENCE.md §4 for section coverage checklist.

### Step 3: Write Spec and Review

When the design is approved:

**A. Write the spec** to `specs/<name>.md` using the spec template. Include numbered ACs (Constitution I3). Preserve unchanged sections if updating.

**B. Review the spec:** invoke `/review` — reviewer gets ONLY spec content, constitution, and spec template. Fix critical and important findings. Restore phase: `python scripts/workflow-state.py set phase design`

**C. Present to user:** present the spec, show review findings (fixed vs. dismissed with rationale). Wait for approval.

### Step 4: Write Plan and Review

**A. Write the plan** to `.plans/<date>-<name>.md`: phased, each phase maps to spec ACs, sequential vs parallel identified, file paths and commands included.

**B. Review the plan:** invoke `/review` — reviewer checks plan against spec ACs for gaps. Fix findings. Restore phase: `python scripts/workflow-state.py set phase design`

**C. Present to user:** present plan with summary table, show review findings. Wait for approval.

### Step 5: Commit

On user approval:

```bash
git add specs/<name>.md
git commit -m "spec: <name>

Co-Authored-By: {use the format from the system prompt}"
```

Note: `.plans/` is gitignored — the plan lives in the worktree only.

```bash
python scripts/workflow-state.py set spec specs/<name>.md
```

### Step 6: Handoff

**Do NOT flip `phase` here.** The downstream skill owns its own phase transition (`/implement` sets `phase=implementing`; pipeline sets `phase=pipeline`; Defer leaves `phase=design`). Read REFERENCE.md §5 for the historical rationale.

Present three options:

```
Spec committed. Choose next step:

  1. Launch headless pipeline (recommended)
     → python scripts/pipeline.py --worktree <cwd> --spec specs/<name>.md --from implement

  2. Continue interactively
     → /implement

  3. Defer (pick up later)
     → Spec is committed on branch feat/<name>. Resume anytime.
```

If option 1: run pipeline command in background (pipeline sets its own phase).
If option 2: invoke `/implement` immediately (it sets `phase=implementing` at Step 3.5).
If option 3: note spec path and stop — phase stays `design`.

---
name: design
description: Brainstorm ideas into specs and plans through collaborative dialogue. Use when starting a new feature, exploring an idea, or designing a system — before any implementation. Requires a worktree (run /start first).
---

# Design

Collaborative design sessions that produce reviewed specs and implementation plans. Brainstorm → spec → review → plan → review → handoff. Runs in a worktree, not on main.

## When to Use

- "I have an idea for..."
- "Let's design..."
- "We need to figure out how to..."
- "Let's brainstorm..."
- Starting any new feature or non-trivial change

## Process

### Step 0: Set Phase

```bash
python scripts/workflow-state.py set phase design
```

### Step 1: Load Context and Search

**Gate:** Check current branch. If on `main` or `master`, error immediately:
> You're on main. Run `/start` first to create a worktree.

Before asking any questions, read:
- `specs/CONSTITUTION.md` — non-negotiable principles that constrain the design
- `specs/SPEC-TEMPLATE.md` — the format the output must follow

**Search for existing specs:** Grep all `specs/*.md` for overlapping scope — check file names, overview sections, and Code frontmatter for the domain being designed. If an existing spec covers this domain:
- Present the finding: "Found existing spec `specs/<name>.md` covering this domain."
- Propose update mode: "Should I update this spec, or is this a new spec?"
- If updating, read the existing spec fully before proceeding.

**Check for design context:** After searching for existing specs, check for
`.plans/context.md` in the current working directory.

If found:
1. Read the file
2. Present a summary: "Design context loaded from investigation: {topic}. Covers: {scope summary}."
3. Ask: "Proceed to spec writing, or brainstorm further?"
4. If "proceed": skip Step 2 (Brainstorm), go directly to Step 3 (Write Spec)
5. If "brainstorm": continue with Step 2, using the design context as starting input

**Constraint checking:** Before presenting the architecture (Step 2 or Step 3),
verify the proposal against each Constraint and each Known Concern in
`context.md`. Flag conflicts — e.g., "Constraint #3 says X, but the
proposed architecture does Y."

If not found: continue with normal Step 2 brainstorm flow.

### Step 2: Brainstorm

**Understand the idea** — ask clarifying questions **one at a time**:
- Prefer multiple choice when possible
- Focus on: purpose, constraints, success criteria
- Assess scope: if the request describes multiple independent subsystems, flag this immediately and help decompose

**Explore approaches:**
- Propose 2-3 different approaches with trade-offs
- Lead with your recommended option and explain why
- Be honest about consequences — don't oversell

**Present design** — present in sections, scaled to complexity:
- Ask after each section: "Does this look right?"
- Cover: architecture, components, data flow, error handling, testing
- Check against constitution principles — flag any tensions

### Step 3: Write Spec and Review

When the design is approved:

**A. Write the spec:**
1. Write the spec to `specs/<name>.md` using the spec template
2. Include numbered acceptance criteria (Constitution I3)
3. If updating an existing spec, preserve unchanged sections

**B. Review the spec:**
1. Invoke `/review` — dispatch an impartial reviewer that gets ONLY the spec content, constitution, and spec template. No design conversation context.
2. Fix critical and important findings
3. Note which findings were fixed and which were dismissed with rationale
4. Restore phase: `python scripts/workflow-state.py set phase design`

**C. Present to user:**
1. Present the spec to the user
2. Show review findings: "The reviewer found N issues. Fixed M, dismissed K. Here's what was caught and fixed: [list]. Here's what I disagreed with: [list with rationale]."
3. Wait for user approval before proceeding

### Step 4: Write Plan and Review

After user approves the spec:

**A. Write the implementation plan:**
1. Generate a phased implementation plan in `.plans/<date>-<name>.md`
2. Each phase should map to specific ACs from the spec
3. Identify sequential vs parallel phases
4. Include file paths, commands, and verification steps

**B. Review the plan:**
1. Invoke `/review` — reviewer checks plan against spec ACs for coverage gaps
2. Fix findings (missing ACs, incorrect phase ordering, missing verification)
3. Note fixes and dismissals
4. Restore phase: `python scripts/workflow-state.py set phase design`

**C. Present to user:**
1. Present the plan with a summary table (phases, files, ACs covered)
2. Show review findings and fixes
3. Wait for user approval

### Step 5: Commit

On user approval of the plan:

```bash
git add specs/<name>.md
git commit -m "spec: <name>

Co-Authored-By: {use the format from the system prompt}"
```

Note: `.plans/` is gitignored — the plan lives in the worktree filesystem only.

Write the spec path to workflow state so `/implement` can find it:

```bash
python scripts/workflow-state.py set spec specs/<name>.md
```

### Step 6: Handoff

**Do NOT flip `phase` here.** Leave it as `design`. The downstream skill
owns its own phase transition:

- `/implement` Step 3.5 sets `phase=implementing` when the user actually
  starts building.
- `scripts/pipeline.py` sets `phase=pipeline` when the headless pipeline
  starts (and also sets `PPDS_PIPELINE=1`, which bypasses the stop hook
  entirely).
- If the user chooses **Defer**, phase stays `design` — which the
  session-stop hook treats as a bypass phase, so the user can close the
  session cleanly without triggering a premature gates-enforcement loop
  on a spec-only commit.

Historical note: this used to unconditionally run
`workflow-state.py set phase implementing` here, which caused issue
#800 — pausing between `/design` and `/implement` triggered the
session-stop hook to block with a spurious "gates/verify/QA missing"
message on a spec-only commit.

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

If the user chooses option 1, run the pipeline command in the background
(pipeline sets its own phase).
If option 2, invoke `/implement` immediately (it sets `phase=implementing`
at Step 3.5).
If option 3, note the spec path and stop — phase stays `design`, stop
hook allows clean exit.

## Key Principles

- **One question at a time** — don't overwhelm
- **Multiple choice preferred** — easier to answer than open-ended
- **YAGNI ruthlessly** — remove unnecessary features
- **Explore alternatives** — always propose 2-3 approaches before settling
- **Incremental validation** — present design, get approval, then proceed
- **Constitution compliance** — every design must comply with the constitution
- **Review before presenting** — specs and plans go through /review before the user sees them
- **Do NOT use plan mode** — /design has its own approval gates (one question at a time, incremental validation). Plan mode blocks spec writing. Exit plan mode before running /design.

## Anti-Patterns

| Pattern | Fix |
|---------|-----|
| "This is too simple for a design" | Every new feature goes through this. Bug fixes skip design entirely (code + test + `/gates` + `/verify` + `/pr`). Enhancements with existing specs use `/implement` directly. Short designs are fine. |
| Jumping to implementation | Design MUST be approved before any code |
| Asking 5 questions at once | One question per message |
| Proposing only one approach | Always propose 2-3 with trade-offs |
| Skipping the spec | The spec IS the deliverable of this skill |
| Skipping the plan | The plan is the second deliverable — spec alone isn't enough |
| Using plan mode with /design | /design has its own approval gates. Exit plan mode first. |
| Running on main | /design requires a worktree. Run /start first. |
| Skipping spec search | Always search existing specs before creating new ones. |

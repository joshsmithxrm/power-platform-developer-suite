---
name: design
description: Brainstorm ideas into specs through collaborative dialogue. Use when starting a new feature, exploring an idea, or designing a system — before any implementation.
---

# Design

Collaborative design sessions that produce committed spec files. Replaces external brainstorming workflows with a PPDS-native process that knows our architecture, constitution, and spec template.

## When to Use

- "I have an idea for..."
- "Let's design..."
- "We need to figure out how to..."
- "Let's brainstorm..."
- Starting any new feature or non-trivial change

## Process

### 1. Load Context

Before asking any questions, read:
- `specs/CONSTITUTION.md` — non-negotiable principles that constrain the design
- `specs/SPEC-TEMPLATE.md` — the format the output must follow

### 2. Understand the Idea

Ask clarifying questions **one at a time**:
- Prefer multiple choice when possible
- Focus on: purpose, constraints, success criteria
- Assess scope: if the request describes multiple independent subsystems, flag this immediately and help decompose

### 3. Explore Approaches

- Propose 2-3 different approaches with trade-offs
- Lead with your recommended option and explain why
- Be honest about consequences — don't oversell

### 4. Present Design

- Present the design in sections, scaled to complexity
- Ask after each section: "Does this look right?"
- Cover: architecture, components, data flow, error handling, testing
- Check against constitution principles — flag any tensions

### 5. Write Spec

When the design is approved:
1. Create a `spec/<name>` branch from main: `git checkout -b spec/<name>`
2. Write the spec to `specs/<name>.md` using the spec template
3. Include numbered acceptance criteria (Constitution I3)
4. Commit the spec on the branch
5. Present the spec path for user review

**Why a branch?** GitHub branch protection blocks direct pushes to main. Specs go through PRs like everything else.

### 6. Transition

After user approves the written spec:
1. Write an implementation plan to `.plans/` (ephemeral, gitignored — do NOT commit plans)
2. Push the spec branch and create a PR for the spec
3. Present the pipeline command for implementation:

> Spec PR created: `<url>`
>
> To implement: `python scripts/pipeline.py --spec specs/<name>.md --branch <branch-name>`
>
> Or say "run it" and I'll invoke the pipeline from here.

If the user wants to proceed immediately, invoke the pipeline:
```bash
python scripts/pipeline.py --spec specs/<name>.md --branch <branch-name>
```
Run this in the background so the user can check `/status` while it runs.

If deferring, note the plan path for the next session.

## Key Principles

- **One question at a time** — don't overwhelm
- **Multiple choice preferred** — easier to answer than open-ended
- **YAGNI ruthlessly** — remove unnecessary features
- **Explore alternatives** — always propose 2-3 approaches before settling
- **Incremental validation** — present design, get approval, then proceed
- **Constitution compliance** — every design must comply with the constitution

## Anti-Patterns

| Pattern | Fix |
|---------|-----|
| "This is too simple for a design" | Every change goes through this. Short designs are fine. |
| Jumping to implementation | Design MUST be approved before any code |
| Asking 5 questions at once | One question per message |
| Proposing only one approach | Always propose 2-3 with trade-offs |
| Skipping the spec | The spec IS the deliverable of this skill |
| Using plan mode with /design | /design has its own approval gates (one question at a time, incremental validation). Plan mode blocks spec writing. Exit plan mode before running /design. |

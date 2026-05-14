# Design — Reference

## §1 - When to Use

Use `/design` for:
- "I have an idea for..." / "Let's design..." / "We need to figure out how to..."
- Starting any new feature or non-trivial change
- After `/investigate` produces a design context

Do NOT use for bug fixes (code + test + `/gates` + `/verify` + `/pr`) or for enhancements with an existing spec that just need `/implement`.

## §2 - Key Principles

- **One question at a time** — don't overwhelm
- **Multiple choice preferred** — easier to answer than open-ended
- **YAGNI ruthlessly** — remove unnecessary features
- **Explore alternatives** — always propose 2-3 approaches before settling
- **Incremental validation** — present design, get approval, then proceed
- **Constitution compliance** — every design must comply with the constitution
- **Review before presenting** — specs and plans go through /review before the user sees them
- **Do NOT use plan mode** — /design has its own approval gates. Exit plan mode before running /design.

## §3 - Step 1 Constraint Checking

Before presenting the architecture (Step 2 or Step 3), verify the proposal against each Constraint and each Known Concern in `context.md`. Flag conflicts — e.g., "Constraint #3 says X, but the proposed architecture does Y."

## §4 - Step 2 Present Design Detail

Present in sections, scaled to complexity. Ask after each section: "Does this look right?" Cover: architecture, components, data flow, error handling, testing. Check against constitution principles — flag any tensions.

## §5 - Step 6 Historical Note

This Step 6 used to unconditionally run `workflow-state.py set phase implementing`, which caused issue #800 — pausing between `/design` and `/implement` triggered the session-stop hook to block with a spurious "gates/verify/QA missing" message on a spec-only commit.

The fix: leave phase as `design` and let the downstream skill own its transition:
- `/implement` Step 3.5 sets `phase=implementing`
- `scripts/pipeline.py` sets `phase=pipeline`
- User choosing Defer: phase stays `design` (stop hook bypass phase)

## §6 - Anti-Patterns

| Pattern | Fix |
|---------|-----|
| "This is too simple for a design" | Every new feature goes through this. Bug fixes skip design entirely. Short designs are fine. |
| Jumping to implementation | Design MUST be approved before any code |
| Asking 5 questions at once | One question per message |
| Proposing only one approach | Always propose 2-3 with trade-offs |
| Skipping the spec | The spec IS the deliverable of this skill |
| Skipping the plan | The plan is the second deliverable — spec alone isn't enough |
| Using plan mode with /design | /design has its own approval gates. Exit plan mode first. |
| Running on main | /design requires a worktree. Run /start first. |
| Skipping spec search | Always search existing specs before creating new ones. |

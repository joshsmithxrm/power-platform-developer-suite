# Architecture Decision Records (ADRs)

This directory contains Architecture Decision Records for PPDS.

## What is an ADR?

An ADR captures a significant architectural decision along with its context and consequences. ADRs help future maintainers understand *why* decisions were made, not just *what* was decided.

## When to Write an ADR

Write an ADR when:

| Situation | Example |
|-----------|---------|
| Introducing a new pattern | Connection pooling strategy, error handling model |
| Making a cross-cutting decision | File format policy, CLI output conventions |
| Choosing between alternatives | Why bulk APIs over ExecuteMultiple |
| Defining ownership/responsibility | Which component owns elapsed time tracking |

Don't write an ADR for:
- Bug fixes
- Implementation details that don't affect architecture
- Decisions that are easily reversible

## ADR Format

Use this template:

```markdown
# ADR-NNNN: Title

**Status:** Proposed | Accepted | Deprecated | Superseded by ADR-XXXX
**Date:** YYYY-MM-DD
**Authors:** Names

## Context

What is the issue? Why are we making this decision?

## Decision

What is the change being proposed/made?

## Consequences

### Positive
- Benefits of this decision

### Negative
- Tradeoffs and costs

### Neutral
- Side effects that are neither good nor bad

## References

- Related ADRs
- External documentation
```

## ADR Lifecycle

### Immutability

**ADRs are immutable once accepted.** The decision was made at a point in time with specific context. Changing the ADR retroactively hides history.

### Evolving Decisions

When a decision needs to change:

1. **Create a new ADR** that supersedes the old one
2. **Update the old ADR's status** to `Superseded by ADR-XXXX`
3. **Reference the old ADR** in the new one to explain the evolution

### Filling Gaps

When an existing ADR has gaps (missing concerns):

1. **Create a new ADR** addressing the gap
2. **Add a reference** in the original ADR's References section
3. **Don't modify** the original decision or context

Example:
```markdown
## References
- ADR-0015: Application Service Layer
- ADR-0027: Operation Clock (fills gap in elapsed time ownership)
```

## Numbering

ADRs are numbered sequentially: `0001`, `0002`, etc. Use the next available number.

To find the next number:
```powershell
Get-ChildItem docs/adr/*.md | Sort-Object Name | Select-Object -Last 1
```

## File Naming

Format: `NNNN_BRIEF_DESCRIPTION.md`

Examples:
- `0025_UI_AGNOSTIC_PROGRESS.md`
- `0026_STRUCTURED_ERROR_MODEL.md`

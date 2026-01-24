# {System Name}

**Status:** Draft | Implemented
**Version:** 1.0
**Last Updated:** YYYY-MM-DD
**Code:** [path/](../path/) | None

---

## Overview

{2-3 sentences explaining what this system does and why it matters.}

### Goals

- **{Goal 1}**: {What this system provides}
- **{Goal 2}**: {Another capability}

### Non-Goals

- {What this system explicitly does NOT do}
- {Deferred to another spec: link if applicable}

---

## Architecture

{ASCII diagram showing component relationships}

```
┌─────────────┐     ┌─────────────┐
│ Component A │────▶│ Component B │
└─────────────┘     └─────────────┘
        │
        ▼
┌─────────────┐
│ Component C │
└─────────────┘
```

{Brief explanation of what flows where and why.}

### Components

| Component | Responsibility |
|-----------|----------------|
| {Name} | {What it does} |

### Dependencies

- Depends on: [{other-spec}](./other-spec.md)
- Uses patterns from: [architecture.md](./architecture.md)

---

## Specification

### Core Requirements

1. {Requirement with specific behavior}
2. {Requirement with specific behavior}

### Primary Flows

**{Flow Name}:**

1. **{Step}**: {Description}
2. **{Step}**: {Description}
3. **{Step}**: {Description}

### Constraints

- {Constraint the implementation must follow}

### Validation Rules

| Field | Rule | Error |
|-------|------|-------|
| {field} | {validation} | {error message} |

---

## Core Types

### {TypeName}

{Purpose and when to use this type.}

```{language}
// Illustrative snippet (3-5 lines)
public interface ITypeName
{
    Task<Result> MethodAsync(Input input);
}
```

{For post-implementation: The implementation ([`File.cs:45-67`](../src/Path/File.cs#L45-L67)) handles...}

### Usage Pattern

```{language}
var instance = new TypeName(dependencies);
var result = await instance.MethodAsync(input);
```

---

## API/Contracts

{Include this section only if the system has external APIs.}

| Method | Path | Purpose |
|--------|------|---------|
| GET | /api/{resource} | {description} |
| POST | /api/{resource} | {description} |

### Request/Response Examples

**GET /api/{resource}**

Request:
```json
{}
```

Response:
```json
{}
```

---

## Error Handling

### Error Types

| Error | Condition | Recovery |
|-------|-----------|----------|
| {ErrorType} | {When it occurs} | {How to handle} |

### Recovery Strategies

- **{Error category}**: {How to recover}

### Edge Cases

| Scenario | Expected Behavior |
|----------|-------------------|
| Empty input | Return empty collection, not null |
| {case} | {behavior} |

---

## Design Decisions

{Required for every spec. Explain WHY, not just WHAT.}

### Why {Decision Name}?

**Context:** {Problem being solved}

**Decision:** {What was chosen}

**Test results:** {If available, include specific numbers}
| Scenario | Result |
|----------|--------|
| {Approach A} | {Outcome} |
| {Approach B} | {Outcome} |

**Alternatives considered:**
- {Alternative}: {Why rejected}

**Consequences:**
- Positive: {Benefits}
- Negative: {Trade-offs}

{For post-implementation: Absorb relevant ADRs here—do not link to them.}

---

## Extension Points

{Include this section only if the system is designed for extensibility.}

### Adding a New {Thing}

1. **Create {file/type}**: {What to create and where}
2. **Implement {interface}**: {Which interface and key methods}
3. **Register**: {How to wire it up}

**Example skeleton:**

```{language}
public class New{Thing} : I{Thing}
{
    // Required implementation
}
```

---

## Configuration

{Include this section only if the system has configuration.}

| Setting | Type | Required | Default | Description |
|---------|------|----------|---------|-------------|
| {SettingName} | {type} | Yes/No | {default} | {What it controls} |

---

## Testing

### Acceptance Criteria

- [ ] {Testable criterion}
- [ ] {Testable criterion}

### Edge Cases

| Scenario | Input | Expected Output |
|----------|-------|-----------------|
| {case} | {input} | {output} |

### Test Examples

```{language}
// Example test showing expected behavior
[Fact]
public void Should_DoSomething_When_Condition()
{
    // Arrange, Act, Assert
}
```

---

## Related Specs

- [{other-spec}.md](./other-spec.md) - {Relationship description}

{Note: ADRs are absorbed into Design Decisions, not linked here.}

---

## Roadmap

{Optional section for future enhancements.}

- {Planned enhancement}
- {Potential extension}

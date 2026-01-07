# ADR-0027: Multi-Interface Development Process

**Status:** Accepted
**Date:** 2026-01-07
**Authors:** Josh, Claude

## Context

PPDS has multiple user interfaces for the same functionality:

| Interface | Technology | Purpose |
|-----------|------------|---------|
| CLI | System.CommandLine + Spectre.Console | Command-line operations |
| TUI | Terminal.Gui | Interactive terminal application |
| RPC | StreamJsonRpc | VS Code extension backend |
| MCP | Model Context Protocol | AI tool integration |
| Extension | TypeScript + VS Code API | VS Code UI |

Without a defined process:
- Features get implemented inconsistently across interfaces
- UI patterns diverge between TUI and Extension
- Some interfaces lag behind others
- No clear guidance on which interfaces a feature needs

## Decision

### TUI-First Development

The TUI is the **reference implementation** for UI patterns. The VS Code extension ports TUI patterns rather than inventing its own.

### Development Order

When implementing a new feature:

```
1. Application Service    → Business logic, testable in isolation
2. CLI Command            → Exposes service, defines parameters
3. TUI Panel              → Reference UI implementation
4. RPC Method             → If extension needs data not available via existing RPC
5. MCP Tool               → If AI analysis adds value
6. Extension View         → Ports TUI patterns to VS Code
```

### Interface Matrix

Each feature issue includes an interface checklist:

```markdown
### Interface Matrix

| Interface | In Scope | Status | Implementation |
|-----------|----------|--------|----------------|
| CLI       | [x]      | [ ]    | `ppds foo bar` |
| TUI       | [x]      | [ ]    | Foo Panel      |
| RPC       | [ ]      | N/A    | -              |
| MCP       | [x]      | [ ]    | `ppds_foo_bar` |
| Extension | [ ]      | N/A    | Deferred to v1.1 |
```

### When to Include Each Interface

| Interface | Include When |
|-----------|--------------|
| **CLI** | Always - this is the service exposure layer |
| **TUI** | Feature has interactive use case |
| **RPC** | Extension needs data not available via existing RPC methods |
| **MCP** | AI analysis adds value (queries, debugging, metadata) |
| **Extension** | After TUI pattern is established |

### Interface Labels

Issues are labeled by interface:
- `interface:cli`
- `interface:tui`
- `interface:mcp`
- `interface:extension`

## Consequences

### Positive

- **Consistent UI patterns** - Extension follows TUI, not the other way around
- **Clear development order** - No ambiguity about what to build first
- **Explicit scoping** - Each feature declares which interfaces are in scope
- **Parallelizable** - Different interfaces can be developed by different sessions
- **Incremental delivery** - Features can ship to CLI/TUI first, Extension later

### Negative

- **TUI dependency** - Extension development blocked on TUI patterns
- **Additional overhead** - Interface matrix adds to issue templates

### Neutral

- **Partial delivery** - Features may exist in CLI/TUI but not Extension

## Implementation

### `/new-feature` Slash Command

Generate interface matrix for new features:

```markdown
## Usage
/new-feature [feature-name]

## Output
Creates issue template with:
- Feature description
- Interface matrix checklist
- Suggested labels
```

### Feature Template

```markdown
## Feature: [Name]

**Description:** [What it does]

**Application Service:** `I[Name]Service`

### Interface Matrix

| Interface | In Scope | Status | Implementation |
|-----------|----------|--------|----------------|
| CLI       | [ ]      | [ ]    | `ppds ...`     |
| TUI       | [ ]      | [ ]    | [Panel name]   |
| RPC       | [ ]      | [ ]    | `[method]`     |
| MCP       | [ ]      | [ ]    | `ppds_...`     |
| Extension | [ ]      | [ ]    | [View name]    |

### Acceptance Criteria

- [ ] Application Service implemented with tests
- [ ] [Each in-scope interface checked]
- [ ] Documentation updated
```

## Examples

### Example: Plugin Traces Feature

```markdown
### Interface Matrix

| Interface | In Scope | Status | Implementation |
|-----------|----------|--------|----------------|
| CLI       | [x]      | [x]    | `ppds plugin-traces list/get/timeline` |
| TUI       | [x]      | [ ]    | Plugin Traces Panel |
| RPC       | [ ]      | N/A    | Uses existing query RPC |
| MCP       | [x]      | [ ]    | `ppds_plugin_traces_list/get/timeline` |
| Extension | [x]      | [ ]    | Plugin Traces View (ports TUI) |
```

### Example: Bulk Delete Feature

```markdown
### Interface Matrix

| Interface | In Scope | Status | Implementation |
|-----------|----------|--------|----------------|
| CLI       | [x]      | [x]    | `ppds data delete` |
| TUI       | [x]      | [ ]    | Delete confirmation dialog |
| RPC       | [ ]      | N/A    | - |
| MCP       | [ ]      | N/A    | Excluded (destructive) |
| Extension | [ ]      | N/A    | Deferred |
```

## References

- ADR-0015: Application Service Layer
- ADR-0018: MCP Server Architecture
- CLAUDE.md: Platform Architecture section

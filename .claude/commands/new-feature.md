# New Feature Planning

Generate an interface matrix for planning a new feature across CLI/TUI/MCP/Extension.

## Usage

`/new-feature [feature-name]`

Examples:
- `/new-feature web-resources` - Plan web resource management feature
- `/new-feature bulk-update` - Plan bulk update feature

## Arguments

`$ARGUMENTS` - Feature name (optional, will prompt if not provided)

## Process

### 1. Understand the Feature

If feature name not provided, ask:
- What is the feature?
- What problem does it solve?
- Who uses it?

### 2. Design Application Service

Identify:
- Service interface name (`I[Feature]Service`)
- Key methods
- Dependencies on other services

### 3. Generate Interface Matrix

For each interface, determine:
- **In Scope?** - Does this feature need this interface?
- **Implementation** - What specifically will be built?

### 4. Output Issue Template

Generate a GitHub issue with the interface matrix.

---

## Template Output

```markdown
## Feature: [name]

**Description:** [What it does and why]

**Application Service:** `I[Name]Service`

### Interface Matrix

| Interface | In Scope | Implementation |
|-----------|----------|----------------|
| CLI       | [ ]      | `ppds [command]` |
| TUI       | [ ]      | [Panel name] |
| RPC       | [ ]      | `[method]` (if extension needs it) |
| MCP       | [ ]      | `ppds_[tool]` (if AI-useful) |
| Extension | [ ]      | [View name] (ports TUI) |

### Acceptance Criteria

- [ ] Application Service implemented with tests
- [ ] CLI command implemented
- [ ] TUI panel implemented (if in scope)
- [ ] MCP tool implemented (if in scope)
- [ ] Documentation updated

### Labels
- `enhancement`
- `interface:cli` (if CLI in scope)
- `interface:tui` (if TUI in scope)
- `interface:mcp` (if MCP in scope)
- `interface:extension` (if extension in scope)

### Milestone
[Appropriate milestone]
```

---

## Interface Selection Guide

### Always Include
- **CLI** - Every feature needs CLI exposure

### Include When
- **TUI** - Feature has interactive use case
- **MCP** - AI analysis adds value:
  - Queries and data exploration
  - Debugging and troubleshooting
  - Metadata and schema inspection
- **RPC** - Extension needs data not available via existing RPC
- **Extension** - After TUI pattern established

### Exclude From MCP
- Destructive operations (delete, truncate)
- Bulk mutations (import, update, deploy)
- Credential management
- Security changes

---

## Development Order (ADR-0027)

```
1. Application Service    → Business logic
2. CLI Command            → Exposes service
3. TUI Panel              → Reference UI
4. RPC Method             → If extension needs it
5. MCP Tool               → If AI-useful
6. Extension View         → Ports TUI patterns
```

---

## Example: Plugin History Feature

```markdown
## Feature: Plugin History

**Description:** View historical plugin executions and performance trends over time.

**Application Service:** `IPluginHistoryService`

### Interface Matrix

| Interface | In Scope | Implementation |
|-----------|----------|----------------|
| CLI       | [x]      | `ppds plugins history --assembly Foo --days 7` |
| TUI       | [x]      | Plugin History Panel with timeline chart |
| RPC       | [ ]      | Uses existing plugin-traces RPC |
| MCP       | [x]      | `ppds_plugins_history` (trend analysis) |
| Extension | [x]      | Plugin History View (ports TUI timeline) |

### Acceptance Criteria

- [ ] IPluginHistoryService with GetHistoryAsync, GetTrendsAsync
- [ ] CLI command with --assembly, --days, --format options
- [ ] TUI panel with timeline visualization
- [ ] MCP tool for AI trend analysis
- [ ] Extension view porting TUI timeline

### Labels
- `enhancement`
- `interface:cli`
- `interface:tui`
- `interface:mcp`
- `interface:extension`

### Milestone
v1.1
```

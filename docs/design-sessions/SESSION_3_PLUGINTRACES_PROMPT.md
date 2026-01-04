# Design Session 3: Plugin Traces

## Session Prompt

Use this prompt to start the design session:

---

Design session for Plugin Traces CLI command (`ppds plugintraces`).

## Context

The extension has a comprehensive Plugin Trace Viewer with advanced filtering, timeline correlation, and trace level management. We need CLI parity.

### Extension Features (57 files)
- **Filtering:** 25 filter fields, 11 operators, 8 quick filters
- **Quick filters:** Exceptions, Last Hour, Last 24h, Today, Async Only, Sync Only, Recursive
- **Detail panel:** Overview, Details, Timeline, Raw Data tabs
- **Timeline:** Related traces by correlation ID showing execution pipeline
- **Actions:** Export CSV/JSON, Delete (selected/all/old), Trace level management
- **Trace levels:** Off (0), Exception (1), All (2)

### Key Design Challenges
1. **Expensive fields:** `messageblock`, `configuration`, `secureconfiguration`, `profile` must be excluded from list, included in get
2. **Filtering complexity:** 25 fields with various operators - need inline flags + filter file
3. **Timeline view:** Query related traces by correlation ID
4. **Trace level:** Read/write organization setting

### Current SDK Status
- No `ppds plugintraces` command
- Entity: `plugintracelog`

## Deliverables

1. **Full command structure** with all subcommands and flags
2. **Filter design:**
   - Inline flags for common filters
   - Quick filter shortcuts
   - Filter file schema (JSON)
3. **JSON output schemas** for list vs get (selective field loading)
4. **Timeline correlation query** pattern
5. **Issue breakdown** for implementation

## Expected Commands

```bash
# List with inline filters
ppds plugintraces list [--plugin <name>] [--entity <name>] [--message <name>]
                       [--level exception|all] [--mode sync|async]
                       [--since <datetime>] [--until <datetime>]
                       [--min-duration <ms>] [--correlation <guid>]
                       [--depth <n>] [--top <n>] [--orderby <field>]

# Quick filter shortcuts
ppds plugintraces list --exceptions
ppds plugintraces list --last-hour
ppds plugintraces list --last-24h
ppds plugintraces list --today
ppds plugintraces list --async-only
ppds plugintraces list --sync-only
ppds plugintraces list --recursive     # depth > 1

# Filter file for complex queries
ppds plugintraces list --filter-file traces-filter.json

# Get single trace (all fields)
ppds plugintraces get <id>

# Timeline view (related traces by correlation)
ppds plugintraces related <correlation-id>

# Delete operations
ppds plugintraces delete --older-than 30d [--batch-size 100]
ppds plugintraces delete --all [--confirm]

# Trace level management
ppds plugintraces settings                        # Get current
ppds plugintraces settings --level off|exception|all

# Export
ppds plugintraces export [--format csv|json] [filters...]
```

## Filter File Schema

```json
{
  "$schema": "https://ppds.dev/schemas/plugintrace-filter.json",
  "conditions": [
    { "field": "typename", "operator": "contains", "value": "MyPlugin" },
    { "field": "performanceexecutionduration", "operator": "gt", "value": 1000 }
  ],
  "logicalOperator": "and"
}
```

## References

- [ROADMAP.md](../ROADMAP.md) - Phase 3 overview
- [ADR-0009](../adr/0009_CLI_COMMAND_TAXONOMY.md) - Why `plugintraces` not `traces`
- [ADR-0008](../adr/0008_CLI_OUTPUT_ARCHITECTURE.md) - Output architecture
- Extension source: `C:\VS\ppds\extension\src\features\pluginTraceViewer`

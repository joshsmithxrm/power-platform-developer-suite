# Design Session 2: Flows + Connection References + Deployment Settings

## Session Prompt

Use this prompt to start the design session:

---

Design session for Flows, Connection References, Connections, and Deployment Settings CLI commands.

## Context

The extension has a Connection References panel that shows flows and their connection references. We need CLI commands to support this and enable deployment settings generation.

### Entity Hierarchy
```
Flow (cloudflow/workflow)
  └─► ConnectionReference (1:N)
        └─► Connection (1:1, but multiple valid options exist)
```

### Extension Features
- Solution selector
- Flow links (open in Maker)
- Sync deployment settings button (generates Microsoft-format deployment settings file)
- Unified view of flows with their connection references

### Current SDK Status
- No `ppds flows` command
- No `ppds connrefs` command
- No `ppds connections` command
- No `ppds deployment-settings` command

### Key Questions
1. What Dataverse queries are needed to get flow → connref → connection relationships?
2. How do we detect orphaned connection references?
3. What valid connections exist for a given connection reference?
4. Should we use Microsoft deployment settings format or our own?

## Deliverables

1. **Entity relationship model** with Dataverse queries
2. **Command structure:**
   - `ppds flows` (list, get, url)
   - `ppds connrefs` (list, get, flows, connections, analyze)
   - `ppds connections` (list, get)
   - `ppds deployment-settings` (generate, validate)
3. **ADR on deployment settings format** (Microsoft vs custom)
4. **JSON output schemas** for each command
5. **Issue breakdown** for implementation

## Expected Commands

```bash
# Flows
ppds flows list [--solution <name>]
ppds flows get <id>
ppds flows url <id>

# Connection References
ppds connrefs list [--solution <name>] [--orphaned]
ppds connrefs get <id>
ppds connrefs flows <id>         # Which flows use this ref?
ppds connrefs connections <id>   # Valid connections for this ref
ppds connrefs analyze [--solution <name>]  # Unified view

# Connections
ppds connections list [--connector <name>]
ppds connections get <id>

# Deployment Settings
ppds deployment-settings generate [--solution <name>] [--output <path>]
ppds deployment-settings validate <path>
```

## References

- [ROADMAP.md](../ROADMAP.md) - Phase 2 overview
- [ADR-0009](../adr/0009_CLI_COMMAND_TAXONOMY.md) - Command naming conventions
- [ADR-0008](../adr/0008_CLI_OUTPUT_ARCHITECTURE.md) - Output architecture
- Extension source: `C:\VS\ppds\extension\src\features\connectionReferences`

# ADR-0018: MCP Server Architecture

**Status:** Accepted
**Date:** 2026-01-07
**Authors:** Josh, Claude

## Context

We need Claude Code and other AI tools to intelligently interact with PPDS/Dataverse. Several integration approaches were considered:

| Approach | Description |
|----------|-------------|
| CLI invokes Claude API | ppds embeds Anthropic API calls |
| Claude invokes ppds via Bash | Claude Code shells out to `ppds` commands |
| MCP Server | Standard protocol for AI tool integration |
| Agent SDK embedded | ppds hosts an autonomous agent |

### Requirements

- Claude should be able to query Dataverse data
- Claude should be able to debug plugin issues
- Claude should understand entity metadata
- Integration should work with any MCP-compatible AI client
- Destructive operations should be excluded

## Decision

Build an MCP (Model Context Protocol) server as a separate binary (`ppds-mcp-server`) in `src/PPDS.Mcp/`.

### Architecture

```
Claude Code → MCP Protocol → ppds-mcp-server → Application Services
           (JSON-RPC 2.0        (stdio)
            over stdio)
```

### Why MCP?

1. **Standard protocol** - MCP is the emerging standard for AI tool integration
2. **Existing infrastructure** - ppds already has JSON-RPC infrastructure (`ppds serve`)
3. **Clean separation** - Claude handles reasoning, ppds provides domain expertise
4. **Multi-client support** - Works with Claude Code, VS Code Copilot, any MCP client
5. **Tool discovery** - AI clients discover capabilities via `tools/list`

### Tool Selection Criteria

| Include | Exclude |
|---------|---------|
| Read operations | Destructive operations (delete, truncate) |
| Queries (SQL, FetchXML) | Bulk mutations (import, update) |
| Analysis and debugging | Credential management |
| Metadata exploration | Security changes (role assignment) |

**Principle:** Read-heavy, write-light. Claude gathers and analyzes, humans approve and execute changes.

### Prototype Scope (12 Tools)

**Context:**
- `ppds_auth_who` - Current profile/environment
- `ppds_env_list` - Available environments
- `ppds_env_select` - Switch environment
- `ppds_data_schema` - Entity structure

**Query & Analysis:**
- `ppds_query_sql` - Execute SQL queries
- `ppds_query_fetch` - Execute FetchXML
- `ppds_data_analyze` - Data quality analysis

**Plugin Debugging:**
- `ppds_plugin_traces_list` - Get trace logs
- `ppds_plugin_traces_get` - Specific trace details
- `ppds_plugin_traces_timeline` - Execution timeline

**Metadata:**
- `ppds_metadata_entity` - Entity details
- `ppds_plugins_list` - Plugin inventory

## Consequences

### Positive

- **Standard integration** - Any MCP client can use ppds tools
- **Leverages existing code** - Reuses Application Services
- **Safe by design** - Read-only tools prevent accidental damage
- **Discoverable** - AI clients learn available capabilities dynamically
- **Extensible** - New tools can be added as features are built

### Negative

- **New project** - Additional binary to build and release
- **Protocol dependency** - Tied to MCP protocol evolution

### Neutral

- **Separate from `ppds serve`** - Different protocol (MCP vs custom JSON-RPC)
- **Authentication** - Uses same profile system as CLI

## Implementation

### Project Structure

```
src/PPDS.Mcp/
├── PPDS.Mcp.csproj
├── Program.cs              # Entry point with MCP hosting
├── McpServer.cs            # Server configuration
└── Tools/
    ├── AuthTools.cs        # ppds_auth_who
    ├── EnvTools.cs         # ppds_env_list, ppds_env_select
    ├── QueryTools.cs       # ppds_query_sql, ppds_query_fetch
    ├── PluginTraceTools.cs # Plugin debugging tools
    └── MetadataTools.cs    # ppds_metadata_entity, etc.
```

### User Setup

```bash
# Add ppds MCP server to Claude Code
claude mcp add --transport stdio ppds -- ppds-mcp-server

# Or via configuration file (.mcp.json)
{
  "mcpServers": {
    "ppds": {
      "type": "stdio",
      "command": "ppds-mcp-server"
    }
  }
}
```

### Integration with Existing ADRs

- **ADR-0015** (Application Services) - MCP tools call services directly
- **ADR-0025** (Progress Reporting) - Long operations use `IProgressReporter`
- **ADR-0026** (Structured Errors) - Errors wrapped in `PpdsException`

## References

- [Model Context Protocol Specification](https://modelcontextprotocol.io)
- [MCP C# SDK](https://github.com/modelcontextprotocol/csharp-sdk)
- Issue #281 - MCP Server Implementation

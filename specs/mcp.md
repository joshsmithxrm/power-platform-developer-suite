# MCP Server

**Status:** Implemented
**Version:** 1.0
**Last Updated:** 2026-01-21
**Code:** [src/PPDS.Mcp/](../src/PPDS.Mcp/)

---

## Overview

PPDS MCP Server (`ppds-mcp-server`) exposes Power Platform/Dataverse capabilities through the Model Context Protocol (MCP), enabling AI assistants like Claude Code to query data, analyze plugins, and explore metadata. The server follows a read-heavy, write-light principle: AI gathers and analyzes information, humans approve and execute changes.

### Goals

- **AI-friendly Dataverse access**: Enable Claude Code and MCP-compatible clients to query and analyze Dataverse
- **Plugin debugging support**: Trace log retrieval, execution timeline visualization, error analysis
- **Safe by design**: Read-only operations by default, no destructive mutations exposed

### Non-Goals

- Write operations (create, update, delete records)
- Credential management via MCP (use CLI for profile setup)
- Administrative operations (solution deployment, security role assignment)

---

## Architecture

```
┌─────────────────────────────────────────────────────────────────────────┐
│                           MCP Client (Claude Code)                       │
└────────────────────────────────────┬────────────────────────────────────┘
                                     │
                          JSON-RPC 2.0 over stdio
                                     │
                                     ▼
┌─────────────────────────────────────────────────────────────────────────┐
│                          ppds-mcp-server                                 │
│  ┌─────────────────────────────────────────────────────────────────┐   │
│  │                         MCP Tools                                │   │
│  │  ┌────────────┐ ┌────────────┐ ┌────────────┐ ┌────────────┐   │   │
│  │  │ AuthWho    │ │ EnvList    │ │ QuerySql   │ │ Plugins    │   │   │
│  │  │ EnvSelect  │ │ DataSchema │ │ QueryFetch │ │ Traces     │   │   │
│  │  │ DataAnalyze│ │ Metadata   │ │            │ │ Timeline   │   │   │
│  │  └────────────┘ └────────────┘ └────────────┘ └────────────┘   │   │
│  └─────────────────────────────────┬───────────────────────────────┘   │
│                                    │                                    │
│  ┌─────────────────────────────────▼───────────────────────────────┐   │
│  │                     McpToolContext                               │   │
│  │  • Profile resolution                                            │   │
│  │  • Connection pool management                                    │   │
│  │  • Service provider creation                                     │   │
│  └─────────────────────────────────┬───────────────────────────────┘   │
│                                    │                                    │
│  ┌─────────────────────────────────▼───────────────────────────────┐   │
│  │                 McpConnectionPoolManager                         │   │
│  │  • Cached pools keyed by profile+environment                     │   │
│  │  • Lazy<Task<T>> pattern prevents duplicate creation             │   │
│  │  • Invalidation on profile/environment changes                   │   │
│  └─────────────────────────────────┬───────────────────────────────┘   │
│                                    │                                    │
└────────────────────────────────────┼────────────────────────────────────┘
                                     │
                    ┌────────────────┼────────────────┐
                    │                │                │
                    ▼                ▼                ▼
             ┌──────────┐    ┌──────────────┐  ┌──────────────┐
             │ PPDS.Auth│    │PPDS.Dataverse│  │PPDS.Migration│
             │ Profiles │    │ Pool, Query  │  │ (future use) │
             └──────────┘    └──────────────┘  └──────────────┘
```

Tools are auto-discovered via `[McpServerToolType]` attribute and registered by `WithToolsFromAssembly()`.

### Components

| Component | Responsibility |
|-----------|----------------|
| **Program.cs** | Entry point, host configuration, stdout→stderr redirect |
| **McpToolContext** | Shared context for profile access, pool acquisition |
| **McpConnectionPoolManager** | Cached connection pools with lazy initialization |
| **ProfileConnectionSourceAdapter** | Bridges PPDS.Auth to PPDS.Dataverse pool interface |
| **Tools/*.cs** | Individual MCP tool implementations |

### Dependencies

- Depends on: [connection-pool.md](./connection-pool.md), [authentication.md](./authentication.md)
- Uses patterns from: [architecture.md](./architecture.md)

---

## Specification

### Core Requirements

1. All output to stdout is reserved for MCP protocol messages
2. Tools are read-only by default (no destructive operations)
3. Environment selection persists to global profile (unlike TUI session isolation)
4. Connection pools are cached and reused across tool invocations
5. Query results are capped at configurable maximums (default 100, max 5000)

### Primary Flows

**Tool Discovery:**

1. **MCP client** sends `tools/list` request
2. **Server** returns all `[McpServerToolType]` classes
3. **Client** receives tool schemas with parameter descriptions

**Tool Execution:**

1. **Client** sends `tools/call` with tool name and parameters
2. **Server** resolves active profile via `McpToolContext`
3. **Server** acquires connection pool from cache
4. **Tool** executes against Dataverse
5. **Server** returns JSON result

**Environment Switching:**

1. **Client** calls `ppds_env_select` with environment identifier
2. **Server** resolves environment via multi-layer resolution
3. **Server** updates global profile (persisted)
4. **Server** invalidates old environment's cached pool
5. **Subsequent tools** use new environment

### Constraints

- All logging must go to stderr (stdout reserved for MCP protocol)
- Pool creation timeout: 5 minutes (allows for device code flow)
- Maximum query rows: 5000 (prevents runaway queries)
- Tool names prefixed with `ppds_` for namespace isolation

---

## Core Types

### McpToolContext

Shared context ([`McpToolContext.cs:24-229`](../src/PPDS.Mcp/Infrastructure/McpToolContext.cs#L24-L229)) providing profile resolution and service access for all tools.

```csharp
public sealed class McpToolContext
{
    Task<AuthProfile> GetActiveProfileAsync(CancellationToken ct);
    Task<IDataverseConnectionPool> GetPoolAsync(CancellationToken ct);
    Task<ServiceProvider> CreateServiceProviderAsync(CancellationToken ct);
}
```

Tools inject `McpToolContext` via constructor and use it to access Dataverse services.

### IMcpConnectionPoolManager

Pool management interface ([`IMcpConnectionPoolManager.cs:10-38`](../src/PPDS.Mcp/Infrastructure/IMcpConnectionPoolManager.cs#L10-L38)) for cached connection pools.

```csharp
public interface IMcpConnectionPoolManager : IAsyncDisposable
{
    Task<IDataverseConnectionPool> GetOrCreatePoolAsync(
        IReadOnlyList<string> profileNames,
        string environmentUrl,
        Action<DeviceCodeInfo>? deviceCodeCallback = null,
        CancellationToken ct = default);
    void InvalidateProfile(string profileName);
    void InvalidateEnvironment(string environmentUrl);
}
```

### Usage Pattern

```csharp
[McpServerToolType]
public sealed class MyTool
{
    private readonly McpToolContext _context;

    public MyTool(McpToolContext context) => _context = context;

    [McpServerTool(Name = "ppds_my_tool")]
    [Description("Tool description for AI clients")]
    public async Task<MyResult> ExecuteAsync(
        [Description("Parameter description")] string param,
        CancellationToken ct)
    {
        await using var sp = await _context.CreateServiceProviderAsync(ct);
        var service = sp.GetRequiredService<IMyService>();
        return await service.DoWorkAsync(param, ct);
    }
}
```

---

## API/Contracts

### Tool Inventory

| Tool | Category | Description |
|------|----------|-------------|
| `ppds_auth_who` | Context | Current profile, identity, token status |
| `ppds_env_list` | Context | Available environments with active indicator |
| `ppds_env_select` | Context | Switch environment (persists globally) |
| `ppds_data_schema` | Schema | Entity attributes and types |
| `ppds_data_analyze` | Analysis | Record counts, samples, statistics |
| `ppds_query_sql` | Query | SQL SELECT with transpilation to FetchXML |
| `ppds_query_fetch` | Query | Raw FetchXML execution |
| `ppds_metadata_entity` | Metadata | Full entity metadata with relationships |
| `ppds_plugins_list` | Plugins | Registered assemblies and types |
| `ppds_plugin_traces_list` | Debugging | Plugin trace log listing |
| `ppds_plugin_traces_get` | Debugging | Trace details with message block |
| `ppds_plugin_traces_timeline` | Debugging | Hierarchical execution timeline |

### Request/Response Examples

**ppds_query_sql**

Request:
```json
{
  "sql": "SELECT name, revenue FROM account WHERE statecode = 0 ORDER BY revenue DESC",
  "maxRows": 100
}
```

Response:
```json
{
  "entityName": "account",
  "columns": [
    {"logicalName": "name", "dataType": "String"},
    {"logicalName": "revenue", "dataType": "Money"}
  ],
  "records": [
    {"name": "Contoso", "revenue": {"value": 1000000, "formatted": "$1,000,000.00"}}
  ],
  "count": 1,
  "moreRecords": false,
  "executedFetchXml": "<fetch top=\"100\">...</fetch>",
  "executionTimeMs": 145
}
```

**ppds_plugin_traces_timeline**

Request:
```json
{
  "correlationId": "a1b2c3d4-e5f6-7890-abcd-ef1234567890"
}
```

Response:
```json
{
  "correlationId": "a1b2c3d4-e5f6-7890-abcd-ef1234567890",
  "nodes": [
    {
      "id": "...",
      "typeName": "MyPlugin.AccountCreate",
      "messageName": "Create",
      "primaryEntity": "account",
      "mode": "Synchronous",
      "depth": 1,
      "durationMs": 250,
      "hasException": false,
      "hierarchyDepth": 0,
      "offsetPercent": 0,
      "widthPercent": 100,
      "children": [
        {
          "typeName": "MyPlugin.ContactCreate",
          "depth": 2,
          "durationMs": 50,
          "children": []
        }
      ]
    }
  ],
  "totalNodes": 2
}
```

---

## Error Handling

### Error Types

| Error | Condition | Recovery |
|-------|-----------|----------|
| `InvalidOperationException` | No active profile | Run `ppds auth create` |
| `InvalidOperationException` | No environment selected | Run `ppds env select` |
| `ArgumentException` | Missing required parameter | Provide parameter value |
| `TimeoutException` | Pool creation timeout | Check network, retry |
| `SqlParseException` | Invalid SQL syntax | Fix SQL query |

### Recovery Strategies

- **No profile**: User must configure profile via CLI before using MCP tools
- **No environment**: Use `ppds_env_select` to choose environment
- **Auth expired**: Tools will trigger token refresh automatically
- **Throttling**: Connection pool handles retry with backoff

### Edge Cases

| Scenario | Expected Behavior |
|----------|-------------------|
| Query returns 0 rows | Return empty `records` array, `count: 0` |
| Trace not found | Throw `InvalidOperationException` with clear message |
| Invalid GUID format | Throw `ArgumentException` explaining expected format |
| SQL with no TOP clause | Inject `top="100"` (or user-specified maxRows) |

---

## Design Decisions

### Why MCP Protocol?

**Context:** Multiple approaches exist for AI tool integration - CLI invocation, embedded API calls, custom JSON-RPC.

**Decision:** Build an MCP server following the Model Context Protocol standard.

**Alternatives considered:**
- Claude invokes `ppds` via Bash: Rejected - brittle parsing, no structured data
- Embedded Anthropic API: Rejected - couples ppds to specific AI vendor
- Custom JSON-RPC: Rejected - non-standard, extra client implementation needed

**Consequences:**
- Positive: Works with any MCP-compatible client (Claude Code, VS Code Copilot)
- Positive: Tool discovery via standard `tools/list`
- Negative: Tied to MCP protocol evolution

### Why Read-Heavy Tool Selection?

**Context:** Dataverse operations span reads, writes, and destructive actions. AI tools with write access create risk.

**Decision:** Expose only read operations by default. Claude gathers information, humans execute changes via CLI.

**Tool categories:**
| Include | Exclude |
|---------|---------|
| Read operations | Destructive operations (delete, truncate) |
| Queries (SQL, FetchXML) | Bulk mutations (import, update) |
| Analysis and debugging | Credential management |
| Metadata exploration | Security changes (role assignment) |

**Consequences:**
- Positive: Safe for exploration, can't accidentally delete data
- Negative: Multi-step workflows require user intervention

### Why Stdout Redirect?

**Context:** MCP uses stdout for JSON-RPC protocol messages. Any non-protocol output corrupts the stream.

**Decision:** Redirect `Console.Out` to `Console.Error` at startup ([`Program.cs:10`](../src/PPDS.Mcp/Program.cs#L10)).

```csharp
// MCP servers MUST NOT write to stdout (reserved for protocol).
Console.SetOut(Console.Error);
```

**Consequences:**
- Positive: All console output (logging, errors) goes to stderr
- Positive: MCP protocol stream stays clean
- Negative: Must remember this pattern in all tool implementations

### Why Cached Connection Pools?

**Context:** Creating Dataverse connections is expensive. MCP tools may be called frequently within a session.

**Decision:** Cache connection pools keyed by profile+environment combination. Use `Lazy<Task<T>>` to prevent duplicate creation races.

**Cache key generation** ([`McpConnectionPoolManager.cs:207-212`](../src/PPDS.Mcp/Infrastructure/McpConnectionPoolManager.cs#L207-L212)):
```csharp
internal static string GenerateCacheKey(IReadOnlyList<string> profileNames, string environmentUrl)
{
    var sortedProfiles = string.Join(",", profileNames.OrderBy(p => p, StringComparer.OrdinalIgnoreCase));
    var normalizedUrl = NormalizeUrl(environmentUrl);
    return $"{sortedProfiles}|{normalizedUrl}";
}
```

**Consequences:**
- Positive: Fast tool execution after initial connection
- Positive: Pool reuse across tool invocations
- Negative: Memory footprint for long-running sessions

### Why Global Environment Persistence?

**Context:** TUI uses session-only environment switching (ADR-0018). MCP tools need persistence across invocations.

**Decision:** `ppds_env_select` updates the global profile in `profiles.json`, unlike TUI which is session-only.

**Rationale:** MCP server may restart between tool calls. Without persistence, every new session would require re-selection.

**Consequences:**
- Positive: Environment selection survives MCP server restarts
- Negative: MCP environment change affects CLI default

---

## Extension Points

### Adding a New MCP Tool

1. **Create tool class** in `src/PPDS.Mcp/Tools/`:
   ```csharp
   [McpServerToolType]
   public sealed class MyNewTool
   {
       private readonly McpToolContext _context;

       public MyNewTool(McpToolContext context) => _context = context;

       [McpServerTool(Name = "ppds_my_new_tool")]
       [Description("Clear description for AI clients explaining what this tool does and when to use it.")]
       public async Task<MyResult> ExecuteAsync(
           [Description("Parameter purpose and example values")]
           string requiredParam,
           [Description("Optional parameter with default")]
           int maxRows = 100,
           CancellationToken ct = default)
       {
           // Implementation
       }
   }
   ```

2. **Tool is auto-discovered** by `WithToolsFromAssembly()` - no registration needed

3. **Follow conventions:**
   - Name: `ppds_{category}_{action}` (e.g., `ppds_data_analyze`)
   - Description: Explain purpose and usage context for AI
   - Parameters: Use `[Description]` on all parameters
   - Return type: Dedicated result class with `[JsonPropertyName]`

### Adding a Result Type

1. **Create sealed class** with JSON serialization attributes:
   ```csharp
   public sealed class MyResult
   {
       [JsonPropertyName("fieldName")]
       public string FieldName { get; set; } = "";

       [JsonPropertyName("optionalField")]
       [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
       public string? OptionalField { get; set; }
   }
   ```

2. **Place in same file as tool** or in `QueryResultTypes.cs` if shared

---

## Configuration

### User Setup

```bash
# Install MCP server as .NET tool
dotnet tool install -g PPDS.Mcp

# Add to Claude Code
claude mcp add --transport stdio ppds -- ppds-mcp-server
```

Or via `.mcp.json`:
```json
{
  "mcpServers": {
    "ppds": {
      "type": "stdio",
      "command": "ppds-mcp-server"
    }
  }
}
```

### Prerequisites

| Requirement | Setup |
|-------------|-------|
| Active profile | `ppds auth create` |
| Selected environment | `ppds env select <url>` or use `ppds_env_select` tool |
| Trace logging (for debug tools) | Enable in environment settings |

---

## Testing

### Acceptance Criteria

- [ ] All tools return structured JSON (no raw strings)
- [ ] Query tools respect maxRows limit
- [ ] Environment selection persists across MCP server restarts
- [ ] Pool invalidation triggers on profile/environment change
- [ ] No stdout output except MCP protocol messages

### Edge Cases

| Scenario | Input | Expected Output |
|----------|-------|-----------------|
| Empty query result | SQL returning 0 rows | `{ "records": [], "count": 0 }` |
| Large result set | Query without TOP | Auto-inject `top="100"` |
| Invalid trace ID | Non-GUID string | `ArgumentException` with format hint |
| Expired token | Any tool call | Auto-refresh via MSAL |

### Test Examples

```csharp
[Fact]
public void GenerateCacheKey_SortsProfileNamesAlphabetically()
{
    var key1 = McpConnectionPoolManager.GenerateCacheKey(
        new[] { "profile-b", "profile-a" },
        "https://contoso.crm.dynamics.com");
    var key2 = McpConnectionPoolManager.GenerateCacheKey(
        new[] { "profile-a", "profile-b" },
        "https://contoso.crm.dynamics.com/");

    Assert.Equal(key1, key2);
}

[Fact]
public void InjectTopAttribute_AddsTopWhenMissing()
{
    var input = "<fetch><entity name=\"account\"/></fetch>";
    var result = QueryFetchTool.InjectTopAttribute(input, 100);

    Assert.Contains("top=\"100\"", result);
}
```

---

## Related Specs

- [architecture.md](./architecture.md) - Multi-interface platform design
- [connection-pool.md](./connection-pool.md) - Pool management reused by MCP
- [authentication.md](./authentication.md) - Profile system shared with CLI/TUI
- [error-handling.md](./error-handling.md) - Exception patterns (not yet using PpdsException)

---

## Roadmap

- Additional analysis tools (data quality scoring, dependency mapping)
- Write operation tools with confirmation prompts (future)
- Resource exposure for entity/attribute lists
- Prompt templates for common debugging workflows

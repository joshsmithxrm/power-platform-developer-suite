# MCP Server

**Status:** Implemented
**Version:** 1.0
**Last Updated:** 2026-01-23
**Code:** [src/PPDS.Mcp/](../src/PPDS.Mcp/)

---

## Overview

The MCP (Model Context Protocol) server exposes Power Platform/Dataverse capabilities to AI assistants like Claude Code. It provides 13 tools for authentication, environment management, schema discovery, query execution, and plugin debugging—enabling natural language interaction with Dataverse through the standardized MCP protocol over stdio transport.

### Goals

- **AI Integration**: Enable AI assistants to query and analyze Dataverse data naturally
- **Full Dataverse Access**: Expose metadata, querying, and plugin diagnostics through MCP tools
- **Performance**: Maintain long-lived connection pools across tool invocations

### Non-Goals

- HTTP transport (stdio only for security in IDE contexts)
- Write operations to Dataverse (read-only tools)
- Custom tool registration at runtime (tools are statically discovered)

---

## Architecture

```
┌──────────────────────────────────────────────────────────────────────────┐
│                         MCP Client (Claude Code, etc.)                    │
│                              stdin/stdout                                 │
└────────────────────────────────┬─────────────────────────────────────────┘
                                 │ MCP Protocol (JSON-RPC)
                                 ▼
┌──────────────────────────────────────────────────────────────────────────┐
│                    MCP Server (ModelContextProtocol.Server)               │
│  ┌────────────────────────────────────────────────────────────────────┐  │
│  │                 Tool Discovery & Dispatch                           │  │
│  │  [McpServerToolType] classes auto-discovered via reflection         │  │
│  │  [McpServerTool(Name = "ppds_*")] methods invoked per request       │  │
│  └────────────────────────────────────────────────────────────────────┘  │
│                                 │                                         │
│  ┌──────────────────────────────┴────────────────────────────────────┐   │
│  │                     McpToolContext                                 │   │
│  │  ┌──────────────┐ ┌──────────────────┐ ┌───────────────────────┐  │   │
│  │  │ GetActive    │ │ GetPoolAsync     │ │ CreateServiceProvider │  │   │
│  │  │ ProfileAsync │ │ (cached pools)   │ │ Async (full DI)       │  │   │
│  │  └──────────────┘ └────────┬─────────┘ └───────────────────────┘  │   │
│  └────────────────────────────┼──────────────────────────────────────┘   │
│                               │                                           │
│  ┌────────────────────────────┴──────────────────────────────────────┐   │
│  │              IMcpConnectionPoolManager                             │   │
│  │  ConcurrentDictionary<"profiles|url", Lazy<Task<CachedPoolEntry>>>│   │
│  │  5-minute timeout for device code flow                             │   │
│  └────────────────────────────┬──────────────────────────────────────┘   │
│                               │                                           │
└──────────────────────────────────────────────────────────────────────────┘
                                │
                                ▼
┌──────────────────────────────────────────────────────────────────────────┐
│                    PPDS.Dataverse + PPDS.Auth                             │
│  IDataverseConnectionPool, IQueryExecutor, IMetadataService, etc.        │
└──────────────────────────────────────────────────────────────────────────┘
```

### Components

| Component | Responsibility |
|-----------|----------------|
| `Program.cs` | Entry point, DI registration, stdout→stderr redirection |
| `IMcpConnectionPoolManager` | Caches connection pools by profile+environment key |
| `McpConnectionPoolManager` | Race-safe pool creation with `Lazy<Task<T>>` pattern |
| `McpToolContext` | Unified context for tools: profiles, pools, service provider |
| `ProfileConnectionSourceAdapter` | Bridges `ProfileConnectionSource` → `IConnectionSource` |
| 13 Tool classes | Individual MCP tool implementations |

### Dependencies

- Depends on: [authentication.md](./authentication.md) (profiles, credential providers)
- Depends on: [connection-pooling.md](./connection-pooling.md) (IDataverseConnectionPool, pool patterns)
- Depends on: [query.md](./query.md) (IQueryExecutor, SQL transpilation)

---

## Specification

### Core Requirements

1. **Stdout reserved for MCP protocol**: All console output redirected to stderr before any logging
2. **Auto-discovery of tools**: Classes with `[McpServerToolType]` discovered at startup
3. **Long-lived pools**: Connection pools persist across tool invocations within a session
4. **Profile-environment isolation**: Separate pools for each profile+environment combination

### Primary Flows

**Tool Invocation:**

1. **Request received**: MCP client sends JSON-RPC request over stdin
2. **Tool dispatched**: Framework matches tool name to `[McpServerTool]` method
3. **Context accessed**: Tool uses `McpToolContext` to get profile/pool/services
4. **Operation executed**: Tool calls Dataverse services via DI
5. **Response returned**: Result serialized as JSON via stdout

**Pool Creation:**

1. **First tool request**: `GetPoolAsync()` called for profile+environment
2. **Cache miss**: `Lazy<Task<CachedPoolEntry>>` created and added to dictionary
3. **Pool built**: ProfileConnectionSource created, wrapped, registered with DI
4. **Cached**: Subsequent requests return same pool immediately
5. **Timeout**: 5-minute timeout allows device code authentication

### Constraints

- Maximum pool size: 52 connections per Application User
- Pool creation timeout: 5 minutes (device code flow)
- Tool results must be JSON-serializable
- All tools must be stateless across invocations

### Validation Rules

| Parameter | Rule | Error |
|-----------|------|-------|
| `sql` (QuerySqlTool) | Non-empty string | "The 'sql' parameter is required" |
| `fetchXml` (QueryFetchTool) | Non-empty string | "The 'fetchXml' parameter is required" |
| `maxRows` | 1-5000 | Clamped silently |
| `environmentUrl` | Non-empty, valid URL | "Environment URL is required" |
| Active profile | Must exist | "No active profile configured" |
| Selected environment | Must be set | "No environment selected" |

---

## Core Types

### IMcpConnectionPoolManager

Manages cached connection pools keyed by profile+environment combination ([`IMcpConnectionPoolManager.cs:10-38`](../src/PPDS.Mcp/Infrastructure/IMcpConnectionPoolManager.cs#L10-L38)).

```csharp
public interface IMcpConnectionPoolManager : IAsyncDisposable
{
    Task<IDataverseConnectionPool> GetOrCreatePoolAsync(
        IReadOnlyList<string> profileNames,
        string environmentUrl,
        Action<DeviceCodeInfo>? deviceCodeCallback = null,
        CancellationToken cancellationToken = default);

    void InvalidateProfile(string profileName);
    void InvalidateEnvironment(string environmentUrl);
}
```

### McpToolContext

Shared context injected into all tools providing access to profiles and pools ([`McpToolContext.cs:24-229`](../src/PPDS.Mcp/Infrastructure/McpToolContext.cs#L24-L229)).

```csharp
public sealed class McpToolContext
{
    Task<AuthProfile> GetActiveProfileAsync(CancellationToken ct);
    Task<IDataverseConnectionPool> GetPoolAsync(CancellationToken ct);
    Task<ServiceProvider> CreateServiceProviderAsync(CancellationToken ct);
    void InvalidateEnvironment(string environmentUrl);
}
```

### Usage Pattern

```csharp
[McpServerToolType]
public sealed class ExampleTool
{
    private readonly McpToolContext _context;

    public ExampleTool(McpToolContext context) => _context = context;

    [McpServerTool(Name = "ppds_example")]
    public async Task<ExampleResult> ExecuteAsync(
        [Description("parameter description")] string param,
        CancellationToken ct = default)
    {
        await using var sp = await _context.CreateServiceProviderAsync(ct);
        var service = sp.GetRequiredService<IExampleService>();
        return await service.DoWorkAsync(param, ct);
    }
}
```

---

## API/Contracts

### Available Tools

| Tool | Name | Purpose |
|------|------|---------|
| AuthWhoTool | `ppds_auth_who` | Get current profile, identity, token status |
| EnvListTool | `ppds_env_list` | List accessible Dataverse environments |
| EnvSelectTool | `ppds_env_select` | Select target environment for queries |
| QuerySqlTool | `ppds_query_sql` | Execute SQL queries (transpiled to FetchXML) |
| QueryFetchTool | `ppds_query_fetch` | Execute FetchXML queries directly |
| MetadataEntityTool | `ppds_metadata_entity` | Get entity metadata (forms, views, relationships) |
| DataSchemaTool | `ppds_data_schema` | Get entity attributes with types and constraints |
| DataAnalyzeTool | `ppds_data_analyze` | Analyze entity data (counts, samples) |
| PluginsListTool | `ppds_plugins_list` | List registered plugin assemblies and types |
| PluginTracesListTool | `ppds_plugin_traces_list` | List plugin trace logs with filtering |
| PluginTracesGetTool | `ppds_plugin_traces_get` | Get detailed trace by ID |
| PluginTracesTimelineTool | `ppds_plugin_traces_timeline` | Build execution timeline from correlation ID |

### Example Request/Response

**ppds_query_sql**

Request:
```json
{
    "sql": "SELECT name, revenue FROM account WHERE statecode = 0 ORDER BY revenue DESC",
    "maxRows": 10
}
```

Response:
```json
{
    "entityName": "account",
    "columns": [
        { "logicalName": "name", "dataType": "String" },
        { "logicalName": "revenue", "dataType": "Money" }
    ],
    "records": [
        { "name": "Contoso Ltd", "revenue": { "value": 1000000, "formatted": "$1,000,000.00" } }
    ],
    "count": 1,
    "moreRecords": false,
    "executedFetchXml": "<fetch top=\"10\">...</fetch>",
    "executionTimeMs": 42
}
```

---

## Error Handling

### Error Types

| Error | Condition | Recovery |
|-------|-----------|----------|
| `InvalidOperationException` | No active profile | Run `ppds auth create` |
| `InvalidOperationException` | No environment selected | Run `ppds env select <url>` |
| `TimeoutException` | Pool creation timeout (5 min) | Retry; check device code flow |
| `ArgumentException` | Invalid/missing parameters | Check tool description for requirements |
| `SqlParseException` | Invalid SQL syntax | Fix SQL query |

### Recovery Strategies

- **Profile errors**: User must configure profile via CLI before using MCP
- **Pool timeout**: Check network; device code may require browser interaction
- **Query errors**: Tool returns structured error message for AI to interpret

### Edge Cases

| Scenario | Expected Behavior |
|----------|-------------------|
| Multiple callers request same pool | Second caller awaits first caller's creation |
| Pool creation fails | Failed entry removed from cache; next caller retries |
| Environment changed mid-session | `InvalidateEnvironment()` clears cached pool |
| Profile deleted | `InvalidateProfile()` removes all pools using that profile |

---

## Design Decisions

### Why Stdio Transport?

**Context:** MCP servers can use stdio or HTTP transport.

**Decision:** Use stdio exclusively.

**Rationale:**
- IDE integration: Claude Code runs as subprocess with direct pipe access
- Security: No exposed HTTP port on user machine
- Simplicity: No need for port management, TLS, or authentication

**Consequences:**
- Positive: Zero network configuration required
- Negative: Cannot serve multiple clients simultaneously

### Why Profile-Keyed Pool Caching?

**Context:** Tools need connection pools, but creating pools is expensive (authentication, MSAL token acquisition).

**Decision:** Cache pools by sorted profile names + normalized environment URL.

**Implementation:** The pool manager ([`McpConnectionPoolManager.cs:31`](../src/PPDS.Mcp/Infrastructure/McpConnectionPoolManager.cs#L31)) uses `ConcurrentDictionary<string, Lazy<Task<CachedPoolEntry>>>`.

**Cache key format:** `"{sorted_profiles}|{normalized_url}"` ([`McpConnectionPoolManager.cs:207-212`](../src/PPDS.Mcp/Infrastructure/McpConnectionPoolManager.cs#L207-L212))

**Alternatives considered:**
- Per-request pools: Rejected—too slow (authentication overhead per request)
- Single global pool: Rejected—different profiles may target different environments

**Consequences:**
- Positive: Fast subsequent tool calls (cached pool reuse)
- Negative: Memory usage grows with unique profile+environment combinations

### Why Lazy<Task<T>> for Pool Creation?

**Context:** Multiple concurrent tool calls may request the same pool simultaneously.

**Decision:** Use `Lazy<Task<T>>` with `LazyThreadSafetyMode.ExecutionAndPublication` ([`McpConnectionPoolManager.cs:93-95`](../src/PPDS.Mcp/Infrastructure/McpConnectionPoolManager.cs#L93-L95)).

**Rationale:**
- First caller initiates creation, subsequent callers await same Task
- Prevents duplicate authentication attempts
- Device code callback captured by first caller, shared with others

**Consequences:**
- Positive: Race-safe, single authentication attempt per key
- Negative: First caller's cancellation token cannot cancel creation for others

### Why 5-Minute Pool Creation Timeout?

**Context:** Device code flow requires user to visit URL and authenticate.

**Decision:** Default timeout of 5 minutes ([`McpConnectionPoolManager.cs:29`](../src/PPDS.Mcp/Infrastructure/McpConnectionPoolManager.cs#L29)).

**Rationale:**
- Device code typically expires in 15 minutes
- 5 minutes provides reasonable user experience
- On timeout, cache entry removed for retry

**Consequences:**
- Positive: Allows interactive authentication flows
- Negative: Long-running requests tie up resources

---

## Extension Points

### Adding a New Tool

1. **Create tool class** in `src/PPDS.Mcp/Tools/`
2. **Add attributes**: `[McpServerToolType]` on class, `[McpServerTool]` on method
3. **Inject context**: Constructor takes `McpToolContext`
4. **Define result type**: JSON-serializable class with `[JsonPropertyName]`

**Example skeleton:**

```csharp
[McpServerToolType]
public sealed class NewOperationTool
{
    private readonly McpToolContext _context;

    public NewOperationTool(McpToolContext context)
        => _context = context;

    [McpServerTool(Name = "ppds_new_operation")]
    [Description("Description of what this tool does")]
    public async Task<NewOperationResult> ExecuteAsync(
        [Description("Parameter description")] string param,
        CancellationToken cancellationToken = default)
    {
        await using var sp = await _context.CreateServiceProviderAsync(cancellationToken);
        // Implementation
        return new NewOperationResult { /* ... */ };
    }
}

public sealed class NewOperationResult
{
    [JsonPropertyName("field")]
    public string Field { get; set; } = "";
}
```

---

## Configuration

| Setting | Type | Required | Default | Description |
|---------|------|----------|---------|-------------|
| Profile | File | Yes | `~/.ppds/profiles.json` | Active profile determines credentials |
| Log level | Env | No | Warning | Minimum log level (`PPDS_LOG_LEVEL`) |
| Max pool size | Code | No | 52 | Connections per Application User |
| Pool timeout | Code | No | 5 min | Device code flow timeout |

---

## Testing

### Acceptance Criteria

- [ ] Tool auto-discovery finds all 13 tools at startup
- [ ] Pool reuse: Second query reuses cached pool (no re-authentication)
- [ ] Pool isolation: Different profiles get different pools
- [ ] Invalidation: `InvalidateProfile()` removes correct pools
- [ ] Timeout: Pool creation fails gracefully after 5 minutes
- [ ] Stdout purity: No logging or output corrupts MCP protocol

### Edge Cases

| Scenario | Input | Expected Output |
|----------|-------|-----------------|
| No profile configured | Any tool | InvalidOperationException with guidance |
| SQL syntax error | `ppds_query_sql("SELEC...")` | InvalidOperationException with parse error |
| Empty result set | Valid query, no matches | `{ records: [], count: 0 }` |
| Large result capped | Query returning 10000 rows | Capped to `maxRows` (default 100, max 5000) |

### Test Examples

```csharp
[Fact]
public async Task GetOrCreatePoolAsync_ConcurrentCalls_SingleCreation()
{
    // Arrange
    var creationCount = 0;
    var manager = new McpConnectionPoolManager(
        loadProfilesAsync: async _ =>
        {
            Interlocked.Increment(ref creationCount);
            await Task.Delay(100); // Simulate slow creation
            return CreateTestProfileCollection();
        });

    // Act - 10 concurrent requests for same pool
    var tasks = Enumerable.Range(0, 10)
        .Select(_ => manager.GetOrCreatePoolAsync(
            new[] { "test" }, "https://test.crm.dynamics.com"));
    var pools = await Task.WhenAll(tasks);

    // Assert
    Assert.Equal(1, creationCount);
    Assert.All(pools, p => Assert.Same(pools[0], p));
}
```

---

## Related Specs

- [authentication.md](./authentication.md) - Profile and credential management used by MCP
- [connection-pooling.md](./connection-pooling.md) - Pool infrastructure MCP builds upon
- [query.md](./query.md) - Query execution and SQL transpilation
- [dataverse-services.md](./dataverse-services.md) - Metadata and plugin trace services

---

## Roadmap

- Resource tools for exploring solutions and components
- Write operations (with confirmation prompts)
- Streaming responses for large result sets
- WebSocket transport for multi-client scenarios

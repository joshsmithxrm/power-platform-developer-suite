# PPDS.Mcp: Tools

## Overview

The PPDS.Mcp Tools subsystem provides 12 MCP (Model Context Protocol) tools for AI assistants to interact with Dataverse. Tools are auto-discovered via `[McpServerToolType]` attributes and share infrastructure through `McpToolContext`. The tools cover authentication, environment management, data querying, schema exploration, and plugin debugging.

## Public API

### Tool Discovery Attributes

| Attribute | Purpose |
|-----------|---------|
| `[McpServerToolType]` | Marks class as containing MCP tools |
| `[McpServerTool(Name = "...")]` | Registers method as invokable tool |
| `[Description("...")]` | Provides parameter/tool descriptions for MCP clients |

### Shared Types

| Type | Purpose |
|------|---------|
| `QueryResult` | Standard query result container |
| `QueryColumnInfo` | Column metadata (name, type, alias) |
| `QueryResultMapper` | Maps Dataverse results to MCP-friendly format |

## Tool Catalog

### Authentication & Environment (3 tools)

| Tool | Purpose |
|------|---------|
| `ppds_auth_who` | Get current authentication profile, identity, and token status |
| `ppds_env_list` | List available Dataverse environments |
| `ppds_env_select` | Select environment for subsequent queries |

### Data Querying (4 tools)

| Tool | Purpose |
|------|---------|
| `ppds_query_sql` | Execute SQL queries (transpiled to FetchXML) |
| `ppds_query_fetch` | Execute FetchXML queries directly |
| `ppds_data_schema` | Get entity schema (attributes, types) |
| `ppds_data_analyze` | Get record count and sample data |

### Metadata (1 tool)

| Tool | Purpose |
|------|---------|
| `ppds_metadata_entity` | Full entity metadata with relationships |

### Plugin Debugging (4 tools)

| Tool | Purpose |
|------|---------|
| `ppds_plugins_list` | List registered plugin assemblies |
| `ppds_plugin_traces_list` | List plugin trace logs with filtering |
| `ppds_plugin_traces_get` | Get detailed trace information |
| `ppds_plugin_traces_timeline` | Build hierarchical execution timeline |

## Tool Specifications

### ppds_auth_who

Returns current authentication profile context.

**Parameters:** None

**Return Type:** `AuthWhoResult`

| Field | Type | Description |
|-------|------|-------------|
| `index` | int | Profile index (1-based) |
| `name` | string? | Profile name |
| `authMethod` | string | DeviceCode, ClientSecret, Certificate, etc. |
| `cloud` | string | Public, UsGov, China, etc. |
| `tenantId` | string? | Azure AD tenant ID |
| `username` | string? | User principal name |
| `objectId` | string? | Azure AD object ID |
| `applicationId` | string? | Service principal app ID |
| `tokenExpiresOn` | DateTimeOffset? | Token expiration |
| `tokenStatus` | string? | "valid" or "expired" |
| `environment` | EnvironmentDetails? | Currently selected environment |

**Edge Cases:**
- Token info failure returns null `tokenStatus`/`tokenExpiresOn`
- No environment selected returns null `environment`

---

### ppds_env_list

Lists available Dataverse environments with optional filtering.

**Parameters:**

| Name | Type | Default | Description |
|------|------|---------|-------------|
| `filter` | string? | null | Substring filter (name, URL, ID) |

**Return Type:** `EnvListResult`

| Field | Type | Description |
|-------|------|-------------|
| `filter` | string? | Applied filter (echoed) |
| `environments` | List\<EnvironmentInfo\> | Discovered environments |

**EnvironmentInfo:**

| Field | Type | Description |
|-------|------|-------------|
| `id` | Guid | Organization ID |
| `friendlyName` | string | Display name |
| `uniqueName` | string | Technical name |
| `apiUrl` | string | API URL (use for selection) |
| `type` | string? | Production, Sandbox, Developer, Trial |
| `state` | string | Enabled or Disabled |
| `isActive` | bool | Whether currently selected |

---

### ppds_env_select

Selects a Dataverse environment; invalidates cached connection pool.

**Parameters:**

| Name | Type | Description |
|------|------|-------------|
| `environment` | string | URL, display name, or unique name |

**Return Type:** `EnvSelectResult`

| Field | Type | Description |
|-------|------|-------------|
| `url` | string | Selected environment API URL |
| `displayName` | string | Display name |
| `uniqueName` | string? | Technical name |
| `resolutionMethod` | string | Direct, Discovery, or Api |

**Error Scenarios:**

| Exception | Condition |
|-----------|-----------|
| `ArgumentException` | Empty environment parameter |
| `InvalidOperationException` | No active profile |
| `InvalidOperationException` | Environment not found |

---

### ppds_query_sql

Executes SQL SELECT queries transpiled to FetchXML.

**Parameters:**

| Name | Type | Default | Description |
|------|------|---------|-------------|
| `sql` | string | required | SQL SELECT statement |
| `maxRows` | int | 100 | Max rows (clamped 1-5000) |

**Return Type:** `QueryResult`

| Field | Type | Description |
|-------|------|-------------|
| `entityName` | string? | Primary entity logical name |
| `columns` | List\<QueryColumnInfo\> | Column metadata |
| `records` | List\<Dict\<string, object?\>\> | Result records |
| `count` | int | Records returned |
| `moreRecords` | bool | Pagination indicator |
| `executedFetchXml` | string? | Generated FetchXML (debug) |
| `executionTimeMs` | long | Query duration |

**Supported SQL:**
- SELECT with columns and aliases
- FROM single entity
- JOIN (inner, left, right)
- WHERE with filters
- ORDER BY with ASC/DESC
- Aggregates (COUNT, SUM, AVG, MIN, MAX)

**Error Scenarios:**

| Exception | Condition |
|-----------|-----------|
| `ArgumentException` | Empty sql parameter |
| `InvalidOperationException` | SQL parse/transpile error |

---

### ppds_query_fetch

Executes FetchXML queries directly.

**Parameters:**

| Name | Type | Default | Description |
|------|------|---------|-------------|
| `fetchXml` | string | required | FetchXML query |
| `maxRows` | int | 100 | Max rows (clamped 1-5000) |

**Return Type:** `QueryResult` (same as ppds_query_sql)

**Behavior:**
- Injects `top` attribute if not present
- Existing `top` attribute is NOT overridden

---

### ppds_data_schema

Retrieves entity schema (all attributes with types).

**Parameters:**

| Name | Type | Description |
|------|------|-------------|
| `entityName` | string | Entity logical name |

**Return Type:** `DataSchemaResult`

| Field | Type | Description |
|-------|------|-------------|
| `entityLogicalName` | string | Entity logical name |
| `displayName` | string? | Human-readable name |
| `primaryIdAttribute` | string? | Primary key attribute |
| `primaryNameAttribute` | string? | Primary name attribute |
| `attributes` | List\<SchemaAttributeInfo\> | All attributes (sorted) |

**SchemaAttributeInfo:**

| Field | Type | Description |
|-------|------|-------------|
| `logicalName` | string | Attribute logical name |
| `displayName` | string? | Display name |
| `attributeType` | string | String, Integer, Money, Lookup, etc. |
| `isCustomAttribute` | bool | Whether custom |
| `maxLength` | int? | Max length for strings |
| `targetEntities` | List\<string\>? | Lookup targets |
| `optionSetValues` | List\<OptionSetValue\>? | Picklist options |

---

### ppds_data_analyze

Analyzes entity data with record count and samples.

**Parameters:**

| Name | Type | Description |
|------|------|-------------|
| `entityName` | string | Entity logical name |

**Return Type:** `DataAnalysisResult`

| Field | Type | Description |
|-------|------|-------------|
| `entityName` | string | Entity logical name |
| `recordCount` | int | Total records |
| `attributeCount` | int | Total attributes |
| `customAttributeCount` | int | Custom attributes |
| `sampleRecords` | List\<Dict\> | 5 most recent records |

**Sample Records Include:**
- Primary ID and name
- createdon, modifiedon
- statecode, statuscode
- ownerid (if applicable)

---

### ppds_metadata_entity

Retrieves full entity metadata including relationships.

**Parameters:**

| Name | Type | Default | Description |
|------|------|---------|-------------|
| `entityName` | string | required | Entity logical name |
| `includeAttributes` | bool | true | Include attributes |
| `includeRelationships` | bool | false | Include relationships |

**Return Type:** `EntityMetadataResult`

| Field | Type | Description |
|-------|------|-------------|
| `logicalName` | string | Entity logical name |
| `displayName` | string? | Display name |
| `isCustomEntity` | bool | Whether custom |
| `isActivityEntity` | bool | Whether activity |
| `ownershipType` | string? | UserOwned, OrganizationOwned |
| `attributes` | List? | Attribute metadata |
| `oneToManyRelationships` | List? | 1:N relationships |
| `manyToOneRelationships` | List? | N:1 relationships |
| `manyToManyRelationships` | List? | N:N relationships |

---

### ppds_plugins_list

Lists registered plugin assemblies.

**Parameters:**

| Name | Type | Default | Description |
|------|------|---------|-------------|
| `nameFilter` | string? | null | Filter by name (contains) |
| `maxRows` | int | 50 | Max assemblies (1-200) |

**Return Type:** `PluginsListResult`

| Field | Type | Description |
|-------|------|-------------|
| `assemblies` | List\<PluginAssemblyResult\> | Matching assemblies |
| `count` | int | Number returned |

**PluginAssemblyResult:**

| Field | Type | Description |
|-------|------|-------------|
| `id` | Guid | Assembly ID |
| `name` | string? | Assembly name |
| `version` | string? | Version |
| `isolationMode` | string? | None or Sandbox |
| `types` | List\<PluginTypeResult\> | Plugin types (up to 100) |

---

### ppds_plugin_traces_list

Lists plugin trace logs with filtering.

**Parameters:**

| Name | Type | Default | Description |
|------|------|---------|-------------|
| `entity` | string? | null | Filter by entity |
| `message` | string? | null | Filter by message |
| `typeName` | string? | null | Filter by plugin type |
| `errorsOnly` | bool | false | Only show exceptions |
| `maxRows` | int | 50 | Max traces (1-500) |

**Return Type:** `PluginTracesListResult`

| Field | Type | Description |
|-------|------|-------------|
| `traces` | List\<PluginTraceSummary\> | Trace summaries |
| `count` | int | Number returned |

**PluginTraceSummary:**

| Field | Type | Description |
|-------|------|-------------|
| `id` | Guid | Trace ID (for ppds_plugin_traces_get) |
| `typeName` | string | Plugin class name |
| `messageName` | string? | Create, Update, etc. |
| `mode` | string | Synchronous or Asynchronous |
| `depth` | int | Execution depth (1 = top) |
| `durationMs` | int? | Execution duration |
| `hasException` | bool | Whether exception occurred |
| `correlationId` | Guid? | For timeline grouping |

---

### ppds_plugin_traces_get

Gets detailed trace information.

**Parameters:**

| Name | Type | Description |
|------|------|-------------|
| `traceId` | string | Trace ID (GUID) |

**Return Type:** `PluginTraceDetailResult`

| Field | Type | Description |
|-------|------|-------------|
| `id` | Guid | Trace ID |
| `typeName` | string | Plugin class name |
| `durationMs` | int? | Execution duration |
| `constructorDurationMs` | int? | Constructor duration |
| `hasException` | bool | Whether exception occurred |
| `exceptionDetails` | string? | Full exception with stack trace |
| `messageBlock` | string? | System.Debug output |
| `configuration` | string? | Unsecured config |
| `correlationId` | Guid? | Correlation ID |

**Error Scenarios:**

| Exception | Condition |
|-----------|-----------|
| `ArgumentException` | Empty traceId |
| `ArgumentException` | Invalid GUID format |
| `InvalidOperationException` | Trace not found |

---

### ppds_plugin_traces_timeline

Builds hierarchical execution timeline for correlated traces.

**Parameters:**

| Name | Type | Description |
|------|------|-------------|
| `correlationId` | string | Correlation ID (GUID) |

**Return Type:** `PluginTimelineResult`

| Field | Type | Description |
|-------|------|-------------|
| `correlationId` | Guid | Correlation ID |
| `nodes` | List\<TimelineNodeDto\> | Root nodes (recursive) |
| `totalNodes` | int | Total nodes in tree |

**TimelineNodeDto:**

| Field | Type | Description |
|-------|------|-------------|
| `id` | Guid | Trace ID |
| `typeName` | string | Plugin class name |
| `depth` | int | Plugin chain depth |
| `hierarchyDepth` | int | Tree depth (0 = root) |
| `offsetPercent` | double | Position in timeline (0-100) |
| `widthPercent` | double | Duration as percentage |
| `children` | List\<TimelineNodeDto\> | Nested executions |

## Behaviors

### Tool Pattern

All tools follow a consistent structure:

```csharp
[McpServerToolType]
public sealed class MyTool
{
    private readonly McpToolContext _context;

    public MyTool(McpToolContext context)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
    }

    [McpServerTool(Name = "ppds_my_tool")]
    [Description("Tool description for MCP client")]
    public async Task<ResultType> ExecuteAsync(
        [Description("Parameter help")] string param,
        CancellationToken cancellationToken = default)
    {
        // Validation
        if (string.IsNullOrWhiteSpace(param))
            throw new ArgumentException("Required", nameof(param));

        // Use context for connection pool or service provider
        await using var sp = await _context.CreateServiceProviderAsync(ct);
        var service = sp.GetRequiredService<IMetadataService>();

        // Execute and return
        return MapToResult(await service.DoAsync(ct));
    }
}
```

### Query Result Mapping

`QueryResultMapper` handles Dataverse-specific value types:

| Input Type | Output Format |
|------------|---------------|
| Lookup | `{ value, formatted, entityType, entityId }` |
| Formatted value | `{ value, formatted }` |
| Simple value | Raw value |

### Parameter Bounds

| Tool | Parameter | Min | Max | Default |
|------|-----------|-----|-----|---------|
| ppds_query_sql | maxRows | 1 | 5000 | 100 |
| ppds_query_fetch | maxRows | 1 | 5000 | 100 |
| ppds_plugins_list | maxRows | 1 | 200 | 50 |
| ppds_plugin_traces_list | maxRows | 1 | 500 | 50 |

## Edge Cases

| Scenario | Behavior | Notes |
|----------|----------|-------|
| No active profile | InvalidOperationException | Message: "Run 'ppds auth create'" |
| No environment selected | InvalidOperationException | Message: "Run 'ppds env select'" |
| Entity not found | Exception from metadata service | Entity must exist |
| Empty filter | Returns all results | Filter is optional |
| Invalid GUID format | ArgumentException | Includes format hint |
| Trace not found | InvalidOperationException | With trace ID |

## Error Handling

| Exception | Condition | Recovery |
|-----------|-----------|----------|
| `ArgumentException` | Missing/invalid parameter | Fix parameter value |
| `InvalidOperationException` | No profile/environment | Run setup commands |
| `InvalidOperationException` | Entity/trace not found | Check entity/ID exists |

## Dependencies

- **Internal:**
  - `McpToolContext` - Connection pool and service access
  - `IMetadataService` - Entity metadata
  - `IQueryExecutor` - Query execution
  - `IPluginTraceService` - Plugin trace retrieval
- **External:**
  - `ModelContextProtocol.Server` - Tool attributes

## Thread Safety

- Tools are instantiated per-request by MCP framework
- No static or shared mutable state
- All async operations support cancellation tokens
- Service providers scoped to request lifetime (`await using`)

## Related

- [PPDS.Mcp: Server Architecture](01-server-architecture.md) - Infrastructure
- [PPDS.Dataverse: Query Executor](../01-dataverse/06-query-executor.md) - Query execution
- [PPDS.Dataverse: Metadata Service](../01-dataverse/05-metadata-service.md) - Metadata access

## Source Files

| File | Purpose |
|------|---------|
| `src/PPDS.Mcp/Tools/AuthWhoTool.cs` | Authentication status tool |
| `src/PPDS.Mcp/Tools/EnvListTool.cs` | Environment listing tool |
| `src/PPDS.Mcp/Tools/EnvSelectTool.cs` | Environment selection tool |
| `src/PPDS.Mcp/Tools/QuerySqlTool.cs` | SQL query execution |
| `src/PPDS.Mcp/Tools/QueryFetchTool.cs` | FetchXML query execution |
| `src/PPDS.Mcp/Tools/DataSchemaTool.cs` | Entity schema retrieval |
| `src/PPDS.Mcp/Tools/DataAnalyzeTool.cs` | Entity data analysis |
| `src/PPDS.Mcp/Tools/MetadataEntityTool.cs` | Full entity metadata |
| `src/PPDS.Mcp/Tools/PluginsListTool.cs` | Plugin assembly listing |
| `src/PPDS.Mcp/Tools/PluginTracesListTool.cs` | Plugin trace listing |
| `src/PPDS.Mcp/Tools/PluginTracesGetTool.cs` | Plugin trace details |
| `src/PPDS.Mcp/Tools/PluginTracesTimelineTool.cs` | Execution timeline |
| `src/PPDS.Mcp/Tools/QueryResultTypes.cs` | Shared result types |
| `tests/PPDS.Mcp.Tests/Tools/*.cs` | Tool unit tests |

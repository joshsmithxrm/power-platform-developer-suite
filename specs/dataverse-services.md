# Dataverse Services

**Status:** Implemented
**Version:** 1.0
**Last Updated:** 2026-01-23
**Code:** [src/PPDS.Dataverse/Services/](../src/PPDS.Dataverse/Services/) | [src/PPDS.Dataverse/Metadata/](../src/PPDS.Dataverse/Metadata/)

---

## Overview

The Dataverse Services layer provides domain-specific operations for Dataverse entities through a consistent interface pattern. These services abstract SDK complexity, provide type-safe DTOs, and integrate with the connection pool for efficient resource usage.

### Goals

- **Consistent API**: All services follow identical patterns for DI, async operations, and error handling
- **Type Safety**: Strong typing via DTOs instead of raw Entity objects
- **Pool Integration**: Efficient connection usage through IDataverseConnectionPool
- **Composability**: Services can depend on each other for complex workflows

### Non-Goals

- Data migration (see [migration.md](./migration.md))
- Bulk record operations (see [bulk-operations.md](./bulk-operations.md))
- Query transpilation (see [query.md](./query.md))

---

## Architecture

```
┌──────────────────────────────────────────────────────────────────┐
│                     Application Services (CLI)                    │
└──────────────────────────────────────────────────────────────────┘
                                   │
                                   ▼
┌──────────────────────────────────────────────────────────────────┐
│                      Dataverse Services                           │
│  ┌─────────────┐ ┌─────────────┐ ┌─────────────┐ ┌─────────────┐ │
│  │  Solution   │ │    User     │ │    Role     │ │    Flow     │ │
│  │   Service   │ │   Service   │ │   Service   │ │   Service   │ │
│  └─────────────┘ └─────────────┘ └─────────────┘ └─────────────┘ │
│  ┌─────────────┐ ┌─────────────┐ ┌─────────────┐ ┌─────────────┐ │
│  │PluginTrace  │ │  ImportJob  │ │  EnvVar     │ │  ConnRef    │ │
│  │   Service   │ │   Service   │ │   Service   │ │   Service   │ │
│  └─────────────┘ └─────────────┘ └─────────────┘ └─────────────┘ │
│  ┌─────────────┐ ┌─────────────┐                                  │
│  │ Deployment  │ │  Metadata   │                                  │
│  │  Settings   │ │   Service   │                                  │
│  └─────────────┘ └─────────────┘                                  │
└──────────────────────────────────────────────────────────────────┘
                                   │
                                   ▼
┌──────────────────────────────────────────────────────────────────┐
│                   IDataverseConnectionPool                        │
│              (see connection-pooling.md)                          │
└──────────────────────────────────────────────────────────────────┘
```

### Components

| Component | Responsibility |
|-----------|----------------|
| ISolutionService | Query, export, import solutions and components |
| IUserService | Query system users and role assignments |
| IRoleService | Query security roles and manage assignments |
| IFlowService | Query cloud flows with connection reference extraction |
| IPluginTraceService | Plugin trace logs, timeline building, settings |
| IImportJobService | Monitor solution import progress |
| IEnvironmentVariableService | Manage environment variable definitions and values |
| IConnectionReferenceService | Connection references with orphan detection |
| IDeploymentSettingsService | PAC-compatible deployment settings files |
| IMetadataService | Entity/attribute/relationship metadata queries |

### Dependencies

- Depends on: [connection-pooling.md](./connection-pooling.md)
- Uses patterns from: [architecture.md](./architecture.md)

### Service Dependency Graph

```
DeploymentSettingsService
  ├── IEnvironmentVariableService
  └── IConnectionReferenceService

ConnectionReferenceService
  └── IFlowService (for relationship analysis)

All Services
  └── IDataverseConnectionPool
```

---

## Specification

### Core Requirements

1. All services accept `IDataverseConnectionPool` as first constructor parameter
2. All async methods accept optional `CancellationToken` (default provided)
3. All Get methods return `null` on not found (never throw for missing entities)
4. All List methods return empty collections (never null)
5. Connection acquired per operation, not held across multiple queries

### Primary Flows

**Query Flow:**

1. **Acquire**: Get pooled client via `_pool.GetClientAsync()`
2. **Execute**: Run QueryExpression against Dataverse
3. **Map**: Transform Entity to typed DTO
4. **Release**: Client returns to pool on dispose

**Polling Flow (ImportJobService):**

1. **Start**: Begin polling with configurable interval (default: 5s)
2. **Check**: Query import job progress
3. **Callback**: Invoke optional `onProgress` action
4. **Complete**: Return when job finishes or timeout reached

### Constraints

- Services are registered as **Transient** lifetime
- No internal caching (stateless design)
- State code filtering applied to all queries (active records only)

---

## Core Types

### Service Pattern

All services follow this constructor pattern ([`SolutionService.cs:15-25`](../src/PPDS.Dataverse/Services/SolutionService.cs#L15-L25)):

```csharp
public class SolutionService : ISolutionService
{
    private readonly IDataverseConnectionPool _pool;
    private readonly ILogger<SolutionService> _logger;

    public SolutionService(IDataverseConnectionPool pool, ILogger<SolutionService> logger)
    {
        _pool = pool ?? throw new ArgumentNullException(nameof(pool));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }
}
```

### Connection Usage Pattern

Every method acquires its own client ([`SolutionService.cs:45-52`](../src/PPDS.Dataverse/Services/SolutionService.cs#L45-L52)):

```csharp
public async Task<List<SolutionInfo>> ListAsync(CancellationToken ct = default)
{
    await using var client = await _pool.GetClientAsync(cancellationToken: ct);
    var results = await client.RetrieveMultipleAsync(query, ct);
    return results.Entities.Select(MapToInfo).ToList();
}
```

---

## Service Specifications

### ISolutionService

Manages Dataverse solutions including export, import, and component queries.

| Method | Purpose |
|--------|---------|
| `ListAsync(filter?, includeManaged)` | List solutions with optional filter |
| `GetAsync(uniqueName)` | Get solution by unique name |
| `GetByIdAsync(solutionId)` | Get solution by ID |
| `GetComponentsAsync(solutionId, componentType?)` | List solution components |
| `ExportAsync(uniqueName, managed)` | Export solution as byte array |
| `ImportAsync(solutionZip, overwrite, publishWorkflows)` | Import solution, returns job ID |
| `PublishAllAsync()` | Publish all customizations |

**DTOs:** `SolutionInfo`, `SolutionComponentInfo`

### IUserService

Queries system users with filtering and role lookups.

| Method | Purpose |
|--------|---------|
| `ListAsync(filter?, includeDisabled, top)` | List users with multi-field search |
| `GetByIdAsync(userId)` | Get user by ID |
| `GetByDomainNameAsync(domainName)` | Get user by domain name |
| `GetUserRolesAsync(userId)` | Get roles assigned to user |

**Filter searches:** FullName, DomainName, InternalEmailAddress (OR logic with LIKE)

**DTOs:** `UserInfo`

### IRoleService

Manages security roles and user-role assignments.

| Method | Purpose |
|--------|---------|
| `ListAsync(filter?)` | List root security roles |
| `GetByIdAsync(roleId)` | Get role by ID |
| `GetByNameAsync(name)` | Get role by name |
| `GetRoleUsersAsync(roleId)` | List users with role |
| `AssignRoleAsync(userId, roleId)` | Assign role to user |
| `RemoveRoleAsync(userId, roleId)` | Remove role from user |

**DTOs:** `RoleInfo`

### IFlowService

Queries Power Automate cloud flows with connection reference extraction.

| Method | Purpose |
|--------|---------|
| `ListAsync(solutionName?, state?)` | List flows with filtering |
| `GetAsync(uniqueName)` | Get flow by unique name |
| `GetByIdAsync(id)` | Get flow by ID |

**Connection Reference Extraction:** Parses ClientData JSON to extract referenced connection reference logical names.

**DTOs:** `FlowInfo`, `FlowState`, `FlowCategory`

### IPluginTraceService

Comprehensive plugin trace log management with timeline visualization.

| Method | Purpose |
|--------|---------|
| `ListAsync(filter?, top)` | List traces with 10+ filter criteria |
| `GetAsync(traceId)` | Get full trace detail |
| `GetRelatedAsync(correlationId, top)` | Get traces by correlation ID |
| `BuildTimelineAsync(correlationId)` | Build hierarchical timeline |
| `DeleteAsync(traceId)` | Delete single trace |
| `DeleteByIdsAsync(ids, progress?)` | Bulk delete with progress |
| `DeleteByFilterAsync(filter, progress?)` | Delete matching filter |
| `DeleteAllAsync(progress?)` | Delete all traces |
| `DeleteOlderThanAsync(olderThan, progress?)` | Age-based deletion |
| `GetSettingsAsync()` | Get org trace settings |
| `SetSettingsAsync(setting)` | Set org trace settings |
| `CountAsync(filter?)` | Count traces |

**Bulk Deletion:** Uses pool parallelism ([`PluginTraceService.cs:180-195`](../src/PPDS.Dataverse/Services/PluginTraceService.cs#L180-L195)):

```csharp
var parallelism = Math.Min(_pool.GetTotalRecommendedParallelism(), ids.Count);
await Parallel.ForEachAsync(ids,
    new ParallelOptions { MaxDegreeOfParallelism = parallelism },
    async (id, ct) => { /* delete with fresh client */ });
```

**DTOs:** `PluginTraceInfo`, `PluginTraceDetail`, `PluginTraceFilter`, `TimelineNode`, `PluginTraceSettings`

### IImportJobService

Monitors solution import jobs with polling support.

| Method | Purpose |
|--------|---------|
| `ListAsync(solutionName?, top)` | List import jobs |
| `GetAsync(importJobId)` | Get job by ID |
| `GetDataAsync(importJobId)` | Get import data XML (separate query) |
| `WaitForCompletionAsync(id, pollInterval?, timeout?, onProgress?)` | Poll until complete |

**Polling Defaults:** 5 second interval, 30 minute timeout

**DTOs:** `ImportJobInfo`

### IEnvironmentVariableService

Manages environment variable definitions and values.

| Method | Purpose |
|--------|---------|
| `ListAsync(solutionName?)` | List environment variables |
| `GetAsync(schemaName)` | Get by schema name |
| `GetByIdAsync(id)` | Get by ID |
| `SetValueAsync(schemaName, value)` | Set variable value |
| `ExportAsync(solutionName?)` | Export for deployment |

**DTOs:** `EnvironmentVariableInfo`, `EnvironmentVariableExport`

### IConnectionReferenceService

Manages connection references with orphan detection.

| Method | Purpose |
|--------|---------|
| `ListAsync(solutionName?, unboundOnly)` | List connection references |
| `GetAsync(logicalName)` | Get by logical name |
| `GetByIdAsync(id)` | Get by ID |
| `GetFlowsUsingAsync(logicalName)` | Find flows using reference |
| `AnalyzeAsync(solutionName?)` | Full relationship analysis |

**Orphan Detection:** Identifies flows referencing missing CRs and CRs not used by any flow.

**DTOs:** `ConnectionReferenceInfo`, `FlowConnectionAnalysis`, `FlowConnectionRelationship`

### IDeploymentSettingsService

Generates PAC-compatible deployment settings files.

| Method | Purpose |
|--------|---------|
| `GenerateAsync(solutionName)` | Generate new settings file |
| `SyncAsync(solutionName, existingSettings?)` | Sync with preserving values |
| `ValidateAsync(solutionName, settings)` | Validate for deployment readiness |

**Excludes:** Secret-type environment variables
**Ordering:** Sorted by SchemaName (StringComparison.Ordinal) for deterministic output

**DTOs:** `DeploymentSettingsFile`, `DeploymentSettingsSyncResult`, `DeploymentSettingsValidation`

### IMetadataService

Queries Dataverse schema metadata.

| Method | Purpose |
|--------|---------|
| `GetEntitiesAsync(customOnly?, filter?)` | List entities |
| `GetEntityAsync(logicalName, ...)` | Get entity with components |
| `GetAttributesAsync(entityLogicalName, attributeType?)` | List attributes |
| `GetRelationshipsAsync(entityLogicalName, type?)` | Get relationships |
| `GetGlobalOptionSetsAsync(filter?)` | List global option sets |
| `GetOptionSetAsync(name)` | Get option set values |
| `GetKeysAsync(entityLogicalName)` | List alternate keys |

**Filtering:** Regex-based with wildcard (`*`) support

**DTOs:** `EntitySummary`, `EntityMetadataDto`, `AttributeMetadataDto`, `RelationshipMetadataDto`, `OptionSetMetadataDto`

---

## Error Handling

### Error Types

| Error | Condition | Recovery |
|-------|-----------|----------|
| `ArgumentNullException` | Null pool or logger in constructor | Fix DI registration |
| `TimeoutException` | Import job polling timeout | Increase timeout or check job manually |
| `OperationCanceledException` | Cancellation requested | Normal flow |
| `null` return | Entity not found | Expected behavior, check for null |

### Recovery Strategies

- **Not Found**: Services return `null` rather than throwing
- **Empty Results**: Return empty collections, never null
- **SDK Exceptions**: Bubble up for higher-level handling

### Edge Cases

| Scenario | Expected Behavior |
|----------|-------------------|
| Empty filter string | Treated as no filter (return all) |
| Entity not found | Return null (Get methods) |
| No matching results | Return empty list (List methods) |
| Invalid JSON in ClientData | Return empty CR list (FlowService) |

---

## Design Decisions

### Why Stateless Services?

**Context:** Services could cache metadata or entity lookups for performance.

**Decision:** All services are stateless with Transient lifetime.

**Alternatives considered:**
- Singleton with caching: Rejected - stale data risk, memory pressure
- Request-scoped with cache: Rejected - added complexity

**Consequences:**
- Positive: Simple concurrency model, always fresh data
- Negative: More Dataverse calls (mitigated by connection pooling)

### Why Null Returns Instead of Exceptions?

**Context:** "Not found" can be handled via null return or exception.

**Decision:** Return `null` for single-entity lookups when not found.

**Alternatives considered:**
- Throw NotFoundException: Rejected - "not found" is often expected flow
- Return Result<T>: Rejected - adds API complexity

**Consequences:**
- Positive: Cleaner calling code with null-coalescing
- Positive: No try-catch needed for expected scenarios
- Negative: Caller must check for null

### Why Separate GetDataAsync for ImportJob?

**Context:** Import job data field contains large XML.

**Decision:** Separate method to avoid fetching large field on every query.

**Test results:**
| Scenario | Payload Size |
|----------|-------------|
| List without data | ~2KB per job |
| List with data | ~500KB per job |

**Consequences:**
- Positive: 250x smaller payloads for listing
- Negative: Extra API call when data is needed

### Why Pool Parallelism for Bulk Delete?

**Context:** Deleting thousands of plugin traces needs performance.

**Decision:** Use `Parallel.ForEachAsync` with pool's recommended DOP.

**Alternatives considered:**
- Sequential deletion: Rejected - too slow for large datasets
- Fixed parallelism: Rejected - doesn't adapt to environment

**Consequences:**
- Positive: Adapts to available connections
- Positive: Respects pool's throttle awareness
- Negative: Complexity in progress reporting

---

## Extension Points

### Adding a New Service

1. **Create interface**: Define in `Services/I{ServiceName}Service.cs`
2. **Implement**: Create `{ServiceName}Service.cs` following constructor pattern
3. **Create DTOs**: Add record types in `Services/Models/`
4. **Register**: Add to `ServiceCollectionExtensions.RegisterDataverseServices()`

**Interface skeleton:**

```csharp
public interface INewEntityService
{
    Task<List<EntityInfo>> ListAsync(
        string? filter = null,
        CancellationToken cancellationToken = default);

    Task<EntityInfo?> GetAsync(
        string identifier,
        CancellationToken cancellationToken = default);
}
```

**Implementation skeleton:**

```csharp
public class NewEntityService : INewEntityService
{
    private readonly IDataverseConnectionPool _pool;
    private readonly ILogger<NewEntityService> _logger;

    public NewEntityService(IDataverseConnectionPool pool, ILogger<NewEntityService> logger)
    {
        _pool = pool ?? throw new ArgumentNullException(nameof(pool));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }
}
```

---

## Configuration

Services are registered via `AddDataverseConnectionPool()` or `RegisterDataverseServices()`:

```csharp
services.AddDataverseConnectionPool(options => { ... });
// Automatically registers all services as Transient
```

No service-specific configuration. Services inherit pool configuration for connection behavior.

---

## Testing

### Acceptance Criteria

- [x] Constructor null validation for pool and logger
- [x] Transient lifetime verified via different instances
- [x] List methods return empty collection on no results
- [x] Get methods return null on not found
- [x] Cancellation token properly propagated
- [x] Progress callbacks invoked during bulk operations

### Test Categories

| Category | Filter | Purpose |
|----------|--------|---------|
| Unit | `Category!=Integration` | Mock-based constructor and method tests |
| Integration | `Category=Integration` | FakeXrmEasy in-memory tests |

### Test Examples

**Constructor Validation ([`SolutionServiceTests.cs:15-30`](../tests/PPDS.Dataverse.Tests/Services/SolutionServiceTests.cs#L15-L30)):**

```csharp
[Fact]
public void Constructor_ThrowsOnNullConnectionPool()
{
    var logger = new NullLogger<SolutionService>();
    var act = () => new SolutionService(null!, logger);
    act.Should().Throw<ArgumentNullException>()
        .And.ParamName.Should().Be("pool");
}
```

**Integration Test with FakeXrmEasy ([`PluginTraceServiceTests.cs`](../tests/PPDS.Dataverse.IntegrationTests/Services/PluginTraceServiceTests.cs)):**

```csharp
[Fact]
public async Task ListAsync_WithTypeNameFilter_ReturnsMatching()
{
    InitializeWith(
        CreatePluginTrace("Plugin.Account", "Create", "account"),
        CreatePluginTrace("Plugin.Contact", "Update", "contact"));

    var filter = new PluginTraceFilter { TypeName = "Plugin.Account" };
    var results = await _service.ListAsync(filter);

    results.Should().ContainSingle()
        .Which.TypeName.Should().Be("Plugin.Account");
}
```

---

## Related Specs

- [connection-pooling.md](./connection-pooling.md) - Pool usage patterns and DOP coordination
- [bulk-operations.md](./bulk-operations.md) - High-throughput record operations
- [architecture.md](./architecture.md) - Cross-cutting patterns and error handling
- [migration.md](./migration.md) - Uses services for data migration workflows

---

## Roadmap

- Batch operations for role assignments
- Caching layer for metadata service (opt-in)
- Webhook registration service
- Business process flow service

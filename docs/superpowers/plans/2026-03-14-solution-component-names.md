# Solution Component Name Resolution Implementation Plan

> **For agentic workers:** REQUIRED: Use superpowers:subagent-driven-development (if subagents available) or superpowers:executing-plans to implement this plan. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace raw GUID display in solution panel components with human-readable names, fix "Unknown" component type labels, enable daemon logging, and add click-to-expand detail cards.

**Architecture:** New `ComponentNameResolver` service resolves objectId GUIDs to names via type-specific Dataverse table queries. Entity-type components use the existing `CachedMetadataProvider` (requires adding `MetadataId` to `EntitySummary`). All caching stays daemon-side. Daemon logging is enabled in serve mode so resolution timing is observable.

**Tech Stack:** C# (.NET 8+), xUnit + FluentAssertions + Moq, TypeScript, Vitest

**Spec:** [`specs/solution-component-names.md`](../../../specs/solution-component-names.md)

**Testing:**
- .NET unit tests: `dotnet test PPDS.sln --filter "Category!=Integration" -v q`
- Extension unit tests: `npm run ext:test`

---

## Chunk 1: Foundation (Tasks 1-3)

Prerequisites and daemon logging — no new features yet, just laying groundwork.

### Task 1: Add MetadataId to EntitySummary

**AC:** AC-14
**Files:**
- Modify: `src/PPDS.Dataverse/Metadata/Models/EntitySummary.cs:8-69`
- Modify: `src/PPDS.Dataverse/Metadata/DataverseMetadataService.cs:328-343`
- Test: `tests/PPDS.Dataverse.Tests/Metadata/DataverseMetadataServiceTests.cs`

- [ ] **Step 1: Write failing test**

In `tests/PPDS.Dataverse.Tests/Metadata/DataverseMetadataServiceTests.cs`, add:

```csharp
[Fact]
public void EntitySummary_HasMetadataIdProperty()
{
    // Verify EntitySummary has the MetadataId property we need
    // for matching solutioncomponent.objectId to entities
    var summary = new EntitySummary
    {
        MetadataId = Guid.NewGuid(),
        LogicalName = "account",
        DisplayName = "Account",
        SchemaName = "Account",
        ObjectTypeCode = 1
    };

    summary.MetadataId.Should().NotBe(Guid.Empty);
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test PPDS.sln --filter "FullyQualifiedName~EntitySummary_HasMetadataIdProperty" -v q`
Expected: FAIL — `EntitySummary` does not have `MetadataId` property

- [ ] **Step 3: Add MetadataId to EntitySummary**

In `src/PPDS.Dataverse/Metadata/Models/EntitySummary.cs`, add after line 38 (after `ObjectTypeCode`):

```csharp
/// <summary>
/// Gets the entity metadata ID (used to match solutioncomponent.objectId for entity-type components).
/// </summary>
[JsonPropertyName("metadataId")]
public Guid MetadataId { get; init; }
```

- [ ] **Step 4: Populate MetadataId in MapToEntitySummary**

In `src/PPDS.Dataverse/Metadata/DataverseMetadataService.cs:330-342`, add to the object initializer:

```csharp
MetadataId = e.MetadataId ?? Guid.Empty,
```

Add it after `LogicalName = e.LogicalName,` (line 332).

- [ ] **Step 5: Run test to verify it passes**

Run: `dotnet test PPDS.sln --filter "FullyQualifiedName~EntitySummary_HasMetadataIdProperty" -v q`
Expected: PASS

- [ ] **Step 6: Commit**

```bash
git add src/PPDS.Dataverse/Metadata/Models/EntitySummary.cs src/PPDS.Dataverse/Metadata/DataverseMetadataService.cs tests/PPDS.Dataverse.Tests/Metadata/DataverseMetadataServiceTests.cs
git commit -m "feat: add MetadataId to EntitySummary for solution component matching"
```

---

### Task 2: Enable daemon logging in serve mode

**AC:** AC-08
**Files:**
- Modify: `src/PPDS.Cli/Commands/Serve/ServeCommand.cs:35-92`
- Modify: `src/PPDS.Cli/Infrastructure/DaemonConnectionPoolManager.cs:46-53,344-348`

- [ ] **Step 1: Create a logger factory in ServeCommand**

In `src/PPDS.Cli/Commands/Serve/ServeCommand.cs`, add a using statement at the top:

```csharp
using PPDS.Cli.Infrastructure.Logging;
```

Then in `ExecuteAsync` (line 35), before the pool manager creation (line 46), create a logger factory:

```csharp
// Create a logger factory for daemon serve mode.
// Logs go to stderr (stdout is reserved for JSON-RPC).
// Use text format with Information level for operational visibility.
var loggerOptions = new CliLoggerOptions
{
    MinimumLevel = LogLevel.Information,
    UseJsonFormat = false,
    EnableColors = !Console.IsErrorRedirected
};
var logServices = new ServiceCollection();
logServices.AddCliLogging(loggerOptions);
await using var logServiceProvider = logServices.BuildServiceProvider();
var loggerFactory = logServiceProvider.GetRequiredService<ILoggerFactory>();
```

Add required usings:
```csharp
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
```

- [ ] **Step 2: Pass logger factory to DaemonConnectionPoolManager**

Change line 46 from:
```csharp
await using var poolManager = new DaemonConnectionPoolManager();
```
to:
```csharp
await using var poolManager = new DaemonConnectionPoolManager(loggerFactory);
```

- [ ] **Step 3: Pass logger to RpcMethodHandler**

Change line 53 from:
```csharp
using var handler = new RpcMethodHandler(poolManager, authProvider);
```
to:
```csharp
using var handler = new RpcMethodHandler(poolManager, authProvider, loggerFactory.CreateLogger<RpcMethodHandler>());
```

- [ ] **Step 4: Lower DaemonConnectionPoolManager minimum log level**

In `src/PPDS.Cli/Infrastructure/DaemonConnectionPoolManager.cs:344-348`, change `LogLevel.Warning` to `LogLevel.Information`:

```csharp
services.AddLogging(builder =>
{
    builder.SetMinimumLevel(LogLevel.Information);
    builder.AddProvider(new LoggerFactoryProvider(_loggerFactory));
});
```

- [ ] **Step 5: Run full test suite to verify no regressions**

Run: `dotnet test PPDS.sln --filter "Category!=Integration" -v q`
Expected: All existing tests PASS

- [ ] **Step 6: Commit**

```bash
git add src/PPDS.Cli/Commands/Serve/ServeCommand.cs src/PPDS.Cli/Infrastructure/DaemonConnectionPoolManager.cs
git commit -m "feat: enable Information-level logging in daemon serve mode"
```

---

### Task 3: Fix componenttype resolution bare catch and cache key

**AC:** AC-04
**Files:**
- Modify: `src/PPDS.Dataverse/Services/SolutionService.cs:311,315-322`
- Test: `tests/PPDS.Dataverse.Tests/Services/SolutionServiceTests.cs`

- [ ] **Step 1: Fix the outer bare catch**

In `src/PPDS.Dataverse/Services/SolutionService.cs:315-322`, replace:

```csharp
try
{
    resolvedTypeNames = await GetComponentTypeNamesAsync(envUrl, cancellationToken);
}
catch
{
    resolvedTypeNames = ComponentTypeNames;
}
```

with:

```csharp
try
{
    resolvedTypeNames = await GetComponentTypeNamesAsync(envUrl, cancellationToken);
}
catch (Exception ex)
{
    _logger.LogWarning(ex, "Failed to resolve component type names for {EnvUrl}, falling back to hardcoded dictionary", envUrl);
    resolvedTypeNames = ComponentTypeNames;
}
```

- [ ] **Step 2: Fix the cache key**

On line 311, `client.ConnectedOrgUniqueName` can return null. The environment URL is more reliable but isn't currently available in `GetComponentsAsync`. For now, fix the null fallback to be consistent. Replace line 311:

```csharp
var envUrl = client.ConnectedOrgUniqueName ?? "default";
```

with:

```csharp
var envUrl = client.ConnectedOrgUniqueName ?? client.ConnectedOrgId?.ToString() ?? "default";
```

`ConnectedOrgUniqueName` is the most reliable identifier. `ConnectedOrgId` (Guid) is the fallback. Note: `ConnectedOrgUriActual` exists on `ServiceClient` but is not exposed through `IDataverseClient` — do not use it.

- [ ] **Step 3: Run existing tests**

Run: `dotnet test PPDS.sln --filter "Category!=Integration" -v q`
Expected: All tests PASS

- [ ] **Step 4: Commit**

```bash
git add src/PPDS.Dataverse/Services/SolutionService.cs
git commit -m "fix: replace bare catch in componenttype resolution with structured logging"
```

---

## Chunk 2: ComponentNameResolver (Tasks 4-5)

Core name resolution service with full test coverage.

### Task 4: Create IComponentNameResolver interface and ComponentNames record

**AC:** AC-01, AC-02, AC-03
**Files:**
- Create: `src/PPDS.Dataverse/Services/IComponentNameResolver.cs`
- Create: `src/PPDS.Dataverse/Services/ComponentNameResolver.cs`
- Create: `tests/PPDS.Dataverse.Tests/Services/ComponentNameResolverTests.cs`

- [ ] **Step 1: Create interface and record**

Create `src/PPDS.Dataverse/Services/IComponentNameResolver.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace PPDS.Dataverse.Services;

/// <summary>
/// Resolved name fields for a solution component.
/// </summary>
public record ComponentNames(
    string? LogicalName,
    string? SchemaName,
    string? DisplayName);

/// <summary>
/// Resolves component objectId GUIDs to human-readable names
/// by querying type-specific Dataverse tables.
/// </summary>
public interface IComponentNameResolver
{
    /// <summary>
    /// Resolves names for a batch of components of the same type.
    /// </summary>
    /// <param name="componentType">The component type code.</param>
    /// <param name="objectIds">The objectId GUIDs to resolve.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Dictionary mapping objectId to resolved names. Missing entries = unresolvable.</returns>
    Task<IReadOnlyDictionary<Guid, ComponentNames>> ResolveAsync(
        int componentType,
        IReadOnlyList<Guid> objectIds,
        CancellationToken cancellationToken = default);
}
```

- [ ] **Step 2: Write failing tests for entity-type resolution**

Create `tests/PPDS.Dataverse.Tests/Services/ComponentNameResolverTests.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using PPDS.Dataverse.Metadata;
using PPDS.Dataverse.Metadata.Models;
using PPDS.Dataverse.Pooling;
using PPDS.Dataverse.Services;
using Xunit;

namespace PPDS.Dataverse.Tests.Services;

public class ComponentNameResolverTests
{
    private readonly Mock<ICachedMetadataProvider> _metadataProvider = new();
    private readonly Mock<IDataverseConnectionPool> _pool = new();
    private readonly ILogger<ComponentNameResolver> _logger = NullLogger<ComponentNameResolver>.Instance;

    private ComponentNameResolver CreateResolver() =>
        new(_metadataProvider.Object, _pool.Object, _logger);

    [Fact]
    public async Task ResolveAsync_EntityType_UsesMetadataProvider()
    {
        // Arrange
        var entityId = Guid.NewGuid();
        var entities = new List<EntitySummary>
        {
            new()
            {
                MetadataId = entityId,
                LogicalName = "account",
                SchemaName = "Account",
                DisplayName = "Account",
                ObjectTypeCode = 1
            }
        };
        _metadataProvider
            .Setup(m => m.GetEntitiesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(entities);

        var resolver = CreateResolver();

        // Act
        var result = await resolver.ResolveAsync(1, new[] { entityId });

        // Assert
        result.Should().ContainKey(entityId);
        result[entityId].LogicalName.Should().Be("account");
        result[entityId].SchemaName.Should().Be("Account");
        result[entityId].DisplayName.Should().Be("Account");

        // Verify pool was NOT used (entity resolution uses metadata cache)
        _pool.Verify(p => p.GetClientAsync(
            It.IsAny<DataverseClientOptions>(),
            It.IsAny<string>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ResolveAsync_UnmappedType_ReturnsEmptyDictionary()
    {
        // Arrange
        var resolver = CreateResolver();
        var objectId = Guid.NewGuid();

        // Act — type 999 has no mapping
        var result = await resolver.ResolveAsync(999, new[] { objectId });

        // Assert
        result.Should().BeEmpty();
    }

    [Fact]
    public async Task ResolveAsync_EmptyObjectIds_ReturnsEmptyDictionary()
    {
        // Arrange
        var resolver = CreateResolver();

        // Act
        var result = await resolver.ResolveAsync(61, Array.Empty<Guid>());

        // Assert
        result.Should().BeEmpty();
    }
}
```

- [ ] **Step 3: Run tests to verify they fail**

Run: `dotnet test PPDS.sln --filter "FullyQualifiedName~ComponentNameResolverTests" -v q`
Expected: FAIL — `ComponentNameResolver` class does not exist

- [ ] **Step 4: Implement ComponentNameResolver**

Create `src/PPDS.Dataverse/Services/ComponentNameResolver.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using PPDS.Dataverse.Metadata;
using PPDS.Dataverse.Pooling;

namespace PPDS.Dataverse.Services;

/// <summary>
/// Resolves component objectId GUIDs to human-readable names
/// by querying type-specific Dataverse tables.
/// </summary>
public class ComponentNameResolver : IComponentNameResolver
{
    /// <summary>
    /// Maximum number of IDs per IN-clause to avoid URL/query length limits.
    /// </summary>
    private const int MaxBatchSize = 100;

    private readonly ICachedMetadataProvider _metadataProvider;
    private readonly IDataverseConnectionPool _pool;
    private readonly ILogger<ComponentNameResolver> _logger;

    /// <summary>
    /// Mapping from component type code to table name and name field(s).
    /// Fields: (tableName, logicalNameField, schemaNameField, displayNameField).
    /// Null field means "not available for this type".
    /// </summary>
    private static readonly Dictionary<int, ComponentTypeMapping> TypeMappings = new()
    {
        // Entity type (1) is handled specially via CachedMetadataProvider — not in this dict
        [26]  = new("savedquery", "name", null, null),
        [29]  = new("workflow", "uniquename", null, "name"),
        [60]  = new("systemform", "name", null, null),
        [61]  = new("webresource", "name", null, null),
        [66]  = new("customcontrol", "name", null, null),
        [90]  = new("plugintype", "name", null, null),
        [91]  = new("pluginassembly", "name", null, null),
        [92]  = new("sdkmessageprocessingstep", "name", null, null),
        [300] = new("canvasapp", "name", null, "displayname"),
        [371] = new("connector", "name", null, "displayname"),
        [372] = new("connector", "name", null, "displayname"),
        [380] = new("environmentvariabledefinition", null, "schemaname", "displayname"),
        [381] = new("environmentvariablevalue", null, "schemaname", null),
    };

    public ComponentNameResolver(
        ICachedMetadataProvider metadataProvider,
        IDataverseConnectionPool pool,
        ILogger<ComponentNameResolver> logger)
    {
        _metadataProvider = metadataProvider ?? throw new ArgumentNullException(nameof(metadataProvider));
        _pool = pool ?? throw new ArgumentNullException(nameof(pool));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<Guid, ComponentNames>> ResolveAsync(
        int componentType,
        IReadOnlyList<Guid> objectIds,
        CancellationToken cancellationToken = default)
    {
        if (objectIds.Count == 0)
            return new Dictionary<Guid, ComponentNames>();

        var stopwatch = Stopwatch.StartNew();

        try
        {
            IReadOnlyDictionary<Guid, ComponentNames> result;

            if (componentType == 1)
            {
                result = await ResolveEntitiesAsync(objectIds, cancellationToken);
            }
            else if (TypeMappings.TryGetValue(componentType, out var mapping))
            {
                result = await ResolveFromTableAsync(mapping, objectIds, cancellationToken);
            }
            else
            {
                // Unmapped type — no name resolution available
                return new Dictionary<Guid, ComponentNames>();
            }

            stopwatch.Stop();
            _logger.LogInformation(
                "Resolved {Count} {TypeName} names in {ElapsedMs}ms",
                result.Count,
                componentType == 1 ? "Entity" : mapping!.TableName,
                stopwatch.ElapsedMilliseconds);

            return result;
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            _logger.LogWarning(
                ex,
                "Failed to resolve names for component type {ComponentType} ({Count} IDs) after {ElapsedMs}ms",
                componentType, objectIds.Count, stopwatch.ElapsedMilliseconds);
            return new Dictionary<Guid, ComponentNames>();
        }
    }

    private async Task<IReadOnlyDictionary<Guid, ComponentNames>> ResolveEntitiesAsync(
        IReadOnlyList<Guid> objectIds,
        CancellationToken cancellationToken)
    {
        var entities = await _metadataProvider.GetEntitiesAsync(cancellationToken);
        var lookup = new Dictionary<Guid, ComponentNames>();

        var entityByMetadataId = entities.ToDictionary(e => e.MetadataId);

        foreach (var objectId in objectIds)
        {
            if (entityByMetadataId.TryGetValue(objectId, out var entity))
            {
                lookup[objectId] = new ComponentNames(
                    entity.LogicalName,
                    entity.SchemaName,
                    entity.DisplayName);
            }
        }

        return lookup;
    }

    private async Task<IReadOnlyDictionary<Guid, ComponentNames>> ResolveFromTableAsync(
        ComponentTypeMapping mapping,
        IReadOnlyList<Guid> objectIds,
        CancellationToken cancellationToken)
    {
        var result = new Dictionary<Guid, ComponentNames>();

        // Split into batches to avoid query length limits
        for (var i = 0; i < objectIds.Count; i += MaxBatchSize)
        {
            var batch = objectIds.Skip(i).Take(MaxBatchSize).ToArray();
            var batchResult = await QueryBatchAsync(mapping, batch, cancellationToken);

            foreach (var kvp in batchResult)
            {
                result[kvp.Key] = kvp.Value;
            }
        }

        return result;
    }

    private async Task<Dictionary<Guid, ComponentNames>> QueryBatchAsync(
        ComponentTypeMapping mapping,
        Guid[] objectIds,
        CancellationToken cancellationToken)
    {
        await using var client = await _pool.GetClientAsync(cancellationToken: cancellationToken);

        // Build column set from non-null name fields
        var columns = new List<string>();
        if (mapping.LogicalNameField != null) columns.Add(mapping.LogicalNameField);
        if (mapping.SchemaNameField != null) columns.Add(mapping.SchemaNameField);
        if (mapping.DisplayNameField != null) columns.Add(mapping.DisplayNameField);

        // Primary key is always <tablename>id for standard Dataverse tables
        var primaryKey = mapping.TableName + "id";

        var query = new QueryExpression(mapping.TableName)
        {
            ColumnSet = new ColumnSet(columns.ToArray())
        };
        query.Criteria.AddCondition(primaryKey, ConditionOperator.In, objectIds.Cast<object>().ToArray());

        var response = await client.RetrieveMultipleAsync(query, cancellationToken);

        var result = new Dictionary<Guid, ComponentNames>();
        foreach (var entity in response.Entities)
        {
            var logicalName = mapping.LogicalNameField != null
                ? NullIfEmpty(entity.GetAttributeValue<string>(mapping.LogicalNameField))
                : null;
            var schemaName = mapping.SchemaNameField != null
                ? NullIfEmpty(entity.GetAttributeValue<string>(mapping.SchemaNameField))
                : null;
            var displayName = mapping.DisplayNameField != null
                ? NullIfEmpty(entity.GetAttributeValue<string>(mapping.DisplayNameField))
                : null;

            result[entity.Id] = new ComponentNames(logicalName, schemaName, displayName);
        }

        return result;
    }

    private static string? NullIfEmpty(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;

    /// <summary>
    /// Mapping definition for a component type to its Dataverse table and name fields.
    /// </summary>
    private sealed record ComponentTypeMapping(
        string TableName,
        string? LogicalNameField,
        string? SchemaNameField,
        string? DisplayNameField);
}
```

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test PPDS.sln --filter "FullyQualifiedName~ComponentNameResolverTests" -v q`
Expected: All 3 tests PASS

- [ ] **Step 6: Commit**

```bash
git add src/PPDS.Dataverse/Services/IComponentNameResolver.cs src/PPDS.Dataverse/Services/ComponentNameResolver.cs tests/PPDS.Dataverse.Tests/Services/ComponentNameResolverTests.cs
git commit -m "feat: add ComponentNameResolver with entity-type and table-query resolution"
```

---

### Task 5: Additional ComponentNameResolver tests

**AC:** AC-09, AC-10, AC-12
**Files:**
- Modify: `tests/PPDS.Dataverse.Tests/Services/ComponentNameResolverTests.cs`

- [ ] **Step 1: Add timing log test**

```csharp
[Fact]
public async Task ResolveAsync_LogsTiming()
{
    // Arrange
    var entityId = Guid.NewGuid();
    _metadataProvider
        .Setup(m => m.GetEntitiesAsync(It.IsAny<CancellationToken>()))
        .ReturnsAsync(new List<EntitySummary>
        {
            new()
            {
                MetadataId = entityId,
                LogicalName = "account",
                SchemaName = "Account",
                DisplayName = "Account",
                ObjectTypeCode = 1
            }
        });

    var mockLogger = new Mock<ILogger<ComponentNameResolver>>();
    var resolver = new ComponentNameResolver(_metadataProvider.Object, _pool.Object, mockLogger.Object);

    // Act
    await resolver.ResolveAsync(1, new[] { entityId });

    // Assert — verify Information-level log was emitted with timing
    mockLogger.Verify(
        l => l.Log(
            LogLevel.Information,
            It.IsAny<EventId>(),
            It.Is<It.IsAnyType>((v, t) => v.ToString()!.Contains("Resolved") && v.ToString()!.Contains("ms")),
            null,
            It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
        Times.Once);
}
```

- [ ] **Step 2: Add partial failure test**

```csharp
[Fact]
public async Task ResolveAsync_PartialFailure_ReturnsEmptyAndLogsWarning()
{
    // Arrange
    _metadataProvider
        .Setup(m => m.GetEntitiesAsync(It.IsAny<CancellationToken>()))
        .ThrowsAsync(new InvalidOperationException("Metadata unavailable"));

    var mockLogger = new Mock<ILogger<ComponentNameResolver>>();
    var resolver = new ComponentNameResolver(_metadataProvider.Object, _pool.Object, mockLogger.Object);

    // Act
    var result = await resolver.ResolveAsync(1, new[] { Guid.NewGuid() });

    // Assert — graceful degradation: empty dict, not exception
    result.Should().BeEmpty();

    // Verify warning was logged
    mockLogger.Verify(
        l => l.Log(
            LogLevel.Warning,
            It.IsAny<EventId>(),
            It.IsAny<It.IsAnyType>(),
            It.IsAny<Exception>(),
            It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
        Times.Once);
}
```

- [ ] **Step 3: Add batch splitting test**

```csharp
[Fact]
public async Task ResolveAsync_LargeBatch_SplitsIntoMultipleQueries()
{
    // Arrange — 150 IDs should produce 2 batches (100 + 50)
    var objectIds = Enumerable.Range(0, 150).Select(_ => Guid.NewGuid()).ToList();

    var mockClient = new Mock<IPooledClient>();
    mockClient
        .Setup(c => c.RetrieveMultipleAsync(It.IsAny<QueryExpression>(), It.IsAny<CancellationToken>()))
        .ReturnsAsync(new EntityCollection()); // Empty results fine for this test

    _pool
        .Setup(p => p.GetClientAsync(
            It.IsAny<DataverseClientOptions>(),
            It.IsAny<string>(),
            It.IsAny<CancellationToken>()))
        .ReturnsAsync(mockClient.Object);

    var resolver = CreateResolver();

    // Act — type 61 (WebResource) has a table mapping
    await resolver.ResolveAsync(61, objectIds);

    // Assert — pool should be called twice (batches of 100 + 50)
    _pool.Verify(
        p => p.GetClientAsync(
            It.IsAny<DataverseClientOptions>(),
            It.IsAny<string>(),
            It.IsAny<CancellationToken>()),
        Times.Exactly(2));
}
```

- [ ] **Step 4: Add WebResource table query test**

```csharp
[Fact]
public async Task ResolveAsync_WebResource_QueriesTable()
{
    // Arrange
    var id1 = Guid.NewGuid();
    var id2 = Guid.NewGuid();

    var entity1 = new Entity("webresource", id1);
    entity1["name"] = "new_scripts/form.js";
    var entity2 = new Entity("webresource", id2);
    entity2["name"] = "new_styles/global.css";

    var mockClient = new Mock<IPooledClient>();
    mockClient
        .Setup(c => c.RetrieveMultipleAsync(It.IsAny<QueryExpression>(), It.IsAny<CancellationToken>()))
        .ReturnsAsync(new EntityCollection(new List<Entity> { entity1, entity2 }));

    _pool
        .Setup(p => p.GetClientAsync(
            It.IsAny<DataverseClientOptions>(),
            It.IsAny<string>(),
            It.IsAny<CancellationToken>()))
        .ReturnsAsync(mockClient.Object);

    var resolver = CreateResolver();

    // Act
    var result = await resolver.ResolveAsync(61, new[] { id1, id2 });

    // Assert
    result.Should().HaveCount(2);
    result[id1].LogicalName.Should().Be("new_scripts/form.js");
    result[id2].LogicalName.Should().Be("new_styles/global.css");
}
```

- [ ] **Step 5: Add entity-not-found test**

```csharp
[Fact]
public async Task ResolveAsync_EntityNotInCache_OmitsFromResult()
{
    // Arrange
    var unknownId = Guid.NewGuid();
    _metadataProvider
        .Setup(m => m.GetEntitiesAsync(It.IsAny<CancellationToken>()))
        .ReturnsAsync(new List<EntitySummary>()); // Empty cache

    var resolver = CreateResolver();

    // Act
    var result = await resolver.ResolveAsync(1, new[] { unknownId });

    // Assert
    result.Should().BeEmpty();
}
```

- [ ] **Step 6: Run all resolver tests**

Run: `dotnet test PPDS.sln --filter "FullyQualifiedName~ComponentNameResolverTests" -v q`
Expected: All 8 tests PASS

- [ ] **Step 7: Commit**

```bash
git add tests/PPDS.Dataverse.Tests/Services/ComponentNameResolverTests.cs
git commit -m "test: add timing, partial failure, and entity-not-found tests for ComponentNameResolver"
```

---

## Chunk 3: Integration into SolutionService and RPC (Tasks 6-8)

Wire ComponentNameResolver into the existing component loading flow and expose names through JSON-RPC.

### Task 6: Update SolutionComponentInfo record and DI registration

**Files:**
- Modify: `src/PPDS.Dataverse/Services/ISolutionService.cs:95-101`
- Modify: `src/PPDS.Dataverse/DependencyInjection/ServiceCollectionExtensions.cs:256-258`

- [ ] **Step 1: Add name fields to SolutionComponentInfo**

In `src/PPDS.Dataverse/Services/ISolutionService.cs:95-101`, replace:

```csharp
public record SolutionComponentInfo(
    Guid Id,
    Guid ObjectId,
    int ComponentType,
    string ComponentTypeName,
    int RootComponentBehavior,
    bool IsMetadata);
```

with:

```csharp
public record SolutionComponentInfo(
    Guid Id,
    Guid ObjectId,
    int ComponentType,
    string ComponentTypeName,
    int RootComponentBehavior,
    bool IsMetadata,
    string? DisplayName = null,
    string? LogicalName = null,
    string? SchemaName = null);
```

- [ ] **Step 2: Register ComponentNameResolver in DI**

In `src/PPDS.Dataverse/DependencyInjection/ServiceCollectionExtensions.cs`, after line 257 (`AddTransient<ISolutionService>`), add:

```csharp
services.AddTransient<IComponentNameResolver, ComponentNameResolver>();
```

- [ ] **Step 3: Run tests to verify no regressions**

Run: `dotnet test PPDS.sln --filter "Category!=Integration" -v q`
Expected: All tests PASS (existing callers use positional args, new fields have defaults)

- [ ] **Step 4: Commit**

```bash
git add src/PPDS.Dataverse/Services/ISolutionService.cs src/PPDS.Dataverse/DependencyInjection/ServiceCollectionExtensions.cs
git commit -m "feat: add name fields to SolutionComponentInfo and register ComponentNameResolver"
```

---

### Task 7: Wire ComponentNameResolver into SolutionService

**AC:** AC-01, AC-02, AC-03, AC-04, AC-12
**Files:**
- Modify: `src/PPDS.Dataverse/Services/SolutionService.cs:22-26,135-142,282-341`

- [ ] **Step 1: Add IComponentNameResolver dependency**

In `src/PPDS.Dataverse/Services/SolutionService.cs`, add a field after line 25:

```csharp
private readonly IComponentNameResolver _nameResolver;
```

Update the constructor (lines 135-142) to accept it:

```csharp
public SolutionService(
    IDataverseConnectionPool pool,
    ILogger<SolutionService> logger,
    IMetadataService metadataService,
    IComponentNameResolver nameResolver)
{
    _pool = pool ?? throw new ArgumentNullException(nameof(pool));
    _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    _metadataService = metadataService ?? throw new ArgumentNullException(nameof(metadataService));
    _nameResolver = nameResolver ?? throw new ArgumentNullException(nameof(nameResolver));
}
```

- [ ] **Step 2: Add name resolution to GetComponentsAsync**

In `src/PPDS.Dataverse/Services/SolutionService.cs`, after the component list is built (after line 340 `}).ToList();`), add name resolution before the return:

```csharp
// Resolve component names by type
var stopwatch = System.Diagnostics.Stopwatch.StartNew();
var grouped = components.GroupBy(c => c.ComponentType).ToList();

foreach (var group in grouped)
{
    try
    {
        var names = await _nameResolver.ResolveAsync(
            group.Key,
            group.Select(c => c.ObjectId).ToList(),
            cancellationToken);

        for (var i = 0; i < components.Count; i++)
        {
            var comp = components[i];
            if (comp.ComponentType == group.Key &&
                names.TryGetValue(comp.ObjectId, out var resolved))
            {
                components[i] = comp with
                {
                    LogicalName = resolved.LogicalName,
                    SchemaName = resolved.SchemaName,
                    DisplayName = resolved.DisplayName
                };
            }
        }
    }
    catch (Exception ex)
    {
        _logger.LogWarning(ex,
            "Name resolution failed for component type {Type}, components will show GUIDs",
            group.Key);
    }
}

stopwatch.Stop();
_logger.LogInformation(
    "Total component name resolution: {TypeCount} types, {TotalMs}ms",
    grouped.Count, stopwatch.ElapsedMilliseconds);

return components;
```

Note: Change the `return` on the existing line (the `.ToList()` call) to assign to a variable `var components = ...` instead of returning directly.

- [ ] **Step 3: Fix existing SolutionServiceTests constructor tests**

In `tests/PPDS.Dataverse.Tests/Services/SolutionServiceTests.cs`, update the constructor calls to include the new `IComponentNameResolver` parameter. For example, line 26:

```csharp
var nameResolver = new Mock<IComponentNameResolver>().Object;
var act = () => new SolutionService(null!, logger, metadataService, nameResolver);
```

Apply the same pattern to all constructor tests in this file.

- [ ] **Step 4: Run tests**

Run: `dotnet test PPDS.sln --filter "Category!=Integration" -v q`
Expected: All tests PASS

- [ ] **Step 5: Commit**

```bash
git add src/PPDS.Dataverse/Services/SolutionService.cs tests/PPDS.Dataverse.Tests/Services/SolutionServiceTests.cs
git commit -m "feat: wire ComponentNameResolver into SolutionService.GetComponentsAsync"
```

---

### Task 8: Update RPC DTO and extension types

**Files:**
- Modify: `src/PPDS.Cli/Commands/Serve/Handlers/RpcMethodHandler.cs:1830-1838,2546-2583`
- Modify: `extension/src/types.ts:175-182`

- [ ] **Step 1: Add name fields to SolutionComponentInfoDto**

In `src/PPDS.Cli/Commands/Serve/Handlers/RpcMethodHandler.cs`, after line 2582 (`IsMetadata` property), add:

```csharp
/// <summary>
/// Gets or sets the component display name.
/// </summary>
[JsonPropertyName("displayName")]
public string? DisplayName { get; set; }

/// <summary>
/// Gets or sets the component logical name.
/// </summary>
[JsonPropertyName("logicalName")]
public string? LogicalName { get; set; }

/// <summary>
/// Gets or sets the component schema name.
/// </summary>
[JsonPropertyName("schemaName")]
public string? SchemaName { get; set; }
```

- [ ] **Step 2: Map name fields in SolutionsComponentsAsync**

In the same file, update the mapping at lines 1830-1838. Add after `IsMetadata = c.IsMetadata` (line 1837):

```csharp
DisplayName = c.DisplayName,
LogicalName = c.LogicalName,
SchemaName = c.SchemaName
```

- [ ] **Step 3: Update TypeScript interface**

In `extension/src/types.ts:175-182`, replace:

```typescript
export interface SolutionComponentInfoDto {
    id: string;
    objectId: string;
    componentType: number;
    componentTypeName: string;
    rootComponentBehavior: number;
    isMetadata: boolean;
}
```

with:

```typescript
export interface SolutionComponentInfoDto {
    id: string;
    objectId: string;
    componentType: number;
    componentTypeName: string;
    rootComponentBehavior: number;
    isMetadata: boolean;
    displayName?: string;
    logicalName?: string;
    schemaName?: string;
}
```

- [ ] **Step 4: Run tests**

Run: `dotnet test PPDS.sln --filter "Category!=Integration" -v q && cd extension && npm run ext:test`
Expected: All tests PASS

- [ ] **Step 5: Commit**

```bash
git add src/PPDS.Cli/Commands/Serve/Handlers/RpcMethodHandler.cs extension/src/types.ts
git commit -m "feat: add name fields to SolutionComponentInfoDto and TypeScript interface"
```

---

## Chunk 4: Extension UI (Tasks 9-11)

Update the webview to display component names and detail cards.

### Task 9: Pass name fields from extension host to webview

**AC:** AC-05
**Files:**
- Modify: `extension/src/panels/SolutionsPanel.ts:240-246`

- [ ] **Step 1: Update component mapping in loadComponents**

In `extension/src/panels/SolutionsPanel.ts:240-246`, replace:

```typescript
components: components.map(c => ({
    objectId: c.objectId,
    isMetadata: c.isMetadata,
})),
```

with:

```typescript
components: components.map(c => ({
    objectId: c.objectId,
    isMetadata: c.isMetadata,
    logicalName: c.logicalName,
    schemaName: c.schemaName,
    displayName: c.displayName,
    rootComponentBehavior: c.rootComponentBehavior,
})),
```

- [ ] **Step 2: Run extension tests**

Run: `cd extension && npm run ext:test`
Expected: All tests PASS

- [ ] **Step 3: Commit**

```bash
git add extension/src/panels/SolutionsPanel.ts
git commit -m "feat: forward component name and detail fields from daemon to webview"
```

---

### Task 10: Render component names in webview

**AC:** AC-05, AC-11
**Files:**
- Modify: `extension/src/panels/SolutionsPanel.ts:617-624` (webview `renderComponents` function)

- [ ] **Step 1: Update renderComponents to display names**

In the webview JavaScript inside `SolutionsPanel.ts`, find the `renderComponents` function (line 596). Replace the component item rendering (lines 617-624):

```javascript
for (const comp of group.components) {
    html += '<div class="component-item">';
    html += escapeHtml(comp.objectId);
    if (comp.isMetadata) {
        html += ' <span class="metadata-badge">metadata</span>';
    }
    html += '</div>';
}
```

with:

```javascript
for (const comp of group.components) {
    var name = comp.logicalName || comp.schemaName || comp.displayName || comp.objectId;
    var subtitle = '';
    if (comp.logicalName && comp.displayName && comp.displayName !== comp.logicalName) {
        subtitle = ' (' + escapeHtml(comp.displayName) + ')';
    }

    html += '<div class="component-item" tabindex="0" data-object-id="' + escapeAttr(comp.objectId) + '">';
    html += '<span class="component-name">' + escapeHtml(name) + subtitle + '</span>';
    if (comp.isMetadata) {
        html += ' <span class="metadata-badge">metadata</span>';
    }
    html += '</div>';
}
```

Note: All dynamic values go through `escapeHtml()` or `escapeAttr()` (Constitution S1).

- [ ] **Step 2: Run extension tests**

Run: `cd extension && npm run ext:test`
Expected: All tests PASS

- [ ] **Step 3: Commit**

```bash
git add extension/src/panels/SolutionsPanel.ts
git commit -m "feat: display component names with logicalName > schemaName > displayName priority"
```

---

### Task 11: Add click-to-expand detail card

**AC:** AC-06, AC-07, AC-13
**Files:**
- Modify: `extension/src/panels/SolutionsPanel.ts` (webview JavaScript section)

- [ ] **Step 1: Add detail card rendering in renderComponents**

After the component item `</div>`, add a hidden detail card div. Update the component item rendering from Task 10 to include the detail card:

```javascript
for (const comp of group.components) {
    var name = comp.logicalName || comp.schemaName || comp.displayName || comp.objectId;
    var subtitle = '';
    if (comp.logicalName && comp.displayName && comp.displayName !== comp.logicalName) {
        subtitle = ' (' + escapeHtml(comp.displayName) + ')';
    }

    html += '<div class="component-item" tabindex="0" data-object-id="' + escapeAttr(comp.objectId) + '">';
    html += '<span class="component-name">' + escapeHtml(name) + subtitle + '</span>';
    if (comp.isMetadata) {
        html += ' <span class="metadata-badge">metadata</span>';
    }
    html += '</div>';

    // Detail card (hidden by default)
    html += '<div class="component-detail-card" data-detail-for="' + escapeAttr(comp.objectId) + '">';
    if (comp.logicalName) {
        html += '<span class="detail-label">Logical Name</span><span class="detail-value">' + escapeHtml(comp.logicalName) + '</span>';
    }
    if (comp.schemaName) {
        html += '<span class="detail-label">Schema Name</span><span class="detail-value">' + escapeHtml(comp.schemaName) + '</span>';
    }
    if (comp.displayName) {
        html += '<span class="detail-label">Display Name</span><span class="detail-value">' + escapeHtml(comp.displayName) + '</span>';
    }
    html += '<span class="detail-label">Object ID</span><span class="detail-value">' + escapeHtml(comp.objectId) + ' <button class="copy-btn" data-copy="' + escapeAttr(comp.objectId) + '">&#128203;</button></span>';
    html += '<span class="detail-label">Root Behavior</span><span class="detail-value">' + comp.rootComponentBehavior + '</span>';
    html += '<span class="detail-label">Metadata</span><span class="detail-value">' + (comp.isMetadata ? 'Yes' : 'No') + '</span>';
    html += '</div>';
}
```

Note: `rootComponentBehavior` must also be passed through from the host mapping (add to Task 9's mapping if not already there).

- [ ] **Step 2: Add CSS for detail card**

Find the `<style>` section in `getHtmlContent` and add:

```css
.component-detail-card {
    display: none;
    padding: 4px 8px 4px 28px;
    margin: 0 0 2px 0;
    background: var(--vscode-editor-background);
    border-left: 2px solid var(--vscode-focusBorder);
    font-size: 12px;
}
.component-detail-card.expanded {
    display: grid;
    grid-template-columns: auto 1fr;
    gap: 2px 8px;
}
.component-item { cursor: pointer; }
.component-item:focus { outline: 1px solid var(--vscode-focusBorder); outline-offset: -1px; }
.copy-btn {
    background: none;
    border: none;
    cursor: pointer;
    padding: 0 4px;
    color: var(--vscode-foreground);
    font-size: 12px;
}
.copy-btn:hover { color: var(--vscode-textLink-foreground); }
```

- [ ] **Step 3: Add click and keyboard event handlers**

In the webview JavaScript, add event delegation for component items and copy buttons. Add after the existing event listeners:

```javascript
// Component item click → toggle detail card
content.addEventListener('click', (e) => {
    var item = e.target.closest('.component-item');
    if (!item) return;

    var copyBtn = e.target.closest('.copy-btn');
    if (copyBtn) {
        // Copy button clicked
        var text = copyBtn.dataset.copy;
        navigator.clipboard.writeText(text).then(() => {
            var original = copyBtn.innerHTML;
            copyBtn.textContent = '\\u2713';
            setTimeout(() => { copyBtn.innerHTML = original; }, 1500);
        });
        e.stopPropagation();
        return;
    }

    var objectId = item.dataset.objectId;
    var detailCard = content.querySelector('.component-detail-card[data-detail-for="' + cssEscape(objectId) + '"]');
    if (!detailCard) return;

    // Collapse any other expanded card
    var expanded = content.querySelector('.component-detail-card.expanded');
    if (expanded && expanded !== detailCard) {
        expanded.classList.remove('expanded');
    }

    detailCard.classList.toggle('expanded');
});

// Keyboard: Enter/Space on component item
content.addEventListener('keydown', (e) => {
    if (e.key !== 'Enter' && e.key !== ' ') return;
    var item = e.target.closest('.component-item');
    if (!item) return;
    e.preventDefault();
    item.click();
});

```

Note: The copy button is handled within the component click handler via `e.target.closest('.copy-btn')` check with `e.stopPropagation()`. Do NOT add a separate copy button handler — that would cause duplicate clipboard writes.

- [ ] **Step 4: Run extension tests**

Run: `cd extension && npm run ext:test`
Expected: All tests PASS

- [ ] **Step 5: Manual smoke test**

Open the extension in VS Code, connect to a Dataverse environment, open the Solutions panel, expand a solution. Verify:
- Components show display names instead of GUIDs
- Clicking a component shows the detail card
- Clicking another collapses the first
- Copy button copies the GUID
- Enter/Space on a focused component toggles the card
- Check the PPDS output channel for timing logs

- [ ] **Step 6: Commit**

```bash
git add extension/src/panels/SolutionsPanel.ts
git commit -m "feat: add click-to-expand detail cards with copy and keyboard support"
```

---

## Chunk 5: Final verification (Task 12)

### Task 12: Run full test suite and verify all ACs

- [ ] **Step 1: Run all .NET tests**

Run: `dotnet test PPDS.sln --filter "Category!=Integration" -v q`
Expected: All tests PASS

- [ ] **Step 2: Run all extension tests**

Run: `cd extension && npm run ext:test`
Expected: All tests PASS

- [ ] **Step 3: Verify AC coverage**

Review acceptance criteria against implemented tests:

| AC | Status |
|----|--------|
| AC-01 | `ComponentNameResolverTests.ResolveAsync_EntityType_UsesMetadataProvider` |
| AC-02 | Requires integration test (table query) — verify manually |
| AC-03 | `ComponentNameResolverTests.ResolveAsync_UnmappedType_ReturnsEmptyDictionary` |
| AC-04 | Fixed bare catch + cache key — verify manually against live environment |
| AC-05 | Webview priority logic in renderComponents |
| AC-06 | Detail card toggle with single-expand |
| AC-07 | Copy button with clipboard API |
| AC-08 | ServeCommand now passes loggerFactory |
| AC-09 | `ComponentNameResolverTests.ResolveAsync_LogsTiming` |
| AC-10 | Batch splitting in `ResolveFromTableAsync` (MaxBatchSize = 100) |
| AC-11 | All values go through `escapeHtml()` |
| AC-12 | `ComponentNameResolverTests.ResolveAsync_PartialFailure_ReturnsEmptyAndLogsWarning` |
| AC-13 | Keyboard handler for Enter/Space |
| AC-14 | `EntitySummary_HasMetadataIdProperty` |

- [ ] **Step 4: Final commit with any cleanup**

Review `git status` and stage only relevant files:

```bash
git status
# Stage specific changed files, NOT git add -A
git commit -m "chore: final cleanup for component name resolution feature"
```

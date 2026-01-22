# Migration

**Status:** Implemented
**Version:** 1.0
**Last Updated:** 2026-01-21
**Code:** [src/PPDS.Migration/](../src/PPDS.Migration/)

---

## Overview

The Migration system provides high-performance import and export of Dataverse data with automatic dependency resolution, circular reference handling, and comprehensive error diagnostics. It uses the CMT (Configuration Migration Tool) ZIP format for interoperability with Microsoft tools.

### Goals

- **Dependency-Aware Import**: Automatically resolve entity dependencies and import in correct order
- **Performance**: Use bulk APIs (CreateMultiple/UpdateMultiple) for 5x faster throughput than ExecuteMultiple
- **Error Diagnostics**: Preserve RecordId in errors, detect patterns, generate retry manifests
- **Format Compatibility**: Read/write CMT-format ZIP files for tool interoperability

### Non-Goals

- GUI-based mapping (deferred to TUI/CLI interaction)
- Real-time sync (batch-oriented design only)
- Cross-environment user synchronization (uses user mapping files instead)

---

## Architecture

```
┌─────────────────────────────────────────────────────────────────────────────┐
│                              IMPORT PIPELINE                                 │
├─────────────────────────────────────────────────────────────────────────────┤
│  data.zip ──▶ CmtDataReader ──▶ DependencyGraphBuilder ──▶ ExecutionPlan   │
│                                   (Tarjan's SCC)           (Ordered Tiers)  │
└─────────────────────────────────────────────────────────────────────────────┘
                                        │
        ┌───────────────────────────────┼───────────────────────────────┐
        │                               │                               │
        ▼                               ▼                               ▼
┌─────────────────┐         ┌─────────────────────┐         ┌─────────────────┐
│   PHASE 1       │         │      PHASE 2        │         │     PHASE 3     │
│  Entity Import  │─────────│  Deferred Fields    │─────────│  M2M Relations  │
│  (Tiered+Bulk)  │         │  (Self-References)  │         │  (Associations) │
└─────────────────┘         └─────────────────────┘         └─────────────────┘
        │                               │                               │
        │                               │                               │
        └───────────────────────────────┼───────────────────────────────┘
                                        │
                                        ▼
                              ┌─────────────────────┐
                              │    ImportResult     │
                              │  + ErrorReport      │
                              │  + RetryManifest    │
                              └─────────────────────┘

┌─────────────────────────────────────────────────────────────────────────────┐
│                              EXPORT PIPELINE                                 │
├─────────────────────────────────────────────────────────────────────────────┤
│  schema.xml ──▶ CmtSchemaReader ──▶ ParallelExporter ──▶ CmtDataWriter     │
│                                    (Parallel.ForEachAsync)   (data.zip)    │
└─────────────────────────────────────────────────────────────────────────────┘
```

### Components

| Component | Responsibility |
|-----------|----------------|
| `TieredImporter` | Main import orchestrator; 3-phase pipeline |
| `ParallelExporter` | Parallel FetchXML-based export |
| `DependencyGraphBuilder` | Tarjan's algorithm for cycle detection |
| `ExecutionPlanBuilder` | Topological sort for tier ordering |
| `DeferredFieldProcessor` | Phase 2: self-referential lookups |
| `RelationshipProcessor` | Phase 3: M2M associations |
| `BulkOperationProber` | Detects bulk API support per entity |
| `CmtDataReader`/`CmtDataWriter` | CMT ZIP format I/O |
| `ErrorReportWriter` | Structured JSON error reports |

### Dependencies

- Depends on: [connection-pool.md](./connection-pool.md) for parallel operations
- Depends on: [authentication.md](./authentication.md) for connection sources
- Uses patterns from: [architecture.md](./architecture.md)

---

## Specification

### Core Requirements

1. **Tiered Import**: Process entities in dependency order; dependencies must exist before dependents
2. **Circular Reference Handling**: Detect cycles using Tarjan's SCC algorithm; defer self-referential fields
3. **Bulk Operation Fallback**: Probe entities for bulk API support; fall back to individual operations for unsupported entities (team, queue)
4. **RecordId Preservation**: Track original GUIDs through entire pipeline for error correlation
5. **Continue-on-Error**: Support partial success with comprehensive failure tracking

### Primary Flows

**Import Flow:**

1. **Parse** ([`CmtDataReader.ReadAsync`](../src/PPDS.Migration/Formats/CmtDataReader.cs)): Extract schema and records from ZIP
2. **Analyze** ([`DependencyGraphBuilder.Build:33-153`](../src/PPDS.Migration/Analysis/DependencyGraphBuilder.cs#L33-L153)): Build dependency graph, detect circular references
3. **Plan** ([`ExecutionPlanBuilder.Build`](../src/PPDS.Migration/Analysis/ExecutionPlanBuilder.cs)): Generate tiered execution plan via topological sort
4. **Phase 1** ([`TieredImporter.ProcessTiersAsync:342-454`](../src/PPDS.Migration/Import/TieredImporter.cs#L342-L454)): Import entities tier-by-tier using bulk APIs
5. **Phase 2** ([`DeferredFieldProcessor.ProcessAsync:48-144`](../src/PPDS.Migration/Import/DeferredFieldProcessor.cs#L48-L144)): Update self-referential lookups
6. **Phase 3** ([`RelationshipProcessor.ProcessAsync`](../src/PPDS.Migration/Import/RelationshipProcessor.cs)): Create M2M associations

**Export Flow:**

1. **Parse** ([`CmtSchemaReader.ReadAsync`](../src/PPDS.Migration/Formats/CmtSchemaReader.cs)): Load entity schema from XML
2. **Export** ([`ParallelExporter.ExportAsync:90-224`](../src/PPDS.Migration/Export/ParallelExporter.cs#L90-L224)): Parallel FetchXML queries per entity
3. **M2M Export** ([`ParallelExporter.ExportM2MRelationshipsAsync:317-377`](../src/PPDS.Migration/Export/ParallelExporter.cs#L317-L377)): Query intersect entities
4. **Write** ([`CmtDataWriter.WriteAsync`](../src/PPDS.Migration/Formats/CmtDataWriter.cs)): Create CMT-format ZIP

### Constraints

- Maximum 4 parallel entities per tier (default `MaxParallelEntities`)
- Deferred fields must be self-referential lookups only
- M2M relationship source records must exist in export set
- Plugin steps disabled during import are re-enabled in finally block

### Phase Processing

| Phase | Processor | Input | Output |
|-------|-----------|-------|--------|
| 1 | `TieredImporter` | Tiered entity records | `IdMappingCollection` |
| 2 | `DeferredFieldProcessor` | Records with self-refs | Updated lookup fields |
| 3 | `RelationshipProcessor` | M2M relationship data | AssociateRequest results |

---

## Core Types

### IImporter

Main interface for import operations ([`IImporter.cs:11-43`](../src/PPDS.Migration/Import/IImporter.cs#L11-L43)).

```csharp
public interface IImporter
{
    Task<ImportResult> ImportAsync(
        string dataPath,
        ImportOptions? options = null,
        IProgressReporter? progress = null,
        CancellationToken cancellationToken = default);
}
```

### IExporter

Main interface for export operations ([`IExporter.cs:9-44`](../src/PPDS.Migration/Export/IExporter.cs#L9-L44)).

```csharp
public interface IExporter
{
    Task<ExportResult> ExportAsync(
        string schemaPath,
        string outputPath,
        ExportOptions? options = null,
        IProgressReporter? progress = null,
        CancellationToken cancellationToken = default);
}
```

### ExecutionPlan

Dependency-ordered import plan ([`ExecutionPlan.cs:9-49`](../src/PPDS.Migration/Models/ExecutionPlan.cs#L9-L49)).

```csharp
public class ExecutionPlan
{
    public IReadOnlyList<ImportTier> Tiers { get; set; }
    public IReadOnlyDictionary<string, IReadOnlyList<string>> DeferredFields { get; set; }
    public IReadOnlyList<RelationshipSchema> ManyToManyRelationships { get; set; }
}
```

### IdMappingCollection

Thread-safe GUID remapping cache ([`IdMapping.cs`](../src/PPDS.Migration/Models/IdMapping.cs)).

```csharp
// Populated during Phase 1, read during Phase 2 and Phase 3
idMappings.AddMapping(entityName, oldId, newId);
idMappings.TryGetNewId(entityName, oldId, out var newId);
```

### Usage Pattern

```csharp
// Import with options
var importer = services.GetRequiredService<IImporter>();
var result = await importer.ImportAsync(
    "data.zip",
    new ImportOptions
    {
        Mode = ImportMode.Upsert,
        UseBulkApis = true,
        ContinueOnError = true
    },
    progressReporter);

// Export with schema
var exporter = services.GetRequiredService<IExporter>();
await exporter.ExportAsync("schema.xml", "output.zip");
```

---

## Error Handling

### Error Types

| Error | Condition | Recovery |
|-------|-----------|----------|
| `SchemaMismatchException` | Exported columns missing in target | Use `SkipMissingColumns = true` or update target schema |
| `MigrationError` | Individual record failure | Check `ErrorCode` and `RecordId`; retry failed records |
| Bulk not supported | Entity doesn't support CreateMultiple | Automatic fallback to individual operations |
| `MISSING_USER` pattern | User reference can't be resolved | Provide user mapping file |
| `DUPLICATE_RECORD` pattern | Record already exists | Use `ImportMode.Upsert` |

### Error Report Structure

The error report ([`ErrorReport.cs:10-243`](../src/PPDS.Migration/Progress/ErrorReport.cs#L10-L243)) provides comprehensive diagnostics:

```json
{
  "version": "1.1",
  "generatedAt": "2026-01-21T12:00:00Z",
  "sourceFile": "data.zip",
  "targetEnvironment": "https://org.crm.dynamics.com",
  "executionContext": {
    "cliVersion": "1.2.3",
    "sdkVersion": "1.2.3",
    "runtimeVersion": "8.0.1",
    "platform": "Windows 10.0.22631",
    "importMode": "Upsert"
  },
  "summary": {
    "totalRecords": 43710,
    "successCount": 28310,
    "failureCount": 15400,
    "errorPatterns": { "MISSING_USER": 15000, "PERMISSION_DENIED": 400 }
  },
  "errors": [
    {
      "entityLogicalName": "account",
      "recordId": "a1b2c3d4-...",
      "recordIndex": 42,
      "errorCode": -2147220969,
      "message": "...",
      "pattern": "MISSING_USER"
    }
  ],
  "retryManifest": {
    "failedRecordsByEntity": {
      "account": ["guid1", "guid2"]
    }
  }
}
```

### Recovery Strategies

- **Schema mismatch**: Enable `SkipMissingColumns` to import partial data
- **User not found**: Provide user mapping CSV or enable `StripOwnerFields`
- **Bulk not supported**: Automatic—prober detects and falls back
- **Partial failure**: Use `retryManifest` as input filter for subsequent import

### Edge Cases

| Scenario | Expected Behavior |
|----------|-------------------|
| Circular A → B → A | Both placed in same tier; lookups deferred |
| Self-reference (A → A) | Lookup nulled in Phase 1; updated in Phase 2 |
| M2M duplicate association | Treated as idempotent success (0x80040237) |
| Empty entity data | Skipped with no error |
| All bulk failures | Fall back to individual operations for entire entity |

---

## Design Decisions

### Why Tiered Import with Tarjan's Algorithm?

**Context:** Dataverse lookups require target records to exist before creating references. With arbitrary entity dependencies, incorrect import order causes failures.

**Decision:** Use Tarjan's Strongly Connected Components algorithm ([`DependencyGraphBuilder.cs:155-243`](../src/PPDS.Migration/Analysis/DependencyGraphBuilder.cs#L155-L243)) to detect cycles, then Kahn's topological sort ([`DependencyGraphBuilder.cs:245-356`](../src/PPDS.Migration/Analysis/DependencyGraphBuilder.cs#L245-L356)) on the condensed graph.

**Algorithm flow:**
1. Build entity dependency graph from lookup fields
2. Run Tarjan's SCC to find circular references
3. Condense SCCs into single nodes
4. Topological sort for tier ordering
5. Expand SCCs back to entities

**Alternatives considered:**
- Manual tier configuration: Rejected—error-prone, doesn't scale
- Two-pass import (create all, then update): Rejected—doubles write operations

**Consequences:**
- Positive: Automatic handling of any dependency structure; circular references handled gracefully
- Negative: Analysis phase adds startup time; complex algorithm requires understanding for debugging

---

### Why Bulk Operation Probe-Once Pattern?

**Context:** Some entities (team, queue) don't support CreateMultiple/UpdateMultiple. Previously, all records were sent before detecting failure, wasting 117 records in production scenarios.

**Decision:** Probe with first record before bulk processing ([`BulkOperationProber.cs:73-127`](../src/PPDS.Migration/Import/BulkOperationProber.cs#L73-L127)). If probe fails with "not enabled on entity", fall back to individual operations for ALL records including probe.

**Flow:**
1. Send first record to bulk API
2. If failure contains "is not enabled on the entity" or "does not support entities of type"
3. Cache result, fall back to individual operations
4. Otherwise, process remaining records in bulk

**Alternatives considered:**
- Hardcoded list of unsupported entities: Rejected—requires maintenance, misses new entities
- Metadata query for bulk capability: Rejected—SDK doesn't expose this information

**Consequences:**
- Positive: Reduces wasted records from N to 1 for unsupported entities
- Negative: Probe adds minimal latency for first record per entity

---

### Why Preserve RecordId in Error Reports?

**Context:** Large imports (78,681 records) showed failure counts without identifying WHICH records failed. Only batch position was shown, making correlation to source data impossible.

**Decision:** Preserve RecordId (GUID) through entire error pipeline ([`ErrorReport.cs:170-215`](../src/PPDS.Migration/Progress/ErrorReport.cs#L170-L215)). Include in structured error report with retry manifest.

**Report includes:**
- `RecordId`: Original GUID from source data
- `RecordIndex`: Position in batch (for batch correlation)
- `Pattern`: Detected error pattern (MISSING_USER, DUPLICATE_RECORD, etc.)
- `RetryManifest`: Failed RecordIds grouped by entity for retry input

**Alternatives considered:**
- Log only error messages: Rejected—can't identify specific records
- Include full record data: Rejected—PII concerns, file size

**Consequences:**
- Positive: Users can identify exactly which records failed; enables automated retry workflows
- Negative: Error report files larger; requires memory for RecordId tracking

---

### Why CSV Mapping Schema Versioning?

**Context:** JSON mapping files configure CSV-to-Dataverse imports. Schema evolves with features; need to upgrade CLI without breaking existing files.

**Decision:** Semantic versioning with compatibility rules in mapping files.

**Version format:** `major.minor` (e.g., "1.0", "1.1", "2.0")

| CLI Version vs File | Behavior |
|---------------------|----------|
| Same version | Silent proceed (exit 0) |
| CLI newer (minor) | Silent proceed (exit 0) |
| CLI older (minor) | Warning to stderr (exit 0) |
| Different major | Error with suggestion (exit 8) |

**Schema features:**
- `$schema` URL for editor validation
- `_` prefix for metadata fields (`_status`, `_note`, `_csvSample`)
- `[JsonExtensionData]` for forward compatibility

**Consequences:**
- Positive: Clear upgrade path; non-breaking minor updates; human-friendly metadata
- Negative: Version maintenance burden; minor version warnings may confuse users

---

### Why Error Report v1.1 with Execution Context?

**Context:** Production import analysis revealed missing version info—couldn't correlate issues to specific CLI builds.

**Decision:** Version 1.1 adds `executionContext` block ([`ErrorReport.cs:62-107`](../src/PPDS.Migration/Progress/ErrorReport.cs#L62-L107)):

```json
"executionContext": {
  "cliVersion": "1.2.3",
  "sdkVersion": "1.2.3",
  "runtimeVersion": "8.0.1",
  "platform": "Windows 10.0.22631",
  "importMode": "Upsert",
  "stripOwnerFields": true,
  "bypassPlugins": false
}
```

**Consequences:**
- Positive: Build correlation for debugging; reproduction context captured
- Negative: Report JSON slightly larger; consumers should check version field

---

## Extension Points

### Bulk Operations Pattern

Parallel bulk operations with pool-managed concurrency:

```csharp
public async Task<BulkOperationResult> CreateRecordsAsync(
    string entityLogicalName,
    IReadOnlyList<Entity> entities,
    IProgressReporter? progress = null,
    CancellationToken cancellationToken = default)
{
    // Get parallelism from pool, not hardcoded
    var parallelism = _connectionPool.GetTotalRecommendedParallelism();
    var batches = entities.Chunk(1000).ToList();

    var successCount = 0;
    var failedRecords = new ConcurrentBag<FailedRecord>();

    await Parallel.ForEachAsync(
        batches,
        new ParallelOptions { MaxDegreeOfParallelism = parallelism, CancellationToken = cancellationToken },
        async (batch, ct) =>
        {
            // Get client INSIDE the parallel loop
            await using var client = await _connectionPool.GetClientAsync(ct);

            var request = new CreateMultipleRequest
            {
                Targets = new EntityCollection(batch.ToList())
            };

            var response = (CreateMultipleResponse)await client.ExecuteAsync(request, ct);

            // Handle partial failures - CreateMultiple can succeed partially
            if (response.Responses != null)
            {
                foreach (var item in response.Responses)
                {
                    if (item.Fault != null)
                        failedRecords.Add(new FailedRecord(batch[item.RequestIndex], item.Fault));
                    else
                        Interlocked.Increment(ref successCount);
                }
            }
            else
            {
                Interlocked.Add(ref successCount, batch.Count());
            }
        });

    return new BulkOperationResult { SuccessCount = successCount, FailedRecords = failedRecords.ToList() };
}
```

**Error handling strategies:**
- Throttle (429): Wait and retry (pool handles automatically)
- Auth error: Limited retries, may need re-authentication
- Deadlock: Retry with backoff

### Adding a New Import Phase Processor

1. **Implement IImportPhaseProcessor** ([`IImportPhaseProcessor.cs`](../src/PPDS.Migration/Import/IImportPhaseProcessor.cs)):

```csharp
public class CustomPhaseProcessor : IImportPhaseProcessor
{
    public string PhaseName => "Custom Processing";

    public async Task<PhaseResult> ProcessAsync(
        ImportContext context,
        CancellationToken cancellationToken)
    {
        // Access context.Data, context.IdMappings, context.Options
        return new PhaseResult { Success = true, ... };
    }
}
```

2. **Wire into TieredImporter** after Phase 3

### Adding a New Data Format

1. **Implement ICmtDataReader/ICmtDataWriter** interfaces
2. **Register in ServiceCollectionExtensions**
3. **Format detection based on file extension**

---

## Configuration

### ImportOptions

| Setting | Type | Default | Description |
|---------|------|---------|-------------|
| `UseBulkApis` | bool | true | Use CreateMultiple/UpdateMultiple vs individual |
| `Mode` | ImportMode | Upsert | Create, Update, or Upsert |
| `ContinueOnError` | bool | true | Continue after individual record failures |
| `MaxParallelEntities` | int | 4 | Parallel entities per tier |
| `BypassCustomPlugins` | CustomLogicBypass | None | Skip custom plugins (requires privilege) |
| `BypassPowerAutomateFlows` | bool | false | Skip Power Automate triggers |
| `StripOwnerFields` | bool | false | Remove ownerid, createdby, etc. |
| `SkipMissingColumns` | bool | false | Skip columns not in target schema |
| `UserMappings` | UserMappingCollection | null | User GUID remapping |
| `RespectDisablePluginsSetting` | bool | true | Honor schema disableplugins attribute |

Configuration defined in [`ImportOptions.cs:11-171`](../src/PPDS.Migration/Import/ImportOptions.cs#L11-L171).

### ExportOptions

| Setting | Type | Default | Description |
|---------|------|---------|-------------|
| `DegreeOfParallelism` | int | ProcessorCount | Parallel entity exports |
| `PageSize` | int | 500 | FetchXML page size |
| `ProgressInterval` | int | 1000 | Report progress every N records |

---

## Testing

### Acceptance Criteria

- [ ] Circular references detected and handled (A → B → A)
- [ ] Self-referential lookups deferred and updated in Phase 2
- [ ] Bulk probe detects unsupported entities (team, queue)
- [ ] RecordId preserved in error reports
- [ ] Retry manifest contains all failed record GUIDs
- [ ] M2M relationships created after entity import

### Edge Cases

| Scenario | Input | Expected Output |
|----------|-------|-----------------|
| Circular dependency | account → contact → account | Both in same tier; lookups deferred |
| Self-reference | account.parentaccountid → account | Field nulled Phase 1; updated Phase 2 |
| Bulk not supported | team entity | Probe detects; individual fallback |
| All records fail | Invalid data | Comprehensive error report |
| User not mapped | Missing systemuser | MISSING_USER pattern detected |

### Test Examples

```csharp
[Fact]
public void Build_SelfReferentialEntity_DetectsCircularReference()
{
    // Arrange
    var schema = new MigrationSchema
    {
        Entities = new[]
        {
            new EntitySchema
            {
                LogicalName = "account",
                Fields = new[]
                {
                    new FieldSchema
                    {
                        LogicalName = "parentaccountid",
                        Type = "lookup",
                        LookupEntity = "account"
                    }
                }
            }
        }
    };

    // Act
    var graph = new DependencyGraphBuilder().Build(schema);

    // Assert
    Assert.Single(graph.CircularReferences);
    Assert.Contains("account", graph.CircularReferences[0].Entities);
}

[Fact]
public static void IsBulkNotSupportedFailure_TeamError_ReturnsTrue()
{
    // Arrange
    var result = new BulkOperationResult
    {
        FailureCount = 1,
        Errors = new[]
        {
            new BulkOperationError
            {
                Message = "CreateMultiple is not enabled on the entity team"
            }
        }
    };

    // Act & Assert
    Assert.True(BulkOperationProber.IsBulkNotSupportedFailure(result, 1));
}
```

---

## Related Specs

- [connection-pool.md](./connection-pool.md) - Pool provides parallel execution capacity
- [authentication.md](./authentication.md) - Connection sources for pool
- [application-services.md](./application-services.md) - IProgressReporter integration
- [cli.md](./cli.md) - `ppds data import`/`export` commands

---

## Roadmap

- Incremental/delta export based on modifiedon timestamps
- Parallel Phase 2 processing (currently sequential per entity)
- Direct S3/Azure Blob input/output for large datasets
- Real-time streaming import for continuous sync scenarios

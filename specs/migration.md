# Migration

**Status:** Implemented
**Last Updated:** 2026-01-28
**Surfaces:** CLI, TUI
**Code:** [src/PPDS.Migration/](../src/PPDS.Migration/), TUI: [src/PPDS.Tui/Screens/MigrationScreen.cs](../src/PPDS.Tui/Screens/MigrationScreen.cs)

---

## Overview

The migration system provides high-performance data export and import between Dataverse environments. It uses dependency analysis to determine correct import ordering, handles circular references through deferred field processing, and supports the Configuration Migration Tool (CMT) format for interoperability with Microsoft tooling.

### Goals

- **Performance**: Parallel export with entity-level concurrency; parallel import with tier-level and entity-level parallelism
- **Correctness**: Dependency-aware import ordering via topological sort; deferred fields for circular references
- **Interoperability**: Full CMT format support for compatibility with Microsoft's Configuration Migration Tool

### Non-Goals

- Solution export/import (handled by Dataverse platform)
- Schema migration or table creation (schema must exist in target)
- Cross-environment relationship resolution (IDs must match or be mapped)

---

## Architecture

```
┌─────────────────────────────────────────────────────────────────────────────────┐
│                            Application Layer                                      │
│               (CLI: ppds data export/import, MCP Tools)                          │
└────────────────────────────────────┬────────────────────────────────────────────┘
                                     │
         ┌───────────────────────────┴───────────────────────────┐
         │                                                       │
         ▼                                                       ▼
┌─────────────────────────────┐              ┌─────────────────────────────────────┐
│        IExporter            │              │           IImporter                  │
│   ParallelExporter          │              │         TieredImporter               │
│  ┌────────────────────────┐ │              │  ┌─────────────────────────────────┐│
│  │ Entity-level parallel  │ │              │  │ Phase 1: Tiered Entity Import   ││
│  │ FetchXML pagination    │ │              │  │ Phase 2: Deferred Field Update  ││
│  │ M2M relationship export│ │              │  │ Phase 3: M2M Relationship Create││
│  └────────────────────────┘ │              │  └─────────────────────────────────┘│
└──────────────┬──────────────┘              └──────────────────┬──────────────────┘
               │                                                │
               │                                                │
               ▼                                                ▼
┌─────────────────────────────┐              ┌─────────────────────────────────────┐
│     ICmtSchemaReader        │              │       IDependencyGraphBuilder       │
│     ICmtDataWriter          │              │       IExecutionPlanBuilder         │
│  ┌────────────────────────┐ │              │  ┌─────────────────────────────────┐│
│  │ schema.xml parsing     │ │              │  │ Tarjan's SCC for cycles         ││
│  │ data.zip generation    │ │              │  │ Kahn's algorithm for tiers      ││
│  └────────────────────────┘ │              │  │ Deferred field identification   ││
└─────────────────────────────┘              │  └─────────────────────────────────┘│
                                             └──────────────────┬──────────────────┘
                                                                │
                                                                ▼
                                             ┌─────────────────────────────────────┐
                                             │     IDataverseConnectionPool        │
                                             │     IBulkOperationExecutor          │
                                             └─────────────────────────────────────┘
```

### Components

| Component | Responsibility |
|-----------|----------------|
| `ParallelExporter` | Parallel data extraction with FetchXML pagination |
| `TieredImporter` | Three-phase import orchestration with dependency ordering |
| `DependencyGraphBuilder` | Builds dependency graph using Tarjan's SCC algorithm |
| `ExecutionPlanBuilder` | Converts graph to executable plan with deferred fields |
| `DeferredFieldProcessor` | Updates self-referential lookups after entity creation |
| `RelationshipProcessor` | Creates M2M associations after all entities exist |
| `SchemaValidator` | Validates export schema against target environment |
| `BulkOperationProber` | Detects per-entity bulk API support |
| `CmtDataReader/Writer` | CMT format serialization |
| `CmtSchemaReader/Writer` | CMT schema XML handling |
| `PluginStepManager` | Disable/enable plugin steps during import |

### Dependencies

- Depends on: [connection-pooling.md](./connection-pooling.md) for client management
- Depends on: [bulk-operations.md](./bulk-operations.md) for high-throughput import
- Uses patterns from: [architecture.md](./architecture.md) for progress reporting

---

## Specification

### Core Requirements

1. **Parallel export**: Entities exported concurrently with FetchXML pagination; M2M relationships exported per entity
2. **Dependency analysis**: Build topologically sorted tiers using Tarjan's SCC algorithm for cycle detection
3. **Three-phase import**: Entities first (respecting tier order), then deferred fields, then M2M relationships
4. **CMT format compatibility**: Read/write Microsoft Configuration Migration Tool ZIP archives
5. **Schema validation**: Detect missing columns in target; optionally skip or fail

### Primary Flows

**Export Flow:**

1. **Parse schema**: Read schema.xml via `ICmtSchemaReader.ReadAsync()`
2. **Parallel entity export**: For each entity concurrently via `Parallel.ForEachAsync()`:
   - Acquire pooled client
   - Build FetchXML with fields from schema
   - Execute paginated queries with paging cookies
   - Store records in thread-safe collection
3. **Export M2M relationships**: For each entity with M2M relationships:
   - Query intersect entity for associations
   - Filter to exported source records
   - Group by source ID
4. **Write output**: Create data.zip with data.xml, data_schema.xml, [Content_Types].xml

**Import Flow:**

1. **Read data**: Load CMT ZIP via `ICmtDataReader.ReadAsync()`
2. **Build dependency graph**: Analyze lookup relationships, detect cycles
3. **Build execution plan**: Topological sort into tiers, identify deferred fields
4. **Phase 1 - Entity Import** (sequential tiers, parallel within tier):
   - Validate schema against target environment
   - Optionally disable plugins
   - For each tier (sequential):
     - For each entity (parallel, default 4):
       - Prepare records (remap lookups, null deferred fields)
       - Execute via bulk API with probe fallback
       - Track ID mappings (source → target)
5. **Phase 2 - Deferred Fields**:
   - For each entity with deferred fields:
     - Build update batch using Phase 1 ID mappings
     - Execute updates
6. **Phase 3 - M2M Relationships**:
   - For each M2M relationship (parallel):
     - Map source/target IDs
     - Execute associate requests
     - Handle duplicates as idempotent success
7. **Re-enable plugins** if disabled

### Constraints

- Import requires schema to exist in target environment
- Bulk API support varies by entity (probed at runtime)
- Standard tables: all-or-nothing per batch; elastic tables: partial success
- M2M associations created only after all entities exist
- Self-referential lookups must be deferred (circular dependency)

### Validation Rules

| Field | Rule | Error |
|-------|------|-------|
| Schema entities | At least 1 required | `ArgumentException` |
| Output path | Must be valid file path | `ArgumentException` |
| Import data | Must have schema and entity data | `InvalidDataException` |
| Target environment | Must have entities defined | `SchemaMismatchException` |

### TUI Surface

The `MigrationScreen` provides an interactive Terminal.Gui interface for configuring and monitoring export/import operations. It lives under **Tools > Data Migration** in TuiShell, replacing the "Coming Soon" placeholder.

**Key components:**

| Component | Responsibility |
|-----------|----------------|
| `MigrationScreen` | Main screen: mode selection, configuration panel, real-time progress, results display |
| `ExecutionPlanPreviewDialog` | Modal showing tier ordering, deferred fields, and M2M relationships before import starts |
| `TuiMigrationProgressReporter` | Adapts `IProgressReporter` to TUI by marshaling all callbacks via `Application.MainLoop.Invoke()` |

**Layout:** RadioGroup (Export/Import) at top; configuration panel below (file paths, options per mode); progress area with phase label, entity progress bars, rate/ETA; results area showing per-entity success/failure/warning counts.

**Hotkeys:**

| Key | Action |
|-----|--------|
| Ctrl+Enter | Start export or import operation |
| Ctrl+P | Open ExecutionPlanPreviewDialog (import mode only) |
| Escape | Cancel running operation (with confirmation) |

**Core types:**

- `MigrationScreen` — implements `ITuiScreen` and `ITuiStateCapture<MigrationScreenState>`; title is "Data Migration - {environment}"
- `MigrationScreenState` — captures mode, operation state, paths, current phase/entity, record counts, rate, ETA, elapsed, error/warning counts
- `MigrationMode` — `Export` | `Import`
- `MigrationOperationState` — `Idle` | `Configuring` | `PreviewingPlan` | `Running` | `Completed` | `Failed` | `Cancelled`
- `ExecutionPlanPreviewDialog` — extends `TuiDialog`, implements `ITuiStateCapture<ExecutionPlanPreviewDialogState>`; `IsApproved` indicates proceed vs. cancel
- `ExecutionPlanPreviewDialogState` — captures tier count, entity count, deferred field count, M2M relationship count, tier summaries
- `TuiMigrationProgressReporter` — implements `IProgressReporter`; all `Report()`, `Complete()`, and `Error()` calls dispatched to UI thread

**Key TUI constraints:**

- Only one operation (export or import) may run at a time; UI blocks Start while running
- All migration work runs on a background thread; UI updates are marshaled via `Application.MainLoop.Invoke()`
- File paths are validated before the Start button is enabled (schema `.xml` must exist for export; data `.zip` must exist for import)
- Import options cannot be modified while an operation is running
- Cancellation fires the `CancellationToken`; the operation completes its current batch then stops; partial results are displayed
- Operations are not resumable — cancellation requires a full restart (mitigated by upsert mode)

**Import plan preview flow:** Before starting import, Ctrl+P reads the data file via `ICmtDataReader`, builds the dependency graph via `IDependencyGraphBuilder`, builds the execution plan via `IExecutionPlanBuilder`, and displays it in `ExecutionPlanPreviewDialog`. The user must approve (Proceed) before the import begins.

**TUI acceptance criteria:**

- [ ] Screen loads in Export mode by default
- [ ] Mode switch changes configuration panel without losing shared progress/results UI
- [ ] File paths validated; Start button disabled until paths are valid
- [ ] Ctrl+P opens `ExecutionPlanPreviewDialog` showing tiers, deferred fields, M2M counts
- [ ] Progress area updates phase, entity, record count, rate, and ETA in real time
- [ ] Results area shows per-entity success/failure/warning counts after completion
- [ ] Cancel stops the operation; partial results displayed; state shows `Cancelled`
- [ ] `TuiMigrationProgressReporter` marshals all progress updates to the UI thread
- [ ] `MigrationScreen.CaptureState()` returns accurate `MigrationScreenState`

**TUI code location:** `src/PPDS.Tui/Screens/MigrationScreen.cs` (screen, state, mode, operation state), `src/PPDS.Tui/Dialogs/ExecutionPlanPreviewDialog.cs` (dialog), `src/PPDS.Tui/Progress/TuiMigrationProgressReporter.cs` (progress adapter). Tests use `[Trait("Category", "TuiUnit")]`.

---

## Core Types

### IExporter

Entry point for data extraction ([`Export/IExporter.cs`](../src/PPDS.Migration/Export/IExporter.cs)).

```csharp
public interface IExporter
{
    Task<ExportResult> ExportAsync(string schemaPath, string outputPath,
        ExportOptions? options = null, IProgressReporter? progress = null,
        CancellationToken cancellationToken = default);
}
```

### IImporter

Entry point for data import ([`Import/IImporter.cs`](../src/PPDS.Migration/Import/IImporter.cs)).

```csharp
public interface IImporter
{
    Task<ImportResult> ImportAsync(string dataPath,
        ImportOptions? options = null, IProgressReporter? progress = null,
        CancellationToken cancellationToken = default);

    Task<ImportResult> ImportAsync(MigrationData data, ExecutionPlan plan,
        ImportOptions? options = null, IProgressReporter? progress = null,
        CancellationToken cancellationToken = default);
}
```

### IDependencyGraphBuilder

Analyzes entity relationships for import ordering ([`Analysis/IDependencyGraphBuilder.cs`](../src/PPDS.Migration/Analysis/IDependencyGraphBuilder.cs)).

```csharp
public interface IDependencyGraphBuilder
{
    DependencyGraph Build(MigrationSchema schema);
}
```

The implementation ([`DependencyGraphBuilder.cs`](../src/PPDS.Migration/Analysis/DependencyGraphBuilder.cs)) uses Tarjan's Strongly Connected Components algorithm to detect circular references, then Kahn's algorithm to produce topologically sorted tiers.

### IExecutionPlanBuilder

Creates executable import plan ([`Analysis/IExecutionPlanBuilder.cs`](../src/PPDS.Migration/Analysis/IExecutionPlanBuilder.cs)).

```csharp
public interface IExecutionPlanBuilder
{
    ExecutionPlan Build(DependencyGraph graph, MigrationSchema schema);
}
```

### ExecutionPlan

Ordered import strategy with deferred fields ([`Models/ExecutionPlan.cs`](../src/PPDS.Migration/Models/ExecutionPlan.cs)).

```csharp
public class ExecutionPlan
{
    public IReadOnlyList<ImportTier> Tiers { get; }
    public IReadOnlyDictionary<string, IReadOnlyList<string>> DeferredFields { get; }
    public IReadOnlyList<RelationshipSchema> ManyToManyRelationships { get; }
}
```

### IProgressReporter

Migration-specific progress with metrics ([`Progress/IProgressReporter.cs`](../src/PPDS.Migration/Progress/IProgressReporter.cs)).

```csharp
public interface IProgressReporter
{
    string OperationName { get; set; }
    void Report(ProgressEventArgs args);
    void Complete(MigrationResult result);
    void Error(Exception exception, string? context = null);
    void Reset();
}
```

### IPluginStepManager

Manages plugin step disabling/enabling during import for entities with `disableplugins=true` ([`Import/PluginStepManager.cs:176-200`](../src/PPDS.Migration/Import/PluginStepManager.cs#L176-L200)).

```csharp
public interface IPluginStepManager
{
    Task<IReadOnlyList<Guid>> GetActivePluginStepsAsync(
        IEnumerable<int> objectTypeCodes, CancellationToken cancellationToken = default);
    Task DisablePluginStepsAsync(
        IEnumerable<Guid> stepIds, CancellationToken cancellationToken = default);
    Task EnablePluginStepsAsync(
        IEnumerable<Guid> stepIds, CancellationToken cancellationToken = default);
}
```

Used by `TieredImporter` when `RespectDisablePluginsSetting=true` and schema entities have `disableplugins="true"`. Steps are re-enabled in a `finally` block after import completes.

### Usage Pattern

```csharp
// Export
var exporter = serviceProvider.GetRequiredService<IExporter>();
var exportResult = await exporter.ExportAsync(
    "schema.xml",
    "data.zip",
    new ExportOptions { DegreeOfParallelism = 8 },
    new ConsoleProgressReporter());

// Import
var importer = serviceProvider.GetRequiredService<IImporter>();
var importResult = await importer.ImportAsync(
    "data.zip",
    new ImportOptions
    {
        Mode = ImportMode.Upsert,
        BypassCustomPlugins = CustomLogicBypass.Synchronous,
        MaxParallelEntities = 4
    },
    new ConsoleProgressReporter());

if (!importResult.Success)
{
    foreach (var error in importResult.Errors)
        Console.WriteLine($"{error.EntityLogicalName}: {error.Message}");
}
```

---

## CMT Format

The Configuration Migration Tool format is a ZIP archive containing XML files.

### Archive Structure

```
data.zip
├── [Content_Types].xml      # OpenXML content types manifest
├── data.xml                 # Entity records and M2M relationships
└── data_schema.xml          # Schema: entities, fields, relationships
```

### Schema XML (data_schema.xml)

```xml
<entities version="1.0" timestamp="2026-01-23T10:30:00Z">
  <entity name="account" displayname="Account" etc="1"
          primaryidfield="accountid" primarynamefield="name"
          disableplugins="false">
    <fields>
      <field displayname="Name" name="name" type="string"/>
      <field displayname="Parent" name="parentaccountid" type="lookup"
             lookupType="account"/>
    </fields>
    <relationships>
      <relationship name="systemuserroles" manyToMany="true"
                    relatedEntityName="role" m2mTargetEntity="role"
                    m2mTargetEntityPrimaryKey="roleid"/>
    </relationships>
    <filter><!-- Optional FetchXML filter --></filter>
  </entity>
</entities>
```

### Data XML (data.xml)

```xml
<entities timestamp="2026-01-23T10:30:00Z">
  <entity name="account" displayname="Account">
    <records>
      <record id="00000000-0000-0000-0000-000000000001">
        <field name="accountid" value="00000000-0000-0000-0000-000000000001"/>
        <field name="name" value="Contoso"/>
        <field name="parentaccountid" value="00000000-0000-0000-0000-000000000002"
               lookupentity="account" lookupentityname="Parent Corp"/>
      </record>
    </records>
    <m2mrelationships>
      <m2mrelationship sourceid="..." targetentityname="role"
                       m2mrelationshipname="systemuserroles">
        <targetids>
          <targetid>00000000-0000-0000-0000-000000000003</targetid>
        </targetids>
      </m2mrelationship>
    </m2mrelationships>
  </entity>
</entities>
```

### Format Interfaces

| Interface | Implementation | Purpose |
|-----------|----------------|---------|
| `ICmtSchemaReader` | `CmtSchemaReader` | Parse schema.xml |
| `ICmtSchemaWriter` | `CmtSchemaWriter` | Generate schema.xml |
| `ICmtDataReader` | `CmtDataReader` | Read data.zip archive |
| `ICmtDataWriter` | `CmtDataWriter` | Write data.zip archive |

---

## Error Handling

### Error Types

| Error | Condition | Recovery |
|-------|-----------|----------|
| `SchemaMismatchException` | Column in export missing from target | Use `SkipMissingColumns=true` to continue |
| `ImportException` | Record-level failure | Captured in `ImportResult.Errors` |
| `PoolExhaustedException` | Connection pool depleted | Reduce `MaxParallelEntities` |
| Throttle (service protection) | Rate limit hit | Automatic retry via connection pool |

### Recovery Strategies

- **Schema mismatch**: Set `SkipMissingColumns=true` to warn and continue
- **Record failure**: Set `ContinueOnError=true` to process remaining records
- **User reference missing**: Provide `UserMappings` or enable `UseCurrentUserAsDefault`

### Warning Collection

Non-fatal issues are collected via `IWarningCollector` ([`Progress/IWarningCollector.cs`](../src/PPDS.Migration/Progress/IWarningCollector.cs)):

| Code | Condition |
|------|-----------|
| `BULK_NOT_SUPPORTED` | Entity fell back to individual operations |
| `COLUMN_SKIPPED` | Column in source but not in target |
| `USER_MAPPING_FALLBACK` | User reference fell back to current user |
| `PLUGIN_REENABLE_FAILED` | Failed to re-enable plugin steps |

### Edge Cases

| Scenario | Expected Behavior |
|----------|-------------------|
| Empty data file | Return success with 0 counts |
| All records fail | Return with `Success=false`, errors populated |
| Circular reference group | Deferred fields updated in Phase 2 |
| Duplicate M2M association | Treated as idempotent success (error code 0x80040237) |
| Missing lookup target | Error recorded, `ContinueOnError` controls behavior |

---

## Design Decisions

### Why Tiered Import?

**Context:** Lookup fields reference other records. Importing child before parent causes "record does not exist" errors.

**Decision:** Analyze dependencies and import in topologically sorted tiers. Records with no dependencies import first; records depending on them import in subsequent tiers.

**Algorithm:**
1. Build directed graph from lookup relationships
2. Detect cycles using Tarjan's SCC algorithm
3. Topologically sort using Kahn's algorithm
4. Entities in circular references placed in same tier with deferred fields

**Consequences:**
- Positive: Correct import order without manual specification
- Positive: Circular references handled automatically
- Negative: Analysis overhead (acceptable for migration scale)

### Why Three-Phase Import?

**Context:** Self-referential lookups (e.g., `parentaccountid` on `account`) cannot be set during creation because the target record doesn't exist yet.

**Decision:** Three-phase pipeline:
1. **Phase 1**: Import entities with self-refs nulled
2. **Phase 2**: Update deferred fields using ID mappings
3. **Phase 3**: Create M2M associations

**Consequences:**
- Positive: Handles all circular dependency patterns
- Positive: M2M only after all records exist
- Negative: Additional passes for deferred updates

### Why Bulk Operation Probing?

**Context:** Not all entities support `CreateMultiple`/`UpdateMultiple`. Some (e.g., `team`, `queue`) require individual operations.

**Decision:** Probe with single record first; cache result per entity; fall back to individual operations if unsupported.

**Implementation:** ([`Import/BulkOperationProber.cs`](../src/PPDS.Migration/Import/BulkOperationProber.cs))
1. Execute bulk operation with 1 record
2. If unsupported error, cache and use fallback
3. If success, merge probe result with remaining batch

**Consequences:**
- Positive: Automatic detection without entity list maintenance
- Positive: Probe result reused for all batches of same entity
- Negative: One extra request per entity on first batch

### Why Parallel Export but Sequential Tiers?

**Context:** Export has no ordering requirements; import must respect dependencies.

**Decision:**
- Export: Full entity-level parallelism via `Parallel.ForEachAsync()`
- Import: Tiers execute sequentially; entities within tier execute in parallel

**Consequences:**
- Positive: Export maximizes throughput
- Positive: Import respects dependencies while maximizing within-tier parallelism
- Negative: Tier boundaries are synchronization points

### Why CMT Format?

**Context:** Microsoft's Configuration Migration Tool is widely used. Custom formats create vendor lock-in.

**Decision:** Use CMT format for interoperability. Users can import PPDS exports into Microsoft tools and vice versa.

**Format details:**
- ZIP archive with XML files
- Schema in `data_schema.xml` with entity/field/relationship definitions
- Data in `data.xml` with record values and M2M associations
- `[Content_Types].xml` for OpenXML compliance

**Consequences:**
- Positive: Interoperability with Microsoft tooling
- Positive: Human-readable (XML)
- Negative: XML parsing overhead (acceptable for migration scale)

### Why ID Mapping Collection?

**Context:** Source environment IDs differ from target. Deferred fields and M2M relationships need source→target mapping.

**Decision:** Thread-safe `IdMappingCollection` tracks all mappings during Phase 1, consumed by Phases 2 and 3.

**Implementation:** Uses `ConcurrentDictionary` for lock-free concurrent access during parallel import.

**Consequences:**
- Positive: Safe concurrent access from parallel entity imports
- Positive: Single source of truth for ID resolution
- Negative: Memory grows with record count

---

## Extension Points

### Adding a New Import Phase

1. **Implement** `IImportPhaseProcessor` in `src/PPDS.Migration/Import/`
2. **Return** `PhaseResult` with metadata
3. **Add** to `TieredImporter.ImportAsync()` pipeline

**Example skeleton:**

```csharp
public class MyPhaseProcessor : IImportPhaseProcessor
{
    public string PhaseName => "MyPhase";

    public async Task<PhaseResult> ProcessAsync(
        ImportContext context,
        CancellationToken cancellationToken)
    {
        // Access context.Data, context.Plan, context.IdMappings
        return new PhaseResult { ProcessedCount = count };
    }
}
```

### Adding a New Format Reader/Writer

1. **Create interfaces** in `src/PPDS.Migration/Formats/`
2. **Implement** reader returning `MigrationData`
3. **Implement** writer accepting `MigrationData`
4. **Register** in DI container

---

## Configuration

### ExportOptions

| Setting | Type | Required | Default | Description |
|---------|------|----------|---------|-------------|
| `DegreeOfParallelism` | int | No | CPU × 2 | Entity-level parallelism |
| `PageSize` | int | No | 5000 | Records per FetchXML page |
| `ProgressInterval` | int | No | 100 | Report progress every N records |

### ImportOptions

| Setting | Type | Required | Default | Description |
|---------|------|----------|---------|-------------|
| `Mode` | enum | No | Upsert | Create, Update, or Upsert |
| `UseBulkApis` | bool | No | true | Use CreateMultiple/UpdateMultiple |
| `MaxParallelEntities` | int | No | 4 | Entities per tier parallelism |
| `BypassCustomPlugins` | enum | No | None | Sync/Async/All plugin bypass |
| `BypassPowerAutomateFlows` | bool | No | false | Suppress cloud flows |
| `ContinueOnError` | bool | No | true | Continue on record failure |
| `SkipMissingColumns` | bool | No | false | Warn instead of fail on missing |
| `StripOwnerFields` | bool | No | false | Remove owner-related fields |
| `UserMappings` | collection | No | null | Source→target user mappings |
| `CurrentUserId` | Guid? | No | null | Fallback for unmapped users |
| `RespectDisablePluginsSetting` | bool | No | true | Honor schema `disableplugins` |
| `SuppressDuplicateDetection` | bool | No | false | Suppress duplicate detection rules |
| `ErrorCallback` | Action\<MigrationError\>? | No | null | Real-time error streaming (thread-safe) |
| `OutputManager` | ImportOutputManager? | No | null | Checkpoint logging for structured progress |

### SchemaGeneratorOptions

| Setting | Type | Required | Default | Description |
|---------|------|----------|---------|-------------|
| `IncludeAllFields` | bool | No | true | Include all readable fields |
| `IncludeAuditFields` | bool | No | false | Include createdon, createdby, etc. |
| `CustomFieldsOnly` | bool | No | false | Only custom fields |
| `DisablePluginsByDefault` | bool | No | false | Set `disableplugins=true` |
| `IncludeAttributes` | list | No | null | Whitelist specific attributes |
| `ExcludeAttributes` | list | No | null | Blacklist specific attributes |

---

## Testing

### Acceptance Criteria

- [ ] Export creates valid CMT ZIP archive
- [ ] Import respects tier ordering
- [ ] Circular references handled via deferred fields
- [ ] M2M relationships created after entities
- [ ] Progress reports phase, entity, record counts
- [ ] Schema mismatch detected and reported
- [ ] Bulk fallback works for unsupported entities

### Edge Cases

| Scenario | Input | Expected Output |
|----------|-------|-----------------|
| Self-referential entity | account with parentaccountid | Deferred field updated in Phase 2 |
| Mutual reference | A→B, B→A | Both in same tier, both deferred |
| No dependencies | 3 independent entities | All in Tier 0, parallel import |
| Missing target column | Export has field X, target missing | `SchemaMismatchException` or warning |
| Duplicate M2M | Same association twice | Second treated as success |

### Test Examples

```csharp
[Fact]
public void DependencyGraphBuilder_DetectsCircularReference()
{
    var schema = CreateSchemaWithCircularReference("account", "contact");
    var builder = new DependencyGraphBuilder();

    var graph = builder.Build(schema);

    Assert.True(graph.HasCircularReferences);
    Assert.Single(graph.CircularReferences);
}

[Fact]
public async Task TieredImporter_ProcessesDeferredFields()
{
    var data = CreateDataWithSelfReference("account", "parentaccountid");
    var importer = new TieredImporter(pool, bulkExecutor, ...);

    var result = await importer.ImportAsync(data);

    Assert.True(result.Success);
    // Verify parent lookup is set (updated in Phase 2)
}

[Fact]
public async Task ParallelExporter_ExportsAllEntities()
{
    var schema = CreateSchema("account", "contact");
    var exporter = new ParallelExporter(pool, schemaReader, dataWriter);

    var result = await exporter.ExportAsync(schema, outputPath);

    Assert.True(result.Success);
    Assert.Equal(2, result.EntityResults.Count);
}
```

---

## Related Specs

- [connection-pooling.md](./connection-pooling.md) - Provides pooled clients for parallel operations
- [bulk-operations.md](./bulk-operations.md) - High-throughput record operations
- [architecture.md](./architecture.md) - Progress reporting patterns, error handling
- [authentication.md](./authentication.md) - Credential providers for connection sources

---

## Roadmap

- Incremental export with change tracking
- Resume support for interrupted imports
- Automatic entity detection from environment
- Cross-environment user name-based mapping

---

## Changelog

| Date | Change |
|------|--------|
| 2026-03-18 | Merged TUI surface content from tui-migration.md per SL3 |

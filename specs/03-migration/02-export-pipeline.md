# PPDS.Migration: Export Pipeline

## Overview

The Export Pipeline extracts data from Dataverse environments and packages it into CMT-compatible ZIP files for migration to other environments. It uses parallel execution with connection pooling, supports many-to-many relationships, and provides real-time progress reporting throughout the export process.

## Public API

### Interfaces

| Interface | Purpose |
|-----------|---------|
| `IExporter` | Main entry point for export operations |
| `ICmtSchemaReader` | Parses CMT-compatible schema.xml files |
| `ICmtDataWriter` | Writes export data to ZIP format |
| `ISchemaGenerator` | Generates schemas from Dataverse metadata |

### Classes

| Class | Purpose |
|-------|---------|
| `ParallelExporter` | Primary `IExporter` implementation with parallel entity export |
| `CmtSchemaReader` | XML parser for CMT schema format |
| `CmtDataWriter` | ZIP file writer with CMT data format |
| `DataverseSchemaGenerator` | Generates `MigrationSchema` from entity metadata |

### DTOs/Models

| Type | Purpose |
|------|---------|
| `ExportOptions` | Configuration for parallelism, page size, progress interval |
| `ExportResult` | Outcome with success status, record counts, timing, errors |
| `EntityExportResult` | Per-entity export outcome |
| `MigrationData` | Complete export payload (schema + records + M2M) |
| `MigrationSchema` | Schema definition for export scope |
| `EntitySchema` | Entity definition with fields and relationships |
| `FieldSchema` | Field definition with type and validation metadata |
| `RelationshipSchema` | M2M relationship definition |
| `ManyToManyRelationshipData` | Exported M2M associations |

## Behaviors

### Normal Operation

1. **Schema Parsing**: Read and validate CMT schema.xml defining export scope
2. **Parallel Entity Export**: Query each entity in parallel using pooled connections
3. **Pagination**: Handle large result sets with FetchXML paging cookies
4. **M2M Export**: Query intersect entities for many-to-many relationships
5. **Data Serialization**: Write records and M2M to CMT-compatible XML
6. **ZIP Output**: Package data.xml and data_schema.xml into output ZIP

### Export Stages

```
Schema Parsing → Entity Export (Parallel) → M2M Relationship Export → Data Serialization → ZIP Output
```

**Stage 1: Schema Parsing**
- Reads CMT-compatible schema.xml files via `ICmtSchemaReader`
- Supports both `<entities>` root and `<ImportExportXml><entities>` structure
- Validates required attributes and sets defaults for compatibility

**Stage 2: Parallel Entity Export**
- Uses `Parallel.ForEachAsync` with configurable degree of parallelism
- Default DOP: `Environment.ProcessorCount * 2` (I/O-bound optimization)
- Each parallel task acquires a pooled connection for its duration
- Builds FetchXML from entity schema including all fields
- Handles pagination with paging cookies (default page size: 5000)
- Reports progress at configurable intervals

**Stage 3: M2M Relationship Export**
- Sequential processing per relationship (after entity export completes)
- Queries intersect entities directly
- Filters to associations where source records were actually exported
- Groups by source ID (CMT format requirement)

**Stage 4: ZIP Output**
- Creates CMT-compatible ZIP structure:
  - `[Content_Types].xml` - CMT required metadata
  - `data.xml` - Entity records and M2M associations
  - `data_schema.xml` - Schema definition

### Lifecycle

- **Initialization**: `ParallelExporter` constructed via DI with `IDataverseConnectionPool`, `ICmtSchemaReader`, `ICmtDataWriter`
- **Operation**: `ExportAsync` executes stages sequentially, entity export runs in parallel
- **Cleanup**: Pooled connections returned automatically, no explicit cleanup required

## Edge Cases

| Scenario | Behavior | Notes |
|----------|----------|-------|
| Empty schema (no entities) | Returns empty export with zero records | Valid but unusual |
| Entity with no records | Included in output with empty `<records>` | Maintains schema structure |
| Entity export failure | Recorded in errors, other entities continue | Partial export possible |
| Null field value | Field omitted from record XML | Standard CMT behavior |
| EntityReference field | Emits `value` (GUID) + `lookupentity` attribute | Required for import resolution |
| DateTime field | ISO 8601 with 7 decimal places + Z suffix | Invariant format |
| Polymorphic lookup (customer/owner) | `lookupentity` contains actual referenced entity | Resolved at export time |
| M2M with no exported sources | Relationship section omitted | No orphan associations |
| Reflexive M2M (self-referencing) | Handled with `IsReflexive` flag | Same entity both sides |
| Cancellation requested | Throws `OperationCanceledException` | Token checked in loops |

## Error Handling

| Exception | Condition | Recovery |
|-----------|-----------|----------|
| `InvalidOperationException` | Schema file not found or invalid | Validate schema path before export |
| `FaultException` | Dataverse query failure | Recorded per-entity, export continues |
| `OperationCanceledException` | Cancellation token triggered | Clean cancellation, partial result |
| `IOException` | Cannot write output ZIP | Ensure output path writable |
| Any exception | Unexpected failure | Logged, redacted, returned in `ExportResult.Errors` |

**Error Strategy:**
- Per-entity try-catch blocks allow partial export on failure
- Errors collected in `ConcurrentBag<MigrationError>` for thread safety
- Connection strings redacted via `ConnectionStringRedactor.RedactExceptionMessage()`
- `ExportResult.Success` is `true` only when zero errors

## Dependencies

- **Internal**:
  - `PPDS.Dataverse` - `IDataverseConnectionPool` for Dataverse queries
  - `PPDS.Migration.Models` - Schema and data models
  - `PPDS.Migration.Progress` - `IProgressReporter`, `ProgressEventArgs`, `MigrationError`
- **External**:
  - `Microsoft.PowerPlatform.Dataverse.Client` - `ServiceClient`, FetchXML execution
  - `Microsoft.Xrm.Sdk` - `Entity`, `EntityReference`, attribute types
  - `System.IO.Compression` - ZIP file creation

## Configuration

| Setting | Type | Default | Description |
|---------|------|---------|-------------|
| `DegreeOfParallelism` | int | `ProcessorCount * 2` | Concurrent entity exports |
| `PageSize` | int | 5000 | Records per FetchXML request (Dataverse max) |
| `ProgressInterval` | int | 100 | Report progress every N records |

**CLI Arguments:**
```
ppds data export
  --schema <path>              # Required: Schema file path
  --output <path>              # Required: Output ZIP path
  --profile <name>             # Connection profile
  --environment <url>          # Environment URL
  --parallel <n>               # Degree of parallelism
  --batch-size <n>             # Page size (1-5000)
```

## Thread Safety

- **`ParallelExporter`**: Thread-safe for concurrent calls to `ExportAsync`
- **Error collection**: Uses `ConcurrentBag<MigrationError>` for thread-safe accumulation
- **Entity results**: Uses `ConcurrentBag<EntityExportResultWithData>` during parallel export
- **Connection pool**: Thread-safe (see PPDS.Dataverse Connection Pooling spec)
- **Progress reporting**: Single thread calls reporter per stage (entity export synchronizes internally)

**Concurrency Model:**
- Entities exported in parallel (DOP-limited)
- M2M relationships exported sequentially after entity phase
- ZIP writing is single-threaded after all data collected

## Performance Characteristics

| Metric | Value | Notes |
|--------|-------|-------|
| Default parallelism | `ProcessorCount * 2` | Optimal for I/O-bound operations |
| Page size | 5000 records | Dataverse maximum |
| Typical throughput | 200+ records/sec | With proper DOP and network |
| Memory | O(entity records) | Entire entity held in memory during export |

**Limitations:**
- All records for an entity held in memory (no streaming)
- M2M export is sequential, not parallel
- Large entities may cause memory pressure

## Data Format

### ZIP Structure
```
output.zip
├── [Content_Types].xml          # CMT required
├── data.xml                     # Entity + M2M data
└── data_schema.xml              # Schema definition
```

### Field Value Serialization

| Type | Format | Example |
|------|--------|---------|
| EntityReference | `value` (GUID) + `lookupentity` | `value="abc-123" lookupentity="account"` |
| OptionSetValue | Integer value | `value="100000000"` |
| Money | Decimal, invariant culture | `value="1234.56"` |
| DateTime | ISO 8601 + 7 decimals + Z | `value="2024-01-15T10:30:00.0000000Z"` |
| Boolean | "True"/"False" | `value="True"` |
| Guid/Decimal/Double | Invariant culture | Standard .NET formatting |

## Related

- [ADR-0002: Connection Pooling](../docs/adr/002-connection-pooling.md)
- [ADR-0005: DOP-Based Parallelism](../docs/adr/005-dop-based-parallelism.md)
- [ADR-0015: Application Service Layer](../docs/adr/015-application-service-layer.md)
- [ADR-0016: File Format Policy](../docs/adr/016-file-format-policy.md)
- [Spec: Connection Pooling](../specs/01-dataverse/01-connection-pooling.md)
- [Spec: Dependency Analysis](../specs/03-migration/01-dependency-analysis.md)

## Source Files

| File | Purpose |
|------|---------|
| `src/PPDS.Migration/Export/IExporter.cs` | Main export interface |
| `src/PPDS.Migration/Export/ParallelExporter.cs` | Parallel export implementation |
| `src/PPDS.Migration/Export/ExportOptions.cs` | Export configuration |
| `src/PPDS.Migration/Export/ExportResult.cs` | Export result models |
| `src/PPDS.Migration/Formats/CmtSchemaReader.cs` | Schema XML parser |
| `src/PPDS.Migration/Formats/CmtDataWriter.cs` | ZIP/XML data writer |
| `src/PPDS.Migration/Schema/DataverseSchemaGenerator.cs` | Schema generation from metadata |
| `src/PPDS.Migration/Models/MigrationSchema.cs` | Schema model |
| `src/PPDS.Migration/Models/EntitySchema.cs` | Entity schema model |
| `src/PPDS.Migration/Models/FieldSchema.cs` | Field schema model |
| `src/PPDS.Migration/Models/RelationshipSchema.cs` | Relationship schema model |
| `src/PPDS.Migration/Models/MigrationData.cs` | Complete export data model |
| `src/PPDS.Migration/Models/ManyToManyRelationshipData.cs` | M2M association model |
| `src/PPDS.Migration/Progress/MigrationError.cs` | Error tracking model |
| `src/PPDS.Migration/DependencyInjection/ServiceCollectionExtensions.cs` | DI registration |
| `src/PPDS.Cli/Commands/Data/ExportCommand.cs` | CLI command |
| `tests/PPDS.Migration.Tests/Export/ExportOptionsTests.cs` | Options unit tests |
| `tests/PPDS.Migration.Tests/Export/ExportResultTests.cs` | Result unit tests |

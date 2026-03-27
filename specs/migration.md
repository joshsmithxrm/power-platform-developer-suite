# Migration

**Status:** Draft
**Last Updated:** 2026-03-25
**Surfaces:** CLI, TUI
**Code:** [src/PPDS.Migration/](../src/PPDS.Migration/), TUI: [src/PPDS.Cli/Tui/Screens/MigrationScreen.cs](../src/PPDS.Cli/Tui/Screens/MigrationScreen.cs)

---

## Overview

The migration system provides high-performance data export and import between Dataverse environments. It uses dependency analysis to determine correct import ordering, handles circular references through deferred field processing, and supports the Configuration Migration Tool (CMT) format for interoperability with Microsoft tooling.

The system achieves full parity with Microsoft's Configuration Migration Tool while surpassing it with bulk APIs, automatic dependency analysis, CLI automation, and connection pooling.

### Goals

- **Performance**: Parallel export with entity-level concurrency; parallel import with tier-level and entity-level parallelism; bulk APIs (5x faster than CMT)
- **Correctness**: Dependency-aware import ordering via topological sort; deferred fields for circular references; state machine entity transitions via SDK messages
- **Interoperability**: Full CMT format support including date shifting modes, file column data, and schema attributes
- **Resilience**: Entity-specific handlers for special cases; external lookup resolution; owner preservation; failure cascading

### Non-Goals

- Solution export/import (handled by Dataverse platform)
- Schema migration or table creation (schema must exist in target)
- Incremental/delta export via change tracking (future — #38)
- Checkpoint/resume for interrupted imports (future — #41)
- Schema compare/diff command (future — #42)

---

## Architecture

```
┌─────────────────────────────────────────────────────────────────────────────────┐
│                            Application Layer                                    │
│               (CLI: ppds data export/import, TUI, MCP)                         │
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
│  │ M2M relationship export│ │              │  │ Phase 3: State Transitions      ││
│  │ File column download   │ │              │  │ Phase 4: M2M Relationship Create││
│  └────────────────────────┘ │              │  └─────────────────────────────────┘│
└──────────────┬──────────────┘              └───────────┬───────────┬─────────────┘
               │                                         │           │
               │                                         │           ▼
               ▼                                         │  ┌─────────────────────┐
┌─────────────────────────────┐                          │  │  Handler Framework  │
│     ICmtSchemaReader        │                          │  │  ┌───────────────┐  │
│     ICmtDataWriter          │                          │  │  │ IRecordFilter │  │
│  ┌────────────────────────┐ │                          │  │  │ IRecordTrans. │  │
│  │ schema.xml parsing     │ │                          │  │  │ IStateTransit.│  │
│  │ data.zip generation    │ │                          │  │  │ IPostImport   │  │
│  │ files/ directory I/O   │ │                          │  │  └───────────────┘  │
│  └────────────────────────┘ │                          │  └─────────────────────┘
└─────────────────────────────┘                          │
                                                         ▼
                                          ┌─────────────────────────────────────┐
                                          │     IDependencyGraphBuilder         │
                                          │     IExecutionPlanBuilder           │
                                          │     EntityReferenceMapper           │
                                          │     StateTransitionCollection       │
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
| `ParallelExporter` | Parallel data extraction with FetchXML pagination and file column download |
| `TieredImporter` | Four-phase import orchestration with dependency ordering |
| `DependencyGraphBuilder` | Builds dependency graph using Tarjan's SCC algorithm |
| `ExecutionPlanBuilder` | Converts graph to executable plan with deferred fields |
| `DeferredFieldProcessor` | Updates self-referential lookups after entity creation |
| `StateTransitionProcessor` | Applies state/status transitions via SetStateRequest or SDK messages |
| `RelationshipProcessor` | Creates M2M associations after all entities exist |
| `EntityReferenceMapper` | Centralized lookup resolution: ID mapping, direct ID check, name-based fallback |
| `StateTransitionCollection` | Thread-safe collection of deferred state transition data |
| `SchemaValidator` | Validates export schema against target environment |
| `BulkOperationProber` | Detects per-entity bulk API support |
| `CmtDataReader/Writer` | CMT format serialization including file column data |
| `CmtSchemaReader/Writer` | CMT schema XML handling with date mode and import mode attributes |
| `PluginStepManager` | Disable/enable plugin steps during import |
| `FileColumnTransferHelper` | Chunked upload/download for file column data (4MB blocks) |
| `DateShifter` | Applies date offset based on schema `dateMode` and export timestamp |

#### Entity-Specific Handlers

| Handler | Implements | Entity | Behavior |
|---------|-----------|--------|----------|
| `SystemUserHandler` | `IRecordFilter` | `systemuser` | Skip SYSTEM, INTEGRATION, and support users (accessmode 3/5) |
| `BusinessUnitHandler` | `IRecordTransformer` | `businessunit` | Map root BU to target's root BU |
| `ActivityPointerHandler` | `IRecordFilter` | `activitypointer` | Skip base type; only concrete activity types are imported |
| `OpportunityHandler` | `IRecordTransformer`, `IStateTransitionHandler` | `opportunity` | Strip state/status; emit `WinOpportunityRequest` or `LoseOpportunityRequest` |
| `IncidentHandler` | `IRecordTransformer`, `IStateTransitionHandler` | `incident` | Strip state/status; emit `CloseIncidentRequest` |
| `QuoteHandler` | `IRecordTransformer`, `IStateTransitionHandler` | `quote` | Strip state/status; emit `WinQuoteRequest` or `CloseQuoteRequest` |
| `SalesOrderHandler` | `IRecordTransformer`, `IStateTransitionHandler` | `salesorder` | Strip state/status; emit `FulfillSalesOrderRequest` or `CancelSalesOrderRequest` |
| `LeadHandler` | `IRecordTransformer`, `IStateTransitionHandler` | `lead` | Strip state/status; emit `QualifyLeadRequest` (suppress side-effect creation) or `SetStateRequest` for disqualified |
| `DuplicateRuleHandler` | `IRecordTransformer`, `IPostImportHandler` | `duplicaterule` | Remap Entity Type Codes; publish via `PublishDuplicateRuleRequest` after import |
| `ProductHandler` | `IRecordFilter`, `IStateTransitionHandler` | `product` | Track failed parents; cascade skip to children; handle product lifecycle state |

### Dependencies

- Depends on: [connection-pooling.md](./connection-pooling.md) for client management
- Depends on: [bulk-operations.md](./bulk-operations.md) for high-throughput import
- Uses patterns from: [architecture.md](./architecture.md) for progress reporting

---

## Specification

### Core Requirements

1. **Parallel export**: Entities exported concurrently with FetchXML pagination; M2M relationships exported per entity; file column data optionally downloaded
2. **Dependency analysis**: Build topologically sorted tiers using Tarjan's SCC algorithm for cycle detection
3. **Four-phase import**: Entity create (respecting tier order), deferred field update, state transitions, M2M relationships
4. **State machine handling**: Strip `statecode`/`statuscode` from create payloads; collect transition data; apply via `SetStateRequest` for normal entities or specialized SDK messages for state machine entities
5. **Entity-specific handlers**: Extensible handler interfaces for record filtering, transformation, state transitions, and post-import actions
6. **External lookup resolution**: Cascading resolution — ID mapping first, then direct ID check in target, then name-based query with caching
7. **Date shifting**: Four modes on schema fields — `absolute`, `relative` (weeks), `relativeDaily` (days), `relativeExact` (exact elapsed time)
8. **File column data**: Opt-in export/import of file column binary data with chunked transfer (4MB blocks)
9. **Per-entity import mode**: Schema-level `importMode` attribute overrides global `ImportOptions.Mode`
10. **Owner impersonation**: Clone pooled client per owner group, set `CallerAADObjectId`, execute that owner's records
11. **CMT format compatibility**: Read/write Microsoft Configuration Migration Tool ZIP archives including new schema attributes
12. **Schema validation**: Detect missing columns in target; optionally skip or fail

### Primary Flows

**Export Flow:**

1. **Parse schema**: Read schema.xml via `ICmtSchemaReader.ReadAsync()`
2. **Parallel entity export**: For each entity concurrently via `Parallel.ForEachAsync()`:
   - Acquire pooled client
   - Build FetchXML with fields from schema
   - Execute paginated queries with paging cookies
   - If `IncludeFileData=true` and field type is `file`, download file data via chunked download and store in `files/{entityname}/{recordid}_{fieldname}.bin`
   - Store records in thread-safe collection
3. **Export M2M relationships**: For each entity with M2M relationships:
   - Query intersect entity for associations
   - Filter to exported source records
   - Group by source ID
4. **Write output**: Create data.zip with data.xml, data_schema.xml, files/ directory, [Content_Types].xml
5. **Record export timestamp**: Store `ExportedAt` in data.xml for date shifting calculations

**Import Flow:**

1. **Read data**: Load CMT ZIP via `ICmtDataReader.ReadAsync()` (includes file data if present)
2. **Build dependency graph**: Analyze lookup relationships, detect cycles
3. **Build execution plan**: Topological sort into tiers, identify deferred fields
4. **Resolve entity-specific handlers**: Match registered handlers to entities in the import data
5. **Phase 1 — Entity Import** (sequential tiers, parallel within tier):
   - Validate schema against target environment
   - Optionally disable plugins
   - For each tier (sequential):
     - For each entity (parallel, default 4):
       - Resolve per-entity import mode (schema attribute -> global fallback)
       - Apply `IRecordFilter` handlers (skip SYSTEM users, activitypointer base type, failed product children)
       - Apply `IRecordTransformer` handlers (remap root BU, remap duplicate rule ETCs, strip state/status for state machine entities)
       - Apply generic state/status stripping for all entities; collect `StateTransitionData` (default `SetStateRequest` path or handler-provided SDK message)
       - Apply date shifting based on schema field `dateMode` and elapsed time since export
       - Resolve lookups via `EntityReferenceMapper` cascading strategy (ID mapping -> direct ID -> name-based)
       - If `ImpersonateOwners=true`, group records by owner and clone client with `CallerAADObjectId` per group
       - Execute via bulk API with probe fallback
       - Track ID mappings (source -> target)
6. **Phase 2 — Deferred Fields**:
   - For each entity with deferred fields:
     - Build update batch using Phase 1 ID mappings
     - Execute updates (records still in Active/default state — mutable)
7. **Phase 3 — State Transitions**:
   - Consume `StateTransitionCollection` populated in Phase 1
   - For normal entities: apply `SetStateRequest` where statecode/statuscode differ from defaults
   - For state machine entities: execute specialized SDK messages (`WinOpportunityRequest`, `CloseIncidentRequest`, etc.)
   - Check `IsRecordClosed` before executing close messages to avoid double-closing
   - Apply `IPostImportHandler` actions (publish duplicate rules)
8. **Phase 4 — M2M Relationships**:
   - For each M2M relationship (parallel):
     - Map source/target IDs
     - Execute associate requests
     - Handle duplicates as idempotent success
9. **Re-enable plugins** if disabled
10. **Upload file column data**: For records with file fields, execute chunked upload via `InitializeFileBlocksUploadRequest` / `UploadBlockRequest` / `CommitFileBlocksUploadRequest`

### State Machine Entity Handling

The Dataverse platform blocks `SetStateRequest` for certain entity/state combinations and requires specialized SDK messages instead. These are hard mandatory — the platform throws an error if you attempt `SetStateRequest`.

| Entity | Target State | Required SDK Message |
|--------|-------------|---------------------|
| `incident` | Resolved (1), Canceled (2) | `CloseIncidentRequest` |
| `opportunity` | Won (1) | `WinOpportunityRequest` |
| `opportunity` | Lost (2) | `LoseOpportunityRequest` |
| `quote` | Won (2) | `WinQuoteRequest` |
| `quote` | Closed/Lost (3) | `CloseQuoteRequest` |
| `salesorder` | Fulfilled (3) | `FulfillSalesOrderRequest` |
| `salesorder` | Canceled (2) | `CancelSalesOrderRequest` |
| `lead` | Qualified (1) | `QualifyLeadRequest` (suppress side-effect record creation) |

For all other entities, `SetStateRequest` via `UpdateStateAndStatusForEntity` handles state transitions generically.

**Lead qualification specifics:** `QualifyLeadRequest` supports `CreateAccount`, `CreateContact`, and `CreateOpportunity` boolean parameters. During import, all three are set to `false` — the import data includes those records separately if needed. The qualification only transitions the lead's state.

### External Lookup Resolution

The `EntityReferenceMapper` resolves lookup references using a cascading strategy:

1. **ID Mapping** (fastest, no network): Check `IdMappingCollection` for source->target GUID from records imported in this session
2. **Direct ID check** (single retrieve): Query target environment by source GUID — common for standard reference data (roles, currencies) that share GUIDs across orgs
3. **Name-based resolution** (query by primary name field): Query target by entity-specific match field with results cached in `ConcurrentDictionary`

Entity-specific match fields override the default primary name field:

| Entity | Match Field |
|--------|-------------|
| `transactioncurrency` | `isocurrencycode` |
| `businessunit` | `name` (special handling for root BU) |
| `role` | `name` (scoped to root BU) |
| `uom` / `uomschedule` | `name` |
| All others | Primary name field from entity metadata |

Controlled by `ImportOptions.ResolveExternalLookups` (default: `false`). When `false`, only strategy 1 runs (current behavior). When enabled and a lookup still cannot be resolved, `ImportOptions.SkipUnresolvedLookups` determines whether to null the field (default: `true`) or fail the record.

### Date Shifting

Schema fields can declare a `dateMode` attribute that controls how date values are adjusted during import:

| Mode | Behavior | Use Case |
|------|----------|----------|
| `absolute` | Keep original date value (default) | Production migrations, audit-sensitive data |
| `relative` | Shift by whole weeks elapsed since export | Demo data — "meeting next week" stays next week |
| `relativeDaily` | Shift by whole days elapsed since export | Training data — finer granularity than weekly |
| `relativeExact` | Shift by exact elapsed time since export | Test automation — precise relative offsets |

**Calculation:** `ImportTime - ExportedAt` produces the offset. For `relative`, offset is rounded to nearest week. For `relativeDaily`, rounded to nearest day. For `relativeExact`, applied as-is.

The `ExportedAt` timestamp is already stored in `MigrationData`. The `dateMode` attribute is read from the schema field definition and defaults to `absolute` when absent.

CMT compatibility: `absolute`, `relative`, and `relativeDaily` match CMT's `dateMode` attribute exactly. `relativeExact` is a PPDS extension.

### File Column Data

File columns store binary data (documents, images) in Dataverse. Export and import use the chunked transfer API.

**Export:**
- Controlled by `ExportOptions.IncludeFileData` (default: `false`)
- Files stored in ZIP at `files/{entityname}/{recordid}_{fieldname}.bin`
- Field element in data.xml carries metadata: `value="files/account/abc123_myfilecolumn.bin" filename="report.pdf" mimetype="application/pdf"`
- Download via `InitializeFileBlocksDownloadRequest` -> `DownloadBlockRequest` (4MB chunks) until complete

**Import:**
- Upload via `InitializeFileBlocksUploadRequest` -> `UploadBlockRequest` (4MB chunks) -> `CommitFileBlocksUploadRequest`
- Executed after record creation (file must be associated with an existing record)
- Progress reported per file

**Platform constraints:**
- Chunk size: 4MB (4,194,304 bytes) — Dataverse SDK limit
- Default max file size per column: 32MB (`MaxSizeInKB = 32768`)
- Absolute max file size per column: 10GB (`MaxSizeInKB = 10485760`)
- BYOK (self-managed key) orgs: 128MB max per file, no chunking support

### Per-Entity Import Mode

The schema `<entity>` element supports an optional `importMode` attribute:

```xml
<entity name="account" importMode="upsert" />
<entity name="transactioncurrency" importMode="update" />
<entity name="annotation" importMode="skip" />
```

| Mode | Behavior |
|------|----------|
| `create` | Create only; fail if record exists |
| `update` | Update only; fail if record does not exist |
| `upsert` | Create or update (default) |
| `skip` | Do not import this entity |

When not specified, falls back to `ImportOptions.Mode`. The `skip` mode is a PPDS extension not present in CMT (CMT uses `skipupdate` and `forcecreate` boolean attributes; we normalize to a single enum).

### Owner Impersonation

When `ImportOptions.ImpersonateOwners = true` and `UserMappings` is provided:

1. Group records by mapped owner (target user ID)
2. For each owner group:
   - Clone the pooled `ServiceClient` via `ServiceClient.Clone()` (lightweight copy)
   - Set `CallerAADObjectId` on the clone (preferred over `CallerId` per Microsoft guidance)
   - Execute that owner's records through the clone
   - Dispose the clone
3. Records whose owner cannot be mapped fall back to the import service principal

**Limitation:** Within a single `CreateMultiple`/`UpdateMultiple` batch, all records execute under the same impersonation context. Records are grouped by owner before batching.

**Prerequisites:**
- Application User must have `prvActOnBehalfOfAnotherUser` privilege
- Target users must exist and be enabled
- `UserMappings` must be provided (generated via `ppds data users`)

### Activity Pointer Handling

Activity entities use polymorphic inheritance — `activitypointer` is the base type, with concrete types (`email`, `phonecall`, `appointment`, `task`, `letter`, `fax`) inheriting from it.

**Rules:**
- `activitypointer` records are never imported directly — the `ActivityPointerHandler` filter skips them
- Only concrete activity types are imported; Dataverse auto-creates the `activitypointer` record
- `activityparty` records are imported as part of the parent activity entity data, not as a separate entity pass
- Activity entities are ordered after their `regardingobjectid` targets in dependency tiers

### Duplicate Rule ETC Remapping

Entity Type Codes (ETCs) are auto-assigned integers that differ between environments. Duplicate rules reference entities by ETC in their conditions.

**The `DuplicateRuleHandler`:**
1. On import, queries `EntityMetadata` in target to build `logicalName -> ETC` mapping
2. Rewrites `baseentitytypecode` and `matchingentitytypecode` on `duplicaterule` records
3. Imports rules in unpublished state
4. After all records are imported (Phase 3 `IPostImportHandler`), publishes rules via `PublishDuplicateRuleRequest`

### Product Hierarchy Failure Cascading

Product families form parent-child hierarchies via `parentproductid`. The `ProductHandler`:

1. Tracks failed product record IDs in a concurrent set during Phase 1
2. Before importing a product, checks if `parentproductid` references a failed product
3. If parent failed, skips the child and adds its ID to the failed set (cascading)
4. Skips dependent entities (`productpricelevel`, `productassociation`, `dynamicproperty`, `dynamicpropertyassociation`) whose product reference is in the failed set
5. Reports cascaded skips distinctly from failures — includes root cause (parent ID) in skip reason

### Surface-Specific Behavior

#### CLI Surface

New and modified flags for `ppds data import`:

| Flag | Type | Default | Description |
|------|------|---------|-------------|
| `--resolve-lookups` | bool | false | Enable external lookup resolution |
| `--skip-unresolved-lookups` | bool | true | Null unresolved lookups instead of failing |
| `--impersonate-owners` | bool | false | Preserve ownership via CallerId impersonation |
| `--include-file-data` | bool | false | Export: include file column binary data |

Per-entity import mode and date shifting are schema-level attributes — configured in the schema XML, not via CLI flags.

#### TUI Surface

The `MigrationScreen` provides an interactive Terminal.Gui interface for configuring and monitoring export/import operations. It lives under **Tools > Data Migration** in TuiShell.

**Key components:**

| Component | Responsibility |
|-----------|----------------|
| `MigrationScreen` | Main screen: mode selection, configuration panel, real-time progress, results display |
| `ExecutionPlanPreviewDialog` | Modal showing tier ordering, deferred fields, state transitions, and M2M relationships before import starts |
| `TuiMigrationProgressReporter` | Adapts `IProgressReporter` to TUI by marshaling all callbacks via `Application.MainLoop.Invoke()` |

**Layout:** RadioGroup (Export/Import) at top; configuration panel below (file paths, options per mode including new options); progress area with phase label (now 4 phases), entity progress bars, rate/ETA; results area showing per-entity success/failure/warning/skip counts.

**Hotkeys:**

| Key | Action |
|-----|--------|
| Ctrl+Enter | Start export or import operation |
| Ctrl+P | Open ExecutionPlanPreviewDialog (import mode only) |
| Escape | Cancel running operation (with confirmation) |

**Core types:**

- `MigrationScreen` — implements `ITuiScreen` and `ITuiStateCapture<MigrationScreenState>`; title is "Data Migration - {environment}"
- `MigrationScreenState` — captures mode, operation state, paths, current phase/entity, record counts, rate, ETA, elapsed, error/warning/skip counts
- `MigrationMode` — `Export` | `Import`
- `MigrationOperationState` — `Idle` | `Configuring` | `PreviewingPlan` | `Running` | `Completed` | `Failed` | `Cancelled`
- `ExecutionPlanPreviewDialog` — extends `TuiDialog`, implements `ITuiStateCapture<ExecutionPlanPreviewDialogState>`; `IsApproved` indicates proceed vs. cancel; now shows state transition summary
- `ExecutionPlanPreviewDialogState` — captures tier count, entity count, deferred field count, state transition count, M2M relationship count, tier summaries
- `TuiMigrationProgressReporter` — implements `IProgressReporter`; all `Report()`, `Complete()`, and `Error()` calls dispatched to UI thread

**Key TUI constraints:**

- Only one operation (export or import) may run at a time; UI blocks Start while running
- All migration work runs on a background thread; UI updates are marshaled via `Application.MainLoop.Invoke()`
- File paths are validated before the Start button is enabled
- Import options cannot be modified while an operation is running
- Cancellation fires the `CancellationToken`; the operation completes its current batch then stops; partial results are displayed

### Constraints

- Import requires schema to exist in target environment
- Bulk API support varies by entity (probed at runtime)
- Standard tables: all-or-nothing per batch; elastic tables: partial success
- M2M associations created only after all entities exist and state transitions are applied
- Self-referential lookups must be deferred (circular dependency)
- State transitions must happen after deferred fields (closed records have read-only fields)
- File column chunk size is 4MB — Dataverse platform constraint
- Owner impersonation requires `prvActOnBehalfOfAnotherUser` privilege
- `QualifyLeadRequest` side-effect creation is suppressed during import
- Within a bulk API batch, all records share the same impersonation context

### Validation Rules

| Field | Rule | Error |
|-------|------|-------|
| Schema entities | At least 1 required | `ArgumentException` |
| Output path | Must be valid file path | `ArgumentException` |
| Import data | Must have schema and entity data | `InvalidDataException` |
| Target environment | Must have entities defined | `SchemaMismatchException` |
| `importMode` attribute | Must be `create`, `update`, `upsert`, or `skip` | `InvalidDataException` |
| `dateMode` attribute | Must be `absolute`, `relative`, `relativeDaily`, or `relativeExact` | `InvalidDataException` |
| `ImpersonateOwners` | Requires `UserMappings` to be set | `InvalidOperationException` |

---

## Acceptance Criteria

| ID | Criterion | Test | Status |
|----|-----------|------|--------|
| AC-01 | Export creates valid CMT ZIP archive with schema, data, and content types | `ParallelExporterTests.ExportCreatesValidZipArchive` | 🔲 |
| AC-02 | Import respects tier ordering from dependency analysis | `TieredImporterTests.ImportRespectsTierOrdering` | 🔲 |
| AC-03 | Circular references handled via deferred fields in Phase 2 | `DeferredFieldProcessorTests.UpdatesSelfReferentialLookups` | 🔲 |
| AC-04 | M2M relationships created after all entities and state transitions | `RelationshipProcessorTests.CreatesAssociationsAfterStateTransitions` | 🔲 |
| AC-05 | `statecode`/`statuscode` stripped from create payloads for all entities | `TieredImporterTests.StripsStateStatusFromCreatePayload` | 🔲 |
| AC-06 | `SetStateRequest` applied in Phase 3 for entities with non-default state | `StateTransitionProcessorTests.AppliesSetStateForNonDefaultState` | 🔲 |
| AC-07 | `WinOpportunityRequest` issued for opportunity with statecode=1 | `OpportunityHandlerTests.EmitsWinOpportunityForStateCodeOne` | 🔲 |
| AC-08 | `LoseOpportunityRequest` issued for opportunity with statecode=2 | `OpportunityHandlerTests.EmitsLoseOpportunityForStateCodeTwo` | 🔲 |
| AC-09 | `CloseIncidentRequest` issued for incident with statecode=1 or 2 | `IncidentHandlerTests.EmitsCloseIncidentForNonActiveState` | 🔲 |
| AC-10 | `WinQuoteRequest` issued for quote with statecode=2 | `QuoteHandlerTests.EmitsWinQuoteForStateCodeTwo` | 🔲 |
| AC-11 | `CloseQuoteRequest` issued for quote with statecode=3 | `QuoteHandlerTests.EmitsCloseQuoteForStateCodeThree` | 🔲 |
| AC-12 | `FulfillSalesOrderRequest` issued for salesorder with statecode=3 | `SalesOrderHandlerTests.EmitsFulfillForStateCodeThree` | 🔲 |
| AC-13 | `CancelSalesOrderRequest` issued for salesorder with statecode=2 | `SalesOrderHandlerTests.EmitsCancelForStateCodeTwo` | 🔲 |
| AC-14 | `QualifyLeadRequest` issued for lead with statecode=1 with `CreateAccount/Contact/Opportunity=false` | `LeadHandlerTests.EmitsQualifyWithSuppressedSideEffects` | 🔲 |
| AC-15 | Lead disqualification (statecode=2) uses `SetStateRequest`, not `QualifyLeadRequest` | `LeadHandlerTests.UsesSetStateForDisqualified` | 🔲 |
| AC-16 | `IsRecordClosed` check prevents double-closing of already-closed records | `StateTransitionProcessorTests.SkipsAlreadyClosedRecords` | 🔲 |
| AC-17 | `SystemUserHandler` skips records with accessmode 3 (support) and 5 (integration) | `SystemUserHandlerTests.SkipsSupportAndIntegrationUsers` | 🔲 |
| AC-18 | `BusinessUnitHandler` maps root BU GUID to target's root BU | `BusinessUnitHandlerTests.MapsRootBusinessUnit` | 🔲 |
| AC-19 | `ActivityPointerHandler` skips `activitypointer` base type records | `ActivityPointerHandlerTests.SkipsBaseTypeRecords` | 🔲 |
| AC-20 | `DuplicateRuleHandler` remaps ETCs using target environment metadata | `DuplicateRuleHandlerTests.RemapsEntityTypeCodes` | 🔲 |
| AC-21 | `DuplicateRuleHandler` publishes rules via `PublishDuplicateRuleRequest` in Phase 3 | `DuplicateRuleHandlerTests.PublishesRulesPostImport` | 🔲 |
| AC-22 | `ProductHandler` cascades skip to children when parent product fails | `ProductHandlerTests.CascadesSkipToChildren` | 🔲 |
| AC-23 | `ProductHandler` reports cascaded skips with root cause parent ID | `ProductHandlerTests.ReportsRootCauseInSkipReason` | 🔲 |
| AC-24 | External lookup resolution: ID mapping checked first (no network call) | `EntityReferenceMapperTests.ResolvesFromIdMappingFirst` | 🔲 |
| AC-25 | External lookup resolution: direct ID check when not in mapping | `EntityReferenceMapperTests.FallsBackToDirectIdCheck` | 🔲 |
| AC-26 | External lookup resolution: name-based query as final fallback | `EntityReferenceMapperTests.FallsBackToNameBasedQuery` | 🔲 |
| AC-27 | External lookup resolution: results cached (same target queried at most once) | `EntityReferenceMapperTests.CachesResolvedLookups` | 🔲 |
| AC-28 | External lookup resolution: `transactioncurrency` matched by `isocurrencycode` | `EntityReferenceMapperTests.MatchesCurrencyByIsoCode` | 🔲 |
| AC-29 | Date shifting: `absolute` mode preserves original date value | `DateShifterTests.AbsolutePreservesOriginalDate` | 🔲 |
| AC-30 | Date shifting: `relative` mode shifts by whole weeks since export | `DateShifterTests.RelativeShiftsByWeeks` | 🔲 |
| AC-31 | Date shifting: `relativeDaily` mode shifts by whole days since export | `DateShifterTests.RelativeDailyShiftsByDays` | 🔲 |
| AC-32 | Date shifting: `relativeExact` mode shifts by exact elapsed time | `DateShifterTests.RelativeExactShiftsByExactTime` | 🔲 |
| AC-33 | File column export: files stored in `files/{entity}/{recordid}_{field}.bin` in ZIP | `ParallelExporterTests.ExportsFileColumnsToFilesDirectory` | 🔲 |
| AC-34 | File column export: field element carries `filename` and `mimetype` attributes | `CmtDataWriterTests.WritesFileMetadataAttributes` | 🔲 |
| AC-35 | File column import: chunked upload via 4MB blocks | `FileColumnTransferHelperTests.UploadsInFourMegabyteChunks` | 🔲 |
| AC-36 | File column export disabled by default (`IncludeFileData=false`) | `ParallelExporterTests.SkipsFileColumnsWhenNotOptedIn` | 🔲 |
| AC-37 | Per-entity `importMode` attribute overrides global mode | `TieredImporterTests.PerEntityModeOverridesGlobal` | 🔲 |
| AC-38 | Per-entity `importMode="skip"` excludes entity from import | `TieredImporterTests.SkipModeExcludesEntity` | 🔲 |
| AC-39 | Owner impersonation: records grouped by owner, clone per group with `CallerAADObjectId` | `TieredImporterTests.ImpersonatesViaClonePerOwnerGroup` | 🔲 |
| AC-40 | Owner impersonation: unmapped owners fall back to service principal | `TieredImporterTests.FallsBackToServicePrincipalForUnmappedOwner` | 🔲 |
| AC-41 | Progress reports phase (1-4), entity, record counts, rate, and ETA | `ProgressReporterTests.ReportsAllFourPhases` | 🔲 |
| AC-42 | Schema validation detects missing columns and reports | `SchemaValidatorTests.DetectsMissingColumns` | 🔲 |
| AC-43 | Bulk fallback works for unsupported entities | `BulkOperationProberTests.FallsBackForUnsupportedEntities` | 🔲 |

### Edge Cases

| Scenario | Input | Expected Output |
|----------|-------|-----------------|
| Self-referential entity | account with parentaccountid | Deferred field updated in Phase 2 |
| Mutual reference | A->B, B->A | Both in same tier, both deferred |
| No dependencies | 3 independent entities | All in Tier 0, parallel import |
| Missing target column | Export has field X, target missing | `SchemaMismatchException` or warning |
| Duplicate M2M | Same association twice | Second treated as success |
| Record already closed | Opportunity already Won in target | `IsRecordClosed` returns true, skip transition |
| State machine entity with state=0 | Active opportunity | No state transition needed, no SDK message |
| Lead disqualified (state=2) | Lead with statecode=2 | `SetStateRequest`, not `QualifyLeadRequest` |
| Product parent fails, 5 children | Parent create fails | All 5 children skipped with root cause |
| External lookup to nonexistent record | Currency "XYZ" not in target | Field nulled (if `SkipUnresolvedLookups=true`) or record fails |
| File column on BYOK org | 128MB limit, no chunking | Single block upload; fail if file > 128MB |
| No dateMode attribute | Field without `dateMode` | Treated as `absolute` (default) |
| importMode="skip" on all entities | All entities skipped | Return success with 0 counts |

---

## Core Types

### Handler Interfaces

Each concern is a separate interface. A handler class implements only the interfaces it needs.

```csharp
public interface IRecordFilter
{
    bool CanHandle(string entityLogicalName);
    bool ShouldSkip(Entity record, ImportContext context);
}

public interface IRecordTransformer
{
    bool CanHandle(string entityLogicalName);
    Entity Transform(Entity record, ImportContext context);
}

public interface IStateTransitionHandler
{
    bool CanHandle(string entityLogicalName);
    StateTransitionData? GetTransition(Entity record, ImportContext context);
}

public interface IPostImportHandler
{
    bool CanHandle(string entityLogicalName);
    Task ExecuteAsync(string entityLogicalName, ImportContext context, CancellationToken cancellationToken);
}
```

### StateTransitionCollection

Thread-safe collection populated in Phase 1, consumed in Phase 3.

```csharp
public class StateTransitionCollection
{
    public void Add(string entityName, Guid recordId, StateTransitionData data);
    public IReadOnlyList<StateTransitionData> GetTransitions(string entityName);
    public IEnumerable<string> GetEntityNames();
    public int Count { get; }
}

public class StateTransitionData
{
    public string EntityName { get; init; }
    public Guid RecordId { get; init; }
    public int StateCode { get; init; }
    public int StatusCode { get; init; }
    public string? SdkMessage { get; init; }        // null = SetStateRequest
    public Dictionary<string, object>? MessageData { get; init; }
}
```

### EntityReferenceMapper

Centralized lookup resolution with cascading strategy and caching.

```csharp
public class EntityReferenceMapper
{
    public EntityReferenceMapper(
        IdMappingCollection idMappings,
        IDataverseConnectionPool pool,
        ImportOptions options);

    public async Task<Guid?> ResolveAsync(
        string targetEntityName, Guid sourceId,
        CancellationToken cancellationToken);
}
```

### DateShifter

Applies date offset based on schema field mode and elapsed time since export.

```csharp
public static class DateShifter
{
    public static DateTime? Shift(DateTime? value, DateMode mode, TimeSpan elapsed);
}

public enum DateMode { Absolute, Relative, RelativeDaily, RelativeExact }
```

### FileColumnTransferHelper

Chunked upload/download for file column binary data.

```csharp
public class FileColumnTransferHelper
{
    public async Task<byte[]> DownloadAsync(
        string entityName, Guid recordId, string fieldName,
        CancellationToken cancellationToken);

    public async Task UploadAsync(
        string entityName, Guid recordId, string fieldName,
        byte[] data, string fileName, string mimeType,
        CancellationToken cancellationToken);
}
```

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

### IExecutionPlanBuilder

Creates executable import plan ([`Analysis/IExecutionPlanBuilder.cs`](../src/PPDS.Migration/Analysis/IExecutionPlanBuilder.cs)).

```csharp
public interface IExecutionPlanBuilder
{
    ExecutionPlan Build(DependencyGraph graph, MigrationSchema schema);
}
```

### ExecutionPlan

Ordered import strategy with deferred fields and state transition awareness.

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

`MigrationPhase` enum extended: `Analyzing`, `Exporting`, `Importing`, `ProcessingDeferredFields`, `ApplyingStateTransitions`, `ProcessingRelationships`, `UploadingFiles`, `Complete`, `Error`

### Usage Pattern

```csharp
// Import with CMT parity features
var importer = serviceProvider.GetRequiredService<IImporter>();
var importResult = await importer.ImportAsync(
    "data.zip",
    new ImportOptions
    {
        Mode = ImportMode.Upsert,
        ResolveExternalLookups = true,
        ImpersonateOwners = true,
        UserMappings = userMappings,
        BypassCustomPlugins = CustomLogicBypass.Synchronous,
    },
    new ConsoleProgressReporter());

// Export with file data
var exporter = serviceProvider.GetRequiredService<IExporter>();
var exportResult = await exporter.ExportAsync(
    "schema.xml",
    "data.zip",
    new ExportOptions { IncludeFileData = true },
    new ConsoleProgressReporter());
```

---

## CMT Format

The Configuration Migration Tool format is a ZIP archive containing XML files.

### Archive Structure

```
data.zip
+-- [Content_Types].xml      # OpenXML content types manifest
+-- data.xml                 # Entity records and M2M relationships
+-- data_schema.xml          # Schema: entities, fields, relationships
+-- files/                   # File column binary data (optional)
    +-- {entityname}/
        +-- {recordid}_{fieldname}.bin
```

### Schema XML (data_schema.xml)

```xml
<entities version="1.0" timestamp="2026-01-23T10:30:00Z">
  <entity name="account" displayname="Account" etc="1"
          primaryidfield="accountid" primarynamefield="name"
          disableplugins="false" importMode="upsert">
    <fields>
      <field displayname="Name" name="name" type="string"/>
      <field displayname="Scheduled Start" name="scheduledstart" type="datetime"
             dateMode="relative"/>
      <field displayname="Parent" name="parentaccountid" type="lookup"
             lookupType="account"/>
      <field displayname="Logo" name="entityimage" type="filedata"/>
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
        <field name="statecode" value="0"/>
        <field name="statuscode" value="1"/>
        <field name="parentaccountid" value="00000000-0000-0000-0000-000000000002"
               lookupentity="account" lookupentityname="Parent Corp"/>
        <field name="entityimage" value="files/account/00000001_entityimage.bin"
               filename="logo.png" mimetype="image/png"/>
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
| `ICmtSchemaReader` | `CmtSchemaReader` | Parse schema.xml (including `dateMode`, `importMode`, `filedata` type) |
| `ICmtSchemaWriter` | `CmtSchemaWriter` | Generate schema.xml |
| `ICmtDataReader` | `CmtDataReader` | Read data.zip archive (including `files/` directory) |
| `ICmtDataWriter` | `CmtDataWriter` | Write data.zip archive (including `files/` directory) |

---

## Error Handling

### Error Types

| Error | Condition | Recovery |
|-------|-----------|----------|
| `SchemaMismatchException` | Column in export missing from target | Use `SkipMissingColumns=true` to continue |
| `ImportException` | Record-level failure | Captured in `ImportResult.Errors` |
| `StateTransitionException` | Close/qualify message fails | Captured in errors; `ContinueOnError` controls behavior |
| `PoolExhaustedException` | Connection pool depleted | Reduce `MaxParallelEntities` |
| `FileUploadException` | File column upload fails | Captured in errors; record created, file attachment failed |
| Throttle (service protection) | Rate limit hit | Automatic retry via connection pool |

### Recovery Strategies

- **Schema mismatch**: Set `SkipMissingColumns=true` to warn and continue
- **Record failure**: Set `ContinueOnError=true` to process remaining records
- **User reference missing**: Provide `UserMappings` or enable `UseCurrentUserAsDefault`
- **State transition failure**: Record exists in default state; transition logged as error; can be retried manually
- **External lookup unresolved**: `SkipUnresolvedLookups=true` nulls the field; `false` fails the record
- **Product parent failure**: Children automatically skipped; re-import after fixing parent

### Warning Collection

Non-fatal issues are collected via `IWarningCollector` ([`Progress/IWarningCollector.cs`](../src/PPDS.Migration/Progress/IWarningCollector.cs)):

| Code | Condition |
|------|-----------|
| `BULK_NOT_SUPPORTED` | Entity fell back to individual operations |
| `COLUMN_SKIPPED` | Column in source but not in target |
| `USER_MAPPING_FALLBACK` | User reference fell back to current user |
| `PLUGIN_REENABLE_FAILED` | Failed to re-enable plugin steps |
| `LOOKUP_RESOLVED_BY_NAME` | External lookup resolved by name (potential duplicate risk) |
| `RECORD_SKIPPED_CASCADE` | Record skipped due to parent failure |
| `STATE_TRANSITION_SKIPPED` | Record already in target state |
| `FILE_UPLOAD_FAILED` | File attachment failed but record was created |
| `BYOK_FILE_LIMIT` | File exceeds 128MB BYOK limit |

### Edge Cases

| Scenario | Expected Behavior |
|----------|-------------------|
| Empty data file | Return success with 0 counts |
| All records fail | Return with `Success=false`, errors populated |
| Circular reference group | Deferred fields updated in Phase 2 |
| Duplicate M2M association | Treated as idempotent success (error code 0x80040237) |
| Missing lookup target | Error recorded, `ContinueOnError` controls behavior |
| Double-close attempt | `IsRecordClosed` prevents; warning emitted |
| File column with no data in archive | Field skipped with warning |

---

## Design Decisions

### Why Four-Phase Import?

**Context:** The original three-phase pipeline (entity import, deferred fields, M2M) does not account for state transitions. State machine entities require specialized SDK messages that may reference records from other entities. Deferred field updates must happen while records are still in their Active/default state (closed records have read-only fields).

**Decision:** Four-phase pipeline: Entity Import -> Deferred Fields -> State Transitions -> M2M Relationships.

**Alternatives considered:**
- State transitions per-entity at end of Phase 1: Fails when close messages reference records from later tiers
- State transitions before deferred fields: Fails because closed records have read-only fields that deferred updates need to modify

**Consequences:**
- Positive: All records exist before any cross-entity state transitions
- Positive: All lookups resolved while records are still mutable
- Positive: State transitions happen on fully-formed records
- Negative: One additional pass over imported data

### Why Separate Handler Interfaces?

**Context:** Entity-specific import behavior spans multiple concerns: filtering, transformation, state transitions, post-import actions. A single god-interface forces handlers to stub out methods they don't need.

**Decision:** Four focused interfaces: `IRecordFilter`, `IRecordTransformer`, `IStateTransitionHandler`, `IPostImportHandler`. A handler class implements only what it needs.

**Alternatives considered:**
- Single `IEntityImportHandler` with all methods: Forces stub implementations; harder to test
- Middleware pipeline pattern: Over-engineered for a fixed set of concerns with known ordering

**Consequences:**
- Positive: `SystemUserHandler` is 10 lines (only `IRecordFilter`), not 50 with stubs
- Positive: Each interface testable in isolation
- Positive: New concern types addable without modifying existing interfaces
- Negative: Handler registration requires scanning multiple interface types

### Why Generic State Handling in Orchestrator?

**Context:** ~95% of entities use the same `SetStateRequest` path. Only ~5 entities need specialized SDK messages.

**Decision:** The `TieredImporter` orchestrator owns generic state/status stripping and `SetStateRequest` logic. Entity-specific handlers only registered for the exceptions (opportunity, incident, quote, salesorder, lead).

**Alternatives considered:**
- Default handler registered for all entities: Phantom registrations for hundreds of entities that don't need them
- Every entity must have a handler: Busywork with no benefit

**Consequences:**
- Positive: Handlers stay lean and exceptional
- Positive: No maintenance of handler registrations for standard entities
- Negative: Two code paths (orchestrator generic + handler override) — must be clearly documented

### Why Cascading Lookup Resolution?

**Context:** Cross-environment imports fail when lookup targets exist in the target but with different GUIDs.

**Decision:** Three-strategy cascade in `EntityReferenceMapper`: ID mapping (no network) -> direct ID check -> name-based query. Results cached per target record.

**Alternatives considered:**
- Pluggable strategy pattern: Only three strategies in a fixed order; over-engineering
- Batch resolution in a separate phase: Adds pipeline complexity for marginal performance gain (caching eliminates repeated queries)

**Consequences:**
- Positive: Single class, predictable behavior, self-contained
- Positive: Cache eliminates repeated queries (e.g., 10,000 records referencing "USD" = 1 query)
- Negative: First encounter of each external lookup adds latency (one query)

### Why Clone-Per-Owner for Impersonation?

**Context:** `CallerId`/`CallerAADObjectId` is a client-level property in the Dataverse SDK. There is no per-request impersonation parameter on `OrganizationRequest`.

**Decision:** Group records by mapped owner, clone `ServiceClient` per group, set `CallerAADObjectId` on clone.

**Alternatives considered:**
- Set/restore `CallerId` on pooled client: Mutates shared state; exception could leak impersonation
- Per-request parameter: Does not exist in the SDK (only Web API has per-request headers)

**Consequences:**
- Positive: No shared state mutation; clone is isolated
- Positive: `CallerAADObjectId` is Microsoft's preferred approach (Entra ID-based)
- Negative: Smaller batches per owner group reduces bulk API efficiency
- Negative: `ServiceClient.Clone()` has lightweight but nonzero cost

### Why CMT Format?

**Context:** Microsoft's Configuration Migration Tool is widely used. Custom formats create vendor lock-in.

**Decision:** Use CMT format for interoperability. Extended with `importMode`, `relativeExact` dateMode, and `files/` directory while maintaining backward compatibility with CMT archives.

**Consequences:**
- Positive: Interoperability with Microsoft tooling
- Positive: Human-readable (XML)
- Positive: PPDS extensions are additive — CMT ignores unknown attributes
- Negative: XML parsing overhead (acceptable for migration scale)

---

## Extension Points

### Adding a New Entity Handler

1. **Create class** in `src/PPDS.Migration/Import/Handlers/`
2. **Implement** one or more handler interfaces (`IRecordFilter`, `IRecordTransformer`, `IStateTransitionHandler`, `IPostImportHandler`)
3. **Implement** `CanHandle(string entityLogicalName)` to return `true` for target entity
4. **Register** in DI container — dispatcher auto-discovers via interface scanning

**Example skeleton:**

```csharp
public class ContractHandler : IRecordTransformer, IStateTransitionHandler
{
    public bool CanHandle(string entityLogicalName)
        => entityLogicalName == EntityNames.Contract;

    public Entity Transform(Entity record, ImportContext context)
    {
        // Strip state/status, remap fields
        return record;
    }

    public StateTransitionData? GetTransition(Entity record, ImportContext context)
    {
        // Return CancelContractRequest data if statecode indicates cancelled
        return null;
    }
}
```

### Adding a New Import Phase

1. **Implement** `IImportPhaseProcessor` in `src/PPDS.Migration/Import/`
2. **Return** `PhaseResult` with metadata
3. **Add** to `TieredImporter.ImportAsync()` pipeline

### Adding a New Format Reader/Writer

1. **Create interfaces** in `src/PPDS.Migration/Formats/`
2. **Implement** reader returning `MigrationData`
3. **Implement** writer accepting `MigrationData`
4. **Register** in DI container

### Adding a New Lookup Resolution Match Field

Add entry to the entity-specific match field dictionary in `EntityReferenceMapper`:

```csharp
private static readonly Dictionary<string, string> MatchFields = new()
{
    ["transactioncurrency"] = "isocurrencycode",
    ["businessunit"] = "name",
    ["role"] = "name",
    // Add new entries here
};
```

---

## Configuration

### ExportOptions

| Setting | Type | Required | Default | Description |
|---------|------|----------|---------|-------------|
| `DegreeOfParallelism` | int | No | CPU x 2 | Entity-level parallelism |
| `PageSize` | int | No | 5000 | Records per FetchXML page |
| `ProgressInterval` | int | No | 100 | Report progress every N records |
| `IncludeFileData` | bool | No | false | Download and include file column binary data |

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
| `UserMappings` | collection | No | null | Source->target user mappings |
| `CurrentUserId` | Guid? | No | null | Fallback for unmapped users |
| `RespectDisablePluginsSetting` | bool | No | true | Honor schema `disableplugins` |
| `SuppressDuplicateDetection` | bool | No | false | Suppress duplicate detection rules |
| `ResolveExternalLookups` | bool | No | false | Enable cascading external lookup resolution |
| `SkipUnresolvedLookups` | bool | No | true | Null unresolved lookups instead of failing record |
| `ImpersonateOwners` | bool | No | false | Clone client per owner with CallerAADObjectId |
| `ErrorCallback` | Action | No | null | Real-time error streaming (thread-safe) |
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

## Related Specs

- [connection-pooling.md](./connection-pooling.md) - Provides pooled clients for parallel operations and `CallerAADObjectId` support
- [bulk-operations.md](./bulk-operations.md) - High-throughput record operations
- [architecture.md](./architecture.md) - Progress reporting patterns, error handling
- [authentication.md](./authentication.md) - Credential providers for connection sources

---

## Roadmap

- Incremental export with change tracking (#38)
- Checkpoint and resume for interrupted imports (#41)
- Schema compare/diff command (#42)

---

## Changelog

| Date | Change |
|------|--------|
| 2026-03-25 | CMT parity: 4-phase pipeline, entity handlers, state transitions, external lookups, date shifting, file columns, per-entity import mode, owner impersonation, activity pointer handling, duplicate rule ETC remapping, product hierarchy cascading |
| 2026-03-18 | Merged TUI surface content from tui-migration.md per SL3 |

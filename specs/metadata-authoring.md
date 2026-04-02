# Metadata Authoring

**Status:** Implemented
**Last Updated:** 2026-04-01
**Code:** [src/PPDS.Dataverse/Metadata/](../src/PPDS.Dataverse/Metadata/) | [src/PPDS.Cli/Commands/Metadata/](../src/PPDS.Cli/Commands/Metadata/) | [src/PPDS.Mcp/Tools/](../src/PPDS.Mcp/Tools/) | [src/PPDS.Cli/Tui/Screens/](../src/PPDS.Cli/Tui/Screens/) | [src/PPDS.Extension/src/panels/](../src/PPDS.Extension/src/panels/)
**Surfaces:** CLI, TUI, Extension, MCP

---

## Overview

Create, modify, and delete Dataverse schema objects — tables, columns, relationships, choices, and alternate keys — from any PPDS surface. Closes the read-only gap in the existing metadata browser by adding full authoring support through the same Application Service architecture.

### Goals

- **Full schema CRUD:** Create, update, and delete tables, columns, relationships, choices, and alternate keys
- **SDK superset:** Expose all SDK-settable properties, including capabilities the Maker UI cannot access (BigInt columns, individual cascade control, managed properties, AutoNumber format strings)
- **Solution-aware:** Every authoring operation targets an explicit solution — no accidental Default Solution pollution
- **Safe by default:** Dry-run validation, interactive delete confirmation (mirroring truncate safety pattern), and explicit publish step
- **Multi-surface consistency:** Same `IMetadataAuthoringService` powers CLI, TUI, Extension, and MCP (Constitution A1, A2)

### Non-Goals

- Forms, views, charts, dashboards (XML-based visual design — separate domain)
- Formula, calculated, and rollup column definitions (complex DSLs best handled in Maker UI)
- Business rules and prompt columns
- Duplicate detection rules
- Solution management (covered by `specs/solutions.md`)
- Data operations (covered by `specs/data-explorer.md`, `specs/query.md`)

---

## Architecture

```
┌──────────────────────────────────────────────────────────────────┐
│                      UI Surfaces (thin)                          │
│  ┌──────────┐  ┌─────────┐  ┌──────────┐  ┌─────────────────┐  │
│  │  VS Code │  │   TUI   │  │   MCP    │  │      CLI        │  │
│  │  Webview │  │ Screen  │  │  Tools   │  │    Commands     │  │
│  └────┬─────┘  └────┬────┘  └────┬─────┘  └───────┬─────────┘  │
│  JSON-RPC        Direct       Direct            Direct           │
│  ┌────▼──────────────▼────────────▼────────────────▼──────────┐  │
│  │     IMetadataAuthoringService  │  IMetadataQueryService     │  │
│  │  ┌──────────────────────────┐  │  (renamed from             │  │
│  │  │ Validation │ Solution   │  │   IMetadataService)         │  │
│  │  │ Context │ Dry-Run       │  │                              │  │
│  │  └──────────────────────────┘  │                              │  │
│  │              │    invalidates  │                              │  │
│  │              ▼─────────────────▼                              │  │
│  │  ┌──────────────────────────────────────────────────────┐   │  │
│  │  │  CachedMetadataProvider (shared — reads cache,       │   │  │
│  │  │  writes invalidate)                                   │   │  │
│  │  └──────────────────────────────┬───────────────────────┘   │  │
│  └─────────────────────────────────┼───────────────────────────┘  │
│                                    │                              │
│  ┌─────────────────────────────────▼───────────────────────────┐ │
│  │  Dataverse SDK + IDataverseConnectionPool                    │ │
│  └──────────────────────────────────────────────────────────────┘ │
└──────────────────────────────────────────────────────────────────┘
```

### Components

| Component | Responsibility |
|-----------|----------------|
| `IMetadataAuthoringService` | Domain service — schema CRUD operations with validation and solution context |
| `DataverseMetadataAuthoringService` | SDK implementation — maps DTOs to SDK requests, handles responses |
| `IMetadataQueryService` | Renamed from `IMetadataService` — read-only schema queries (unchanged behavior) |
| `CachedMetadataProvider` | Cache layer — invalidated by authoring operations |
| CLI commands | Noun-verb subcommands under `ppds metadata` |
| MCP tools | Create/update tools per schema object (no delete — MCP is non-destructive) |
| Extension panel | Authoring actions integrated into existing Metadata Browser |
| TUI screen | Authoring actions integrated into existing Metadata Explorer |

### Dependencies

- Depends on: [metadata-browser.md](./metadata-browser.md) for read-only query service and cache infrastructure
- Depends on: [connection-pooling.md](./connection-pooling.md) for pooled Dataverse clients
- Depends on: [publish.md](./publish.md) for `ppds publish --type entity` integration
- Uses patterns from: [CONSTITUTION.md](./CONSTITUTION.md) — A1, A2, A3, D1, D2, D4, I1

---

## Specification

### Service Rename: IMetadataService → IMetadataQueryService

Phase 0 prerequisite. Mechanical rename across the entire codebase:

- `IMetadataService` → `IMetadataQueryService`
- `DataverseMetadataQueryService` → `DataverseMetadataQueryService`
- All DI registrations, RPC handlers, MCP tools, CLI commands, TUI screens, Extension panel references
- Update `specs/metadata-browser.md` and `specs/publish.md` to reflect the rename and `--type entity` support
- No behavioral changes — purely a rename

### IMetadataAuthoringService

All methods accept `CancellationToken` (Constitution R2). Methods that make SDK calls (create, update, delete) accept `IProgressReporter?` for operation feedback (Constitution A3). All methods require `solutionUniqueName` via the request DTO. All methods wrap exceptions in `PpdsException` with `ErrorCode` (Constitution D4).

#### Table Operations

```csharp
Task<CreateTableResult> CreateTableAsync(CreateTableRequest request, IProgressReporter? reporter = null, CancellationToken ct = default);
Task UpdateTableAsync(UpdateTableRequest request, IProgressReporter? reporter = null, CancellationToken ct = default);
Task DeleteTableAsync(DeleteTableRequest request, IProgressReporter? reporter = null, CancellationToken ct = default);
```

**CreateTableRequest properties:**
- `SolutionUniqueName` (required)
- `SchemaName` (required) — prefix auto-validated against solution publisher
- `DisplayName`, `PluralDisplayName`, `Description` (required)
- `OwnershipType` (required) — UserOwned or OrganizationOwned
- `PrimaryAttributeSchemaName`, `PrimaryAttributeDisplayName`, `PrimaryAttributeMaxLength`
- Optional flags: `HasActivities`, `HasNotes`, `IsActivity`, `ChangeTrackingEnabled`, `IsAuditEnabled`, `IsQuickCreateEnabled`, `IsDuplicateDetectionEnabled`, `IsValidForQueue`, `EntityColor`
- `DryRun` (bool) — validate without executing

**CreateTableResult:** `LogicalName`, `MetadataId`, `WasDryRun`, `ValidationMessages[]`

#### Column Operations

```csharp
Task<CreateColumnResult> CreateColumnAsync(CreateColumnRequest request, IProgressReporter? reporter = null, CancellationToken ct = default);
Task UpdateColumnAsync(UpdateColumnRequest request, IProgressReporter? reporter = null, CancellationToken ct = default);
Task DeleteColumnAsync(DeleteColumnRequest request, IProgressReporter? reporter = null, CancellationToken ct = default);
```

**Supported column types:** String, Memo, Integer, BigInt, Decimal, Double, Money, Boolean, DateTime, Choice (Picklist), Choices (MultiSelectPicklist), Image, File

**CreateColumnRequest properties:**
- `SolutionUniqueName`, `EntityLogicalName` (required)
- `SchemaName`, `DisplayName`, `Description` (required)
- `ColumnType` (required) — enum of supported types
- `RequiredLevel` — None, Recommended, Required
- Type-specific properties:
  - String/Memo: `MaxLength`, `Format` (Text, Email, Url, Phone, TextArea, TickerSymbol, Json)
  - Integer: `MinValue`, `MaxValue`, `Format` (None, Duration, Language, Timezone)
  - Decimal/Double: `MinValue`, `MaxValue`, `Precision`
  - Money: `MinValue`, `MaxValue`, `Precision`, `ImeMode`
  - DateTime: `DateTimeBehavior` (UserLocal, DateOnly, TimeZoneIndependent), `Format` (DateAndTime, DateOnly)
  - Choice: `OptionSetName` (existing global) or `Options[]` (new local), `DefaultValue`
  - Choices: same as Choice (multi-select)
  - Boolean: `TrueLabel`, `FalseLabel`, `DefaultValue`
  - Image: `MaxSizeInKB`, `CanStoreFullImage`
  - File: `MaxSizeInKB`
- `AutoNumberFormat` (string) — AutoNumber columns are String columns with this property set (not a separate type)
- `IsAuditEnabled`, `IsSecured`, `IsValidForAdvancedFind`
- `DryRun` (bool)

**Lookup columns** are created implicitly via relationship creation (see Relationships below) — this matches the SDK design where `CreateOneToManyRequest` includes the lookup attribute definition.

#### Relationship Operations

```csharp
Task<CreateRelationshipResult> CreateOneToManyAsync(CreateOneToManyRequest request, IProgressReporter? reporter = null, CancellationToken ct = default);
Task<CreateRelationshipResult> CreateManyToManyAsync(CreateManyToManyRequest request, IProgressReporter? reporter = null, CancellationToken ct = default);
Task UpdateRelationshipAsync(UpdateRelationshipRequest request, IProgressReporter? reporter = null, CancellationToken ct = default);
Task DeleteRelationshipAsync(DeleteRelationshipRequest request, IProgressReporter? reporter = null, CancellationToken ct = default);
```

**CreateOneToManyRequest properties:**
- `SolutionUniqueName` (required)
- `ReferencedEntity` (required) — the "one" side (parent)
- `ReferencingEntity` (required) — the "many" side (child)
- `SchemaName` (required)
- Lookup column definition: `LookupSchemaName`, `LookupDisplayName`
- `CascadeConfiguration` — individual control over each action:
  - `Assign`, `Delete`, `Merge`, `Reparent`, `Share`, `Unshare`
  - Each accepts: `Cascade`, `Active`, `NoCascade`, `UserOwned`, `RemoveLink`, `Restrict`
- `IsHierarchical` (bool)
- `MenuBehavior` — associated menu configuration
- `DryRun` (bool)

**CreateManyToManyRequest properties:**
- `SolutionUniqueName` (required)
- `Entity1LogicalName`, `Entity2LogicalName` (required)
- `SchemaName` (required)
- `IntersectEntitySchemaName`
- `Entity1NavigationPropertyName`, `Entity2NavigationPropertyName`
- `DryRun` (bool)

**UpdateRelationshipRequest properties:**
- `SolutionUniqueName` (required)
- `SchemaName` (required) — identifies the relationship to update
- `CascadeConfiguration` — updated cascade behavior (1:N only)
- `IsHierarchical` (bool) — update hierarchical flag (1:N only)
- `MenuBehavior` — updated associated menu configuration
- `DryRun` (bool)

#### Choice (Option Set) Operations

```csharp
// Global option set CRUD
Task<CreateChoiceResult> CreateGlobalChoiceAsync(CreateGlobalChoiceRequest request, IProgressReporter? reporter = null, CancellationToken ct = default);
Task UpdateGlobalChoiceAsync(UpdateGlobalChoiceRequest request, IProgressReporter? reporter = null, CancellationToken ct = default);
Task DeleteGlobalChoiceAsync(DeleteGlobalChoiceRequest request, IProgressReporter? reporter = null, CancellationToken ct = default);

// Option value management (global and local)
Task<int> AddOptionValueAsync(AddOptionValueRequest request, CancellationToken ct = default);
Task UpdateOptionValueAsync(UpdateOptionValueRequest request, CancellationToken ct = default);
Task DeleteOptionValueAsync(DeleteOptionValueRequest request, CancellationToken ct = default);
Task ReorderOptionsAsync(ReorderOptionsRequest request, CancellationToken ct = default);

// State/Status label management (SDK-only)
Task UpdateStateValueAsync(UpdateStateValueRequest request, CancellationToken ct = default);
```

**CreateGlobalChoiceRequest properties:**
- `SolutionUniqueName` (required)
- `SchemaName`, `DisplayName`, `Description` (required)
- `Options[]` — initial option values with `Label`, `Value` (int), `Description`, `Color`
- `IsMultiSelect` (bool) — creates OptionSetType.Picklist or OptionSetType.MultiSelectPicklist. Determines whether columns referencing this choice must use `ColumnType.Choice` or `ColumnType.Choices`
- `DryRun` (bool)

#### Alternate Key Operations

```csharp
Task<CreateKeyResult> CreateKeyAsync(CreateKeyRequest request, IProgressReporter? reporter = null, CancellationToken ct = default);
Task DeleteKeyAsync(DeleteKeyRequest request, IProgressReporter? reporter = null, CancellationToken ct = default);
Task ReactivateKeyAsync(ReactivateKeyRequest request, CancellationToken ct = default);
```

**CreateKeyRequest properties:**
- `SolutionUniqueName`, `EntityLogicalName` (required)
- `SchemaName`, `DisplayName` (required)
- `KeyAttributes[]` (required) — 1–16 column logical names
- `DryRun` (bool)

**ReactivateKeyRequest:** For retrying failed key index creation — SDK-only capability not available in Maker UI.

### Validation

All authoring operations perform local validation before making SDK calls. In dry-run mode, validation runs but the SDK call is skipped.

| Rule | Applies To | Error |
|------|-----------|-------|
| Schema name matches solution publisher prefix | Tables, columns, relationships, choices, keys | `INVALID_PREFIX` |
| Schema name follows Dataverse naming rules (alphanumeric + underscore, starts with letter) | All schema objects | `INVALID_SCHEMA_NAME` |
| Required properties are non-empty | All schema objects | `MISSING_REQUIRED_FIELD` |
| Column type-specific constraints (e.g., MaxLength > 0 for String) | Columns | `INVALID_CONSTRAINT` |
| Key attributes exist on entity and are valid key types | Alternate keys | `INVALID_KEY_ATTRIBUTE` |
| Key count does not exceed 10 per entity | Alternate keys | `KEY_LIMIT_EXCEEDED` |
| Key attribute count between 1 and 16 | Alternate keys | `INVALID_KEY_ATTRIBUTE_COUNT` |
| Option values are unique within the option set | Choices | `DUPLICATE_OPTION_VALUE` |
| Entity exists (for column/relationship/key operations) | Columns, relationships, keys | `ENTITY_NOT_FOUND` |
| ColumnType is not Lookup (use relationship creation instead) | Columns | `USE_RELATIONSHIP_FOR_LOOKUP` |

### Cache Invalidation

After any successful authoring operation, the `CachedMetadataProvider` invalidates:
- The affected entity's cached detail (attributes, relationships, keys)
- The entity list cache (for table create/delete)
- Global option set cache (for choice create/update/delete)

Invalidation is scoped — modifying a column on `account` only invalidates the `account` cache entry, not the entire cache.

### Publish Integration

Authoring operations do **not** auto-publish. Users publish explicitly via the existing publish infrastructure:

```bash
# Publish specific entity changes
ppds publish --type entity account

# Publish via domain alias
ppds metadata publish account

# Publish all
ppds publish --all
```

This requires adding `--type entity` support to `PublishCommandGroup` and a `ppds metadata publish` alias command, following the pattern established by `ppds webresources publish`.

The `PublishXml` payload for entities:
```xml
<importexportxml><entities><entity>entitylogicalname</entity></entities></importexportxml>
```

### Delete Safety (Truncate Pattern)

Schema delete operations follow the truncate safety pattern:

**CLI interactive mode (no `--force`):**
1. Automatic dry-run executes first — reports dependencies (relationships, keys, columns that will cascade). This happens regardless of whether `--dry-run` was passed; the explicit `--dry-run` flag gives the same report and then exits without prompting.
2. Displays warning: "This will permanently delete {type} '{name}'. All data in this {type} will be lost. This operation cannot be undone."
3. Prompts for exact confirmation text: `DELETE TABLE account` or `DELETE COLUMN account.statuscode`
4. Exact case-sensitive string match required

**CLI non-interactive mode (stdin redirected, no `--force`):**
- Returns error code `CONFIRMATION_REQUIRED`
- Message: "Use --force to skip confirmation in non-interactive mode"

**CLI with `--force`:**
- Skips confirmation, executes immediately

**MCP surface:**
- No delete operations exposed — MCP tools support create and update only
- This matches the existing MCP design principle (read-only + non-destructive writes)

**TUI:**
- Yes/No confirmation dialog with dependency summary before delete (typed confirmation is impractical in dialog-based TUI; the dependency summary provides the safety context)

**Extension:**
- Confirmation dialog with dependency summary and "Delete" / "Cancel" buttons

### Surface-Specific Behavior

#### CLI Surface

Noun-verb subcommands under `ppds metadata`:

**Table commands:**
```bash
ppds metadata table create --solution <name> --name <schema> --display-name <name> --plural-name <name> [options]
ppds metadata table update --solution <name> --entity <name> [property flags]
ppds metadata table delete --solution <name> --entity <name> [--force] [--dry-run]
```

**Column commands:**
```bash
ppds metadata column create --solution <name> --entity <name> --name <schema> --display-name <name> --type <type> [type-specific options]
ppds metadata column update --solution <name> --entity <name> --column <name> [property flags]
ppds metadata column delete --solution <name> --entity <name> --column <name> [--force] [--dry-run]
```

**Relationship commands:**
```bash
ppds metadata relationship create --solution <name> --from <entity> --to <entity> --type one-to-many|many-to-many --name <schema> [options]
ppds metadata relationship update --solution <name> --name <schema> [--cascade-delete <behavior>] [--cascade-assign <behavior>] [options]
ppds metadata relationship delete --solution <name> --name <schema> [--force] [--dry-run]
```

**Choice commands:**
```bash
ppds metadata choice create --solution <name> --name <schema> --display-name <name> --options "Label1=1,Label2=2" [options]
ppds metadata choice update --solution <name> --name <name> [property flags]
ppds metadata choice delete --solution <name> --name <name> [--force] [--dry-run]
ppds metadata choice add-option --solution <name> --name <name> --label <label> --value <int> [--color <hex>]
ppds metadata choice update-option --solution <name> --name <name> --value <int> --label <new-label>
ppds metadata choice remove-option --solution <name> --name <name> --value <int> [--force]
ppds metadata choice reorder --solution <name> --name <name> --order "1,3,2,4"
```

**Key commands:**
```bash
ppds metadata key create --solution <name> --entity <name> --name <schema> --display-name <name> --attributes "attr1,attr2"
ppds metadata key delete --solution <name> --entity <name> --name <name> [--force] [--dry-run]
ppds metadata key reactivate --solution <name> --entity <name> --name <name>
```

**Publish alias:**
```bash
ppds metadata publish <entity>... [--solution <name>]
```

**Shared flags:** `--dry-run`, `--force` (delete only), `--profile`, `--environment`, `--json`

**Output:** Text mode writes status to stderr, structured results to stdout (Constitution I1). JSON mode returns full result objects.

#### TUI Surface

Authoring actions integrated into the existing `MetadataExplorerScreen`:

- **Action bar:** Add/Edit/Delete buttons context-sensitive to the active tab (Attributes, Relationships, Keys, Choices)
- **Create flows:** Dialog-based forms for each schema object type (fields, validation, type-specific options)
- **Edit flows:** Pre-populated dialog with current values
- **Delete flows:** Confirmation dialog showing dependency summary
- **Hotkeys:** Ctrl+N (new), Ctrl+E (edit selected), Ctrl+D (delete selected)
- **Progress:** Status bar feedback during SDK operations (Constitution A3)

#### Extension Surface

Authoring actions integrated into the existing Metadata Browser panel:

- **Action buttons:** "New Table", "New Column", "New Relationship", "New Choice", "New Key" context-sensitive to the active view
- **Inline editing:** Click-to-edit on mutable properties in detail tabs
- **Create forms:** Webview form panels for each schema object type with validation
- **Delete:** Context menu action with confirmation dialog showing dependencies
- **RPC endpoints:**
  - `metadata/createTable`, `metadata/updateTable`, `metadata/deleteTable`
  - `metadata/createColumn`, `metadata/updateColumn`, `metadata/deleteColumn`
  - `metadata/createRelationship`, `metadata/updateRelationship`, `metadata/deleteRelationship`
  - `metadata/createChoice`, `metadata/updateChoice`, `metadata/deleteChoice`
  - `metadata/addOptionValue`, `metadata/updateOptionValue`, `metadata/deleteOptionValue`, `metadata/reorderOptions`
  - `metadata/createKey`, `metadata/deleteKey`, `metadata/reactivateKey`

#### MCP Surface

Non-destructive tools only (create and update — no delete):

| Tool | Input | Output |
|------|-------|--------|
| `ppds_metadata_create_table` | `{ solution, schemaName, displayName, pluralName, ... }` | `{ logicalName, metadataId }` |
| `ppds_metadata_update_table` | `{ solution, entityName, displayName?, ... }` | `{ success }` |
| `ppds_metadata_add_column` | `{ solution, entityName, schemaName, type, ... }` | `{ logicalName, metadataId }` |
| `ppds_metadata_update_column` | `{ solution, entityName, columnName, ... }` | `{ success }` |
| `ppds_metadata_create_relationship` | `{ solution, from, to, type, schemaName, ... }` | `{ schemaName, metadataId }` |
| `ppds_metadata_update_relationship` | `{ solution, schemaName, cascadeConfig?, ... }` | `{ success }` |
| `ppds_metadata_create_choice` | `{ solution, schemaName, displayName, options[] }` | `{ name, metadataId }` |
| `ppds_metadata_update_choice` | `{ solution, name, displayName?, options? }` | `{ success }` |
| `ppds_metadata_add_option_value` | `{ solution, optionSetName, label, value }` | `{ value }` |
| `ppds_metadata_create_key` | `{ solution, entityName, schemaName, attributes[] }` | `{ schemaName }` |

All MCP tools support `dryRun` parameter. All use `McpToolBase` with `CreateScopeAsync()` per existing patterns.

### Constraints

- All authoring methods use `IDataverseConnectionPool` — never create `ServiceClient` per request (Constitution D1)
- Pooled clients are not held across multiple operations (Constitution D2)
- All exceptions wrapped in `PpdsException` with `ErrorCode` (Constitution D4)
- `CancellationToken` threaded through entire async chain (Constitution R2)
- CLI stdout is for data only — status to stderr (Constitution I1)
- No UI-level business logic — all validation and orchestration in `IMetadataAuthoringService` (Constitution A1)
- Extension webview forms must render Dataverse-sourced metadata (display names, descriptions) via `textContent` or proper escaping — never `innerHTML` (Constitution S1)

---

## Acceptance Criteria

| ID | Criterion | Test | Status |
|----|-----------|------|--------|
| AC-01 | `IMetadataService` renamed to `IMetadataQueryService` across entire codebase with no behavioral changes | `RegisterDataverseServicesTests.RegisterDataverseServices_RegistersIMetadataQueryService` | ✅ |
| AC-02 | `CreateTableAsync` creates a table in the specified solution with all required properties | `MetadataAuthoringServiceTests.CreateTableAsync_ValidRequest_CallsSdkAndReturnsResult` | ✅ |
| AC-03 | `CreateTableAsync` with `DryRun=true` validates without executing SDK call | `MetadataAuthoringServiceTests.CreateTableAsync_DryRun_DoesNotCallSdk` | ✅ |
| AC-04 | `CreateColumnAsync` creates columns of all supported types with type-specific properties (parameterized: String, Memo, Integer, BigInt, Decimal, Double, Money, Boolean, DateTime, Choice, Choices, Image, File) | `CreateColumnTypeTests.CreateColumn_{Type}_SetsTypeSpecificProperties` | ✅ |
| AC-05 | `CreateOneToManyAsync` creates a 1:N relationship with lookup column and cascade configuration | `SchemaValidatorTests.ValidateCreateOneToManyRequest_ValidRequest_DoesNotThrow` | ✅ |
| AC-06 | `CreateManyToManyAsync` creates an N:N relationship with intersect entity | `SchemaValidatorTests.ValidateCreateManyToManyRequest_ValidRequest_DoesNotThrow` | ✅ |
| AC-07 | `CreateGlobalChoiceAsync` creates a global option set with initial values | `CacheInvalidationTests.CreateGlobalChoice_InvalidatesGlobalOptionSets` | ✅ |
| AC-08 | `AddOptionValueAsync` adds a value to an existing global or local option set | — | ❌ |
| AC-09 | `CreateKeyAsync` creates an alternate key with specified attributes | `SchemaValidatorTests.ValidateCreateKeyRequest_OneAttribute_DoesNotThrow` | ✅ |
| AC-10 | `ReactivateKeyAsync` retries a failed key index creation | — | ❌ |
| AC-11 | `DeleteTableAsync` with `DryRun=true` reports dependencies without deleting | `MetadataAuthoringServiceTests.DeleteTableAsync_DryRun_DoesNotCallSdk` | ✅ |
| AC-12 | Validation rejects invalid schema names (spaces, special chars, wrong prefix) | `SchemaValidatorTests.ValidateSchemaName_InvalidChars_Throws` | ✅ |
| AC-13 | Validation rejects schema names that don't match solution publisher prefix | `SchemaValidatorTests.ValidateSchemaPrefix_WrongPrefix_Throws` | ✅ |
| AC-14 | Successful authoring operations invalidate relevant `CachedMetadataProvider` entries | `CacheInvalidationTests.CreateColumn_InvalidatesEntityCache_Scoped` | ✅ |
| AC-15 | `ppds metadata table create` CLI command creates table with required flags | `TableCreateCommandTests.Command_HasRequiredOptions` | ✅ |
| AC-16 | `ppds metadata column create` CLI command creates column with type-specific flags | `ColumnCreateCommandTests.Command_HasRequiredOptions` | ✅ |
| AC-17 | `ppds metadata table delete` without `--force` requires interactive confirmation matching truncate pattern | `TableDeleteCommandTests` | ✅ |
| AC-18 | `ppds metadata table delete` in non-interactive mode without `--force` returns `CONFIRMATION_REQUIRED` | `TableDeleteCommandTests` | ✅ |
| AC-19 | `ppds metadata publish` alias delegates to `ppds publish --type entity` | `MetadataPublishAliasTests` | ✅ |
| AC-20 | `ppds publish --type entity` publishes entity metadata via `PublishXmlRequest` | `PublishCommandEntityTypeTests` | ✅ |
| AC-21 | `ppds_metadata_create_table` MCP tool creates table and returns logical name | `MetadataCreateTableToolTests` | ✅ |
| AC-22 | MCP tools do not expose delete operations | `McpToolRegistrationTests.NoMetadataDeleteToolsRegistered` | ✅ |
| AC-23 | TUI Metadata Explorer shows action bar with Add/Edit/Delete per active tab | `MetadataExplorerScreenTests.ActionBarVisibility_AttributesTab_ShowsAllButtons` | ✅ |
| AC-24 | Extension Metadata Browser panel exposes authoring actions via RPC endpoints | `metadataBrowserPanel.test.ts` (message contracts) | ✅ |
| AC-25 | `UpdateTableAsync` modifies mutable table properties (display name, description, flags) | `MetadataAuthoringServiceTests.UpdateTableAsync_ChangesDisplayName` | ✅ |
| AC-26 | `UpdateColumnAsync` modifies mutable column properties | `MetadataAuthoringServiceTests.UpdateColumnAsync_ChangesRequiredLevel` | ✅ |
| AC-27 | `DeleteColumnAsync` CLI follows truncate confirmation pattern with `DELETE COLUMN entity.column` text | `ColumnDeleteCommandTests` | ✅ |
| AC-28 | `ReorderOptionsAsync` reorders option set values | `MetadataAuthoringServiceTests.ReorderOptionsAsync_SendsOrderOptionRequest` | ✅ |
| AC-29 | `UpdateStateValueAsync` renames state labels (SDK-only capability) | `MetadataAuthoringServiceTests.UpdateStateValueAsync_RenamesLabel` | ✅ |
| AC-30 | All SDK-calling authoring methods pass `CancellationToken` to SDK calls; pre-cancelled token throws `OperationCanceledException` | `MetadataAuthoringServiceTests.CreateTableAsync_PropagatesCancellationToken` | ✅ |
| AC-31 | All SDK-calling authoring methods report progress via `IProgressReporter` when provided | `MetadataAuthoringServiceTests.CreateTableAsync_ReportsPhases` | ✅ |
| AC-32 | CLI `--json` mode returns structured result objects to stdout for all authoring commands | `MetadataCommandJsonOutputTests.Command_HasOutputFormatOption` | ✅ |
| AC-33 | `UpdateRelationshipAsync` modifies cascade configuration on an existing 1:N relationship | `MetadataAuthoringServiceTests.UpdateRelationshipAsync_ChangesCascadeConfig` | ✅ |
| AC-34 | Extension Metadata Browser supports click-to-edit on mutable properties, calling update RPC endpoints | `metadataBrowserPanel.test.ts` (message contracts) | ✅ |
| AC-35 | Validation rejects `ColumnType.Lookup` with `USE_RELATIONSHIP_FOR_LOOKUP` directing user to create a relationship | `MetadataAuthoringServiceTests.CreateColumnAsync_LookupType_ThrowsValidationException` | ✅ |
| AC-36 | Validation rejects key with 0 or >16 attributes with `INVALID_KEY_ATTRIBUTE_COUNT` | `SchemaValidatorTests.ValidateCreateKeyRequest_ZeroAttributes_ThrowsWithInvalidKeyAttributeCount` | ✅ |

### Edge Cases

| Scenario | Input | Expected Output |
|----------|-------|-----------------|
| Create table with duplicate schema name | `SchemaName` already exists | `PpdsException` with `DUPLICATE_SCHEMA_NAME` |
| Delete table with dependent relationships | Table has 1:N relationships | Dry-run reports dependencies; delete cascades |
| Create column on non-existent entity | `EntityLogicalName` not found | `PpdsException` with `ENTITY_NOT_FOUND` |
| Create key exceeding 10-key limit | Entity already has 10 keys | `PpdsException` with `KEY_LIMIT_EXCEEDED` |
| Create key with invalid column type | Key attribute is Image type | `PpdsException` with `INVALID_KEY_ATTRIBUTE` |
| Create key with 0 attributes | Empty `KeyAttributes[]` | `PpdsException` with `INVALID_KEY_ATTRIBUTE_COUNT` |
| Create key with >16 attributes | 17 attributes in `KeyAttributes[]` | `PpdsException` with `INVALID_KEY_ATTRIBUTE_COUNT` |
| Create Lookup column directly | `ColumnType = Lookup` | `PpdsException` with `USE_RELATIONSHIP_FOR_LOOKUP` |
| Add option with duplicate value | Value already exists in set | `PpdsException` with `DUPLICATE_OPTION_VALUE` |
| Delete managed component | Component `IsManaged = true` | `PpdsException` with `CANNOT_DELETE_MANAGED` |
| Create column with wrong publisher prefix | Prefix doesn't match solution publisher | `PpdsException` with `INVALID_PREFIX` |
| Concurrent authoring + publish | Authoring during publish | Operations are independent; cache invalidation still fires |
| Reactivate key that is Active | Key status is already Active | No-op, returns success |

---

## Core Types

### IMetadataAuthoringService

Central service interface for all schema write operations.

```csharp
public interface IMetadataAuthoringService
{
    // Tables
    Task<CreateTableResult> CreateTableAsync(CreateTableRequest request, IProgressReporter? reporter = null, CancellationToken ct = default);
    Task UpdateTableAsync(UpdateTableRequest request, IProgressReporter? reporter = null, CancellationToken ct = default);
    Task DeleteTableAsync(DeleteTableRequest request, IProgressReporter? reporter = null, CancellationToken ct = default);

    // Columns
    Task<CreateColumnResult> CreateColumnAsync(CreateColumnRequest request, IProgressReporter? reporter = null, CancellationToken ct = default);
    Task UpdateColumnAsync(UpdateColumnRequest request, IProgressReporter? reporter = null, CancellationToken ct = default);
    Task DeleteColumnAsync(DeleteColumnRequest request, IProgressReporter? reporter = null, CancellationToken ct = default);

    // Relationships
    Task<CreateRelationshipResult> CreateOneToManyAsync(CreateOneToManyRequest request, IProgressReporter? reporter = null, CancellationToken ct = default);
    Task<CreateRelationshipResult> CreateManyToManyAsync(CreateManyToManyRequest request, IProgressReporter? reporter = null, CancellationToken ct = default);
    Task UpdateRelationshipAsync(UpdateRelationshipRequest request, IProgressReporter? reporter = null, CancellationToken ct = default);
    Task DeleteRelationshipAsync(DeleteRelationshipRequest request, IProgressReporter? reporter = null, CancellationToken ct = default);

    // Choices
    Task<CreateChoiceResult> CreateGlobalChoiceAsync(CreateGlobalChoiceRequest request, IProgressReporter? reporter = null, CancellationToken ct = default);
    Task UpdateGlobalChoiceAsync(UpdateGlobalChoiceRequest request, IProgressReporter? reporter = null, CancellationToken ct = default);
    Task DeleteGlobalChoiceAsync(DeleteGlobalChoiceRequest request, IProgressReporter? reporter = null, CancellationToken ct = default);
    Task<int> AddOptionValueAsync(AddOptionValueRequest request, CancellationToken ct = default);
    Task UpdateOptionValueAsync(UpdateOptionValueRequest request, CancellationToken ct = default);
    Task DeleteOptionValueAsync(DeleteOptionValueRequest request, CancellationToken ct = default);
    Task ReorderOptionsAsync(ReorderOptionsRequest request, CancellationToken ct = default);
    Task UpdateStateValueAsync(UpdateStateValueRequest request, CancellationToken ct = default);

    // Alternate Keys
    Task<CreateKeyResult> CreateKeyAsync(CreateKeyRequest request, IProgressReporter? reporter = null, CancellationToken ct = default);
    Task DeleteKeyAsync(DeleteKeyRequest request, IProgressReporter? reporter = null, CancellationToken ct = default);
    Task ReactivateKeyAsync(ReactivateKeyRequest request, CancellationToken ct = default);
}
```

### Usage Pattern

```csharp
// Service injected via DI
var result = await authoringService.CreateTableAsync(new CreateTableRequest
{
    SolutionUniqueName = "contoso_inventory",
    SchemaName = "contoso_widget",
    DisplayName = "Widget",
    PluralDisplayName = "Widgets",
    Description = "Tracks widget inventory",
    OwnershipType = OwnershipType.UserOwned,
    DryRun = false
}, reporter: progressReporter, ct: cancellationToken);

// result.LogicalName = "contoso_widget"
// result.MetadataId = Guid
```

---

## Error Handling

### Error Types

| Error Code | Condition | Recovery |
|-----------|-----------|----------|
| `INVALID_SCHEMA_NAME` | Schema name contains invalid characters or format | Fix name and retry |
| `INVALID_PREFIX` | Schema name prefix doesn't match solution publisher | Use correct publisher prefix |
| `MISSING_REQUIRED_FIELD` | Required property not provided | Provide missing field |
| `INVALID_CONSTRAINT` | Type-specific constraint violation (e.g., negative MaxLength) | Fix constraint value |
| `DUPLICATE_SCHEMA_NAME` | Schema name already exists in environment | Choose different name |
| `ENTITY_NOT_FOUND` | Target entity doesn't exist | Verify entity logical name |
| `CANNOT_DELETE_MANAGED` | Attempting to delete a managed component | Cannot delete managed — modify unmanaged layer |
| `KEY_LIMIT_EXCEEDED` | Entity already has 10 alternate keys | Remove an existing key first |
| `INVALID_KEY_ATTRIBUTE` | Key attribute type not supported for keys | Use supported column types |
| `DUPLICATE_OPTION_VALUE` | Option value already exists in set | Use unique value |
| `CONFIRMATION_REQUIRED` | Non-interactive delete without `--force` | Use `--force` flag |
| `DEPENDENCY_CONFLICT` | Object has dependencies preventing deletion | Remove dependencies first or use cascade |
| `INVALID_KEY_ATTRIBUTE_COUNT` | Key has 0 or >16 attributes | Use 1–16 attributes per key |
| `USE_RELATIONSHIP_FOR_LOOKUP` | Attempted to create Lookup column directly | Use `CreateOneToManyAsync` instead |

### Recovery Strategies

- **Validation errors:** Fix input and retry — all validation errors include the specific field and constraint that failed
- **Dependency conflicts:** Use `--dry-run` first to discover dependencies, then resolve or accept cascade behavior
- **Managed component errors:** Cannot modify managed components directly — create unmanaged customizations in the target solution

---

## Design Decisions

### Why separate IMetadataAuthoringService from IMetadataQueryService?

**Context:** Metadata authoring could extend the existing `IMetadataService` or live in a separate interface.

**Decision:** Separate interfaces — `IMetadataQueryService` (read) and `IMetadataAuthoringService` (write).

**Rationale:**
- Single Responsibility — queries are cached, reads-only; writes need validation, solution context, cache invalidation
- Independent lifecycles — query service is stable/shipped; authoring is new and will evolve
- Interface segregation — consumers that only read shouldn't depend on write methods
- Testability — focused interfaces are simpler to mock

**Consequences:**
- Positive: Clean separation, independent evolution, focused testing
- Negative: Two services to inject when both read and write are needed

### Why rename IMetadataService to IMetadataQueryService?

**Context:** With a new `IMetadataAuthoringService`, the existing `IMetadataService` name is ambiguous — "service" doesn't distinguish read from write.

**Decision:** Rename to `IMetadataQueryService` pre-v1 while there are no public API commitments.

**Rationale:** Post-v1 this rename would be a breaking change. Pre-v1 is the only window to fix naming inconsistency at low cost. The rename is mechanical (find-and-replace) with no behavioral changes.

### Why explicit --solution on every command?

**Context:** Schema changes must target a Dataverse solution. Could infer from profile, default to Default Solution, or require explicit specification.

**Decision:** Require `--solution` on every authoring command. No default, no inference.

**Rationale:** Accidental schema creation in the Default Solution is a common and painful Dataverse mistake. Explicit is better than implicit for irreversible operations. Future enhancement (post-v1): solution workspace configuration that maps project structure to solutions.

### Why no auto-publish?

**Context:** Dataverse schema changes require publishing to take effect. Could auto-publish after each operation or require explicit publish.

**Decision:** No auto-publish. Follow existing pattern — publish is always a separate, explicit step via `ppds publish --type entity`.

**Rationale:** Established codebase pattern — web resources already work this way. Batch workflows benefit from deferring publish (one publish vs. N publishes). The publish infrastructure (`PublishCommandGroup`, per-environment semaphore, domain aliases) already exists and is extensible.

### Why mimic truncate's delete safety pattern?

**Context:** Schema deletes are destructive and irreversible. Need a confirmation mechanism.

**Decision:** Follow the truncate pattern: `--dry-run` for impact preview, interactive confirmation with exact typed text, `--force` to skip, `CONFIRMATION_REQUIRED` error in non-interactive mode.

**Rationale:** Consistency — users who've used `ppds truncate` already understand this pattern. The typed confirmation text (e.g., `DELETE TABLE account`) forces acknowledgment of exactly what's being destroyed. MCP surfaces don't expose delete at all, matching the existing read-only + non-destructive MCP design.

### Why no delete via MCP?

**Context:** MCP tools could expose delete operations with dry-run as a safeguard.

**Decision:** MCP tools support create and update only — no delete.

**Rationale:** MCP is designed for AI agent workflows. An AI agent accidentally deleting a table is catastrophic and not easily recoverable. The interactive confirmation pattern (typed text) doesn't translate well to MCP tool invocation. If an AI agent needs to delete schema, it can instruct the user to run the CLI command.

---

## Related Specs

- [metadata-browser.md](./metadata-browser.md) — Read-only metadata queries (renamed service)
- [publish.md](./publish.md) — Publish infrastructure (`--type entity` integration)
- [connection-pooling.md](./connection-pooling.md) — Dataverse connection management
- [CONSTITUTION.md](./CONSTITUTION.md) — Governing principles

---

## Changelog

| Date | Change |
|------|--------|
| 2026-03-31 | Initial spec |
| 2026-04-01 | Post-implementation cleanup: fixed CodeQL findings, completed TUI choice editing, replaced Extension webview stubs with VS Code input collection, updated AC statuses |

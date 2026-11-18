# Metadata Authoring

**Status:** Implemented (extending — #1159/#1160/#1161 ratified, in implementation; AC-37–AC-60 reach ✅ as their tests land; #1208 positional `<entity>` shipped, AC-61/AC-62)
**Last Updated:** 2026-11-17
**Code:** [src/PPDS.Dataverse/Metadata/](../src/PPDS.Dataverse/Metadata/) | [src/PPDS.Cli/Commands/Metadata/](../src/PPDS.Cli/Commands/Metadata/) | [src/PPDS.Cli/Services/Metadata/](../src/PPDS.Cli/Services/Metadata/) | [src/PPDS.Mcp/Tools/](../src/PPDS.Mcp/Tools/) | [src/PPDS.Cli/Tui/Screens/](../src/PPDS.Cli/Tui/Screens/) | [src/PPDS.Extension/src/panels/](../src/PPDS.Extension/src/panels/)
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
- **Canonical, SDK-aligned command surface:** One canonical noun per schema object — `entity`, `attribute`, `optionset` — matching the SDK's singular `*Metadata` naming (`EntityMetadata`, `AttributeMetadata`, `OptionSetMetadata`), consistent with the already-canonical `relationship` and `key`. Redundant legacy nouns (`table`, `column`, `choice`) keep working as deprecation shims (#1159).
- **Status reason management:** First-class add/list/remove/update of `statuscode` (status reason) option values on an entity — no more raw `InsertStatusValue` POSTs (#1160).
- **Local choice columns:** `attribute create --type Choice` creates a column-scoped (local) option set by default, plus local option add/remove/update (#1161).

### Non-Goals

- Forms, views, charts, dashboards (XML-based visual design — separate domain)
- Formula, calculated, and rollup column definitions (complex DSLs best handled in Maker UI)
- Business rules and prompt columns
- Duplicate detection rules
- Solution management (covered by `specs/solutions.md`)
- Data operations (covered by `specs/data-explorer.md`, `specs/query.md`)
- **`statecode` (state) authoring** — creating/deleting the Active/Inactive states themselves is not exposed; only their child status reasons (`statuscode`) are managed (#1160). Renaming existing state labels remains available via `UpdateStateValueAsync`.
- **Adopting publisher-prefix value derivation on `optionset add-option` (global)** — this existing add-option operation retains SDK auto-assignment. Initial global-choice creation now shares publisher-prefix derivation with local creation, status reasons and local column options; the global add-option extension remains a follow-up.
- **Read-side noun renames** — query commands (`entities`, `attributes`, `relationships`, `keys`, `optionsets`, plus `entity <name>` / `optionset <name>` lookups) are unchanged except where a noun gains authoring subcommands.

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

All methods accept `CancellationToken` (Constitution R2). Methods that make SDK calls (create, update, delete) accept a progress reporter for operation feedback (Constitution A3). The authoring service uses the domain-specific `IMetadataAuthoringProgressReporter?` (defined in `src/PPDS.Dataverse/Metadata/Authoring/IMetadataAuthoringProgressReporter.cs` — phase/info reporting tailored to schema operations); the generic `IProgressReporter` shorthand in older prose and the `## Core Types` interface block refers to this same authoring reporter. New methods (`AddStatusReasonAsync`) follow the established `IMetadataAuthoringProgressReporter?` signature. All methods require `solutionUniqueName` via the request DTO. All methods wrap exceptions in `PpdsException` with `ErrorCode` (Constitution D4).

#### Table Operations

```csharp
Task<CreateTableResult> CreateTableAsync(CreateTableRequest request, IProgressReporter? reporter = null, CancellationToken ct = default);
Task<MetadataLabelWriteResult> UpdateTableAsync(UpdateTableRequest request, IProgressReporter? reporter = null, CancellationToken ct = default);
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
Task<MetadataLabelWriteResult> UpdateColumnAsync(UpdateColumnRequest request, IProgressReporter? reporter = null, CancellationToken ct = default);
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
- `IntersectEntitySchemaName` — schema name of the auto-generated intersect (link) table that holds the M:N associations. Required by the Dataverse SDK message but optional on this DTO: when null/empty, the service defaults it to `SchemaName` (matches Power Apps Maker convention and the Microsoft Learn `CreateManyToManyRequest` sample). Override only when the relationship name and intersect-entity name must differ. Validated for publisher prefix alongside `SchemaName`, so dry-run catches mismatches.
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
Task<MetadataLabelWriteResult> UpdateGlobalChoiceAsync(UpdateGlobalChoiceRequest request, IProgressReporter? reporter = null, CancellationToken ct = default);
Task DeleteGlobalChoiceAsync(DeleteGlobalChoiceRequest request, IProgressReporter? reporter = null, CancellationToken ct = default);

// Option value management (global and local)
Task<int> AddOptionValueAsync(AddOptionValueRequest request, CancellationToken ct = default);
Task<MetadataLabelWriteResult> UpdateOptionValueAsync(UpdateOptionValueRequest request, CancellationToken ct = default);
Task DeleteOptionValueAsync(DeleteOptionValueRequest request, CancellationToken ct = default);
Task ReorderOptionsAsync(ReorderOptionsRequest request, CancellationToken ct = default);

// State/Status label management (SDK-only)
Task UpdateStateValueAsync(UpdateStateValueRequest request, CancellationToken ct = default);
```

**CreateGlobalChoiceRequest properties:**
- `SolutionUniqueName` (required)
- `SchemaName`, `DisplayName`, `Description` (required)
- `Options[]` — initial option values with `Label`, `Value` (int), `Description`, `Color`; omitted Value differs from explicit0 through the additive read-only `HasExplicitValue` getter
- `IsMultiSelect` (bool) — creates OptionSetType.Picklist or OptionSetType.MultiSelectPicklist. Determines whether columns referencing this choice must use `ColumnType.Choice` or `ColumnType.Choices`
- `DryRun` (bool)

**Local (column-scoped) option sets — #1161 bug fix.** When a `Choice`/`Choices` column is created with inline options (no global choice referenced), the SDK requires `OptionSetMetadata.IsGlobal` to be **explicitly** set to `false`; omitting it produces the Dataverse error *"IsGlobal is not specified"*. `BuildChoiceAttribute`/`BuildMultiSelectChoiceAttribute` MUST set `IsGlobal = false` and `OptionSetType = OptionSetType.Picklist` on the inline `OptionSetMetadata`. Local option add/remove/update reuse the existing `AddOptionValueAsync`/`UpdateOptionValueAsync`/`DeleteOptionValueAsync` methods, which already carry optional `EntityLogicalName` + `AttributeLogicalName` to scope the operation to a column's local set.

#### Status Reason Operations (#1160)

Status reasons are the option values of an entity's `statuscode` attribute (a `StatusAttributeMetadata`). Each value belongs to a state (`statecode`: 0 = Active, 1 = Inactive). Inserting a status reason requires the SDK `InsertStatusValueRequest` (which carries `StateCode`); the generic `InsertOptionValueRequest` cannot create them. List/update/remove reuse the entity-scoped option-value paths against `statuscode`.

```csharp
Task<int> AddStatusReasonAsync(AddStatusReasonRequest request, IMetadataAuthoringProgressReporter? reporter = null, CancellationToken ct = default);
Task<IReadOnlyList<StatusReasonInfo>> ListStatusReasonsAsync(string entityLogicalName, CancellationToken ct = default);
Task<MetadataLabelWriteResult> UpdateStatusReasonAsync(UpdateStatusReasonRequest request, CancellationToken ct = default);
Task RemoveStatusReasonAsync(RemoveStatusReasonRequest request, CancellationToken ct = default);
```

**AddStatusReasonRequest properties:**
- `EntityLogicalName` (required)
- `Label` (required)
- `StateCode` (required, int) — 0 (Active) or 1 (Inactive); the parent state the reason belongs to
- `Value` (int?) — explicit option value; when null, derived via `OptionValueDeriver` from `SolutionUniqueName`
- `SolutionUniqueName` (string?) — required for value derivation when `Value` is null; also scopes the SDK request
- `Color` (string?) — hex color
- `Publish` (bool) — publish the entity after the change
- `DryRun` (bool)

**Value derivation (shared, #1160 + #1161):** explicit `Value` wins; else `SolutionUniqueName` → publisher option-value prefix × 10,000, advancing to the lowest free value at or above that base (skipping values already present on `statuscode`, including filling gaps); else fail with `MISSING_REQUIRED_FIELD` ("provide --value or --solution"). See `OptionValueDeriver` in Core Types. Collision behavior is explicit: an **explicit** `Value` that already exists on the set is rejected with `DUPLICATE_OPTION_VALUE`; a **derived** value auto-advances past existing values and never collides.

**StatusReasonInfo (list projection):** `Value` (int), `Label`, `StateCode` (int), `StateLabel` (Active/Inactive), `Color`.

**UpdateStatusReasonRequest:** `EntityLogicalName` (required); target by `Value` (int?) **or** `Label` (string?) — exactly one required; `NewLabel` (string?), `Color` (string?); `SolutionUniqueName` (string?); `Publish` (bool). Delegates to `UpdateOptionValueAsync` scoped to `statuscode`.

**RemoveStatusReasonRequest:** `EntityLogicalName` (required); target by `Value` (int?) **or** `Label` (string?) — exactly one required; `SolutionUniqueName` (string?); `Publish` (bool). Delegates to `DeleteOptionValueAsync` scoped to `statuscode`.

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

In dry-run mode, mutation SDK calls are skipped. Checks vary by operation: creates resolve publisher information and validate inputs; schema updates validate required strings; option update/removal resolves the option; status/local additions read options and derive a provisional value. Schema deletes resolve unpublished target metadata and read deletion dependencies. Other platform constraints are not simulated.

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
| Local choice column sets `OptionSetMetadata.IsGlobal = false` explicitly | Choice/Choices columns with inline options | `IsGlobal` SDK fault (#1161) — prevented by fix |
| Exactly one of `--value` / `--solution` (neither → missing; both → invalid) | `add-statusreason`, `attribute add-option` | `MISSING_REQUIRED_FIELD` (neither) / `INVALID_CONSTRAINT` (both) |
| Exactly one of `--value` / `--label` (neither → missing; both → invalid) | `update-/remove-statusreason`, `attribute update-/remove-option`, `optionset update-/remove-option` | `MISSING_REQUIRED_FIELD` (neither) / `INVALID_CONSTRAINT` (both) |
| Exactly one of `--state` / `--state-code` (neither → missing; both → invalid) | `add-statusreason` | `MISSING_REQUIRED_FIELD` (neither) / `INVALID_CONSTRAINT` (both) |
| `--choice` mutually exclusive with `--option`/`--options`/`--options-file` | `attribute create --type Choice` | `INVALID_CONSTRAINT` |
| `--option-set-name` mutually exclusive with `--options` (Choice/Choices); at most one of `--option`/`--options`/`--options-file`; malformed `--options` entry | `column create` / `attribute create` (Choice/Choices) | `INVALID_CONSTRAINT` |
| Global choice name matches several stored names case-insensitively (exact spelling not stored) | choice update, delete preview, option add/update/remove/reorder, color readback | `INVALID_CONSTRAINT` |
| `--label` names different options in published and unpublished metadata | `update-/remove-statusreason`, local option update/remove, `optionset update-/remove-option` | `INVALID_CONSTRAINT` |
| Explicit option value not already present on the target set | status reasons, local options | `DUPLICATE_OPTION_VALUE` |
| Target status reason / option resolvable by value or label | `update-/remove-statusreason`, local option update/remove | `OPTION_NOT_FOUND` |

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
1. Read-only preflight resolves the named target and deletion dependencies first. Blockers or incomplete inspection stop before confirmation. `--dry-run` reports the observation and exits without prompting or mutating; a complete empty result does not establish server deletion feasibility. The service rechecks dependencies immediately before an actual deletion after confirmation.
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

### Command Surface Rationalization (#1159)

The authoring surface uses **one canonical noun per schema object**, matching the SDK's singular `*Metadata` types and the already-canonical `relationship`/`key`:

| Schema object | Canonical noun | Deprecated alias (kept working) | SDK type |
|---------------|----------------|---------------------------------|----------|
| Table / entity | `entity` | `table` | `EntityMetadata` |
| Column / field | `attribute` | `column` | `AttributeMetadata` |
| Global option set | `optionset` (read+write) / `optionsets` (list) | `choice`, `choices` | `OptionSetMetadata` |
| Relationship | `relationship` | — (already canonical) | `*RelationshipMetadata` |
| Alternate key | `key` | — (already canonical) | `EntityKeyMetadata` |

Per issue #1159, **both** the singular `choice` and the plural `choices` are deprecated aliases. `choices` (a plural that never had write verbs) is registered solely as a deprecation alias whose warning points to the canonical `optionset`/`optionsets`.

**Verb placement.** Every authoring verb is a subcommand of its canonical noun (`entity create`, `attribute update`, `optionset add-option`) — verb-first, consistent with the existing `relationship`/`key` groups and with the deprecated `table`/`column`/`choice` groups being replaced. The nouns `entity` and `optionset` are **dual-purpose commands**: a bare positional token is the existing read lookup (`metadata entity account`, `metadata optionset new_status`), while a recognized verb token routes to the authoring subcommand (`metadata entity create …`). System.CommandLine binds a subcommand token before the parent's positional argument, so the two never collide except for the pathological case of a Dataverse object literally named `create`/`update`/`delete`/etc.; that edge is documented, not guarded. `attribute` has no read lookup today (reads go through the plural `attributes --entity <e>`), so it is a pure authoring group.

**Deprecation shims.** The deprecated nouns `table`, `column`, `choice`, and `choices` remain registered with identical options and behavior (`choices` carries no write verbs — it only warns toward `optionsets`/`optionset`). Each deprecated subcommand, on invocation, writes a single deprecation warning to **stderr** (Constitution I1 — never stdout) naming the exact canonical replacement, then delegates to the **same** shared execute path as the canonical command (no logic duplication; Constitution A1/A2). Example:

```
warning: 'ppds metadata table create' is deprecated and will be removed in a future release.
         Use 'ppds metadata entity create' instead.
```

The warning is suppressed in `--json` mode's data stream (it is stderr-only and never pollutes stdout). `ppds metadata --help` lists only the canonical nouns plus the publish alias; deprecated nouns are marked `(deprecated)` in their one-line description.

### Surface-Specific Behavior

#### CLI Surface

Canonical noun-verb subcommands under `ppds metadata`. Deprecated forms (`table`/`column`/`choice`) accept the identical flags and emit the stderr deprecation warning above.

**Entity commands (was `table`):**
```bash
ppds metadata entity <name>                                      # read lookup (unchanged)
ppds metadata entity create --solution <s> --name <schema> --display-name <n> --plural-name <n> --ownership <UserOwned|OrganizationOwned> [--publish] [options]
ppds metadata entity update <entity> --solution <s> [property flags] [--publish]
ppds metadata entity delete <entity> --solution <s> [--force] [--dry-run]
```

**Status reason commands (#1160, new — on `entity`):**
```bash
ppds metadata entity add-statusreason <entity> --label <label> (--value <int> | --solution <s>) [--state Active|Inactive | --state-code 0|1] [--color <#hex>] [--publish] [--dry-run]
ppds metadata entity list-statusreasons <entity>
ppds metadata entity update-statusreason <entity> (--value <int> | --label <label>) [--new-label <label>] [--color <#hex>] [--publish]
ppds metadata entity remove-statusreason <entity> (--value <int> | --label <label>) [--force] [--publish]
```

All six `entity` authoring verbs (`update`, `delete`, `add-statusreason`, `list-statusreasons`, `update-statusreason`, `remove-statusreason`) identify the entity with a **positional `<entity>` argument**, with `--entity <name>` retained as a fully equivalent flag for back-compat (#1208). A positional on the *subcommand* binds after the verb token, so it never contends with the parent's `entity <name>` read lookup — the original flag-only rationale overstated the collision risk; the real consideration was cross-noun consistency, and operator usage showed the flag-only form was a recurring stumble (users naturally type `entity <name> update`, which the parser cannot route, then reach for `entity update <name>`, which previously printed help). When both the positional and `--entity` are supplied they must agree (case-insensitive) or the parse fails with a clear "disagree" error; supplying neither is also a parse error. `attribute` and `key` verbs keep `--entity` only (scope (a) of #1208 — they have no positional read form to mirror). `--state` and `--state-code` are mutually exclusive (`--state` maps Active→0, Inactive→1); one is required for `add`. `add` requires exactly one of `--value` / `--solution`; `update`/`remove` require exactly one of `--value` / `--label` to target the reason.

**Attribute commands (was `column`):**
```bash
ppds metadata attribute create --solution <s> --entity <name> --name <schema> --display-name <n> --type <type> [type-specific options]
ppds metadata attribute update --solution <s> --entity <name> --column <name> [property flags] [--publish]
ppds metadata attribute delete --solution <s> --entity <name> --column <name> [--force] [--dry-run]
```

**Local Choice column creation + local option management (#1161, on `attribute`):**
```bash
# Local (column-scoped) option set — DEFAULT when --option/--options/--options-file given:
ppds metadata attribute create --solution <s> --entity <e> --name <schema> --display-name <n> --type Choice \
    --option "Label[:Value][:#Color]" [--option ...] [--default-value <int>] [--publish]
ppds metadata attribute create ... --type Choice --options-file <path.json> [--publish]
# Attach to an existing GLOBAL option set (mutually exclusive with --option/--options/--options-file):
ppds metadata attribute create ... --type Choice --choice <global-optionset-name> [--publish]

ppds metadata attribute add-option --solution <s> --entity <e> --column <c> --label <l> (--value <int> | --solution <s>) [--color <#hex>] [--publish]
ppds metadata attribute update-option --solution <s> --entity <e> --column <c> (--value <int> | --label <l>) [--new-label <l>] [--color <#hex>] [--publish]
ppds metadata attribute remove-option --solution <s> --entity <e> --column <c> (--value <int> | --label <l>) [--force] [--publish]
```

- `--option` (repeatable) uses `Label[:Value][:#Color]` — label required, value/color optional; omitted values derive from the required solution's publisher prefix, while an explicit0 remains0. `--options-file` is a JSON array of `{ "label", "value"?, "color"? }`. `--options` (legacy `"Label1=1,Label2=2"` CSV) remains accepted and maps to the same local set; a bare `Label` is a label-only option and a malformed entry is rejected (Choice/Choices only). Labels cannot contain `,` or `=` in this CSV form; use `--option` or `--options-file` for such labels. Supply only one of `--option`, `--options` and `--options-file`.
- `--choice <name>` replaces and supersedes the legacy `--option-set-name` flag (kept as a hidden deprecated alias) for attaching to a global option set. `--choice` is mutually exclusive with `--option`/`--options`/`--options-file`; supplying both is a validation error.
- `add-option` derives the new value via the shared `OptionValueDeriver` (explicit `--value` wins; else `--solution` prefix × 10,000 over the column's current local options; else SDK auto-assign). `update-option`/`remove-option` target by `--value` or `--label`.

**Relationship commands (unchanged):**
```bash
ppds metadata relationship create --solution <name> --from <entity> --to <entity> --type one-to-many|many-to-many --name <schema> [options]
ppds metadata relationship update --solution <name> --name <schema> [--cascade-delete <behavior>] [--cascade-assign <behavior>] [options]
ppds metadata relationship delete --solution <name> --name <schema> [--force] [--dry-run]
```

**Option set commands (was `choice` — global option sets):**
```bash
ppds metadata optionset <name>                                   # read lookup (unchanged)
ppds metadata optionset create --solution <s> --name <schema> --display-name <n> --options "Label1=1,Label2=2" [options]
ppds metadata optionset update --solution <s> --name <name> [property flags]
ppds metadata optionset delete --solution <s> --name <name> [--force] [--dry-run]
ppds metadata optionset add-option --solution <s> --name <name> --label <label> [--value <int>] [--color <hex>] [--dry-run]
ppds metadata optionset update-option --solution <s> --name <name> (--value <int> | --label <l>) [--new-label <l>] [--color <#hex>] [--dry-run]
ppds metadata optionset remove-option --solution <s> --name <name> (--value <int> | --label <l>) [--force] [--dry-run]
ppds metadata optionset reorder --solution <s> --name <name> --order "1,3,2,4"
```

**Key commands (unchanged):**
```bash
ppds metadata key create --solution <name> --entity <name> --name <schema> --display-name <name> --attributes "attr1,attr2"
ppds metadata key delete --solution <name> --entity <name> --name <name> [--force] [--dry-run]
ppds metadata key reactivate --solution <name> --entity <name> --name <name>
```

**Publish alias:**
```bash
ppds metadata publish <entity>... [--solution <name>]
```

**Shared flags where exposed by the verb:** `--dry-run`, `--force` (delete/remove only), `--publish` (authoring verbs that change live metadata), `--profile`, `--environment`, `--json`

**Output:** Text mode writes status (including deprecation warnings) to stderr, structured results to stdout (Constitution I1). JSON mode returns full result objects on stdout.

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

All MCP authoring tools except `ppds_metadata_add_option_value` support a `dryRun` parameter. All use `McpToolBase` with `CreateScopeAsync()` per existing patterns.

A `MetadataValidationException` from the authoring service is reported like the CLI reports it: the structured MCP error carries its `errorCode` (e.g. `INVALID_CONSTRAINT`), its message as `userMessage`, and the failing request field as `context.field` when known. Its inner exception is never read, so server diagnostic text stays withheld. Other non-PPDS exceptions keep the generic `Operation.Internal` error (AC-144).

### Constraints

- All authoring methods use `IDataverseConnectionPool` — never create `ServiceClient` per request (Constitution D1)
- Pooled clients are not held across multiple operations (Constitution D2)
- All exceptions wrapped in `PpdsException` with `ErrorCode` (Constitution D4)
- `CancellationToken` threaded through entire async chain (Constitution R2)
- CLI stdout is for data only — status to stderr (Constitution I1)
- No UI-level business logic — all validation and orchestration in `IMetadataAuthoringService` (Constitution A1)
- Extension webview forms must render Dataverse-sourced metadata (display names, descriptions) via `textContent` or proper escaping — never `innerHTML` (Constitution S1)

### Connection lifetime and existing error contracts

Generic option add/update, status-reason update/removal, and table creation return their mutation connection before optional color/publication or unrelated postprocessing. Global/local color helpers acquire separate connections for the metadata read and update. Publisher-prefix resolution acquires separate connections for its solution and publisher queries, retains only the publisher ID between them, and keeps the existing prefix cache and table dry-run metadata reads.

This lifetime correction preserves existing SDK payloads, read modes, validation, guards, cache success stages and error contracts. The scoped cancellation/acquisition and diagnostic-message corrections below change only their named boundaries; other SDK/read behavior remains as implemented. Acquisition normalization is separate from SDK fault handling. Multi-request authoring remains non-atomic: a later failure can follow a successful mutation, and no automatic retry or rollback is introduced.

---

## Acceptance Criteria

| ID | Criterion | Test | Status |
|----|-----------|------|--------|
| AC-01 | `IMetadataService` renamed to `IMetadataQueryService` across entire codebase with no behavioral changes | `RegisterDataverseServicesTests.RegisterDataverseServices_RegistersIMetadataQueryService` | ✅ |
| AC-02 | `CreateTableAsync` creates a table in the specified solution with all required properties | `MetadataAuthoringServiceTests.CreateTableAsync_ValidRequest_CallsSdkAndReturnsResult` | ✅ |
| AC-03 | `CreateTableAsync` with `DryRun=true` validates without executing SDK call | `MetadataAuthoringServiceTests.CreateTableAsync_DryRun_DoesNotCallSdk` | ✅ |
| AC-04 | `CreateColumnAsync` creates columns of all supported types with type-specific properties (parameterized: String, Memo, Integer, BigInt, Decimal, Double, Money, Boolean, DateTime, Choice, Choices, Image, File) | `CreateColumnTypeTests.CreateColumn_{Type}_SetsTypeSpecificProperties` | ✅ |
| AC-05 | `CreateOneToManyAsync` creates a 1:N relationship with lookup column and cascade configuration | `SchemaValidatorTests.ValidateCreateOneToManyRequest_ValidRequest_DoesNotThrow` | ✅ |
| AC-06 | `CreateManyToManyAsync` creates an N:N relationship with intersect entity. `IntersectEntitySchemaName` is set on the SDK `CreateManyToManyRequest` (not the metadata) and defaults to `SchemaName` when caller omits it. Issue #1008. | `MetadataAuthoringServiceTests.CreateManyToManyAsync_OmittedIntersect_DefaultsToSchemaNameOnSdkRequest`, `MetadataAuthoringServiceTests.CreateManyToManyAsync_ExplicitIntersect_PassesThroughOnSdkRequest`, `SchemaValidatorTests.ValidateCreateManyToManyRequest_ResolvedIntersectMissing_ThrowsMissingRequiredField`, `MetadataRelationshipCreateE2ETests.CreateManyToMany_OmittedIntersect_DefaultsFromName_Succeeds` | ✅ |
| AC-07 | `CreateGlobalChoiceAsync` creates a global option set with initial values | `CacheInvalidationTests.CreateGlobalChoice_InvalidatesGlobalOptionSets` | ✅ |
| AC-08 | `AddOptionValueAsync` adds a value to an existing global or local option set (local path is now load-bearing for #1161 — AC-55) | `MetadataAuthoringServiceTests.AddOptionValueAsync_GlobalAndLocal_InsertsViaInsertOptionValue` | ❌ |
| AC-09 | `CreateKeyAsync` creates an alternate key with specified attributes | `SchemaValidatorTests.ValidateCreateKeyRequest_OneAttribute_DoesNotThrow` | ✅ |
| AC-10 | `ReactivateKeyAsync` retries a failed key index creation | — | ❌ |
| AC-11 | `DeleteTableAsync` with `DryRun=true` reads target identity and deletion dependencies without deleting | `MetadataDeleteDependencyTests.DryRun_ResolvesUnpublishedIdentityAndRequestsDependencies` | ✅ |
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
| AC-37 | `ppds metadata entity create/update/delete` exist as canonical subcommands of `entity` with the same flags as the former `table` commands | `MetadataEntityCommandTests.Entity_HasCreateUpdateDeleteSubcommands` | ❌ |
| AC-38 | Bare `ppds metadata entity <name>` still performs the read lookup (positional arg) when no verb subcommand matches | `MetadataEntityCommandTests.Entity_BarePositional_RoutesToQuery` | ❌ |
| AC-39 | `ppds metadata attribute create/update/delete` exist as canonical subcommands (former `column` commands) | `MetadataAttributeCommandTests.Attribute_HasCreateUpdateDeleteSubcommands` | ❌ |
| AC-40 | `ppds metadata optionset create/update/delete/add-option/update-option/remove-option/reorder` exist as canonical subcommands (former global `choice` commands); bare `optionset <name>` still queries | `MetadataOptionSetCommandTests.OptionSet_HasWriteSubcommandsAndQuery` | ❌ |
| AC-41 | Deprecated `table`/`column`/`choice` subcommands still execute and write a deprecation warning to **stderr** naming the exact canonical replacement command | `MetadataDeprecationTests.DeprecatedNoun_WritesCanonicalReplacementToStderr` | ❌ |
| AC-42 | Deprecation warnings never appear on stdout, including in `--json` mode (stdout stays valid JSON) | `MetadataDeprecationTests.DeprecatedNoun_JsonMode_StdoutHasNoWarning` | ❌ |
| AC-43 | Deprecated and canonical commands delegate to one shared execute path (no logic duplication; A1/A2) | `MetadataDeprecationTests.DeprecatedAndCanonical_ShareExecutePath` | ❌ |
| AC-44 | All internal consumers (skills, scripts, tests, docs) reference canonical nouns; no non-deprecation reference to `metadata table/column/choice` remains in-repo | `tests/test_metadata_canonical_nouns.py` | ❌ |
| AC-45 | `AddStatusReasonAsync` inserts a `statuscode` value via `InsertStatusValueRequest` with the given `StateCode`, returning the assigned value | `MetadataStatusReasonServiceTests.AddStatusReason_InsertsViaInsertStatusValue` | ❌ |
| AC-46 | `add-statusreason` wires the value choice through `OptionValueDeriver` (explicit `--value` used as-is; `--solution`-only derives; neither → `MISSING_REQUIRED_FIELD`) — gap-fill/advance-past-collision semantics are the unit-level responsibility of AC-57 | `MetadataStatusReasonServiceTests.AddStatusReason_DelegatesToDeriver` | ❌ |
| AC-47 | `add-statusreason` with an explicit `--value` already present on `statuscode` fails `DUPLICATE_OPTION_VALUE` | `MetadataStatusReasonServiceTests.AddStatusReason_ExplicitCollision_Throws` | ❌ |
| AC-48 | `list-statusreasons` returns each `statuscode` value with label, value, state code, state label, and color | `MetadataStatusReasonServiceTests.ListStatusReasons_ProjectsAllFields` | ❌ |
| AC-49 | `update-statusreason` / `remove-statusreason` target by `--value` or `--label` and fail `MISSING_REQUIRED_FIELD` when neither given, `OPTION_NOT_FOUND` when unresolved | `MetadataStatusReasonServiceTests.UpdateRemoveStatusReason_Targeting` | ❌ |
| AC-50 | `add-statusreason` requires exactly one of `--state` / `--state-code`; `--state` maps Active→0, Inactive→1 | `MetadataStatusReasonCommandTests.AddStatusReason_StateFlags` | ❌ |
| AC-51 | Creating a `Choice` column with inline `--option`(s) sets `OptionSetMetadata.IsGlobal = false` (and `OptionSetType.Picklist`) and succeeds (no "IsGlobal is not specified" fault) | `CreateColumnTypeTests.Choice_WithLocalOptions_SetsIsGlobalFalse` | ✅ |
| AC-52 | Creating a `Choices` (multi-select) column with inline options sets `IsGlobal = false` and succeeds | `CreateColumnTypeTests.Choices_WithLocalOptions_SetsIsGlobalFalse` | ✅ |
| AC-53 | `attribute create --type Choice --choice <global>` attaches the column to an existing global option set; `--choice` with `--option/--options/--options-file` is rejected `INVALID_CONSTRAINT` | `MetadataAttributeOptionParseTests.Create_ChoiceWithLocalOptions_ReturnsValidationError` | ✅ |
| AC-54 | `--option "Label[:Value][:#Color]"` (repeatable) and `--options-file <json>` parse into local option definitions (incl. color); legacy `--options` CSV still accepted | `MetadataAttributeOptionParseTests.ParseOptionSpecs_*`, `CreateColumnTypeTests.Choice_WithLocalOptionColor_AppliesColor` | ✅ |
| AC-55 | `attribute add-option` derives the local option value via the same `OptionValueDeriver` (explicit `--value` wins; `--solution` derives; neither → `MISSING_REQUIRED_FIELD`); inserts scoped to entity+attribute | `MetadataLocalOptionServiceTests.AddColumnOption_ExplicitValue_InsertsScopedToColumn`, `AddColumnOption_NeitherValueNorSolution_Throws` | ✅ |
| AC-56 | `attribute update-option` / `remove-option` target a local option by `--value` or `--label` (→ `OPTION_NOT_FOUND` when unresolved), scoped to the column's local set | `MetadataLocalOptionServiceTests.UpdateColumnOption_ByValue_UpdatesScoped`, `RemoveColumnOption_ByLabel_ResolvesAndDeletes`, `RemoveColumnOption_ValueNotFound_ThrowsOptionNotFound` | ✅ |
| AC-57 | `OptionValueDeriver.Derive` is a single shared helper used by both status-reason add and local-option add; unit tests cover explicit-wins, prefix derivation, gap-fill, collision, and missing-input cases | `OptionValueDeriverTests` | ✅ |
| AC-58 | Authoring verbs that change live metadata honor `--publish`, publishing the affected entity after the change (wired on `entity create`/`update`, `attribute create`/`update`/`add-/update-/remove-option`, and status-reason verbs via `PublishEntityInternalAsync`; #1171 added the entity create/update and attribute update surfaces) | `MetadataAuthoringServiceTests.CreateTableAsync_WithPublish_PublishesEntity`, `UpdateTableAsync_WithPublish_PublishesEntity`, `UpdateColumnAsync_WithPublish_PublishesEntity` | ✅ |
| AC-59 | `ppds metadata --help` lists the canonical nouns and marks deprecated nouns (`table`, `column`, `choice`, `choices`) `(deprecated)` in their one-line description | `MetadataDeprecationTests.MetadataHelp_MarksDeprecatedNouns` | ❌ |
| AC-60 | `entity --help` lists the status-reason subcommands; `attribute create --help` documents `--option`/`--choice`/derivation; each new subcommand exposes accurate `--help` | `MetadataHelpCoverageTests.NewSubcommands_HaveHelp` | ❌ |
| AC-61 | All six `entity` authoring verbs (`update`, `delete`, `add-statusreason`, `list-statusreasons`, `update-statusreason`, `remove-statusreason`) accept a positional `<entity>`; `--entity` still works (#1208) | `MetadataEntityCommandTests.AuthoringVerb_ParsesPositionalEntity` + `AuthoringVerb_StillAcceptsEntityFlag` | ✅ |
| AC-62 | When both the positional `<entity>` and `--entity` are supplied they must agree (case-insensitive) or the parse fails with a "disagree" error; supplying neither is a parse error (#1208) | `MetadataEntityCommandTests.AuthoringVerb_PositionalAndFlagAgreeing_HasNoErrors` + `AuthoringVerb_PositionalAndFlagDisagreeing_HasErrors` + `AuthoringVerb_MissingEntity_HasErrors` | ✅ |
| AC-63 | `optionset remove-option` targets a global option by `--value` or `--label` (exactly one; service-side resolution against unpublished metadata; `OPTION_NOT_FOUND` when unresolved) | `MetadataGlobalOptionServiceTests.DeleteOptionValue_ByLabel_ResolvesAndDeletes`, `DeleteOptionValue_ValueNotFound_ThrowsOptionNotFound`, `DeleteOptionValue_Resolution_RetrievesAsIfPublished` | ✅ |
| AC-64 | `optionset update-option` matches the `attribute update-option` shape: (`--value` \| `--label`) selector, `--new-label` for the new label, optional `--color`; the current label is preserved on color-only updates | `MetadataGlobalOptionServiceTests.UpdateOptionValue_ByLabel_AppliesNewLabel`, `UpdateOptionValue_ColorOnly_PreservesCurrentLabelAndForwardsColor` | ✅ |
| AC-65 | `optionset add-option`/`update-option`/`remove-option` honor `--dry-run` with a service-layer early exit; update/remove still validate the target exists, and `remove-option --dry-run` skips the confirmation prompt | `MetadataGlobalOptionServiceTests.AddOptionValue_DryRun_DoesNotCallSdk`, `UpdateOptionValue_DryRun_ValidatesTargetWithoutMutating`, `DeleteOptionValue_DryRun_ValidatesTargetWithoutMutating`, `DeleteOptionValue_DryRun_TargetNotFound_StillThrows` | ✅ |

### Delete preview scope (#1532)

Schema-delete previews resolve unpublished named metadata and call `RetrieveDependenciesForDelete` for component types 1 (table), 2 (column), 10 (relationship), 14 (key) and 9 (global choice). Every returned dependency edge is retained. Names are best effort; unknown numeric types, unavailable names and GUIDs remain visible blockers. The modern control-configuration code is used only after its environment metadata confirms the mapping. Partial/malformed identity or dependency results fail closed. No preview performs a mutation or invalidates caches.

The existing version-1 JSON envelope and `data.preview` nesting remain. The schema preview now reports `validationScope: delete-dependencies`, target/dependency check flags, status (`no-blocking-dependencies`, `blocked`, `incomplete`), complete returned rows, counts and safe name-lookup issues. Blocked output has success=false with data and a structured error, exit 10; incomplete output is nonzero (generic incomplete exit 2). A clear observation exits 0 while server permissions, customizability, feasibility and concurrent changes remain unchecked. Existing `--force` skips confirmation only; the actual service delete rechecks rather than trusting an earlier preview. Existing Task signatures and constructor remain; optional preview capability absence is an error, never a checked-success fallback.

Other dry-run routes preserve existing data fields and add `metadata.preview` for completed input/publisher checks, resolved option existence, requested-only SDK allocation or provisional status/local option values. Existing global/local removal `data.preview` remains. No derived or placeholder value is described as persisted. Status update/removal and legacy choice add/update/removal still expose no dry-run flag. Their existing behavior is preserved. Canonical JSON diagnostics stay on stdout as data; text diagnostics and alias deprecation notices stay on stderr.

Non-delete `--dry-run` help, including deprecated aliases, names the checks applicable to that route and explicitly leaves dependencies and server feasibility unchecked. Global option-add help states that supplied values are requested and Dataverse assigns the final value only when changes are applied; it does not imply publisher or target metadata reads. Status/local option-add help describes derived values as provisional. These help descriptions do not change execution or add metadata reads.

| ID | Criterion | Test | Status |
|----|-----------|------|--------|
| AC-66 | All five canonical schema-delete commands and three deprecated aliases report observed dependencies, nonzero blocked/incomplete status and unchecked server feasibility in help/text/JSON | `MetadataDeletePreviewTests.SchemaDelete_ReportsObservedDependencyScopeWithoutWrites`, `PreviewHelp_StatesLimitations` | ✅ |
| AC-67 | Global/local option-removal previews report resolved target existence, but never claim removability or checked dependencies; additive JSON scope and help agree; SDK calls remain read-only | `MetadataDeletePreviewTests.OptionRemoval_ReportsExistenceOnlyAndNeverWrites`, `PreviewHelp_StatesLimitations` | ✅ |
| AC-68 | Real validation failures (including missing option) remain errors with existing exit/envelope contracts and never emit a successful preview; no preview writes | `MetadataDeletePreviewTests.MissingOption_RemainsValidationFailure`, `SchemaDelete_MissingInput_RemainsValidationFailure` | ✅ |
| AC-69 | Real forced-delete payloads/result fields and confirmation remain, with fresh inspection before the acknowledged mutation; canonical/deprecated paths share the execute implementation | `MetadataDeletePreviewTests.SchemaDelete_RealFlowRetainsDeleteResult`, `SchemaDelete_WithoutForceStillRequiresConfirmation` | ✅ |


### Dependency discovery and truthful preview acceptance (#1532)

A ✅ in these tables records the named offline service/parser/SDK-mock contract tests. It does not claim server-side deletion feasibility, actual platform persistence, every-system-column editability, full #1015/#1215 surface parity, or final release readiness. Existing protected-target/session boundaries remain required.

The common 101-case mock baseline has 10 passing safety/input controls and 91 added-contract assertion failures. The compile-corrected raw-read baseline has 12 passing controls and 8 classification failures; the two earlier compile failures are not test evidence. The original 277-case fixed candidate passed. Full normal validation then found two existing combined-workflow cases with stale preview fixtures; the bounded fixture correction is retained here. The 277-case focused suite passed, and both corrected combined-workflow regressions passed in the full ordinary unit-test matrix. The listed dependency and wider-scope contracts also passed with the composed Choice and label implementations. This is offline service/command evidence, not live platform acceptance or final merged-source/hosted/security clearance. The composed #1530 initial-create paths report derived omitted values as provisional and never claim persisted values or target-option reads.

| ID | Criterion | Test | Status |
|----|-----------|------|--------|
| AC-114 | All five families resolve exact named unpublished identity, parent ownership, nonempty GUID and dependency type; both relationship shapes are supported | `MetadataDeleteDependencyTests.DryRun_ResolvesUnpublishedIdentityAndRequestsDependencies`, `MetadataDeleteDependencyDetailTests.ConflictingReturnedParent_FailsBeforeDependencyRead` | ✅ |
| AC-115 | Preview reads dependencies without writes and blocks on known/unknown components | `MetadataDeleteDependencyTests.Blockers_RejectWithoutDiscardingUnknownTypeOrObjectId`, `MetadataDeleteDependencyCommandTests.AllAliases_BlockedDryRunShowsComponentsAndNonzero` | ✅ |
| AC-116 | Keep every edge, exact type/GUID, visible duplicate counts and unresolved names; use typed metadata ID or minimal record-name queries, verifying modern mappings | `MetadataDeleteDependencyDetailTests.KnownComponentNames_UseExactTypedIdOrMinimalNameQuery`, `UnknownOrUnverifiedModernType_KeepsIdentityAndNeverQueriesGuessedTable`, `DuplicateDependencyEdges_AreRetainedButNamesReadOncePerIdentity`, `IncompleteNameResult_NeverClearsTheKnownBlocker` | ✅ |
| AC-117 | Invalid identity, missing/partial/malformed dependency results fail closed; never convert partial data into empty success | `MetadataDeleteDependencyTests.InvalidIdentity_FailsBeforeDependencyRead`, `IncompleteDependencies_CannotBecomeEmptySuccess`, `MetadataDeleteDependencyDetailTests.PartialCollection_RetainsKnownEdgesAndRemainsIncomplete` | ✅ |
| AC-118 | Exact cancellation/cause/token and coded classification survive each read; one SDK operation per returned lease; optional name failures never expose provider text | `MetadataDeleteDependencyTests.NewPreflightStageCancellation_PreservesTokenIdentityAndStops`, `DependencySdkFault_KeepsCauseAndFixedMessage`, `MetadataDeleteDependencyDetailTests.OptionalNameCancellation_PropagatesExactExceptionAndReturnsEveryLease`, `OptionalNameAuthFailure_PreservesClassificationAndCauseWithoutProviderText` | ✅ |
| AC-119 | Actual service deletion rechecks after preview; force cannot bypass dependencies; guards/required inputs precede checkout and cache invalidation follows only mutation | `MetadataDeleteDependencyTests.ActualDelete_ReadsBeforeExactlyOneOriginalDelete`, `PreviewThenActual_RechecksAndRefusesNewBlockers`, `ExistingInputAndSafetyRefusals_PrecedeAnyCheckout`, `MetadataDeleteDependencyCommandTests.Force_DoesNotBypassDependencyRefusal` | ✅ |
| AC-120 | All eight canonical/legacy routes retain version-1 data/preview while text/JSON reports blocked/incomplete nonzero and explicit unchecked feasibility | `MetadataDeleteDependencyCommandTests.AllAliases_BlockedDryRunShowsComponentsAndNonzero`, `AllAliases_EmptyCompleteObservationDoesNotClaimFeasibility`, `AllAliases_IncompleteDependencyResultIsNeverSuccess` | ✅ |
| AC-121 | Old five Task signatures/constructor and public command factories remain; missing optional preview capability fails closed | `MetadataDeleteDependencyTests.ExistingTaskDeleteContracts_RetainOptionalPreviewCapability`, `MetadataDryRunScopeCommandTests.ServiceWithoutOptionalCapability_FailsClosedBeforeAnyDeleteCall` | ✅ |
| AC-122 | All 17 other explicit service branches perform only documented reads; 23 canonical/alias dry-run handler cases across 22 command paths report real scope in help/text/JSON, preserve existing data and exact linked token, and existing no-dry-run wrappers retain their flags | `MetadataOtherDryRunContractTests.EveryBranch_OnlyPerformsItsDocumentedReads_NoCacheOrWrite`, `ReadCancellation_PreservesExactCauseTokenAndReleasesLease`, `MetadataDryRunScopeCommandTests.RealHandlers_ReportOnlyCompletedChecksAndPreserveData`, `DryRunHelp_StatesCompletedChecksAndUncheckedConstraints`, `ExistingWrappersWithoutDryRun_KeepFlagsAndForwardActualToken` | ✅ |

### Draft-aware option allocation (#1531)

Status-reason and local choice-option allocation must include unpublished metadata when collecting existing values, including explicit-value collision checks and dry-run allocation. Public status/local-option lists remain published reads. Update/remove target resolution retains its current published-read behavior. The private local color readback (after the insert/update that carried the colour, see "Option colours") includes unpublished metadata so a just-inserted option is found; nothing re-sends the option collection, so other draft options are untouched. This helper is shared by local/status add and update and the existing local-target option-value path. Global option-set helpers and public query APIs are unchanged.

The add-statusreason service wrapper emits fixed action/target text, retains the original SDK cause and existing code/CLI exit, and the CLI places that user message in the error envelope. SDK diagnostic text is omitted from that user message. Allocation reads propagate their cancellation token and cancellation exceptions. The local colour read-back runs after the committed insert, so its cancellation is reported as `WRITE_NOT_CONFIRMED` carrying the cancellation (AC-97, AC-146). Publication remains explicit.

| ID | Criterion | Test | Status |
|----|-----------|------|--------|
| AC-70 | Status-reason and local Choice/Choices allocation reads include draft values, derive past draft collisions, preserve explicit values, reject explicit draft collisions, and keep dry-run free of mutation | `MetadataDraftOptionReadTests.Allocation_UsesDraftValues`, `ExplicitDraftCollision_RejectsBeforeInsert`, `ExplicitUnusedValue_IsPreserved` | ✅ |
| AC-71 | Local and status color readback sees newly inserted draft options; the colour rides on the insert/update for that option alone (no option collection or attribute is re-sent), so other draft options and labels are untouched. The shared update and local option-value callers use that same readback. | `MetadataDraftOptionReadTests.AddWithColor_ReadsDraftAndPreservesOtherOptions`, `UpdateColor_PreservesDraftOptions`, `GenericLocalOptionAdd_ColorReadsDraft` | ✅ |
| AC-72 | Public status/local-option lists remain published reads, without changing public APIs or environment routing. Update/remove target resolution reads as-if-published so an option added moments earlier can be updated or removed before publishing (#1531); without a new label no `Label` is sent (AC-142), and an update must request something (AC-146); a value missing from drafts is still `OPTION_NOT_FOUND` | `MetadataDraftOptionReadTests.PublicList_RemainsPublished`, `UpdateAndRemove_TargetResolutionReadsDrafts`, `AddThenUpdateOrRemove_WithoutPublish_TargetsTheNewOption`, `UpdateAndRemove_MissingFromDrafts_StillOptionNotFound` | ✅ |
| AC-73 | Draft allocation reads propagate the token and preserve cancellation exception identity; a cancelled local colour read-back after the insert is `WRITE_NOT_CONFIRMED` with the cancellation as its cause (AC-97). The CLI error mapper and JSON writer retain the fixed add-statusreason action/target message, code, original cause and exit classification; SDK diagnostic text is excluded from that user message. | `MetadataDraftOptionReadTests.AllocationCancellation_Propagates`, `ColorReadCancellation_AfterTheInsert_IsWriteNotConfirmed`, `AddStatusSdkFailure_UsesFixedMessageInCliEnvelope` | ✅ |

### Primary ID column labels (#1534)

A real column update can change the display name, description, or both on a table's primary ID. Some tables have multiple GUID columns with `IsPrimaryId` set, including address IDs. When an existing column has runtime type exactly `AttributeMetadata`, `AttributeType == Uniqueidentifier`, and `IsPrimaryId == true`, validate the requested changes and retrieve just the table definition (`EntityFilters.Entity`). Its `PrimaryIdAttribute` must match both the requested column and the retrieved attribute logical name, ignoring case. The SDK enforces the table's managed customization rules.

Build a fresh base attribute with the retrieved logical and schema names and only the requested labels, in the organization's base language (AC-142). Set `UpdateAttributeRequest.MergeLabels` to true for this path so Dataverse retains translations in other languages. Required level, length, range, precision, format, audit, security, Advanced Find and auto-number settings are incompatible even when explicitly set to false, zero or an empty string. A dry run continues to validate the required input identifiers before any metadata retrieval.

| ID | Criterion | Test | Status |
|----|-----------|------|--------|
| AC-74 | Display name, description, or both on the verified primary ID generate one targeted update with a fresh minimal base attribute and merged labels. Other language labels survive through the SDK's merge contract. Attribute retrieval, table-key verification and update each return their own client after one SDK request. Cache invalidation and optional publish follow the successful update after its lease is returned. | `PrimaryIdLabelUpdateTests.VerifiedPrimaryId_SendsOnlyRequestedLabels`, `PrimaryIdLabelUpdateTests.SingleSlot_UpdatesAndPublishesInOrder` | ✅ |
| AC-75 | Reject unrelated base attributes, unconfirmed finite-label metadata, misleading address IDs, and missing or inconsistent primary ID metadata before any update or publish. Only an eligible primary-ID candidate triggers the table lookup. State/status and nonprimary GUID metadata require the separate finite identity contract below. | `PrimaryIdLabelUpdateTests.UnsupportedShape_RemainsRejected`, `PrimaryIdLabelUpdateTests.UnconfirmedPrimaryId_RejectsBeforeWrite` | ✅ |
| AC-76 | Require at least one supplied label. Reject every supplied incompatible field on an otherwise eligible primary ID with a clear validation error before the table lookup or any write. | `PrimaryIdLabelUpdateTests.IncompatibleSettings_RejectBeforeTableLookup`, `PrimaryIdLabelUpdateTests.NoLabels_RejectsBeforeTableLookup` | ✅ |
| AC-77 | Preserve input-only dry-run, typed updates, mutation guard, SDK and cancellation routing. Both CLI command names accept both label flags. Table lookup failures and cancellation prevent update and publish. A failed operation releases its lease. A failed pool checkout propagates its original exception and skips subsequent work. Typed updates support publication with one pool slot. | `PrimaryIdLabelUpdateTests.DryRun_ValidatesInputsWithoutMetadata`, `PrimaryIdLabelUpdateTests.TypedUpdate_PreservesRequestShape`, `MetadataAuthoringServiceGuardTests.EveryMutationMethod_Blocks` (`UpdateColumnAsync`), `PrimaryIdLabelUpdateTests.UpdateFailure_SkipsPublishAndCacheInvalidation`, `PrimaryIdLabelUpdateTests.TableLookupFailure_StopsBeforeWrite`, `PrimaryIdLabelUpdateTests.CliUpdate_AcceptsBothLabelFlags`, `PrimaryIdLabelUpdateTests.SingleSlot_UpdatesAndPublishesInOrder`, `PrimaryIdLabelUpdateTests.SingleSlot_FailedOperationReturnsClient`, `PrimaryIdLabelUpdateTests.SingleSlot_CheckoutFailureLeavesSlotFree` | ✅ |

### Finite label-only column updates (#1534 follow-up)

The additional label-only shapes are exact `StateAttributeMetadata`, exact `StatusAttributeMetadata`, exact `UniqueIdentifierAttributeMetadata` with known primary/nonprimary identity, and exact base `AttributeMetadata`/Uniqueidentifier with `IsPrimaryId == false`. These paths require matching canonical table and column logical names, a valid matching schema name, consistent SDK type code/name and a nonempty retrieved `MetadataId`. A concrete primary GUID also requires the table definition's logical name and `PrimaryIdAttribute` to confirm the same target. Unknown GUID primary identity and other unreviewed/internal/collection shapes stay unsupported; there is no catch-all metadata update.

The fresh payload uses the exact retrieved SDK type and only canonical logical/schema names, that exact MetadataId and the requested base-language display name/description. `MergeLabels = true` preserves other translations. State/status column labels are distinct from their option labels: options, defaults, existing translations and unrelated managed settings are never copied. Empty description intentionally clears that label. At least one label is required, and all ten unrelated request settings are refused by presence, including false, zero and empty-string values. The original fourteen typed paths and exact-base primary-ID proof/payload remain separate and unchanged.

Known `IsCustomizable == false` denies either label update; known `IsRenameable == false` denies a requested display name while permitting description-only edits unless customization is denied. Unknown managed editability is left to the SDK. Neither `IsManaged` nor record-data `IsValidForUpdate` establishes metadata editability. These finite checks do not claim live server acceptance for every platform column.

SDK reads and mutation use one operation per returned lease. Cancellation stops later work; coded failures and cancellation preserve identity, while unexpected SDK diagnostics remain in the original cause behind a fixed public message. No mutation is retried. Cache invalidation follows an acknowledged update after its lease returns, including cancellation delivered with that acknowledgement; cancellation then prevents publication and does not imply rollback. Optional publication uses a fresh lease.

| ID | Criterion | Test | Status |
|----|-----------|------|--------|
| AC-123 | Five finite shapes produce exact fresh requested-label payloads with canonical identity, exact nonempty MetadataId, base-language (LCID from `organization.languagecode`; 1033 in these cases) and merged labels; rich retrieved metadata stays unchanged | `ColumnLabelOnlyTransitionTests.NewShapes_SendExactFreshRequestedLabels` (20 cases) | ✅ |
| AC-124 | Require at least one label; allow description clearing; refuse every supplied unrelated setting by presence | `NewShapes_RequireAtLeastOneLabel`, `NewShapes_RejectEverySuppliedNonlabelSetting` (55 cases) | ✅ |
| AC-125 | Reject uncertain target/type/MetadataId identity and unreadable responses; concrete primary GUID requires authoritative table identity before writing | `NewShapes_RejectUncertainTargetIdentity`, `ConcreteGuidPrimary_RequiresAuthoritativeTableIdentity`, `UnreadableAttributeResponse_IsCodedAndCannotWrite` (56 cases) | ✅ |
| AC-126 | Apply known customization/rename denials and server-authoritative unknown editability without using managed/record-update flags as permission proxies | `NewShapes_ApplyKnownDenialAndUnknownPolicy` (30 cases) | ✅ |
| AC-127 | Preserve cancellation/coded causes, fixed public SDK errors, returned leases and acknowledged-update cache/publication boundaries | `NewShapes_PropagateCancellationAndReleaseLeases`, `PrimaryGuid_TableCancellationStopsBeforeUpdate`, `UnexpectedBoundaryFault_IsPrivateAndStops`, `CodedFaults_ArePreserved`, `CheckoutFaults_ArePreservedWithoutLeakingSlots`, `UpdateAck_CancellationStopsPublishAndInvalidatesKnownAppliedCache`, `ColumnLabelFailureClassificationTests.RawSdkFailures_RetainClassificationAndPrivateOutput` (46 cases) | ✅ |
| AC-128 | Preserve all fourteen typed fresh payloads and existing exact-base primary-ID proof/payload omission of MetadataId; amended 2026-11-17 (AC-147): typed updates now set `MergeLabels = true` | `ExistingFourteenTypes_KeepFreshPayloadAndMergeContract`, `ExistingBasePrimary_PreservesAuthoritativeProofAndPayload` (18 cases) | ✅ |
| AC-129 | Retain input-only dry-run, mutation guard and explicit unsupported-shape refusals | `GuardAndDryRun_DoNotReadMetadata`, `UnreviewedShapes_StayUnsupported` (10 cases) | ✅ |
| AC-130 | Preserve existing constructor and seventeen-property public request; the service method returns `Task<MetadataLabelWriteResult>` since AC-147 | `PublicRequestAndServiceContracts_RemainCompatible` (1 case) | ✅ |

The same byte-identical 224-case suite compiled and executed against unchanged B3: 32 positive controls passed, 192 expected assertions failed and no case was skipped. A separate offline readback corrected two exact custom-message assertion classifications while retaining the original raw exit1 and invalid wrapper result. The fixed 224-case transition suite and the additional 12 raw-auth/timeout cases passed: 236 strict SDK-mock cases in total, with no skipped case. The full ordinary unit matrix also passed with the composed dependency and Choice paths. These tests establish client identity, payload, editability, lease and error contracts; they do not establish server acceptance for every system column or a live Dataverse write.

### Global choice names on column creation (#1535)

When creating a Choice or Choices column that references a global choice, resolve its stored name from the target environment after input validation and dry run. An exact ordinal match takes precedence. Otherwise one ordinal case-insensitive match supplies the canonical name. The column carries a minimal global reference using that name. Public metadata queries continue to return the published view. The authoring lookup includes unpublished choices and incurs one extra catalog read per global attachment.

| ID | Criterion | Test | Status |
|----|-----------|------|--------|
| AC-78 | Single- and multi-select global references use the stored metadata name verbatim. A unique exact name wins over other case variants. The canonical and deprecated command handlers pass the supplied spelling into the service DTO, and the service preserves the caller DTO. | `GlobalChoiceReferenceTests.Attach_UsesCanonicalName`, `ExactName_WinsOverCaseVariant`, `CliActions_PreserveSpellingAndAttachStoredName` | ✅ |
| AC-79 | An absent or ambiguous global name or incompatible definition produces a validation error before column creation. Duplicate exact matches are ambiguous. | `GlobalChoiceReferenceTests.UnresolvableName_StopsBeforeCreate`, `IncompatibleChoice_StopsBeforeCreate` | ✅ |
| AC-80 | The lookup uses fresh unpublished metadata from the caller's pool, releases its own lease before matching or obtaining a create lease, and skips create, publish and cache invalidation on failure. Raw transport and pool configuration or argument failures retain their mapped codes, exits and original causes in coded service exceptions, including the Dataverse reauthentication flag when present. Cancellation and already-coded errors retain their identities. | `GlobalChoiceReferenceTests.ConsecutiveCreates_ReadCurrentCatalog`, `LookupFailure_StopsBeforeCreate`, `InvalidCatalog_StopsBeforeCreate`, `CatalogLease_IsReturnedBeforeCreateCheckout`, `CatalogCheckoutFailure_IsCodedAndSkipsCreate` | ✅ |
| AC-81 | Existing local, non-choice, validation, dry-run and safety-guard paths perform their established operations without a global-choice lookup. The create lease is returned before cache invalidation and any requested publication. | `GlobalChoiceReferenceTests.UnrelatedPath_SkipsCatalog`, `DryRunAndInputErrors_SkipCatalog`, `Guard_PrecedesAllLookups`, `Attach_UsesCanonicalName`, `Publish_ObtainsLeaseAfterCreateLeaseIsReturned` | ✅ |

### Connection-lifetime acceptance criteria

| ID | Criterion | Test | Status |
|----|-----------|------|--------|
| AC-82 | Generic option add/update with color, status update with color/publication and status removal with publication complete with one available connection; each mutation lease returns before another checkout | `MetadataLeaseLifetimeTests.SingleSlot_WorkflowsReturnEachLeaseBeforeNextOperation` | ✅ |
| AC-83 | Table creation returns its lease before cache invalidation/publication, preserves result identity, skips later work after failed create and never replays create after publication failure | `MetadataLeaseLifetimeTests.SingleSlot_WorkflowsReturnEachLeaseBeforeNextOperation`, `FailureAtEachStep_ReturnsLeasesAndStopsLaterWork` | ✅ |
| AC-84 | Each global/local colour read-back uses one SDK operation per short lease after the write that carried the colour; the cache is invalidated before it; a missing target is `WRITE_NOT_CONFIRMED` (cause `OPTION_NOT_FOUND`) and publication is not attempted; a failed read checkout returns all acquired leases | `MetadataLeaseLifetimeTests.SingleSlot_WorkflowsReturnEachLeaseBeforeNextOperation`, `FailureAtEachStep_ReturnsLeasesAndStopsLaterWork`, `ColorRead_MissingTarget_ReportsWriteNotConfirmed_AndSkipsPublication` | ✅ |
| AC-87 | Covered SDK faults retain their stage-specific wrapping; a colour read-back failure of any kind after the committed write is `WRITE_NOT_CONFIRMED` (AC-146); selected acquisitions follow AC-98 through AC-101, preserving CLI exits and original causes, with no retry or later work | `MetadataLeaseLifetimeTests.ColorReadBackFailure_AfterTheWrite_IsWriteNotConfirmed`, `CheckoutFailures_PreserveCodedCauseAndCliClassification`, `FailureAtEachStep_ReturnsLeasesAndStopsLaterWork` | ✅ |
| AC-88 | Payloads, read modes, target/value/label/solution/token stay unchanged, except as "Option colours" (AC-146) and AC-142 amend them (colour on the insert/update; no unrequested label); guards, validation and actual dry-run read behavior remain intact | `MetadataLeaseLifetimeTests.Payloads_ReadModesAndResultsRemainStable`, `PrefixLookup_UsesSeparateLeasesAndCachesAcrossDryRuns`, `MetadataAuthoringServiceGuardTests.EveryMutationMethod_Blocks`, `MetadataGlobalOptionServiceTests.AddOptionValue_DryRun_DoesNotCallSdk`, `UpdateOptionValue_DryRun_ValidatesTargetWithoutMutating` | ✅ |
| AC-89 | Already-correct status-add and local-option add/update/remove outer scopes continue to complete with one available connection | `MetadataLeaseLifetimeTests.SingleSlot_WorkflowsReturnEachLeaseBeforeNextOperation` | ✅ |
| AC-90 | The MCP option-add tool retains generic fallback for raw exceptions from unnormalized mocked services, coded SDK failures, raw cancellation identity, existing coded faults and one-time request forwarding; the real service acquisition route follows AC-100. No transport cancellation response is implied | `MetadataAddOptionValueLeaseContractTests.RawPoolExhaustion_RemainsInternalAtMcpBoundary`, `WrappedSdkCancellation_RemainsCodedAndFaulted`, `RawCancellation_PropagatesOriginalAndCanceled`, `AlreadyCodedFault_PreservesCodeAndContext`, `SuccessfulInvocation_ForwardsRequestOnce` | ✅ |
| AC-91 | Publisher-prefix lookup uses separate solution/publisher leases, retains cached/dry-run behavior and stops before create/publication after either checkout/query failure or cancellation | `MetadataLeaseLifetimeTests.PrefixLookup_UsesSeparateLeasesAndCachesAcrossDryRuns`, `FailureAtEachStep_ReturnsLeasesAndStopsLaterWork` | ✅ |

### Table-update connection lifetime

Table updates return their mutation connection before cache invalidation, optional publication and success reporting. Publication remains an explicit follow-up request; a later failure can leave an acknowledged update without publication. SDK/already-coded exception and cancellation contracts are preserved. Checkout failures follow the scoped acquisition correction below. There is no retry, rollback or atomicity guarantee, and broader D4/cancellation gaps outside that scope remain.

| ID | Criterion | Test | Status |
|----|-----------|------|--------|
| AC-93 | Updating a table with optional publication completes with one available connection; the label read (AC-147) and update leases each return before the next checkout and before cache/publication/success, and no-publish uses only the read and the update | `TableUpdateLeaseTests.SingleSlot_UpdateAndPublish_ReturnsMutationLeaseBeforeFollowup`, `UpdateWithoutPublish_ReturnsLeaseBeforeCacheAndSuccess`, `TwoSlotControl_CompletesSameUpdateAndPublication` | ✅ |
| AC-94 | Covered update/publication SDK failures and cancellation retain original identity or existing coded error; checkouts follow AC-101 while preserving task state and CLI classification. Leases return, caches follow the acknowledged update stage, no mutation is replayed and later publication stops | `TableUpdateLeaseTests.FailureAtEachBoundary_PreservesStageContractAndStopsLaterWork`, `CacheFailure_AfterReturnedMutationLease_PreservesCauseAndStopsPublication` | ✅ |
| AC-95 | Existing guard/required-input validation and input-only dry run, including Publish=true, perform no checkout, mutation, cache invalidation or publication | `TableUpdateLeaseTests.DryRunValidationAndGuard_DoNotAcquire` | ✅ |
| AC-96 | Table update retains request identity/solution/token, metadata labels, explicit false/empty/omitted fields/icons, cache order and existing success reporting | `TableUpdateLeaseTests.PayloadAndSuccessMessages_RemainUnchanged` | ✅ |

### Cancellation and connection-acquisition errors

The approved correction preserved cancellation at the colour catches; since AC-146 the colour helpers only read back after a committed write, and every failure there, cancellation included, is `WRITE_NOT_CONFIRMED` (AC-97). Connection-acquisition normalization is limited to table create/update, generic option add/update, status update/remove and publisher-prefix resolution; a colour read-back checkout failure is also normalized, then reported as `WRITE_NOT_CONFIRMED` with that coded failure as its cause, so it is not one of the selected boundaries of AC-98. `ResolvePublisherPrefixAsync` normalizes its two checkouts for its existing callers: `CreateTableAsync`, `CreateColumnAsync`, `CreateOneToManyAsync`, `CreateManyToManyAsync`, `CreateGlobalChoiceAsync` and `CreateKeyAsync`. The shared publication helper normalizes its own checkout, including publication requested by other metadata writers. Other direct column acquisitions, global-choice catalog reads and SDK operation contracts remain unchanged. There is no retry, rollback or atomicity promise. MCP tool-boundary checks do not establish wire-level cancellation responses.

OCE, existing coded PPDS errors and metadata validation errors keep their identities, except after a committed option write: there a colour read-back failure is `WRITE_NOT_CONFIRMED` (cause kept as the inner exception) and a publish failure is re-raised with its own code and exit and a message stating what was written (AC-146). Other acquisition failures become coded service errors with the original mapped CLI code/exit and identical inner cause. Authentication retains its existing reauthentication flag. An internal implementation type stores the prior mapped exit because a validation-prefixed file-not-found code would otherwise change the CLI exit. No public DTO, pool-setting or generic error policy changes.

| ID | Criterion | Test | Status |
|----|-----------|------|--------|
| AC-97 | Immediate and delayed colour read-back cancellation, which happens after the committed write, is reported as `WRITE_NOT_CONFIRMED` whose message says the write was applied and whose inner exception is the exact cancellation (with its token); later work stops and leases return (amended by AC-146: earlier the cancellation itself propagated) | `MetadataLeaseLifetimeTests.ErrorContract_ColorCancellation_IsWriteNotConfirmedCarryingTheCancellation`, `ErrorContract_ColorCancellation_DoesNotRequireCallerTokenCancellation` | ✅ |
| AC-98 | Every selected acquisition boundary returns a coded timeout with its identical cause and unchanged CLI exit4, stops later work, and preserves OCE/already-coded/metadata-validation identities; all acquired leases return | `MetadataLeaseLifetimeTests.ErrorContract_PoolBoundary_IsCodedAndStopsLaterWork`, `ErrorContract_AcquisitionIdentity_PropagatesWithoutWrapping` | ✅ |
| AC-99 | Other acquisition failures retain the exact mapper code/numeric exit and inner cause, including auth/reauthentication, configuration/arguments, not-found and ordinary errors; no arbitrary message is exposed as the new service user message | `MetadataLeaseLifetimeTests.ErrorContract_AcquisitionMapping_PreservesCodeExitAndCause` | ✅ |
| AC-100 | Actual MCP option-add invocation routed into the real service reports Connection.Timeout for pool exhaustion and reports a colour read-back cancellation after the committed insert as a structured `WRITE_NOT_CONFIRMED` at the tool boundary (AC-146); unrelated generic-helper controls remain | `MetadataAddOptionValueLeaseContractTests.ActualServicePoolTimeout_IsConnectionTimeoutAtMcpBoundary`, `ActualColorCancellation_AfterTheInsert_IsWriteNotConfirmedAtMcpBoundary` | ✅ |
| AC-101 | Table-update checkout normalization and the shared publication helper preserve prior CLI exits, acknowledged progress and cleanup; column direct/catalog acquisition classifications and ordinary SDK execution behavior retain their controls, subject to the bounded user-message changes below | `TableUpdateLeaseTests.ErrorContract_Acquisition_PreservesMappingCauseAndPartialProgress`, `MetadataLeaseLifetimeTests.ErrorContract_PoolBoundary_IsCodedAndStopsLaterWork`, `PrimaryIdLabelUpdateTests.SingleSlot_CheckoutFailureLeavesSlotFree`, `GlobalChoiceReferenceTests.CatalogCheckoutFailure_IsCodedAndSkipsCreate` | ✅ |
| AC-102 | At the covered connection-acquisition boundary, raw authentication/provider diagnostic details remain only in the original inner cause and never enter the structured MCP user message; auth code/CLI exit and reauthentication flag remain | `MetadataAcquisitionPrivacyTests.RawAuthDetail_StaysInCauseAndOutOfMcpPayload` | ✅ |

### Bounded SDK diagnostic messages

The following fourteen existing SDK/catalog error boundaries use service-owned action text and caller-owned target identifiers or labels. Provider/SDK diagnostic text is no longer appended to their user messages. Status-reason and local-column-option insertion additionally report a typed Dataverse numeric fault code, when found within the first eight exception objects, and state that server diagnostic text is withheld. They never copy fault messages, reasons, traces, error-detail values or inner-fault text. The closest typed fault supplies the code; nested fault details are not traversed or reclassified. A code is not a diagnosis of a duplicate option or proof that a mutation did not occur. Unknown/non-SDK exceptions retain the existing fixed message. Original inner exceptions, PPDS error codes, exception types, auth/reauthentication flags, CLI exit mappings, catch filters, cancellation behavior, request payloads and connection scopes remain unchanged. Existing partial-operation limits remain: an earlier mutation can succeed before a later failure.

| Method | Changed error boundary |
|--------|------------------------|
| `ResolveGlobalOptionAsync` | Option-set retrieval |
| `AddStatusReasonAsync` | Status-reason insertion |
| `ReadStatusReasonsAsync` | Status-attribute retrieval |
| `UpdateStatusReasonAsync` | Status-reason update |
| `RemoveStatusReasonAsync` | Status-reason removal |
| `ReadColumnOptionsAsync` | Local option-attribute retrieval |
| `AddColumnOptionAsync` | Local option insertion |
| `UpdateColumnOptionAsync` | Local option update |
| `RemoveColumnOptionAsync` | Local option removal |
| `ResolveGlobalChoiceNameAsync` | Catalog authentication and other mapped failures (two boundaries) |
| `ReadGlobalOptionColorAsync` | Colour read-back (formerly read and update) |
| `ReadLocalOptionColorAsync` | Colour read-back (formerly read and update) |
| `ValidatePrimaryIdLabelUpdateAsync` | Primary-ID table lookup |

The synthetic global colour read-back cases verify the real service -> MCP tool -> structured JSON path with fake ClientSecret= and Bearer sentinels. The initial-insert generic fallback, covered acquisition privacy, coded/raw cancellation and existing helper controls remain separate. This is a bounded message change, not repository-wide secret redaction, a log-privacy guarantee, a real-provider incident or a new MCP transport/RPC test. The other inventoried boundaries are included by exact source substitution; no new dynamic case for each of those boundaries is claimed.

| ID | Criterion | Test | Status |
|----|-----------|------|--------|
| AC-103 | Global colour read-back failures are `WRITE_NOT_CONFIRMED` wrapping the service's coded read failure (which keeps the original cause) while fake ClientSecret/Bearer details are absent from the actual MCP user message and emitted exception JSON (two cases; the separate colour write no longer exists, AC-146) | `MetadataSdkDiagnosticPrivacyProbeTests.ActualServiceColorFailure_UntrustedDetailMustNotReachMcp` | ✅ |
| AC-104 | Add-statusreason failures use the fixed action/target message in the CLI error envelope, omit the synthetic SDK diagnostic and retain SDK failure code, original cause, success=false and CLI exit2 | `MetadataDraftOptionReadTests.AddStatusSdkFailure_UsesFixedMessageInCliEnvelope` | ✅ |
| AC-105 | The covered synthetic SDK route preserves tokens, one request per returned lease, request order and original causes; initial raw SDK faults remain generic, covered auth details stay private and existing timeout/cancellation/helper controls remain | `MetadataSdkDiagnosticPrivacyProbeTests.InitialInsertRawFailure_UsesGenericMcpError`, `ActualColorWrapper_CliMapperRemovesKnownSecretKey`, `DirectMapperCharacterization_DoesNotClaimBearerRedaction`, `MetadataAcquisitionPrivacyTests.RawAuthDetail_StaysInCauseAndOutOfMcpPayload`, `MetadataAddOptionValueLeaseContractTests.ActualServicePoolTimeout_IsConnectionTimeoutAtMcpBoundary`, `ActualColorCancellation_AfterTheInsert_IsWriteNotConfirmedAtMcpBoundary`, `McpToolErrorHelperTests.ThrowStructuredError_GenericException_DoesNotLeakInnerMessage` | ✅ |
| AC-131 | Status-reason, Choice and Choices insertion failures expose only a typed numeric fault code alongside the fixed action/target message; retain the original cause, SDK failure code and exit2 across service, CLI JSON and MCP structured-helper output without any synthetic server secrets or traces | `MetadataFaultCodePrivacyTests.TypedFault_ExposesOnlyNumericCodeAcrossErrorBoundaries` | ✅ |
| AC-132 | Unknown exceptions and faults beyond the bounded exception walk retain the existing fixed message; the nearest typed fault supplies the code without descending into nested fault details | `MetadataFaultCodePrivacyTests.UnknownOrDeepFault_RetainsFixedMessage`, `ClosestTypedFault_DoesNotCopyInnerFault` | ✅ |
| AC-133 | Authoring paths that take a global choice name from the caller (choice update, delete preview, option add/update/remove/reorder, global color readback) send the exact spelling first. Only a Dataverse "not found" fault lists the as-if-published catalog. Recognition is designed to be language-independent: a typed fault whose `ErrorCode` is ObjectDoesNotExist (`0x80040217`) qualifies whatever its text. Asserted live (2026-11-17, `MetadataGlobalChoiceLiveTests.MisCasedName_ResolvesAndRecordsTheFaultCode`): Dataverse returns this code for a mis-cased global choice name on `RetrieveOptionSet` (published and as-if-published), `InsertOptionValue` and `OrderOption`; the lookup is case-sensitive; the service resolves the case variant on every path. The texts were `Could not find an optionset with name <name> and id 00000000-…` (retrieve) and `Could not find an option set with specified name: <name>` (insert, order), on an English organisation; the code on a non-English organisation is not observed. A fault with any other code qualifies only through the English fallback, kept because Microsoft does not document which code this message uses: the fault text contains the unnamed wording `Could not find an option set with specified name`, or an anchored phrase (`Could not find an optionset with name`, the recorded server wording, or `Could not find an option set with`; ordinal, case-insensitive) AND the requested name. A single case-insensitive match is retried once under its stored name and used for every later request; several matches fail `INVALID_CONSTRAINT` with the original fault as inner exception. A stored name returned by the server is adopted. Exact names trigger no listing. The color readback request itself changed (AC-135). Each attempt and the lookup use separate pooled leases. `optionset get` applies the same recognition and single-match retry against the published catalog but rethrows the original fault when nothing or several match (#1535) | `GlobalChoiceNameResolutionTests.ExactStoredName_SendsTheSameNames_WithoutCatalogListing`, `CaseVariant_ResolvesToStoredName_ForEveryRequestAfterTheRejectedAttempt`, `RecognisedNotFoundWordings_TriggerResolution`, `NonEnglishNotFoundFault_ResolvesByFaultCode`, `DataverseMetadataQueryServiceOptionSetNameTests.NonEnglishNotFoundFault_ResolvesByFaultCode`, `EnglishWordingWithAnotherCode_StillResolves`, `CaseInsensitiveServer_AdoptsTheStoredNameItReturns_WithoutListing`, `AmbiguousCaseVariants_FailWithAmbiguityCarryingTheOriginalFault`, `DataverseMetadataQueryServiceOptionSetNameTests.ExactName_RetrievesOnce_WithoutListing`, `CaseVariant_ResolvesToStoredName` | ✅ (fault code confirmed live) |
| AC-134 | Any other failure propagates untouched with no listing and no retry: faults with another code and without the anchored phrase (verified with English and non-English privilege faults), near-miss "Could not find" faults with another code about a solution, publisher, option value or a different option set name, and non-fault failures (timeout, connection). The code cannot say which object was missing: an ObjectDoesNotExist fault about another object (e.g. the solution) lists the catalog and, only when a single stored case variant of the requested name exists, retries once; that retry's own failure surfaces. An exact-name caller whose request fails that way pays one extra as-if-published catalog listing (every global choice with its options) and gets the original fault, with no second write. Asserted live (`MetadataGlobalChoiceLiveTests.MissingSolution_OnAddOption_SurfacesTheErrorAndWritesNothing`): an unknown solution on `InsertOptionValue` is also ObjectDoesNotExist (`0x80040217`, `The given solution unique name (<name>) is not valid`), nothing was written, and `add-option` surfaced that original fault for both the exact and a case-variant spelling. When no case variant exists, the exact spelling is itself stored, or the lookup fails (logged at debug; a non-caller cancellation counts as a lookup failure), the original exception is rethrown unchanged, so every pre-existing failure outcome is preserved. A failing retry surfaces its own failure; caller cancellation propagates; input-only dry-runs perform no reads. The same rules hold in `optionset get` | `GlobalChoiceNameResolutionTests.MissingName_KeepsThePreviousFailure_AndDoesNotRetry`, `OtherServerFault_PropagatesWithoutListingOrRetry`, `NearMissNotFoundFault_PropagatesWithoutListingOrRetry`, `NonEnglishFaultWithAnotherCode_PropagatesWithoutListingOrRetry`, `ObjectDoesNotExistAboutAnotherObject_RetriesOnceAndSurfacesTheRetryFailure`, `ObjectDoesNotExist_ForTheExactStoredName_ListsOnce_AndNeverResendsTheWrite`, `NearMissText_WithObjectDoesNotExist_ListsOnce_AndRetriesOnceUnderTheStoredName`, `DataverseMetadataQueryServiceOptionSetNameTests.NonEnglishFaultWithAnotherCode_DoesNotList`, `NearMissText_WithObjectDoesNotExist_ListsOnce_AndReadsTheStoredName`, `NonFaultWriteFailure_IsNotRetriedUnderAnotherSpelling`, `FailingRetry_SurfacesTheRetryFailure`, `CatalogLookupFailure_SurfacesTheOriginalFault`, `NonCallerCancellationDuringListing_KeepsTheOriginalFault`, `CallerCancellationDuringListing_Propagates`, `OptionTargetPath_KeepsItsWrappingForNonResolutionFailures`, `InputOnlyDryRuns_PerformNoReads`, `DataverseMetadataQueryServiceOptionSetNameTests.Unresolvable_KeepsOriginalServerFault`, `AmbiguousVariants_KeepOriginalServerFault`, `OtherServerFault_DoesNotList_OrRetry`, `NearMissNotFoundFault_DoesNotList_OrRetry`, `CatalogLookupFailure_KeepsTheOriginalFault`, `NonCallerCancellationDuringListing_KeepsTheOriginalFault`, `CallerCancellationDuringListing_Propagates`, `NonFaultFailure_DoesNotList` | ✅ |
| AC-135 | Global option color readback reads as-if-published (a deliberate change to that request for exact names too), so `optionset add-option --color` finds the just-inserted option; the colour itself rides on the insert, and the read-back under the stored name confirms it ("Option colours") | `GlobalChoiceNameResolutionTests.AddOptionWithColor_CarriesTheColorOnTheInsert_AndReadsTheDraftBack` | ✅ |
| AC-136 | Status-reason and local-option update/remove write failures expose only the typed numeric fault code, like AC-131; the caller's cancellation of those writes propagates as cancellation, while any other `OperationCanceledException` (e.g. a transport timeout that may hide a committed write) keeps the previous `SDK_OPERATION_FAILED` reporting | `MetadataDraftOptionReadTests.UpdateAndRemove_SdkFault_SurfacesFaultCodeOnly`, `UpdateAndRemove_CallerCancellation_PropagatesInsteadOfSdkFailure`, `UpdateAndRemove_NonCallerCancellation_KeepsSdkFailure` | ✅ |
| AC-137 | For Choice/Choices columns, the legacy `--options` CSV on `attribute create` and `column create` uses the `optionset create` grammar: bare labels become label-only options (value allocated like `--option "Label"`), explicit values including 0 are kept, malformed entries are rejected `INVALID_CONSTRAINT`; more than one of `--option`, `--options`, `--options-file` is rejected `INVALID_CONSTRAINT`; `column create --option-set-name` with `--options` is rejected like `--choice` (Choice/Choices only; on other types both are still ignored). Labels cannot contain `,` or `=` in this form. Other column types keep the previous parse (#1530) | `MetadataOptionInputGrammarCommandTests.LegacyOptions_BareLabelsBecomeLabelOnlyOptions`, `LegacyOptions_MixedEntriesKeepExplicitZeroAndOrder`, `LegacyOptions_MalformedEntryIsRejectedNotDropped`, `ConflictingLocalOptionInputs_AreRejected`, `GlobalChoiceWithLegacyOptions_IsRejected`, `StrayLegacyOptions_OnNonChoiceColumn_BehaveAsBefore`, `StrayConflictingInputs_OnNonChoiceColumn_BehaveAsBefore`, `GlobalChoiceWithLegacyOptions_OnNonChoiceColumn_IsNotNewlyRejected`, `MetadataCreateOptionAllocationTests.ActualCliParser_LabelOnlyAndZero_ReachRealService` | ✅ |
| AC-138 | `optionset add-option --dry-run` without `--value` reports `optionValueAllocation` "unallocated; Dataverse assigns the value on write" and no numeric value; with `--value` (including 0) the requested value and existing allocation text are kept | `MetadataOptionInputGrammarCommandTests.AddOptionDryRun_OmittedValueIsReportedAsUnallocated` | ✅ |
| AC-139 | `optionset delete` deletes exactly the global choice its preview inspected: the preview reports the stored name (`TargetName`); the confirmation prompt, stderr and JSON (`name`, plus `requestedName`; `--dry-run` too) name the stored name and say the typed name resolved to it; the user must type the stored-name phrase; the delete targets that exact stored name pinned to the previewed metadata id, and is not sent at all if the preview gave no id. The pre-delete recheck performs no catalog resolution and sends the name it was given, so a rename or re-create in between is refused and nothing is deleted. Service-level `DeleteGlobalChoiceAsync` (RPC, TUI) likewise performs no catalog resolution; whether a case-variant spelling matches is then up to the server, as before #1535 | `GlobalChoiceDeleteCommandTests.ConfirmationHelper_NamesTheStoredName_AndSaysTheTypedNameResolvedToIt`, `InteractivePrompt_ShowsAndRequiresTheStoredName_ThenDeletesIt`, `InteractivePrompt_TypedNamePhrase_IsNotAccepted_AndNothingIsDeleted`, `CaseVariant_Json_ReportsStoredName_AndDeletesExactlyThePreviewedObject`, `CaseVariant_Text_StderrSaysTheTypedNameResolved`, `CaseVariant_DryRun_ReportsStoredAndRequestedNames_AndSendsNoDelete`, `ClearPreviewWithoutTargetId_IsRefused_AndNoDeleteIsSent`, `RenamedOrRecreatedBetweenPreviewAndDelete_IsRefused_AndNothingIsDeleted`, `GlobalChoiceNameResolutionTests.ServiceDelete_PerformsNoCatalogResolution_AndSendsTheNameItWasGiven`, `ServiceDelete_ExpectedIdMismatch_RefusesWithoutDeleting`, `PreviewReportsTheStoredName` | ✅ |
| AC-140 | `--label` targeting on status-reason, local-option and global (`optionset update-option` / `remove-option`) update/remove also reads the published list; when the label names a different option, several options, or only a published option, it fails `INVALID_CONSTRAINT` asking for `--value` or a publish, and nothing is written. A label that exists only in drafts proceeds | `MetadataDraftOptionReadTests.LabelTarget_PublishedAndDraftDisagree_IsRefusedAndNothingIsWritten`, `LabelTarget_SameOptionInDraftsButSeveralInPublished_IsRefused`, `LabelTarget_OnlyInPublishedView_IsRefused`, `LabelTarget_AgreeingOrDraftOnly_Proceeds`, `GlobalChoiceNameResolutionTests.GlobalLabelTarget_PublishedAndDraftDisagree_IsRefusedAndNothingIsWritten`, `GlobalLabelTarget_OtherDisagreements_AreRefused`, `GlobalLabelTarget_AgreeingOrDraftOnly_Proceeds` | ✅ |

These fault-code tests invoke real services with fake SDK responses and the existing CLI/MCP error serializers. They do not invoke a live endpoint or claim an MCP tool exists for the status/local-column operations. The numeric code provides an underlying platform identifier; arbitrary platform fault messages remain deliberately withheld, so the literal underlying-message criterion in issue #1531 is not claimed complete.

### Option colours

An option's colour is set on create through `OptionMetadata.Color` (choice create, column create with options). For an existing option the colour rides on the same message as the rest of the change, as that message's `Color` parameter: `optionset add-option --color`, `attribute add-option --option "Label[:Value][:#Color]"` and `entity add-statusreason --color` put it on the `InsertOptionValue` / `InsertStatusValue`; `optionset update-option`, `attribute update-option` and `entity update-statusreason` put it on the one `UpdateOptionValue` that also carries a requested `--new-label` (and, for callers of the service's `UpdateOptionValueRequest`, a description; no CLI option, MCP tool, RPC method or TUI screen sets it), so the label and the colour are written together in one request. A colour-only update sends no `Label` and no `Description`. Option updates set `MergeLabels = true`. Every option-value message that carries a solution (insert, status insert, update, delete/remove, order) sends an empty or blank solution name as no solution: Dataverse rejects `""` on each of them with a "solution name is not valid" fault.

The service passes the colour through exactly as it receives it: Dataverse stores the `Color` parameter (update and insert) and `OptionMetadata.Color` (create) verbatim, without validating or normalising it (asserted live for a global choice, a local option, a status reason and a state value), so the service neither rejects nor rewrites a colour and compares the stored value exactly. Surfaces that take `--color` / `color` pass it unchanged; `attribute add-option`'s `--option` token is trimmed and accepts a colour only when it starts with `#`, and the comparison uses the value actually sent. A colour cannot be cleared through these commands: an empty `--color` is "not provided".

An option update must request something: `optionset update-option`, `attribute update-option` and `entity update-statusreason` without `--new-label` or `--color` fail `MISSING_REQUIRED_FIELD` ("Nothing to update: provide --new-label or --color.") before connecting, and the service refuses the same request (for the global update DTO: no new label, description or colour) before any Dataverse call. An empty description in the service's `UpdateOptionValueRequest` is sent as an empty base-language description, which clears it; null leaves it unchanged.

After the write request returns, the service invalidates the cached metadata and, when a label, description or colour was written, reads the option back from draft metadata on its own lease (one read confirms all three; since AC-148 labels and descriptions are confirmed as well as colours). If that read fails in any way (SDK fault, timeout, checkout failure, cancellation, authentication, missing option) or a written value differs from what is stored, the command fails `WRITE_NOT_CONFIRMED` (CLI exit 2): the write request returned success, but what it carried could not be confirmed. The message states what was written (for an insert, the option and the value it was added with; for an update, the option and the properties sent), that `--publish` was not attempted when it was requested, and only service-authored text about the cause, which is the inner exception. Three read-back outcomes moved to this code and exit: a missing option (was `OPTION_NOT_FOUND`, exit 8), a checkout timeout (was `Connection.Timeout`, exit 4) and an authentication failure (was exit 5). For the last, the re-authentication signal stays available: `Context["requiresReauthentication"]` is true and the inner exception chain holds the authentication failure. Re-running an add with an auto-assigned value after this error inserts a second option (with an explicit `--value` the re-run fails as a duplicate). `SDK_OPERATION_FAILED` means the request failed and makes no statement either way: when an option or status-reason insert itself times out or is cancelled, its message says the outcome is unknown and how to check (for status reasons, `ppds metadata entity list-statusreasons <entity>`) before retrying. When the write and its confirmation succeed but `--publish` fails, the publish failure keeps its own code, exit and re-authentication signal, and its message states what was written, that it is saved but not published, and the publish command to run. Successful option deletes, reorders and status-reason removes invalidate the cache with or without `--publish`.

Asserted by live tests (2026-11-17, development organisation, base language 1033; French 1036 provisioned for the language tests): `OptionColorPersistenceLiveTests.GlobalChoice_OnlyTheColorParameterChangesAnExistingOptionsColour` (global choice) — `OptionMetadata.Color` through `UpdateOptionSet` with and without `MergeLabels` leaves the colour unchanged in the draft read and in the published read after `PublishXml`; the `Color` parameter of `UpdateOptionValue` is stored with no solution, with a solution, alone and with a `Label` and a `Description`; an omitted `Label` / `Description` / `Color` leaves that value unchanged, and a request with none of them changes nothing; an empty base-language description clears it (the base-language entry stays, empty); `InsertOptionValue` `Color` is stored; `SolutionUniqueName = ""` faults ("solution name is not valid") on `UpdateOptionValue`, `InsertOptionValue`, `OrderOption` and `DeleteOptionValue` and writes nothing. `OptionColorPersistenceLiveTests.Colors_AreStoredVerbatim_WithoutValidation` — `#ff0000`, `#F00`, `FF0000`, `Red`, ` #00FF00 `, `notacolor`, `#GGGGGG`, `#FF0000FF` are each stored verbatim on create, update and insert of a global choice option; `MetadataGlobalChoiceLiveTests.ColourMechanisms_LocalOptionStatusReasonAndStateValue` — `#ff0000`, `#F00`, `FF0000` and `Red` are stored verbatim on update and insert of a local option and a status reason, and on update of a state value. `MetadataGlobalChoiceLiveTests.ColourMechanisms_LocalOptionStatusReasonAndStateValue` (raw messages on a throwaway table) — a minimal `UpdateAttribute` carrier is ignored for a local option and a status reason, also after `PublishXml`; the `Color` parameter of `UpdateOptionValue` is stored on a local option, a multi-select (Choices) option, a status reason and a state value (`statecode`, without a solution), and of `InsertOptionValue` / `InsertStatusValue`; an empty solution faults on local and status `DeleteOptionValue` and on `InsertStatusValue`. `MetadataGlobalChoiceLiveTests.ServiceColourWrites_KeepDescriptionsAndLabels_WithoutASolution` — through the service with no or a blank solution, descriptions and labels are kept. Earlier releases (since #1234) used the ignored `UpdateOptionSet` / `UpdateAttribute` route, so `--color` on add/update of an existing option had no effect. The same tests run on an organization whose base language is French (they create and read labels in the organization's base language; results in "Label languages"). Not observed: the generic service update with entity + attribute (unit-tested only); a multi-select option with non-canonical colour text.

| ID | Criterion | Test | Status |
|----|-----------|------|--------|
| AC-146 | A requested colour rides as the `Color` parameter on the insert or on the single `UpdateOptionValue` that also carries any requested label/description, passed through verbatim; a colour-only update sends no `Label`; no `UpdateOptionSet` or `UpdateAttribute` is sent for a colour; a blank solution is sent as none on every option-value message; an update requesting nothing is refused before any Dataverse call (CLI: before connecting); an empty description clears; after a committed write the cache is invalidated and every read-back failure or colour mismatch is `WRITE_NOT_CONFIRMED` (exit 2) stating what was written and that publication was not attempted, on all six paths, keeping the re-authentication signal in `Context`; a timed-out or cancelled insert says its outcome is unknown; a publish failure after the write keeps its code and exit and says what was written; deletes, reorders and status-reason removes invalidate the cache. Live (asserted): see "Option colours" | `MetadataGlobalOptionServiceTests.AddOptionValue_WithColor_CarriesColorOnTheInsert_AndReadsItBack`, `UpdateOptionValue_WithLabelAndColor_SendsOneRequestCarryingBoth`, `UpdateOptionValue_ColorOnly_SendsNoLabel_AndCarriesTheColor`, `ColorThatDataverseDidNotStore_FailsWithWriteNotConfirmed`, `EmptySolution_IsSentAsNoSolution`, `MetadataLocalOptionServiceTests.AddColumnOption_WithColor_CarriesColorOnTheInsert`, `UpdateColumnOption_WithLabelAndColor_SendsOneRequestCarryingBoth`, `MetadataStatusReasonServiceTests.UpdateStatusReasonAsync_ColorOnly_SendsOneRequestWithNoLabel`, `OptionLabelLanguageTests.ColorOnlyUpdate_SendsOneRequestWithNoLabel_AndMergesLabels`, `OptionWriteRequestTests.BlankSolution_IsSentAsNoSolution_OnEveryOptionMessage`, `NamedSolution_IsSentUnchanged`, `UpdateThatRequestsNothing_IsRefusedBeforeAnyDataverseCall`, `EmptyDescription_IsSentAsAnEmptyEnglishDescription` (base 1033), `GenericUpdate_WithEntityAndAttribute_WritesAndConfirmsTheColorOnThatAttribute`, `StoredColor_IsComparedExactly`, `MetadataLeaseLifetimeTests.ColorNotStored_AfterTheWrite_IsWriteNotConfirmed_OnEveryPath`, `ColorReadBackFailure_AfterTheWrite_IsWriteNotConfirmed`, `MetadataDraftOptionReadTests.ColorReadCancellation_AfterTheInsert_IsWriteNotConfirmed`, `LabelNoticeCommandTests.UpdateOption_WithNothingToChange_IsAValidationErrorBeforeConnecting`, `MetadataDraftOptionReadTests.InsertThatTimesOutOrIsCancelled_SaysTheOutcomeIsUnknown`, `MetadataLeaseLifetimeTests.ColorReadBackAuthFailure_KeepsTheReauthenticationSignal`, `PublishAuthFailure_AfterTheWrite_KeepsItsCodeExitAndSignal`, `FailureAtEachStep_ReturnsLeasesAndStopsLaterWork`, `OptionWriteRequestTests.SuccessfulRemoveOrReorder_InvalidatesTheCache_WithoutPublish`, live `OptionColorPersistenceLiveTests.GlobalChoice_OnlyTheColorParameterChangesAnExistingOptionsColour`, `Colors_AreStoredVerbatim_WithoutValidation`, `MetadataGlobalChoiceLiveTests.ColourMechanisms_LocalOptionStatusReasonAndStateValue`, `ServiceColourWrites_KeepDescriptionsAndLabels_WithoutASolution`, `GlobalOptionUpdate_NewLabelWritesTheBaseLanguage_ColourAndDescriptionOnlyChangeNoLabel`, `LocalOptionAndStatusReason_NewLabelWritesTheBaseLanguage_ColourOnlyChangesNoLabel` | ✅ |

### Updates preserve unpublished and unrequested values

An update changes only what the caller asked to change. `optionset update` re-sends the whole global choice through `UpdateOptionSet`, so it reads the choice as-if-published: a property the request does not set (display name, description and the other choice-level settings) keeps its current unpublished value instead of reverting to the published copy. The other update paths already avoid this: table and column updates send a fresh minimal payload carrying only the requested fields, and option, status-reason and color updates resolve their targets from draft metadata (#1531). This does not make concurrent edits by another user safe: the read and the update are separate requests.

Live test `MetadataGlobalChoiceLiveTests.ChoiceUpdate_ChangesOnlyTheDisplayName_KeepsAnotherDescriptionEdit` asserts that `optionset update` changes only the requested property (another edit to the description and the options are kept) and, when an unpublished edit stays unpublished, that `optionset update` publishes nothing. Whether a global choice's display-name/description edit made through `UpdateOptionSet` is visible to the published read before `PublishXml` is recorded, not asserted: on the development organisation used on 2026-11-17 it was visible at once, so there the publish boundary could not be exercised. The as-if-published read is kept because it is never worse than the published read. The "must be published" advice the commands print is unchanged.

| ID | Criterion | Test | Status |
|----|-----------|------|--------|
| AC-141 | `optionset update` reads the global choice as-if-published for both an exact and a case-variant name, so an unpublished description survives a display-name-only update and the update carries the stored name | `GlobalChoiceNameResolutionTests.ChoiceUpdate_ReadsDraft_SoUnrequestedPropertiesKeepTheirUnpublishedValues` | ✅ |

### Label languages

Every label PPDS writes (`--label` on add/create, `--new-label`, `--display-name`, `--plural-name`, `--description`, Boolean true/false labels, local and global option labels, the primary name column, lookup and alternate-key display names, state labels) is written in the organization's base language: the LCID in `organization.languagecode`, which Dataverse requires on every component (other provisioned languages are translations of it). The service reads it once per environment (`IOrganizationBaseLanguageProvider`, one instance per service provider and connection pool, on its own lease released before the value is returned); a failed read is not cached and is classified like the other metadata reads (an expired sign-in stays an authentication error with the re-authentication signal, connection failures keep their code, anything else, including a response without a language, is `SDK_OPERATION_FAILED`). The read happens only when a label is about to be written or a notice computed (one extra request per environment per process). On an English-base organization nothing changes: the base language is 1033. The help text of every update option that writes a label (`--new-label`, the deprecated `choice update-option --label`, and every update `--display-name`, `--plural-name` and `--description` on `optionset`, `choice`, `entity`, `table`, `attribute` and `column`) and of the MCP update tools says "(in the organization's base language)". There is no option to choose another language. `--label` used as a *selector* on update/remove matches the label in the caller's user language (the user-localised label, falling back to the first localised label), which is the label list commands display.

Why not 1033: observed live on 2026-11-17 on an organization whose base language is French (1036) with English not provisioned (live probe and the generalised live tests before this change), a 1033 label was (D1) acknowledged but not stored by `UpdateOptionValue` and `UpdateOptionSet` (labels, descriptions and an empty description), so `optionset update`, `optionset update-option`, `attribute update-option` and `--description ""` exited 0 having written nothing; (D2) stored over the 1036 base label by `UpdateEntity` and `UpdateAttribute` (`table update`, `column update`), with a notice saying the 1036 label was "not changed"; (D3) rejected with `0x80044195` ("The language code 1033 is not a valid language for this organization") by `InsertOptionValue`, `InsertStatusValue` and a status-reason `UpdateOptionValue`. Create messages stored the 1033 text as the 1036 label. The live tests `MetadataGlobalChoiceLiveTests.BaseLanguage_D1_LabelUpdatesAreStored_NotSilentlyIgnored`, `BaseLanguage_D2_TableAndColumnUpdates_WriteTheBaseLabel_WithoutAFalseNotice` and `BaseLanguage_D3_InsertsAndStatusReasonUpdates_Succeed` replay the old 1033 shape raw on such an organization (recorded, "before") and assert the service result ("after").

Option value, status-reason, state-value and global-choice updates set `MergeLabels = true`, so a written base-language label leaves the other languages' text in place (asserted live with French (1036) provisioned on an English-base organization: a requested base-language label keeps the French label of a global option, a local option and a status reason — `MetadataGlobalChoiceLiveTests.GlobalOptionUpdate_NewLabel_KeepsTheSecondLanguage`, `LocalOptionAndStatusReason_NewLabel_KeepsTheSecondLanguage`). An option or status-reason update that does not supply `--new-label` (for example a colour- or description-only update, or one that selects the option by its user-language `--label`) sends no `Label` at all, so it cannot replace the base-language label with the caller's user-language text or rewrite any translation; asserted live: an `UpdateOptionValue` without a `Label` leaves the label unchanged for a global option, a local option and a status reason. `optionset update` re-sends unrequested choice labels as read from the draft (AC-141), with every translation that read returns.

Table and column updates (`entity`/`table update`, `attribute`/`column update`, the MCP `ppds_metadata_update_table`/`ppds_metadata_update_column` tools, the RPC `metadata/updateTable`/`metadata/updateColumn` methods and the TUI column edit) send a fresh minimal payload, so an unrequested label is never sent and cannot be overwritten with the caller's user-language text. A requested `--display-name`, `--plural-name` or `--description` is written as a single base-language label, and the `UpdateEntity` and typed `UpdateAttribute` requests set `MergeLabels = true`, so other languages' text is kept (asserted live with French on an English-base organization: `MetadataGlobalChoiceLiveTests.TableAndColumnLabelUpdates_KeepTheSecondLanguage`). When a table label is requested, the table is first read (`RetrieveEntity`, entity filter, its own lease) to produce the `BASE_LANGUAGE_LABEL_ONLY` notices; a column update reuses its existing attribute read. Both reads are published reads, so both notices quote the label list commands show. Known limit: with `--publish` and a pending (unpublished) edit to the caller's-language label, the quoted label can be stale as soon as the command ends, because the publish makes the pending edit current. Option, status-reason and local-option notices differ: they quote the draft label from the as-if-published target read those updates already make. The table read classifies failures as the column read does (timeout/connection/auth keep their codes, exit codes and re-authentication signal; other SDK failures `SDK_OPERATION_FAILED`), its checkout follows the shared acquisition rule, and a response without table metadata stops the update. The notices (`DisplayName`, `PluralDisplayName`, `Description`) are surfaced as for choices: CLI `Warning:` / JSON `data.validationMessages`, MCP and RPC `validationMessages`, the TUI status line after a column edit.

The notice. A requested `--new-label`, `--display-name`, `--plural-name` or `--description` reports a notice (rule `BASE_LANGUAGE_LABEL_ONLY`, field `NewLabel`, `DisplayName`, `PluralDisplayName` or `Description`; the update still succeeds, exit 0) exactly when (1) the label Dataverse shows the caller (the target's user-localised label) is in a language other than the base language, (2) a label in that language is actually stored on the target (it is one of its localised labels), and (3) that stored text differs from the requested text. Message: "Only the base-language (LCID <base>) <noun> is written. The <noun> Dataverse shows you (LCID <shown>) is not changed and still reads '<stored text>'." Every notice is therefore true: the caller still sees the quoted text after the write. When the shown label is only a fallback (no stored label in the caller's language), the caller sees the new base-language text after the write and there is no notice. Observed live (2026-11-17, English-base organization with French provisioned, French-UI caller, `MetadataGlobalChoiceLiveTests.FrenchCaller_*`): `UserLocalizedLabel` is the 1036 label when one is stored; for an option labelled in English only it is the base text reported with LCID 1033 (a fallback with the base LCID); an option with empty 1033 text and French text shows LCID 1036 'Seulement'. Created with English text only, a table's display, plural and description, a column's display name and description and the primary name column carry the same text under 1033 and 1036 (a real stored copy, so the notice is true), while an option label is stored under 1033 alone. The notice was true at every call site (global option, choice display name and description, local option, status reason, table display/plural/description, column display/description, primary name column) and absent where the caller then saw the new text. For a caller whose UI language is the base language, `UserLocalizedLabel` is the base entry even when its text is empty (`MetadataGlobalChoiceLiveTests.UserLocalizedLabel_WithoutALabelInTheCallersLanguage_IsRecorded`). Surfaces: a `Warning:` line on stderr in CLI text mode; an additive `data.validationMessages` array (`field`, `rule`, `message`) in JSON, omitted when there is no notice; the TUI status line after a choice or column update ("Choice updated: <name> Warning: …"); the MCP `ppds_metadata_update_choice`/`_table`/`_column` results and the RPC `metadata/updateTable`/`metadata/updateColumn` responses carry `validationMessages` (explicit `field`/`rule`/`message`). The service does not also send notices through the progress reporter, so nothing is shown twice. This covers `optionset update`, `optionset update-option`, `attribute update-option`, `entity update-statusreason`, `table`/`entity update`, `column`/`attribute update` and the deprecated `choice update` / `choice update-option`. Dry-runs of all of these read the target as the update would and return the same notices, writing nothing (before this change the table, column and choice dry-runs returned no notice and read nothing). State-value updates read no labels and give no notice.

Read-back (AC-148). After a label write returns success, the service invalidates the caches and reads the target back as-if-published on its own lease, before any publish, and the stored base-language text must equal the text sent (ordinal comparison; an empty requested text is matched by an empty or absent base-language label). One read confirms every label (and, for options, the description and colour) the write carried. Requests added per command, only when a label is written: table create 2 (the table, then its primary name column), every other create or update 1 (`RetrieveEntity`, `RetrieveAttribute`, `RetrieveOptionSet` or `RetrieveEntityKey`); option writes that already confirmed a colour send no extra request. A label not stored as sent, or a failed read-back (SDK fault, timeout, cancellation, checkout failure, authentication, missing target), is `WRITE_NOT_CONFIRMED` (CLI exit 2) saying what was written, which label could not be confirmed (requested text and LCID, stored text), and, when `--publish` was requested, that it was not attempted; `Context["requiresReauthentication"]` is true when the read failed on an expired sign-in. Observed live (`MetadataGlobalChoiceLiveTests.BaseLanguage_LabelReadBack_RecordsWhatDataverseStores`, 2026-11-17, English-base development organization and French-base organization): `UpdateOptionValue` and `UpdateEntity` store label text verbatim, including leading/trailing spaces, an inner double space, non-ASCII characters and a line break, and the service confirms each; Dataverse normalises none of these, so the comparison is exact.

Clearing: `optionset update --display-name ""` or `--description ""`, `table update --description ""` and `column update --description ""` send an empty base-language label with `MergeLabels`, so other languages' text is kept, and the read-back accepts an empty or absent base-language label. Asserted live (`MetadataGlobalChoiceLiveTests.EmptyDescription_RecordsWhatDataverseDoes`): the request succeeds and Dataverse clears the base-language description; the label keeps a base-language entry whose text is empty. Asserted live with French (`EmptyDescription_KeepsTheSecondLanguage`): the French description is kept. On a French-base organization the old 1033 shape was ignored (D1); the base-language clear is asserted by `BaseLanguage_D1_LabelUpdatesAreStored_NotSilentlyIgnored` and `BaseLanguage_D2_TableAndColumnUpdates_WriteTheBaseLabel_WithoutAFalseNotice`.

Not observed: a French-base organization with English (1033) also provisioned (a caller with an English UI there would be the case where a stored translation is shown); a base language other than 1033 or 1036.

| ID | Criterion | Test | Status |
|----|-----------|------|--------|
| AC-142 | Every label write (create and update of tables, columns, lookups, keys, global choices, options, local options, status reasons and state values) is written in the organization's base language (`organization.languagecode`, read once per environment, failures classified); global, local and status-reason option updates without `--new-label` send no `Label`; `--new-label` is the single base-language label with `MergeLabels`; `optionset update` and state-value updates merge labels; clearing sends an empty base-language label. Live (asserted): an omitted label is unchanged; with French provisioned, a requested base-language label keeps the French label of global options, local options and status reasons, and an empty base-language description keeps the French one; on a French-base organization label updates, clears, inserts and status-reason updates are stored (D1, D3) | `BaseLanguageLabelWriteTests.EveryLabel_IsWrittenInTheBaseLanguageOnly` (18 paths, base 1036), `Clearing_SendsAnEmptyBaseLanguageLabelOnly_AndIsConfirmedByAnEmptyStoredText`, `OrganizationBaseLanguageProviderTests` (all), `OptionLabelLanguageTests.UnrequestedLabel_IsNeverSent`, `RequestedLabel_IsWrittenInEnglishAndMergedWithOtherLanguages`, `LabelSelector_MatchesTheUserLanguageLabel_AndLeavesTheEnglishLabelAlone`, `ChoiceUpdate_MergesTheRequestedEnglishLabel_AndResendsOtherLabelsInEveryLanguage`, `StateValueUpdate_MergesTheEnglishLabel`, `UnrequestedLabel_WithNoText_IsNotSent`, `ColorOnlyUpdate_SendsOneRequestWithNoLabel_AndMergesLabels`, live `OptionColorPersistenceLiveTests.GlobalChoice_OnlyTheColorParameterChangesAnExistingOptionsColour`, `MetadataGlobalChoiceLiveTests.ColourMechanisms_LocalOptionStatusReasonAndStateValue`, `GlobalOptionUpdate_NewLabel_KeepsTheSecondLanguage`, `LocalOptionAndStatusReason_NewLabel_KeepsTheSecondLanguage`, `EmptyDescription_KeepsTheSecondLanguage`, `BaseLanguage_D1_LabelUpdatesAreStored_NotSilentlyIgnored`, `BaseLanguage_D3_InsertsAndStatusReasonUpdates_Succeed` | ✅ |
| AC-145 | A requested label returns a `BASE_LANGUAGE_LABEL_ONLY` notice naming the base LCID, the shown LCID and the shown text exactly when the label Dataverse shows the caller is a stored label in a non-base language that differs from the requested text, for `NewLabel`, `DisplayName`, `PluralDisplayName` and `Description`; no notice when no label is requested, the shown label is the base language, it is only a fallback, or it already reads the requested text; dry-runs return the same notices and write nothing. The TUI status line shows the notice. The CLI prints a `Warning:` line in text mode and an additive `data.validationMessages` array in JSON (absent without a notice), exit 0; the update options' help says "(in the organization's base language)"; the MCP and RPC update results carry the notices | `BaseLanguageLabelWriteTests.NonBaseShownLabel_ThatIsStored_GivesTheNotice_NamingBothLanguages`, `NonBaseShownLabel_ThatIsOnlyAFallback_GivesNoNotice`, `BaseShownLabel_OrAStoredTranslationThatAlreadyReadsTheText_GivesNoNotice`, `OnAFrenchBaseWithoutTranslations_NoFalseNotice`, `DryRun_ReturnsTheSameNotices_AndWritesNothing`, `OptionLabelLanguageTests.RequestedLabel_ForANonEnglishUser_SaysOnlyTheEnglishLabelIsWritten`, `NoNotice_WhenNoLabelIsRequested_OrTheUserLanguageIsEnglish`, `ChoiceUpdate_ForANonEnglishUser_SaysOnlyTheEnglishDisplayNameIsWritten`, `ChoiceUpdate_Description_ForANonEnglishUser_ReportsTheDescriptionNotice`, `NoNotice_WhenTheShownLabelAlreadyReadsTheRequestedText`, `LabelNoticeCommandTests.Tui_ChoiceUpdateStatus_ShowsTheNotice`, `Text_WarnsThatOnlyTheEnglishLabelIsWritten`, `Json_ReportsTheNoticeInValidationMessages`, `NoNotice_AddsNothing`, `Help_SaysTheLabelIsInTheBaseLanguage`, `MetadataValidationErrorMappingTests.UpdateChoiceTool_ReportsTheUncheckedNameOnDryRun_AndLabelNotices`; live `MetadataGlobalChoiceLiveTests.UserLocalizedLabel_WithoutALabelInTheCallersLanguage_IsRecorded`, `BaseLanguage_D2_TableAndColumnUpdates_WriteTheBaseLabel_WithoutAFalseNotice`, the `FrenchCaller_*` tests (opt-in; they switch the caller's UI language) | ✅ |
| AC-147 | Table and typed/finite column updates set `MergeLabels = true`, write each requested label as a single base-language label, send no unrequested label, read the table's published labels only when a table label is requested (read failures keep the column read's classification; a response without metadata stops the update), and return `BASE_LANGUAGE_LABEL_ONLY` notices per AC-145; CLI, MCP, RPC and TUI surface them (the TUI keeps a reload error in the status line); dry-runs return the same notices. Live (asserted): unrequested table and column labels are unchanged; with French provisioned, requested base-language table and column labels keep the French ones; on a French-base organization they replace the base label with no notice (D2) | `TableColumnLabelLanguageTests.TableUpdate_ForANonEnglishUser_MergesEnglishLabels_AndReportsEachUnchangedShownLabel`, `TableUpdate_WithoutALabel_ReadsNothing_AndStillMerges`, `TableUpdate_NoNotice_ForAnEnglishUser_OrWhenTheShownLabelAlreadyReadsTheText`, `TypedColumnUpdate_ForANonEnglishUser_MergesEnglishLabels_AndReportsTheUnchangedShownLabels`, `TypedColumnUpdate_WithoutALabel_SendsNoLabel_AndGivesNoNotice`, `DryRuns_ReturnTheSameNotices_AndWriteNothing`, `ColumnLabelOnlyTransitionTests.NewShapes_ReportEnglishOnlyNotices_ForTheShownNonEnglishLabels`, `ExistingFourteenTypes_KeepFreshPayloadAndMergeContract`, `PrimaryIdLabelUpdateTests.TypedUpdate_PreservesRequestShape`, `TableUpdateLeaseTests.LabelReadFailure_KeepsItsClassification_AndStopsBeforeTheUpdate`, `LabelReadWithoutTableMetadata_StopsBeforeTheUpdate`, `PayloadAndSuccessMessages_RemainUnchanged`, `MetadataAuthoringRpcTests.MapLabelNotices_CarriesFieldRuleAndMessage_AndIsOmittedWithoutNotices`, `LabelNoticeCommandTests.Text_WarnsThatOnlyTheEnglishLabelIsWritten`, `Json_ReportsTheNoticeInValidationMessages`, `Tui_ColumnUpdateStatus_ShowsTheNotice`, `Tui_UpdateStatus_KeepsAReloadError`, `MetadataValidationErrorMappingTests.UpdateTableAndColumnTools_ReportLabelNotices`, `BaseLanguageLabelWriteTests.OnAFrenchBaseWithoutTranslations_NoFalseNotice`, live `MetadataGlobalChoiceLiveTests.TableAndColumnLabelUpdates_WriteTheBaseLanguage_AndLeaveUnrequestedLabelsAlone`, `TableAndColumnLabelUpdates_KeepTheSecondLanguage`, `BaseLanguage_D2_TableAndColumnUpdates_WriteTheBaseLabel_WithoutAFalseNotice` | ✅ |
| AC-148 | Every label write is read back once as-if-published after the cache is invalidated and before any publish (table create: the table and its primary name column; options: label, description and colour in one read); a label not stored as sent, or any read-back failure, is `WRITE_NOT_CONFIRMED` (exit 2) stating what was written and, with `--publish`, that publication was not attempted, keeping the cause as the inner exception and the re-authentication signal in `Context`. Live (asserted): what Dataverse stores for padded, double-spaced, non-ASCII and multi-line text, and that the service confirms it | `BaseLanguageLabelWriteTests.EveryLabelWrite_IsReadBackOnce_AsIfPublished`, `ALabelDataverseDidNotStore_IsWriteNotConfirmed`, `ALabelDataverseDidNotStore_WithPublish_SaysPublicationWasNotAttempted`, `AConfirmedWrite_WithPublish_PublishesAfterTheReadBack`, `AFailedReadBack_IsWriteNotConfirmed_AndKeepsTheCause` (18 paths × timeout, fault, cancellation), `AReadBackThatNeedsASignIn_KeepsTheReauthenticationSignal`, `OptionLabelAndColour_AreConfirmedByOneRead`, `ColourOnly_IsStillConfirmed_AndAMismatchNamesTheColour`, `AnEmptyDescriptionOnCreate_IsNotSentAndNotConfirmed`, live `MetadataGlobalChoiceLiveTests.BaseLanguage_LabelReadBack_RecordsWhatDataverseStores` | ✅ |

### Global choice names in dry-runs

Dry-runs keep their documented scopes. `optionset update` and `optionset add-option` are input-only, and `attribute create` / `column create` validate inputs and the publisher; none of them reads the global choice they name, so a mis-cased or unknown name passes the dry-run and is resolved (#1535) or rejected only on the real run. `optionset update-option`, `remove-option` and `delete` dry-runs read the target and therefore resolve the name. `reorder` has no dry-run. The input-only previews state the gap: JSON `metadata.preview.globalChoiceNameChecked` is `false`, and text mode adds `[Dry-Run] Global choice name '<name>' was not looked up; its spelling and existence are checked only when changes are applied.` The field is additive and only ever `false`: it is omitted on every other dry-run, so its absence means "not an input-only dry-run that takes a global choice name", not "checked". The deprecated `choice` and `column` commands share these handlers. MCP: `ppds_metadata_update_choice` with `dryRun: true` returns `globalChoiceNameChecked: false` (omitted on a real run). `ppds_metadata_add_column` cannot reference a global choice and `ppds_metadata_add_option_value` has no dry-run, so neither carries the field.

| ID | Criterion | Test | Status |
|----|-----------|------|--------|
| AC-143 | Dry-runs of `optionset update`, `optionset add-option` (with and without `--value`), `attribute create --choice` and `column create --option-set-name` report `globalChoiceNameChecked=false` and `targetExistenceChecked=false` in JSON `metadata.preview` and the not-looked-up line in text; other dry-runs omit both; the MCP update-choice dry-run reports `globalChoiceNameChecked=false` | `GlobalChoiceDryRunScopeTests.Json_PreviewSaysTheGlobalChoiceNameWasNotLookedUp`, `Text_DiagnosticSaysTheGlobalChoiceNameWasNotLookedUp`, `OtherDryRuns_DoNotReportAGlobalChoiceName`, `MetadataValidationErrorMappingTests.UpdateChoiceTool_ReportsTheUncheckedNameOnDryRun_AndLabelNotices` | ✅ |

### MCP validation errors

The structured payload changes; the MCP protocol error code stays `InternalError`, as for every other structured PPDS error, so clients must read `errorCode` from the payload.

| ID | Criterion | Test | Status |
|----|-----------|------|--------|
| AC-144 | Every MCP authoring tool (add column, add option value, create choice/key/relationship/table, update choice/column/relationship/table) reports a metadata validation failure with its error code, message and `context.field` (omitted when the exception has no field) instead of `Operation.Internal`; through the real service an ambiguous case-variant choice name is `INVALID_CONSTRAINT` on field `Name`, and neither the inner fault's text nor synthetic secrets reach the payload | `MetadataValidationErrorMappingTests.Helper_ReportsCodeMessageAndField_WithoutTheInnerFault`, `Helper_WithoutField_OmitsContext`, `EveryAuthoringTool_ReportsTheValidationFailure`, `ActualService_AmbiguousChoiceName_IsInvalidConstraintAtMcpBoundary` | ✅ |

### Create-option presence and publisher warnings

Local Choice/Choices creation and global-choice creation distinguish an omitted numeric value from an explicitly supplied zero. The existing public `int OptionDefinition.Value` getter/setter and parameterless constructor remain; the additive read-only `[JsonIgnore] HasExplicitValue` property records whether the setter was used. Callers wanting allocation must omit the setter/property. `Value = 0` now deliberately means zero, rather than requesting allocation.

System.Text.Json roundtrips omit only absent `Value` and retain explicit zero, including a default-value ignore policy. Existing Label/Description/Color fields, naming policy, case sensitivity, unknown-member and number handling remain. Direct model JSON `Value: null` remains invalid for the int property; the CLI options-file grammar retains its existing nullable-value-as-omission compatibility.

Creation reserves all explicit values, rejects duplicate explicit values and allocates distinct omitted values through the existing publisher-prefix derivation helper. It normalizes copied options rather than mutating caller data. Global `--options` accepts comma-separated bare labels or `Label=integer`, splitting the last `=` for explicit values. Empty/trailing comma entries remain ignored; malformed nonempty explicit definitions fail instead of disappearing. Labels containing commas still require a different input surface; no CSV quoting grammar is added.

Explicit values outside `[customizationoptionvalueprefix * 10000, prefix * 10000 + 9999]` remain unchanged and produce validation warnings. If numeric prefix metadata is absent, explicit-only creation remains permitted with a truthful range-unavailable warning; omitted values still require a prefix. Warnings appear in existing CLI results/text and the additive global-choice MCP `validationMessages` field. These checks do not establish server feasibility, persisted option values or whole-repository error normalization.

Successful initial local Choice/Choices or global-choice dry-runs containing typed omitted values report `validationScope=input-and-publisher`, `publisherPrefixChecked=true` and `optionValueAllocation=provisional`. This describes completed copy-based derivation, never a reservation or platform-accepted numeric assignment. `targetOptionsRead`, `targetExistenceChecked`, `dependenciesChecked`, `serverFeasibilityChecked` and `changesPersisted` remain false. JSON and text use the same selected scope; neither exposes a normalized value list. Explicit-only values (including0), nonchoice columns, global-reference columns and empty option arrays retain `optionValueAllocation=not-performed`; missing numeric prefix with omission fails rather than returning a successful preview. Existing draft-value and global add-option semantics remain unchanged. `MetadataCreatePreviewCompositionTests.InitialCreate_ReportsOnlyCompletedProvisionalAllocation` covers the composed real-service CLI boundary; all 36 composed real-service CLI cases passed, alongside the original 58 option-presence/service/parser/MCP cases; the full ordinary unit matrix passed with the composed dependency, label and plugin paths.

Public API delta and migration: `OptionDefinition.HasExplicitValue` is an additive read-only ignored property; the public int Value accessors and parameterless constructor remain. `MetadataCreateChoiceResult.ValidationMessages` is a second additive public surface, serialized by MCP as `validationMessages`. Consumers should tolerate the extra warning field and must omit Value, rather than set Value=0, when requesting allocation. Release notes must identify both additive surfaces and the explicit-zero migration; the acceptance table below records passing offline contracts. Persisted platform allocation and final-source/API/release validation remain separate.

| AC | Contract | Test | Status |
|---|---|---|---|
| AC-106 | Local Choice/Choices and global create allocate only omission, reserve explicit0 and other values, and preserve caller options | `MetadataCreateOptionAllocationTests.OmittedValues_AreDistinctAndDoNotMutateCaller`, `ExplicitZero_IsPreservedAlongsideOmittedAndReservedValues` | ✅ |
| AC-107 | Duplicate explicit values including0 reject before create; omitted values are not duplicate zero | `MetadataCreateOptionAllocationTests.DuplicateExplicitValues_RejectBeforeCreate` | ✅ |
| AC-108 | Range warnings preserve explicit values, missing numeric prefix preserves explicit-only compatibility, and dry-run never creates | `MetadataCreateOptionAllocationTests.ExplicitRange_WarnsWithoutRejectingOrRewriting`, `DryRun_OmittedValuesValidateWithoutCreateOrCallerMutation`, `MetadataCreateMissingPrefixTests.ExplicitOnly_MissingNumericPrefixWarnsAndPreservesValue`, `Omission_MissingNumericPrefixRetainsValidationFailure` | ✅ |
| AC-109 | SDK cancellation/cause/token behavior, per-request returned leases and immutable caller options are retained | `MetadataCreateOptionAllocationTests.CancellationAtEverySdkStage_PreservesIdentityAndCaller`, `CreateSdkFailure_PreservesExistingCauseWithoutMutatingCaller` | ✅ |
| AC-110 | Actual pure CLI parsers preserve bare global labels/legacy explicit syntax and options-file omission versus0 | `MetadataCreateOptionAllocationTests.ActualCliParser_LabelOnlyAndZero_ReachRealService`, `OptionDefinitionPresenceTests.GlobalGrammar_PreservesValidLegacyAndAddsBareLabels`, `GlobalGrammar_MalformedNonemptyDefinitionsRejectInsteadOfDisappearing`, `OptionsFile_OmittedAndExistingNullCompatibilityAreDistinctFromExplicitZero` | ✅ |
| AC-111 | Existing int accessor/constructor shape remains with an additive ignored readonly presence getter | `OptionDefinitionPresenceTests.ExistingIntAndConstructorContract_RemainsWithAdditiveReadOnlyPresence` | ✅ |
| AC-112 | JSON omission/explicit0 roundtrip preserves existing field and serializer policies, including int-null rejection | `OptionDefinitionPresenceTests.RoundTrip_OmissionAndExplicitZeroRemainDifferent`, `ExplicitZero_SurvivesDefaultValueIgnorePolicy`, `Read_PreservesCaseSensitivityAndAllExistingFields`, `DirectModel_InvalidIntJsonRetainsRejection`, `Read_RespectsExplicitNumberHandling`, `UnknownMemberDisallow_RemainsEnforced` | ✅ |
| AC-113 | Real global-choice MCP tool retains service warnings and parsed option presence in serialized output | `MetadataCreateChoiceWarningTests.ActualTool_PreservesServiceWarningsAndOptionPresence` | ✅ |


### Edge Cases

| Scenario | Input | Expected Output |
|----------|-------|-----------------|
| Create table with duplicate schema name | `SchemaName` already exists | `PpdsException` with `DUPLICATE_SCHEMA_NAME` |
| Delete table with dependent relationships | Table has 1:N relationships | Dry-run validates input only; the server decides whether a real delete succeeds |
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
    Task<MetadataLabelWriteResult> UpdateTableAsync(UpdateTableRequest request, IProgressReporter? reporter = null, CancellationToken ct = default);
    Task DeleteTableAsync(DeleteTableRequest request, IProgressReporter? reporter = null, CancellationToken ct = default);

    // Columns
    Task<CreateColumnResult> CreateColumnAsync(CreateColumnRequest request, IProgressReporter? reporter = null, CancellationToken ct = default);
    Task<MetadataLabelWriteResult> UpdateColumnAsync(UpdateColumnRequest request, IProgressReporter? reporter = null, CancellationToken ct = default);
    Task DeleteColumnAsync(DeleteColumnRequest request, IProgressReporter? reporter = null, CancellationToken ct = default);

    // Relationships
    Task<CreateRelationshipResult> CreateOneToManyAsync(CreateOneToManyRequest request, IProgressReporter? reporter = null, CancellationToken ct = default);
    Task<CreateRelationshipResult> CreateManyToManyAsync(CreateManyToManyRequest request, IProgressReporter? reporter = null, CancellationToken ct = default);
    Task UpdateRelationshipAsync(UpdateRelationshipRequest request, IProgressReporter? reporter = null, CancellationToken ct = default);
    Task DeleteRelationshipAsync(DeleteRelationshipRequest request, IProgressReporter? reporter = null, CancellationToken ct = default);

    // Choices
    Task<CreateChoiceResult> CreateGlobalChoiceAsync(CreateGlobalChoiceRequest request, IProgressReporter? reporter = null, CancellationToken ct = default);
    Task<MetadataLabelWriteResult> UpdateGlobalChoiceAsync(UpdateGlobalChoiceRequest request, IProgressReporter? reporter = null, CancellationToken ct = default);
    Task DeleteGlobalChoiceAsync(DeleteGlobalChoiceRequest request, IProgressReporter? reporter = null, CancellationToken ct = default);
    Task<int> AddOptionValueAsync(AddOptionValueRequest request, CancellationToken ct = default);
    Task<MetadataLabelWriteResult> UpdateOptionValueAsync(UpdateOptionValueRequest request, CancellationToken ct = default);
    Task DeleteOptionValueAsync(DeleteOptionValueRequest request, CancellationToken ct = default);
    Task ReorderOptionsAsync(ReorderOptionsRequest request, CancellationToken ct = default);
    Task UpdateStateValueAsync(UpdateStateValueRequest request, CancellationToken ct = default);

    // Status Reasons (#1160)
    Task<int> AddStatusReasonAsync(AddStatusReasonRequest request, IMetadataAuthoringProgressReporter? reporter = null, CancellationToken ct = default);
    Task<IReadOnlyList<StatusReasonInfo>> ListStatusReasonsAsync(string entityLogicalName, CancellationToken ct = default);
    Task<MetadataLabelWriteResult> UpdateStatusReasonAsync(UpdateStatusReasonRequest request, CancellationToken ct = default);
    Task RemoveStatusReasonAsync(RemoveStatusReasonRequest request, CancellationToken ct = default);

    // Alternate Keys
    Task<CreateKeyResult> CreateKeyAsync(CreateKeyRequest request, IProgressReporter? reporter = null, CancellationToken ct = default);
    Task DeleteKeyAsync(DeleteKeyRequest request, IProgressReporter? reporter = null, CancellationToken ct = default);
    Task ReactivateKeyAsync(ReactivateKeyRequest request, CancellationToken ct = default);
}
```

### MetadataLabelWriteResult

Returned by `UpdateTableAsync`, `UpdateColumnAsync`, `UpdateGlobalChoiceAsync`, `UpdateOptionValueAsync`, `UpdateStatusReasonAsync` and `UpdateColumnOptionAsync` (PPDS.Cli, `PPDS.Cli.Services.Metadata.Authoring`). `ValidationMessages` (`IReadOnlyList<ValidationMessage>`: `Field`, `Rule`, `Message`) is empty unless a requested label was written in the organization's base language while the label Dataverse shows the caller is a stored label in another language that reads different text; then it holds one `BASE_LANGUAGE_LABEL_ONLY` entry per such label (`NewLabel`, `DisplayName`, `PluralDisplayName` (table updates) or `Description`). `MetadataLabelWriteResult.None` is the empty result. See "Label languages" (AC-145, AC-147).

### OptionValueDeriver (shared — #1160 + #1161)

**Code:** `src/PPDS.Dataverse/Metadata/Authoring/OptionValueDeriver.cs`

Single, pure, unit-testable helper used by status-reason add, local-column option-add and initial local/global choice creation to choose an option value. Prefix resolution and reading/reserving option values happen in the service; the deriver only chooses.

```csharp
public static class OptionValueDeriver
{
    /// <summary>
    /// Chooses an option value. Throws MetadataValidationException on collision or missing inputs.
    /// </summary>
    /// <param name="explicitValue">--value, if supplied (wins).</param>
    /// <param name="publisherOptionPrefix">Publisher customizationoptionvalueprefix, if a solution was supplied for derivation.</param>
    /// <param name="existingValues">Values already on the target option set (statuscode set, or the column's local set).</param>
    public static int Derive(int? explicitValue, int? publisherOptionPrefix, IReadOnlyCollection<int> existingValues);
}
```

**Algorithm:**
1. `explicitValue` present → if `existingValues` contains it, throw `DUPLICATE_OPTION_VALUE`; else return it.
2. else `publisherOptionPrefix` present → `base = publisherOptionPrefix * 10_000`; return the lowest integer `>= base` not in `existingValues` (fills gaps, advances past collisions — never collides).
3. else → throw `MISSING_REQUIRED_FIELD` ("provide --value or --solution").

The service resolves `publisherOptionPrefix` from the solution's publisher (`customizationoptionvalueprefix`), reusing the publisher-lookup pattern already in `ResolvePublisherPrefixAsync` (extended to also read the option-value prefix).

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
| `INVALID_CONSTRAINT` | Type-specific constraint violation (e.g., negative MaxLength); also a global choice name that matches several stored names by case, or a `--label` that names different options in published and unpublished metadata | Fix constraint value; use the exact stored name; target by `--value` or publish first |
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
| `OPTION_NOT_FOUND` | A status reason / local option targeted by `--value` or `--label` does not resolve to an existing option on the set | List the set (`list-statusreasons` / inspect the column) and target an existing value or label |
| `MISSING_REQUIRED_FIELD` | An "exactly one of" pair has **neither** member supplied (`--value`/`--solution`, `--value`/`--label`, `--state`/`--state-code`); or an option / status-reason update requests nothing ("Nothing to update", CLI exit 8) | Supply exactly one of the pair; give `--new-label` or `--color` |
| `SDK_OPERATION_FAILED` | A Dataverse request failed (CLI exit 2). Makes no statement about whether a write was applied; a timed-out or cancelled insert says the outcome is unknown | Check the target (for example list the options) before retrying an insert |
| `WRITE_NOT_CONFIRMED` | The write request returned success, but what it carried could not be confirmed: the read-back failed, or a written label, description or colour is not stored as sent (AC-148); CLI exit 2. The message states what was written and that `--publish` was not attempted; `Context["requiresReauthentication"]` is true when the cause is an expired sign-in | Inspect the option; do not blindly re-run an add (a second option would be inserted); publish separately if needed |

All `ErrorCode`s above are carried on `MetadataValidationException`, which derives from `PpdsException` (Constitution D4): `MetadataValidationException : PpdsException`. `DUPLICATE_OPTION_VALUE` (existing) is reused for collisions on status reasons and local options.

### Recovery Strategies

- **Validation errors:** Fix input and retry — all validation errors include the specific field and constraint that failed
- **Dependency conflicts:** Inspect and resolve dependencies separately before deletion. Schema-delete previews discover them while retaining unresolved component identities; other server constraints remain unchecked
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

**Decision:** Follow the truncate pattern with dependency inspection before confirmation and again at the service delete boundary. `--dry-run` never mutates; `--force` skips confirmation only. `CONFIRMATION_REQUIRED` remains the non-interactive refusal without force.

**Rationale:** Consistency — users who've used `ppds truncate` already understand this pattern. The typed confirmation text (e.g., `DELETE TABLE account`) forces acknowledgment of exactly what's being destroyed. MCP surfaces don't expose delete at all, matching the existing read-only + non-destructive MCP design.

### Why no delete via MCP?

**Context:** MCP tools could expose delete operations with dry-run as a safeguard.

**Decision:** MCP tools support create and update only — no delete.

**Rationale:** MCP is designed for AI agent workflows. An AI agent accidentally deleting a table is catastrophic and not easily recoverable. The interactive confirmation pattern (typed text) doesn't translate well to MCP tool invocation. If an AI agent needs to delete schema, it can instruct the user to run the CLI command.

### Why canonical nouns `entity` / `attribute` / `optionset`? (#1159)

**Context:** The authoring surface grew organically with Maker-UI-flavored nouns (`table`, `column`, `choice`) alongside SDK-flavored ones (`relationship`, `key`, `optionset`). Two vocabularies for one surface confuse users and internal callers.

**Decision:** One canonical noun per schema object, aligned to the SDK's singular `*Metadata` type names: `entity` (`EntityMetadata`), `attribute` (`AttributeMetadata`), `optionset` (`OptionSetMetadata`). `relationship` and `key` already follow this; `table`/`column`/`choice` are the stragglers and become deprecation shims.

**Alternatives considered:**
- Keep Maker nouns (`table`/`column`/`choice`) as canonical — rejected: diverges from the SDK types the code already uses, and from the already-canonical `relationship`/`key`.
- Hard rename with no shims — rejected: breaks existing user scripts pre-v1 with no migration runway.

**Consequences:** Positive — one vocabulary, SDK-aligned, discoverable. Negative — three deprecation shims to carry until a future removal; a one-time sweep of internal consumers.

### Why verb-first subcommands and `--entity` flag for status reasons, not `entity <name> add-statusreason`? (#1160) — DECIDED: Form A (owner-ratified 2026-05-29)

**Context:** Both issue bodies (#1159, #1160) and the owner brief sketch `entity <name> add-statusreason` — a noun that takes a positional name *and then* hosts a verb. System.CommandLine (2.x, the new `Subcommands`/`SetAction` API this CLI uses) resolves a subcommand token *before* binding the parent's positional argument, so the literal `entity <name> <verb>` ordering (name first, verb second) is not natively parseable; only `entity <verb> …` is.

**Decision:** **Form A** — status-reason verbs are verb-first subcommands of `entity` (`entity add-statusreason`, `entity list-statusreasons`, …) that identify the entity with the `--entity <name>` flag. The bare `entity <name>` read lookup is preserved via the parent command's positional argument + default action; a recognized verb routes to the subcommand, a bare token routes to the lookup.

**Forms considered (owner ratified A on 2026-05-29):**
| Form | Example | Parseable | Trade-off |
|------|---------|-----------|-----------|
| **A — CHOSEN** | `entity add-statusreason --entity hsl_appt --label …` | yes | Consistent with `entity update/delete`, `attribute`, `key` (all use `--entity`). Verbose. |
| **B** | `entity add-statusreason hsl_appt --label …` | yes | Entity as positional on the verb; reads slightly oddly next to other positionals. |
| **C (issue literal)** | `entity hsl_appt add-statusreason --label …` | **not** natively in System.CommandLine | Requires a custom positional-then-dispatch parser on `entity`, sacrificing per-verb `--help`/validation. |

**Falsification:** a future System.CommandLine version supports positional-then-subcommand cleanly, or operator usage data shows Form A's `--entity` flag is a friction point.

**Amendment (#1208, 2026-06-10):** The falsification condition fired — operator usage showed Form A's flag-only surface is a real friction point (users type `entity <name> update …`, the parser can't route it, and the natural fallback `entity update <name> …` printed help instead of working). The `entity` authoring verbs now additionally accept Form B's positional `<entity>` on the verb subcommand, keeping `--entity` as a fully equivalent back-compat flag; when both are supplied they must agree or the parse errors. This is additive — Form A invocations are unchanged. Scope is `entity` verbs only: `attribute`/`key` keep `--entity` exclusively, since they have no positional read form to mirror and their verbs take other identifying flags (`--column`, `--name`).

### Why explicit `IsGlobal = false` for local choice columns? (#1161)

**Context:** `attribute create --type Choice --option …` failed with the Dataverse fault *"IsGlobal is not specified"*. Root cause: `BuildChoiceAttribute` constructed `new OptionSetMetadata()` for the inline (local) set without setting `IsGlobal`, and the SDK does not default it for inline option sets.

**Decision:** Inline/local option sets explicitly set `IsGlobal = false` and `OptionSetType = OptionSetType.Picklist`. Global attach (`--choice <name>`) continues to set `IsGlobal = true` with the referenced name.

**Consequences:** Local choice columns work as designed; the global-vs-local intent is explicit at the construction site rather than relying on SDK defaults that don't exist.

### Why one shared `OptionValueDeriver`? (#1160 + #1161)

**Context:** Both status-reason add and local-column option-add must turn `--value`/`--solution` into a concrete option value with identical collision and prefix semantics. Two copies would drift.

**Decision:** A single pure static `OptionValueDeriver.Derive(explicitValue, publisherOptionPrefix, existingValues)` is the only place that chooses a value. Owner explicitly required one helper. The service supplies the prefix (from `customizationoptionvalueprefix`) and the current values; the deriver chooses; collision behavior is explicit (explicit value collides → throw; derived value advances past collisions and fills gaps).

**Consequences:** Identical behavior across both surfaces, one set of unit tests (AC-57), trivially extensible to global option sets later (Roadmap).

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
| 2026-11-09 | Prepare the approved finite State/Status/GUID column-label transition, strict identity/minimal-payload/editability policy and lifecycle/privacy safeguards; preserve the common224 BEFORE proof and hold fixed validation (AC-123 through AC-130). |
| 2026-11-09 | Add strict dependency preview/recheck, safe read recovery classification and truthful wider scope with AC-114–AC-122; 277 focused cases and both corrected combined-workflow regressions passed, with composed ordinary unit validation. |
| 2026-11-12 | Add bounded typed Dataverse fault codes to status/local-option insertion errors without exposing server text; verify service, CLI JSON and MCP helper privacy (AC-131–132). Literal issue #1531 underlying-message criterion remains qualified. |
| 2026-11-16 | Follow-ups for #1530/#1531/#1535: global choice names resolve to the stored name on a not-found fault only, preserving every previous failure outcome except case ambiguity; delete confirms and deletes exactly the previewed stored name; update/remove targeting and global color readback read drafts, with a published/draft label agreement check; legacy `--options` accepts bare labels and rejects malformed or conflicting inputs for Choice columns; add-option dry-run marks omitted values unallocated (AC-133-AC-140, AC-72 amended). |
| 2026-11-17 | `optionset update` reads the choice as-if-published so unrequested properties keep their unpublished values (AC-141). |
| 2026-11-17 | State the label-language rule; option, status-reason, state-value and global-choice updates merge labels and re-send unrequested option labels in every language (AC-142). |
| 2026-11-17 | Recognise the global choice "not found" fault by its ObjectDoesNotExist code, with the English wording as fallback (AC-133/AC-134 amended); input-only dry-runs say the global choice name was not looked up (AC-143). |
| 2026-11-17 | MCP reports metadata validation failures with their error code, message and field instead of a generic internal error (AC-144). |
| 2026-11-17 | Review follow-ups: `ENGLISH_LABEL_ONLY` notices and "(English, LCID 1033)" help (AC-145); empty translations not re-sent and no null `Label` (AC-142 amended); colour readback merges labels; MCP update-choice dry-run reports `globalChoiceNameChecked`; fault code marked as an assumption pending live tests (AC-133). |
| 2026-11-17 | Second review: notice names the label Dataverse shows (not "your language"), is suppressed when that label already reads the requested text, and appears in the TUI; MCP notices use explicit `field`/`rule`/`message`; help suffix on every update label option; `MetadataLabelWriteResult` signatures and type documented (AC-145 amended). Live multi-language checks need `PPDS_TEST_SECOND_LCID` and are skipped without it. |
| 2026-11-17 | Live run on a development organisation: the not-found fault code `0x80040217` is confirmed (AC-133/AC-134); option colours for existing options are written through `UpdateOptionValue`'s `Color` parameter because `UpdateOptionSet`/`UpdateAttribute` ignore them (AC-146; AC-71, AC-88, AC-135, AC-142 amended); global choice label edits are visible without publishing (AC-141 note); `--description ""` clears the English text. |
| 2026-11-17 | Table and column label updates merge labels and report `ENGLISH_LABEL_ONLY` notices on every surface; table label updates read the table first (AC-147; AC-93, AC-128, AC-130 amended). |
| 2026-11-17 | Review round: colours ride on the insert/update and are read back to confirm (AC-146 rewritten; AC-71, AC-72, AC-84, AC-88, AC-97, AC-103, AC-135 amended); updates send no unrequested option label (AC-142 rewritten); empty solution names are sent as none; table label read is published and keeps auth/connection/timeout classification (AC-147 amended); every "observed live" statement now names the live test that asserts it, or says it is recorded only. |
| 2026-11-17 | Fourth review: failures after a committed option write are `WRITE_NOT_CONFIRMED` stating what was written (AC-84, AC-87, AC-97, AC-100, AC-103, AC-146 amended); colours are passed through verbatim (asserted live); updates that request nothing are refused; empty descriptions clear; blank solutions are sent as none on every option message; AC-72 aligned with AC-142; table-notice staleness limit stated. |
| 2026-11-17 | Fifth review: AC-73, AC-98 scope and AC-105 aligned with `WRITE_NOT_CONFIRMED`; error codes defined (`SDK_OPERATION_FAILED` makes no statement about the write; `WRITE_NOT_CONFIRMED` exit 2 and the three moved exits); indeterminate inserts, publish failures after a write and cache invalidation on delete/reorder/remove specified (AC-146 amended); verbatim colour storage asserted for local, status and state options. |
| 2026-11-17 | Second-language live run (French 1036 provisioned in the development organisation): `MergeLabels` keeps the French label of global options, local options, status reasons, tables and columns, and an empty English description keeps the French one (AC-142, AC-147 now asserted live); `UserLocalizedLabel` for a 1033 caller is the 1033 entry even when its text is empty, and a French-only create label is stored under 1033 too (AC-145 note). |
| 2026-11-17 | Base-language labels: every label is written in `organization.languagecode` instead of 1033 and confirmed by a read-back (`WRITE_NOT_CONFIRMED`), after a French-base organization ignored, overwrote or rejected 1033 labels (D1–D3); the notice is renamed `BASE_LANGUAGE_LABEL_ONLY` and requires a stored non-base label; table, column and choice dry-runs return the notices; help says "in the organization's base language" (AC-142, AC-145, AC-147 rewritten; AC-148 added; AC-123, AC-146, `WRITE_NOT_CONFIRMED` amended). |
| 2026-11-09 | Bound sixteen SDK/catalog user-message paths to service-owned action/target text; retain diagnostic causes and classifications, verify synthetic MCP privacy and update the intentional CLI message contract (AC-103 through AC-105). |
| 2026-11-08 | Preserve covered color cancellation and normalize the selected acquisition boundaries with unchanged CLI exits; protect authentication diagnostic details at the MCP error boundary (AC-97 through AC-102). |
| 2026-11-03 | #1532: document actual delete-preview scope, remove CLI dependency/removability assurances, add additive JSON scope and AC-66–AC-69. Correct AC-11 to incomplete: dependency discovery is still unimplemented. |
| 2026-11-03 | #1531: draft-aware status/local option allocation and local color readback, published public lists retained. Added AC-70–AC-73. |
| 2026-03-31 | Initial spec |
| 2026-04-01 | Post-implementation cleanup: fixed CodeQL findings, completed TUI choice editing, replaced Extension webview stubs with VS Code input collection, updated AC statuses |
| 2026-05-29 | Surface rationalization (#1159 — canonical `entity`/`attribute`/`optionset` nouns + `table`/`column`/`choice` deprecation shims), status reason management (#1160 — add/list/update/remove on `entity`), local Choice/OptionSet column fix + local option management (#1161 — `IsGlobal=false`), shared `OptionValueDeriver`. Added AC-37–AC-58. |
| 2026-06-10 | `entity` authoring verbs accept positional `<entity>` alongside `--entity` (#1208) — amended the #1160 Form A decision (Form B accepted additively), rewrote the §"CLI Surface" rationale, added AC-61/AC-62. `attribute`/`key` unchanged. |
| 2026-11-07 | Resolve global choice references during column creation using the target environment's stored name (#1535, AC-78 through AC-81). |
| 2026-11-07 | #1534 added primary ID label updates with table-key verification and locale merging. Added AC-74–AC-77. |

---

## Roadmap

- Extend `OptionValueDeriver` (publisher-prefix × 10,000 derivation) to `optionset add-option` (global option sets) for uniform value behavior across global and local/status sets.
- Surface status-reason and local-option management through TUI / Extension / MCP (this iteration ships the Application Service + CLI; the service is surface-agnostic per A1/A2).
- Remove the `table`/`column`/`choice` deprecation shims in a future release once internal and external callers have migrated.

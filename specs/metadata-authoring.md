# Metadata Authoring

**Status:** Implemented (extending — #1159/#1160/#1161 ratified, in implementation; AC-37–AC-60 reach ✅ as their tests land; #1208 positional `<entity>` shipped, AC-61/AC-62)
**Last Updated:** 2026-06-10
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
- **Adopting publisher-prefix value derivation on `optionset add-option` (global)** — global option-set option values retain the existing SDK auto-assignment behavior in this iteration. The shared `OptionValueDeriver` is applied to status reasons (#1160) and local column options (#1161) only; extending it to global option sets is a follow-up (see Roadmap).
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

**Local (column-scoped) option sets — #1161 bug fix.** When a `Choice`/`Choices` column is created with inline options (no global choice referenced), the SDK requires `OptionSetMetadata.IsGlobal` to be **explicitly** set to `false`; omitting it produces the Dataverse error *"IsGlobal is not specified"*. `BuildChoiceAttribute`/`BuildMultiSelectChoiceAttribute` MUST set `IsGlobal = false` and `OptionSetType = OptionSetType.Picklist` on the inline `OptionSetMetadata`. Local option add/remove/update reuse the existing `AddOptionValueAsync`/`UpdateOptionValueAsync`/`DeleteOptionValueAsync` methods, which already carry optional `EntityLogicalName` + `AttributeLogicalName` to scope the operation to a column's local set.

#### Status Reason Operations (#1160)

Status reasons are the option values of an entity's `statuscode` attribute (a `StatusAttributeMetadata`). Each value belongs to a state (`statecode`: 0 = Active, 1 = Inactive). Inserting a status reason requires the SDK `InsertStatusValueRequest` (which carries `StateCode`); the generic `InsertOptionValueRequest` cannot create them. List/update/remove reuse the entity-scoped option-value paths against `statuscode`.

```csharp
Task<int> AddStatusReasonAsync(AddStatusReasonRequest request, IMetadataAuthoringProgressReporter? reporter = null, CancellationToken ct = default);
Task<IReadOnlyList<StatusReasonInfo>> ListStatusReasonsAsync(string entityLogicalName, CancellationToken ct = default);
Task UpdateStatusReasonAsync(UpdateStatusReasonRequest request, CancellationToken ct = default);
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
| Local choice column sets `OptionSetMetadata.IsGlobal = false` explicitly | Choice/Choices columns with inline options | `IsGlobal` SDK fault (#1161) — prevented by fix |
| Exactly one of `--value` / `--solution` (neither → missing; both → invalid) | `add-statusreason`, `attribute add-option` | `MISSING_REQUIRED_FIELD` (neither) / `INVALID_CONSTRAINT` (both) |
| Exactly one of `--value` / `--label` (neither → missing; both → invalid) | `update-/remove-statusreason`, `attribute update-/remove-option` | `MISSING_REQUIRED_FIELD` (neither) / `INVALID_CONSTRAINT` (both) |
| Exactly one of `--state` / `--state-code` (neither → missing; both → invalid) | `add-statusreason` | `MISSING_REQUIRED_FIELD` (neither) / `INVALID_CONSTRAINT` (both) |
| `--choice` mutually exclusive with `--option`/`--options`/`--options-file` | `attribute create --type Choice` | `INVALID_CONSTRAINT` |
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
ppds metadata entity create --solution <s> --name <schema> --display-name <n> --plural-name <n> --ownership <UserOwned|OrganizationOwned> [options]
ppds metadata entity update <entity> --solution <s> [property flags]
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
ppds metadata attribute update --solution <s> --entity <name> --column <name> [property flags]
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

- `--option` (repeatable) uses `Label[:Value][:#Color]` — label required, value/color optional; omitted values are derived (auto-assigned by the SDK at create time when no `--solution` derivation applies, matching today's local-create behavior). `--options-file` is a JSON array of `{ "label", "value"?, "color"? }`. `--options` (legacy `"Label1=1,Label2=2"` CSV) remains accepted as a synonym for `--option` and maps to the same local set.
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
ppds metadata optionset add-option --solution <s> --name <name> --label <label> [--value <int>] [--color <hex>]
ppds metadata optionset update-option --solution <s> --name <name> --value <int> --label <new-label>
ppds metadata optionset remove-option --solution <s> --name <name> --value <int> [--force]
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

**Shared flags:** `--dry-run`, `--force` (delete/remove only), `--publish` (authoring verbs that change live metadata), `--profile`, `--environment`, `--json`

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
| AC-06 | `CreateManyToManyAsync` creates an N:N relationship with intersect entity. `IntersectEntitySchemaName` is set on the SDK `CreateManyToManyRequest` (not the metadata) and defaults to `SchemaName` when caller omits it. Issue #1008. | `MetadataAuthoringServiceTests.CreateManyToManyAsync_OmittedIntersect_DefaultsToSchemaNameOnSdkRequest`, `MetadataAuthoringServiceTests.CreateManyToManyAsync_ExplicitIntersect_PassesThroughOnSdkRequest`, `SchemaValidatorTests.ValidateCreateManyToManyRequest_ResolvedIntersectMissing_ThrowsMissingRequiredField`, `MetadataRelationshipCreateE2ETests.CreateManyToMany_OmittedIntersect_DefaultsFromName_Succeeds` | ✅ |
| AC-07 | `CreateGlobalChoiceAsync` creates a global option set with initial values | `CacheInvalidationTests.CreateGlobalChoice_InvalidatesGlobalOptionSets` | ✅ |
| AC-08 | `AddOptionValueAsync` adds a value to an existing global or local option set (local path is now load-bearing for #1161 — AC-55) | `MetadataAuthoringServiceTests.AddOptionValueAsync_GlobalAndLocal_InsertsViaInsertOptionValue` | ❌ |
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
| AC-58 | Authoring verbs that change live metadata honor `--publish`, publishing the affected entity after the change (wired on `attribute create`/`add-/update-/remove-option` and status-reason verbs via `PublishEntityInternalAsync`) | — (wired; live-publish covered by Integration) | ❌ |
| AC-59 | `ppds metadata --help` lists the canonical nouns and marks deprecated nouns (`table`, `column`, `choice`, `choices`) `(deprecated)` in their one-line description | `MetadataDeprecationTests.MetadataHelp_MarksDeprecatedNouns` | ❌ |
| AC-60 | `entity --help` lists the status-reason subcommands; `attribute create --help` documents `--option`/`--choice`/derivation; each new subcommand exposes accurate `--help` | `MetadataHelpCoverageTests.NewSubcommands_HaveHelp` | ❌ |
| AC-61 | All six `entity` authoring verbs (`update`, `delete`, `add-statusreason`, `list-statusreasons`, `update-statusreason`, `remove-statusreason`) accept a positional `<entity>`; `--entity` still works (#1208) | `MetadataEntityCommandTests.AuthoringVerb_ParsesPositionalEntity` + `AuthoringVerb_StillAcceptsEntityFlag` | ✅ |
| AC-62 | When both the positional `<entity>` and `--entity` are supplied they must agree (case-insensitive) or the parse fails with a "disagree" error; supplying neither is a parse error (#1208) | `MetadataEntityCommandTests.AuthoringVerb_PositionalAndFlagAgreeing_HasNoErrors` + `AuthoringVerb_PositionalAndFlagDisagreeing_HasErrors` + `AuthoringVerb_MissingEntity_HasErrors` | ✅ |

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

    // Status Reasons (#1160)
    Task<int> AddStatusReasonAsync(AddStatusReasonRequest request, IMetadataAuthoringProgressReporter? reporter = null, CancellationToken ct = default);
    Task<IReadOnlyList<StatusReasonInfo>> ListStatusReasonsAsync(string entityLogicalName, CancellationToken ct = default);
    Task UpdateStatusReasonAsync(UpdateStatusReasonRequest request, CancellationToken ct = default);
    Task RemoveStatusReasonAsync(RemoveStatusReasonRequest request, CancellationToken ct = default);

    // Alternate Keys
    Task<CreateKeyResult> CreateKeyAsync(CreateKeyRequest request, IProgressReporter? reporter = null, CancellationToken ct = default);
    Task DeleteKeyAsync(DeleteKeyRequest request, IProgressReporter? reporter = null, CancellationToken ct = default);
    Task ReactivateKeyAsync(ReactivateKeyRequest request, CancellationToken ct = default);
}
```

### OptionValueDeriver (shared — #1160 + #1161)

**Code:** `src/PPDS.Dataverse/Metadata/Authoring/OptionValueDeriver.cs`

Single, pure, unit-testable helper that both status-reason add and local-column option-add use to choose an option value. Owner-mandated single source of truth so both surfaces behave identically. Pure logic: prefix resolution and reading the current option values happen in the service; the deriver only chooses.

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
| `OPTION_NOT_FOUND` | A status reason / local option targeted by `--value` or `--label` does not resolve to an existing option on the set | List the set (`list-statusreasons` / inspect the column) and target an existing value or label |
| `MISSING_REQUIRED_FIELD` | An "exactly one of" pair has **neither** member supplied (`--value`/`--solution`, `--value`/`--label`, `--state`/`--state-code`) | Supply exactly one of the pair |

All `ErrorCode`s above are carried on `MetadataValidationException`, which derives from `PpdsException` (Constitution D4): `MetadataValidationException : PpdsException`. `DUPLICATE_OPTION_VALUE` (existing) is reused for collisions on status reasons and local options.

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
| 2026-03-31 | Initial spec |
| 2026-04-01 | Post-implementation cleanup: fixed CodeQL findings, completed TUI choice editing, replaced Extension webview stubs with VS Code input collection, updated AC statuses |
| 2026-05-29 | Surface rationalization (#1159 — canonical `entity`/`attribute`/`optionset` nouns + `table`/`column`/`choice` deprecation shims), status reason management (#1160 — add/list/update/remove on `entity`), local Choice/OptionSet column fix + local option management (#1161 — `IsGlobal=false`), shared `OptionValueDeriver`. Added AC-37–AC-58. |
| 2026-06-10 | `entity` authoring verbs accept positional `<entity>` alongside `--entity` (#1208) — amended the #1160 Form A decision (Form B accepted additively), rewrote the §"CLI Surface" rationale, added AC-61/AC-62. `attribute`/`key` unchanged. |

---

## Roadmap

- Extend `OptionValueDeriver` (publisher-prefix × 10,000 derivation) to `optionset add-option` (global option sets) for uniform value behavior across global and local/status sets.
- Surface status-reason and local-option management through TUI / Extension / MCP (this iteration ships the Application Service + CLI; the service is surface-agnostic per A1/A2).
- Remove the `table`/`column`/`choice` deprecation shims in a future release once internal and external callers have migrated.

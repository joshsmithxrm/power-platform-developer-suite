# Model-Driven App Navigation Management

**Status:** Draft
**Last Updated:** 2026-06-01
**Code:** [src/PPDS.Cli/Services/ModelDrivenApps/](../src/PPDS.Cli/Services/ModelDrivenApps/), [src/PPDS.Cli/Commands/ModelDrivenApps/](../src/PPDS.Cli/Commands/ModelDrivenApps/)
**Surfaces:** CLI

---

## Overview

CLI commands for managing model-driven app (MDA) navigation — listing apps, inspecting sitemaps, adding/removing tables from navigation, and controlling form/view/chart visibility. All modifications operate on existing apps; app creation is out of scope.

### Goals

- **Inspect MDAs**: List apps, view metadata, display sitemap navigation structure
- **Manage navigation**: Add/remove tables from app sitemap with group/area control
- **Control component visibility**: Set which forms/views/charts appear for each table
- **Validate changes**: XSD validation before any sitemap modification

### Non-Goals

- Creating new model-driven apps (use Power Apps maker portal)
- Managing app settings beyond navigation (security roles, business rules)
- Canvas app management (different app type)

---

## Architecture

```
┌─────────────────────────────────────────────────────────────────┐
│                         CLI Commands                            │
│  model-driven-app list | get | sitemap | add-table | ...        │
└─────────────────────────┬───────────────────────────────────────┘
                          │
                          ▼
┌─────────────────────────────────────────────────────────────────┐
│                   ModelDrivenAppService                         │
│  - ListAppsAsync()                                              │
│  - GetAppAsync(name)                                            │
│  - GetSitemapAsync(name)                                        │
│  - SetSitemapXmlAsync(name, xml)  ← internal + public command   │
│  - AddTableAsync(name, entities[], group?, area?, title?)       │
│  - RemoveTableAsync(name, entity)                               │
│  - SetFormsAsync(name, entity, forms[] | all)                   │
│  - SetViewsAsync(name, entity, views[] | all)                   │
│  - SetChartsAsync(name, entity, charts[] | all)                 │
└─────────────────────────┬───────────────────────────────────────┘
                          │
          ┌───────────────┼───────────────┐
          ▼               ▼               ▼
┌─────────────────┐ ┌─────────────────┐ ┌─────────────────┐
│  Dataverse      │ │ Sitemap XSD     │ │  Solution       │
│  Pool           │ │ Validator       │ │  Service        │
└─────────────────┘ └─────────────────┘ └─────────────────┘
```

### Components

| Component | Responsibility |
|-----------|----------------|
| `ModelDrivenAppService` | All business logic for MDA operations |
| `IModelDrivenAppService` | Interface for dependency injection |
| `SitemapSchemaResources` | Loads bundled XSD files from embedded resources |
| `SitemapXmlValidator` | Validates sitemap XML against XSD schema |
| `ModelDrivenAppErrorCodes` | Domain-specific error codes |

### Dependencies

- Uses patterns from: [architecture.md](./architecture.md)
- Uses: [connection-pooling.md](./connection-pooling.md) for Dataverse access
- Uses: [solutions.md](./solutions.md) for `--solution` flag behavior

---

## Specification

### Command Structure

```
ppds model-driven-app
├── list                    # List all MDAs in environment
├── get --app <name>        # Show app metadata + component counts
├── sitemap --app <name>    # Display sitemap navigation structure
├── set-sitemap-xml --app <name> --xml <path>   # Set sitemap from file
├── add-table <entities...> --app <name> [--group] [--area] [--title] [--solution] [--publish]
├── remove-table --app <name> --entity <name> [--solution] [--publish]
├── set-forms --app <name> --entity <name> (--all | --form <name>...) [--solution] [--publish]
├── set-views --app <name> --entity <name> (--all | --view <name>...) [--solution] [--publish]
└── set-charts --app <name> --entity <name> (--all | --chart <name>...) [--solution] [--publish]
```

### Shared Options

| Option | Type | Description |
|--------|------|-------------|
| `--app <name>` | string | App display name or unique name (required on all except `list`) |
| `--solution <name>` | string | Add app (type 80) + sitemap (type 62) to solution if not present |
| `--publish` | flag | Publish the app after modification (default: false) |

### Core Requirements

1. All read operations (`list`, `get`, `sitemap`) work against the current published state
2. All write operations validate sitemap XML against bundled XSD before PATCH
3. Entity additions modify sitemap XML (not `AddAppComponents` — see Design Decisions)
4. Form/view/chart additions use `AddAppComponents` with correct `@odata.type`
5. `--publish` flag behavior matches existing commands (publish if present, skip if not)
6. All write operations accept `IProgressReporter` for status feedback (A3 compliance)
7. All errors from service methods wrapped in `PpdsException` with `ErrorCode` (D4 compliance)

### Primary Flows

**`add-table` flow:**

1. Resolve app by name → get `appmoduleid`, `appmoduleidunique`, sitemap ID
2. Fetch current `sitemapxml` from sitemap record
3. Parse XML, locate target Area:
   - If `--area` specified: find or create Area with that name
   - If no `--area`: use first existing Area, or create default "Main" Area if sitemap is empty
4. Locate or create Group (by `--group` or first Group in Area)
5. For each entity:
   - Verify entity exists in environment (error `EntityNotFound` if not)
   - Verify entity not already in sitemap (error `EntityAlreadyInApp` if present)
   - Fetch entity metadata for localized display name (if `--title` not provided)
   - Generate unique SubArea ID (`subarea_{guid}`)
   - Insert `<SubArea Id="..." Entity="<logical>" Title="..." />`
6. Validate modified XML against XSD (error `InvalidSitemapXml` on failure)
7. PATCH sitemap record with new `sitemapxml`
8. If `--solution`: add appmodule (80) + sitemap (62) to solution
9. If `--publish`: `PublishXmlRequest` for the app

**`remove-table` flow:**

1. Resolve app → get IDs
2. Fetch sitemap, parse XML
3. Find `<SubArea>` with matching `Entity` attribute (error `EntityNotInApp` if not found)
4. Remove the `<SubArea>` element
5. Call `RemoveAppComponents` to remove any forms/views/charts for that entity
6. Call `RemoveAppComponents` to remove the entity itself (type 1)
7. Validate and PATCH sitemap
8. If `--solution` / `--publish`: same as above

**`set-forms` flow (similar for views/charts):**

1. Resolve app → verify entity is in sitemap (error `EntityNotInApp` if not)
2. If `--all`: call `RemoveAppComponents` for all explicit forms of that entity (resets to "include all" mode)
3. If `--form`:
   - Call `RemoveAppComponents` for all existing explicit forms of that entity (reset first)
   - Lookup form IDs by name for the entity (error `ComponentNotFound` if any not found)
   - Call `AddAppComponents` with `@odata.type: #Microsoft.Dynamics.CRM.systemform`
4. If `--solution` / `--publish`: same as above

### Partial Failure Behavior

Write operations are not transactional. If a later step fails after an earlier mutation succeeds:
- Sitemap PATCH succeeds, then solution add fails → sitemap change persists, solution add can be retried
- Sitemap PATCH succeeds, then publish fails → sitemap change persists, publish can be retried manually

Error messages include recovery guidance (e.g., "Sitemap updated. Publish failed: {reason}. Retry with: ppds publish...").

### CLI Surface

#### `list` Command

```bash
ppds model-driven-app list
```

Output (text format):
```
Name                  Unique Name           Components
─────────────────────────────────────────────────────────
Sales Hub             salesportal           45
Customer Service Hub  msdyn_customerservice 38
PPDS MDA              new_PPDSMDA           2
```

#### `get` Command

```bash
ppds model-driven-app get --app "PPDS MDA"
```

Output:
```
Name:           PPDS MDA
Unique Name:    new_PPDSMDA
App ID:         961fa5c7-385e-f111-a826-0022480af817
Description:    (none)
Publisher:      Default Publisher

Components:
  Entities:     1
  Forms:        0 (explicit)
  Views:        0 (explicit)
  Charts:       0 (explicit)
  Sitemap:      1
```

#### `sitemap` Command

```bash
ppds model-driven-app sitemap --app "PPDS MDA"
```

Output:
```
Area: Area1
└── Group: (default)
    └── Account
```

#### `add-table` Command

```bash
# Add single table
ppds model-driven-app add-table contact --app "PPDS MDA"

# Add multiple tables to a group
ppds model-driven-app add-table hsl_veterinarian hsl_appointment \
  --app "PPDS MDA" \
  --group "Veterinary" \
  --publish

# Add with custom title and area
ppds model-driven-app add-table hsl_diagnosis \
  --app "PPDS MDA" \
  --area "Clinical" \
  --group "Records" \
  --title "Patient Diagnoses"
```

#### `set-forms` / `set-views` / `set-charts` Commands

```bash
# Include all forms
ppds model-driven-app set-forms --app "PPDS MDA" --entity account --all

# Include specific forms only
ppds model-driven-app set-forms --app "PPDS MDA" --entity account \
  --form "Account" \
  --form "Quick Create"
```

### Constraints

- `--all` and `--form`/`--view`/`--chart` are mutually exclusive
- At least one of `--all` or `--form`/`--view`/`--chart` is required
- Entity must be in the app's sitemap before setting forms/views/charts
- Sitemap XML must validate against XSD before any write

### Validation Rules

| Field | Rule | Error |
|-------|------|-------|
| `--app` | Must resolve to existing appmodule | `AppNotFound` |
| `--entity` | Must exist in environment | `EntityNotFound` |
| `--entity` (for add-table) | Must not already be in app's sitemap | `EntityAlreadyInApp` |
| `--entity` (for remove-table, set-*) | Must be in app's sitemap | `EntityNotInApp` |
| `--form` | Must exist for the entity | `ComponentNotFound` |
| `--view` | Must exist for the entity | `ComponentNotFound` |
| `--chart` | Must exist for the entity | `ComponentNotFound` |
| `--solution` | Must exist in environment | `SolutionNotFound` |
| `--xml` | Must be valid sitemap XML | `InvalidSitemapXml` |
| `--all` / `--form` | Mutually exclusive; at least one required | `InvalidArguments` |

---

## Acceptance Criteria

| ID | Criterion | Test | Status |
|----|-----------|------|--------|
| AC-01 | `ppds model-driven-app list` returns all model-driven apps in the environment | `ListCommand_ReturnsAllApps` | 🔲 |
| AC-02 | `ppds model-driven-app get --app <name>` returns app name, unique name, ID, description, publisher, and component counts | `GetCommand_ShowsMetadata` | 🔲 |
| AC-03 | `ppds model-driven-app sitemap --app <name>` returns hierarchical Area → Group → SubArea structure | `SitemapCommand_DisplaysStructure` | 🔲 |
| AC-04 | `set-sitemap-xml` validates XML against bundled XSD before PATCH | `SetSitemapXml_ValidatesSchema` | 🔲 |
| AC-05 | `set-sitemap-xml` with invalid XML returns error with line number and element name | `SetSitemapXml_InvalidXml_ReturnsError` | 🔲 |
| AC-06 | `set-sitemap-xml` with valid XML PATCHes the sitemap record | `SetSitemapXml_PatchesSitemap` | 🔲 |
| AC-07 | `add-table` with single entity adds SubArea to sitemap | `AddTable_SingleEntity` | 🔲 |
| AC-08 | `add-table` with multiple entities adds all as SubAreas | `AddTable_MultipleEntities` | 🔲 |
| AC-09 | `add-table --group "X"` with non-existent group creates the group | `AddTable_CreatesNewGroup` | 🔲 |
| AC-10 | `add-table --group "X"` with existing group adds to that group | `AddTable_UsesExistingGroup` | 🔲 |
| AC-11 | `add-table --area "X"` with non-existent area creates the area | `AddTable_CreatesNewArea` | 🔲 |
| AC-12 | `add-table` without `--area` uses first existing area | `AddTable_UsesFirstArea` | 🔲 |
| AC-13 | `add-table` on empty sitemap creates default Area and Group | `AddTable_EmptySitemap` | 🔲 |
| AC-14 | `add-table` without `--title` uses entity's localized DisplayName | `AddTable_TitleDefault` | 🔲 |
| AC-15 | `add-table --solution` adds appmodule (type 80) to solution | `AddTable_AddsSolutionAppModule` | 🔲 |
| AC-16 | `add-table --solution` adds sitemap (type 62) to solution | `AddTable_AddsSolutionSitemap` | 🔲 |
| AC-17 | `add-table --publish` calls PublishXmlRequest after modification | `AddTable_PublishesApp` | 🔲 |
| AC-18 | `remove-table` removes SubArea from sitemap XML | `RemoveTable_RemovesSubArea` | 🔲 |
| AC-19 | `remove-table` removes explicit form/view/chart components via RemoveAppComponents | `RemoveTable_RemovesExplicitComponents` | 🔲 |
| AC-20 | `remove-table` removes entity component (type 1) via RemoveAppComponents | `RemoveTable_RemovesEntityComponent` | 🔲 |
| AC-21 | `set-forms --all` removes explicit form selections (enables include-all mode) | `SetForms_AllMode` | 🔲 |
| AC-22 | `set-forms --form "X"` removes existing explicit forms then adds specified forms | `SetForms_SpecificMode` | 🔲 |
| AC-23 | `set-views --all` removes explicit view selections | `SetViews_AllMode` | 🔲 |
| AC-24 | `set-views --view "X"` removes existing explicit views then adds specified views | `SetViews_SpecificMode` | 🔲 |
| AC-25 | `set-charts --all` removes explicit chart selections | `SetCharts_AllMode` | 🔲 |
| AC-26 | `set-charts --chart "X"` removes existing explicit charts then adds specified charts | `SetCharts_SpecificMode` | 🔲 |
| AC-27 | `set-*` with `--all` and `--form`/`--view`/`--chart` returns mutual exclusion error | `SetComponents_MutuallyExclusive` | 🔲 |
| AC-28 | `set-*` with entity not in app returns `EntityNotInApp` error | `SetComponents_EntityNotInApp_Error` | 🔲 |
| AC-29 | `set-*` with neither `--all` nor specific component returns error | `SetComponents_NeitherOption_Error` | 🔲 |
| AC-30 | `ppds model-driven-app --help` lists all 9 subcommands | `Help_ListsSubcommands` | 🔲 |
| AC-31 | Each subcommand responds to `--help` with usage information | `Help_SubcommandHelp` | 🔲 |
| AC-32 | `set-forms`/`set-views`/`set-charts` help text explains that `--all` or at least one component is required | `Help_SetComponentsRequirement` | 🔲 |

### Edge Cases

| Scenario | Input | Expected Output |
|----------|-------|-----------------|
| App not found | `--app "NonExistent"` | Error: `AppNotFound` with list of available apps |
| Entity already in app | `add-table account` (when account exists) | Error: `EntityAlreadyInApp` |
| Entity not in environment | `add-table fake_entity` | Error: `EntityNotFound` |
| Empty sitemap | `add-table contact` on app with no Areas | Creates "Main" Area, default Group, adds SubArea |
| Form not found | `--form "Fake Form"` | Error: `ComponentNotFound` with list of available forms |
| Entity not in app (remove) | `remove-table fake_entity` | Error: `EntityNotInApp` |
| Entity not in app (set-*) | `set-forms --entity fake --all` | Error: `EntityNotInApp` |
| Solution not found | `--solution "NonExistent"` | Error: `SolutionNotFound` |
| Solution component already present | `--solution "MySolution"` (already has app) | No-op (idempotent) |
| Malformed XML (not well-formed) | `set-sitemap-xml` with `<SiteMap><Area>` | Error: XML parse error |
| Invalid XML (well-formed but wrong schema) | `set-sitemap-xml` with `<SiteMap><Foo/>` | Error: XSD validation error with element name |

---

## Core Types

### IModelDrivenAppService

```csharp
public interface IModelDrivenAppService
{
    Task<IReadOnlyList<ModelDrivenAppSummary>> ListAppsAsync(CancellationToken ct);
    Task<ModelDrivenAppDetails> GetAppAsync(string appName, CancellationToken ct);
    Task<SitemapStructure> GetSitemapAsync(string appName, CancellationToken ct);
    
    Task SetSitemapXmlAsync(string appName, string xml, SetSitemapOptions options, IProgressReporter? progress, CancellationToken ct);
    
    Task AddTableAsync(string appName, IReadOnlyList<string> entities, AddTableOptions options, IProgressReporter? progress, CancellationToken ct);
    Task RemoveTableAsync(string appName, string entity, ModifyOptions options, IProgressReporter? progress, CancellationToken ct);
    
    Task SetFormsAsync(string appName, string entity, ComponentSelectionOptions options, IProgressReporter? progress, CancellationToken ct);
    Task SetViewsAsync(string appName, string entity, ComponentSelectionOptions options, IProgressReporter? progress, CancellationToken ct);
    Task SetChartsAsync(string appName, string entity, ComponentSelectionOptions options, IProgressReporter? progress, CancellationToken ct);
}
```

### AddTableOptions

```csharp
public record AddTableOptions(
    string? Group,
    string? Area,
    string? Title,
    string? Solution,
    bool Publish);
```

### ModifyOptions

```csharp
public record ModifyOptions(
    string? Solution,
    bool Publish);
```

### SetSitemapOptions

```csharp
public record SetSitemapOptions(
    string? Solution,
    bool Publish);
```

### ComponentSelectionOptions

```csharp
public record ComponentSelectionOptions(
    bool All,
    IReadOnlyList<string> ComponentNames,
    string? Solution,
    bool Publish);
```

---

## Error Handling

### Error Types

| Error Code | Condition | Recovery |
|------------|-----------|----------|
| `AppNotFound` | App name doesn't match any appmodule | List available apps |
| `EntityNotFound` | Entity logical name doesn't exist | Check metadata |
| `EntityNotInApp` | Entity not in app's sitemap (for set-* commands) | Use `add-table` first |
| `EntityAlreadyInApp` | Entity already in sitemap (for add-table) | Skip or use different app |
| `ComponentNotFound` | Form/view/chart name not found for entity | List available components |
| `SolutionNotFound` | Solution name doesn't exist | List available solutions |
| `InvalidSitemapXml` | XML fails XSD validation | Show line/element details |

### Error Messages

```
Error: Model-driven app 'Fake App' not found.
Available apps: Sales Hub, Customer Service Hub, PPDS MDA

Error: Table 'hsl_veterinarian' is not in app 'PPDS MDA'.
Add it first with: ppds model-driven-app add-table hsl_veterinarian --app "PPDS MDA"

Error: Sitemap XML validation failed at line 5, position 12:
  The attribute 'Entityy' is not declared. Did you mean: 'Entity'?
```

---

## Design Decisions

### Why sitemap XML manipulation for entity additions?

**Context:** Adding tables to an MDA can theoretically be done via `AddAppComponents` action or by modifying the sitemap XML.

**Decision:** Use sitemap XML manipulation exclusively for entity additions.

**Evidence:** Investigation revealed:
- Modern MDAs derive navigation from the **sitemap**, not from `appmodulecomponent` records
- `AddAppComponents` silently no-ops for entities when the app uses sitemap-driven inclusion
- The `AddAppComponents` action's `Components` parameter is typed as `Collection(crmbaseentity)` with no documented examples of the correct JSON payload format for entity additions

**Consequences:**
- Positive: Reliable, matches how Power Apps maker portal works
- Negative: Requires XML manipulation and XSD validation

### Why use AddAppComponents for forms/views/charts?

**Context:** Form/view/chart visibility can be controlled via `appmodulecomponent` records.

**Decision:** Use `AddAppComponents` action with correct `@odata.type` and key fields.

**Evidence:** The correct payload format requires concrete OData types:
```json
{
  "AppId": "<appmoduleid>",
  "Components": [
    {"@odata.type": "#Microsoft.Dynamics.CRM.systemform", "formid": "<guid>"},
    {"@odata.type": "#Microsoft.Dynamics.CRM.savedquery", "savedqueryid": "<guid>"}
  ]
}
```

**Key distinction:** `appmoduleid` (used in API paths and `AppId` parameter) vs `appmoduleidunique` (lookup value in `appmodulecomponents` table). The `appmodulecomponents` table is read-only — no Create/Update via Web API.

### Why strict XSD validation?

**Context:** Sitemap XML can be validated client-side or server-side.

**Decision:** Strict XSD validation against bundled `SiteMap.xsd` + `SiteMapType.xsd` (v9.0.0.2090) before any PATCH.

**Rationale:**
- Catches errors before they hit the server
- Provides line/element-level error messages (server errors are less helpful)
- Follows PR 1174 pattern for form XML validation

**Implementation:** Embed XSDs in `src/PPDS.Cli/Resources/Schemas/`, use `EmbeddedXsdResolver` pattern from `FormSchemaResources`.

### Why single ModelDrivenAppService class?

**Context:** Could split into separate services (SitemapService, AppComponentService).

**Decision:** Single `ModelDrivenAppService` class handles all operations.

**Rationale:**
- Operations are tightly coupled (add-table touches both sitemap AND components)
- Simpler dependency graph for commands
- Easier to ensure transactional consistency

---

## Related Specs

- [architecture.md](./architecture.md) — Application Services pattern (A1/A2)
- [solutions.md](./solutions.md) — Solution component management
- [connection-pooling.md](./connection-pooling.md) — Dataverse connection handling

---

## Changelog

| Date | Change |
|------|--------|
| 2026-06-01 | Initial spec |

# Form Management Commands

**Status:** Draft
**Last Updated:** 2026-11-17
**Code:** [src/PPDS.Cli/Commands/Forms/](../src/PPDS.Cli/Commands/Forms/) | [src/PPDS.Cli/Services/Forms/](../src/PPDS.Cli/Services/Forms/) | [src/PPDS.Cli/Resources/Schemas/](../src/PPDS.Cli/Resources/Schemas/)
**Surfaces:** CLI

---

## Overview

`ppds forms` is a CLI command group for inspecting and modifying Dataverse `systemforms` records. It covers listing forms, reading form structure, and structured XML manipulation (tabs, sections, fields, sub-grids) without requiring the maker portal. All modification operations go through a shared internal `SetFormXml` method that validates XML against a bundled Dataverse customization schema before writing.

### Goals

- **Read:** List all forms for an entity; display form structure (tabs, sections, fields, sub-grids)
- **Write:** Modify formxml through structured commands (add/update/remove tabs, sections, fields, sub-grids) or directly via `set-xml`
- **Safe by default:** Schema validation and GUID uniqueness checks before every write; `--publish` and `--solution` as explicit, opt-in side effects
- **Single code path:** `IFormService` powers the CLI now; designed for future TUI/Extension/MCP consumption (Constitution A1, A2)

### Non-Goals

- View management (`savedqueries`) — tracked separately
- Visual form designer (UI canvas)
- Business rules, business process flows
- TUI, Extension, and MCP surfaces — CLI only for this iteration; `IFormService` is the extension point
- Issue #1012 (Forms and views typed authoring services) is superseded by this spec for the forms side; the views portion of #1012 is deferred

---

## Architecture

```
┌──────────────────────────────────────────────────────────────────┐
│                       CLI Commands (thin)                         │
│  ┌─────────────────────────────────────────────────────────────┐ │
│  │  FormsCommandGroup                                           │ │
│  │  list  get  set-xml  add-tab  update-tab  remove-tab        │ │
│  │  find-tab  add-section  update-section  remove-section      │ │
│  │  find-section  add-field  remove-field  reorder-fields      │ │
│  │  repair-bindings  add-subgrid  remove-subgrid                │ │
│  └──────────────────────┬──────────────────────────────────────┘ │
│                         │ calls                                   │
│  ┌──────────────────────▼──────────────────────────────────────┐ │
│  │  IFormService                                                │ │
│  │  ┌──────────────────────────────────────────────────────┐   │ │
│  │  │  FormXmlEditor (internal XML manipulation)           │   │ │
│  │  │  FormXmlValidator (schema + GUID uniqueness)         │   │ │
│  │  │  ClassIdResolver (column-type → classid mapping)     │   │ │
│  │  └──────────────────────────────────────────────────────┘   │ │
│  └──────────────────────┬──────────────────────────────────────┘ │
│                         │                                         │
│  ┌──────────────────────▼──────────────────────────────────────┐ │
│  │  IDataverseConnectionPool + IMetadataQueryService           │ │
│  └─────────────────────────────────────────────────────────────┘ │
└──────────────────────────────────────────────────────────────────┘
```

### Components

| Component | Responsibility |
|-----------|----------------|
| `FormsCommandGroup` | Registers all `ppds forms` subcommands; shared `--profile` / `--environment` options |
| `IFormService` | Domain service interface — all form read/write operations |
| `FormService` | `IDataverseConnectionPool`-backed implementation |
| `FormXmlEditor` | Pure XML manipulation helpers (add/update/remove tabs, sections, fields, sub-grids) using `System.Xml.Linq`; no I/O |
| `FormXmlValidator` | Schema validation against bundled XSD + GUID uniqueness check |
| `ClassIdResolver` | Maps `AttributeTypeCode` enum to formxml classid GUIDs; throws `FormErrorCodes.UnsupportedColumnType` for unmapped types |
| `FormSchemaResources` | Loads bundled `.xsd` files from embedded assembly resources |

### Dependencies

- Depends on: [connection-pooling.md](./connection-pooling.md) — pooled Dataverse clients (D1, D2)
- Depends on: [metadata-browser.md](./metadata-browser.md) — `IMetadataQueryService.GetAttributesAsync` for column type lookup in `add-field`
- Depends on: [publish.md](./publish.md) — `IPooledClient.PublishXmlAsync` (pooled-client extension) for `--publish` flag
- Depends on: [solutions.md](./solutions.md) — `ISolutionService` for `--solution` flag (AddSolutionComponentRequest, component type 60)
- Uses patterns from: [CONSTITUTION.md](./CONSTITUTION.md) — A1, A2, A3, D1, D2, D4, I1, R2

---

## Specification

### IFormService

All methods accept `CancellationToken` (Constitution R2). Write methods accept `IProgressReporter?` (Constitution A3). All methods wrap exceptions in `PpdsException` with `ErrorCode` (Constitution D4).

```csharp
public interface IFormService
{
    // Read
    Task<ListResult<FormInfo>> ListAsync(string entityLogicalName, CancellationToken ct = default);
    Task<FormDetail?> GetAsync(string entityLogicalName, string formName, CancellationToken ct = default);

    // Set XML (all form types; also used internally by all modification methods)
    Task SetFormXmlAsync(SetFormXmlRequest request, IProgressReporter? reporter = null, CancellationToken ct = default);

    // Tab management (Main forms only)
    Task<TabResult> AddTabAsync(AddTabRequest request, IProgressReporter? reporter = null, CancellationToken ct = default);
    Task UpdateTabAsync(UpdateTabRequest request, IProgressReporter? reporter = null, CancellationToken ct = default);
    Task RemoveTabAsync(RemoveTabRequest request, IProgressReporter? reporter = null, CancellationToken ct = default);
    Task<TabInfo?> FindTabAsync(FindTabRequest request, CancellationToken ct = default);

    // Section management (Main forms only)
    Task<SectionResult> AddSectionAsync(AddSectionRequest request, IProgressReporter? reporter = null, CancellationToken ct = default);
    Task UpdateSectionAsync(UpdateSectionRequest request, IProgressReporter? reporter = null, CancellationToken ct = default);
    Task RemoveSectionAsync(RemoveSectionRequest request, IProgressReporter? reporter = null, CancellationToken ct = default);
    Task<SectionInfo?> FindSectionAsync(FindSectionRequest request, CancellationToken ct = default);

    // Field management (Main forms only)
    Task AddFieldAsync(AddFieldRequest request, IProgressReporter? reporter = null, CancellationToken ct = default);
    Task<AddFieldsResult> AddFieldsWithResultAsync(AddFieldRequest request, IProgressReporter? reporter = null, CancellationToken ct = default);
    Task RemoveFieldAsync(RemoveFieldRequest request, IProgressReporter? reporter = null, CancellationToken ct = default);
    Task ReorderFieldsAsync(ReorderFieldsRequest request, IProgressReporter? reporter = null, CancellationToken ct = default);
    Task ReorderFieldsStrictAsync(ReorderFieldsRequest request, IProgressReporter? reporter = null, CancellationToken ct = default);
    Task MoveFieldAsync(MoveFieldRequest request, IProgressReporter? reporter = null, CancellationToken ct = default);
    Task<RepairBindingsResult> RepairBindingsAsync(RepairBindingsRequest request, IProgressReporter? reporter = null, CancellationToken ct = default);

    // Sub-grid management (Main forms only)
    Task AddSubgridAsync(AddSubgridRequest request, IProgressReporter? reporter = null, CancellationToken ct = default);
    Task RemoveSubgridAsync(RemoveSubgridRequest request, IProgressReporter? reporter = null, CancellationToken ct = default);
}
```

### Internal SetFormXml Method

All create/update operations follow this pipeline:

1. **Retrieve** current `formxml` from `systemforms` record (identified by entity logical name + form name, scoped to Main form type for modification commands)
2. **Mutate** XML in-memory using `FormXmlEditor`
3. **Validate** mutated XML using `FormXmlValidator`:
   - Schema validation against bundled Dataverse customization XSD
   - GUID format validation: the XSD types structural IDs as `FormGuidType`, accepting braced or unbraced GUIDs; logical-name IDs remain strings
   - GUID uniqueness: no duplicate instance GUID-valued `id` or `labelid` values (normalized for braces and case); reusable control-type IDs at the exact schema path are excluded
4. **Update** the `systemform` record through the pooled SDK client: `IPooledClient.UpdateAsync(new Entity("systemform", formId) { ["formxml"] = xml })`. There is no generated early-bound `systemform` entity — use a late-bound `Entity`. (PPDS uses the SDK through `IDataverseConnectionPool`, not raw Web API HTTP; D1/D2.)
5. **Optionally** add form to solution (`AddSolutionComponentRequest`, component type 60 = `componenttype.SystemForm`). This request is **not** idempotent — re-adding a component already in the solution throws `FaultException<OrganizationServiceFault>` with `ErrorCode == -2147159998` ("Component already exists in the solution"). `FormService` catches that specific fault and treats it as a no-op, mirroring `PluginRegistrationService.AddToSolutionAsync`.
6. **Optionally** publish entity via the pooled-client extension `IPooledClient.PublishXmlAsync(parameterXml, environmentKey, ct)` with `<importexportxml><entities><entity>{logicalName}</entity></entities></importexportxml>`

`ppds forms set-xml` skips step 2 — it validates and writes user-provided XML directly.

### Schema Validation

Timeline controls accept `UClientActivitiesConfigurationJSON` as string content alongside the existing `UClient*` parameters. Direct `set-xml` writes and unrelated structured edits must preserve this content. This maintenance fix extends the existing parameter allowlist; it does not open all control parameters to arbitrary elements. Known parameter types, form structure, GUID validation, and XML parser protections remain enforced.

The official Dataverse customization schema (v9.0.0.2090) is bundled as embedded resources in `src/PPDS.Cli/Resources/Schemas/`. Form validation needs four files — `FormXml.xsd` (root; defines `<form>`) plus its `xs:include` chain `RibbonCore.xsd` → `RibbonTypes.xsd` + `RibbonWSS.xsd`. They are loaded via `FormSchemaResources` at validation time (never downloaded at runtime), using `System.Xml.Schema.XmlSchemaSet` with a custom `XmlResolver` that resolves the `xs:include` filenames back to embedded resources. Schema load/compile failure throws `Operation.Internal` — it never silently falls back to GUID-only checks.

The schema's `FormGuidType` permits both braced and unbraced GUIDs (`\{?...\}?`). Since #1209, format is enforced only by the XSD. The custom check enforces document-wide uniqueness for GUID-valued instance `id` and `labelid` attributes, normalizing braces and case. Logical-name IDs on elements such as `<control>`, `<data>`, and `<dependency>` do not participate in that uniqueness check. The unqualified `id` at the exact document-root path `form/controlDescriptions/controlDescription/customControl` identifies a reusable control type and is excluded from uniqueness, but still validated by the XSD. Only this attribute is excluded: label IDs, other paths, namespaced lookalikes and nested parameter content retain their existing checks. No bundled schema optionality or opaque parameter rules change.

Validation failure produces a `PpdsValidationException` with `FormErrorCodes.InvalidFormXml` and an error message that includes the failing element name and the line/position.

### classid Resolution for add-field

Column lookup for `add-field` and newly created legacy `reorder-fields` cells is case-insensitive. New controls use the metadata `LogicalName` for bindings and as the base of their control ID (suffixed `1`, `2`, … when that ID is already used on the form; see `add-field` below) and the metadata display label. Existing cells retain authored labels, IDs and properties. Repeated additions to the same section are no-ops, including input case variants; a new intentional placement in another section is allowed. Duplicate input is reported separately from already-present fields.

`AddFieldAsync` retains its original Task signature and parameter positions. The additive `AddFieldsWithResultAsync` shares the real implementation's single write path. Its default interface implementation calls a legacy implementation once and returns unknown counts (null collections, `outcomeKnown: false`), never fabricated zero additions. The additive `ReorderFieldsWithResultAsync` shares the legacy reorder write path and returns the retained case-variant bindings; its default interface implementation calls `ReorderFieldsAsync` once and returns a null list (unknown, not empty), so legacy implementations add no JSON keys. An all-existing request skips the XML update but still validates protections/XML and honors explicit solution/publication requests, reported separately.

Legacy `reorder-fields` remains an authoritative add/reorder/drop list during transition. New cells receive metadata labels; fields only in another section are rejected rather than silently copied. Repeated field arguments and multiple controls for one selected-section binding are rejected before writing. It retains its historical row-flattening behavior and is deprecated in favor of the explicit operations below. The deprecation warning goes to stderr only, preserving the existing JSON data shape (the sole exception is the additive `nonCanonicalBindings` key described under `reorder-fields`, emitted only when non-empty). Removal is planned for CLI 2.0, no earlier than 90 days after the replacement commands' stable release; no calendar removal date is set.

`forms reorder-fields-strict --entity <table> --form <form> --section <section> --fields <complete-permutation>` only permutes existing unique bound cells. It cannot add/drop/copy fields. Bound-cell slots change; rows and their attributes, non-field cells, subgrids, Timeline and nested control content retain their positions. Missing, extra, duplicate and ambiguous bindings fail before writing. Changed field cells with non-unit spans are rejected instead of flattening them.

`forms move-field --entity <table> --form <form> --field <field> --from-section <section> --to-section <section> [--before-field <field>]` relocates one unique existing cell. A standalone source row moves with its row attributes. A cell leaving a mixed row preserves that row's attributes/siblings and receives a new plain target row. Cross-section moves append by default; an anchor must identify one different target field in a standalone row, before which the moved row is inserted. Empty source rows containers remain valid; a rowless target gains rows. Non-unit spans and bound subgrid movement are rejected. Unaffected sections and complete cell/control/nested content are preserved.

Same-section moves without a position and unambiguous source-absent/target-present retries are no-ops after confirmation, customization, metadata and structural checks. A retry reports completion without claiming provenance of a prior write. A source-present field with a target duplicate, duplicate controls or ambiguous section selectors is rejected. New layout APIs have default interface implementations that throw NotSupportedException; they never fall back to legacy destructive reorder. Both new operations finish validation before at most one final SDK XML update; explicit solution association/publication follows separately. Unchanged layouts skip that XML update, retaining explicit side effects. All commands retain target confirmation, cancellation and managed-lock checks.

`FormService` resolves classid by calling `IMetadataQueryService.GetAttributesAsync(entityLogicalName)`, locating the attribute whose `LogicalName` matches the requested column, and reading its `AttributeType` (a `string`, e.g. `"String"`, `"Picklist"`). `AttributeMetadataDto.AttributeType` is a string — there is no `AttributeTypeCode` enum on the DTO — so `ClassIdResolver` keys off the string value. The mapping is:

| `AttributeType` value | Column type | classid |
|-----------------------|-------------|---------|
| `String` | Text (nvarchar) | `{4273EDBD-AC1D-40d3-9FB2-095C621B552D}` |
| `Money` | Currency | `{533B9108-5A8B-42cb-BD37-52D1B8E7C741}` |
| `Picklist` | Choice | `{3EF39988-22BB-4f0b-BBBE-64B5A3748AEE}` |
| `Lookup` | Lookup | `{270BD3DB-D9AF-4782-9025-509E298DEC0A}` |
| `DateTime` | Date/Time | `{5B773807-9FB2-42db-97C3-7A91EFF8ADFF}` |
| `Integer` | Whole Number | `{C6D124CA-7EDA-4a60-AEA9-7FB8D318B68F}` |
| `Decimal` | Decimal | `{C3EFE0C3-0EC6-42be-8349-CBD9079C5A6F}` |
| `Boolean` | Toggle | `{67FAC785-CD58-4f9f-ABB3-4B7DDC6ED5ED}` |
| `Memo` | Multiline Text | `{E0DECE4B-6FC8-4a8f-A065-082708572369}` |

`classid` is never user-supplied. If the column's `AttributeType` is not in the table (e.g. `BigInt`, `Double`, `Customer`, `State`), the operation fails with `FormErrorCodes.UnsupportedColumnType` and a message listing the type name and the supported types.

For `add-subgrid`, classid is always `{E7A81278-8635-4d9e-8D4D-59480B391C5B}` — hardcoded, no lookup required.

### Request DTOs

**SetFormXmlRequest:**
- `EntityLogicalName` (required)
- `FormName` (required)
- `FormXml` (required) — raw XML string
- `SolutionUniqueName?` — if set, adds form to solution after write
- `Publish` (bool) — if true, publishes entity after write

**AddTabRequest:**
- `EntityLogicalName`, `FormName` (required)
- `Label` (required)
- `ShowLabel` (bool, default true)
- `Expanded` (bool, default true)
- `Visible` (bool, default true)
- `AvailableOnPhone` (bool, default true)
- `Columns` (1|2|3, default 1)
- `SolutionUniqueName?`, `Publish` (bool)

**UpdateTabRequest:**
- `EntityLogicalName`, `FormName`, `TabLabel` (required — identifies existing tab by its current label)
- `NewLabel?` (string) — renames the tab if provided
- `ShowLabel?` (bool?) — null = no change; non-null overwrites current value
- `Expanded?` (bool?)
- `Visible?` (bool?)
- `AvailableOnPhone?` (bool?)
- `Columns?` (int?) — 1, 2, or 3
- `SolutionUniqueName?`, `Publish` (bool)

**RemoveTabRequest:**
- `EntityLogicalName`, `FormName`, `TabLabel` (required)
- `SolutionUniqueName?`, `Publish` (bool)

**FindTabRequest:**
- `EntityLogicalName`, `FormName`, `TabLabel` (required)

**AddSectionRequest:**
- `EntityLogicalName`, `FormName`, `TabLabel` (required — identifies parent tab)
- `Label` (required)
- `ShowLabel` (bool, default true)
- `Columns` (1|2, default 1)
- `Visible` (bool, default true)
- `AvailableOnPhone` (bool, default true)
- `SolutionUniqueName?`, `Publish` (bool)

> **Note:** sections have no `expanded` attribute in the Dataverse form schema (`FormXml.xsd` — only tabs do). The issue's section property list mirrored the tab list; the schema is authoritative, so section expand/collapse is not supported and `--expanded` is omitted from `add-section` / `update-section`.

**UpdateSectionRequest:**
- `EntityLogicalName`, `FormName`, `SectionLabel` (required — identifies existing section by its current label)
- `NewLabel?` (string) — renames the section if provided
- `ShowLabel?` (bool?) — null = no change; non-null overwrites current value
- `Columns?` (int?) — 1 or 2
- `Visible?` (bool?)
- `AvailableOnPhone?` (bool?)
- `SolutionUniqueName?`, `Publish` (bool)

**RemoveSectionRequest:**
- `EntityLogicalName`, `FormName`, `SectionLabel` (required)
- `SolutionUniqueName?`, `Publish` (bool)

**FindSectionRequest:**
- `EntityLogicalName`, `FormName`, `SectionLabel` (required)

**AddFieldRequest:**
- `EntityLogicalName`, `FormName`, `SectionLabel` (required)
- `FieldLogicalNames` (string[], required, min 1) — appended in order
- `SolutionUniqueName?`, `Publish` (bool)

**RemoveFieldRequest:**
- `EntityLogicalName`, `FormName`, `FieldLogicalName` (required)
- `SectionLabelOrId?` (string) — if set, only the occurrence in that section is removed; if null, all occurrences are removed
- `SolutionUniqueName?`, `Publish` (bool)

**ReorderFieldsRequest:**
- `EntityLogicalName`, `FormName`, `SectionLabel` (required)
- `FieldLogicalNames` (string[], required) — authoritative ordered list; fields not in list are removed
- `SolutionUniqueName?`, `Publish` (bool)

**MoveFieldRequest:**
- `EntityLogicalName`, `FormName`, `FieldLogicalName`, `FromSection`, `ToSection` (required)
- `BeforeField?` — unique target field in a standalone row
- `SolutionUniqueName?`, `Publish` (bool)

**RepairBindingsRequest:**
- `EntityLogicalName`, `FormName` (required)
- `DryRun` (bool) — plan only: no environment acknowledgement, XML update, solution association or publication; combining it with `SolutionUniqueName` or `Publish` is a `PpdsValidationException`
- `SolutionUniqueName?`, `Publish` (bool)
- `ConfirmEnvironment?` — forwarded to the acknowledgement on a real run; on a dry run it must match the resolved environment (`Environment.ConfirmationTargetMismatch` otherwise)

**AddSubgridRequest:**
- `EntityLogicalName`, `FormName`, `SectionLabel` (required)
- `Label` (required)
- `TargetEntity` (required) — logical name of entity displayed in sub-grid
- `DefaultViewId` (Guid, required) — must correspond to an existing `savedqueries` record
- `Relationship?` (string) — relationship schema name
- `HideLabel` (bool, default false)
- `HideOnPhone` (bool, default false)
- `MaxRows` (int, default 5, range 2–250)
- `HideSearchBox` (bool, default false)
- `SolutionUniqueName?`, `Publish` (bool)

**RemoveSubgridRequest:**
- `EntityLogicalName`, `FormName`, `Label` (required)
- `SolutionUniqueName?`, `Publish` (bool)

### Result Types

**FormInfo:** `Id` (Guid), `Name` (string), `FormType` (int), `FormTypeName` (string), `IsManaged` (bool), `Description?` (string)

**FormDetail:** All FormInfo fields + `Tabs` (list of TabDetail)

**TabDetail:** `Id` (Guid), `Label` (string), `Expanded` (bool), `Visible` (bool), `Columns` (int), `Sections` (list of SectionDetail)

**SectionDetail:** `Id` (Guid), `Label` (string), `Columns` (int), `Fields` (list of FieldDetail), `Subgrids` (list of SubgridDetail)

**FieldDetail:** `LogicalName` (string), `Label?` (string), `ColumnNumber` (int), `RowNumber` (int)

**SubgridDetail:** `Id` (Guid), `Label` (string), `TargetEntity` (string), `ViewId` (Guid), `Relationship?` (string)

**RepairBindingsResult:** `FormId` (Guid), `FormName` (string), `DryRun` (bool), `Repaired` (list of RepairedBinding), `Unchanged` (list of UnchangedBinding), `FormXmlChanged` (bool: the update was submitted), `ReadBackVerified` (bool: the form was read back by ID afterwards and every control this run rewrote was found at its position with its id and the new binding; it says nothing about `Unchanged` entries), `SolutionAssociationRequested` (bool), `PublicationRequested` (bool), `ReadBackRepairable` (list of RepairedBinding: bindings the read-back shows as repairable that this run did not rewrite, for example because the server changed how a value is spelled; reported, not a failure)

**RepairedBinding:** `ControlId?` (string), `OldBinding` (string), `NewBinding` (string), `Location` (string: `<Tab> / <Section>` labels, `<Tab> / Tab header`, `<Tab> / Tab footer`, `Header` or `Footer`; a control whose location cannot be described this way is never rewritten), `SectionId?` (string, null outside sections), `ControlIdCaseDiffers` (bool: the id also spells the logical name in another case; ids are never rewritten)

**UnchangedBinding:** `ControlId?`, `Binding` (stored value as read), `Reason` (`no-matching-column` | `ambiguous-column` | `control-id-case-differs` | `related-or-unbound-control` | `binding-not-plain-text` | `outside-form-layout` | `custom-control-parameter-case-differs` | `event-attribute-case-differs` | `hidden-data-case-differs`), `Location` (a layout location as above, or `Control descriptions`, `Event '<name>'`, `Hidden controls`, `Outside form layout`), `SectionId?`, `CanonicalName?` (null for no match or an ambiguous match). A rewritten control never appears here.

**TabResult:** `TabId` (Guid), `TabLabel` (string)

**TabInfo:** `TabId` (Guid), `TabLabel` (string), `Position` (int, 0-based)

**SectionResult:** `SectionId` (Guid), `SectionLabel` (string), `TabLabel` (string)

**SectionInfo:** `SectionId` (Guid), `SectionLabel` (string), `TabLabel` (string), `TabId` (Guid)

### Surface-Specific Behavior

#### CLI Surface

Command group: `ppds forms <subcommand>`

All commands share `--profile` / `--environment` global options plus `--json` output mode (via `GlobalOptions`).

Modification commands (`add-*`, `update-*`, `remove-*`, `reorder-*`, `set-xml`) accept:
- `--publish` — publish entity after operation
- `--solution <name>` — add form to solution after write (component type 60). `AddSolutionComponentRequest` throws if the form is already in the solution (`OrganizationServiceFault.ErrorCode == -2147159998`); `FormService` catches that specific fault and treats it as a no-op, so re-running with `--solution` is safe

Status messages go to stderr (Constitution I1). Structured output goes to stdout.

**Read commands:**

```
ppds forms list --entity <logical-name>
ppds forms get --entity <logical-name> --form "<name-or-id>" [--unpublished] [--raw]
```

`list` output (text mode): tabular display of form name, form type, ID, and managed status.
`get` output (text mode): hierarchical tree of tabs → sections → fields / sub-grids. Each tab and section line includes the element's `name` attribute in brackets (e.g. `Tab: General [tab_general]` for PPDS-generated forms, or `Tab: General [{guid}]` for legacy Dataverse-generated forms). Sub-grid lines include the control name (the `id` attribute).
`get --raw`: writes the raw `formxml` string directly to stdout, bypassing both the tree view and `--json` mode. Suitable for piping to `forms set-xml`.

`get` reads the **published** form by default; `--unpublished` returns the latest draft (via
`RetrieveUnpublishedMultiple`) — useful for inspecting pending edits made without `--publish`. This
matches `views get` and `webresources get`. (Mutation commands always read unpublished so successive
edits compose; only the read-for-display default is published.)

**set-xml:**

```
ppds forms set-xml --entity <logical-name> --form "<name-or-id>" --xml <path-to-file>
```

Reads XML from the given file path, validates it, and writes it back. Works on all form types.
Help text states that schema validation defines GUID format, instance id/labelid GUIDs must be unique, and customControl type IDs under controlDescriptions may repeat.

**Tab commands:**

```
ppds forms add-tab     --entity <e> --form "<f-or-id>" --label "<l>" [--show-label bool] [--expanded bool] [--visible bool] [--available-on-phone bool] [--columns 1|2|3]
ppds forms update-tab  --entity <e> --form "<f-or-id>" --tab "<t-or-id>" [--label "<new-label>"] [--show-label bool] [--expanded bool] [--visible bool] [--available-on-phone bool] [--columns 1|2|3]
ppds forms remove-tab  --entity <e> --form "<f-or-id>" --tab "<t-or-id>"
ppds forms find-tab    --entity <e> --form "<f-or-id>" --label "<l-or-id>"
```

`update-tab`: `--tab` identifies the existing tab by its current label or ID; `--label` optionally renames it. Only provided flags are updated — omitted flags leave the existing value unchanged (nullable `bool?` properties).

Bool flags accept `true` / `false` (case-insensitive).

**Section commands:**

```
ppds forms add-section    --entity <e> --form "<f-or-id>" --tab "<t-or-id>" --label "<l>" [--show-label bool] [--columns 1|2] [--visible bool] [--available-on-phone bool]
ppds forms update-section --entity <e> --form "<f-or-id>" --section "<s-or-id>" [--label "<new-label>"] [--show-label bool] [--columns 1|2] [--visible bool] [--available-on-phone bool]
ppds forms remove-section --entity <e> --form "<f-or-id>" --section "<s-or-id>"
ppds forms find-section   --entity <e> --form "<f-or-id>" --label "<l-or-id>"
```

Sections have no `--expanded` flag — the Dataverse form schema does not model section expand state (only tabs do).

`update-section`: `--section` identifies the existing section by its current label or ID; `--label` optionally renames it. Only provided flags are updated.

**Field commands:**

```
ppds forms add-field      --entity <e> --form "<f-or-id>" --section "<s-or-id>" --field <col> [--field <col2> ...]
ppds forms remove-field   --entity <e> --form "<f-or-id>" --field <col> [--section "<s-or-id>"]
ppds forms reorder-fields --entity <e> --form "<f-or-id>" --section "<s-or-id>" --fields <col1>,<col2>,...
ppds forms repair-bindings --entity <e> --form "<f-or-id>" [--dry-run] [--solution <name>] [--publish] [--confirm-env <url>]
```

`add-field`: `--field` option is repeatable (one per field); order determines append order. A field already bound in the target section is not added again (no-op, reported as already present). A field that exists in a different tab/section (or the form header/footer) is still added (multi-section placement is allowed) and the result lists where else it exists (`otherPlacements` in JSON, `[{field, locations[]}]`; a stderr notice in text mode). Locations are display labels (`Tab / Section`, `Header`, `Footer`), not ids; sections sharing both labels collapse into one entry, so a location is not a reliable key for `--section`. An already-present control whose `datafieldname` differs from the column logical name only by case is left unchanged and reported in `nonCanonicalBindings` (`[{storedName, canonicalName}]`, always present for add-field, possibly empty) with a stderr warning. That spelling has been reported (#1527) to stop the control rendering; this was not verified against Dataverse here. The warning says `forms repair-bindings` (below) rewrites every such binding on the form and only those bindings, keeping the cells' authored labels and settings, which removing and re-adding the field would discard. Editing the binding by hand via `forms get --raw` / `forms set-xml` remains possible. New controls get a control `id` that is unique on the form: the column logical name when no control on the form already uses that `id`, otherwise `<logicalname>N` with the smallest N ≥ 1 that is neither a control `id` on the form nor the logical name of another column of the table (`name`, `name1`, `name2`, …; `new_q1` placed twice on a table that also has `new_q11` becomes `new_q12`), compared case-insensitively against every `<control>` `id` in the form, header and footer included (any control type, e.g. a sub-grid or web resource holding the name forces a suffix; a missing or empty `id` occupies nothing). `<hiddencontrols>/<data>` ids are not controls and are not counted, so a column that is only a hidden data entry keeps its bare name. Reverse order: if a suffixed `id` already on the form equals a column's logical name (for example written by an earlier version or authored), that column's later placement cannot use its bare name and is suffixed too (`new_q11` → `new_q111`); existing ids are never renamed to avoid this. Legacy `reorder-fields` plans new cells against the ids that remain in the written XML: ids of bound cells it drops from the selected section are free, and each planned new cell reserves its id before the next is planned. The assigned ids are reported additively: `addedControls` (`[{field, controlId}]`, in add order) in add-field JSON and `AddFieldsResult.AddedControls`, and `controlId` in the MCP `ppds_forms_add_field` result (omitted when null, so never on `ppds_forms_publish` results): the planned id on a dry run; the written id once the draft write is submitted; null when the field was already in the section and when the draft write definitely failed (`draftWrite.status` `failed`), because no such control exists. When the write outcome is `unknown` (for example a lost acknowledgement), or the write succeeded but `draftVerification` did not, `controlId` is the submitted id and is unconfirmed on the form; callers should read the phase results before relying on it. This follows the FormXml reference (control `id` "uniquely identifies the control on the form") and the Client API naming of repeated column controls (`name`, `name1`, …; header controls are `header_<name>`, so a header placement does not occupy the bare name). Existing controls are never renamed, including forms that already repeat an `id`; the validator does not reject such forms, because server enforcement is unconfirmed and rejecting them would block every later edit. Already-present detection, `otherPlacements` and `nonCanonicalBindings` key on `datafieldname`, never on `id`. The runtime Client API control name is assigned at form load and is not guaranteed to equal the XML `id`; scripts should reach every placement through the column's `controls` collection. The same `id` rule applies to new cells from legacy `reorder-fields` and to MCP draft authoring; `move-field` and `reorder-fields-strict` move existing cells and keep their ids.
`remove-field`: `--section` is optional. If omitted, every occurrence of the field is removed from the form. If specified, only the occurrence within that section is removed (no-op if the field is not present in that section).
`reorder-fields`: `--fields` is a comma-separated ordered list; bound fields not in the list are removed from the section (non-field cells are kept). Retained controls whose binding differs from the logical name only by case are kept unchanged and reported with the same warning; `nonCanonicalBindings` is added to the JSON data only when non-empty, preserving the pinned legacy key list otherwise (the one documented exception to the unchanged JSON shape).

`repair-bindings` (#1527): finds every layout-bound control on a Main form — a `<control>` with a non-empty `datafieldname` at `cell/row/rows` under a tab section (`form/tabs/tab/columns/column/sections/section`), a tab header/footer (`form/tabs/tab/tabheader|tabfooter`) or the form header/footer (`form/header|footer`), on that exact path from the root — whose `datafieldname` equals exactly one column logical name of the form's table when compared ignoring case but differs in case, and rewrites that `datafieldname` to the exact logical name from metadata. **What is written.** Unlike `add-field`/`move-field`, which parse the form and send back a re-serialised document, `repair-bindings` sends back the stored `formxml` text with only the repaired attribute values replaced; every other character is kept. Values are located from the XML reader's line and position information; if the text at that position is not the attribute, nothing is written. A case-variant binding whose stored text uses character references is not rewritten (`binding-not-plain-text`). A document with a DOCTYPE, and a form without form XML, are refused before anything is written. **Reported and never rewritten:** a binding that matches no column even ignoring case (`no-matching-column`); a binding that matches several column names ignoring case and none exactly (`ambiguous-column`; a binding equal to a column name is always exact and never rewritten); an exact binding whose control `id` spells the logical name in a different case — bare, with a `header_`/`footer_` prefix or followed only by digits (`control-id-case-differs`; on a rewritten control this is the `controlIdCaseDiffers` flag instead, so no control is listed twice); a case-variant binding on a control with a `relationship` or `isunbound` true (`1`/`true`) — the XSD's `FormXmlControlType` attributes — whose binding may not name a column of this table (`related-or-unbound-control`; such a control that matches nothing is not reported); a case-variant `cell/control` that is not on the layout path, for example inside custom-control `parameters` (`outside-form-layout`); and case-variant column names in custom-control `datafieldname` parameters under `controlDescriptions` (`custom-control-parameter-case-differs`), `event/@attribute` (`event-attribute-case-differs`) and `hiddencontrols/data/@datafieldname` (`hidden-data-case-differs`), skipping events and data with a `relationship`. Control ids are never rewritten and there is no option to do so. **Columns** come from the same metadata read as the other form writers: `IMetadataQueryService.GetAttributesAsync`, which sends `RetrieveEntityRequest` with `RetrieveAsIfPublished = false`. An empty column list is a `Forms.ColumnNotFound` failure, not a success. **Write, verification and errors.** The form, environment, `--profile`/`--environment`/`--confirm-env`, `--solution` and `--publish` behave as for `add-field`: the unpublished form is read; the planned text is validated once; a repair is one update submitted as an unpublished draft. There is no row-version check between read and update (as for the other CLI form writers). The command reports by phase: (1) a failure while planning keeps its own classification (for example `Connection.Timeout`, authentication or throttling codes and their exit codes; otherwise `Forms.RepairFailed`) and says nothing was written; (2) a failed or timed-out update, including a cancellation during it, keeps its classification (otherwise `Forms.RepairWriteOutcomeUnknown`) and says the outcome is unknown; (3) the form is then read back by its ID and each control this run rewrote must be at its position with its id and the new binding — a control missing or different, a read-back that fails, and a cancellation during the read-back all fail with `Forms.RepairNotVerified`, which says the update was submitted; bindings that became repairable only in the read-back are reported in `readBackRepairable`, not a failure; (4) explicit `--solution` then `--publish` follow, and a failure in either says what was already written and verified (or that nothing needed repair), keeps its classification (otherwise `Forms.SolutionAssociationFailed` / `Forms.PublicationFailed`) and, for an association failure, that publication was not attempted. No association or publication follows a write that is not verified. A form with nothing to repair gets no XML update but still honours an explicit `--solution`/`--publish`. Non-customizable and non-Main forms are rejected before any write. **Dry run.** `--dry-run` (the `--dry-run` convention of the metadata, data and flows commands; no other forms CLI command has one) reads the form and metadata, validates the planned text and reports the same lists without acknowledgement and without any write; a supplied `--confirm-env` must still match the resolved environment. Combining it with `--solution` or `--publish` is rejected by the service and, before connecting, by the command (exit 3; standard JSON error envelope in JSON mode). **Output.** Exit code 0 on success, including nothing to repair and reported-only entries; failures use the shared exception mapping. Text mode writes status and one line per entry to stderr (stdout empty); when case-variant references remain (any reason other than `no-matching-column`) the summary says so instead of implying a full repair. JSON data: `entity`, `form`, `formId`, `dryRun`, `message`, `repairedCount`, `unchangedCount`, `formXmlChanged`, `readBackVerified`, `solutionAssociationRequested`, `publicationRequested`, `repaired` (`[{controlId, oldBinding, newBinding, location, sectionId, controlIdCaseDiffers}]`), `unchanged` (`[{controlId, binding, reason, location, sectionId, canonicalName}]`) and `readBackRepairable` (same shape as `repaired`); null values are omitted. The read-back confirms what the server returned, not how Dataverse or the form designer renders the binding; no such claim is made. It is CLI only; no Spec Law requires an MCP counterpart and the MCP forms surface stays bounded to list/get/add-field/publish.

**Sub-grid commands:**

```
ppds forms add-subgrid    --entity <e> --form "<f-or-id>" --section "<s-or-id>" --label "<l>" --target-entity <te> --default-view <guid> [--relationship <rel>] [--hide-label bool] [--hide-on-phone bool] [--max-rows <n>] [--hide-search-box bool]
ppds forms remove-subgrid --entity <e> --form "<f-or-id>" --label "<l-or-id>"
```

`--default-view` accepts a bare GUID (no braces required at CLI; `FormService` normalizes).

Sub-grid properties map onto schema-defined `<control><parameters>` element names (per `FormXml.xsd`): `--max-rows` → `RecordsPerPage`, `--hide-search-box` → `EnableQuickFind` (inverted), `--target-entity` → `TargetEntityType`, `--default-view` → `ViewId`, `--relationship` → `RelationshipName` (omitted when not supplied). The control carries `classid="{E7A81278-8635-4d9e-8D4D-59480B391C5B}"` and `indicationOfSubgrid="true"`.

### Constraints

- All Dataverse access via `IDataverseConnectionPool` (Constitution D1)
- No pooled client held across multiple method calls (Constitution D2)
- All modification commands scope to Main form type (form type code 2) except `set-xml` which supports all types
- Tab labels within a form must be unique (enforced during `update-tab`, `remove-tab`, `find-tab`)
- Section labels within a form must be unique (enforced during `update-section`, `remove-section`, `find-section`)
- Sub-grid labels within a form must be unique (enforced during `remove-subgrid`)

### Validation Rules

| Field | Rule | Error Code |
|-------|------|------------|
| `--entity` | Must match an existing entity logical name (validated on first Dataverse call) | `FormErrorCodes.EntityNotFound` |
| `--form` | Accepts name or GUID. If GUID-formatted (with or without braces), queries `formid`; otherwise queries `name` | `FormErrorCodes.FormNotFound` |
| `--tab` / `--label` (tab) | Accepts label or name. Tries label match first (case-insensitive), then `name` attribute match (case-insensitive). Dataverse-generated forms have GUID-valued `name` attributes, so callers can still identify tabs by their GUID name string. | `FormErrorCodes.TabNotFound` |
| `--section` / `--label` (section) | Accepts label or name. Same label-then-name dispatch as `--tab`. | `FormErrorCodes.SectionNotFound` |
| `--label` (subgrid) | Accepts label or name. Tries cell label match first, then `<control>` `id` attribute match (the control's unique name). | `FormErrorCodes.SubgridNotFound` |
| tab `name` uniqueness | Each tab's generated name (`tab_<sanitized>`) must be unique within the form | `FormErrorCodes.DuplicateTabName` |
| section `name` uniqueness | Each section's generated name (`section_<sanitized>`) must be unique within the form | `FormErrorCodes.DuplicateSectionName` |
| subgrid `name` uniqueness | Each sub-grid's generated name (`subgrid_<sanitized>`) must be unique within the form and must not equal the `id` of any other control (field controls included) | `FormErrorCodes.DuplicateSubgridName` |
| `--field` | Column must exist on entity | `FormErrorCodes.ColumnNotFound` |
| `--field` classid | Column type must be in classid table | `FormErrorCodes.UnsupportedColumnType` |
| `--default-view` | GUID must match an existing `savedqueries` record | `FormErrorCodes.ViewNotFound` |
| `--max-rows` | Integer in range 2–250 | `FormErrorCodes.InvalidMaxRows` |
| `--columns` (tab) | Value must be 1, 2, or 3 | `Validation.InvalidValue` |
| `--columns` (section) | Value must be 1 or 2 | `Validation.InvalidValue` |
| `id` / `labelid` format | GUID-typed attributes accept braced or unbraced GUIDs; the XSD defines which IDs are logical-name strings | `FormErrorCodes.InvalidFormXml` |
| GUID uniqueness | No duplicate instance GUID-valued `id` or `labelid` values after brace/case normalization; exact-path reusable control-type IDs excluded | `FormErrorCodes.DuplicateGuid` |
| XSD validation | formxml must conform to bundled Dataverse schema | `FormErrorCodes.InvalidFormXml` |

---

## Acceptance Criteria

| ID | Criterion | Test | Status |
|----|-----------|------|--------|
| AC-01 | `ppds forms list --entity X` returns all forms with name, type, ID, and managed status | `FormsListCommandTests.List_ReturnsAllFormsWithCorrectFields` | 🔲 |
| AC-02 | `ppds forms get --entity X --form "Name"` returns hierarchical structure: tabs → sections → fields and sub-grids | `FormsGetCommandTests.Get_ReturnsFormStructure` | 🔲 |
| AC-03 | `SetFormXmlAsync` validates XML against bundled Dataverse customization XSD before writing | `FormXmlValidatorTests.Validate_SchemaViolation_ThrowsWithInvalidFormXml` | ✅ |
| AC-04 | Validation failure from XSD includes the failing element name and line number in the error message | `FormXmlValidatorTests.Validate_SchemaViolation_ErrorMessageIncludesElementAndLine` | ✅ |
| AC-05 | Validation rejects malformed GUID-typed IDs and accepts valid unbraced GUIDs | `FormXmlValidatorTests.Validate_MalformedGuidOnGuidTypedId_ThrowsWithInvalidFormXml`, `FormXmlValidatorTests.Validate_UnbracedGuidOnSectionId_DoesNotThrow` | ✅ |
| AC-06 | Validation rejects formxml with duplicate instance GUID-valued `id` or `labelid` values | `FormXmlValidatorTests.Validate_DuplicateGuid_ThrowsWithDuplicateGuid` | ✅ |
| AC-07 | `ppds forms set-xml` reads XML from file, validates, and PATCHes the `systemforms` record | `SetFormXmlCommandTests.SetXml_ValidFile_PatchesRecord` | 🔲 |
| AC-08 | `ppds forms add-tab` creates a tab with all supported properties and correct defaults (show-label=true, expanded=true, visible=true, available-on-phone=true, columns=1) | `FormServiceTests.AddTab_DefaultProperties_GeneratesCorrectXml` | 🔲 |
| AC-09 | `ppds forms update-tab` modifies only the provided properties on the existing tab; omitted `bool?` properties are unchanged | `FormServiceTests.UpdateTab_PartialUpdate_ChangesOnlyProvidedProperties` | 🔲 |
| AC-09b | `ppds forms update-tab --label "<new>"` renames the tab in formxml | `FormXmlEditorTests.UpdateTab_NewLabel_RenamesTab` | 🔲 |
| AC-10 | `ppds forms remove-tab` removes the tab element and all child section and field elements from formxml | `FormXmlEditorTests.RemoveTab_RemovesTabAndChildren` | 🔲 |
| AC-11 | `ppds forms find-tab` returns tab ID and 0-based position for a matching label | `FormServiceTests.FindTab_ExistingLabel_ReturnsIdAndPosition` | 🔲 |
| AC-12 | `ppds forms add-section` creates a section within the specified tab with all supported properties | `FormServiceTests.AddSection_AllProperties_GeneratesCorrectXml` | 🔲 |
| AC-13 | `ppds forms update-section` modifies only the provided properties on the existing section; omitted `bool?` properties are unchanged | `FormServiceTests.UpdateSection_PartialUpdate_ChangesOnlyProvidedProperties` | 🔲 |
| AC-13b | `ppds forms update-section --label "<new>"` renames the section in formxml | `FormXmlEditorTests.UpdateSection_NewLabel_RenamesSection` | 🔲 |
| AC-14 | `ppds forms remove-section` removes the section element and all child field elements from formxml | `FormXmlEditorTests.RemoveSection_RemovesSectionAndChildren` | 🔲 |
| AC-15 | `ppds forms find-section` returns section ID and parent tab label for a matching section label | `FormServiceTests.FindSection_ExistingLabel_ReturnsIdAndParentTab` | 🔲 |
| AC-16 | `ppds forms add-field` appends fields to the named section in the order specified by repeated `--field` options | `FormXmlEditorTests.AddField_MultipleFields_AppendsInOrder` | 🔲 |
| AC-17 | `ppds forms add-field` resolves classid from column metadata type; classid is never user-supplied | `ClassIdResolverTests.Resolve_AllSupportedTypes_ReturnsCorrectClassId` | 🔲 |
| AC-18 | `ppds forms add-field` fails with `UnsupportedColumnType` for column types not in the classid table | `ClassIdResolverTests.Resolve_UnsupportedType_ThrowsUnsupportedColumnType` | 🔲 |
| AC-19 | `ppds forms remove-field` without `--section` removes all occurrences of the named field from formxml; with `--section`, removes only from that section | `FormXmlEditorTests.RemoveField_NoSection_RemovesAllOccurrences`, `FormXmlEditorTests.RemoveField_WithSection_RemovesOnlyFromSection` | 🔲 |
| AC-20 | `ppds forms reorder-fields` replaces the section's field list with the specified ordered list; bound fields not in the list are removed (non-field cells are kept) | `FormXmlEditorTests.ReorderFields_AuthoritativeList_RemovesUnlistedFields` | 🔲 |
| AC-21 | `ppds forms add-subgrid` adds a sub-grid with classid `{E7A81278-8635-4d9e-8D4D-59480B391C5B}` and all supported properties, applying correct defaults (hide-label=false, hide-on-phone=false, max-rows=5, hide-search-box=false) | `FormXmlEditorTests.AddSubgrid_DefaultProperties_GeneratesCorrectXml` | 🔲 |
| AC-22 | `--target-entity` and `--default-view` are required for `add-subgrid`; `--relationship` is optional | `AddSubgridCommandTests.Command_RequiresTargetEntityAndDefaultView` | 🔲 |
| AC-23 | `add-subgrid` validates that `--default-view` GUID corresponds to an existing `savedqueries` record before modifying formxml | `FormServiceTests.AddSubgrid_InvalidViewId_ThrowsViewNotFound` | 🔲 |
| AC-24 | `--max-rows` values outside 2–250 are rejected with `InvalidMaxRows` error | `FormServiceTests.AddSubgrid_MaxRowsOutOfRange_ThrowsInvalidMaxRows` | 🔲 |
| AC-25 | `ppds forms remove-subgrid` removes the sub-grid control matching the label from formxml | `FormXmlEditorTests.RemoveSubgrid_ExistingLabel_RemovesControl` | 🔲 |
| AC-26 | `--publish` flag publishes the entity after a successful modification via `PublishXmlAsync` | `FormServiceTests.SetFormXml_WithPublish_CallsPublishXmlAsync` | 🔲 |
| AC-27 | `--solution <name>` adds the form to the named solution using `AddSolutionComponentRequest` with component type 60 after a successful write; a `-2147159998` fault (already present) is caught and treated as success | `FormServiceTests.SetFormXml_WithSolution_AddsSolutionComponent`, `FormServiceTests.SetFormXml_FormAlreadyInSolution_Swallows­Fault` | 🔲 |
| AC-28 | `ppds forms --help` lists all subcommands | `FormsCommandGroupTests.Create_HasAllSubcommands` | 🔲 |
| AC-29 | Each subcommand's `--help` text is present, non-empty, and describes its purpose | `FormsCommandGroupTests.AllSubcommands_HaveDescriptions` | 🔲 |
| AC-30 | `ppds forms set-xml --help` text explicitly documents schema validation, schema-defined GUID format and instance GUID uniqueness | `SetFormXmlCommandTests.Command_HelpTextMentionsSchemaDefinedGuidFormat`, `Command_HelpTextMentionsUniqueness` | ✅ |
| AC-31 | All exceptions from `IFormService` are wrapped in `PpdsException` with a `FormErrorCodes.*` code | `FormServiceTests.AllMethods_WrapExceptionsInPpdsException` | 🔲 |
| AC-32 | `CancellationToken` is passed to every async call in `FormService`; pre-cancelled token causes `OperationCanceledException` | `FormServiceTests.Methods_PropagatesCancellationToken` | 🔲 |
| AC-33 | `IProgressReporter` receives phase updates during `SetFormXmlAsync` (read → validate → write) | `FormServiceTests.SetFormXml_ReportsPhases` | 🔲 |
| AC-34 | All CLI status messages are written to stderr; stdout carries only data output (text table or JSON) | `FormsCommandOutputTests.StatusMessages_WrittenToStderr` | 🔲 |

### Timeline compatibility (#1465)

| AC | Criterion | Test | Status |
|----|-----------|------|--------|
| AC-35 | Timeline JSON configuration validates before, between, or after existing Timeline parameters | `FormXmlValidatorTests.Validate_TimelineConfiguration_AmongExistingParameters` | ✅ |
| AC-36 | Direct XML writes and unrelated tab edits preserve the Timeline configuration | `FormServiceTests.SetFormXml_TimelineConfiguration_PreservesXml`, `FormServiceTests.UpdateTab_TimelineConfiguration_PreservesControl` | ✅ |
| AC-37 | Timeline compatibility retains known parameter types and structural validation | `FormXmlValidatorTests.Validate_TimelineConfiguration_InvalidContentRejected` | ✅ |
| AC-38 | Malformed XML and excessive entity expansion fail before any write | `FormServiceTests.SetFormXml_MalformedXml_DoesNotWrite`, `FormServiceTests.SetFormXml_EntityExpansionLimit_DoesNotWrite` | ✅ |

### Edge Cases

| Scenario | Input | Expected Output |
|----------|-------|-----------------|
| Entity does not exist | `--entity nonexistent` | `PpdsException` with `FormErrorCodes.EntityNotFound` |
| Form not found | `--form "No Such Form"` | `PpdsException` with `FormErrorCodes.FormNotFound` |
| Tab label not found | `--tab "Missing Tab"` | `PpdsException` with `FormErrorCodes.TabNotFound` |
| Section label not found | `--section "Missing Section"` | `PpdsException` with `FormErrorCodes.SectionNotFound` |
| Field not on entity | `--field nonexistent_column` | `PpdsException` with `FormErrorCodes.ColumnNotFound` |
| Column type not in classid table | BigInt column | `PpdsException` with `FormErrorCodes.UnsupportedColumnType` |
| Default view GUID not found | `--default-view {00000000-...}` (no matching record) | `PpdsException` with `FormErrorCodes.ViewNotFound` |
| max-rows = 1 | `--max-rows 1` | `PpdsException` with `FormErrorCodes.InvalidMaxRows` |
| max-rows = 251 | `--max-rows 251` | `PpdsException` with `FormErrorCodes.InvalidMaxRows` |
| Duplicate GUID in set-xml input | `id="{same-guid}"` on two elements | `PpdsException` with `FormErrorCodes.DuplicateGuid` |
| Valid unbraced GUID in set-xml input | Structural `id="8d5e5d54-cae9-42ac-a610-85e840196095"` | Accepted, subject to GUID uniqueness |
| add-field on already-present field | Field already bound in the target section (case-insensitive) | No-op: listed as already present, no write. If the stored binding differs by case, also listed in `nonCanonicalBindings` with a warning pointing to `repair-bindings`; control not rewritten |
| repair-bindings on a form with nothing to repair | No binding differs from a column logical name only by case | Success (exit 0), no XML update; explicit `--solution`/`--publish` still honoured; remaining reported references are listed |
| repair-bindings binding matches no column | `datafieldname="new_gone"` and no such column | Reported as `no-matching-column`; left unchanged |
| add-field on field present in another section | Field bound only in a different section/tab | Control is added; `otherPlacements` lists the other tab/section/header/footer labels |
| remove-field without --section, field in multiple sections | Field appears in 2 sections | Both occurrences are removed |
| remove-field with --section, field not in that section | Field absent from specified section | No-op — no error, section is left unchanged |
| reorder-fields with empty list | `--fields ""` | `PpdsValidationException`: at least one field required |
| Solution name not found | `--solution nonexistent` | `PpdsException` with `Solution.NotFound` |
| Form already in solution | `--solution X` where form is already a component | No-op — fault `-2147159998` caught and swallowed; operation succeeds |

---

## Core Types

### IFormService

Central service interface for all form operations — CLI, future TUI, Extension, and MCP all call the same methods.

### FormXmlEditor

Pure static XML manipulation helpers — no I/O, no DI, fully unit-testable.

```csharp
internal static class FormXmlEditor
{
    internal static XDocument AddTab(XDocument formXml, AddTabRequest request);
    // UpdateTab: null bool? properties are left unchanged; NewLabel renames if non-null
    internal static XDocument UpdateTab(XDocument formXml, UpdateTabRequest request);
    internal static XDocument RemoveTab(XDocument formXml, string tabLabel);
    internal static XDocument AddSection(XDocument formXml, AddSectionRequest request);
    // UpdateSection: null bool? properties are left unchanged; NewLabel renames if non-null
    internal static XDocument UpdateSection(XDocument formXml, UpdateSectionRequest request);
    internal static XDocument RemoveSection(XDocument formXml, string sectionLabel);
    internal static XDocument AddField(XDocument formXml, string sectionLabel, string fieldLogicalName, string classId);
    internal static XDocument RemoveField(XDocument formXml, string fieldLogicalName);
    internal static XDocument ReorderFields(XDocument formXml, string sectionLabel, IReadOnlyList<string> orderedFields, IDictionary<string, string> classIdsByField);
    internal static XDocument AddSubgrid(XDocument formXml, AddSubgridRequest request);
    internal static XDocument RemoveSubgrid(XDocument formXml, string label);
    // Plans the repair from the stored text; RepairedXml is that text with only the repaired datafieldname values replaced.
    internal static BindingRepairPlan PlanBindingRepair(string storedXml, IEnumerable<string> tableColumnNames);
}
```

### FormXmlValidator

```csharp
internal static class FormXmlValidator
{
    // Throws PpdsValidationException on failure
    internal static void Validate(XDocument formXml);
    internal static void ValidateGuids(XDocument formXml); // GUID-valued ID uniqueness
}
```

### ClassIdResolver

```csharp
internal static class ClassIdResolver
{
    // attributeType is AttributeMetadataDto.AttributeType (a string, e.g. "String", "Picklist").
    // Throws PpdsException(FormErrorCodes.UnsupportedColumnType) for unmapped types.
    internal static string ResolveForField(string attributeType);

    public const string SubgridClassId = "{E7A81278-8635-4d9e-8D4D-59480B391C5B}";
}
```

### FormErrorCodes

```csharp
public static class FormErrorCodes
{
    public const string EntityNotFound       = "Forms.EntityNotFound";
    public const string FormNotFound         = "Forms.FormNotFound";
    public const string TabNotFound          = "Forms.TabNotFound";
    public const string SectionNotFound      = "Forms.SectionNotFound";
    public const string ColumnNotFound       = "Forms.ColumnNotFound";
    public const string UnsupportedColumnType= "Forms.UnsupportedColumnType";
    public const string ViewNotFound         = "Forms.ViewNotFound";
    public const string InvalidMaxRows       = "Forms.InvalidMaxRows";
    public const string InvalidFormXml       = "Forms.InvalidFormXml";
    public const string DuplicateGuid        = "Forms.DuplicateGuid";
}
```

---

## Error Handling

### Error Types

| Error Code | Condition | Recovery |
|-----------|-----------|----------|
| `Forms.EntityNotFound` | Entity logical name not found | Verify entity name |
| `Forms.FormNotFound` | No form matching name+entity | Verify form name with `ppds forms list` |
| `Forms.TabNotFound` | No tab matching label | Verify tab with `ppds forms get` |
| `Forms.SectionNotFound` | No section matching label | Verify section with `ppds forms get` |
| `Forms.ColumnNotFound` | Column not on entity | Verify column with `ppds metadata attributes` |
| `Forms.UnsupportedColumnType` | Column type lacks classid mapping | Use a supported column type |
| `Forms.ViewNotFound` | `savedqueries` record not found for GUID | Verify view GUID exists |
| `Forms.InvalidMaxRows` | max-rows not in range 2–250 | Use a value between 2 and 250 |
| `Forms.InvalidFormXml` | Malformed XML or XSD validation failure, including malformed GUID-typed IDs | Fix the XML |
| `Forms.DuplicateGuid` | Duplicate instance id/labelid GUID in document | Make instance GUIDs unique; retain reusable control-type IDs |
| `Forms.RepairNotVerified` | `repair-bindings` submitted its update, but the read-back does not show a rewritten control with its new binding, could not be read, or was cancelled | Run `repair-bindings --dry-run` to see the form's current state; no association/publication was attempted |
| `Forms.RepairWriteOutcomeUnknown` | The `repair-bindings` update did not complete and has no more specific code; it may or may not have been applied | Run `repair-bindings --dry-run`; no association/publication was attempted |
| `Forms.RepairFailed` | `repair-bindings` could not plan and the failure has no more specific code; nothing was written | Fix the cause and retry |
| `Forms.SolutionAssociationFailed` / `Forms.PublicationFailed` | The explicit association/publication after the form XML step failed with no more specific code; the message states what was already written | Retry the association or publish the table |
| `Solution.NotFound` | Solution unique name not found | Verify solution name |

### Recovery Strategies

- **Validation errors:** Message includes element name, line/column (XSD) or the duplicate GUID value — fix and retry
- **Not found:** Use read commands (`list`, `get`, `find-tab`, `find-section`) to discover correct identifiers

---

## Design Decisions

### Why FormXmlEditor is a pure static class?

**Context:** formxml manipulation is complex enough to warrant isolation, but also must be straightforwardly unit-testable.

**Decision:** `FormXmlEditor` is a pure static helper — takes `XDocument`, returns `XDocument`. No DI, no I/O.

**Consequences:**
- Positive: Every XML transformation is independently unit-testable without mocking Dataverse
- Negative: Static class cannot be mocked in integration-level tests — tests at that level use real XML inputs

### Why IFormService lives in PPDS.Cli.Services, not PPDS.Dataverse?

**Context:** Both locations host Dataverse-backed logic. The split follows the existing pattern established by `IMetadataAuthoringService`.

**Decision:** Domain/application services belong in `PPDS.Cli.Services` (Constitution A1). `PPDS.Dataverse` hosts infrastructure — connection pooling, bulk operations, generated entities, query engine. `IFormService` is a domain service.

### Why schema files are bundled, not downloaded?

**Context:** Runtime download requires network access and introduces latency and failure modes at validation time.

**Decision:** XSD files are embedded as assembly resources. They match a specific Dataverse schema version. If schema evolves, files are updated with the PPDS release — not transparently at runtime.

**Consequences:**
- Positive: Deterministic, offline-capable, zero latency
- Negative: Schema updates require a PPDS release; hypothetical future schema additions are not caught until next release

### Why allowlist the Timeline JSON parameter instead of opening all parameters?

For #1465, add `UClientActivitiesConfigurationJSON` to `UnifiedClientTimelineWallParameters` as `xs:string`. JSON is opaque control configuration; this client validates XML content, not the JSON's internal semantics. The existing repeated choice accepts the parameter in any position among other Timeline parameters.

This preserves typed checks on known parameters and structure elsewhere in the form. A same-namespace `xs:any` alongside the existing choices makes the current schema ambiguous; replacing the choices with a lax wildcard loses local declarations and their type checks. A broader extensibility design needs separate coverage for known parameter types and realistic form fixtures. Unknown future parameter names can still require schema updates under this maintenance policy.

### Why all modification commands scope to Main form type only?

**Context:** The issue explicitly states "Main forms only for all modification commands." Other form types (Quick Create=7, Quick View=6, Card=11) have different XML structures that overlap poorly with the tab/section/field model.

**Decision:** Scope enforcement lives in `FormService` — the service resolves the form, checks type code, and throws `Forms.FormNotFound` with a message like "Form 'Name' is not a Main form. Modification commands only support Main forms (type 2). Use `set-xml` to modify other form types."

### Why supersede issue #1012?

**Context:** Issue #1012 ("Forms and views typed authoring services") proposed a combined `IFormService` + `IViewService`. Issue #1163 scopes more precisely: forms CLI commands only, views deferred.

**Decision:** This spec covers the forms side of #1012 with the scope defined in #1163. View management is tracked separately.

---

## Related Specs

- [metadata-authoring.md](./metadata-authoring.md) — Pattern reference for Application Service design, error codes, CLI command structure
- [publish.md](./publish.md) — `PublishXmlAsync` integration for `--publish` flag
- [solutions.md](./solutions.md) — `AddSolutionComponentRequest` integration for `--solution` flag
- [connection-pooling.md](./connection-pooling.md) — `IDataverseConnectionPool` usage (D1, D2)
- [CONSTITUTION.md](./CONSTITUTION.md) — Governing principles

---

## Changelog

| Date | Change |
|------|--------|
| 2026-11-17 | Add `forms repair-bindings` (#1527): rewrites case-variant control bindings to the metadata logical name by replacing only those values in the stored text, reads the form back, reports what it does not rewrite, with `--dry-run`; the add-field/reorder case-variant warning now points to it. AC-73–AC-79. |
| 2026-10-29 | Accept Timeline `UClientActivitiesConfigurationJSON` (#1465); add validator and service preservation regressions plus malformed XML/entity-expansion checks. Reconcile GUID validation wording with #1209. |
| 2026-06-01 | Initial spec |
| 2026-06-01 | Opus review corrections: SDK late-bound update (not Web API PATCH); `AddSolutionComponentRequest` non-idempotent fault catch; `GetAttributesAsync` (plural); `ClassIdResolver` keys off `AttributeType` string |
| 2026-06-01 | Embedded real Dataverse schema (FormXml.xsd v9.0.0.2090 + ribbon includes) replacing placeholder; generator emits schema-valid `tabs/columns/sections/rows` hierarchy with `availableforphone` + `labelid` attribute; dropped section `--expanded` (not in schema); subgrid params use `RecordsPerPage`/`EnableQuickFind`; `<control>` `id` exempt from brace-GUID check |
| 2026-06-12 | `--form`, `--tab`, and `--section` options now accept GUID as an alternative to name/label. GUID detection uses `Guid.TryParse` (braces optional); name lookup falls through when the value is not a valid GUID. `GetAsync` and `FetchFormRecordAsync` return the resolved form name so callers always see the display name, not the GUID. |
| 2026-06-12 | `forms remove-field` gains an optional `--section` flag. Without it, all occurrences of the field are removed; with it, only the occurrence in the specified section is removed (no-op if absent). `RemoveFieldRequest` gets `SectionLabelOrId?`. |
| 2026-06-12 | `forms remove-subgrid --label` now accepts a GUID (control `id`) in addition to a label. `ElementMatches` corrected: element `id` attribute is compared as a brace-normalised string rather than re-parsed as a Guid, so pre-existing forms with non-canonical `id` values can still be matched by ID. |
| 2026-06-12 | `forms get` gains `--raw` flag (writes raw `formxml` to stdout) and now shows element names in the text-mode tree output (`Label [name]` format). `FormDetail` gains `FormXml?`, `TabDetail` gains `Name`, `SectionDetail` gains `Name`, `SubgridDetail.Id` (Guid) replaced by `Name` (string, the control `id` attribute). |
| 2026-06-12 | Tab/section `name` attribute now generated as `tab_<sanitized_label>` / `section_<sanitized_label>` instead of a GUID (passes Dataverse designer name validation). Subgrid `control.id` now set to `subgrid_<sanitized_label>`; `uniqueid` GUID attribute added for Dataverse use. Name uniqueness enforced on add: `DuplicateTabName`, `DuplicateSectionName`, `DuplicateSubgridName`. `--tab`/`--section`/`--label` (subgrid) now match by label OR `name` attribute (no GUID detection). |

## Canonical new field bindings (#1527)

| ID | Criterion | Test | Status |
|---|---|---|---|
| AC-48 | Case-insensitive add-field lookup emits the metadata LogicalName for the new binding and as the base of the new control ID (suffixed per AC-72 only when that ID is already used), and metadata display labels. Existing same-section bindings remain unchanged. | FormFieldBindingTests.Add_UsesMetadataLogicalName, Add_ExistingCaseVariantsAreUnchanged | Pending revised regression run |
| AC-49 | Legacy reorder creates new cells with metadata LogicalName/display label, preserves retained cells/IDs/properties and non-field controls, rejects cross-section copying and retains omission semantics. | FormFieldBindingTests.Reorder_CanonicalizesOnlyNewCells, Reorder_ExistingFieldNeedsNoSupportedMetadata | Pending revised regression run |
| AC-50 | Unknown or unsupported new fields and empty requests fail without Update, solution association or publication, including rowless sections. A valid reorder of a rowless section leaves the section empty. | FormFieldBindingTests.InvalidFields_DoNotWrite, Reorder_RowlessInvalidField_DoesNotWrite, Reorder_RowlessValidField_PreservesNoNewCellBehavior | ✅ |
| AC-51 | Normal CLI parsing and action invoke the common service and produce canonical new bindings. Existing MCP draft authoring uses the metadata logical name. | FormFieldBindingCommandTests.Command_UsesRealService, FormAuthoringTests.AddField_ManagedOrUnmanaged_PreservesTimelineAndPriorDraftWithoutPublishing | ✅ |

## Reusable control type IDs (#1541)

| ID | Criterion | Test | Status |
|---|---|---|---|
| AC-52 | Full-schema valid forms may repeat reusable customControl type IDs at the exact unqualified root path, including brace/case variants and separate control instances. | FormControlTypeIdTests.Fixture_PassesBundledSchema; SetXml_RepeatedTypeIds_PreservesExactInput | ✅ |
| AC-53 | Actual instance GUID IDs and label IDs remain unique; malformed type IDs, invalid structure and namespaced/nested lookalikes remain rejected before Update/solution/publish. | FormControlTypeIdTests.InvalidXml_DoesNotWrite; GuidUniqueness_DoesNotExemptLookalikes | ✅ |
| AC-54 | Parsed get --raw → set-xml preserves exact input XML and IDs/references/properties through real service calls, with unchanged target/confirmation/cancellation behavior. | FormControlTypeRoundTripCommandTests.RawGetThenSet_PreservesExactPayload; FormControlTypeIdTests.CancelledSet_DoesNotWrite; ConfirmationDenied_DoesNotAcquireClient | ✅ |
| AC-55 | set-xml command and --xml help explain schema-defined GUID format, unique instance GUIDs and reusable control-type IDs. | FormControlTypeRoundTripCommandTests.Help_ExplainsInstanceAndTypeIds | ✅ |

## Combined field and control validation

| ID | Criterion | Test | Status |
|---|---|---|---|
| AC-56 | Canonical add and reorder accept an existing form containing repeated reusable control types and Timeline JSON while retaining authored cells, references and metadata. | FormCombinationCommandTests.CanonicalMutation_ThenRawGetSet_PreservesPayload | ✅ |
| AC-57 | Published and draft raw get output after canonical mutation survives file-backed set-xml exactly, including mixed-case metadata bindings, Timeline JSON and reusable type IDs. | FormCombinationCommandTests.CanonicalMutation_ThenRawGetSet_PreservesPayload | ✅ |
| AC-58 | Combined forms still reject duplicate instance IDs, malformed type GUIDs and invalid Timeline parameter content without update, solution association or publication; denied confirmation prevents canonical mutation writes. | FormCombinationCommandTests.MutatedForm_InvalidPayload_DoesNotWrite; CanonicalMutation_DeniedConfirmation_DoesNotWrite | ✅ |

## Safe MCP authoring surface

MCP exposes list/get, adding one existing field to an existing Main-form section, and explicit table publication. Writes use stable form/section IDs and an expected unpublished XML fingerprint. Dry runs do not write. Add saves and verifies the draft; publication is a separate operation requiring the exact entity name as scope acknowledgement. Dataverse publishes the entity's pending customizations, not only the selected form.

All FormService mutation routes enforce environment confirmation, including existing CLI writers. Missing context fails closed. Managed-customizable forms are supported; non-customizable forms are rejected. Existing XML and draft edits must survive targeted changes. A per-process form lock plus stale fingerprint check protects cooperating operations; server optimistic concurrency is used only when supported and otherwise reports preflight-only limitations.

Results record each phase separately (write, association, verification, publication). Partial and uncertain outcomes never imply rollback. MCP and CLI use common Application Services, cancellation and progress reporting.

| ID | Criterion | Test | Status |
|---|---|---|---|
| AC-39 | MCP list/get return stable IDs, published/draft source and draft fingerprint without writes. | FormsProtocolTests.ClientListsBoundedTools_AndCallsReadDraftPublishJourneyWithProgress; FormServiceTests.GetAsync_DefaultsToPublishedRead / GetAsync_Unpublished_ReadsDraft | ✅ protocol/service and live roundtrip |
| AC-40 | MCP completes discovery, preview, draft edit/readback and explicit publication without shell access. | FormsProtocolTests.ClientListsBoundedTools_AndCallsReadDraftPublishJourneyWithProgress; FormsManagedRoundTripLiveTests.ManagedAndUnmanagedForms_CompleteMcpRoundTrip_AndLockedFormRejects | ✅ local protocol and live roundtrip |
| AC-41 | Invalid table/form/type/section/column/solution is rejected before writes. | FormAuthoringTests.InvalidPreconditions_DoNotWrite; FormXmlEditorTests.StableIdWinsOverLegacyGuidName_AndDuplicateIdsFailClosed | ✅ |
| AC-42 | Targeted edits preserve unknown/modern XML, Timeline, IDs, layout and earlier draft changes. | FormAuthoringTests.AddField_ManagedOrUnmanaged_PreservesTimelineAndPriorDraftWithoutPublishing | ✅ SDK fixture and live roundtrip |
| AC-43 | Managed-customizable and managed-locked fixtures cover forms; fixture provenance is documented. | FormAuthoringTests.AddField_ManagedOrUnmanaged_PreservesTimelineAndPriorDraftWithoutPublishing, InvalidPreconditions_DoNotWrite; FormsManagedRoundTripLiveTests.ManagedAndUnmanagedForms_CompleteMcpRoundTrip_AndLockedFormRejects | ✅ local and live roundtrip |
| AC-44 | Dry run is write-free; repeated field addition does not duplicate controls; requested association remains accurate. | FormAuthoringTests.ReadOnly_DryRunPlansWithoutAcknowledgementOrAnyWrites_ActualNoOpRejected, RetryWithFreshHash_IsIdempotent_AndDoesNotAssociateTwice | ✅ |
| AC-45 | Adding never publishes; publication requires table acknowledgement and verifies the selected published form. | FormAuthoringTests.Publication_RequiresExplicitEntityAcknowledgement_AndVerifiesPublishedState, PublicationReadbackFailure_ReportsPublishedButUnverified | ✅ |
| AC-46 | Stale/concurrent writes, partial side effects, cancellation and uncertain submissions are reported honestly. | FormAuthoringTests.ConcurrentRequests_SerializeAndRejectSecondStaleDraft, AssociationFailure_ReturnsSavedDraftAndFailedAssociation_NoPublish, TransportFailureOrCancellationAfterSubmission_ReportsUnknownOutcome, CancelledBeforePlanning_PropagatesCancellation_AndDoesNotAcquireClient | ✅ |
| AC-47 | All existing and new form writers enforce read-only and protected-target acknowledgement. | FormConfirmationCoverageTests.EveryExistingWriter_FailsClosedWithoutContext_BeforeClientAcquisition; FormAuthoringTests.ReadOnly_DryRunPlansWithoutAcknowledgementOrAnyWrites_ActualNoOpRejected; FormsProtocolTests.ClientReceivesStructuredGuardError_AndInvalidInputNeverCallsService | ✅ |

Fixture provenance, reserved resource names, generation/export/import sequence and cleanup proof are documented in [the live fixture guide](../tests/PPDS.LiveTests/Fixtures/Forms/README.md). The approved live run `4f9c2a71` passed on 2026-10-31 against candidate `c0394de1a` (product source `6932dcd52`), including unmanaged and managed edits/publication, locked-form rejection, and completed cleanup with no failures or unknown submissions. This does not claim Maker designer inspection. Legacy CLI GUID-valued names retain fallback when no stable ID matches; MCP section selectors require the actual ID. Duplicate selectors fail closed.

## Contributor retries and explicit field layout (#1528, #1529)

The 79 focused offline SDK-mock and injected-command cases passed on 2026-11-09. AC-72 was added later and is not part of that count; its focused offline cases (listed in its row) passed on 2026-11-17. The common 37-case baseline had 14 assertion failures and 23 passing controls; four cancellation regression cases also failed before the fix. These checks validate mocked SDK requests and output, not live Dataverse behavior. Earlier live evidence above does not validate these changes.

| ID | Criterion | Test | Status |
|---|---|---|---|
| AC-59 | Same-section retries/casing/batch duplicates preserve controls and authored labels, skip redundant XML updates and report accurate added/already-present/duplicate counts; other-section placements remain allowed. | FormContributorRegressionTests; FormContributorCommandRegressionTests; FormAddOutcomeContractTests | ✅ focused offline cases |
| AC-60 | Legacy reorder uses metadata labels for new fields, preserves existing custom labels, rejects copying/ambiguous duplicate requests and retains authoritative drop-unlisted semantics. | FormContributorRegressionTests; FormFieldBindingTests | ✅ focused offline cases |
| AC-61 | Strict reorder is a complete permutation with no field additions/removals; preserves mixed rows, row attributes, IDs, nested content and non-field positions; unchanged retry skips update. | FormExplicitLayoutTests.StrictPermutation_PreservesMixedRowsAttributesCellsAndNonfieldPositions_AndRetryIsNoop; StrictInvalidPlan_RejectsBeforeAllWrites | ✅ focused offline cases |
| AC-62 | Explicit moves preserve whole cells, source siblings and row attributes, support safe anchors/empty targets, and reject ambiguous/unsupported layouts without writes. | FormExplicitLayoutTests.Move_PreservesEntireCellAndSourceSiblings_RowAttributes_AndRetryIsNoop; InvalidMove_RejectsBeforeXmlSolutionOrPublish | ✅ focused offline cases |
| AC-63 | Synthetic applied-but-lost acknowledgement retries do not duplicate cells or perform a second XML update; no real transport behavior is claimed. | FormContributorRegressionTests.AppliedWriteWithLostAcknowledgement_RetryDoesNotAddControlsOrUpdateAgain; FormExplicitLayoutTests.Move_BeforeAnchor_MovesWholeStandaloneRow_AndLostAckRetryHasNoSecondUpdate | ✅ focused offline cases |
| AC-64 | Confirmation, customization locks, metadata, structural checks and cancellation run before a final single update or protected no-op; explicit solution/publication effects remain separate. | FormConfirmationCoverageTests; FormExplicitLayoutTests.NewOperations_ConfirmationDenialAndManagedLocksCannotBeBypassed; NewOperations_CancellationPreservesIdentityAndReleasesFetchLease | ✅ focused offline cases |
| AC-65 | Legacy reorder warns only on stderr without changing JSON shape (except the additive `nonCanonicalBindings` key when non-empty, AC-71); removal requires CLI 2.0 and at least 90 days after stable replacements. | FormExplicitLayoutCommandTests.LegacyReorder_DeprecatesOnlyOnStderr_AndPreservesJsonDataShape | ✅ focused offline cases |
| AC-66 | Actual legacy-only interface implementations retain the old Task signature, get unknown addition counts and reject unsupported new layout operations without destructive fallback. | FormAddOutcomeContractTests; FormLegacyOutcomeCommandTests | ✅ focused offline cases |
| AC-67 | Removing a standalone source row covered by an earlier active span fails before changing XML, including same-section and retry repositioning. | FormMoveRowSpanRegressionTests.Move_PreservesGeometryAndRefusesCrossedSpans | Paired offline geometry proof; permanent gate pending |
| AC-68 | Moving a cell before a later spanning sibling or inserting a target row across an active span fails without update, solution association or publication. | FormMoveRowSpanRegressionTests.Move_PreservesGeometryAndRefusesCrossedSpans | Paired offline geometry proof; permanent gate pending |
| AC-69 | Covered mixed rows, earlier spanning siblings, expired spans, insertion before a span starts and unchanged-position retries remain supported with cell/reference content preserved. | FormMoveRowSpanRegressionTests.Move_PreservesGeometryAndRefusesCrossedSpans | Paired offline geometry proof; permanent gate pending |
| AC-70 | `add-field` reports (does not block) a field already placed in another tab/section or the form header/footer via `otherPlacements` (label strings, e.g. `My Tab / Other`, `Header`); same-section re-add stays a no-op with no notices; JSON element shape `{field, locations[]}`; stderr notice in text mode with stdout empty. | FormPlacementNoticeTests.Add_FieldInAnotherSection_ReportsOtherPlacementAndStillAdds, Add_FieldInTwoOtherSections_ReportsBothPlacements, Add_FieldInFormHeader_ReportsHeaderPlacement, Add_FieldNewToTheForm_ReportsNoOtherPlacementAndAdds, Add_FieldAlreadyInTargetSection_IsNoOpWithoutOtherPlacementNotice, Add_SameSectionReAdd_RemainsNoOpWithoutNotices; FormPlacementNoticeCommandTests.AddField_Json_HasOtherPlacementsAndEmptyNonCanonicalBindings, AddField_Text_NoticesGoToStderrOnly | ✅ focused offline cases |
| AC-71 | Existing controls bound with a case-variant `datafieldname` (including a non-first control) are reported distinctly (`nonCanonicalBindings`, element shape `{storedName, canonicalName}`) by add-field and legacy reorder and are not rewritten; reorder JSON gains the key only when non-empty and a legacy implementation yields no new keys. | FormPlacementNoticeTests.Add_ExistingCaseVariantBinding_IsReportedDistinctlyAndNotRewritten, Add_SecondControlWithCaseVariantBinding_IsReportedEvenWhenNotFirst, Reorder_ExistingCaseVariantBinding_IsReportedAndKept; FormPlacementNoticeCommandTests.AddField_Json_HasNonCanonicalBindingShape, Reorder_Json_GainsNonCanonicalBindingsOnlyWhenNonEmpty, Reorder_Text_WarningGoesToStderrOnly, Reorder_LegacyImplementationWithOnlyDefaultMethod_AddsNoKeys | ✅ focused offline cases |
| AC-72 | A new placement of a column whose logical name is already a control `id` on the form gets `<logicalname>N` (smallest N that is neither a used control id nor another column's logical name, case-insensitive, header/footer and non-field controls included, missing/empty ids ignored); a header/footer-prefixed id does not force a suffix; a column whose name an existing suffixed id already holds is itself suffixed; existing controls and ids (including pre-existing duplicates) are never rewritten and such forms remain valid and editable (add, legacy reorder, scoped remove); already-present detection keys on `datafieldname`; legacy `reorder-fields` new cells reserve planned ids and reuse ids of cells it drops; MCP draft authoring uses the same rule; `move-field` keeps the moved control's id; a new sub-grid cannot take an id another control uses; hidden `<data>` entries do not occupy ids; the assigned ids are reported additively (`addedControls`, `[]` when nothing is added; MCP `controlId`, null when the draft write failed); the written XML passes the bundled validator. | FormControlIdUniquenessTests (Add_FieldToSecondSection_GetsSuffixedId_ExistingControlUntouched_AndValidates, Add_ThirdPlacement_TakesNextFreeSuffix_AndRetryIsStillNoOp, Add_AuthoredIdLeavesBareNameFree_AndTheNextPlacementIsSuffixed, Add_FieldInHeaderOrFooter_PrefixedIdDoesNotForceASuffix_ButABareIdDoes, Add_NonFieldControlHoldingTheName_ForcesASuffixOnTheFirstPlacement, Add_SuffixSkipsAnotherColumnsLogicalName_InBothOrders_AndRetryIsNoOp, Add_RealColumnWhoseNameIsAlreadyASuffixedControlId_GetsASuffixItself, LegacyReorder_NewCellForFieldOnlyInHeaderOrFooter_GetsUniqueId, LegacyReorder_ReservesIdsOfEarlierNewCells_WithoutColumnNames, LegacyReorder_WithColumnNames_SuffixSkipsTheOtherColumn_InBothOrders, LegacyReorder_IdOfACellItDrops_IsFreeForTheNewCell, MoveField_KeepsTheSuffixedIdOfTheMovedControl, ExistingDuplicateControlIds_AreNotRewritten_AndTheFormStaysEditable, AddSubgrid_RejectsAnIdAFieldControlAlreadyUses, NextFieldControlId_IsDeterministicSmallestFreeSuffix, NextFieldControlId_SkipsOtherColumnNames, NextFieldControlId_IsCaseInsensitiveWhateverSetTheCallerPasses, Add_ColumnOnlyInHiddenControls_KeepsItsBareNameOnTheFirstVisiblePlacement, LegacyReorder_Service_PassesTableColumnNames_SoTheSuffixSkipsARealColumn); FormAuthoringTests.AddField_SecondSectionPlacement_GetsSuffixedControlId_AndRetryIsUnchanged, AddField_ColumnInHeaderOrFooterWithBareId_GetsSuffix_AndSkipsOtherColumnNames, ControlId_IsNullOnlyWhenTheDraftWriteDefinitelyFailed; FormPlacementNoticeCommandTests.AddField_Json_ReportsAssignedControlIdsAdditively; FormsProtocolTests.ClientListsBoundedTools_AndCallsReadDraftPublishJourneyWithProgress (wire name `controlId`, absent from publish) | ✅ focused offline cases (2026-11-17) |

## Binding repair (#1527)

Offline SDK-mock and injected-command cases. The synthetic server stores submitted text verbatim and returns it on read-back; the cases validate requests and output, not how Dataverse stores or normalises `formxml` or how a mis-cased binding renders.

| ID | Criterion | Test | Status |
|---|---|---|---|
| AC-73 | `repair-bindings` rewrites every plain-text case-variant `datafieldname` of a layout-bound control (tab sections, tab header/footer, form header and footer) to the exact logical name with one update whose text equals the stored text except those values — including stored text with a declaration, comments, CRLF, LF-only, lone CR and mixed line endings, tabs, CDATA, a whitespace-only value, single quotes, a start tag spanning lines, surrogate pairs and a repair beyond 4096 characters on one line (single-line documents) — while look-alikes in comments, CDATA and other attributes are untouched; it then reads the form back and reports `readBackVerified`. Against a mock that stores the submitted text verbatim a second run plans nothing (planner idempotence only; Dataverse storage is not observed). | FormBindingRepairTests.Repair_RewritesOnlyMisCasedBindings_InSectionsHeaderAndFooter_AndReadsBack, Repair_PreservesNonNormalisedStoredText_ExceptTheRepairedValues, Editor_DescribesTabHeaderAndFooterLocations; FormBindingRepairEdgeTests.Splice_ReplacesExactlyTheBindingValues | ✅ focused offline cases |
| AC-74 | Reported and never rewritten, each with its reason: no matching column; ambiguous match between column names differing only by case (an exact binding is never rewritten); exact binding with a case-variant id (a rewritten control carries `controlIdCaseDiffers` and is not listed twice); `relationship`/`isunbound` controls (`isunbound="false"` is repaired); character-reference bindings; `cell/control` outside the form layout; custom-control `datafieldname` parameters, event attributes and hidden data (skipped when they carry a `relationship`). Text output does not claim a full repair while such references remain. | FormBindingRepairTests.Repair_UnknownBinding_AndControlIdCase_AreReportedWithoutListingARepairedControlTwice, Repair_ReportsButNeverRewrites_RelatedUnboundCustomParameterEventAndHiddenData, Repair_BindingSpelledWithCharacterReference_IsReportedNotRewritten, ControlIdCaseDiffers_JudgesOnlyIdsBuiltFromTheLogicalName; FormBindingRepairEdgeTests.ControlsOutsideTheFormLayout_AreReportedAndNeverRewritten, UnboundRelatedEventsAndHiddenData_FollowTheirRules, ColumnsDifferingOnlyByCase_ExactBindingIsKept_AndOtherSpellingIsAmbiguous; FormBindingRepairCommandTests.Text_DoesNotClaimAFullRepair_WhileCaseVariantReferencesRemain | ✅ focused offline cases |
| AC-75 | A form with nothing to repair succeeds (exit 0) without an XML update; explicit `--solution`/`--publish` keep their established effects. An empty column list fails with `Forms.ColumnNotFound` and writes nothing. | FormBindingRepairTests.Repair_NothingToRepair_SucceedsWithoutUpdate_AndKeepsExplicitSideEffects, Repair_EmptyColumnList_FailsWithoutWriting; FormBindingRepairCommandTests.NothingToRepair_ExitsSuccessWithoutUpdate | ✅ focused offline cases |
| AC-76 | `--dry-run` reports the same lists as a real run without acknowledgement, XML update, association or publication, in text and JSON; a supplied `--confirm-env` must match; combining it with `--solution`/`--publish` is rejected by the service and by the command before connecting (exit 3, JSON error envelope in JSON mode). | FormBindingRepairTests.DryRun_PlansTheSameRepair_WithoutAcknowledgementOrAnyWrite, DryRun_WithPublishOrSolution_IsRejectedByTheService, DryRun_SuppliedTargetMustMatch_ButNeedsNoAcknowledgement; FormBindingRepairCommandTests.DryRun_NeedsNoAcknowledgement_AndWritesNothing, DryRun_Text_SaysWouldRepair_AndNothingWritten, DryRun_WithExplicitSideEffect_IsRejectedBeforeConnecting | ✅ focused offline cases |
| AC-77 | No write on: denied acknowledgement; missing `--confirm-env` against the real confirmation service on a Production target (the forwarded value is accepted); a non-customizable or non-Main form; empty form XML; a DOCTYPE; an attribute that cannot be located in the text; cancellation during planning or between plan and write; validation failure (dry run and real run). After the write: a read-back that does not show a rewritten control (reverted or missing), that throws, or that is cancelled fails with `Forms.RepairNotVerified` saying the update was submitted, before association/publication; newly repairable read-back bindings are reported, not a failure. Errors keep their classification and state the phase reached: planning (nothing written), update (outcome unknown), association/publication after a verified write or a no-op. Success with `--solution`/`--publish` runs update, read-back, association, publication in that order. Implementations without the method throw `NotSupportedException`. | FormBindingRepairTests.DeniedConfirmation_NonCustomizable_NonMainForm_AndCancellation_DoNotWrite, CancellationBetweenPlanAndWrite_DoesNotWrite, ValidationFailure_PreventsTheWrite_OnDryRunAndRealRun, ReadBackStillMisCased_FailsAsNotVerified_BeforeAssociationOrPublication, LegacyImplementation_RejectsRepair_WithoutAnyFallback; FormBindingRepairEdgeTests (DoctypeAndEmptyXml_AreRefused, LocateAttributeValue_FailsClosed_WithoutLineInfoOrWhenTheTextDisagrees, EmptyStoredFormXml_FailsWithoutWriting, ReadBack_VerifiesRewrittenControls_AndReportsNewlyRepairableOnesWithoutFailing, ReadBack_ThatDoesNotShowARewrittenControl_IsNotVerified, ReadBack_ThatThrows_IsNotVerified_AndSaysTheUpdateWasSubmitted, CancellationDuringReadBack_IsNotVerified_AndSaysTheUpdateWasSubmitted, Success_WithSolutionAndPublish_RunsUpdateReadBackAssociationPublicationInOrder, AssociationFailureAfterAVerifiedWrite_KeepsItsClassification_AndSaysTheBindingsWereWritten, PublicationFailure_IsReportedWithItsOwnCode_AfterWriteOrNoOp, UpdateTimeout_IsAnUnknownOutcome_ClassifiedAsATimeout_WithNoSideEffects, MetadataReadTimeout_KeepsItsClassification_AndSaysNothingWasWritten); FormConfirmationCoverageTests.EveryExistingWriter_FailsClosedWithoutContext_BeforeClientAcquisition; FormBindingRepairCommandTests.DeniedConfirmation_FailsWithoutWriting, RealConfirmationService_RefusesWithoutConfirmEnv_AndAcceptsTheForwardedValue | ✅ focused offline cases |
| AC-78 | The command is registered with `--confirm-env`, `--dry-run`, `--solution`, `--publish`; JSON lists `repaired`, `unchanged` and `readBackRepairable` with counts and `readBackVerified`, omitting null `controlId`/`sectionId`; text mode writes only to stderr. | FormBindingRepairCommandTests.Json_ListsRepairedAndUnchangedControls_WithLocations, Json_OmitsNullControlIdAndSectionId, Text_WritesStatusToStderrOnly, Help_DescribesBindingOnlyRepair_AndOptions; FormsCommandGroupTests.Create_HasAllSubcommands, EveryMutation_ExposesExactEnvironmentAcknowledgement_SeparateFromPublication | ✅ focused offline cases |
| AC-79 | The add-field and legacy reorder case-variant warning says `ppds forms repair-bindings` rewrites every such binding on the form (preview with `--dry-run`) instead of pointing to the manual `get --raw` / `set-xml` route. | FormBindingRepairCommandTests.CaseVariantWarning_FromAddFieldAndLegacyReorder_PointsToRepairBindings | ✅ focused offline cases |

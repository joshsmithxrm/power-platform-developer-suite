# Form Management Commands

**Status:** Draft
**Last Updated:** 2026-06-01
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
│  │  add-subgrid  remove-subgrid                                 │ │
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
    Task RemoveFieldAsync(RemoveFieldRequest request, IProgressReporter? reporter = null, CancellationToken ct = default);
    Task ReorderFieldsAsync(ReorderFieldsRequest request, IProgressReporter? reporter = null, CancellationToken ct = default);

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
   - GUID format validation: all `id` and `labelid` attributes must use brace format `{xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx}`
   - GUID uniqueness: no duplicate `id` or `labelid` values within the document
4. **Update** the `systemform` record through the pooled SDK client: `IPooledClient.UpdateAsync(new Entity("systemform", formId) { ["formxml"] = xml })`. There is no generated early-bound `systemform` entity — use a late-bound `Entity`. (PPDS uses the SDK through `IDataverseConnectionPool`, not raw Web API HTTP; D1/D2.)
5. **Optionally** add form to solution (`AddSolutionComponentRequest`, component type 60 = `componenttype.SystemForm`). This request is **not** idempotent — re-adding a component already in the solution throws `FaultException<OrganizationServiceFault>` with `ErrorCode == -2147159998` ("Component already exists in the solution"). `FormService` catches that specific fault and treats it as a no-op, mirroring `PluginRegistrationService.AddToSolutionAsync`.
6. **Optionally** publish entity via the pooled-client extension `IPooledClient.PublishXmlAsync(parameterXml, environmentKey, ct)` with `<importexportxml><entities><entity>{logicalName}</entity></entities></importexportxml>`

`ppds forms set-xml` skips step 2 — it validates and writes user-provided XML directly.

### Schema Validation

The official Dataverse customization schema (v9.0.0.2090) is bundled as embedded resources in `src/PPDS.Cli/Resources/Schemas/`. Form validation needs four files — `FormXml.xsd` (root; defines `<form>`) plus its `xs:include` chain `RibbonCore.xsd` → `RibbonTypes.xsd` + `RibbonWSS.xsd`. They are loaded via `FormSchemaResources` at validation time (never downloaded at runtime), using `System.Xml.Schema.XmlSchemaSet` with a custom `XmlResolver` that resolves the `xs:include` filenames back to embedded resources. Schema load/compile failure throws `Operation.Internal` — it never silently falls back to GUID-only checks.

The schema's `FormGuidType` permits both braced and unbraced GUIDs (`\{?...\}?`), so it does **not** enforce the brace requirement — the custom GUID check (below) does. GUID uniqueness is always a custom check (XSD cannot enforce document-wide uniqueness).

**GUID format check exemption:** the brace-format check applies to all `id` and `labelid` attributes **except** the `id` on a `<control>` element, which is the column logical name (`xs:string` per the schema), not a GUID. Control `id` values are excluded from both the brace-format and uniqueness checks.

Validation failure produces a `PpdsValidationException` with `FormErrorCodes.InvalidFormXml` and an error message that includes the failing element name and the line/position.

### classid Resolution for add-field

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
- `EntityLogicalName`, `FormName`, `FieldLogicalName` (required) — removes first occurrence
- `SolutionUniqueName?`, `Publish` (bool)

**ReorderFieldsRequest:**
- `EntityLogicalName`, `FormName`, `SectionLabel` (required)
- `FieldLogicalNames` (string[], required) — authoritative ordered list; fields not in list are removed
- `SolutionUniqueName?`, `Publish` (bool)

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
ppds forms get --entity <logical-name> --form "<name>" [--unpublished]
```

`list` output (text mode): tabular display of form name, form type, ID, and managed status.
`get` output (text mode): hierarchical tree of tabs → sections → fields / sub-grids.

`get` reads the **published** form by default; `--unpublished` returns the latest draft (via
`RetrieveUnpublishedMultiple`) — useful for inspecting pending edits made without `--publish`. This
matches `views get` and `webresources get`. (Mutation commands always read unpublished so successive
edits compose; only the read-for-display default is published.)

**set-xml:**

```
ppds forms set-xml --entity <logical-name> --form "<name>" --xml <path-to-file>
```

Reads XML from the given file path, validates it, and writes it back. Works on all form types.
Help text explicitly states: schema validation is applied, brace-format GUIDs are required on all `id` and `labelid` attributes, and all IDs within the document must be unique.

**Tab commands:**

```
ppds forms add-tab     --entity <e> --form "<f>" --label "<l>" [--show-label bool] [--expanded bool] [--visible bool] [--available-on-phone bool] [--columns 1|2|3]
ppds forms update-tab  --entity <e> --form "<f>" --tab "<t>" [--label "<new-label>"] [--show-label bool] [--expanded bool] [--visible bool] [--available-on-phone bool] [--columns 1|2|3]
ppds forms remove-tab  --entity <e> --form "<f>" --tab "<t>"
ppds forms find-tab    --entity <e> --form "<f>" --label "<l>"
```

`update-tab`: `--tab` identifies the existing tab by its current label; `--label` optionally renames it. Only provided flags are updated — omitted flags leave the existing value unchanged (nullable `bool?` properties).

Bool flags accept `true` / `false` (case-insensitive).

**Section commands:**

```
ppds forms add-section    --entity <e> --form "<f>" --tab "<t>" --label "<l>" [--show-label bool] [--columns 1|2] [--visible bool] [--available-on-phone bool]
ppds forms update-section --entity <e> --form "<f>" --section "<s>" [--label "<new-label>"] [--show-label bool] [--columns 1|2] [--visible bool] [--available-on-phone bool]
ppds forms remove-section --entity <e> --form "<f>" --section "<s>"
ppds forms find-section   --entity <e> --form "<f>" --label "<l>"
```

Sections have no `--expanded` flag — the Dataverse form schema does not model section expand state (only tabs do).

`update-section`: `--section` identifies the existing section by its current label; `--label` optionally renames it. Only provided flags are updated.

**Field commands:**

```
ppds forms add-field      --entity <e> --form "<f>" --section "<s>" --field <col> [--field <col2> ...]
ppds forms remove-field   --entity <e> --form "<f>" --field <col>
ppds forms reorder-fields --entity <e> --form "<f>" --section "<s>" --fields <col1>,<col2>,...
```

`add-field`: `--field` option is repeatable (one per field); order determines append order.
`reorder-fields`: `--fields` is a comma-separated ordered list; fields not in the list are removed from the section.

**Sub-grid commands:**

```
ppds forms add-subgrid    --entity <e> --form "<f>" --section "<s>" --label "<l>" --target-entity <te> --default-view <guid> [--relationship <rel>] [--hide-label bool] [--hide-on-phone bool] [--max-rows <n>] [--hide-search-box bool]
ppds forms remove-subgrid --entity <e> --form "<f>" --label "<l>"
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
| `--form` | Must match an existing form by name | `FormErrorCodes.FormNotFound` |
| `--tab` / `--label` (tab) | Must match exactly one tab by label | `FormErrorCodes.TabNotFound` |
| `--section` / `--label` (section) | Must match exactly one section by label | `FormErrorCodes.SectionNotFound` |
| `--field` | Column must exist on entity | `FormErrorCodes.ColumnNotFound` |
| `--field` classid | Column type must be in classid table | `FormErrorCodes.UnsupportedColumnType` |
| `--default-view` | GUID must match an existing `savedqueries` record | `FormErrorCodes.ViewNotFound` |
| `--max-rows` | Integer in range 2–250 | `FormErrorCodes.InvalidMaxRows` |
| `--columns` (tab) | Value must be 1, 2, or 3 | `Validation.InvalidValue` |
| `--columns` (section) | Value must be 1 or 2 | `Validation.InvalidValue` |
| `id` / `labelid` format | Must use brace format `{...}` in formxml (except `<control>` `id`, which is the column logical name) | `FormErrorCodes.InvalidFormXml` |
| GUID uniqueness | No duplicate `id` or `labelid` values in document (excludes `<control>` `id`) | `FormErrorCodes.DuplicateGuid` |
| XSD validation | formxml must conform to bundled Dataverse schema | `FormErrorCodes.InvalidFormXml` |

---

## Acceptance Criteria

| ID | Criterion | Test | Status |
|----|-----------|------|--------|
| AC-01 | `ppds forms list --entity X` returns all forms with name, type, ID, and managed status | `FormsListCommandTests.List_ReturnsAllFormsWithCorrectFields` | 🔲 |
| AC-02 | `ppds forms get --entity X --form "Name"` returns hierarchical structure: tabs → sections → fields and sub-grids | `FormsGetCommandTests.Get_ReturnsFormStructure` | 🔲 |
| AC-03 | `SetFormXmlAsync` validates XML against bundled Dataverse customization XSD before writing | `FormXmlValidatorTests.Validate_InvalidXml_ThrowsWithInvalidFormXml` | 🔲 |
| AC-04 | Validation failure from XSD includes the failing element name and line number in the error message | `FormXmlValidatorTests.Validate_InvalidXml_ErrorMessageIncludesElement` | 🔲 |
| AC-05 | Validation rejects any `id` or `labelid` attribute not in brace-format `{...}` | `FormXmlValidatorTests.Validate_NonBraceGuid_ThrowsWithInvalidFormXml` | 🔲 |
| AC-06 | Validation rejects formxml with duplicate `id` or `labelid` values | `FormXmlValidatorTests.Validate_DuplicateGuid_ThrowsWithDuplicateGuid` | 🔲 |
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
| AC-19 | `ppds forms remove-field` removes the first occurrence of the named field control from formxml | `FormXmlEditorTests.RemoveField_ExistingField_RemovesFirstOccurrence` | 🔲 |
| AC-20 | `ppds forms reorder-fields` replaces the section's field list with the specified ordered list; fields not in the list are removed | `FormXmlEditorTests.ReorderFields_AuthoritativeList_RemovesUnlistedFields` | 🔲 |
| AC-21 | `ppds forms add-subgrid` adds a sub-grid with classid `{E7A81278-8635-4d9e-8D4D-59480B391C5B}` and all supported properties, applying correct defaults (hide-label=false, hide-on-phone=false, max-rows=5, hide-search-box=false) | `FormXmlEditorTests.AddSubgrid_DefaultProperties_GeneratesCorrectXml` | 🔲 |
| AC-22 | `--target-entity` and `--default-view` are required for `add-subgrid`; `--relationship` is optional | `AddSubgridCommandTests.Command_RequiresTargetEntityAndDefaultView` | 🔲 |
| AC-23 | `add-subgrid` validates that `--default-view` GUID corresponds to an existing `savedqueries` record before modifying formxml | `FormServiceTests.AddSubgrid_InvalidViewId_ThrowsViewNotFound` | 🔲 |
| AC-24 | `--max-rows` values outside 2–250 are rejected with `InvalidMaxRows` error | `FormServiceTests.AddSubgrid_MaxRowsOutOfRange_ThrowsInvalidMaxRows` | 🔲 |
| AC-25 | `ppds forms remove-subgrid` removes the sub-grid control matching the label from formxml | `FormXmlEditorTests.RemoveSubgrid_ExistingLabel_RemovesControl` | 🔲 |
| AC-26 | `--publish` flag publishes the entity after a successful modification via `PublishXmlAsync` | `FormServiceTests.SetFormXml_WithPublish_CallsPublishXmlAsync` | 🔲 |
| AC-27 | `--solution <name>` adds the form to the named solution using `AddSolutionComponentRequest` with component type 60 after a successful write; a `-2147159998` fault (already present) is caught and treated as success | `FormServiceTests.SetFormXml_WithSolution_AddsSolutionComponent`, `FormServiceTests.SetFormXml_FormAlreadyInSolution_Swallows­Fault` | 🔲 |
| AC-28 | `ppds forms --help` lists all subcommands | `FormsCommandGroupTests.Create_HasAllSubcommands` | 🔲 |
| AC-29 | Each subcommand's `--help` text is present, non-empty, and describes its purpose | `FormsCommandGroupTests.AllSubcommands_HaveDescriptions` | 🔲 |
| AC-30 | `ppds forms set-xml --help` text explicitly documents schema validation, brace-format GUID requirement, and GUID uniqueness check | `SetFormXmlCommandTests.Command_HelpTextDescribesValidation` | 🔲 |
| AC-31 | All exceptions from `IFormService` are wrapped in `PpdsException` with a `FormErrorCodes.*` code | `FormServiceTests.AllMethods_WrapExceptionsInPpdsException` | 🔲 |
| AC-32 | `CancellationToken` is passed to every async call in `FormService`; pre-cancelled token causes `OperationCanceledException` | `FormServiceTests.Methods_PropagatesCancellationToken` | 🔲 |
| AC-33 | `IProgressReporter` receives phase updates during `SetFormXmlAsync` (read → validate → write) | `FormServiceTests.SetFormXml_ReportsPhases` | 🔲 |
| AC-34 | All CLI status messages are written to stderr; stdout carries only data output (text table or JSON) | `FormsCommandOutputTests.StatusMessages_WrittenToStderr` | 🔲 |

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
| Non-brace GUID in set-xml input | `id="12345678-..."` (no braces) | `PpdsException` with `FormErrorCodes.InvalidFormXml` |
| add-field on already-present field | Field already exists in section | Field is added a second time (Dataverse allows duplicate controls) |
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
}
```

### FormXmlValidator

```csharp
internal static class FormXmlValidator
{
    // Throws PpdsValidationException on failure
    internal static void Validate(XDocument formXml);
    internal static void ValidateGuids(XDocument formXml); // brace format + uniqueness
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
| `Forms.InvalidFormXml` | XSD validation failure or non-brace GUID | Fix the XML |
| `Forms.DuplicateGuid` | Duplicate id/labelid value in document | Make all GUIDs unique |
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
| 2026-06-01 | Initial spec |
| 2026-06-01 | Opus review corrections: SDK late-bound update (not Web API PATCH); `AddSolutionComponentRequest` non-idempotent fault catch; `GetAttributesAsync` (plural); `ClassIdResolver` keys off `AttributeType` string |
| 2026-06-01 | Embedded real Dataverse schema (FormXml.xsd v9.0.0.2090 + ribbon includes) replacing placeholder; generator emits schema-valid `tabs/columns/sections/rows` hierarchy with `availableforphone` + `labelid` attribute; dropped section `--expanded` (not in schema); subgrid params use `RecordsPerPage`/`EnableQuickFind`; `<control>` `id` exempt from brace-GUID check |

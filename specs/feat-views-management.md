# View Management

**Status:** Draft
**Last Updated:** 2026-11-03
**Code:** [src/PPDS.Cli/Commands/Views/](../src/PPDS.Cli/Commands/Views/), [src/PPDS.Cli/Services/Views/](../src/PPDS.Cli/Services/Views/), [src/PPDS.Dataverse/Metadata/](../src/PPDS.Dataverse/Metadata/)
**Surfaces:** CLI

---

## Overview

View management provides a CLI surface for listing, inspecting, and modifying Dataverse `savedqueries` records. The primary pain point is that configuring views (columns, sort, filters) during table setup requires raw Web API PATCH calls with hand-crafted XML. These commands eliminate that friction.

### Goals

- **Inspect**: List all views for an entity and display detailed column/sort/filter configuration
- **Mutate columns**: Add (direct and related-entity), remove, update width, reorder
- **Mutate sort and filter**: Set/clear sort order; set/clear filter from file or inline condition
- **Set fetchxml**: Apply a complete FetchXML document wholesale
- **Integration**: `--publish` and `--solution` flags available on all modification commands

### Non-Goals

- TUI, Extension, or MCP surfaces (CLI only for v1)
- Form management (separate domain)
- Creating or deleting `savedqueries` records
- Bulk migration of views across entities

---

## Architecture

```
┌────────────────────────────────┐
│  CLI Commands (thin wrappers)  │
│  Commands/Views/*Command.cs    │
└───────────────┬────────────────┘
                │ calls
                ▼
┌────────────────────────────────┐
│  IViewService / ViewService    │
│  Services/Views/               │
└───────────────┬────────────────┘
                │ uses
      ┌─────────┴─────────┐
      ▼                   ▼
┌──────────────┐  ┌───────────────────────┐
│ IDataverse   │  │ ICachedMetadata       │
│ Connection   │  │ Provider (OTC lookup) │
│ Pool         │  └───────────────────────┘
└──────────────┘
```

All XML parsing, manipulation, and Dataverse calls live in `ViewService`. Commands are thin wrappers that parse options and delegate (Constitution A1–A2).

### Components

| Component | Responsibility |
|-----------|----------------|
| `ViewsCommandGroup` | Registers the `views` command and shared profile/environment options |
| `ListCommand` | `ppds views list --entity` |
| `GetCommand` | `ppds views get --entity --view [--unpublished] [--raw]` — reads published by default; `--unpublished` shows the latest draft; `--raw` writes raw fetchxml to stdout |
| `AddColumnCommand` | `ppds views add-column` — direct and relationship columns |
| `RemoveColumnCommand` | `ppds views remove-column` |
| `UpdateColumnCommand` | `ppds views update-column --width` |
| `ReorderColumnsCommand` | `ppds views reorder-columns --columns` |
| `SetSortCommand` | `ppds views set-sort --sort` |
| `ClearSortCommand` | `ppds views clear-sort` |
| `SetFilterCommand` | `ppds views set-filter --filter-file | --condition` |
| `ClearFilterCommand` | `ppds views clear-filter` |
| `SetFetchXmlCommand` | `ppds views set-fetchxml --fetchxml` |
| `IViewService` | Application service interface |
| `ViewService` | Implementation: Dataverse I/O + XML manipulation |

### Dependencies

- Depends on: [dataverse-services.md](./dataverse-services.md)
- Depends on: [solutions.md](./solutions.md) (`--solution` via `AddSolutionComponentRequest`)
- Uses patterns from: [architecture.md](./architecture.md)

---

## Specification

### Core Requirements

1. All read/write operations target `savedqueries` records via `IDataverseConnectionPool`.
2. `layoutxml` holds column definitions; `fetchxml` holds sort and filter. Both are read, mutated in-memory with `System.Xml.Linq`, and written back via `UpdateAsync` on the pool client.
3. `ObjectTypeCode` (OTC) for the entity is required in the `object` attribute of `<grid>` in `layoutxml` and resolved at runtime via `ICachedMetadataProvider`.
4. All mutation commands accept optional `--publish` and `--solution` trailing flags.
5. `IProgressReporter` is accepted by any service method expected to take >1 second (Constitution A3).
6. All Dataverse exceptions are wrapped in `PpdsException` with `ErrorCodes.View.*` codes (Constitution D4).
7. CLI status messages go to `Console.Error`; data output goes to `Console.Out` (Constitution I1).
8. `CancellationToken` is threaded through every async call (Constitution R2).

### Query Type Labels

| `querytype` value | Label |
|-------------------|-------|
| 0 | Standard |
| 1 | Advanced Find Default |
| 2 | Associated |
| 4 | Quick Find |

### Column Syntax

`--column "attributename[:width]"` — width defaults to 150 when omitted.

Multiple `--column` flags may be supplied on a single command invocation.

### Related Column Syntax

`--via-relationship <attribute>` — specifies the lookup/relationship attribute on the current entity that joins to the related entity. All `--column` flags following a single `--via-relationship` are added as related-entity cells in `layoutxml` and as `<attribute>` elements under a `<link-entity>` in `fetchxml`.

Resolving the `<link-entity>` element requires a metadata lookup: the implementation calls `ICachedMetadataProvider` to find the one-to-many or many-to-one relationship where the `ReferencingAttribute` matches the `--via-relationship` value. From that relationship record:
- `link-entity name` = `ReferencedEntity` (the related table's logical name)
- `link-entity from` = the related table's primary key attribute
- `link-entity to` = the `--via-relationship` attribute on the current entity
- `link-entity alias` = the `--via-relationship` attribute name (e.g., `hsl_veterinarian_id`)

If the relationship is not found in metadata, the operation throws `PpdsException(ErrorCodes.View.RelationshipNotFound, ...)`.

Related layout cells use `alias.attribute` identities, e.g. `parentaccountid.name`. Use that same qualified identity for update-column, remove-column and reorder-columns. Inspection derives relationship details from the FetchXML link, while retaining support for legacy layouts.

### Reorder contract

`reorder-columns --columns "a,b,c"` requires a complete, case-insensitive permutation of the current layout columns. Every existing column must appear exactly once. Empty lists/segments, partial lists, unknown names and duplicate names are rejected before any write, solution association or publication. Names are trimmed at the input boundary; qualified `alias.attribute` identities remain distinct. Layouts without one unambiguous row of uniquely named cells are rejected rather than guessing which column to retain.

Reordering changes only cell order. Cell attributes, widths, other layout nodes/settings and FetchXML projections, filters, links and sort remain intact. Use the separate `remove-column` command to remove a displayed column; omission from a reorder list never removes it.

### Sort Syntax

`--sort "attributename:asc|desc"` — multiple flags applied in declaration order (first = primary sort).

### Filter Syntax

`--filter-file path/to/filter.xml` — must be a valid `<filter>` element fragment (not a full FetchXML document).

`--condition "attribute:operator:value"` — convenience shorthand for a single condition. Exactly three colon-separated segments required. The `operator` segment is passed through as-is to the FetchXML `<condition operator="...">` attribute; any valid FetchXML operator string is accepted (e.g., `eq`, `ne`, `lt`, `le`, `gt`, `ge`, `like`, `not-like`, `null`, `not-null`, `in`).

`--filter-file` and `--condition` are mutually exclusive.

**Expected `<filter>` fragment format:**
```xml
<filter type="and">
  <condition attribute="statecode" operator="eq" value="0" />
  <condition attribute="hsl_specialization" operator="ne" value="" />
</filter>
```

### fetchxml Structure

Views use FetchXML. Sort `<order>` elements are positioned before `<filter>`. Columns for the base entity appear as `<attribute>` elements; related columns use `<link-entity>`:

```xml
<fetch version="1.0" output-format="xml-platform" mapping="logical" no-lock="false">
  <entity name="hsl_veterinarian">
    <attribute name="hsl_name" />
    <link-entity name="..." from="..." to="..." alias="...">
      <attribute name="..." />
    </link-entity>
    <order attribute="hsl_name" descending="false" />
    <filter type="and">
      <condition attribute="statecode" operator="eq" value="0" />
    </filter>
  </entity>
</fetch>
```

### layoutxml Structure

Base-entity columns use `<cell name="..." width="..." />`. Related cells use the FetchXML link alias:

```xml
<grid name="resultset" object="10050" jump="hsl_name" select="1" preview="1" icon="1">
  <row name="result" id="hsl_veterinarianid">
    <cell name="hsl_name" width="200" />
    <cell name="hsl_specialization" width="150" />
    <!-- Related column: -->
    <cell name="hsl_veterinarian_id.hsl_specialty" width="150" />
  </row>
</grid>
```

The `object` attribute on `<grid>` is the entity's OTC, resolved from `ICachedMetadataProvider`.

### Projection consistency and verification

Adding a column ensures both its layout cell and its FetchXML projection exist. Retrying repairs an existing layout cell with a missing projection. Existing filters, ordering, links and aliases are preserved; all-attributes already includes direct attributes. Removing/reordering cells changes the display layout and retains query projections used by other query logic.

Every XML write reads back the unpublished draft up to three times and compares every written XML field with the intended state. Formatting, XML attribute order and the platform-maintained root fetch savedqueryid annotation are ignored; query attributes, filters, links and ordering remain significant. Equivalent no-op writes are valid. A missing record or different draft raises View.UpdateNotPersisted. Unavailable verification raises View.VerificationFailed; the write may have applied, so inspect the draft before retrying or publishing. Verification confirms only the observed state and does not lock out subsequent writers.

### Environment acknowledgement

All nine writers enforce the shared [environment confirmation](./environment-confirmation.md) policy before acquiring a client, including no-op edits. The shakedown guard remains effective. Each CLI writer accepts `--confirm-env <resolved-url>`, separately from target selection, `--solution` and `--publish`; the acknowledgement and write use the same provider's captured profile/target. Read-only sessions always reject writing. Missing context fails closed. List/get remain acknowledgement-free.

The existing four-parameter ViewService constructor remains available for readers; writers require the additional confirmation service. No IViewService method or result shape changes. Publication remains entity-scoped with the existing solution-before-publication order.

### View writer safety acceptance criteria

| ID | Criterion | Test | Status |
|---|---|---|---|
| AC-29 | Every writer fails closed before client acquisition for absent context or denied confirmation. | ViewConfirmationCoverageTests.EveryWriter_FailsClosedBeforeClientAcquisition | ✅ |
| AC-30 | Views use the real shared effective-protection policy, including overrides and read-only denial. | ViewConfirmationCoverageTests.RegisteredWriter_UsesSharedPolicy, PolicyUnavailable_FailsClosedBeforeClientAcquisition | ✅ |
| AC-31 | All nine CLI adapters forward exact acknowledgement separately from profile/target/solution/publication; errors stay structured and reads need no acknowledgement. | ViewsCommandGroupTests.EveryMutation_ForwardsAcknowledgementAndOptions, EveryMutation_RejectsMismatchedTargetWithStructuredError, Reads_DoNotExposeAcknowledgement | ✅ |
| AC-32 | Acknowledgement and writer share captured session/identity/target and policy; another scope or changed policy cannot authorize the write. | ViewConfirmationCoverageTests.Acknowledgement_IsScopedAndPolicyRechecked | ✅ |
| AC-33 | Shakedown and cancellation stop all writers before client acquisition; read-only also denies no-op edits. | ViewConfirmationCoverageTests.EveryWriter_ShakedownAndCancellationStopBeforeClientAcquisition, EveryWriter_CancellationDuringPolicyReadStopsBeforeClientAcquisition, EveryWriter_ReadOnlyRejectsBeforeClientAcquisition | ✅ |
| AC-34 | Authorized reads, XML/layout/managed handling, verification and solution-before-publication behavior retain existing results. | ViewServiceTests.GetAsync_DefaultsToPublishedRead, SetFetchXml_ManagedView_WriteRejected_ThrowsManagedComponentNotEditableWithGuidance, SetFetchXml_ReadBackFails_ReportsUnverifiedWrite; ViewMetadataCorrectnessTests.RelatedColumns_RoundTripDistinctIdentities, WriteVerification_RejectsIncorrectState; ViewConfirmationCoverageTests.RegisteredWriter_UsesSharedPolicy | ✅ |
| AC-35 | Four-argument CLR constructor remains usable for reads and fails closed for writes; production DI resolves the guarded implementation. | ViewConfirmationCoverageTests.LegacyConstructor_PreservesSignatureAndReadAccess, RegisteredWriter_UsesSharedPolicy | ✅ |

### `--publish` Behavior

After a successful mutation, calls `PublishXmlRequest` with `<importexportxml><entities><entity>{entityLogicalName}</entity></entities></importexportxml>`.

### `--solution` Behavior

After a successful mutation, calls `AddSolutionComponentRequest` with component type 26 (Saved Query / `savedquery`) targeting the view's `savedqueryid` and the named solution. If the component is already in the solution the call is a no-op.

### Primary Flows

**List views:**
1. Resolve entity OTC from `ICachedMetadataProvider`
2. Query `savedqueries` with `returnedtypecode = OTC`
3. Map each record to `ViewInfo` with label from query type table
4. Return `ListResult<ViewInfo>` (I4: include total count)

**Get view:**
1. Query `savedqueries` by entity OTC + (`name = viewName` or `savedqueryid = viewId` when value is GUID-formatted) using ColumnSet `{savedqueryid, name, querytype, returnedtypecode, layoutxml, fetchxml, ismanaged, modifiedon}`
2. If result count == 0: throw `PpdsException(ErrorCodes.View.NotFound)`; if count > 1: throw `PpdsException(ErrorCodes.View.Ambiguous)`
3. Parse `layoutxml` → `ViewColumn` list
4. Parse `fetchxml` → `ViewSortOrder` list and `ViewFilter`
5. Return `ViewDetail`

**Column mutations (add/remove/update/reorder):**
1. Look up view: query `savedqueries` by entity OTC + (`name = viewName` or `savedqueryid = viewId` when value is GUID-formatted) using ColumnSet `{savedqueryid, layoutxml, fetchxml}`; if 0 results throw `View.NotFound`; if >1 throw `View.Ambiguous`
2. Parse `layoutxml` with `XDocument.Parse`
3. Apply mutation to `<cell>` elements inside `<row>`; for `remove-column`: if attribute not found throw `View.ColumnNotFound`; for `add-column`: if already present, emit warning to stderr and skip (idempotent)
4. For `add-column --via-relationship`: resolve relationship metadata via `ICachedMetadataProvider`; add/update `<link-entity>` + `<attribute>` in `fetchxml`; if relationship not found throw `View.RelationshipNotFound`
5. `UpdateAsync` the record with modified `layoutxml` (and `fetchxml` if changed)
6. If `--solution`: call `AddSolutionComponentRequest` (component type 26); then if `--publish`: call `PublishXmlRequest`

**Sort mutations:**
1. Look up view: query `savedqueries` by entity OTC + (`name = viewName` or `savedqueryid = viewId` when value is GUID-formatted) using ColumnSet `{savedqueryid, fetchxml}`; if 0 results throw `View.NotFound`; if >1 throw `View.Ambiguous`
2. Parse `fetchxml`; replace all `<order>` elements (or remove all for `clear-sort`)
3. `UpdateAsync` with modified `fetchxml`
4. If `--solution`: call `AddSolutionComponentRequest` (component type 26); then if `--publish`: call `PublishXmlRequest`

**Filter mutations:**
1. Look up view: query `savedqueries` by entity OTC + (`name = viewName` or `savedqueryid = viewId` when value is GUID-formatted) using ColumnSet `{savedqueryid, fetchxml}`; if 0 results throw `View.NotFound`; if >1 throw `View.Ambiguous`
2. Parse `fetchxml`; replace or remove `<filter>` child of `<entity>` (for `clear-filter`: remove)
3. For `--filter-file`: read file, parse as `XElement`, validate root element name is `filter`; if not throw `Validation.SchemaInvalid`
4. For `--condition`: parse `"attr:op:value"` into `<filter type="and"><condition attribute="attr" operator="op" value="value" /></filter>`
5. `UpdateAsync` with modified `fetchxml`
6. If `--solution`: call `AddSolutionComponentRequest` (component type 26); then if `--publish`: call `PublishXmlRequest`

**Set fetchxml:**
1. Look up view: query `savedqueries` by entity OTC + (`name = viewName` or `savedqueryid = viewId` when value is GUID-formatted) using ColumnSet `{savedqueryid}`; if 0 results throw `View.NotFound`; if >1 throw `View.Ambiguous`
2. Read file; parse as `XDocument`; validate root element name is `fetch`; if not throw `Validation.SchemaInvalid`
3. `UpdateAsync` with the provided `fetchxml` string
4. If `--solution`: call `AddSolutionComponentRequest` (component type 26); then if `--publish`: call `PublishXmlRequest`

### Constraints

- Use `IDataverseConnectionPool`; acquire → use → dispose within a single method scope (Constitution D1–D2)
- Never create `ServiceClient` directly
- `IProgressReporter` for all Dataverse calls
- `CancellationToken` threaded through all async calls
- No `Console.*` in `ViewService` (Constitution A1 + analyzer enforcement)

### Validation Rules

| Field | Rule | Error Code |
|-------|------|------------|
| `--entity` | Required, non-empty | `Validation.RequiredField` |
| `--view` (mutation commands) | Required, non-empty. Accepts name or GUID (with or without braces). GUID-formatted values query by `savedqueryid`; other values query by `name`. | `Validation.RequiredField` / `View.NotFound` |
| `--column` | Format `"name"` or `"name:width"` | `Validation.InvalidValue` |
| reorder `--columns` | Non-empty complete permutation of current layout names, case-insensitive; no empty segments or duplicates | `Validation.InvalidValue` |
| reorder layout | Exactly one row; non-empty, unique cell names, case-insensitive | `Validation.SchemaInvalid` |
| `--sort` | Format `"attr:asc"` or `"attr:desc"` | `Validation.InvalidValue` |
| `--condition` | Exactly 3 colon-separated segments | `Validation.InvalidValue` |
| `--filter-file` + `--condition` together | Mutually exclusive | `Validation.InvalidArguments` |
| `--filter-file` path | File exists | `Validation.FileNotFound` |
| `--filter-file` content | Well-formed XML with `<filter>` root | `Validation.SchemaInvalid` |
| `--fetchxml` path | File exists | `Validation.FileNotFound` |
| `--fetchxml` content | Well-formed XML with `<fetch>` root | `Validation.SchemaInvalid` |
| `--width` | Positive integer > 0 | `Validation.InvalidValue` |
| view name lookup | Resolves exactly one record | `View.NotFound` / `View.Ambiguous` |

---

## Acceptance Criteria

| ID | Criterion | Test | Status |
|----|-----------|------|--------|
| AC-01 | `ppds views list --entity X` displays all views with name, query type label, and ID | `ViewServiceTests.ListAsync_ReturnsViewInfo_WithQueryTypeLabel` | ❌ |
| AC-02 | `ppds views get --entity X --view "Name"` shows columns, sort order, and active filter | `ViewServiceTests.GetAsync_ReturnsViewDetail_WithColumnsAndSortAndFilter` | ❌ |
| AC-03 | `add-column` appends direct columns; width defaults to 150 if omitted | `ViewServiceTests.AddColumn_DirectColumn_DefaultWidth_Is150` | ❌ |
| AC-04 | `add-column --via-relationship` adds related entity columns via the lookup attribute | `ViewServiceTests.AddColumn_ViaRelationship_AddsRelatedCell` | ❌ |
| AC-05 | `remove-column` removes a column by attribute name | `ViewServiceTests.RemoveColumn_RemovesMatchingCell` | ❌ |
| AC-06 | `update-column --width N` updates the width of an existing column | `ViewServiceTests.UpdateColumn_SetsNewWidth` | ❌ |
| AC-07 | `reorder-columns --columns "a,b,c"` applies a complete permutation without dropping columns | `ViewServiceTests.ReorderColumns_SetsExactOrder` | ✅ |
| AC-08 | `set-sort` sets sort order; multiple `--sort` flags applied in declaration order | `ViewServiceTests.SetSort_MultipleFlags_AppliedInOrder` | ❌ |
| AC-09 | `clear-sort` removes all sort configuration | `ViewServiceTests.ClearSort_RemovesAllOrderElements` | ❌ |
| AC-10 | `set-filter --filter-file` applies a FetchXML `<filter>` fragment from file | `ViewServiceTests.SetFilter_FromFile_ReplacesFilter` | ❌ |
| AC-11 | `set-filter --condition "attr:op:val"` applies a single inline condition | `ViewServiceTests.SetFilter_FromCondition_BuildsFilterElement` | ❌ |
| AC-12 | `--filter-file` and `--condition` are mutually exclusive (parse error) | `ViewsCommandGroupTests.SetFilterSubcommand_FilterFileAndConditionAreMutuallyExclusive` | ❌ |
| AC-13 | `clear-filter` removes all filter conditions | `ViewServiceTests.ClearFilter_RemovesFilterElement` | ❌ |
| AC-14 | `set-fetchxml --fetchxml file` applies a complete FetchXML document | `ViewServiceTests.SetFetchXml_ReplacesFullFetchXml` | ❌ |
| AC-15 | `--publish` publishes the entity after the modification | `ViewServiceTests.Mutation_WithPublish_CallsPublishXmlRequest` | ❌ |
| AC-16 | `--solution` adds the view to the named solution if not already present | `ViewServiceTests.Mutation_WithSolution_CallsAddSolutionComponent` | ❌ |
| AC-17 | `ppds views --help` lists all eleven subcommands | `ViewsCommandGroupTests.Create_HasAllElevenSubcommands` | ❌ |
| AC-18 | Each subcommand has a non-empty description | `ViewsCommandGroupTests.AllSubcommands_HaveDescription` | ❌ |
| AC-19 | `set-fetchxml` help documents expected `<fetch>` XML format; `set-filter` help documents expected `<filter>` fragment format | `ViewsCommandGroupTests.SetFetchXmlSubcommand_HelpIncludesXmlExample`, `ViewsCommandGroupTests.SetFilterSubcommand_HelpIncludesXmlExample` | ❌ |
| AC-20 | `ViewService.ListAsync` wraps Dataverse exceptions in `PpdsException` with `View.ListFailed` | `ViewServiceTests.ListAsync_DataverseException_ThrowsPpdsException` | ❌ |
| AC-21 | Column already in view: `add-column` is idempotent (no-op, warning on stderr) | `ViewServiceTests.AddColumn_AlreadyExists_IsIdempotent` | ❌ |
| AC-22 | `remove-column` with nonexistent attribute throws `PpdsException` with `View.ColumnNotFound` | `ViewServiceTests.RemoveColumn_ColumnNotFound_ThrowsPpdsException` | ❌ |
| AC-23 | `GetAsync` throws `PpdsException` with `View.NotFound` when view name not found | `ViewServiceTests.GetAsync_ViewNotFound_ThrowsPpdsException` | ❌ |
| AC-24 | `UpdateAsync` failures (any mutation) are wrapped in `PpdsException` with `View.UpdateFailed` | `ViewServiceTests.MutationAsync_UpdateException_ThrowsPpdsException` | ❌ |

### Lossless reorder correction (#1533)

AC-29–35 above define the environment-confirmation contract enforced before layout acquisition, including reorder validation.

| ID | Criterion | Test | Status |
|---|---|---|---|
| AC-36 | Empty, partial, unknown, duplicate or blank input names fail with structured validation before any Update, Publish or solution write, even when those options are requested. | `ViewReorderSafetyTests.Reorder_InvalidPermutation_DoesNotMutate` | ✅ |
| AC-37 | Missing/ambiguous rows and missing, blank or case-insensitively duplicate layout names fail before mutation. | `ViewReorderSafetyTests.Reorder_AmbiguousLayout_DoesNotMutate` | ✅ |
| AC-38 | A full case-insensitive permutation, including qualified aliases, updates only cell order while preserving cell metadata, other layout nodes/settings and all FetchXML/query content; explicit remove-column remains separate. | `ViewReorderSafetyTests.Reorder_CompletePermutation_PreservesEverythingExceptCellOrder`, `RemoveColumn_RemainsExplicit` | ✅ |
| AC-39 | CLI parsing rejects empty comma-separated segments and duplicate names instead of silently removing them; help describes the complete-list requirement and separate removal command. | `ViewReorderCommandTests.Columns_InvalidSyntax_IsParseError`, `Columns_CompleteSyntax_Parses`, `Help_ExplainsLosslessReorder` | ✅ |
| AC-40 | Complete and partial reorder requests for an unconfirmed protected target fail before client/metadata acquisition; acknowledged complete requests retain the exact AC-38 payload and layout guarantees. | `ViewReorderSafetyTests.Reorder_UnconfirmedProtectedTarget_RejectsBeforeLayoutAcquisition`, `Reorder_CompletePermutation_PreservesEverythingExceptCellOrder` | ✅ |

### Edge Cases

| Scenario | Input | Expected Output |
|----------|-------|-----------------|
| No views for entity | `list --entity unknown_entity` | Empty list; `0 views found` (not error) |
| View name not found | `get --entity X --view "Ghost View"` | `View.NotFound` error |
| Multiple views with same name | `get --entity X --view "Dup"` | `View.Ambiguous` error |
| Column already in view | `add-column` with duplicate name | Idempotent: no-op + warning |
| Column not found for remove | `remove-column --column nonexistent` | `View.ColumnNotFound` error |
| Malformed filter XML | `set-filter --filter-file bad.xml` | `Validation.SchemaInvalid` error |
| fetchxml missing `<fetch>` root | `set-fetchxml --fetchxml bad.xml` | `Validation.SchemaInvalid` error |
| Both filter options supplied | `set-filter --filter-file x.xml --condition "a:eq:b"` | `Validation.InvalidArguments` error |
| Quick Find view modified | `add-column --view "Quick Find Active..."` | Allowed; no special handling in v1 — help text notes that Quick Find views have required fetch attributes |
| `--solution` + `--publish` together | Any mutation command | `AddSolutionComponentRequest` called first, then `PublishXmlRequest` |

---

## Core Types

### ViewInfo

```csharp
public record ViewInfo(
    Guid Id,
    string Name,
    int QueryType,
    string QueryTypeLabel,
    bool IsManaged,
    DateTime? ModifiedOn);
```

### ViewDetail

```csharp
public record ViewDetail(
    Guid Id,
    string Name,
    int QueryType,
    string QueryTypeLabel,
    string EntityLogicalName,
    IReadOnlyList<ViewColumn> Columns,
    IReadOnlyList<ViewSortOrder> SortOrders,
    ViewFilter? ActiveFilter);
```

### ViewColumn

```csharp
public record ViewColumn(
    string AttributeName,
    int Width,
    bool IsRelated = false,
    string? RelationshipAttribute = null,
    string? RelatedEntityName = null,
    string? RelatedEntityPrimaryKeyName = null);
```

### ViewSortOrder

```csharp
public record ViewSortOrder(string AttributeName, bool Descending);
```

### ViewFilter

```csharp
public record ViewFilter(string FetchXmlFragment);
```

### ColumnSpec

```csharp
public record ColumnSpec(string AttributeName, int Width = 150);
```

### IViewService

```csharp
public interface IViewService
{
    Task<ListResult<ViewInfo>> ListAsync(
        string entityLogicalName,
        IProgressReporter? progressReporter = null,
        CancellationToken cancellationToken = default);

    /// <summary>Throws View.NotFound if the view does not exist; throws View.Ambiguous if name matches multiple records.</summary>
    Task<ViewDetail> GetAsync(
        string entityLogicalName,
        string viewName,
        IProgressReporter? progressReporter = null,
        CancellationToken cancellationToken = default);

    Task AddColumnAsync(
        string entityLogicalName, string viewName,
        IReadOnlyList<ColumnSpec> columns,
        string? viaRelationship = null,
        bool publish = false, string? solution = null,
        IProgressReporter? progressReporter = null,
        CancellationToken cancellationToken = default);

    Task RemoveColumnAsync(
        string entityLogicalName, string viewName,
        string attributeName,
        bool publish = false, string? solution = null,
        IProgressReporter? progressReporter = null,
        CancellationToken cancellationToken = default);

    Task UpdateColumnAsync(
        string entityLogicalName, string viewName,
        string attributeName, int width,
        bool publish = false, string? solution = null,
        IProgressReporter? progressReporter = null,
        CancellationToken cancellationToken = default);

    Task ReorderColumnsAsync(
        string entityLogicalName, string viewName,
        IReadOnlyList<string> orderedAttributes,
        bool publish = false, string? solution = null,
        IProgressReporter? progressReporter = null,
        CancellationToken cancellationToken = default);

    Task SetSortAsync(
        string entityLogicalName, string viewName,
        IReadOnlyList<ViewSortOrder> sorts,
        bool publish = false, string? solution = null,
        IProgressReporter? progressReporter = null,
        CancellationToken cancellationToken = default);

    Task ClearSortAsync(
        string entityLogicalName, string viewName,
        bool publish = false, string? solution = null,
        IProgressReporter? progressReporter = null,
        CancellationToken cancellationToken = default);

    Task SetFilterAsync(
        string entityLogicalName, string viewName,
        string filterXmlFragment,
        bool publish = false, string? solution = null,
        IProgressReporter? progressReporter = null,
        CancellationToken cancellationToken = default);

    Task ClearFilterAsync(
        string entityLogicalName, string viewName,
        bool publish = false, string? solution = null,
        IProgressReporter? progressReporter = null,
        CancellationToken cancellationToken = default);

    Task SetFetchXmlAsync(
        string entityLogicalName, string viewName,
        string fetchXml,
        bool publish = false, string? solution = null,
        IProgressReporter? progressReporter = null,
        CancellationToken cancellationToken = default);
}
```

---

## Error Handling

### Error Types

> **Implementation note:** A new `static class View` must be added to `src/PPDS.Cli/Infrastructure/Errors/ErrorCodes.cs` with all `View.*` constants below. Do not use string literals.

| Error | Condition | Recovery |
|-------|-----------|----------|
| `View.NotFound` | No `savedqueries` record matches entity + name | Run `ppds views list --entity X` to verify name |
| `View.Ambiguous` | Multiple records match the view name | Use an exact, unique view name |
| `View.ColumnNotFound` | Attribute not present in `layoutxml` | Run `ppds views get` to inspect current columns |
| `View.RelationshipNotFound` | `--via-relationship` attribute not found in entity metadata | Verify relationship attribute name |
| `View.ListFailed` | Dataverse query for `savedqueries` failed | Check connectivity |
| `View.GetFailed` | Retrieve of view record failed | Check connectivity |
| `View.UpdateFailed` | PATCH to `savedqueries` failed | Check permissions on `savedqueries` entity |
| `View.PublishFailed` | `PublishXmlRequest` failed | Check customization permissions |
| `View.AddToSolutionFailed` | `AddSolutionComponentRequest` failed | Verify solution unique name |
| `Validation.SchemaInvalid` | Malformed XML or wrong root element | Fix the XML file |
| `Validation.InvalidValue` | Bad column/sort/condition syntax | Check format in `--help` |
| `Validation.InvalidArguments` | Mutually exclusive options supplied | Remove one of the conflicting flags |

---

## Design Decisions

### Why `System.Xml.Linq` over raw string manipulation?

**Context:** `layoutxml` and `fetchxml` are structured XML with attribute ordering and whitespace variation between environments. String manipulation is fragile.

**Decision:** Use `XDocument`/`XElement` (System.Xml.Linq) for all reads and writes. Parse on read, mutate elements, serialize on write.

**Alternatives considered:**
- `System.Xml.XmlDocument`: heavier DOM API; XLinq is cleaner for targeted mutation
- Regex/string replace: breaks on whitespace or attribute-order variation

**Consequences:**
- Positive: Correct for any valid XML Dataverse returns; easy to unit test with inline XML strings
- Negative: Slightly more code than string hacks; serialization preserves declaration on round-trip

### Why resolve OTC at runtime from `ICachedMetadataProvider`?

**Context:** The `<grid object="OTC">` attribute in `layoutxml` requires the entity's integer ObjectTypeCode. Custom entities have OTCs ≥ 10000 that cannot be hard-coded.

**Decision:** Call `ICachedMetadataProvider.GetEntitiesAsync()`, which is already warm for most sessions (shared cache), incurring zero extra API calls in the common path.

**Alternatives considered:**
- Hard-coded map of well-known entity OTCs: Fails for custom entities
- Per-call metadata query: Extra network round-trip when cache already populated

**Consequences:**
- Positive: Works for all entities; zero extra API calls when cache is warm
- Negative: `ViewService` requires `ICachedMetadataProvider` dependency

### Why `reorder-columns` uses `--columns "a,b,c"` (comma-separated) while `add-column` uses repeated flags?

**Context:** `reorder-columns` must express an ordered list of column names in a single atomic specification. `add-column` is an append operation where multiple flags are naturally order-independent (columns are appended in the order supplied).

**Decision:** `--columns` takes a single comma-separated complete permutation for `reorder-columns`. Repeated `--column` flags are for `add-column`. Reordering preserves membership; removal is explicit through `remove-column`.

**Alternatives considered:**
- Repeated `--column` flags for reorder: ambiguous about whether order is preserved across multiple invocations
- Both commands using comma-separated: inconsistent with the CLI convention where repeated flags are idiomatic for `add-*` operations

**Consequences:**
- Positive: Reorder is atomic and explicit; append is incremental and composable
- Negative: Two syntactic conventions for column specs; documented in `--help`

### Why `--condition` is limited to single-condition inline?

**Context:** Compound nested filter expressions (nested `<filter>` elements, `or` logic) cannot be concisely represented as a single CLI string without a custom DSL.

**Decision:** `--condition "attr:op:value"` handles single conditions only. Compound filters require `--filter-file`.

**Alternatives considered:**
- Rich inline DSL: Too complex to parse reliably; surprising for users
- No `--condition`: Too inconvenient for the most common case (`statecode:eq:0`)

**Consequences:**
- Positive: Simple and unambiguous; documented clearly in help text
- Negative: File required for compound filters (acceptable — compound filters should be version-controlled anyway)

---

## Related Specs

- [dataverse-services.md](./dataverse-services.md) — Connection pool and progress patterns
- [solutions.md](./solutions.md) — `--solution` integration via `AddSolutionComponentRequest`
- [cli.md](./cli.md) — CLI surface conventions

---

## Changelog

### Metadata correctness (#1448)

| AC | Requirement | Test | Status |
|---|---|---|---|
| AC-25 | Add/repair direct projections without changing other query semantics or all-attributes | `ViewMetadataCorrectnessTests.AddDirectColumn_RepairsProjection` | ✅ |
| AC-26 | Related columns use distinct alias-qualified layout identities and valid attributes across editing/inspection | `ViewMetadataCorrectnessTests.RelatedColumns_RoundTripDistinctIdentities` | ✅ |
| AC-27 | Verify every written XML field against intended state, including missing/concurrent/partial results | `ViewMetadataCorrectnessTests.WriteVerification_RejectsIncorrectState` | ✅ |
| AC-28 | Equivalent XML, platform-maintained fetch savedqueryid and no-ops verify successfully; unreadable results remain unverified | `ViewMetadataCorrectnessTests.WriteVerification_NormalizesPlatformAnnotation`, `ViewMetadataCorrectnessTests.WriteVerification_UnavailableReadIsExplicit` | ✅ |

| Date | Change |
|------|--------|
| 2026-05-31 | Initial spec |

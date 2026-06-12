# View Management

**Status:** Draft
**Last Updated:** 2026-05-31
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
| `GetCommand` | `ppds views get --entity --view [--unpublished]` — reads published by default; `--unpublished` shows the latest draft |
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

The `RelatedEntityPrimaryKeyName` used in `layoutxml` cell attributes is the related entity's primary key attribute (derived from relationship metadata as `ReferencedEntityIdAttribute`).

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

Base-entity columns use `<cell name="..." width="..." />`. Related columns carry additional relationship attributes:

```xml
<grid name="resultset" object="10050" jump="hsl_name" select="1" preview="1" icon="1">
  <row name="result" id="hsl_veterinarianid">
    <cell name="hsl_name" width="200" />
    <cell name="hsl_specialization" width="150" />
    <!-- Related column: -->
    <cell name="hsl_specialty" width="150" disableSorting="1" LookupStyle="1"
          RelatedEntityName="hsl_veterinarian"
          RelatedEntityPrimaryKeyName="hsl_veterinariandid"
          RelationshipName="hsl_veterinarian_id" />
  </row>
</grid>
```

The `object` attribute on `<grid>` is the entity's OTC, resolved from `ICachedMetadataProvider`.

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
| AC-07 | `reorder-columns --columns "a,b,c"` sets the authoritative column order | `ViewServiceTests.ReorderColumns_SetsExactOrder` | ❌ |
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

**Decision:** `--columns` takes a single comma-separated string for `reorder-columns`. Repeated `--column` flags are for `add-column`. The two commands have different semantic models: append vs. replace.

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

| Date | Change |
|------|--------|
| 2026-05-31 | Initial spec |

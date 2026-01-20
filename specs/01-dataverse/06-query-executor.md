# PPDS.Dataverse: Query Executor

## Overview

The Query Executor executes FetchXML queries against Dataverse and transforms SDK results into structured, JSON-serializable objects. It handles paging, column metadata extraction, value formatting (lookups, option sets, money), and provides both single-page and multi-page retrieval operations.

## Public API

### Interfaces

| Interface | Purpose |
|-----------|---------|
| `IQueryExecutor` | Executes FetchXML queries and returns structured results |

### Classes

| Class | Purpose |
|-------|---------|
| `QueryExecutor` | Implementation using connection pool and FetchExpression |

### DTOs/Models

| Type | Purpose |
|------|---------|
| `QueryResult` | Complete query result with records, columns, paging info |
| `QueryColumn` | Column metadata (name, alias, type, linked entity) |
| `QueryValue` | Value wrapper with raw value, formatting, lookup details |
| `QueryColumnType` | Enum of column data types |

## Behaviors

### Query Execution

1. **Parse FetchXML**: Extract entity name, aggregate flag, column metadata
2. **Apply paging**: Set page number and paging cookie if provided
3. **Execute**: Call `RetrieveMultiple` via pooled connection
4. **Map results**: Transform `EntityCollection` to structured records
5. **Return result**: Include columns, records, paging info, execution time

### Paging Modes

| Method | Behavior |
|--------|----------|
| `ExecuteFetchXmlAsync` | Single page with optional paging cookie for continuation |
| `ExecuteFetchXmlAllPagesAsync` | Automatic multi-page retrieval up to `maxRecords` (default 5000) |

### Column Extraction

Columns are extracted from FetchXML before execution:

| FetchXML Element | Column Properties |
|------------------|-------------------|
| `<attribute name="foo">` | LogicalName = "foo" |
| `<attribute name="foo" alias="bar">` | LogicalName = "foo", Alias = "bar" |
| `<attribute aggregate="count" alias="cnt">` | IsAggregate = true, AggregateFunction = "count" |
| `<link-entity alias="ref">...<attribute name="foo">` | LinkedEntityAlias = "ref", QualifiedName = "ref.foo" |
| `<all-attributes />` | Empty list; columns inferred from results |

### Value Mapping

SDK types are mapped to `QueryValue` with formatting:

| SDK Type | Value | FormattedValue | Additional |
|----------|-------|----------------|------------|
| `EntityReference` | Guid (ID) | Display name | `LookupEntityType`, `LookupEntityId` |
| `OptionSetValue` | int | Label text | - |
| `OptionSetValueCollection` | int[] | Comma-separated labels | - |
| `Money` | decimal | Currency-formatted | - |
| `bool` | true/false | "Yes"/"No" | - |
| `DateTime` | DateTime | Formatted string | - |
| `AliasedValue` | Unwrapped inner value | (recursive) | - |
| Primitives | As-is | null | - |

### All-Attributes Handling

When FetchXML uses `<all-attributes />`:
1. Column list is initially empty (unknown until results return)
2. After execution, columns are inferred by scanning all records
3. Necessary because Dataverse omits null attributes from responses
4. Columns sorted: ID columns first, then alphabetically

### Lifecycle

- **Initialization**: Executor receives connection pool via DI
- **Operation**: Each query acquires/releases connection from pool
- **Cleanup**: No persistent state; pool manages connection lifecycle

## Edge Cases

| Scenario | Behavior | Notes |
|----------|----------|-------|
| Invalid FetchXML | Throws `ArgumentException` | Missing entity/name |
| Empty result | Returns `QueryResult` with empty `Records` | Columns still populated |
| All attributes with empty result | Returns empty columns | No records to infer from |
| Null attribute in record | Value omitted by Dataverse | Handled gracefully |
| Multi-page with cancellation | Throws `OperationCanceledException` | Partial results discarded |
| Page number without cookie | Uses page number directly | Server handles |
| Large record set | Automatic paging with `ExecuteFetchXmlAllPagesAsync` | Respects `maxRecords` limit |

## Error Handling

| Exception | Condition | Recovery |
|-----------|-----------|----------|
| `ArgumentException` | Invalid FetchXML structure | Fix FetchXML |
| `FaultException<OrganizationServiceFault>` | Query execution error | Check FetchXML, permissions |
| `PoolExhaustedException` | No connection available | Retry with backoff |
| `OperationCanceledException` | User cancelled | Handle gracefully |

## Dependencies

- **Internal**:
  - `PPDS.Dataverse.Pooling.IDataverseConnectionPool` - Connection management
- **External**:
  - `Microsoft.Xrm.Sdk` (Entity, EntityCollection)
  - `Microsoft.Xrm.Sdk.Query` (FetchExpression)
  - `Microsoft.Extensions.Logging.Abstractions`
  - `System.Xml.Linq` (FetchXML parsing)

## Configuration

No configuration required. Default behaviors:

| Behavior | Default |
|----------|---------|
| `maxRecords` for all-pages | 5000 |
| Page size | Determined by FetchXML `count` attribute or Dataverse default (5000) |
| `returntotalrecordcount` | false unless `includeCount = true` |

## Thread Safety

- **QueryExecutor**: Thread-safe (stateless; each method acquires own connection)
- **QueryResult, QueryColumn, QueryValue**: Immutable; thread-safe

## QueryResult Properties

| Property | Type | Description |
|----------|------|-------------|
| `EntityLogicalName` | string | Primary entity name |
| `Columns` | IReadOnlyList&lt;QueryColumn&gt; | Column metadata |
| `Records` | IReadOnlyList&lt;IReadOnlyDict&lt;string, QueryValue&gt;&gt; | Record data |
| `Count` | int | Records in this page |
| `TotalCount` | int? | Total matching records (if requested) |
| `MoreRecords` | bool | More pages available |
| `PagingCookie` | string? | Cookie for next page |
| `PageNumber` | int | Current page (1-based) |
| `ExecutionTimeMs` | long | Query execution time |
| `ExecutedFetchXml` | string? | Actual FetchXML executed |
| `IsAggregate` | bool | Whether query uses aggregation |

### Static Factory

- `QueryResult.Empty(entityLogicalName)` - Creates empty result with no records

## QueryColumn Properties

| Property | Type | Description |
|----------|------|-------------|
| `LogicalName` | string | Attribute logical name |
| `Alias` | string? | Column alias in query |
| `DisplayName` | string? | Display name from metadata (if enriched) |
| `DataType` | QueryColumnType | Column data type |
| `LinkedEntityAlias` | string? | Alias of linked entity |
| `LinkedEntityName` | string? | Logical name of linked entity |
| `IsAggregate` | bool | Whether column uses aggregate |
| `AggregateFunction` | string? | Aggregate function (count, sum, etc.) |
| `EffectiveName` | string | Alias if set, else LogicalName (computed) |
| `QualifiedName` | string | LinkedEntityAlias.LogicalName or LogicalName (computed) |

## QueryValue Properties

| Property | Type | Description |
|----------|------|-------------|
| `Value` | object? | Raw value |
| `FormattedValue` | string? | Display representation |
| `LookupEntityType` | string? | Target entity for lookups |
| `LookupEntityId` | Guid? | Target record ID for lookups |
| `IsLookup` | bool | Is this a lookup value? |
| `IsOptionSet` | bool | Is this an option set value? |
| `IsBoolean` | bool | Is this a boolean value? |
| `HasFormattedValue` | bool | Does this have formatting? |

## Integration Points

### CLI Commands

- `ppds query` - Executes FetchXML or SQL queries
- `ppds sql` - Transpiles SQL to FetchXML, then executes

### TUI Views

- Query results grid uses `QueryResult` for display
- Column metadata used for header labels
- `FormattedValue` shown when available

### MCP Tools

- `dataverse-query` uses `ExecuteFetchXmlAsync`
- Results serialized to JSON using DTOs

## Performance Considerations

- **Connection pooling**: Each query uses pooled connection
- **Single-page default**: Avoids loading entire datasets
- **Column inference optimization**: Only scans records when using all-attributes
- **Lazy paging**: Multi-page method retrieves pages on demand
- **Execution timing**: Measured and included in result

## Related

- [Connection Pooling spec](./01-connection-pooling.md) - Provides connections
- [SQL Transpiler spec](./04-sql-transpiler.md) - Generates FetchXML from SQL
- [Metadata Service spec](./05-metadata-service.md) - Could enrich column metadata

## Source Files

| File | Purpose |
|------|---------|
| `src/PPDS.Dataverse/Query/IQueryExecutor.cs` | Executor interface |
| `src/PPDS.Dataverse/Query/QueryExecutor.cs` | Implementation |
| `src/PPDS.Dataverse/Query/QueryResult.cs` | Result DTO |
| `src/PPDS.Dataverse/Query/QueryColumn.cs` | Column metadata DTO |
| `src/PPDS.Dataverse/Query/QueryValue.cs` | Value wrapper DTO |
| `src/PPDS.Dataverse/Query/QueryColumnType.cs` | Column type enum |

# Query

**Status:** Implemented
**Version:** 1.0
**Last Updated:** 2026-01-23
**Code:** [src/PPDS.Dataverse/Query/](../src/PPDS.Dataverse/Query/), [src/PPDS.Dataverse/Sql/](../src/PPDS.Dataverse/Sql/), [src/PPDS.Cli/Services/Query/](../src/PPDS.Cli/Services/Query/), [src/PPDS.Cli/Services/History/](../src/PPDS.Cli/Services/History/)

---

## Overview

The query system enables SQL-like queries against Dataverse through automatic transpilation to FetchXML. It provides a full query pipeline: parsing SQL into an AST, transpiling to FetchXML with virtual column support, executing against Dataverse via the connection pool, and expanding results with formatted values. Query history tracks executed queries per environment for recall and re-execution.

### Goals

- **SQL Familiarity**: Query Dataverse using standard SQL syntax instead of FetchXML
- **Virtual Column Transparency**: Automatically handle Dataverse naming conventions (owneridname, statuscodename)
- **Formatted Values**: Preserve and expose display values for lookups, option sets, and booleans
- **Query History**: Track and recall queries per environment for iterative exploration

### Non-Goals

- Full SQL compatibility (subqueries, UNION, HAVING not supported)
- Query optimization (FetchXML is passed directly to Dataverse)
- Cross-environment queries (one connection pool per environment)
- OData query generation (FetchXML is the target format)

---

## Architecture

```
┌─────────────────────────────────────────────────────────────────────────────┐
│                              CLI Commands                                    │
│  ┌──────────────────┐  ┌──────────────────┐  ┌─────────────────────────┐   │
│  │ ppds query sql   │  │ ppds query fetch │  │ ppds query history *    │   │
│  └────────┬─────────┘  └────────┬─────────┘  └────────────┬────────────┘   │
│           │                     │                          │                │
└───────────┼─────────────────────┼──────────────────────────┼────────────────┘
            │                     │                          │
            ▼                     │                          ▼
┌───────────────────────────────┐ │           ┌──────────────────────────────┐
│      ISqlQueryService         │ │           │   IQueryHistoryService       │
│  ┌──────────────────────────┐ │ │           │  ┌─────────────────────────┐ │
│  │ TranspileSql()           │ │ │           │  │ ~/.ppds/history/{hash}  │ │
│  │ ExecuteAsync()           │ │ │           │  │ Max 200 entries/env     │ │
│  └───────────┬──────────────┘ │ │           │  └─────────────────────────┘ │
│              │                │ │           └──────────────────────────────┘
│              ▼                │ │
│  ┌───────────────────────────────────────────────────────────────────────┐ │
│  │                        SQL Processing Pipeline                         │ │
│  │  ┌─────────────┐   ┌──────────────┐   ┌─────────────────────────┐    │ │
│  │  │  SqlLexer   │──▶│  SqlParser   │──▶│ SqlToFetchXmlTranspiler │    │ │
│  │  │  (tokens)   │   │    (AST)     │   │  (FetchXML + virtuals)  │    │ │
│  │  └─────────────┘   └──────────────┘   └─────────────────────────┘    │ │
│  └───────────────────────────────────────────────────────────────────────┘ │
│              │                                                             │
│              ▼                                                             │
│  ┌───────────────────────────────────────────────────────────────────────┐ │
│  │                        IQueryExecutor                                  │ │
│  │  ExecuteFetchXmlAsync() ───▶ IDataverseConnectionPool                 │ │
│  └───────────────────────────────────────────────────────────────────────┘ │
│              │                                                             │
│              ▼                                                             │
│  ┌───────────────────────────────────────────────────────────────────────┐ │
│  │                    SqlQueryResultExpander                              │ │
│  │  Adds *name columns for lookups, optionsets, booleans                 │ │
│  └───────────────────────────────────────────────────────────────────────┘ │
└───────────────────────────────────────────────────────────────────────────┘
            │
            ▼
┌───────────────────────────────────────────────────────────────────────────┐
│                           QueryResult                                      │
│  Records, Columns, Paging (cookie, moreRecords), ExecutionTime            │
└───────────────────────────────────────────────────────────────────────────┘
```

### Components

| Component | Responsibility |
|-----------|----------------|
| `SqlLexer` | Tokenizes SQL input, preserves comments |
| `SqlParser` | Recursive descent parser producing `SqlSelectStatement` AST |
| `SqlToFetchXmlTranspiler` | Converts AST to FetchXML, detects virtual columns |
| `QueryExecutor` | Executes FetchXML via connection pool, maps results |
| `SqlQueryService` | Orchestrates parse → transpile → execute → expand |
| `SqlQueryResultExpander` | Adds formatted value columns to results |
| `QueryHistoryService` | Persists and retrieves query history per environment |

### Dependencies

- Depends on: [connection-pooling.md](./connection-pooling.md) for `IDataverseConnectionPool`
- Uses patterns from: [architecture.md](./architecture.md) for Application Services
- Consumed by: [mcp.md](./mcp.md) for AI assistant query tools

---

## Specification

### Core Requirements

1. **SQL must transpile to valid FetchXML**: Parser validates syntax; transpiler produces well-formed FetchXML
2. **Virtual columns are transparent**: Queries for `owneridname` automatically query `ownerid` and extract formatted value
3. **Paging is stateless**: Results include paging cookie for next page retrieval; no server-side cursor
4. **History deduplicates by normalized SQL**: Same query with different whitespace shares history entry

### Supported SQL Features

| Feature | SQL Syntax | FetchXML Mapping |
|---------|------------|------------------|
| Select columns | `SELECT name, revenue` | `<attribute name="..."/>` |
| Select all | `SELECT *` | `<all-attributes/>` |
| Aliases | `SELECT name AS n` | `alias="n"` |
| TOP/LIMIT | `SELECT TOP 10`, `LIMIT 10` | `<fetch top="10">` |
| DISTINCT | `SELECT DISTINCT` | `<fetch distinct="true">` |
| WHERE | `WHERE status = 1` | `<filter><condition.../></filter>` |
| AND/OR | `WHERE a = 1 AND b = 2` | `<filter type="and/or">` |
| IN | `WHERE status IN (1, 2)` | Multiple `<condition operator="in">` |
| LIKE | `WHERE name LIKE '%acme%'` | `<condition operator="like">` |
| IS NULL | `WHERE parent IS NULL` | `<condition operator="null">` |
| ORDER BY | `ORDER BY name DESC` | `<order attribute="name" descending="true"/>` |
| JOIN | `INNER JOIN contact ON...` | `<link-entity link-type="inner">` |
| COUNT/SUM/AVG/MIN/MAX | `COUNT(*)`, `SUM(revenue)` | `aggregate="count"`, `aggregate="sum"` |
| GROUP BY | `GROUP BY statecode` | `<attribute groupby="true"/>` |

### Unsupported SQL Features

- Subqueries (`SELECT * FROM (SELECT...)`)
- UNION/INTERSECT/EXCEPT
- HAVING clause
- Complex expressions (`revenue * 1.1`, `CASE WHEN`)
- Functions (`CONCAT()`, `UPPER()`)

### Primary Flows

**SQL Query Execution:**

1. **Parse**: `SqlLexer` tokenizes input → `SqlParser` builds `SqlSelectStatement` AST
2. **Transpile**: `SqlToFetchXmlTranspiler` converts AST to FetchXML, detecting virtual columns
3. **Execute**: `QueryExecutor.ExecuteFetchXmlAsync()` runs against Dataverse via pool
4. **Expand**: `SqlQueryResultExpander` adds `*name` columns from formatted values
5. **Return**: `SqlQueryResult` with original SQL, transpiled FetchXML, and expanded `QueryResult`

**Query History:**

1. **Execute Query**: After successful execution, SQL is normalized and stored
2. **Deduplicate**: Same normalized SQL updates existing entry timestamp/metadata
3. **Persist**: Atomic write to `~/.ppds/history/{environment-hash}.json`
4. **Recall**: History entries retrievable by ID or searchable by pattern

### Validation Rules

| Field | Rule | Error |
|-------|------|-------|
| SQL | Non-empty, valid syntax | `SqlParseException` with line/column |
| FROM entity | Must be valid Dataverse entity | Dataverse returns entity not found |
| Column names | Must exist on entity | Dataverse returns attribute not found |
| TOP value | Positive integer | `SqlParseException` |

---

## Core Types

### IQueryExecutor

Entry point for FetchXML execution with automatic paging support.

```csharp
public interface IQueryExecutor
{
    Task<QueryResult> ExecuteFetchXmlAsync(
        string fetchXml,
        int? pageNumber = null,
        string? pagingCookie = null,
        bool includeCount = false,
        CancellationToken cancellationToken = default);

    Task<QueryResult> ExecuteFetchXmlAllPagesAsync(
        string fetchXml,
        int maxRecords = 5000,
        CancellationToken cancellationToken = default);
}
```

The implementation ([`QueryExecutor.cs:37-122`](../src/PPDS.Dataverse/Query/QueryExecutor.cs#L37-L122)) parses FetchXML to extract column metadata, applies paging attributes, executes via `IDataverseConnectionPool`, and maps SDK entities to `QueryValue` dictionaries with formatted values preserved.

### QueryResult

Structured result containing records, column metadata, and paging information.

```csharp
public sealed class QueryResult
{
    public string EntityLogicalName { get; }
    public IReadOnlyList<QueryColumn> Columns { get; }
    public IReadOnlyList<IReadOnlyDictionary<string, QueryValue>> Records { get; }
    public int Count { get; }
    public bool MoreRecords { get; }
    public string? PagingCookie { get; }
    public int PageNumber { get; }
    public int? TotalCount { get; }
    public long ExecutionTimeMs { get; }
    public string? ExecutedFetchXml { get; }  // Transpiled FetchXML for SQL queries
    public bool IsAggregate { get; }
}
```

The `QueryColumn` type ([`QueryColumn.cs:1-63`](../src/PPDS.Dataverse/Query/QueryColumn.cs#L1-L63)) captures attribute name, alias, data type, aggregate function, and linked entity context.

### QueryValue

Wrapper preserving both raw value and formatted display text.

```csharp
public sealed class QueryValue
{
    public object? Value { get; }
    public string? FormattedValue { get; }
    public string? LookupEntityType { get; }
    public Guid? LookupEntityId { get; }

    public static QueryValue Lookup(Guid id, string type, string? name);
    public static QueryValue WithFormatting(object? value, string? formatted);
}
```

The implementation ([`QueryValue.cs:1-97`](../src/PPDS.Dataverse/Query/QueryValue.cs#L1-L97)) handles SDK value type conversions including `EntityReference`, `OptionSetValue`, `Money`, and `AliasedValue` for aggregates.

### SqlSelectStatement

Immutable AST representing a parsed SQL SELECT statement.

```csharp
public sealed class SqlSelectStatement
{
    public IReadOnlyList<ISqlSelectColumn> Columns { get; }
    public SqlTableRef From { get; }
    public IReadOnlyList<SqlJoin> Joins { get; }
    public ISqlCondition? Where { get; }
    public IReadOnlyList<SqlOrderByItem> OrderBy { get; }
    public int? Top { get; }
    public bool Distinct { get; }
}
```

The AST includes helper methods ([`SqlSelectStatement.cs:136-234`](../src/PPDS.Dataverse/Sql/SqlSelectStatement.cs#L136-L234)) for detecting aggregates, extracting table names, and replacing virtual columns.

### ISqlQueryService

Application service orchestrating the full SQL query pipeline.

```csharp
public interface ISqlQueryService
{
    string TranspileSql(string sql, int? topOverride = null);

    Task<SqlQueryResult> ExecuteAsync(
        SqlQueryRequest request,
        CancellationToken cancellationToken = default);
}
```

The implementation ([`SqlQueryService.cs:42-80`](../src/PPDS.Cli/Services/Query/SqlQueryService.cs#L42-L80)) orchestrates parsing, transpilation with virtual column detection, execution, and result expansion in a single operation.

### IQueryHistoryService

Manages per-environment query history with deduplication.

```csharp
public interface IQueryHistoryService
{
    Task<IReadOnlyList<QueryHistoryEntry>> GetHistoryAsync(
        string environmentUrl, int count = 50, CancellationToken ct = default);

    Task<QueryHistoryEntry> AddQueryAsync(
        string environmentUrl, string sql, int? rowCount = null,
        long? executionTimeMs = null, CancellationToken ct = default);

    Task<IReadOnlyList<QueryHistoryEntry>> SearchHistoryAsync(
        string environmentUrl, string pattern, int count = 50, CancellationToken ct = default);

    Task<QueryHistoryEntry?> GetEntryByIdAsync(
        string environmentUrl, string entryId, CancellationToken ct = default);

    Task<bool> DeleteEntryAsync(
        string environmentUrl, string entryId, CancellationToken ct = default);

    Task ClearHistoryAsync(
        string environmentUrl, CancellationToken ct = default);
}
```

The implementation ([`QueryHistoryService.cs:52-98`](../src/PPDS.Cli/Services/History/QueryHistoryService.cs#L52-L98)) normalizes SQL for deduplication, stores up to 200 entries per environment, and performs atomic writes via temp file rename.

---

## Error Handling

### Error Types

| Error | Condition | Recovery |
|-------|-----------|----------|
| `SqlParseException` | Invalid SQL syntax | Display line/column with context snippet |
| `FaultException` | Dataverse execution error | Map to `PpdsException`, show entity/attribute |
| `ThrottleException` | Service protection limits | Automatic retry via connection pool |
| `FileNotFoundException` | History file missing | Return empty list |

### SqlParseException Details

The parser provides rich error context ([`SqlParseException.cs:1-147`](../src/PPDS.Dataverse/Sql/Parsing/SqlParseException.cs#L1-L147)):

```csharp
public sealed class SqlParseException : Exception
{
    public int Position { get; }
    public int Line { get; }
    public int Column { get; }
    public string ContextSnippet { get; }

    // Example output:
    // Unexpected token 'WHEE' (Identifier) at line 2, column 15
    // Context: ...WHERE id = 5 WHEE...
}
```

### Recovery Strategies

- **Parse errors**: Display error with line/column context; user corrects SQL
- **Execution errors**: Map Dataverse fault codes to actionable messages
- **Throttle errors**: Transparent retry with exponential backoff (handled by pool)
- **History errors**: Log warning, continue without history tracking

### Edge Cases

| Scenario | Expected Behavior |
|----------|-------------------|
| Empty SQL | `SqlParseException` with position 0 |
| SELECT * on large entity | Warn about performance, execute normally |
| Virtual column + base column both requested | Return both, no duplicate expansion |
| History file corruption | Log error, start fresh history |
| Query with 0 results | Return empty Records, empty Columns |

---

## Design Decisions

### Why SQL Instead of FetchXML Direct?

**Context:** FetchXML is verbose and requires knowledge of Dataverse-specific syntax. SQL is universally known.

**Decision:** Provide SQL as the primary query interface with FetchXML as the execution target.

**Test results:**
| Metric | SQL | FetchXML |
|--------|-----|----------|
| Characters for simple query | 45 | 180 |
| Learning curve | Minimal | Significant |
| Autocomplete potential | High | Low |

**Alternatives considered:**
- OData: Rejected - less powerful than FetchXML for Dataverse features
- Raw FetchXML only: Rejected - poor developer experience
- Custom DSL: Rejected - unnecessary learning curve

**Consequences:**
- Positive: Familiar syntax, reduced learning curve
- Negative: SQL features must be explicitly supported

### Why Recursive Descent Parser?

**Context:** Need a maintainable parser that produces a clean AST for transpilation.

**Decision:** Hand-written recursive descent parser with explicit token handling.

**Alternatives considered:**
- Parser generator (ANTLR): Rejected - adds complexity, harder to debug
- Regex-based parsing: Rejected - can't handle SQL grammar
- Third-party SQL parser: Rejected - no control over Dataverse-specific features

**Consequences:**
- Positive: Full control, excellent error messages, comment preservation
- Negative: Manual maintenance for new SQL features

### Why Virtual Column Detection?

**Context:** Dataverse stores lookups as GUIDs but users often want display names. Querying `owneridname` directly fails.

**Decision:** Detect virtual columns (ending in `name`) at transpilation, query base column, populate from formatted values.

**Implementation** ([`SqlToFetchXmlTranspiler.cs:113-188`](../src/PPDS.Dataverse/Sql/Transpilation/SqlToFetchXmlTranspiler.cs#L113-L188)):
- Patterns: `*idname`, `*codename`, `*typename`
- Explicit patterns: `statecodename`, `statuscodename`, `is*name`, `do*name`, `has*name`

**Consequences:**
- Positive: Users can query naturally (`owneridname`), results include display values
- Negative: Additional processing overhead, potential for pattern false positives

### Why Per-Environment History?

**Context:** Users work with multiple Dataverse environments. Mixing history would cause confusion.

**Decision:** Store history per environment using URL hash for filename isolation.

**Storage:** `~/.ppds/history/{sha256(url)[:16]}.json`

**Consequences:**
- Positive: Clean separation, no cross-environment confusion
- Negative: Users must reconnect to see environment-specific history

---

## Configuration

| Setting | Type | Required | Default | Description |
|---------|------|----------|---------|-------------|
| History max entries | int | No | 200 | Maximum entries per environment before trimming |
| History location | path | No | `~/.ppds/history/` | Directory for history JSON files |
| Default TOP | int | No | None | Default row limit if not specified |

---

## Testing

### Acceptance Criteria

- [ ] SQL queries transpile to valid FetchXML
- [ ] Virtual columns (`owneridname`) resolve to formatted values
- [ ] Paging works across multiple pages with cookies
- [ ] History deduplicates on normalized SQL
- [ ] Parse errors include line/column information

### Edge Cases

| Scenario | Input | Expected Output |
|----------|-------|-----------------|
| Empty SELECT | `SELECT FROM account` | Parse error at column 8 |
| Unknown operator | `SELECT * FROM account WHERE name ~ 'test'` | Parse error at `~` |
| Large result set | SELECT on 50k record entity | Paging with 5000-record pages |
| Comment preservation | `-- get accounts\nSELECT *` | Comment in FetchXML output |

### Test Coverage

**Unit Tests (200+ test facts):**
- `SqlLexerTests.cs`: Tokenization, comments, operators
- `SqlParserTests.cs`: AST construction, error handling
- `SqlToFetchXmlTranspilerTests.cs`: FetchXML generation, virtual columns
- `SqlQueryServiceTests.cs`: Service orchestration
- `QueryHistoryServiceTests.cs`: Persistence, deduplication

**Integration Tests:**
- `QueryExecutionTests.cs`: Paging, filtering with FakeXrmEasy
- `AggregateQueryTests.cs`: COUNT, SUM, AVG, MIN, MAX

### Test Example

```csharp
[Fact]
public void Transpile_SelectWithJoin_GeneratesLinkEntity()
{
    var sql = "SELECT a.name, c.fullname " +
              "FROM account a " +
              "INNER JOIN contact c ON c.parentcustomerid = a.accountid";

    var result = _transpiler.Transpile(_parser.Parse(sql));

    result.FetchXml.Should().Contain("<link-entity");
    result.FetchXml.Should().Contain("link-type=\"inner\"");
    result.FetchXml.Should().Contain("from=\"parentcustomerid\"");
}
```

---

## Related Specs

- [connection-pooling.md](./connection-pooling.md) - Query execution uses pooled connections
- [architecture.md](./architecture.md) - `ISqlQueryService` follows Application Services pattern
- [mcp.md](./mcp.md) - MCP tools expose query capabilities to AI assistants
- [cli.md](./cli.md) - CLI commands for `ppds query sql|fetch|history`

---

## Roadmap

- **Query builder TUI**: Interactive query construction with autocomplete
- **Explain plan**: Show how FetchXML will be executed
- **Query templates**: Parameterized saved queries
- **Cross-entity unions**: Combine results from multiple entity queries

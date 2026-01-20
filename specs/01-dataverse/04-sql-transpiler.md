# PPDS.Dataverse: SQL Transpiler

## Overview

The SQL Transpiler converts a subset of SQL SELECT syntax into Dataverse FetchXML, enabling developers to use familiar SQL syntax for querying Dataverse. It consists of a lexer, recursive descent parser, AST representation, and FetchXML generator with support for virtual column detection.

## Public API

### Classes

| Class | Purpose |
|-------|---------|
| `SqlParser` | Recursive descent parser for SQL SELECT statements |
| `SqlLexer` | Tokenizes SQL strings into tokens |
| `SqlToFetchXmlTranspiler` | Converts SQL AST to FetchXML string |

### AST Types

| Type | Purpose |
|------|---------|
| `SqlSelectStatement` | Complete SELECT statement AST |
| `SqlColumnRef` | Column reference (qualified, simple, or wildcard) |
| `SqlAggregateColumn` | Aggregate function call (COUNT, SUM, etc.) |
| `SqlTableRef` | Table reference with optional alias |
| `SqlJoin` | JOIN clause (INNER, LEFT, RIGHT) |
| `SqlOrderByItem` | ORDER BY column with direction |
| `SqlCondition` | WHERE condition (comparison, LIKE, IS NULL, IN) |
| `SqlLogicalCondition` | AND/OR logical combination |
| `SqlLiteral` | Literal value (string, number, null) |

### DTOs/Models

| Type | Purpose |
|------|---------|
| `SqlToken` | Lexer token (type, value, position) |
| `SqlComment` | Captured comment for preservation |
| `SqlLexerResult` | Tokens and comments from lexing |
| `TranspileResult` | FetchXML string plus virtual column metadata |
| `VirtualColumnInfo` | Info about virtual *name columns |

### Exceptions

| Type | Purpose |
|------|---------|
| `SqlParseException` | Parse error with line/column info |

## Supported SQL Syntax

### SELECT Clause

| Syntax | FetchXML Output | Notes |
|--------|-----------------|-------|
| `SELECT *` | `<all-attributes />` | |
| `SELECT column` | `<attribute name="column" />` | |
| `SELECT table.column` | `<attribute name="column" />` | Qualified reference |
| `SELECT column AS alias` | `<attribute name="column" alias="alias" />` | |
| `SELECT DISTINCT` | `distinct="true"` on fetch | |
| `SELECT TOP n` | `top="n"` on fetch | |
| `SELECT COUNT(*)` | `aggregate="count"` on primary key | |
| `SELECT COUNT(column)` | `aggregate="countcolumn"` | |
| `SELECT COUNT(DISTINCT column)` | `aggregate="countcolumn" distinct="true"` | |
| `SELECT SUM/AVG/MIN/MAX(column)` | `aggregate="sum/avg/min/max"` | |

### FROM Clause

| Syntax | FetchXML Output |
|--------|-----------------|
| `FROM entity` | `<entity name="entity">` |
| `FROM entity e` | `<entity name="entity">` (alias tracked) |

### JOIN Clause

| Syntax | FetchXML Output |
|--------|-----------------|
| `INNER JOIN table ON left = right` | `link-type="inner"` |
| `LEFT [OUTER] JOIN table ON left = right` | `link-type="outer"` |
| `RIGHT [OUTER] JOIN table ON left = right` | `link-type="outer"` (columns reversed) |

### WHERE Clause

| Syntax | FetchXML Output |
|--------|-----------------|
| `column = value` | `operator="eq"` |
| `column <> value` or `!= value` | `operator="ne"` |
| `column < value` | `operator="lt"` |
| `column > value` | `operator="gt"` |
| `column <= value` | `operator="le"` |
| `column >= value` | `operator="ge"` |
| `column IS NULL` | `operator="null"` |
| `column IS NOT NULL` | `operator="not-null"` |
| `column LIKE '%text%'` | `operator="like"` |
| `column LIKE 'text%'` | `operator="begins-with"` |
| `column LIKE '%text'` | `operator="ends-with"` |
| `column NOT LIKE` | `operator="not-like"` (etc.) |
| `column IN (v1, v2)` | `operator="in"` with values |
| `column NOT IN (v1, v2)` | `operator="not-in"` with values |
| `condition AND condition` | `<filter type="and">` |
| `condition OR condition` | `<filter type="or">` |
| `(condition)` | Parentheses for grouping |

### ORDER BY Clause

| Syntax | FetchXML Output |
|--------|-----------------|
| `ORDER BY column` | `<order attribute="column" descending="false" />` |
| `ORDER BY column ASC` | `<order attribute="column" descending="false" />` |
| `ORDER BY column DESC` | `<order attribute="column" descending="true" />` |

### GROUP BY Clause

| Syntax | FetchXML Output |
|--------|-----------------|
| `GROUP BY column` | `groupby="true"` on attribute |

### LIMIT Clause

| Syntax | FetchXML Output | Notes |
|--------|-----------------|-------|
| `LIMIT n` | `top="n"` on fetch | Alternative to TOP |

## Not Supported

- Subqueries
- UNION / INTERSECT / EXCEPT
- HAVING clause
- OFFSET
- CROSS JOIN
- FULL OUTER JOIN

## Behaviors

### Parsing Pipeline

1. **Lexing**: `SqlLexer.Tokenize()` → tokens + comments
2. **Parsing**: `SqlParser.Parse()` → `SqlSelectStatement` AST
3. **Transpilation**: `SqlToFetchXmlTranspiler.Transpile()` → FetchXML

### Virtual Column Detection

The transpiler detects "*name" columns (e.g., `owneridname`) that map to lookup/optionset base columns:

1. Check if column ends with "name" and has recognizable base pattern
2. Replace virtual column request with base column in FetchXML
3. Return `VirtualColumnInfo` for post-processing by query executor

Recognized patterns:
- Lookup columns: `*id` + `name` (e.g., `owneridname` → `ownerid`)
- Status columns: `statecode`, `statuscode` + `name`
- Code/Type columns: `*code`, `*type` + `name`
- Boolean patterns: `is*`, `do*`, `has*` + `name`

### Comment Preservation

SQL comments are captured with positions and output as XML comments:

```sql
-- Leading comment
SELECT name -- trailing
FROM account
```

Transpiles to:

```xml
<!-- Leading comment -->
<fetch>
  <entity name="account">
    <attribute name="name" />
    <!-- trailing -->
  </entity>
</fetch>
```

### Name Normalization

- Entity names: Lowercased (Dataverse entities are case-insensitive)
- Attribute names: Lowercased
- Aliases: Preserved as-is (user-specified)

## Edge Cases

| Scenario | Behavior | Notes |
|----------|----------|-------|
| Trailing comma | Tolerated | Better UX |
| Keyword as alias | Allowed after AS | e.g., `name AS count` |
| Empty string literal | Valid | `''` → empty value |
| Escaped single quote | Supported | `'it''s'` → `it's` |
| Negative numbers | Supported | `-123`, `-45.67` |
| Bracketed identifiers | Supported | `[My Column]` |
| Double-quoted identifiers | Supported | `"My Column"` |
| COUNT(*) | Uses primary key | FetchXML requires column |

## Error Handling

| Exception | Condition | Contains |
|-----------|-----------|----------|
| `SqlParseException` | Any parse error | Line, column, context snippet |
| "Unexpected character" | Invalid character in SQL | Position info |
| "Unterminated string" | Missing closing quote | Position info |
| "Expected X, found Y" | Syntax error | Token info |

### Error Message Format

```
Expected Identifier, found Keyword at line 1, column 8
Context: ...SELECT FROM account...
```

## Dependencies

- **Internal**: None (leaf component)
- **External**: None (pure .NET, no external packages)

## Configuration

No configuration required. The transpiler is stateless.

## Thread Safety

- **SqlLexer**: Not thread-safe; create per parse
- **SqlParser**: Not thread-safe; create per parse
- **SqlToFetchXmlTranspiler**: Not thread-safe (uses instance counter); create per transpilation
- **Static methods**: Thread-safe (`SqlParser.Parse()`, `SqlToFetchXmlTranspiler.TranspileSql()`)

## Performance Considerations

- **Single-pass lexing**: O(n) token generation
- **Recursive descent parsing**: O(n) for supported grammar
- **No allocations in hot paths**: Uses StringBuilder for string building
- **Comment capture**: Minimal overhead, optional for most use cases

## Extension Points

- **AST methods**: `SqlSelectStatement.WithTop()`, `WithAdditionalColumns()`, `WithVirtualColumnsReplaced()` for AST transformation
- **Virtual column detection**: Extensible pattern matching in transpiler

## Related

- [Query Executor spec](./06-query-executor.md) - Uses transpiler for SQL queries
- [Metadata Service spec](./05-metadata-service.md) - Provides schema for validation

## Source Files

| File | Purpose |
|------|---------|
| `src/PPDS.Dataverse/Sql/Parsing/SqlLexer.cs` | Tokenizer |
| `src/PPDS.Dataverse/Sql/Parsing/SqlParser.cs` | Recursive descent parser |
| `src/PPDS.Dataverse/Sql/Parsing/SqlToken.cs` | Token type |
| `src/PPDS.Dataverse/Sql/Parsing/SqlTokenType.cs` | Token type enum |
| `src/PPDS.Dataverse/Sql/Parsing/SqlParseException.cs` | Parse exception |
| `src/PPDS.Dataverse/Sql/Ast/SqlSelectStatement.cs` | SELECT statement AST |
| `src/PPDS.Dataverse/Sql/Ast/SqlColumnRef.cs` | Column reference AST |
| `src/PPDS.Dataverse/Sql/Ast/SqlAggregateColumn.cs` | Aggregate column AST |
| `src/PPDS.Dataverse/Sql/Ast/SqlTableRef.cs` | Table reference AST |
| `src/PPDS.Dataverse/Sql/Ast/SqlJoin.cs` | JOIN clause AST |
| `src/PPDS.Dataverse/Sql/Ast/SqlCondition.cs` | Condition AST types |
| `src/PPDS.Dataverse/Sql/Ast/SqlLiteral.cs` | Literal value AST |
| `src/PPDS.Dataverse/Sql/Ast/SqlOrderByItem.cs` | ORDER BY AST |
| `src/PPDS.Dataverse/Sql/Ast/SqlEnums.cs` | Enums (operator, direction) |
| `src/PPDS.Dataverse/Sql/Transpilation/SqlToFetchXmlTranspiler.cs` | FetchXML generator |
| `src/PPDS.Dataverse/Sql/Transpilation/TranspileResult.cs` | Result with virtual columns |

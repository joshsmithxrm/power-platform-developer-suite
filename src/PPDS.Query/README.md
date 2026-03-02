# PPDS.Query

Production-grade SQL query engine for Dataverse with FetchXML transpilation and ADO.NET provider.

## Installation

```bash
dotnet add package PPDS.Query
```

## Quick Start - ADO.NET Provider

```csharp
using PPDS.Query.Provider;

await using var connection = new PpdsDbConnection(connectionPool);
await connection.OpenAsync();

await using var command = connection.CreateCommand();
command.CommandText = @"
    SELECT a.name, a.revenue, c.fullname
    FROM account a
    INNER JOIN contact c ON c.parentcustomerid = a.accountid
    WHERE a.statecode = @statecode
    ORDER BY a.revenue DESC";

command.Parameters.AddWithValue("@statecode", 0);

await using var reader = await command.ExecuteReaderAsync();
while (await reader.ReadAsync())
{
    var name = reader.GetString(0);
    var revenue = reader.IsDBNull(1) ? 0m : reader.GetDecimal(1);
    var contact = reader.GetString(2);
    Console.WriteLine($"{name} ({revenue:C}) - {contact}");
}
```

## Quick Start - CLI

```bash
# Simple query
ppds query sql "SELECT name, revenue FROM account WHERE statecode = 0 ORDER BY revenue DESC"

# Join across entities
ppds query sql "SELECT a.name, c.fullname FROM account a JOIN contact c ON c.parentcustomerid = a.accountid"

# Aggregate query
ppds query sql "SELECT ownerid, COUNT(*) as total FROM account GROUP BY ownerid HAVING COUNT(*) > 10"

# Output as CSV
ppds query sql "SELECT name, emailaddress1 FROM contact" -f csv

# View the execution plan
ppds query explain "SELECT name FROM account WHERE revenue > 1000000"
```

## Features

### SQL Language Support

Full T-SQL syntax parsed via Microsoft.SqlServer.TransactSql.ScriptDom:

- **SELECT** with columns, aliases, TOP, DISTINCT, computed expressions
- **WHERE** with all comparison operators, LIKE, IN, BETWEEN, IS NULL, IS NOT DISTINCT FROM
- **JOIN** — INNER, LEFT, RIGHT, FULL OUTER, CROSS, OUTER APPLY
- **GROUP BY** with aggregates: COUNT, SUM, AVG, MIN, MAX, STDEV, STDEVP, VAR, VARP
- **HAVING** clause with aggregate predicates
- **ORDER BY** with ASC/DESC
- **UNION / UNION ALL**
- **Subqueries** — IN, NOT IN, EXISTS, NOT EXISTS, derived tables, scalar subqueries
- **Window functions** — ROW_NUMBER, RANK, DENSE_RANK, CUME_DIST, PERCENT_RANK
- **CTEs** — Common Table Expressions including recursive CTEs
- **CASE/WHEN/THEN/ELSE** and **IIF** expressions
- **Built-in functions** — ISNULL, COALESCE, NULLIF, CAST, string functions, date functions (DATEADD, DATEDIFF, DATEPART, YEAR, MONTH, DAY, GETDATE, GETUTCDATE, TIMEFROMPARTS)
- **JSON** — OPENJSON table-valued function, JSON_MODIFY with array paths
- **Scripting** — DECLARE/SET variables, IF/ELSE, WHILE/BREAK/CONTINUE, SELECT INTO #temp

### Execution Engine

The query engine uses a Volcano iterator model for streaming results:

```
SQL Text
  -> ScriptDom Parser -> SQL AST
  -> Query Planner -> Execution Plan
  -> FetchXML Transpiler -> FetchXML (pushed down to Dataverse)
  -> Volcano Iterators -> Streaming rows
```

- **FetchXML pushdown** — Filters, joins, and aggregates are pushed to Dataverse when possible
- **Client-side fallback** — Hash, merge, and nested-loop joins for queries that exceed FetchXML capabilities
- **Prefetch buffering** — Page-ahead scan node for improved streaming throughput
- **TDS Endpoint routing** — Automatically routes compatible queries to the SQL endpoint
- **Parallel partitioned aggregates** — Accurate COUNT(*) beyond the Dataverse 50K limit via date-range partitioning
- **Adaptive retry** — Binary date-range splitting when partitions exceed limits
- **EXPLAIN** — Inspect query execution plans with pool and parallelism metadata

### Cross-Environment Queries

Query across Dataverse environments using bracket syntax:

```sql
-- Compare account counts across environments
SELECT 'Dev' as env, COUNT(*) as total FROM [dev-org].[account]
UNION ALL
SELECT 'Prod' as env, COUNT(*) as total FROM [prod-org].[account]
```

- **Bracket syntax** — `[environment].[entity]` for cross-environment references
- **Smart label detection** — Resolves 2-part names against configured environment labels
- **Remote execution** — RemoteScanNode executes FetchXML on remote environments

### DML Support

Data modification with safety guards:

```sql
-- Insert with values
INSERT INTO account (name, revenue) VALUES ('Contoso', 1000000)

-- Insert from query
INSERT INTO account (name, revenue)
SELECT name, revenue FROM #staged_accounts

-- Update with filter
UPDATE contact SET jobtitle = 'Senior Developer' WHERE jobtitle = 'Developer'

-- Delete with filter
DELETE FROM account WHERE statecode = 1 AND modifiedon < '2024-01-01'
```

### Safety Features

All DML operations are protected by configurable safety guards:

- **Environment protection levels** — Prevent accidental writes to production environments
- **Row caps** — Configurable maximum rows affected per DML statement
- **Structured error codes** — QueryExecutionException with error codes for programmatic handling
- **@@ERROR / ERROR_MESSAGE()** — Error state tracking within script sessions

## Target Frameworks

- `net8.0`
- `net9.0`
- `net10.0`

## License

MIT License

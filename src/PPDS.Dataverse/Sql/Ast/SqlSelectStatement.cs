using System;
using System.Collections.Generic;
using System.Linq;

namespace PPDS.Dataverse.Sql.Ast;

/// <summary>
/// Complete SQL SELECT statement AST.
/// </summary>
public sealed class SqlSelectStatement : ISqlStatement
{
    /// <inheritdoc />
    public int SourcePosition { get; }

    /// <summary>
    /// The columns in the SELECT clause.
    /// </summary>
    public IReadOnlyList<ISqlSelectColumn> Columns { get; }

    /// <summary>
    /// The table in the FROM clause.
    /// </summary>
    public SqlTableRef From { get; }

    /// <summary>
    /// The JOIN clauses.
    /// </summary>
    public IReadOnlyList<SqlJoin> Joins { get; }

    /// <summary>
    /// The WHERE clause condition, if present.
    /// </summary>
    public ISqlCondition? Where { get; }

    /// <summary>
    /// The ORDER BY clause items.
    /// </summary>
    public IReadOnlyList<SqlOrderByItem> OrderBy { get; }

    /// <summary>
    /// The TOP/LIMIT value, if specified.
    /// </summary>
    public int? Top { get; }

    /// <summary>
    /// Whether DISTINCT is specified.
    /// </summary>
    public bool Distinct { get; }

    /// <summary>
    /// The GROUP BY columns (simple column references).
    /// </summary>
    public IReadOnlyList<SqlColumnRef> GroupBy { get; }

    /// <summary>
    /// GROUP BY expressions that are function calls (e.g., YEAR(createdon), MONTH(createdon)).
    /// These are used for FetchXML dategrouping pushdown. Empty when GROUP BY has only simple columns.
    /// </summary>
    public IReadOnlyList<ISqlExpression> GroupByExpressions { get; }

    /// <summary>
    /// The HAVING clause condition, if present.
    /// </summary>
    public ISqlCondition? Having { get; }

    /// <summary>
    /// Comments that appear before the SELECT keyword.
    /// </summary>
    public List<string> LeadingComments { get; } = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="SqlSelectStatement"/> class.
    /// </summary>
    public SqlSelectStatement(
        IReadOnlyList<ISqlSelectColumn> columns,
        SqlTableRef from,
        IReadOnlyList<SqlJoin>? joins = null,
        ISqlCondition? where = null,
        IReadOnlyList<SqlOrderByItem>? orderBy = null,
        int? top = null,
        bool distinct = false,
        IReadOnlyList<SqlColumnRef>? groupBy = null,
        ISqlCondition? having = null,
        int sourcePosition = 0,
        IReadOnlyList<ISqlExpression>? groupByExpressions = null)
    {
        Columns = columns ?? throw new ArgumentNullException(nameof(columns));
        From = from ?? throw new ArgumentNullException(nameof(from));
        Joins = joins ?? Array.Empty<SqlJoin>();
        Where = where;
        OrderBy = orderBy ?? Array.Empty<SqlOrderByItem>();
        Top = top;
        Distinct = distinct;
        GroupBy = groupBy ?? Array.Empty<SqlColumnRef>();
        GroupByExpressions = groupByExpressions ?? Array.Empty<ISqlExpression>();
        Having = having;
        SourcePosition = sourcePosition;
    }

    /// <summary>
    /// Gets the primary entity name (from FROM clause).
    /// </summary>
    public string GetEntityName() => From.TableName;

    /// <summary>
    /// Checks if this is a SELECT * query.
    /// </summary>
    public bool IsSelectAll()
    {
        return Columns.Count == 1
            && Columns[0] is SqlColumnRef { IsWildcard: true, TableName: null };
    }

    /// <summary>
    /// Checks if this query contains aggregate functions.
    /// </summary>
    public bool HasAggregates()
    {
        return Columns.Any(col => col is SqlAggregateColumn);
    }

    /// <summary>
    /// Gets only the regular (non-aggregate) columns.
    /// </summary>
    public IEnumerable<SqlColumnRef> GetRegularColumns()
    {
        return Columns.OfType<SqlColumnRef>();
    }

    /// <summary>
    /// Gets only the aggregate columns.
    /// </summary>
    public IEnumerable<SqlAggregateColumn> GetAggregateColumns()
    {
        return Columns.OfType<SqlAggregateColumn>();
    }

    /// <summary>
    /// Gets all table/alias names referenced in the query.
    /// </summary>
    public IReadOnlyList<string> GetTableNames()
    {
        var names = new List<string> { From.GetEffectiveName() };
        names.AddRange(Joins.Select(j => j.Table.GetEffectiveName()));
        return names;
    }

    /// <summary>
    /// Checks if this query has a row limit (TOP or LIMIT clause).
    /// </summary>
    public bool HasRowLimit() => Top.HasValue;

    /// <summary>
    /// Creates a new statement with a different TOP value.
    /// </summary>
    /// <param name="top">The new TOP value. Use null to remove the limit.</param>
    /// <returns>A new statement with the updated TOP value.</returns>
    public SqlSelectStatement WithTop(int? top)
    {
        var newStatement = new SqlSelectStatement(
            Columns,
            From,
            Joins,
            Where,
            OrderBy,
            top,
            Distinct,
            GroupBy,
            Having,
            SourcePosition,
            GroupByExpressions);
        newStatement.LeadingComments.AddRange(LeadingComments);
        return newStatement;
    }

    /// <summary>
    /// Creates a new statement with additional columns added.
    /// Used for virtual column transformation - adds parent columns for virtual fields.
    /// </summary>
    public SqlSelectStatement WithAdditionalColumns(IEnumerable<string> additionalColumns)
    {
        var additionalList = additionalColumns.ToList();
        if (additionalList.Count == 0)
        {
            return this;
        }

        var newColumns = Columns.ToList();
        newColumns.AddRange(additionalList.Select(name => SqlColumnRef.Simple(name)));

        var newStatement = new SqlSelectStatement(
            newColumns,
            From,
            Joins,
            Where,
            OrderBy,
            Top,
            Distinct,
            GroupBy,
            Having,
            SourcePosition,
            GroupByExpressions);
        newStatement.LeadingComments.AddRange(LeadingComments);
        return newStatement;
    }

    /// <summary>
    /// Creates a new statement with virtual columns replaced by their parent columns.
    /// Used for transparent virtual column transformation.
    /// </summary>
    /// <param name="virtualToParent">Map of virtual column names to parent column names (case-insensitive keys).</param>
    public SqlSelectStatement WithVirtualColumnsReplaced(Dictionary<string, string> virtualToParent)
    {
        if (virtualToParent.Count == 0)
        {
            return this;
        }

        var seenParents = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var newColumns = new List<ISqlSelectColumn>();

        foreach (var col in Columns)
        {
            if (col is SqlColumnRef { IsWildcard: false } columnRef)
            {
                if (virtualToParent.TryGetValue(columnRef.ColumnName, out var parentName))
                {
                    // This is a virtual column - replace with parent (if not already added)
                    if (!seenParents.Contains(parentName))
                    {
                        seenParents.Add(parentName);
                        newColumns.Add(new SqlColumnRef(columnRef.TableName, parentName, null, false));
                    }
                }
                else
                {
                    // Regular column - keep as-is
                    newColumns.Add(col);
                    seenParents.Add(columnRef.ColumnName);
                }
            }
            else
            {
                // Aggregate or wildcard - keep as-is
                newColumns.Add(col);
            }
        }

        var newStatement = new SqlSelectStatement(
            newColumns,
            From,
            Joins,
            Where,
            OrderBy,
            Top,
            Distinct,
            GroupBy,
            Having,
            SourcePosition,
            GroupByExpressions);
        newStatement.LeadingComments.AddRange(LeadingComments);
        return newStatement;
    }
}

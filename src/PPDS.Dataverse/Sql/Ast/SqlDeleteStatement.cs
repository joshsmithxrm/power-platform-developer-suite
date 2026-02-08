using System.Collections.Generic;

namespace PPDS.Dataverse.Sql.Ast;

/// <summary>
/// Represents a DELETE statement: DELETE FROM table [FROM ...] [JOIN ...] [WHERE ...].
/// </summary>
public sealed class SqlDeleteStatement : ISqlStatement
{
    /// <summary>The target table being deleted from.</summary>
    public SqlTableRef TargetTable { get; }

    /// <summary>The WHERE clause condition, if present.</summary>
    public ISqlCondition? Where { get; }

    /// <summary>Optional FROM table for multi-table DELETE syntax.</summary>
    public SqlTableRef? FromTable { get; }

    /// <summary>Optional JOIN clauses for multi-table DELETE syntax.</summary>
    public IReadOnlyList<SqlJoin>? Joins { get; }

    /// <inheritdoc />
    public int SourcePosition { get; }

    /// <summary>
    /// Initializes a new instance of the <see cref="SqlDeleteStatement"/> class.
    /// </summary>
    public SqlDeleteStatement(
        SqlTableRef targetTable,
        ISqlCondition? where,
        SqlTableRef? fromTable = null,
        IReadOnlyList<SqlJoin>? joins = null,
        int sourcePosition = 0)
    {
        TargetTable = targetTable;
        Where = where;
        FromTable = fromTable;
        Joins = joins;
        SourcePosition = sourcePosition;
    }
}

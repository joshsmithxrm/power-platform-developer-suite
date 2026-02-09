using System.Collections.Generic;

namespace PPDS.Dataverse.Sql.Ast;

/// <summary>
/// Represents an UPDATE statement: UPDATE table SET col = val [FROM ...] [JOIN ...] [WHERE ...].
/// </summary>
public sealed class SqlUpdateStatement : ISqlStatement
{
    /// <summary>The target table being updated.</summary>
    public SqlTableRef TargetTable { get; }

    /// <summary>The SET clauses (column = value assignments).</summary>
    public IReadOnlyList<SqlSetClause> SetClauses { get; }

    /// <summary>Optional FROM table for multi-table UPDATE syntax.</summary>
    public SqlTableRef? FromTable { get; }

    /// <summary>Optional JOIN clauses for multi-table UPDATE syntax.</summary>
    public IReadOnlyList<SqlJoin>? Joins { get; }

    /// <summary>The WHERE clause condition, if present.</summary>
    public ISqlCondition? Where { get; }

    /// <inheritdoc />
    public int SourcePosition { get; }

    /// <summary>
    /// Initializes a new instance of the <see cref="SqlUpdateStatement"/> class.
    /// </summary>
    public SqlUpdateStatement(
        SqlTableRef targetTable,
        IReadOnlyList<SqlSetClause> setClauses,
        ISqlCondition? where,
        SqlTableRef? fromTable = null,
        IReadOnlyList<SqlJoin>? joins = null,
        int sourcePosition = 0)
    {
        TargetTable = targetTable;
        SetClauses = setClauses;
        Where = where;
        FromTable = fromTable;
        Joins = joins;
        SourcePosition = sourcePosition;
    }
}

/// <summary>
/// A single column = value assignment in an UPDATE SET clause.
/// </summary>
public sealed class SqlSetClause
{
    /// <summary>The column being set.</summary>
    public string ColumnName { get; }

    /// <summary>The value expression being assigned.</summary>
    public ISqlExpression Value { get; }

    /// <summary>
    /// Initializes a new instance of the <see cref="SqlSetClause"/> class.
    /// </summary>
    public SqlSetClause(string columnName, ISqlExpression value)
    {
        ColumnName = columnName;
        Value = value;
    }
}

using System;

namespace PPDS.Dataverse.Sql.Ast;

/// <summary>
/// IN subquery condition: column [NOT] IN (SELECT ...)
/// </summary>
public sealed class SqlInSubqueryCondition : ISqlCondition
{
    /// <summary>
    /// The column being checked.
    /// </summary>
    public SqlColumnRef Column { get; }

    /// <summary>
    /// The subquery that produces the value list.
    /// </summary>
    public SqlSelectStatement Subquery { get; }

    /// <summary>
    /// Whether this is a NOT IN condition.
    /// </summary>
    public bool IsNegated { get; }

    /// <inheritdoc />
    public string? TrailingComment { get; set; }

    /// <summary>
    /// Initializes a new instance of the <see cref="SqlInSubqueryCondition"/> class.
    /// </summary>
    public SqlInSubqueryCondition(SqlColumnRef column, SqlSelectStatement subquery, bool isNegated)
    {
        Column = column ?? throw new ArgumentNullException(nameof(column));
        Subquery = subquery ?? throw new ArgumentNullException(nameof(subquery));
        IsNegated = isNegated;
    }
}

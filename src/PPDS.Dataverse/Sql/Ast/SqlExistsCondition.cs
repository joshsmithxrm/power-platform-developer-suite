using System;

namespace PPDS.Dataverse.Sql.Ast;

/// <summary>
/// EXISTS / NOT EXISTS condition with a correlated subquery.
/// Example: EXISTS (SELECT 1 FROM contact WHERE contact.parentcustomerid = account.accountid)
/// </summary>
public sealed class SqlExistsCondition : ISqlCondition
{
    /// <summary>
    /// The subquery inside EXISTS.
    /// </summary>
    public SqlSelectStatement Subquery { get; }

    /// <summary>
    /// Whether this is a NOT EXISTS condition.
    /// </summary>
    public bool IsNegated { get; }

    /// <inheritdoc />
    public string? TrailingComment { get; set; }

    /// <summary>
    /// Initializes a new instance of the <see cref="SqlExistsCondition"/> class.
    /// </summary>
    public SqlExistsCondition(SqlSelectStatement subquery, bool isNegated)
    {
        Subquery = subquery ?? throw new ArgumentNullException(nameof(subquery));
        IsNegated = isNegated;
    }
}

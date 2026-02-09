using System;

namespace PPDS.Dataverse.Sql.Ast;

/// <summary>
/// Computed column in SELECT clause: revenue * 0.1 AS tax.
/// </summary>
public sealed class SqlComputedColumn : ISqlSelectColumn
{
    /// <summary>The expression that computes the column value.</summary>
    public ISqlExpression Expression { get; }

    /// <summary>The column alias.</summary>
    public string? Alias { get; }

    /// <summary>Optional trailing comment.</summary>
    public string? TrailingComment { get; set; }

    /// <summary>Initializes a new instance of the <see cref="SqlComputedColumn"/> class.</summary>
    public SqlComputedColumn(ISqlExpression expression, string? alias = null)
    {
        Expression = expression ?? throw new ArgumentNullException(nameof(expression));
        Alias = alias;
    }
}

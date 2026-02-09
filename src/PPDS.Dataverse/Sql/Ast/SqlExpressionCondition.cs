using System;

namespace PPDS.Dataverse.Sql.Ast;

/// <summary>
/// A condition that compares two arbitrary expressions.
/// Used for column-to-column comparisons (WHERE revenue > cost) and
/// computed conditions (WHERE revenue * 0.1 > 100) that cannot be
/// pushed to FetchXML.
/// </summary>
public sealed class SqlExpressionCondition : ISqlCondition
{
    /// <summary>The left-hand expression.</summary>
    public ISqlExpression Left { get; }

    /// <summary>The comparison operator.</summary>
    public SqlComparisonOperator Operator { get; }

    /// <summary>The right-hand expression.</summary>
    public ISqlExpression Right { get; }

    /// <inheritdoc />
    public string? TrailingComment { get; set; }

    /// <summary>
    /// Initializes a new instance of the <see cref="SqlExpressionCondition"/> class.
    /// </summary>
    public SqlExpressionCondition(ISqlExpression left, SqlComparisonOperator op, ISqlExpression right)
    {
        Left = left ?? throw new ArgumentNullException(nameof(left));
        Operator = op;
        Right = right ?? throw new ArgumentNullException(nameof(right));
    }
}

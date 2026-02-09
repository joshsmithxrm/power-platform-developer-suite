using System.Collections.Generic;

namespace PPDS.Dataverse.Sql.Ast;

/// <summary>
/// Represents a window function expression: function() OVER ([PARTITION BY ...] [ORDER BY ...]).
/// Supported functions: ROW_NUMBER, RANK, DENSE_RANK, and aggregate OVER (SUM, COUNT, AVG, MIN, MAX).
/// </summary>
public sealed class SqlWindowExpression : ISqlExpression
{
    /// <summary>The window function (ROW_NUMBER, RANK, DENSE_RANK, SUM, COUNT, AVG, MIN, MAX).</summary>
    public string FunctionName { get; }

    /// <summary>Optional operand for aggregate window functions (e.g., SUM(revenue)). Null for ranking functions.</summary>
    public ISqlExpression? Operand { get; }

    /// <summary>True when operand is COUNT(*) with star instead of a column expression.</summary>
    public bool IsCountStar { get; }

    /// <summary>PARTITION BY expressions (groups rows into partitions). Null or empty if not specified.</summary>
    public IReadOnlyList<ISqlExpression>? PartitionBy { get; }

    /// <summary>ORDER BY items within the window. Null or empty if not specified.</summary>
    public IReadOnlyList<SqlOrderByItem>? OrderBy { get; }

    /// <summary>Initializes a new instance of the <see cref="SqlWindowExpression"/> class.</summary>
    public SqlWindowExpression(
        string functionName,
        ISqlExpression? operand,
        IReadOnlyList<ISqlExpression>? partitionBy,
        IReadOnlyList<SqlOrderByItem>? orderBy,
        bool isCountStar = false)
    {
        FunctionName = functionName;
        Operand = operand;
        PartitionBy = partitionBy;
        OrderBy = orderBy;
        IsCountStar = isCountStar;
    }
}

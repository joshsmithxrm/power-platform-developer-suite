using System;

namespace PPDS.Dataverse.Sql.Ast;

/// <summary>
/// Comparison operators in SQL WHERE clauses.
/// </summary>
public enum SqlComparisonOperator
{
    /// <summary>Equals operator (=).</summary>
    Equal,
    /// <summary>Not equal operator (&lt;&gt; or !=).</summary>
    NotEqual,
    /// <summary>Less than operator (&lt;).</summary>
    LessThan,
    /// <summary>Greater than operator (&gt;).</summary>
    GreaterThan,
    /// <summary>Less than or equal operator (&lt;=).</summary>
    LessThanOrEqual,
    /// <summary>Greater than or equal operator (&gt;=).</summary>
    GreaterThanOrEqual
}

/// <summary>
/// Logical operators for combining conditions.
/// </summary>
public enum SqlLogicalOperator
{
    /// <summary>AND logical operator.</summary>
    And,
    /// <summary>OR logical operator.</summary>
    Or
}

/// <summary>
/// Sort direction for ORDER BY.
/// </summary>
public enum SqlSortDirection
{
    /// <summary>Ascending sort order (ASC).</summary>
    Ascending,
    /// <summary>Descending sort order (DESC).</summary>
    Descending
}

/// <summary>
/// JOIN types.
/// </summary>
public enum SqlJoinType
{
    /// <summary>INNER JOIN.</summary>
    Inner,
    /// <summary>LEFT OUTER JOIN.</summary>
    Left,
    /// <summary>RIGHT OUTER JOIN.</summary>
    Right
}

/// <summary>
/// Aggregate function types.
/// </summary>
public enum SqlAggregateFunction
{
    /// <summary>COUNT aggregate.</summary>
    Count,
    /// <summary>SUM aggregate.</summary>
    Sum,
    /// <summary>AVG aggregate.</summary>
    Avg,
    /// <summary>MIN aggregate.</summary>
    Min,
    /// <summary>MAX aggregate.</summary>
    Max
}

/// <summary>
/// Extension methods for SQL enums.
/// </summary>
public static class SqlEnumExtensions
{
    /// <summary>
    /// Converts a comparison operator to its FetchXML equivalent.
    /// </summary>
    public static string ToFetchXmlOperator(this SqlComparisonOperator op) => op switch
    {
        SqlComparisonOperator.Equal => "eq",
        SqlComparisonOperator.NotEqual => "ne",
        SqlComparisonOperator.LessThan => "lt",
        SqlComparisonOperator.GreaterThan => "gt",
        SqlComparisonOperator.LessThanOrEqual => "le",
        SqlComparisonOperator.GreaterThanOrEqual => "ge",
        _ => throw new ArgumentOutOfRangeException(nameof(op))
    };

    /// <summary>
    /// Converts a logical operator to its FetchXML filter type.
    /// </summary>
    public static string ToFetchXmlFilterType(this SqlLogicalOperator op) => op switch
    {
        SqlLogicalOperator.And => "and",
        SqlLogicalOperator.Or => "or",
        _ => throw new ArgumentOutOfRangeException(nameof(op))
    };

    /// <summary>
    /// Converts a join type to its FetchXML link-type.
    /// </summary>
    public static string ToFetchXmlLinkType(this SqlJoinType type) => type switch
    {
        SqlJoinType.Inner => "inner",
        SqlJoinType.Left => "outer",
        SqlJoinType.Right => "outer", // FetchXML doesn't have right, we handle differently
        _ => throw new ArgumentOutOfRangeException(nameof(type))
    };

    /// <summary>
    /// Converts an aggregate function to its FetchXML aggregate attribute.
    /// </summary>
    public static string ToFetchXmlAggregate(this SqlAggregateFunction func) => func switch
    {
        SqlAggregateFunction.Count => "count",
        SqlAggregateFunction.Sum => "sum",
        SqlAggregateFunction.Avg => "avg",
        SqlAggregateFunction.Min => "min",
        SqlAggregateFunction.Max => "max",
        _ => throw new ArgumentOutOfRangeException(nameof(func))
    };
}

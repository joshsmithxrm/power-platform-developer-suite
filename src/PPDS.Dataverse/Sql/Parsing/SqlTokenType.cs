using System;
using System.Collections.Generic;

namespace PPDS.Dataverse.Sql.Parsing;

/// <summary>
/// Represents the type of a token in SQL lexical analysis.
/// </summary>
public enum SqlTokenType
{
    // Keywords
    Select,
    From,
    Where,
    And,
    Or,
    Order,
    By,
    Asc,
    Desc,
    Top,
    Limit,
    Is,
    Null,
    Not,
    In,
    Like,
    Join,
    Inner,
    Left,
    Right,
    Outer,
    On,
    As,
    Distinct,
    Group,

    // Aggregate functions
    Count,
    Sum,
    Avg,
    Min,
    Max,

    // Operators
    Equals,              // =
    NotEquals,           // <> or !=
    LessThan,            // <
    GreaterThan,         // >
    LessThanOrEqual,     // <=
    GreaterThanOrEqual,  // >=

    // Punctuation
    Comma,
    Dot,
    Star,
    LeftParen,
    RightParen,

    // Literals
    Identifier,
    String,
    Number,

    // Special
    Eof
}

/// <summary>
/// Extension methods for SqlTokenType.
/// </summary>
public static class SqlTokenTypeExtensions
{
    private static readonly HashSet<SqlTokenType> Keywords = new()
    {
        SqlTokenType.Select,
        SqlTokenType.From,
        SqlTokenType.Where,
        SqlTokenType.And,
        SqlTokenType.Or,
        SqlTokenType.Order,
        SqlTokenType.By,
        SqlTokenType.Asc,
        SqlTokenType.Desc,
        SqlTokenType.Top,
        SqlTokenType.Limit,
        SqlTokenType.Is,
        SqlTokenType.Null,
        SqlTokenType.Not,
        SqlTokenType.In,
        SqlTokenType.Like,
        SqlTokenType.Join,
        SqlTokenType.Inner,
        SqlTokenType.Left,
        SqlTokenType.Right,
        SqlTokenType.Outer,
        SqlTokenType.On,
        SqlTokenType.As,
        SqlTokenType.Distinct,
        SqlTokenType.Group,
        SqlTokenType.Count,
        SqlTokenType.Sum,
        SqlTokenType.Avg,
        SqlTokenType.Min,
        SqlTokenType.Max
    };

    private static readonly HashSet<SqlTokenType> ComparisonOperators = new()
    {
        SqlTokenType.Equals,
        SqlTokenType.NotEquals,
        SqlTokenType.LessThan,
        SqlTokenType.GreaterThan,
        SqlTokenType.LessThanOrEqual,
        SqlTokenType.GreaterThanOrEqual
    };

    private static readonly HashSet<SqlTokenType> AggregateFunctions = new()
    {
        SqlTokenType.Count,
        SqlTokenType.Sum,
        SqlTokenType.Avg,
        SqlTokenType.Min,
        SqlTokenType.Max
    };

    /// <summary>
    /// Checks if this token type is a SQL keyword.
    /// </summary>
    public static bool IsKeyword(this SqlTokenType type) => Keywords.Contains(type);

    /// <summary>
    /// Checks if this token type is a comparison operator.
    /// </summary>
    public static bool IsComparisonOperator(this SqlTokenType type) => ComparisonOperators.Contains(type);

    /// <summary>
    /// Checks if this token type is an aggregate function.
    /// </summary>
    public static bool IsAggregateFunction(this SqlTokenType type) => AggregateFunctions.Contains(type);

    /// <summary>
    /// Keyword string to token type mapping.
    /// </summary>
    internal static readonly Dictionary<string, SqlTokenType> KeywordMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["SELECT"] = SqlTokenType.Select,
        ["FROM"] = SqlTokenType.From,
        ["WHERE"] = SqlTokenType.Where,
        ["AND"] = SqlTokenType.And,
        ["OR"] = SqlTokenType.Or,
        ["ORDER"] = SqlTokenType.Order,
        ["BY"] = SqlTokenType.By,
        ["ASC"] = SqlTokenType.Asc,
        ["DESC"] = SqlTokenType.Desc,
        ["TOP"] = SqlTokenType.Top,
        ["LIMIT"] = SqlTokenType.Limit,
        ["IS"] = SqlTokenType.Is,
        ["NULL"] = SqlTokenType.Null,
        ["NOT"] = SqlTokenType.Not,
        ["IN"] = SqlTokenType.In,
        ["LIKE"] = SqlTokenType.Like,
        ["JOIN"] = SqlTokenType.Join,
        ["INNER"] = SqlTokenType.Inner,
        ["LEFT"] = SqlTokenType.Left,
        ["RIGHT"] = SqlTokenType.Right,
        ["OUTER"] = SqlTokenType.Outer,
        ["ON"] = SqlTokenType.On,
        ["AS"] = SqlTokenType.As,
        ["DISTINCT"] = SqlTokenType.Distinct,
        ["GROUP"] = SqlTokenType.Group,
        ["COUNT"] = SqlTokenType.Count,
        ["SUM"] = SqlTokenType.Sum,
        ["AVG"] = SqlTokenType.Avg,
        ["MIN"] = SqlTokenType.Min,
        ["MAX"] = SqlTokenType.Max
    };
}

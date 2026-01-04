using System;
using System.Collections.Generic;

namespace PPDS.Dataverse.Sql.Parsing;

/// <summary>
/// Represents the type of a token in SQL lexical analysis.
/// </summary>
public enum SqlTokenType
{
    #region Keywords

    /// <summary>SELECT keyword.</summary>
    Select,
    /// <summary>FROM keyword.</summary>
    From,
    /// <summary>WHERE keyword.</summary>
    Where,
    /// <summary>AND keyword.</summary>
    And,
    /// <summary>OR keyword.</summary>
    Or,
    /// <summary>ORDER keyword.</summary>
    Order,
    /// <summary>BY keyword.</summary>
    By,
    /// <summary>ASC keyword.</summary>
    Asc,
    /// <summary>DESC keyword.</summary>
    Desc,
    /// <summary>TOP keyword.</summary>
    Top,
    /// <summary>LIMIT keyword.</summary>
    Limit,
    /// <summary>IS keyword.</summary>
    Is,
    /// <summary>NULL keyword.</summary>
    Null,
    /// <summary>NOT keyword.</summary>
    Not,
    /// <summary>IN keyword.</summary>
    In,
    /// <summary>LIKE keyword.</summary>
    Like,
    /// <summary>JOIN keyword.</summary>
    Join,
    /// <summary>INNER keyword.</summary>
    Inner,
    /// <summary>LEFT keyword.</summary>
    Left,
    /// <summary>RIGHT keyword.</summary>
    Right,
    /// <summary>OUTER keyword.</summary>
    Outer,
    /// <summary>ON keyword.</summary>
    On,
    /// <summary>AS keyword.</summary>
    As,
    /// <summary>DISTINCT keyword.</summary>
    Distinct,
    /// <summary>GROUP keyword.</summary>
    Group,

    #endregion

    #region Aggregate Functions

    /// <summary>COUNT aggregate function.</summary>
    Count,
    /// <summary>SUM aggregate function.</summary>
    Sum,
    /// <summary>AVG aggregate function.</summary>
    Avg,
    /// <summary>MIN aggregate function.</summary>
    Min,
    /// <summary>MAX aggregate function.</summary>
    Max,

    #endregion

    #region Operators

    /// <summary>Equals operator (=).</summary>
    Equals,
    /// <summary>Not equals operator (&lt;&gt; or !=).</summary>
    NotEquals,
    /// <summary>Less than operator (&lt;).</summary>
    LessThan,
    /// <summary>Greater than operator (&gt;).</summary>
    GreaterThan,
    /// <summary>Less than or equal operator (&lt;=).</summary>
    LessThanOrEqual,
    /// <summary>Greater than or equal operator (&gt;=).</summary>
    GreaterThanOrEqual,

    #endregion

    #region Punctuation

    /// <summary>Comma separator (,).</summary>
    Comma,
    /// <summary>Dot separator (.).</summary>
    Dot,
    /// <summary>Star/asterisk (*).</summary>
    Star,
    /// <summary>Left parenthesis (().</summary>
    LeftParen,
    /// <summary>Right parenthesis ()).</summary>
    RightParen,

    #endregion

    #region Literals

    /// <summary>Identifier (column name, table name, alias).</summary>
    Identifier,
    /// <summary>String literal.</summary>
    String,
    /// <summary>Numeric literal.</summary>
    Number,

    #endregion

    #region Special

    /// <summary>End of file/input.</summary>
    Eof

    #endregion
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

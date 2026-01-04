using System;
using System.Collections.Generic;

namespace PPDS.Dataverse.Sql.Ast;

/// <summary>
/// Base interface for WHERE clause conditions.
/// </summary>
public interface ISqlCondition
{
    /// <summary>
    /// Optional trailing comment.
    /// </summary>
    string? TrailingComment { get; set; }
}

/// <summary>
/// Comparison condition: column op value
/// </summary>
public sealed class SqlComparisonCondition : ISqlCondition
{
    /// <summary>
    /// The column being compared.
    /// </summary>
    public SqlColumnRef Column { get; }

    /// <summary>
    /// The comparison operator.
    /// </summary>
    public SqlComparisonOperator Operator { get; }

    /// <summary>
    /// The value being compared to.
    /// </summary>
    public SqlLiteral Value { get; }

    /// <inheritdoc />
    public string? TrailingComment { get; set; }

    /// <summary>
    /// Initializes a new instance of the <see cref="SqlComparisonCondition"/> class.
    /// </summary>
    public SqlComparisonCondition(SqlColumnRef column, SqlComparisonOperator op, SqlLiteral value)
    {
        Column = column ?? throw new ArgumentNullException(nameof(column));
        Operator = op;
        Value = value ?? throw new ArgumentNullException(nameof(value));
    }
}

/// <summary>
/// LIKE condition: column [NOT] LIKE pattern
/// </summary>
public sealed class SqlLikeCondition : ISqlCondition
{
    /// <summary>
    /// The column being matched.
    /// </summary>
    public SqlColumnRef Column { get; }

    /// <summary>
    /// The pattern to match (may contain % wildcards).
    /// </summary>
    public string Pattern { get; }

    /// <summary>
    /// Whether this is a NOT LIKE condition.
    /// </summary>
    public bool IsNegated { get; }

    /// <inheritdoc />
    public string? TrailingComment { get; set; }

    /// <summary>
    /// Initializes a new instance of the <see cref="SqlLikeCondition"/> class.
    /// </summary>
    public SqlLikeCondition(SqlColumnRef column, string pattern, bool isNegated)
    {
        Column = column ?? throw new ArgumentNullException(nameof(column));
        Pattern = pattern ?? throw new ArgumentNullException(nameof(pattern));
        IsNegated = isNegated;
    }
}

/// <summary>
/// NULL condition: column IS [NOT] NULL
/// </summary>
public sealed class SqlNullCondition : ISqlCondition
{
    /// <summary>
    /// The column being checked.
    /// </summary>
    public SqlColumnRef Column { get; }

    /// <summary>
    /// Whether this is an IS NOT NULL condition.
    /// </summary>
    public bool IsNegated { get; }

    /// <inheritdoc />
    public string? TrailingComment { get; set; }

    /// <summary>
    /// Initializes a new instance of the <see cref="SqlNullCondition"/> class.
    /// </summary>
    public SqlNullCondition(SqlColumnRef column, bool isNegated)
    {
        Column = column ?? throw new ArgumentNullException(nameof(column));
        IsNegated = isNegated;
    }
}

/// <summary>
/// IN condition: column [NOT] IN (value1, value2, ...)
/// </summary>
public sealed class SqlInCondition : ISqlCondition
{
    /// <summary>
    /// The column being checked.
    /// </summary>
    public SqlColumnRef Column { get; }

    /// <summary>
    /// The list of values to match.
    /// </summary>
    public IReadOnlyList<SqlLiteral> Values { get; }

    /// <summary>
    /// Whether this is a NOT IN condition.
    /// </summary>
    public bool IsNegated { get; }

    /// <inheritdoc />
    public string? TrailingComment { get; set; }

    /// <summary>
    /// Initializes a new instance of the <see cref="SqlInCondition"/> class.
    /// </summary>
    public SqlInCondition(SqlColumnRef column, IReadOnlyList<SqlLiteral> values, bool isNegated)
    {
        Column = column ?? throw new ArgumentNullException(nameof(column));
        Values = values ?? throw new ArgumentNullException(nameof(values));
        IsNegated = isNegated;
    }
}

/// <summary>
/// Logical condition: condition AND/OR condition
/// </summary>
public sealed class SqlLogicalCondition : ISqlCondition
{
    /// <summary>
    /// The logical operator (AND or OR).
    /// </summary>
    public SqlLogicalOperator Operator { get; }

    /// <summary>
    /// The child conditions.
    /// </summary>
    public IReadOnlyList<ISqlCondition> Conditions { get; }

    /// <inheritdoc />
    public string? TrailingComment { get; set; }

    /// <summary>
    /// Initializes a new instance of the <see cref="SqlLogicalCondition"/> class.
    /// </summary>
    public SqlLogicalCondition(SqlLogicalOperator op, IReadOnlyList<ISqlCondition> conditions)
    {
        Operator = op;
        Conditions = conditions ?? throw new ArgumentNullException(nameof(conditions));
    }

    /// <summary>
    /// Creates an AND condition.
    /// </summary>
    public static SqlLogicalCondition And(params ISqlCondition[] conditions) =>
        new(SqlLogicalOperator.And, conditions);

    /// <summary>
    /// Creates an OR condition.
    /// </summary>
    public static SqlLogicalCondition Or(params ISqlCondition[] conditions) =>
        new(SqlLogicalOperator.Or, conditions);
}

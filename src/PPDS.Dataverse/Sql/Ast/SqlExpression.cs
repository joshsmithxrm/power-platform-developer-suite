using System;
using System.Collections.Generic;

namespace PPDS.Dataverse.Sql.Ast;

/// <summary>
/// Base interface for all SQL expressions.
/// </summary>
public interface ISqlExpression { }

/// <summary>
/// Literal value: 42, 'hello', NULL.
/// </summary>
public sealed class SqlLiteralExpression : ISqlExpression
{
    /// <summary>The literal value.</summary>
    public SqlLiteral Value { get; }

    /// <summary>Initializes a new instance of the <see cref="SqlLiteralExpression"/> class.</summary>
    public SqlLiteralExpression(SqlLiteral value)
    {
        Value = value ?? throw new ArgumentNullException(nameof(value));
    }
}

/// <summary>
/// Column reference as expression: a.name, revenue.
/// </summary>
public sealed class SqlColumnExpression : ISqlExpression
{
    /// <summary>The column reference.</summary>
    public SqlColumnRef Column { get; }

    /// <summary>Initializes a new instance of the <see cref="SqlColumnExpression"/> class.</summary>
    public SqlColumnExpression(SqlColumnRef column)
    {
        Column = column ?? throw new ArgumentNullException(nameof(column));
    }
}

/// <summary>
/// Binary operation: revenue * 0.1, price + tax.
/// </summary>
public sealed class SqlBinaryExpression : ISqlExpression
{
    /// <summary>Left operand.</summary>
    public ISqlExpression Left { get; }

    /// <summary>The binary operator.</summary>
    public SqlBinaryOperator Operator { get; }

    /// <summary>Right operand.</summary>
    public ISqlExpression Right { get; }

    /// <summary>Initializes a new instance of the <see cref="SqlBinaryExpression"/> class.</summary>
    public SqlBinaryExpression(ISqlExpression left, SqlBinaryOperator op, ISqlExpression right)
    {
        Left = left ?? throw new ArgumentNullException(nameof(left));
        Operator = op;
        Right = right ?? throw new ArgumentNullException(nameof(right));
    }
}

/// <summary>
/// Unary operation: -amount, NOT flag.
/// </summary>
public sealed class SqlUnaryExpression : ISqlExpression
{
    /// <summary>The unary operator.</summary>
    public SqlUnaryOperator Operator { get; }

    /// <summary>The operand.</summary>
    public ISqlExpression Operand { get; }

    /// <summary>Initializes a new instance of the <see cref="SqlUnaryExpression"/> class.</summary>
    public SqlUnaryExpression(SqlUnaryOperator op, ISqlExpression operand)
    {
        Operator = op;
        Operand = operand ?? throw new ArgumentNullException(nameof(operand));
    }
}

/// <summary>
/// Function call: UPPER(name), DATEADD(day, 7, createdon).
/// </summary>
public sealed class SqlFunctionExpression : ISqlExpression
{
    /// <summary>The function name (case-insensitive).</summary>
    public string FunctionName { get; }

    /// <summary>The function arguments.</summary>
    public IReadOnlyList<ISqlExpression> Arguments { get; }

    /// <summary>Initializes a new instance of the <see cref="SqlFunctionExpression"/> class.</summary>
    public SqlFunctionExpression(string functionName, IReadOnlyList<ISqlExpression> arguments)
    {
        FunctionName = functionName ?? throw new ArgumentNullException(nameof(functionName));
        Arguments = arguments ?? throw new ArgumentNullException(nameof(arguments));
    }
}

/// <summary>
/// CASE WHEN ... THEN ... ELSE ... END.
/// </summary>
public sealed class SqlCaseExpression : ISqlExpression
{
    /// <summary>The WHEN/THEN clauses.</summary>
    public IReadOnlyList<SqlWhenClause> WhenClauses { get; }

    /// <summary>The ELSE expression, or null if omitted.</summary>
    public ISqlExpression? ElseExpression { get; }

    /// <summary>Initializes a new instance of the <see cref="SqlCaseExpression"/> class.</summary>
    public SqlCaseExpression(IReadOnlyList<SqlWhenClause> whenClauses, ISqlExpression? elseExpression = null)
    {
        WhenClauses = whenClauses ?? throw new ArgumentNullException(nameof(whenClauses));
        ElseExpression = elseExpression;
    }
}

/// <summary>
/// A single WHEN condition THEN result clause.
/// </summary>
public sealed class SqlWhenClause
{
    /// <summary>The WHEN condition.</summary>
    public ISqlCondition Condition { get; }

    /// <summary>The THEN result expression.</summary>
    public ISqlExpression Result { get; }

    /// <summary>Initializes a new instance of the <see cref="SqlWhenClause"/> class.</summary>
    public SqlWhenClause(ISqlCondition condition, ISqlExpression result)
    {
        Condition = condition ?? throw new ArgumentNullException(nameof(condition));
        Result = result ?? throw new ArgumentNullException(nameof(result));
    }
}

/// <summary>
/// IIF(condition, true_value, false_value).
/// </summary>
public sealed class SqlIifExpression : ISqlExpression
{
    /// <summary>The condition to evaluate.</summary>
    public ISqlCondition Condition { get; }

    /// <summary>Value when condition is true.</summary>
    public ISqlExpression TrueValue { get; }

    /// <summary>Value when condition is false.</summary>
    public ISqlExpression FalseValue { get; }

    /// <summary>Initializes a new instance of the <see cref="SqlIifExpression"/> class.</summary>
    public SqlIifExpression(ISqlCondition condition, ISqlExpression trueValue, ISqlExpression falseValue)
    {
        Condition = condition ?? throw new ArgumentNullException(nameof(condition));
        TrueValue = trueValue ?? throw new ArgumentNullException(nameof(trueValue));
        FalseValue = falseValue ?? throw new ArgumentNullException(nameof(falseValue));
    }
}

/// <summary>
/// CAST(expression AS type) or CONVERT(type, expression [, style]).
/// </summary>
public sealed class SqlCastExpression : ISqlExpression
{
    /// <summary>The expression to cast.</summary>
    public ISqlExpression Expression { get; }

    /// <summary>The target type name: "int", "nvarchar(100)", "datetime", etc.</summary>
    public string TargetType { get; }

    /// <summary>Optional CONVERT style code (e.g., 120 for ODBC canonical datetime). Null for CAST.</summary>
    public int? Style { get; }

    /// <summary>Initializes a new instance of the <see cref="SqlCastExpression"/> class.</summary>
    public SqlCastExpression(ISqlExpression expression, string targetType, int? style = null)
    {
        Expression = expression ?? throw new ArgumentNullException(nameof(expression));
        TargetType = targetType ?? throw new ArgumentNullException(nameof(targetType));
        Style = style;
    }
}

/// <summary>
/// Aggregate expression: SUM(revenue), COUNT(*).
/// </summary>
public sealed class SqlAggregateExpression : ISqlExpression
{
    /// <summary>The aggregate function.</summary>
    public SqlAggregateFunction Function { get; }

    /// <summary>The operand expression, or null for COUNT(*).</summary>
    public ISqlExpression? Operand { get; }

    /// <summary>Whether DISTINCT is applied.</summary>
    public bool IsDistinct { get; }

    /// <summary>Initializes a new instance of the <see cref="SqlAggregateExpression"/> class.</summary>
    public SqlAggregateExpression(SqlAggregateFunction function, ISqlExpression? operand = null, bool isDistinct = false)
    {
        Function = function;
        Operand = operand;
        IsDistinct = isDistinct;
    }
}

/// <summary>
/// Subquery expression: (SELECT ...).
/// </summary>
public sealed class SqlSubqueryExpression : ISqlExpression
{
    /// <summary>The subquery statement.</summary>
    public SqlSelectStatement Subquery { get; }

    /// <summary>Initializes a new instance of the <see cref="SqlSubqueryExpression"/> class.</summary>
    public SqlSubqueryExpression(SqlSelectStatement subquery)
    {
        Subquery = subquery ?? throw new ArgumentNullException(nameof(subquery));
    }
}

/// <summary>
/// Binary operators for expressions.
/// </summary>
public enum SqlBinaryOperator
{
    /// <summary>Addition (+) for numbers.</summary>
    Add,
    /// <summary>Subtraction (-).</summary>
    Subtract,
    /// <summary>Multiplication (*).</summary>
    Multiply,
    /// <summary>Division (/).</summary>
    Divide,
    /// <summary>Modulo (%).</summary>
    Modulo,
    /// <summary>String concatenation (+) for strings.</summary>
    StringConcat
}

/// <summary>
/// Unary operators for expressions.
/// </summary>
public enum SqlUnaryOperator
{
    /// <summary>Negation (-).</summary>
    Negate,
    /// <summary>Logical NOT.</summary>
    Not
}

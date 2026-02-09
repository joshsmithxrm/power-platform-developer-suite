using System;
using System.Collections.Generic;
using System.Globalization;
using PPDS.Dataverse.Query.Execution.Functions;
using PPDS.Dataverse.Sql.Ast;

namespace PPDS.Dataverse.Query.Execution;

/// <summary>
/// Evaluates SQL expressions against data rows with SQL three-valued logic (NULL propagation).
/// </summary>
public sealed class ExpressionEvaluator : IExpressionEvaluator
{
    private readonly FunctionRegistry _functionRegistry;

    /// <summary>
    /// Optional variable scope for resolving @variable references.
    /// </summary>
    public VariableScope? VariableScope { get; set; }

    /// <summary>
    /// Creates an evaluator with the default function registry (all built-in functions).
    /// </summary>
    public ExpressionEvaluator()
        : this(FunctionRegistry.CreateDefault())
    {
    }

    /// <summary>
    /// Creates an evaluator with a custom function registry.
    /// </summary>
    public ExpressionEvaluator(FunctionRegistry functionRegistry)
    {
        _functionRegistry = functionRegistry ?? throw new ArgumentNullException(nameof(functionRegistry));
    }
    /// <inheritdoc />
    public object? Evaluate(ISqlExpression expression, IReadOnlyDictionary<string, QueryValue> row)
    {
        return expression switch
        {
            SqlLiteralExpression lit => EvaluateLiteral(lit),
            SqlColumnExpression col => EvaluateColumn(col, row),
            SqlVariableExpression var => EvaluateVariable(var),
            SqlBinaryExpression bin => EvaluateBinary(bin, row),
            SqlUnaryExpression unary => EvaluateUnary(unary, row),
            SqlCaseExpression caseExpr => EvaluateCase(caseExpr, row),
            SqlIifExpression iif => EvaluateIif(iif, row),
            SqlFunctionExpression func => EvaluateFunction(func, row),
            SqlCastExpression cast => EvaluateCast(cast, row),
            _ => throw new NotSupportedException($"Expression type {expression.GetType().Name} is not yet supported.")
        };
    }

    /// <inheritdoc />
    public bool EvaluateCondition(ISqlCondition condition, IReadOnlyDictionary<string, QueryValue> row)
    {
        return condition switch
        {
            SqlComparisonCondition comp => EvaluateComparison(comp, row),
            SqlExpressionCondition exprCond => EvaluateExpressionCondition(exprCond, row),
            SqlNullCondition nullCond => EvaluateNullCheck(nullCond, row),
            SqlLikeCondition like => EvaluateLike(like, row),
            SqlInCondition inCond => EvaluateIn(inCond, row),
            SqlLogicalCondition logical => EvaluateLogical(logical, row),
            _ => throw new NotSupportedException($"Condition type {condition.GetType().Name} is not yet supported.")
        };
    }

    #region Expression Evaluation

    private static object? EvaluateLiteral(SqlLiteralExpression lit)
    {
        return lit.Value.Type switch
        {
            SqlLiteralType.Null => null,
            SqlLiteralType.String => lit.Value.Value,
            SqlLiteralType.Number => ParseNumber(lit.Value.Value!),
            _ => throw new NotSupportedException($"Literal type {lit.Value.Type} is not supported.")
        };
    }

    private static object? EvaluateColumn(SqlColumnExpression col, IReadOnlyDictionary<string, QueryValue> row)
    {
        var name = col.Column.GetFullName();
        if (row.TryGetValue(name, out var qv))
        {
            return qv.Value;
        }

        // Try case-insensitive lookup
        foreach (var kvp in row)
        {
            if (string.Equals(kvp.Key, name, StringComparison.OrdinalIgnoreCase))
            {
                return kvp.Value.Value;
            }
        }

        return null;
    }

    private object? EvaluateVariable(SqlVariableExpression varExpr)
    {
        if (VariableScope == null)
        {
            throw new QueryExecutionException(
                QueryErrorCode.ExecutionFailed,
                $"Variable {varExpr.VariableName} cannot be resolved: no VariableScope is configured.");
        }

        return VariableScope.Get(varExpr.VariableName);
    }

    private object? EvaluateBinary(SqlBinaryExpression bin, IReadOnlyDictionary<string, QueryValue> row)
    {
        var left = Evaluate(bin.Left, row);
        var right = Evaluate(bin.Right, row);

        // NULL propagation: any arithmetic with NULL returns NULL
        if (left is null || right is null)
        {
            return null;
        }

        // String concatenation: if either operand is a string and operator is Add
        if (bin.Operator == SqlBinaryOperator.Add && (left is string || right is string))
        {
            return Convert.ToString(left, CultureInfo.InvariantCulture)
                 + Convert.ToString(right, CultureInfo.InvariantCulture);
        }

        if (bin.Operator == SqlBinaryOperator.StringConcat)
        {
            return Convert.ToString(left, CultureInfo.InvariantCulture)
                 + Convert.ToString(right, CultureInfo.InvariantCulture);
        }

        // Numeric operations
        return EvaluateArithmetic(left, bin.Operator, right);
    }

    private object? EvaluateUnary(SqlUnaryExpression unary, IReadOnlyDictionary<string, QueryValue> row)
    {
        var operand = Evaluate(unary.Operand, row);
        if (operand is null)
        {
            return null;
        }

        return unary.Operator switch
        {
            SqlUnaryOperator.Negate => NegateValue(operand),
            SqlUnaryOperator.Not => operand is bool b ? !b : throw new QueryExecutionException(
                QueryErrorCode.TypeMismatch,
                $"NOT requires a boolean operand, but got {operand.GetType().Name}."),
            _ => throw new NotSupportedException($"Unary operator {unary.Operator} is not supported.")
        };
    }

    private object? EvaluateCase(SqlCaseExpression caseExpr, IReadOnlyDictionary<string, QueryValue> row)
    {
        foreach (var whenClause in caseExpr.WhenClauses)
        {
            if (EvaluateCondition(whenClause.Condition, row))
            {
                return Evaluate(whenClause.Result, row);
            }
        }

        // No WHEN matched: return ELSE if present, otherwise NULL
        return caseExpr.ElseExpression != null
            ? Evaluate(caseExpr.ElseExpression, row)
            : null;
    }

    private object? EvaluateIif(SqlIifExpression iif, IReadOnlyDictionary<string, QueryValue> row)
    {
        return EvaluateCondition(iif.Condition, row)
            ? Evaluate(iif.TrueValue, row)
            : Evaluate(iif.FalseValue, row);
    }

    private object? EvaluateFunction(SqlFunctionExpression func, IReadOnlyDictionary<string, QueryValue> row)
    {
        var args = new object?[func.Arguments.Count];
        for (int i = 0; i < func.Arguments.Count; i++)
        {
            args[i] = Evaluate(func.Arguments[i], row);
        }

        return _functionRegistry.Invoke(func.FunctionName, args);
    }

    private object? EvaluateCast(SqlCastExpression cast, IReadOnlyDictionary<string, QueryValue> row)
    {
        var value = Evaluate(cast.Expression, row);
        if (value is null)
        {
            return null;
        }

        return Functions.CastConverter.Convert(value, cast.TargetType, cast.Style);
    }

    #endregion

    #region Condition Evaluation

    private bool EvaluateComparison(SqlComparisonCondition comp, IReadOnlyDictionary<string, QueryValue> row)
    {
        var columnName = comp.Column.GetFullName();
        object? columnValue = null;

        if (row.TryGetValue(columnName, out var qv))
        {
            columnValue = qv.Value;
        }
        else
        {
            // Case-insensitive lookup
            foreach (var kvp in row)
            {
                if (string.Equals(kvp.Key, columnName, StringComparison.OrdinalIgnoreCase))
                {
                    columnValue = kvp.Value.Value;
                    break;
                }
            }
        }

        var literalValue = ParseLiteralValue(comp.Value);

        // SQL semantics: comparison with NULL always returns false
        if (columnValue is null || literalValue is null)
        {
            return false;
        }

        int cmp = CompareValues(columnValue, literalValue);

        return comp.Operator switch
        {
            SqlComparisonOperator.Equal => cmp == 0,
            SqlComparisonOperator.NotEqual => cmp != 0,
            SqlComparisonOperator.LessThan => cmp < 0,
            SqlComparisonOperator.GreaterThan => cmp > 0,
            SqlComparisonOperator.LessThanOrEqual => cmp <= 0,
            SqlComparisonOperator.GreaterThanOrEqual => cmp >= 0,
            _ => throw new NotSupportedException($"Operator {comp.Operator} is not supported.")
        };
    }

    private bool EvaluateExpressionCondition(SqlExpressionCondition cond, IReadOnlyDictionary<string, QueryValue> row)
    {
        var leftValue = Evaluate(cond.Left, row);
        var rightValue = Evaluate(cond.Right, row);

        // SQL semantics: comparison with NULL always returns false
        if (leftValue is null || rightValue is null)
        {
            return false;
        }

        int cmp = CompareValues(leftValue, rightValue);

        return cond.Operator switch
        {
            SqlComparisonOperator.Equal => cmp == 0,
            SqlComparisonOperator.NotEqual => cmp != 0,
            SqlComparisonOperator.LessThan => cmp < 0,
            SqlComparisonOperator.GreaterThan => cmp > 0,
            SqlComparisonOperator.LessThanOrEqual => cmp <= 0,
            SqlComparisonOperator.GreaterThanOrEqual => cmp >= 0,
            _ => throw new NotSupportedException($"Operator {cond.Operator} is not supported.")
        };
    }

    private bool EvaluateNullCheck(SqlNullCondition cond, IReadOnlyDictionary<string, QueryValue> row)
    {
        var columnName = cond.Column.GetFullName();
        object? value = null;
        bool found = false;

        if (row.TryGetValue(columnName, out var qv))
        {
            value = qv.Value;
            found = true;
        }
        else
        {
            foreach (var kvp in row)
            {
                if (string.Equals(kvp.Key, columnName, StringComparison.OrdinalIgnoreCase))
                {
                    value = kvp.Value.Value;
                    found = true;
                    break;
                }
            }
        }

        bool isNull = !found || value is null;
        return cond.IsNegated ? !isNull : isNull;
    }

    private bool EvaluateLike(SqlLikeCondition cond, IReadOnlyDictionary<string, QueryValue> row)
    {
        var columnName = cond.Column.GetFullName();
        object? value = null;

        if (row.TryGetValue(columnName, out var qv))
        {
            value = qv.Value;
        }
        else
        {
            foreach (var kvp in row)
            {
                if (string.Equals(kvp.Key, columnName, StringComparison.OrdinalIgnoreCase))
                {
                    value = kvp.Value.Value;
                    break;
                }
            }
        }

        if (value is null)
        {
            return false;
        }

        var str = Convert.ToString(value, CultureInfo.InvariantCulture) ?? "";
        var matches = MatchLikePattern(str, cond.Pattern);

        return cond.IsNegated ? !matches : matches;
    }

    private bool EvaluateIn(SqlInCondition cond, IReadOnlyDictionary<string, QueryValue> row)
    {
        var columnName = cond.Column.GetFullName();
        object? columnValue = null;

        if (row.TryGetValue(columnName, out var qv))
        {
            columnValue = qv.Value;
        }
        else
        {
            foreach (var kvp in row)
            {
                if (string.Equals(kvp.Key, columnName, StringComparison.OrdinalIgnoreCase))
                {
                    columnValue = kvp.Value.Value;
                    break;
                }
            }
        }

        if (columnValue is null)
        {
            return false;
        }

        foreach (var literal in cond.Values)
        {
            var litVal = ParseLiteralValue(literal);
            if (litVal is null) continue;

            if (CompareValues(columnValue, litVal) == 0)
            {
                return !cond.IsNegated;
            }
        }

        return cond.IsNegated;
    }

    private bool EvaluateLogical(SqlLogicalCondition logical, IReadOnlyDictionary<string, QueryValue> row)
    {
        if (logical.Operator == SqlLogicalOperator.And)
        {
            foreach (var cond in logical.Conditions)
            {
                if (!EvaluateCondition(cond, row))
                {
                    return false;
                }
            }
            return true;
        }
        else // OR
        {
            foreach (var cond in logical.Conditions)
            {
                if (EvaluateCondition(cond, row))
                {
                    return true;
                }
            }
            return false;
        }
    }

    #endregion

    #region Helpers

    private static object ParseNumber(string value)
    {
        if (value.Contains('.'))
        {
            return decimal.Parse(value, CultureInfo.InvariantCulture);
        }

        if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var l))
        {
            // Use int if it fits, otherwise long
            if (l >= int.MinValue && l <= int.MaxValue)
            {
                return (int)l;
            }
            return l;
        }

        return decimal.Parse(value, CultureInfo.InvariantCulture);
    }

    private static object? ParseLiteralValue(SqlLiteral literal)
    {
        return literal.Type switch
        {
            SqlLiteralType.Null => null,
            SqlLiteralType.String => literal.Value,
            SqlLiteralType.Number => ParseNumber(literal.Value!),
            _ => literal.Value
        };
    }

    private static object? EvaluateArithmetic(object left, SqlBinaryOperator op, object right)
    {
        // Promote both to a common numeric type
        var (l, r) = PromoteNumeric(left, right);

        return (l, r) switch
        {
            (decimal ld, decimal rd) => op switch
            {
                SqlBinaryOperator.Add => ld + rd,
                SqlBinaryOperator.Subtract => ld - rd,
                SqlBinaryOperator.Multiply => ld * rd,
                SqlBinaryOperator.Divide => rd == 0 ? throw new DivideByZeroException() : ld / rd,
                SqlBinaryOperator.Modulo => rd == 0 ? throw new DivideByZeroException() : ld % rd,
                _ => throw new NotSupportedException($"Operator {op} is not supported for arithmetic.")
            },
            (double ld, double rd) => op switch
            {
                SqlBinaryOperator.Add => ld + rd,
                SqlBinaryOperator.Subtract => ld - rd,
                SqlBinaryOperator.Multiply => ld * rd,
                SqlBinaryOperator.Divide => rd == 0.0 ? throw new DivideByZeroException() : ld / rd, // CodeQL [cs/equality-on-floats] SQL semantics: exact zero guard is intentional
                SqlBinaryOperator.Modulo => rd == 0.0 ? throw new DivideByZeroException() : ld % rd, // CodeQL [cs/equality-on-floats] SQL semantics: exact zero guard is intentional
                _ => throw new NotSupportedException($"Operator {op} is not supported for arithmetic.")
            },
            (long ld, long rd) => op switch
            {
                SqlBinaryOperator.Add => ld + rd,
                SqlBinaryOperator.Subtract => ld - rd,
                SqlBinaryOperator.Multiply => ld * rd,
                SqlBinaryOperator.Divide => rd == 0 ? throw new DivideByZeroException() : ld / rd,
                SqlBinaryOperator.Modulo => rd == 0 ? throw new DivideByZeroException() : ld % rd,
                _ => throw new NotSupportedException($"Operator {op} is not supported for arithmetic.")
            },
            _ => throw new QueryExecutionException(
                QueryErrorCode.TypeMismatch,
                $"Cannot perform arithmetic ({op}) on incompatible types: " +
                $"{left.GetType().Name} and {right.GetType().Name}.")
        };
    }

    private static (object, object) PromoteNumeric(object left, object right)
    {
        // If either is double/float, promote both to double
        if (left is double || left is float || right is double || right is float)
        {
            return (Convert.ToDouble(left, CultureInfo.InvariantCulture),
                    Convert.ToDouble(right, CultureInfo.InvariantCulture));
        }

        // If either is decimal, promote both to decimal
        if (left is decimal || right is decimal)
        {
            return (Convert.ToDecimal(left, CultureInfo.InvariantCulture),
                    Convert.ToDecimal(right, CultureInfo.InvariantCulture));
        }

        // Otherwise, promote both to long (covers int, short, byte, etc.)
        return (Convert.ToInt64(left, CultureInfo.InvariantCulture),
                Convert.ToInt64(right, CultureInfo.InvariantCulture));
    }

    private static object NegateValue(object value)
    {
        return value switch
        {
            int i => -i,
            long l => -l,
            decimal d => -d,
            double d => -d,
            float f => -f,
            _ => throw new QueryExecutionException(
                QueryErrorCode.TypeMismatch,
                $"Cannot negate value of type {value.GetType().Name}.")
        };
    }

    private static int CompareValues(object left, object right)
    {
        // String comparison (case-insensitive, like SQL Server default)
        if (left is string ls && right is string rs)
        {
            return string.Compare(ls, rs, StringComparison.OrdinalIgnoreCase);
        }

        // Numeric comparison with type promotion
        if (IsNumeric(left) && IsNumeric(right))
        {
            var ld = Convert.ToDecimal(left, CultureInfo.InvariantCulture);
            var rd = Convert.ToDecimal(right, CultureInfo.InvariantCulture);
            return ld.CompareTo(rd);
        }

        // String vs number: try to parse string as number
        if (IsNumeric(left) && right is string rightStr)
        {
            if (decimal.TryParse(rightStr, NumberStyles.Any, CultureInfo.InvariantCulture, out var rd))
            {
                var ld = Convert.ToDecimal(left, CultureInfo.InvariantCulture);
                return ld.CompareTo(rd);
            }
        }

        if (left is string leftStr && IsNumeric(right))
        {
            if (decimal.TryParse(leftStr, NumberStyles.Any, CultureInfo.InvariantCulture, out var ld))
            {
                var rd = Convert.ToDecimal(right, CultureInfo.InvariantCulture);
                return ld.CompareTo(rd);
            }
        }

        // DateTime comparison
        if (left is DateTime ldt && right is DateTime rdt)
        {
            return ldt.CompareTo(rdt);
        }

        // Guid comparison
        if (left is Guid lg && right is Guid rg)
        {
            return lg.CompareTo(rg);
        }

        // Boolean comparison
        if (left is bool lb && right is bool rb)
        {
            return lb.CompareTo(rb);
        }

        // Fallback: string comparison
        var leftString = Convert.ToString(left, CultureInfo.InvariantCulture) ?? "";
        var rightString = Convert.ToString(right, CultureInfo.InvariantCulture) ?? "";
        return string.Compare(leftString, rightString, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsNumeric(object value)
    {
        return value is int or long or short or byte or decimal or double or float;
    }

    /// <summary>
    /// Matches a string against a SQL LIKE pattern without regex (prevents ReDoS).
    /// % matches any sequence of characters (including empty).
    /// _ matches exactly one character.
    /// All other characters match literally (case-insensitive).
    /// </summary>
    private static bool MatchLikePattern(string input, string pattern)
    {
        int inputIdx = 0;
        int patternIdx = 0;
        int starInputIdx = -1;
        int starPatternIdx = -1;

        while (inputIdx < input.Length)
        {
            if (patternIdx < pattern.Length &&
                (pattern[patternIdx] == '_' ||
                 char.ToUpperInvariant(pattern[patternIdx]) == char.ToUpperInvariant(input[inputIdx])))
            {
                inputIdx++;
                patternIdx++;
            }
            else if (patternIdx < pattern.Length && pattern[patternIdx] == '%')
            {
                starPatternIdx = patternIdx;
                starInputIdx = inputIdx;
                patternIdx++;
            }
            else if (starPatternIdx >= 0)
            {
                patternIdx = starPatternIdx + 1;
                starInputIdx++;
                inputIdx = starInputIdx;
            }
            else
            {
                return false;
            }
        }

        while (patternIdx < pattern.Length && pattern[patternIdx] == '%')
        {
            patternIdx++;
        }

        return patternIdx == pattern.Length;
    }

    #endregion
}

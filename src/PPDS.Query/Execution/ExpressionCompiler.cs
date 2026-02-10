using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Microsoft.SqlServer.TransactSql.ScriptDom;
using PPDS.Dataverse.Query;
using PPDS.Dataverse.Query.Execution;
using PPDS.Dataverse.Query.Execution.Functions;

namespace PPDS.Query.Execution;

/// <summary>
/// Compiles ScriptDom AST expression and condition nodes into executable delegates.
/// Mirrors the evaluation logic of <see cref="ExpressionEvaluator"/> but produces
/// closures (<see cref="CompiledScalarExpression"/> and <see cref="CompiledPredicate"/>)
/// instead of walking the AST at evaluation time.
/// </summary>
public sealed class ExpressionCompiler
{
    private readonly FunctionRegistry _functionRegistry;
    private readonly Func<VariableScope?>? _variableScopeAccessor;

    /// <summary>
    /// Creates a compiler with optional function registry and variable scope accessor.
    /// When <paramref name="functionRegistry"/> is null, creates a default registry with all built-in functions.
    /// </summary>
    /// <param name="functionRegistry">Function registry for dispatching function calls. Null creates a default.</param>
    /// <param name="variableScopeAccessor">Lazy accessor for variable resolution. Null disables variable support.</param>
    public ExpressionCompiler(
        FunctionRegistry? functionRegistry = null,
        Func<VariableScope?>? variableScopeAccessor = null)
    {
        _functionRegistry = functionRegistry ?? FunctionRegistry.CreateDefault();
        _variableScopeAccessor = variableScopeAccessor;
    }

    // ═══════════════════════════════════════════════════════════════════
    //  CompileScalar — ScriptDom ScalarExpression → CompiledScalarExpression
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Compiles a ScriptDom <see cref="ScalarExpression"/> into an executable delegate.
    /// </summary>
    /// <param name="expression">The ScriptDom scalar expression AST node.</param>
    /// <returns>A delegate that evaluates the expression against a row.</returns>
    public CompiledScalarExpression CompileScalar(ScalarExpression expression)
    {
        return expression switch
        {
            IntegerLiteral intLit => CompileIntegerLiteral(intLit),
            StringLiteral strLit => CompileStringLiteral(strLit),
            NullLiteral => CompileNullLiteral(),
            NumericLiteral numLit => CompileNumericLiteral(numLit),
            RealLiteral realLit => CompileRealLiteral(realLit),
            ColumnReferenceExpression colRef => CompileColumnReference(colRef),
            BinaryExpression binExpr => CompileBinaryExpression(binExpr),
            UnaryExpression unaryExpr => CompileUnaryExpression(unaryExpr),
            ParenthesisExpression parenExpr => CompileScalar(parenExpr.Expression),
            SearchedCaseExpression caseExpr => CompileSearchedCase(caseExpr),
            IIfCall iifCall => CompileIIfCall(iifCall),
            FunctionCall funcCall => CompileFunctionCall(funcCall),
            CastCall castCall => CompileCastCall(castCall),
            ConvertCall convertCall => CompileConvertCall(convertCall),
            VariableReference varRef => CompileVariableReference(varRef),
            _ => throw new NotSupportedException(
                $"ScriptDom expression type {expression.GetType().Name} is not yet supported by ExpressionCompiler.")
        };
    }

    // ═══════════════════════════════════════════════════════════════════
    //  CompilePredicate — ScriptDom BooleanExpression → CompiledPredicate
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Compiles a ScriptDom <see cref="BooleanExpression"/> into an executable predicate delegate.
    /// </summary>
    /// <param name="predicate">The ScriptDom boolean expression AST node.</param>
    /// <returns>A delegate that evaluates the predicate against a row.</returns>
    public CompiledPredicate CompilePredicate(BooleanExpression predicate)
    {
        return predicate switch
        {
            BooleanComparisonExpression comp => CompileBooleanComparison(comp),
            BooleanIsNullExpression isNull => CompileBooleanIsNull(isNull),
            LikePredicate like => CompileLikePredicate(like),
            InPredicate inPred => CompileInPredicate(inPred),
            BooleanBinaryExpression boolBin => CompileBooleanBinary(boolBin),
            BooleanNotExpression notExpr => CompileBooleanNot(notExpr),
            BooleanParenthesisExpression parenExpr => CompilePredicate(parenExpr.Expression),
            ExistsPredicate => throw new NotSupportedException(
                "EXISTS predicates are handled at the plan level, not by the ExpressionCompiler."),
            _ => throw new NotSupportedException(
                $"ScriptDom predicate type {predicate.GetType().Name} is not yet supported by ExpressionCompiler.")
        };
    }

    // ═══════════════════════════════════════════════════════════════════
    //  Scalar compilation methods
    // ═══════════════════════════════════════════════════════════════════

    private static CompiledScalarExpression CompileIntegerLiteral(IntegerLiteral lit)
    {
        var value = ParseNumber(lit.Value);
        return _ => value;
    }

    private static CompiledScalarExpression CompileStringLiteral(StringLiteral lit)
    {
        var value = lit.Value;
        return _ => value;
    }

    private static CompiledScalarExpression CompileNullLiteral()
    {
        return _ => null;
    }

    private static CompiledScalarExpression CompileNumericLiteral(NumericLiteral lit)
    {
        var value = decimal.Parse(lit.Value, CultureInfo.InvariantCulture);
        return _ => value;
    }

    private static CompiledScalarExpression CompileRealLiteral(RealLiteral lit)
    {
        var value = double.Parse(lit.Value, CultureInfo.InvariantCulture);
        return _ => value;
    }

    private static CompiledScalarExpression CompileColumnReference(ColumnReferenceExpression colRef)
    {
        var identifiers = colRef.MultiPartIdentifier?.Identifiers;
        if (identifiers == null || identifiers.Count == 0)
        {
            return _ => null;
        }

        // Build full qualified name (e.g. "table.column") and simple name (last identifier)
        var fullName = string.Join(".", identifiers.Select(id => id.Value));
        var simpleName = identifiers[identifiers.Count - 1].Value;

        return row =>
        {
            // Try exact match on full name first
            if (row.TryGetValue(fullName, out var qv))
                return qv.Value;

            // Try case-insensitive on full name
            foreach (var kvp in row)
            {
                if (string.Equals(kvp.Key, fullName, StringComparison.OrdinalIgnoreCase))
                    return kvp.Value.Value;
            }

            // For qualified names (table.column), also try just the column name
            if (identifiers.Count > 1)
            {
                if (row.TryGetValue(simpleName, out var simpleQv))
                    return simpleQv.Value;

                foreach (var kvp in row)
                {
                    if (string.Equals(kvp.Key, simpleName, StringComparison.OrdinalIgnoreCase))
                        return kvp.Value.Value;
                }
            }

            return null;
        };
    }

    private CompiledScalarExpression CompileBinaryExpression(BinaryExpression binExpr)
    {
        var compiledLeft = CompileScalar(binExpr.FirstExpression);
        var compiledRight = CompileScalar(binExpr.SecondExpression);
        var op = binExpr.BinaryExpressionType;

        return row =>
        {
            var left = compiledLeft(row);
            var right = compiledRight(row);

            // NULL propagation: any arithmetic with NULL returns NULL
            if (left is null || right is null)
                return null;

            // String concatenation: Add when either operand is a string
            if (op == BinaryExpressionType.Add && (left is string || right is string))
            {
                return Convert.ToString(left, CultureInfo.InvariantCulture)
                     + Convert.ToString(right, CultureInfo.InvariantCulture);
            }

            // Numeric operations
            return EvaluateArithmetic(left, op, right);
        };
    }

    private CompiledScalarExpression CompileUnaryExpression(UnaryExpression unaryExpr)
    {
        var compiledOperand = CompileScalar(unaryExpr.Expression);
        var op = unaryExpr.UnaryExpressionType;

        return row =>
        {
            var operand = compiledOperand(row);
            if (operand is null)
                return null;

            return op switch
            {
                UnaryExpressionType.Negative => NegateValue(operand),
                UnaryExpressionType.BitwiseNot => operand is bool b
                    ? (object)!b
                    : throw new QueryExecutionException(
                        QueryErrorCode.TypeMismatch,
                        $"NOT requires a boolean operand, but got {operand.GetType().Name}."),
                _ => throw new NotSupportedException($"Unary operator {op} is not supported.")
            };
        };
    }

    private CompiledScalarExpression CompileSearchedCase(SearchedCaseExpression caseExpr)
    {
        var whenClauses = new List<(CompiledPredicate condition, CompiledScalarExpression result)>();
        foreach (var whenClause in caseExpr.WhenClauses)
        {
            var condition = CompilePredicate(whenClause.WhenExpression);
            var result = CompileScalar(whenClause.ThenExpression);
            whenClauses.Add((condition, result));
        }

        var elseExpr = caseExpr.ElseExpression != null
            ? CompileScalar(caseExpr.ElseExpression)
            : null;

        return row =>
        {
            foreach (var (condition, result) in whenClauses)
            {
                if (condition(row))
                    return result(row);
            }
            return elseExpr?.Invoke(row);
        };
    }

    private CompiledScalarExpression CompileIIfCall(IIfCall iifCall)
    {
        var condition = CompilePredicate(iifCall.Predicate);
        var trueExpr = CompileScalar(iifCall.ThenExpression);
        var falseExpr = CompileScalar(iifCall.ElseExpression);

        return row => condition(row) ? trueExpr(row) : falseExpr(row);
    }

    private CompiledScalarExpression CompileFunctionCall(FunctionCall funcCall)
    {
        var functionName = funcCall.FunctionName.Value;
        var compiledArgs = funcCall.Parameters?.Select(CompileScalar).ToArray()
                           ?? Array.Empty<CompiledScalarExpression>();

        return row =>
        {
            var args = new object?[compiledArgs.Length];
            for (int i = 0; i < compiledArgs.Length; i++)
            {
                args[i] = compiledArgs[i](row);
            }
            return _functionRegistry.Invoke(functionName, args);
        };
    }

    private CompiledScalarExpression CompileCastCall(CastCall castCall)
    {
        var compiledInner = CompileScalar(castCall.Parameter);
        var targetType = FormatDataType(castCall.DataType);

        return row =>
        {
            var value = compiledInner(row);
            if (value is null)
                return null;
            return CastConverter.Convert(value, targetType);
        };
    }

    private CompiledScalarExpression CompileConvertCall(ConvertCall convertCall)
    {
        var compiledInner = CompileScalar(convertCall.Parameter);
        var targetType = FormatDataType(convertCall.DataType);

        // Style is optional — usually an IntegerLiteral
        int? style = null;
        if (convertCall.Style is IntegerLiteral styleLit)
        {
            style = int.Parse(styleLit.Value, CultureInfo.InvariantCulture);
        }

        return row =>
        {
            var value = compiledInner(row);
            if (value is null)
                return null;
            return CastConverter.Convert(value, targetType, style);
        };
    }

    private CompiledScalarExpression CompileVariableReference(VariableReference varRef)
    {
        var variableName = varRef.Name;

        return _ =>
        {
            var scope = _variableScopeAccessor?.Invoke();
            if (scope == null)
            {
                throw new QueryExecutionException(
                    QueryErrorCode.ExecutionFailed,
                    $"Variable {variableName} cannot be resolved: no VariableScope is configured.");
            }
            return scope.Get(variableName);
        };
    }

    // ═══════════════════════════════════════════════════════════════════
    //  Predicate compilation methods
    // ═══════════════════════════════════════════════════════════════════

    private CompiledPredicate CompileBooleanComparison(BooleanComparisonExpression comp)
    {
        var compiledLeft = CompileScalar(comp.FirstExpression);
        var compiledRight = CompileScalar(comp.SecondExpression);
        var compType = comp.ComparisonType;

        return row =>
        {
            var left = compiledLeft(row);
            var right = compiledRight(row);

            // SQL semantics: comparison with NULL always returns false
            if (left is null || right is null)
                return false;

            int cmp = CompareValues(left, right);

            return compType switch
            {
                BooleanComparisonType.Equals => cmp == 0,
                BooleanComparisonType.NotEqualToBrackets => cmp != 0,
                BooleanComparisonType.NotEqualToExclamation => cmp != 0,
                BooleanComparisonType.LessThan => cmp < 0,
                BooleanComparisonType.GreaterThan => cmp > 0,
                BooleanComparisonType.LessThanOrEqualTo => cmp <= 0,
                BooleanComparisonType.GreaterThanOrEqualTo => cmp >= 0,
                _ => throw new NotSupportedException($"Comparison type {compType} is not supported.")
            };
        };
    }

    private CompiledPredicate CompileBooleanIsNull(BooleanIsNullExpression isNull)
    {
        var compiledExpr = CompileScalar(isNull.Expression);
        var isNot = isNull.IsNot;

        return row =>
        {
            var value = compiledExpr(row);
            bool valueIsNull = value is null;
            return isNot ? !valueIsNull : valueIsNull;
        };
    }

    private CompiledPredicate CompileLikePredicate(LikePredicate like)
    {
        var compiledExpr = CompileScalar(like.FirstExpression);
        var compiledPattern = CompileScalar(like.SecondExpression);
        var notDefined = like.NotDefined;

        return row =>
        {
            var value = compiledExpr(row);
            var pattern = compiledPattern(row);

            if (value is null || pattern is null)
                return false;

            var str = Convert.ToString(value, CultureInfo.InvariantCulture) ?? "";
            var patternStr = Convert.ToString(pattern, CultureInfo.InvariantCulture) ?? "";
            var matches = MatchLikePattern(str, patternStr);

            return notDefined ? !matches : matches;
        };
    }

    private CompiledPredicate CompileInPredicate(InPredicate inPred)
    {
        var compiledExpr = CompileScalar(inPred.Expression);
        var compiledValues = inPred.Values.Select(CompileScalar).ToArray();
        var notDefined = inPred.NotDefined;

        return row =>
        {
            var value = compiledExpr(row);
            if (value is null)
                return false;

            foreach (var compiledVal in compiledValues)
            {
                var litVal = compiledVal(row);
                if (litVal is null)
                    continue;

                if (CompareValues(value, litVal) == 0)
                    return !notDefined;
            }

            return notDefined;
        };
    }

    private CompiledPredicate CompileBooleanBinary(BooleanBinaryExpression boolBin)
    {
        var compiledLeft = CompilePredicate(boolBin.FirstExpression);
        var compiledRight = CompilePredicate(boolBin.SecondExpression);
        var op = boolBin.BinaryExpressionType;

        return op switch
        {
            BooleanBinaryExpressionType.And => row => compiledLeft(row) && compiledRight(row),
            BooleanBinaryExpressionType.Or => row => compiledLeft(row) || compiledRight(row),
            _ => throw new NotSupportedException($"Boolean binary operator {op} is not supported.")
        };
    }

    private CompiledPredicate CompileBooleanNot(BooleanNotExpression notExpr)
    {
        var compiledInner = CompilePredicate(notExpr.Expression);
        return row => !compiledInner(row);
    }

    // ═══════════════════════════════════════════════════════════════════
    //  Static helpers (ported from ExpressionEvaluator)
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Compares two non-null values with SQL-style type coercion.
    /// Returns negative if left &lt; right, zero if equal, positive if left &gt; right.
    /// </summary>
    internal static int CompareValues(object left, object right)
    {
        // String comparison (case-insensitive, like SQL Server default)
        if (left is string ls && right is string rs)
            return string.Compare(ls, rs, StringComparison.OrdinalIgnoreCase);

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
            return ldt.CompareTo(rdt);

        // Guid comparison
        if (left is Guid lg && right is Guid rg)
            return lg.CompareTo(rg);

        // Boolean comparison
        if (left is bool lb && right is bool rb)
            return lb.CompareTo(rb);

        // Fallback: string comparison
        var leftString = Convert.ToString(left, CultureInfo.InvariantCulture) ?? "";
        var rightString = Convert.ToString(right, CultureInfo.InvariantCulture) ?? "";
        return string.Compare(leftString, rightString, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Promotes two numeric values to a common type for arithmetic.
    /// Returns (double, double), (decimal, decimal), or (long, long).
    /// </summary>
    internal static (object, object) PromoteNumeric(object left, object right)
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

    /// <summary>
    /// Evaluates an arithmetic operation on two non-null values.
    /// </summary>
    internal static object? EvaluateArithmetic(object left, BinaryExpressionType op, object right)
    {
        var (l, r) = PromoteNumeric(left, right);

        return (l, r) switch
        {
            (decimal ld, decimal rd) => op switch
            {
                BinaryExpressionType.Add => ld + rd,
                BinaryExpressionType.Subtract => ld - rd,
                BinaryExpressionType.Multiply => ld * rd,
                BinaryExpressionType.Divide => rd == 0 ? throw new DivideByZeroException() : ld / rd,
                BinaryExpressionType.Modulo => rd == 0 ? throw new DivideByZeroException() : ld % rd,
                BinaryExpressionType.BitwiseAnd => (long)ld & (long)rd,
                BinaryExpressionType.BitwiseOr => (long)ld | (long)rd,
                BinaryExpressionType.BitwiseXor => (long)ld ^ (long)rd,
                _ => throw new NotSupportedException($"Operator {op} is not supported for arithmetic.")
            },
            (double ld, double rd) => op switch
            {
                BinaryExpressionType.Add => ld + rd,
                BinaryExpressionType.Subtract => ld - rd,
                BinaryExpressionType.Multiply => ld * rd,
                BinaryExpressionType.Divide => rd == 0.0 ? throw new DivideByZeroException() : ld / rd, // CodeQL [cs/equality-on-floats] SQL semantics: exact zero guard is intentional
                BinaryExpressionType.Modulo => rd == 0.0 ? throw new DivideByZeroException() : ld % rd, // CodeQL [cs/equality-on-floats] SQL semantics: exact zero guard is intentional
                BinaryExpressionType.BitwiseAnd => (long)ld & (long)rd,
                BinaryExpressionType.BitwiseOr => (long)ld | (long)rd,
                BinaryExpressionType.BitwiseXor => (long)ld ^ (long)rd,
                _ => throw new NotSupportedException($"Operator {op} is not supported for arithmetic.")
            },
            (long ld, long rd) => op switch
            {
                BinaryExpressionType.Add => ld + rd,
                BinaryExpressionType.Subtract => ld - rd,
                BinaryExpressionType.Multiply => ld * rd,
                BinaryExpressionType.Divide => rd == 0 ? throw new DivideByZeroException() : ld / rd,
                BinaryExpressionType.Modulo => rd == 0 ? throw new DivideByZeroException() : ld % rd,
                BinaryExpressionType.BitwiseAnd => ld & rd,
                BinaryExpressionType.BitwiseOr => ld | rd,
                BinaryExpressionType.BitwiseXor => ld ^ rd,
                _ => throw new NotSupportedException($"Operator {op} is not supported for arithmetic.")
            },
            _ => throw new QueryExecutionException(
                QueryErrorCode.TypeMismatch,
                $"Cannot perform arithmetic ({op}) on incompatible types: " +
                $"{left.GetType().Name} and {right.GetType().Name}.")
        };
    }

    /// <summary>
    /// Negates a numeric value.
    /// </summary>
    internal static object NegateValue(object value)
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

    /// <summary>
    /// Parses a string to the most appropriate numeric type (int, long, or decimal).
    /// </summary>
    internal static object ParseNumber(string value)
    {
        if (value.Contains('.'))
            return decimal.Parse(value, CultureInfo.InvariantCulture);

        if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var l))
        {
            // Use int if it fits, otherwise long
            if (l >= int.MinValue && l <= int.MaxValue)
                return (int)l;
            return l;
        }

        return decimal.Parse(value, CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Matches a string against a SQL LIKE pattern without regex (prevents ReDoS).
    /// % matches any sequence of characters (including empty).
    /// _ matches exactly one character.
    /// All other characters match literally (case-insensitive).
    /// </summary>
    internal static bool MatchLikePattern(string input, string pattern)
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

    /// <summary>
    /// Returns true if the value is a numeric type.
    /// </summary>
    internal static bool IsNumeric(object value)
    {
        return value is int or long or short or byte or decimal or double or float;
    }

    // ═══════════════════════════════════════════════════════════════════
    //  Private helpers
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Formats a ScriptDom DataTypeReference as a string for CastConverter.
    /// E.g., SqlDataTypeOption.Int → "int", SqlDataTypeOption.NVarChar with param 100 → "nvarchar(100)".
    /// </summary>
    private static string FormatDataType(DataTypeReference dataType)
    {
        if (dataType is SqlDataTypeReference sqlType)
        {
            var name = sqlType.SqlDataTypeOption.ToString().ToLowerInvariant();
            if (sqlType.Parameters.Count > 0)
            {
                var parms = string.Join(", ", sqlType.Parameters.Select(p => p.Value));
                return $"{name}({parms})";
            }
            return name;
        }

        if (dataType is XmlDataTypeReference)
            return "xml";

        // Generic fallback
        if (dataType.Name?.Identifiers != null && dataType.Name.Identifiers.Count > 0)
            return string.Join(".", dataType.Name.Identifiers.Select(i => i.Value));

        return "varchar";
    }
}

using System.Collections.Generic;
using PPDS.Dataverse.Query;
using PPDS.Dataverse.Query.Execution;
using PPDS.Dataverse.Sql.Ast;
using Xunit;

namespace PPDS.Dataverse.Tests.Query.Execution;

/// <summary>
/// Verifies that ExpressionEvaluator throws QueryExecutionException
/// with the correct QueryErrorCode values for type mismatches and
/// undeclared variable access.
/// </summary>
[Trait("Category", "PlanUnit")]
public class ErrorCodeTests
{
    private readonly ExpressionEvaluator _eval = new();

    private static IReadOnlyDictionary<string, QueryValue> Row(params (string key, object? value)[] pairs)
    {
        var dict = new Dictionary<string, QueryValue>();
        foreach (var (key, value) in pairs)
        {
            dict[key] = QueryValue.Simple(value);
        }
        return dict;
    }

    private static readonly IReadOnlyDictionary<string, QueryValue> EmptyRow =
        new Dictionary<string, QueryValue>();

    #region TypeMismatch — Negation

    [Fact]
    public void Negate_String_ThrowsTypeMismatch()
    {
        // -"hello" should fail: strings are not numeric
        var row = Row(("val", "hello"));
        var expr = new SqlUnaryExpression(
            SqlUnaryOperator.Negate,
            new SqlColumnExpression(SqlColumnRef.Simple("val")));

        var ex = Assert.Throws<QueryExecutionException>(() => _eval.Evaluate(expr, row));
        Assert.Equal(QueryErrorCode.TypeMismatch, ex.ErrorCode);
    }

    [Fact]
    public void Negate_Boolean_ThrowsTypeMismatch()
    {
        // -true should fail: booleans are not numeric
        var row = Row(("flag", true));
        var expr = new SqlUnaryExpression(
            SqlUnaryOperator.Negate,
            new SqlColumnExpression(SqlColumnRef.Simple("flag")));

        var ex = Assert.Throws<QueryExecutionException>(() => _eval.Evaluate(expr, row));
        Assert.Equal(QueryErrorCode.TypeMismatch, ex.ErrorCode);
    }

    #endregion

    #region TypeMismatch — NOT

    [Fact]
    public void Not_String_ThrowsTypeMismatch()
    {
        // NOT "hello" should fail: strings are not boolean
        var row = Row(("val", "hello"));
        var expr = new SqlUnaryExpression(
            SqlUnaryOperator.Not,
            new SqlColumnExpression(SqlColumnRef.Simple("val")));

        var ex = Assert.Throws<QueryExecutionException>(() => _eval.Evaluate(expr, row));
        Assert.Equal(QueryErrorCode.TypeMismatch, ex.ErrorCode);
    }

    [Fact]
    public void Not_Integer_ThrowsTypeMismatch()
    {
        // NOT 42 should fail: integers are not boolean
        var row = Row(("val", 42));
        var expr = new SqlUnaryExpression(
            SqlUnaryOperator.Not,
            new SqlColumnExpression(SqlColumnRef.Simple("val")));

        var ex = Assert.Throws<QueryExecutionException>(() => _eval.Evaluate(expr, row));
        Assert.Equal(QueryErrorCode.TypeMismatch, ex.ErrorCode);
    }

    #endregion

    #region ExecutionFailed — Variables

    [Fact]
    public void Variable_NoScope_ThrowsExecutionFailed()
    {
        // Evaluating a variable with no VariableScope configured
        var expr = new SqlVariableExpression("@threshold");

        var ex = Assert.Throws<QueryExecutionException>(() => _eval.Evaluate(expr, EmptyRow));
        Assert.Equal(QueryErrorCode.ExecutionFailed, ex.ErrorCode);
    }

    [Fact]
    public void Variable_Undeclared_ThrowsExecutionFailed()
    {
        // Evaluating a variable that has not been declared
        var scope = new VariableScope();
        _eval.VariableScope = scope;

        var expr = new SqlVariableExpression("@missing");

        var ex = Assert.Throws<QueryExecutionException>(() => _eval.Evaluate(expr, EmptyRow));
        Assert.Equal(QueryErrorCode.ExecutionFailed, ex.ErrorCode);
    }

    [Fact]
    public void Variable_SetUndeclared_ThrowsExecutionFailed()
    {
        // SET on a variable that has not been declared
        var scope = new VariableScope();

        var ex = Assert.Throws<QueryExecutionException>(() => scope.Set("@x", 42));
        Assert.Equal(QueryErrorCode.ExecutionFailed, ex.ErrorCode);
    }

    #endregion
}

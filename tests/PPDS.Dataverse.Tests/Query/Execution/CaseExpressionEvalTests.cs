using System.Collections.Generic;
using PPDS.Dataverse.Query;
using PPDS.Dataverse.Query.Execution;
using PPDS.Dataverse.Sql.Ast;
using Xunit;

namespace PPDS.Dataverse.Tests.Query.Execution;

[Trait("Category", "PlanUnit")]
public class CaseExpressionEvalTests
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

    #region CASE Expression Evaluation

    [Fact]
    public void EvaluateCase_FirstMatchingWhen_ReturnsResult()
    {
        // CASE WHEN status = 1 THEN 'Active' ELSE 'Inactive' END
        var caseExpr = new SqlCaseExpression(
            new[]
            {
                new SqlWhenClause(
                    new SqlComparisonCondition(
                        SqlColumnRef.Simple("status"),
                        SqlComparisonOperator.Equal,
                        SqlLiteral.Number("1")),
                    new SqlLiteralExpression(SqlLiteral.String("Active")))
            },
            new SqlLiteralExpression(SqlLiteral.String("Inactive")));

        var row = Row(("status", 1));
        var result = _eval.Evaluate(caseExpr, row);

        Assert.Equal("Active", result);
    }

    [Fact]
    public void EvaluateCase_NoMatch_ReturnsElse()
    {
        // CASE WHEN status = 1 THEN 'Active' ELSE 'Inactive' END
        var caseExpr = new SqlCaseExpression(
            new[]
            {
                new SqlWhenClause(
                    new SqlComparisonCondition(
                        SqlColumnRef.Simple("status"),
                        SqlComparisonOperator.Equal,
                        SqlLiteral.Number("1")),
                    new SqlLiteralExpression(SqlLiteral.String("Active")))
            },
            new SqlLiteralExpression(SqlLiteral.String("Inactive")));

        var row = Row(("status", 0));
        var result = _eval.Evaluate(caseExpr, row);

        Assert.Equal("Inactive", result);
    }

    [Fact]
    public void EvaluateCase_NoMatchNoElse_ReturnsNull()
    {
        // CASE WHEN status = 1 THEN 'Active' END
        var caseExpr = new SqlCaseExpression(
            new[]
            {
                new SqlWhenClause(
                    new SqlComparisonCondition(
                        SqlColumnRef.Simple("status"),
                        SqlComparisonOperator.Equal,
                        SqlLiteral.Number("1")),
                    new SqlLiteralExpression(SqlLiteral.String("Active")))
            });

        var row = Row(("status", 0));
        var result = _eval.Evaluate(caseExpr, row);

        Assert.Null(result);
    }

    [Fact]
    public void EvaluateCase_MultipleWhens_ReturnsFirstMatch()
    {
        // CASE WHEN status = 0 THEN 'Draft' WHEN status = 1 THEN 'Active' WHEN status = 2 THEN 'Closed' END
        var caseExpr = new SqlCaseExpression(
            new[]
            {
                new SqlWhenClause(
                    new SqlComparisonCondition(
                        SqlColumnRef.Simple("status"),
                        SqlComparisonOperator.Equal,
                        SqlLiteral.Number("0")),
                    new SqlLiteralExpression(SqlLiteral.String("Draft"))),
                new SqlWhenClause(
                    new SqlComparisonCondition(
                        SqlColumnRef.Simple("status"),
                        SqlComparisonOperator.Equal,
                        SqlLiteral.Number("1")),
                    new SqlLiteralExpression(SqlLiteral.String("Active"))),
                new SqlWhenClause(
                    new SqlComparisonCondition(
                        SqlColumnRef.Simple("status"),
                        SqlComparisonOperator.Equal,
                        SqlLiteral.Number("2")),
                    new SqlLiteralExpression(SqlLiteral.String("Closed")))
            });

        var row = Row(("status", 1));
        var result = _eval.Evaluate(caseExpr, row);

        Assert.Equal("Active", result);
    }

    [Fact]
    public void EvaluateCase_WithColumnResult()
    {
        // CASE WHEN status = 1 THEN name ELSE description END
        var caseExpr = new SqlCaseExpression(
            new[]
            {
                new SqlWhenClause(
                    new SqlComparisonCondition(
                        SqlColumnRef.Simple("status"),
                        SqlComparisonOperator.Equal,
                        SqlLiteral.Number("1")),
                    new SqlColumnExpression(SqlColumnRef.Simple("name")))
            },
            new SqlColumnExpression(SqlColumnRef.Simple("description")));

        var row = Row(("status", 1), ("name", "Contoso"), ("description", "A company"));
        var result = _eval.Evaluate(caseExpr, row);

        Assert.Equal("Contoso", result);
    }

    #endregion

    #region IIF Expression Evaluation

    [Fact]
    public void EvaluateIif_WhenTrue_ReturnsTrueValue()
    {
        // IIF(revenue > 1000000, 'High', 'Low')
        var iif = new SqlIifExpression(
            new SqlComparisonCondition(
                SqlColumnRef.Simple("revenue"),
                SqlComparisonOperator.GreaterThan,
                SqlLiteral.Number("1000000")),
            new SqlLiteralExpression(SqlLiteral.String("High")),
            new SqlLiteralExpression(SqlLiteral.String("Low")));

        var row = Row(("revenue", 2000000m));
        var result = _eval.Evaluate(iif, row);

        Assert.Equal("High", result);
    }

    [Fact]
    public void EvaluateIif_WhenFalse_ReturnsFalseValue()
    {
        // IIF(revenue > 1000000, 'High', 'Low')
        var iif = new SqlIifExpression(
            new SqlComparisonCondition(
                SqlColumnRef.Simple("revenue"),
                SqlComparisonOperator.GreaterThan,
                SqlLiteral.Number("1000000")),
            new SqlLiteralExpression(SqlLiteral.String("High")),
            new SqlLiteralExpression(SqlLiteral.String("Low")));

        var row = Row(("revenue", 500000m));
        var result = _eval.Evaluate(iif, row);

        Assert.Equal("Low", result);
    }

    [Fact]
    public void EvaluateIif_WithNullConditionValue_ReturnsFalseValue()
    {
        // IIF(revenue > 1000000, 'High', 'Low') where revenue is NULL
        // SQL NULL in comparison always returns false, so IIF should return FalseValue
        var iif = new SqlIifExpression(
            new SqlComparisonCondition(
                SqlColumnRef.Simple("revenue"),
                SqlComparisonOperator.GreaterThan,
                SqlLiteral.Number("1000000")),
            new SqlLiteralExpression(SqlLiteral.String("High")),
            new SqlLiteralExpression(SqlLiteral.String("Low")));

        var row = Row(("revenue", null));
        var result = _eval.Evaluate(iif, row);

        Assert.Equal("Low", result);
    }

    [Fact]
    public void EvaluateIif_WithColumnResults()
    {
        // IIF(status = 1, name, description)
        var iif = new SqlIifExpression(
            new SqlComparisonCondition(
                SqlColumnRef.Simple("status"),
                SqlComparisonOperator.Equal,
                SqlLiteral.Number("1")),
            new SqlColumnExpression(SqlColumnRef.Simple("name")),
            new SqlColumnExpression(SqlColumnRef.Simple("description")));

        var row = Row(("status", 1), ("name", "Contoso"), ("description", "A company"));
        var result = _eval.Evaluate(iif, row);

        Assert.Equal("Contoso", result);
    }

    #endregion
}

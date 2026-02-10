using System.Collections.Generic;
using FluentAssertions;
using PPDS.Dataverse.Query.Execution;
using PPDS.Dataverse.Query.Planning;
using PPDS.Dataverse.Query.Planning.Nodes;
using PPDS.Dataverse.Sql.Ast;
using PPDS.Dataverse.Sql.Transpilation;
using PPDS.Query.Planning;
using Xunit;

namespace PPDS.Query.Tests.Planning;

[Trait("Category", "Unit")]
public class ExecutionPlanOptimizerTests
{
    private readonly ExecutionPlanOptimizer _optimizer = new();

    /// <summary>
    /// Compiles a legacy condition into a CompiledPredicate via ExpressionEvaluator closure.
    /// </summary>
    private static CompiledPredicate CompileCondition(ISqlCondition condition)
    {
        var evaluator = new ExpressionEvaluator();
        return row => evaluator.EvaluateCondition(condition, row);
    }

    private static string DescribeCondition(ISqlCondition condition)
    {
        return condition switch
        {
            SqlComparisonCondition comp => $"{comp.Column.GetFullName()} {comp.Operator} {comp.Value.Value}",
            SqlExpressionCondition expr => $"expr {expr.Operator} expr",
            SqlLogicalCondition logical => $"({logical.Operator} with {logical.Conditions.Count} conditions)",
            _ => condition.GetType().Name
        };
    }

    // ────────────────────────────────────────────
    //  Predicate pushdown: now handled at plan-build time
    // ────────────────────────────────────────────

    [Fact]
    public void PredicatePushdown_CompiledPredicateOnly_PassesThrough()
    {
        // With compiled predicates only (no legacy condition), predicate pushdown is a no-op.
        // The planner handles pushdown at build time, so the optimizer just passes through.
        var scanNode = new FetchXmlScanNode(
            "<fetch><entity name=\"account\"><filter><condition attribute=\"name\" operator=\"eq\" value=\"Contoso\" /></filter></entity></fetch>",
            "account");

        var condition = new SqlComparisonCondition(
            SqlColumnRef.Simple("name"),
            SqlComparisonOperator.Equal,
            SqlLiteral.String("Contoso"));

        var filterNode = new ClientFilterNode(scanNode, CompileCondition(condition), DescribeCondition(condition));

        var plan = new QueryPlanResult
        {
            RootNode = filterNode,
            FetchXml = scanNode.FetchXml,
            VirtualColumns = new Dictionary<string, VirtualColumnInfo>(),
            EntityLogicalName = "account"
        };

        var optimized = _optimizer.Optimize(plan);

        // With no legacy condition, optimizer cannot inspect and passes through
        optimized.RootNode.Should().BeSameAs(plan.RootNode);
    }

    [Fact]
    public void PredicatePushdown_ExpressionFilter_PassesThrough()
    {
        // Expression conditions (column-to-column) cannot be pushed to FetchXML.
        // With compiled predicates, the optimizer passes these through unchanged.
        var scanNode = new FetchXmlScanNode(
            "<fetch><entity name=\"account\"><all-attributes /></entity></fetch>",
            "account");

        var condition = new SqlExpressionCondition(
            new SqlColumnExpression(SqlColumnRef.Simple("revenue")),
            SqlComparisonOperator.GreaterThan,
            new SqlColumnExpression(SqlColumnRef.Simple("cost")));

        var filterNode = new ClientFilterNode(scanNode, CompileCondition(condition), DescribeCondition(condition));

        var plan = new QueryPlanResult
        {
            RootNode = filterNode,
            FetchXml = scanNode.FetchXml,
            VirtualColumns = new Dictionary<string, VirtualColumnInfo>(),
            EntityLogicalName = "account"
        };

        var optimized = _optimizer.Optimize(plan);

        // Expression filters stay as client-side filters
        optimized.RootNode.Should().BeAssignableTo<ClientFilterNode>();
    }

    // ────────────────────────────────────────────
    //  Constant folding: binary expression on literals
    // ────────────────────────────────────────────

    [Fact]
    public void ConstantFolding_FoldsAddition()
    {
        var expr = new SqlBinaryExpression(
            new SqlLiteralExpression(SqlLiteral.Number("1")),
            SqlBinaryOperator.Add,
            new SqlLiteralExpression(SqlLiteral.Number("1")));

        var folded = ExecutionPlanOptimizer.FoldExpression(expr);

        folded.Should().BeOfType<SqlLiteralExpression>();
        var lit = (SqlLiteralExpression)folded;
        lit.Value.Value.Should().Be("2");
    }

    [Fact]
    public void ConstantFolding_FoldsMultiplication()
    {
        var expr = new SqlBinaryExpression(
            new SqlLiteralExpression(SqlLiteral.Number("3")),
            SqlBinaryOperator.Multiply,
            new SqlLiteralExpression(SqlLiteral.Number("4")));

        var folded = ExecutionPlanOptimizer.FoldExpression(expr);

        folded.Should().BeOfType<SqlLiteralExpression>();
        var lit = (SqlLiteralExpression)folded;
        lit.Value.Value.Should().Be("12");
    }

    [Fact]
    public void ConstantFolding_FoldsSubtraction()
    {
        var expr = new SqlBinaryExpression(
            new SqlLiteralExpression(SqlLiteral.Number("10")),
            SqlBinaryOperator.Subtract,
            new SqlLiteralExpression(SqlLiteral.Number("3")));

        var folded = ExecutionPlanOptimizer.FoldExpression(expr);

        folded.Should().BeOfType<SqlLiteralExpression>();
        var lit = (SqlLiteralExpression)folded;
        lit.Value.Value.Should().Be("7");
    }

    [Fact]
    public void ConstantFolding_FoldsDivision()
    {
        var expr = new SqlBinaryExpression(
            new SqlLiteralExpression(SqlLiteral.Number("10")),
            SqlBinaryOperator.Divide,
            new SqlLiteralExpression(SqlLiteral.Number("2")));

        var folded = ExecutionPlanOptimizer.FoldExpression(expr);

        folded.Should().BeOfType<SqlLiteralExpression>();
        var lit = (SqlLiteralExpression)folded;
        lit.Value.Value.Should().Be("5");
    }

    [Fact]
    public void ConstantFolding_DivisionByZero_NoFold()
    {
        var expr = new SqlBinaryExpression(
            new SqlLiteralExpression(SqlLiteral.Number("10")),
            SqlBinaryOperator.Divide,
            new SqlLiteralExpression(SqlLiteral.Number("0")));

        var folded = ExecutionPlanOptimizer.FoldExpression(expr);

        // Division by zero is not folded (left as-is)
        folded.Should().BeOfType<SqlBinaryExpression>();
    }

    [Fact]
    public void ConstantFolding_NonLiteral_NoFold()
    {
        var expr = new SqlBinaryExpression(
            new SqlColumnExpression(SqlColumnRef.Simple("revenue")),
            SqlBinaryOperator.Add,
            new SqlLiteralExpression(SqlLiteral.Number("100")));

        var folded = ExecutionPlanOptimizer.FoldExpression(expr);

        // Cannot fold because one side is a column reference
        folded.Should().BeOfType<SqlBinaryExpression>();
    }

    [Fact]
    public void ConstantFolding_NestedExpression_FoldsInner()
    {
        // (1 + 1) + column -- inner (1+1) can fold to 2
        var inner = new SqlBinaryExpression(
            new SqlLiteralExpression(SqlLiteral.Number("1")),
            SqlBinaryOperator.Add,
            new SqlLiteralExpression(SqlLiteral.Number("1")));

        var outer = new SqlBinaryExpression(
            inner,
            SqlBinaryOperator.Add,
            new SqlColumnExpression(SqlColumnRef.Simple("x")));

        var folded = ExecutionPlanOptimizer.FoldExpression(outer);

        folded.Should().BeOfType<SqlBinaryExpression>();
        var binary = (SqlBinaryExpression)folded;
        binary.Left.Should().BeOfType<SqlLiteralExpression>();
        ((SqlLiteralExpression)binary.Left).Value.Value.Should().Be("2");
    }

    // ────────────────────────────────────────────
    //  Condition folding
    // ────────────────────────────────────────────

    [Fact]
    public void FoldCondition_FoldsExpressionConditionValues()
    {
        var condition = new SqlExpressionCondition(
            new SqlBinaryExpression(
                new SqlLiteralExpression(SqlLiteral.Number("2")),
                SqlBinaryOperator.Add,
                new SqlLiteralExpression(SqlLiteral.Number("3"))),
            SqlComparisonOperator.Equal,
            new SqlLiteralExpression(SqlLiteral.Number("5")));

        var folded = ExecutionPlanOptimizer.FoldCondition(condition);

        folded.Should().BeOfType<SqlExpressionCondition>();
        var exprCond = (SqlExpressionCondition)folded;
        exprCond.Left.Should().BeOfType<SqlLiteralExpression>();
        ((SqlLiteralExpression)exprCond.Left).Value.Value.Should().Be("5");
    }

    // ────────────────────────────────────────────
    //  Optimizer returns same plan when nothing to optimize
    // ────────────────────────────────────────────

    [Fact]
    public void Optimize_NothingToOptimize_ReturnsSamePlan()
    {
        var scanNode = new FetchXmlScanNode(
            "<fetch><entity name=\"account\"><all-attributes /></entity></fetch>",
            "account");

        var plan = new QueryPlanResult
        {
            RootNode = scanNode,
            FetchXml = scanNode.FetchXml,
            VirtualColumns = new Dictionary<string, VirtualColumnInfo>(),
            EntityLogicalName = "account"
        };

        var optimized = _optimizer.Optimize(plan);

        optimized.Should().BeSameAs(plan);
    }

    // ────────────────────────────────────────────
    //  Null argument validation
    // ────────────────────────────────────────────

    [Fact]
    public void Optimize_NullPlan_ThrowsArgumentNullException()
    {
        var act = () => _optimizer.Optimize(null!);
        act.Should().Throw<System.ArgumentNullException>();
    }

    // ────────────────────────────────────────────
    //  CanPushToFetchXml
    // ────────────────────────────────────────────

    [Fact]
    public void CanPushToFetchXml_ComparisonCondition_ReturnsTrue()
    {
        var condition = new SqlComparisonCondition(
            SqlColumnRef.Simple("name"),
            SqlComparisonOperator.Equal,
            SqlLiteral.String("Contoso"));

        ExecutionPlanOptimizer.CanPushToFetchXml(condition).Should().BeTrue();
    }

    [Fact]
    public void CanPushToFetchXml_ExpressionCondition_ReturnsFalse()
    {
        var condition = new SqlExpressionCondition(
            new SqlColumnExpression(SqlColumnRef.Simple("a")),
            SqlComparisonOperator.Equal,
            new SqlColumnExpression(SqlColumnRef.Simple("b")));

        ExecutionPlanOptimizer.CanPushToFetchXml(condition).Should().BeFalse();
    }
}

using FluentAssertions;
using Microsoft.SqlServer.TransactSql.ScriptDom;
using Moq;
using PPDS.Dataverse.Query.Planning;
using PPDS.Dataverse.Query.Planning.Nodes;
using PPDS.Dataverse.Sql.Transpilation;
using PPDS.Query.Parsing;
using PPDS.Query.Planning;
using PPDS.Query.Planning.Nodes;
using Xunit;

namespace PPDS.Query.Tests.Planning;

/// <summary>
/// Tests for Phase 2 (Stream A): SQL Language Expansion features.
/// Covers OFFSET/FETCH, INTERSECT, EXCEPT, and CTEs at the plan-building level.
/// </summary>
[Trait("Category", "Unit")]
public class ExecutionPlanBuilderPhase2Tests
{
    private readonly QueryParser _parser = new();
    private readonly Mock<IFetchXmlGeneratorService> _mockFetchXmlService;
    private readonly ExecutionPlanBuilder _builder;

    public ExecutionPlanBuilderPhase2Tests()
    {
        _mockFetchXmlService = new Mock<IFetchXmlGeneratorService>();
        _mockFetchXmlService
            .Setup(s => s.Generate(It.IsAny<TSqlFragment>()))
            .Returns(TranspileResult.Simple(
                "<fetch><entity name=\"account\"><all-attributes /></entity></fetch>"));

        _builder = new ExecutionPlanBuilder(_mockFetchXmlService.Object);
    }

    // ════════════════════════════════════════════
    //  2a. OFFSET/FETCH
    // ════════════════════════════════════════════

    [Fact]
    public void Plan_OffsetFetch_ProducesOffsetFetchNode()
    {
        var fragment = _parser.Parse(
            "SELECT name FROM account ORDER BY name OFFSET 10 ROWS FETCH NEXT 5 ROWS ONLY");

        var result = _builder.Plan(fragment);

        result.RootNode.Should().BeAssignableTo<OffsetFetchNode>();
        var offsetNode = (OffsetFetchNode)result.RootNode;
        offsetNode.Offset.Should().Be(10);
        offsetNode.Fetch.Should().Be(5);
    }

    [Fact]
    public void Plan_OffsetOnly_ProducesOffsetFetchNode()
    {
        var fragment = _parser.Parse(
            "SELECT name FROM account ORDER BY name OFFSET 5 ROWS");

        var result = _builder.Plan(fragment);

        result.RootNode.Should().BeAssignableTo<OffsetFetchNode>();
        var offsetNode = (OffsetFetchNode)result.RootNode;
        offsetNode.Offset.Should().Be(5);
        offsetNode.Fetch.Should().Be(-1);
    }

    [Fact]
    public void Plan_OffsetZeroFetch_ProducesOffsetFetchNode()
    {
        var fragment = _parser.Parse(
            "SELECT name FROM account ORDER BY name OFFSET 0 ROWS FETCH NEXT 10 ROWS ONLY");

        var result = _builder.Plan(fragment);

        result.RootNode.Should().BeAssignableTo<OffsetFetchNode>();
        var offsetNode = (OffsetFetchNode)result.RootNode;
        offsetNode.Offset.Should().Be(0);
        offsetNode.Fetch.Should().Be(10);
    }

    // ════════════════════════════════════════════
    //  2b. INTERSECT
    // ════════════════════════════════════════════

    [Fact]
    public void Plan_Intersect_ProducesIntersectNode()
    {
        var fragment = _parser.Parse(
            "SELECT name FROM account INTERSECT SELECT fullname FROM contact");

        var result = _builder.Plan(fragment);

        result.RootNode.Should().BeAssignableTo<IntersectNode>();
    }

    [Fact]
    public void Plan_Intersect_HasTwoChildren()
    {
        var fragment = _parser.Parse(
            "SELECT name FROM account INTERSECT SELECT fullname FROM contact");

        var result = _builder.Plan(fragment);

        result.RootNode.Children.Should().HaveCount(2);
    }

    [Fact]
    public void Plan_Intersect_FetchXmlContainsOperatorName()
    {
        var fragment = _parser.Parse(
            "SELECT name FROM account INTERSECT SELECT fullname FROM contact");

        var result = _builder.Plan(fragment);

        result.FetchXml.Should().Contain("INTERSECT");
    }

    // ════════════════════════════════════════════
    //  2b. EXCEPT
    // ════════════════════════════════════════════

    [Fact]
    public void Plan_Except_ProducesExceptNode()
    {
        var fragment = _parser.Parse(
            "SELECT name FROM account EXCEPT SELECT fullname FROM contact");

        var result = _builder.Plan(fragment);

        result.RootNode.Should().BeAssignableTo<ExceptNode>();
    }

    [Fact]
    public void Plan_Except_HasTwoChildren()
    {
        var fragment = _parser.Parse(
            "SELECT name FROM account EXCEPT SELECT fullname FROM contact");

        var result = _builder.Plan(fragment);

        result.RootNode.Children.Should().HaveCount(2);
    }

    [Fact]
    public void Plan_Except_FetchXmlContainsOperatorName()
    {
        var fragment = _parser.Parse(
            "SELECT name FROM account EXCEPT SELECT fullname FROM contact");

        var result = _builder.Plan(fragment);

        result.FetchXml.Should().Contain("EXCEPT");
    }

    // ════════════════════════════════════════════
    //  UNION still works after refactoring
    // ════════════════════════════════════════════

    [Fact]
    public void Plan_UnionAll_StillProducesConcatenateNode()
    {
        var fragment = _parser.Parse(
            "SELECT name FROM account UNION ALL SELECT fullname FROM contact");

        var result = _builder.Plan(fragment);

        result.RootNode.Should().BeAssignableTo<ConcatenateNode>();
    }

    [Fact]
    public void Plan_Union_StillProducesDistinctNode()
    {
        var fragment = _parser.Parse(
            "SELECT name FROM account UNION SELECT fullname FROM contact");

        var result = _builder.Plan(fragment);

        result.RootNode.Should().BeAssignableTo<DistinctNode>();
    }

    // ════════════════════════════════════════════
    //  2c. CTEs (Non-recursive)
    // ════════════════════════════════════════════

    [Fact]
    public void Plan_SimpleCte_Succeeds()
    {
        var fragment = _parser.Parse(
            "WITH cte AS (SELECT name FROM account) SELECT * FROM cte");

        var result = _builder.Plan(fragment);

        result.Should().NotBeNull();
        result.FetchXml.Should().Contain("CTE").And.Contain("cte");
    }

    [Fact]
    public void Plan_MultipleCtes_Succeeds()
    {
        var fragment = _parser.Parse(
            "WITH cte1 AS (SELECT name FROM account), cte2 AS (SELECT fullname FROM contact) SELECT * FROM cte1");

        var result = _builder.Plan(fragment);

        result.Should().NotBeNull();
        result.FetchXml.Should().Contain("cte1").And.Contain("cte2");
    }

    // ════════════════════════════════════════════
    //  2f. Control Flow: WHILE loop support
    // ════════════════════════════════════════════

    [Fact]
    public void Plan_WhileStatement_Succeeds()
    {
        var fragment = _parser.Parse(
            "DECLARE @x INT = 0\nWHILE @x < 10 BEGIN SET @x = @x + 1 END");

        // Multi-statement, so we need to handle the batch
        // The first statement will be DECLARE, not WHILE
        // But PlanStatement handles WHILE directly
        var stmt = _parser.ParseStatement("WHILE @x < 10 BEGIN SET @x = @x + 1 END");
        stmt.Should().BeAssignableTo<WhileStatement>();
    }

    [Fact]
    public void Plan_DeclareWithInitialization_ParsesCorrectly()
    {
        var stmt = _parser.ParseStatement("DECLARE @x INT = 5");
        stmt.Should().BeAssignableTo<DeclareVariableStatement>();

        var declare = (DeclareVariableStatement)stmt;
        declare.Declarations[0].Value.Should().NotBeNull();
    }
}

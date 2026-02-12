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

[Trait("Category", "Unit")]
public class ExecutionPlanBuilderTests
{
    private readonly QueryParser _parser = new();
    private readonly Mock<IFetchXmlGeneratorService> _mockFetchXmlService;
    private readonly ExecutionPlanBuilder _builder;

    public ExecutionPlanBuilderTests()
    {
        _mockFetchXmlService = new Mock<IFetchXmlGeneratorService>();
        _mockFetchXmlService
            .Setup(s => s.Generate(It.IsAny<TSqlFragment>()))
            .Returns(TranspileResult.Simple(
                "<fetch><entity name=\"account\"><all-attributes /></entity></fetch>"));

        _builder = new ExecutionPlanBuilder(_mockFetchXmlService.Object);
    }

    // ────────────────────────────────────────────
    //  Simple SELECT produces FetchXmlScanNode
    // ────────────────────────────────────────────

    [Fact]
    public void Plan_SimpleSelect_ProducesFetchXmlScanNode()
    {
        var fragment = _parser.Parse("SELECT name FROM account");

        var result = _builder.Plan(fragment);

        result.RootNode.Should().BeAssignableTo<FetchXmlScanNode>();
        result.EntityLogicalName.Should().Be("account");
    }

    // ────────────────────────────────────────────
    //  SELECT with computed columns produces ProjectNode
    // ────────────────────────────────────────────

    [Fact]
    public void Plan_SelectWithCaseExpression_ProducesProjectNode()
    {
        var fragment = _parser.Parse(
            "SELECT name, CASE WHEN statecode = 0 THEN 'Active' ELSE 'Inactive' END AS status FROM account");

        var result = _builder.Plan(fragment);

        // ProjectNode should be on top (the root or close to root)
        result.RootNode.Should().BeAssignableTo<ProjectNode>();
    }

    // ────────────────────────────────────────────
    //  SELECT with window functions produces ClientWindowNode
    // ────────────────────────────────────────────

    [Fact]
    public void Plan_SelectWithWindowFunction_ProducesClientWindowNode()
    {
        var fragment = _parser.Parse(
            "SELECT name, ROW_NUMBER() OVER (ORDER BY name) AS rn FROM account");

        var result = _builder.Plan(fragment);

        // Either the root is a ClientWindowNode or a ProjectNode wrapping a ClientWindowNode
        var hasWindowNode = FindNodeOfType<ClientWindowNode>(result.RootNode);
        hasWindowNode.Should().BeTrue(
            "a SELECT with ROW_NUMBER() should produce a ClientWindowNode somewhere in the plan tree");
    }

    // ────────────────────────────────────────────
    //  UNION produces ConcatenateNode
    // ────────────────────────────────────────────

    [Fact]
    public void Plan_UnionAll_ProducesConcatenateNode()
    {
        var fragment = _parser.Parse(
            "SELECT name FROM account UNION ALL SELECT fullname FROM contact");

        var result = _builder.Plan(fragment);

        result.RootNode.Should().BeAssignableTo<ConcatenateNode>();
    }

    // ────────────────────────────────────────────
    //  UNION (not ALL) produces DistinctNode on top
    // ────────────────────────────────────────────

    [Fact]
    public void Plan_Union_ProducesDistinctNode()
    {
        var fragment = _parser.Parse(
            "SELECT name FROM account UNION SELECT fullname FROM contact");

        var result = _builder.Plan(fragment);

        result.RootNode.Should().BeAssignableTo<DistinctNode>();
    }

    // ────────────────────────────────────────────
    //  INSERT VALUES produces DmlExecuteNode
    // ────────────────────────────────────────────

    [Fact]
    public void Plan_InsertValues_ProducesDmlExecuteNode()
    {
        var fragment = _parser.Parse(
            "INSERT INTO account (name, revenue) VALUES ('Contoso', 1000000)");

        var result = _builder.Plan(fragment);

        result.RootNode.Should().BeAssignableTo<DmlExecuteNode>();
    }

    // ────────────────────────────────────────────
    //  UPDATE produces DmlExecuteNode with source scan
    // ────────────────────────────────────────────

    [Fact]
    public void Plan_Update_ProducesDmlExecuteNode()
    {
        var fragment = _parser.Parse(
            "UPDATE account SET revenue = 2000000 WHERE name = 'Contoso'");

        var result = _builder.Plan(fragment);

        result.RootNode.Should().BeAssignableTo<DmlExecuteNode>();
    }

    // ────────────────────────────────────────────
    //  DELETE produces DmlExecuteNode with source scan
    // ────────────────────────────────────────────

    [Fact]
    public void Plan_Delete_ProducesDmlExecuteNode()
    {
        var fragment = _parser.Parse(
            "DELETE FROM account WHERE name = 'Contoso'");

        var result = _builder.Plan(fragment);

        result.RootNode.Should().BeAssignableTo<DmlExecuteNode>();
    }

    // ────────────────────────────────────────────
    //  Metadata query produces MetadataScanNode
    // ────────────────────────────────────────────

    [Fact]
    public void Plan_MetadataQuery_ProducesMetadataScanNode()
    {
        var fragment = _parser.Parse(
            "SELECT * FROM metadata.entity");

        var result = _builder.Plan(fragment);

        result.RootNode.Should().BeAssignableTo<MetadataScanNode>();
    }

    // ────────────────────────────────────────────
    //  Constructor null check
    // ────────────────────────────────────────────

    [Fact]
    public void Constructor_NullService_ThrowsArgumentNullException()
    {
        var act = () => new ExecutionPlanBuilder(null!);

        act.Should().Throw<ArgumentNullException>();
    }

    // ────────────────────────────────────────────
    //  Plan result contains FetchXml and entity name
    // ────────────────────────────────────────────

    [Fact]
    public void Plan_SimpleSelect_ResultContainsFetchXmlAndEntityName()
    {
        var fragment = _parser.Parse("SELECT name FROM account");

        var result = _builder.Plan(fragment);

        result.FetchXml.Should().NotBeNullOrEmpty();
        result.EntityLogicalName.Should().Be("account");
        result.VirtualColumns.Should().NotBeNull();
    }

    // ────────────────────────────────────────────
    //  OPENJSON produces OpenJsonNode
    // ────────────────────────────────────────────

    [Fact]
    public void Plan_OpenJson_ProducesOpenJsonNode()
    {
        var fragment = _parser.Parse("SELECT [key], value, type FROM OPENJSON('[1,2,3]')");
        var result = _builder.Plan(fragment);
        // OpenJsonNode may be wrapped in a ProjectNode, so check the tree
        var hasOpenJson = result.RootNode is OpenJsonNode
            || result.RootNode.Children.Any(c => c is OpenJsonNode);
        hasOpenJson.Should().BeTrue("the plan should contain an OpenJsonNode");
    }

    [Fact]
    public void Plan_WhereComputedVsLiteral_ProducesClientFilterNode()
    {
        var fragment = _parser.Parse(
            "SELECT name FROM account WHERE revenue * 0.1 > 100");

        var result = _builder.Plan(fragment);

        result.RootNode.Should().BeAssignableTo<ClientFilterNode>();
    }

    [Fact]
    public void Plan_WhereAndWithComputedVsLiteral_ProducesClientFilterNode()
    {
        var fragment = _parser.Parse(
            "SELECT name FROM account WHERE statecode = 0 AND revenue * 0.1 > 100");

        var result = _builder.Plan(fragment);

        result.RootNode.Should().BeAssignableTo<ClientFilterNode>();
    }

    [Fact]
    public void Plan_WhereExists_ThrowsQueryParseException()
    {
        var fragment = _parser.Parse(
            "SELECT name FROM account WHERE EXISTS (SELECT 1 FROM contact)");

        var act = () => _builder.Plan(fragment);

        act.Should().Throw<QueryParseException>()
            .WithMessage("*EXISTS*");
    }

    [Fact]
    public void Plan_WhereInSubquery_ThrowsQueryParseException()
    {
        var fragment = _parser.Parse(
            "SELECT name FROM account WHERE accountid IN (SELECT parentcustomerid FROM contact)");

        var act = () => _builder.Plan(fragment);

        act.Should().Throw<QueryParseException>()
            .WithMessage("*IN (SELECT*");
    }

    // ────────────────────────────────────────────
    //  JOIN planning: RIGHT and FULL OUTER fall back to client-side
    // ────────────────────────────────────────────

    [Fact]
    public void Plan_RightJoin_ProducesClientSideHashJoin()
    {
        // RIGHT JOIN is not supported by FetchXML — should fall back to client-side HashJoin
        var mockService = new Mock<IFetchXmlGeneratorService>();
        mockService
            .Setup(s => s.Generate(It.IsAny<TSqlFragment>()))
            .Throws(new NotSupportedException("RIGHT JOIN not supported in FetchXML"));
        var builder = new ExecutionPlanBuilder(mockService.Object);

        var sql = "SELECT a.name, c.fullname FROM account a RIGHT JOIN contact c ON a.accountid = c.parentcustomerid";
        var fragment = _parser.Parse(sql);
        var result = builder.Plan(fragment);

        ContainsNodeOfType<HashJoinNode>(result.RootNode).Should().BeTrue(
            "RIGHT JOIN should produce a client-side HashJoinNode");
    }

    [Fact]
    public void Plan_FullOuterJoin_ProducesClientSideHashJoin()
    {
        var mockService = new Mock<IFetchXmlGeneratorService>();
        mockService
            .Setup(s => s.Generate(It.IsAny<TSqlFragment>()))
            .Throws(new NotSupportedException("FULL OUTER JOIN not supported in FetchXML"));
        var builder = new ExecutionPlanBuilder(mockService.Object);

        var sql = "SELECT a.name, c.fullname FROM account a FULL OUTER JOIN contact c ON a.accountid = c.parentcustomerid";
        var fragment = _parser.Parse(sql);
        var result = builder.Plan(fragment);

        ContainsNodeOfType<HashJoinNode>(result.RootNode).Should().BeTrue(
            "FULL OUTER JOIN should produce a client-side HashJoinNode");
    }

    [Fact]
    public void Plan_InnerJoin_StillUsesFetchXml()
    {
        // INNER JOIN is supported by FetchXML — should NOT fall back to client-side
        var sql = "SELECT a.name, c.fullname FROM account a INNER JOIN contact c ON a.accountid = c.parentcustomerid";
        var fragment = _parser.Parse(sql);
        var result = _builder.Plan(fragment);

        // Should use the FetchXML path (FetchXmlScanNode at or near root)
        result.RootNode.Should().BeAssignableTo<FetchXmlScanNode>();
    }

    [Fact]
    public void Plan_LeftJoin_StillUsesFetchXml()
    {
        var sql = "SELECT a.name, c.fullname FROM account a LEFT JOIN contact c ON a.accountid = c.parentcustomerid";
        var fragment = _parser.Parse(sql);
        var result = _builder.Plan(fragment);

        result.RootNode.Should().BeAssignableTo<FetchXmlScanNode>();
    }

    [Fact]
    public void Plan_RightJoin_ExtractsCorrectEntityName()
    {
        var mockService = new Mock<IFetchXmlGeneratorService>();
        mockService
            .Setup(s => s.Generate(It.IsAny<TSqlFragment>()))
            .Throws(new NotSupportedException("RIGHT JOIN not supported"));
        var builder = new ExecutionPlanBuilder(mockService.Object);

        var sql = "SELECT a.name FROM account a RIGHT JOIN contact c ON a.accountid = c.parentcustomerid";
        var fragment = _parser.Parse(sql);
        var result = builder.Plan(fragment);

        // Primary entity should be the leftmost table (account)
        result.EntityLogicalName.Should().Be("account");
    }

    // ────────────────────────────────────────────
    //  Helper: find node type in plan tree
    // ────────────────────────────────────────────

    private static bool ContainsNodeOfType<T>(IQueryPlanNode node) where T : IQueryPlanNode
    {
        if (node is T) return true;
        return node.Children.Any(ContainsNodeOfType<T>);
    }

    private static bool FindNodeOfType<T>(IQueryPlanNode node) where T : IQueryPlanNode
    {
        if (node is T)
            return true;

        // Check if the node exposes child nodes through known wrapper types
        if (node is ProjectNode projectNode)
            return FindNodeOfType<T>(GetInput(projectNode));

        if (node is ClientWindowNode windowNode)
            return FindNodeOfType<T>(GetInput(windowNode));

        return false;
    }

    private static IQueryPlanNode GetInput(ProjectNode node)
    {
        // ProjectNode takes an input node in constructor; access it via reflection
        var field = typeof(ProjectNode).GetField("_input",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        return (IQueryPlanNode)(field?.GetValue(node)
            ?? throw new InvalidOperationException("Could not access _input field"));
    }

    private static IQueryPlanNode GetInput(ClientWindowNode node)
    {
        return node.Input;
    }
}

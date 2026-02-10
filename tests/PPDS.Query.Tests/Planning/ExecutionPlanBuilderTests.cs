using FluentAssertions;
using Microsoft.SqlServer.TransactSql.ScriptDom;
using Moq;
using PPDS.Dataverse.Query.Planning;
using PPDS.Dataverse.Query.Planning.Nodes;
using PPDS.Dataverse.Sql.Transpilation;
using PPDS.Query.Parsing;
using PPDS.Query.Planning;
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
    //  Helper: find node type in plan tree
    // ────────────────────────────────────────────

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

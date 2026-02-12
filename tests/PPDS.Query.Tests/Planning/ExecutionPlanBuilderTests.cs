using FluentAssertions;
using Microsoft.SqlServer.TransactSql.ScriptDom;
using Moq;
using PPDS.Dataverse.Query;
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
    public void Plan_WhereInSubquery_ProducesPlan()
    {
        var fragment = _parser.Parse(
            "SELECT name FROM account WHERE accountid IN (SELECT parentcustomerid FROM contact)");

        var result = _builder.Plan(fragment);

        result.RootNode.Should().NotBeNull();
        ContainsNodeOfType<HashSemiJoinNode>(result.RootNode).Should().BeTrue(
            "IN (subquery) should now produce a HashSemiJoinNode instead of throwing");
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

        // After RIGHT JOIN swap, the preserved (right) side becomes the left,
        // so the primary entity is now "contact" (the original right table).
        result.EntityLogicalName.Should().Be("contact");
    }

    // ────────────────────────────────────────────
    //  CROSS JOIN produces NestedLoopJoinNode
    // ────────────────────────────────────────────

    [Fact]
    public void Plan_CrossJoin_ProducesClientSideNestedLoopJoin()
    {
        // CROSS JOIN is not supported by FetchXML — should route to client-side NestedLoopJoinNode
        var mockService = new Mock<IFetchXmlGeneratorService>();
        mockService
            .Setup(s => s.Generate(It.IsAny<TSqlFragment>()))
            .Throws(new NotSupportedException("CROSS JOIN not supported in FetchXML"));
        var builder = new ExecutionPlanBuilder(mockService.Object);

        var sql = "SELECT a.name, b.fullname FROM account a CROSS JOIN contact b";
        var fragment = _parser.Parse(sql);
        var result = builder.Plan(fragment);

        ContainsNodeOfType<NestedLoopJoinNode>(result.RootNode).Should().BeTrue(
            "CROSS JOIN should produce a client-side NestedLoopJoinNode");
    }

    [Fact]
    public void Plan_CrossJoin_ExtractsCorrectEntityName()
    {
        var mockService = new Mock<IFetchXmlGeneratorService>();
        mockService
            .Setup(s => s.Generate(It.IsAny<TSqlFragment>()))
            .Throws(new NotSupportedException("CROSS JOIN not supported"));
        var builder = new ExecutionPlanBuilder(mockService.Object);

        var sql = "SELECT a.name FROM account a CROSS JOIN contact b";
        var fragment = _parser.Parse(sql);
        var result = builder.Plan(fragment);

        // Primary entity should be the leftmost table (account)
        result.EntityLogicalName.Should().Be("account");
    }

    // ────────────────────────────────────────────
    //  CROSS APPLY / OUTER APPLY throws clear error
    // ────────────────────────────────────────────

    [Fact]
    public void Plan_CrossApply_ThrowsNotYetSupported()
    {
        var mockService = new Mock<IFetchXmlGeneratorService>();
        mockService
            .Setup(s => s.Generate(It.IsAny<TSqlFragment>()))
            .Throws(new NotSupportedException("CROSS APPLY not supported"));
        var builder = new ExecutionPlanBuilder(mockService.Object);

        var sql = "SELECT a.name, s.value FROM account a CROSS APPLY (SELECT value FROM contact WHERE contact.parentcustomerid = a.accountid) s";
        var fragment = _parser.Parse(sql);
        var act = () => builder.Plan(fragment);

        act.Should().Throw<QueryParseException>()
            .WithMessage("*CROSS APPLY*not yet supported*");
    }

    [Fact]
    public void Plan_OuterApply_ThrowsNotYetSupported()
    {
        var mockService = new Mock<IFetchXmlGeneratorService>();
        mockService
            .Setup(s => s.Generate(It.IsAny<TSqlFragment>()))
            .Throws(new NotSupportedException("OUTER APPLY not supported"));
        var builder = new ExecutionPlanBuilder(mockService.Object);

        var sql = "SELECT a.name, s.value FROM account a OUTER APPLY (SELECT value FROM contact WHERE contact.parentcustomerid = a.accountid) s";
        var fragment = _parser.Parse(sql);
        var act = () => builder.Plan(fragment);

        act.Should().Throw<QueryParseException>()
            .WithMessage("*OUTER APPLY*not yet supported*");
    }

    // ────────────────────────────────────────────
    //  RIGHT JOIN planner swap: converts to LEFT JOIN
    // ────────────────────────────────────────────

    [Fact]
    public void Plan_RightJoin_SwapsToLeftJoin()
    {
        var mockService = new Mock<IFetchXmlGeneratorService>();
        mockService
            .Setup(s => s.Generate(It.IsAny<TSqlFragment>()))
            .Throws(new NotSupportedException("RIGHT JOIN not supported in FetchXML"));
        var builder = new ExecutionPlanBuilder(mockService.Object);

        var sql = "SELECT a.name, c.fullname FROM account a RIGHT JOIN contact c ON a.accountid = c.parentcustomerid";
        var fragment = _parser.Parse(sql);
        var result = builder.Plan(fragment);

        // Should produce a client-side HashJoinNode with LEFT type (swapped from RIGHT)
        var hashJoin = FindNode<HashJoinNode>(result.RootNode);
        hashJoin.Should().NotBeNull("RIGHT JOIN should produce a client-side HashJoinNode");
        hashJoin!.JoinType.Should().Be(JoinType.Left,
            "planner should swap RIGHT JOIN to LEFT JOIN by swapping children");
    }

    // ────────────────────────────────────────────
    //  Client-side join post-pipeline: projection, ORDER BY, TOP, OFFSET/FETCH
    // ────────────────────────────────────────────

    [Fact]
    public void Plan_ClientSideJoin_WithSelectColumns_ProducesProjectNode()
    {
        // FULL OUTER JOIN with specific columns should produce a ProjectNode
        var mockService = new Mock<IFetchXmlGeneratorService>();
        mockService
            .Setup(s => s.Generate(It.IsAny<TSqlFragment>()))
            .Throws(new NotSupportedException("FULL OUTER JOIN not supported in FetchXML"));
        var builder = new ExecutionPlanBuilder(mockService.Object);

        var sql = "SELECT a.name, c.fullname FROM account a FULL OUTER JOIN contact c ON a.accountid = c.parentcustomerid";
        var fragment = _parser.Parse(sql);
        var result = builder.Plan(fragment);

        // Should have a ProjectNode filtering to only the requested columns
        var projectNode = FindNode<ProjectNode>(result.RootNode);
        projectNode.Should().NotBeNull(
            "client-side join with specific SELECT columns should produce a ProjectNode");
        projectNode!.OutputColumns.Should().HaveCount(2);
        projectNode.OutputColumns[0].OutputName.Should().Be("name");
        projectNode.OutputColumns[1].OutputName.Should().Be("fullname");
    }

    [Fact]
    public void Plan_ClientSideJoin_WithSelectStar_NoProjectNode()
    {
        // FULL OUTER JOIN with SELECT * should NOT produce a ProjectNode
        var mockService = new Mock<IFetchXmlGeneratorService>();
        mockService
            .Setup(s => s.Generate(It.IsAny<TSqlFragment>()))
            .Throws(new NotSupportedException("FULL OUTER JOIN not supported in FetchXML"));
        var builder = new ExecutionPlanBuilder(mockService.Object);

        var sql = "SELECT * FROM account a FULL OUTER JOIN contact c ON a.accountid = c.parentcustomerid";
        var fragment = _parser.Parse(sql);
        var result = builder.Plan(fragment);

        // Root should be a HashJoinNode (no ProjectNode wrapping it)
        result.RootNode.Should().BeAssignableTo<HashJoinNode>(
            "client-side join with SELECT * should not add a ProjectNode");
    }

    [Fact]
    public void Plan_ClientSideJoin_WithOrderBy_ProducesClientSortNode()
    {
        // FULL OUTER JOIN with ORDER BY should produce a ClientSortNode
        var mockService = new Mock<IFetchXmlGeneratorService>();
        mockService
            .Setup(s => s.Generate(It.IsAny<TSqlFragment>()))
            .Throws(new NotSupportedException("FULL OUTER JOIN not supported in FetchXML"));
        var builder = new ExecutionPlanBuilder(mockService.Object);

        var sql = "SELECT a.name, c.fullname FROM account a FULL OUTER JOIN contact c ON a.accountid = c.parentcustomerid ORDER BY a.name";
        var fragment = _parser.Parse(sql);
        var result = builder.Plan(fragment);

        var sortNode = FindNode<ClientSortNode>(result.RootNode);
        sortNode.Should().NotBeNull(
            "client-side join with ORDER BY should produce a ClientSortNode");
        sortNode!.OrderByItems.Should().HaveCount(1);
        sortNode.OrderByItems[0].ColumnName.Should().Be("name");
        sortNode.OrderByItems[0].Descending.Should().BeFalse();
    }

    [Fact]
    public void Plan_ClientSideJoin_WithOrderByDesc_ProducesDescendingSort()
    {
        var mockService = new Mock<IFetchXmlGeneratorService>();
        mockService
            .Setup(s => s.Generate(It.IsAny<TSqlFragment>()))
            .Throws(new NotSupportedException("FULL OUTER JOIN not supported in FetchXML"));
        var builder = new ExecutionPlanBuilder(mockService.Object);

        var sql = "SELECT a.name FROM account a FULL OUTER JOIN contact c ON a.accountid = c.parentcustomerid ORDER BY a.name DESC";
        var fragment = _parser.Parse(sql);
        var result = builder.Plan(fragment);

        var sortNode = FindNode<ClientSortNode>(result.RootNode);
        sortNode.Should().NotBeNull();
        sortNode!.OrderByItems[0].Descending.Should().BeTrue();
    }

    [Fact]
    public void Plan_ClientSideJoin_WithTop_ProducesOffsetFetchNode()
    {
        // FULL OUTER JOIN with TOP should produce an OffsetFetchNode
        var mockService = new Mock<IFetchXmlGeneratorService>();
        mockService
            .Setup(s => s.Generate(It.IsAny<TSqlFragment>()))
            .Throws(new NotSupportedException("FULL OUTER JOIN not supported in FetchXML"));
        var builder = new ExecutionPlanBuilder(mockService.Object);

        var sql = "SELECT TOP 5 a.name FROM account a FULL OUTER JOIN contact c ON a.accountid = c.parentcustomerid";
        var fragment = _parser.Parse(sql);
        var result = builder.Plan(fragment);

        var offsetFetchNode = FindNode<OffsetFetchNode>(result.RootNode);
        offsetFetchNode.Should().NotBeNull(
            "client-side join with TOP should produce an OffsetFetchNode");
        offsetFetchNode!.Offset.Should().Be(0);
        offsetFetchNode.Fetch.Should().Be(5);
    }

    [Fact]
    public void Plan_ClientSideJoin_WithOffsetFetch_ProducesOffsetFetchNode()
    {
        // FULL OUTER JOIN with OFFSET/FETCH should produce an OffsetFetchNode
        var mockService = new Mock<IFetchXmlGeneratorService>();
        mockService
            .Setup(s => s.Generate(It.IsAny<TSqlFragment>()))
            .Throws(new NotSupportedException("FULL OUTER JOIN not supported in FetchXML"));
        var builder = new ExecutionPlanBuilder(mockService.Object);

        var sql = "SELECT a.name FROM account a FULL OUTER JOIN contact c ON a.accountid = c.parentcustomerid ORDER BY a.name OFFSET 10 ROWS FETCH NEXT 5 ROWS ONLY";
        var fragment = _parser.Parse(sql);
        var result = builder.Plan(fragment);

        var offsetFetchNode = FindNode<OffsetFetchNode>(result.RootNode);
        offsetFetchNode.Should().NotBeNull(
            "client-side join with OFFSET/FETCH should produce an OffsetFetchNode");
        offsetFetchNode!.Offset.Should().Be(10);
        offsetFetchNode.Fetch.Should().Be(5);
    }

    [Fact]
    public void Plan_ClientSideJoin_PostPipeline_CorrectNodeOrder()
    {
        // Verify the correct ordering of post-pipeline nodes:
        // HashJoin -> ProjectNode -> ClientSortNode -> OffsetFetchNode (TOP)
        var mockService = new Mock<IFetchXmlGeneratorService>();
        mockService
            .Setup(s => s.Generate(It.IsAny<TSqlFragment>()))
            .Throws(new NotSupportedException("FULL OUTER JOIN not supported in FetchXML"));
        var builder = new ExecutionPlanBuilder(mockService.Object);

        var sql = "SELECT TOP 3 a.name FROM account a FULL OUTER JOIN contact c ON a.accountid = c.parentcustomerid ORDER BY a.name";
        var fragment = _parser.Parse(sql);
        var result = builder.Plan(fragment);

        // Root should be OffsetFetchNode (TOP) wrapping a ClientSortNode
        result.RootNode.Should().BeOfType<OffsetFetchNode>(
            "TOP should be the outermost node");

        // Next should be ClientSortNode
        var sortNode = result.RootNode.Children[0];
        sortNode.Should().BeOfType<ClientSortNode>(
            "ORDER BY sort should wrap the projection");

        // Next should be ProjectNode
        var projectChild = sortNode.Children[0];
        projectChild.Should().BeOfType<ProjectNode>(
            "SELECT list projection should wrap the join");

        // Innermost should be HashJoinNode
        ContainsNodeOfType<HashJoinNode>(projectChild).Should().BeTrue(
            "HashJoinNode should be at the base of the plan");
    }

    [Fact]
    public void Plan_CrossJoin_WithSelectColumns_ProducesProjectNode()
    {
        // CROSS JOIN (UnqualifiedJoin) should also get the post-pipeline
        var mockService = new Mock<IFetchXmlGeneratorService>();
        mockService
            .Setup(s => s.Generate(It.IsAny<TSqlFragment>()))
            .Throws(new NotSupportedException("CROSS JOIN not supported"));
        var builder = new ExecutionPlanBuilder(mockService.Object);

        var sql = "SELECT a.name FROM account a CROSS JOIN contact b";
        var fragment = _parser.Parse(sql);
        var result = builder.Plan(fragment);

        var projectNode = FindNode<ProjectNode>(result.RootNode);
        projectNode.Should().NotBeNull(
            "CROSS JOIN with specific columns should produce a ProjectNode");
        projectNode!.OutputColumns.Should().HaveCount(1);
        projectNode.OutputColumns[0].OutputName.Should().Be("name");
    }

    // ────────────────────────────────────────────
    //  Derived table (subquery in FROM) planning
    // ────────────────────────────────────────────

    [Fact]
    [Trait("Category", "Unit")]
    public void Plan_DerivedTable_ProducesTableSpool()
    {
        var sql = "SELECT sub.name FROM (SELECT name FROM account) AS sub";
        var fragment = _parser.Parse(sql);
        var result = _builder.Plan(fragment);

        result.RootNode.Should().NotBeNull();
        ContainsNodeOfType<TableSpoolNode>(result.RootNode).Should().BeTrue(
            "a derived table should produce a TableSpoolNode in the plan tree");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void Plan_DerivedTable_WithWhere_AppliesFilter()
    {
        var sql = "SELECT sub.name FROM (SELECT name, revenue FROM account WHERE revenue > 1000) AS sub WHERE sub.name IS NOT NULL";
        var fragment = _parser.Parse(sql);
        var result = _builder.Plan(fragment);

        result.RootNode.Should().NotBeNull();
    }

    // ────────────────────────────────────────────
    //  IN (subquery) / NOT IN (subquery)
    // ────────────────────────────────────────────

    [Fact]
    [Trait("Category", "Unit")]
    public void Plan_WhereInSubquery_ProducesHashSemiJoin()
    {
        var sql = "SELECT name FROM account WHERE accountid IN (SELECT parentcustomerid FROM contact WHERE statecode = 0)";
        var fragment = _parser.Parse(sql);
        var result = _builder.Plan(fragment);

        ContainsNodeOfType<HashSemiJoinNode>(result.RootNode).Should().BeTrue(
            "a WHERE ... IN (SELECT ...) should produce a HashSemiJoinNode in the plan tree");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void Plan_WhereNotInSubquery_ProducesHashSemiJoin()
    {
        var sql = "SELECT name FROM account WHERE accountid NOT IN (SELECT parentcustomerid FROM contact)";
        var fragment = _parser.Parse(sql);
        var result = _builder.Plan(fragment);

        ContainsNodeOfType<HashSemiJoinNode>(result.RootNode).Should().BeTrue(
            "a WHERE ... NOT IN (SELECT ...) should produce a HashSemiJoinNode in the plan tree");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void Plan_WhereInSubquery_WithAdditionalWhereCondition()
    {
        var sql = "SELECT name FROM account WHERE statecode = 0 AND accountid IN (SELECT parentcustomerid FROM contact)";
        var fragment = _parser.Parse(sql);
        var result = _builder.Plan(fragment);

        ContainsNodeOfType<HashSemiJoinNode>(result.RootNode).Should().BeTrue(
            "IN (subquery) combined with other WHERE conditions should still produce a HashSemiJoinNode");
    }

    // ────────────────────────────────────────────
    //  EXISTS / NOT EXISTS
    // ────────────────────────────────────────────

    [Fact]
    [Trait("Category", "Unit")]
    public void Plan_WhereExists_ProducesHashSemiJoin()
    {
        var sql = @"SELECT name FROM account a
                    WHERE EXISTS (SELECT 1 FROM contact c WHERE c.parentcustomerid = a.accountid)";
        var fragment = _parser.Parse(sql);
        var result = _builder.Plan(fragment);

        ContainsNodeOfType<HashSemiJoinNode>(result.RootNode).Should().BeTrue(
            "a WHERE EXISTS (SELECT ...) should produce a HashSemiJoinNode in the plan tree");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void Plan_WhereNotExists_ProducesHashSemiJoin()
    {
        var sql = @"SELECT name FROM account a
                    WHERE NOT EXISTS (SELECT 1 FROM contact c WHERE c.parentcustomerid = a.accountid)";
        var fragment = _parser.Parse(sql);
        var result = _builder.Plan(fragment);

        ContainsNodeOfType<HashSemiJoinNode>(result.RootNode).Should().BeTrue(
            "a WHERE NOT EXISTS (SELECT ...) should produce a HashSemiJoinNode in the plan tree");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void Plan_WhereExists_WithAdditionalCondition()
    {
        var sql = @"SELECT name FROM account a
                    WHERE statecode = 0 AND EXISTS (SELECT 1 FROM contact c WHERE c.parentcustomerid = a.accountid)";
        var fragment = _parser.Parse(sql);
        var result = _builder.Plan(fragment);

        ContainsNodeOfType<HashSemiJoinNode>(result.RootNode).Should().BeTrue(
            "EXISTS combined with other WHERE conditions should still produce a HashSemiJoinNode");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void Plan_WhereExists_WithoutCorrelation_ThrowsQueryParseException()
    {
        var sql = @"SELECT name FROM account a
                    WHERE EXISTS (SELECT 1 FROM contact c WHERE c.statecode = 0)";
        var fragment = _parser.Parse(sql);

        var act = () => _builder.Plan(fragment);

        act.Should().Throw<QueryParseException>()
            .WithMessage("*correlation*");
    }

    // ────────────────────────────────────────────
    //  Cross-environment query planning
    // ────────────────────────────────────────────

    [Fact]
    [Trait("Category", "Unit")]
    public void Plan_CrossEnvironment_ProducesRemoteScanNode()
    {
        var sql = "SELECT name FROM [UAT].dbo.account";
        var mockRemoteExecutor = Mock.Of<IQueryExecutor>();
        var options = new QueryPlanOptions
        {
            RemoteExecutorFactory = label => label == "UAT" ? mockRemoteExecutor : null
        };

        var result = _builder.Plan(_parser.Parse(sql), options);

        ContainsNodeOfType<RemoteScanNode>(result.RootNode).Should().BeTrue(
            "cross-environment [UAT].dbo.account should produce RemoteScanNode");
        ContainsNodeOfType<TableSpoolNode>(result.RootNode).Should().BeTrue(
            "remote scan should be wrapped in TableSpoolNode for materialization");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void Plan_CrossEnvironment_UnknownLabel_ThrowsDescriptiveError()
    {
        var sql = "SELECT name FROM [STAGING].dbo.account";
        var options = new QueryPlanOptions
        {
            RemoteExecutorFactory = label => null  // No matching profile
        };

        var act = () => _builder.Plan(_parser.Parse(sql), options);

        act.Should().Throw<QueryParseException>()
            .WithMessage("*STAGING*");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void Plan_CrossEnvironment_NoFactory_ThrowsDescriptiveError()
    {
        var sql = "SELECT name FROM [UAT].dbo.account";
        var options = new QueryPlanOptions();  // No RemoteExecutorFactory

        var act = () => _builder.Plan(_parser.Parse(sql), options);

        act.Should().Throw<QueryParseException>()
            .WithMessage("*remote executor factory*");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void Plan_TwoPartName_DoesNotTriggerCrossEnvironment()
    {
        // dbo.account is a 2-part name (SchemaIdentifier=dbo, BaseIdentifier=account)
        // and must NOT be treated as a cross-environment reference.
        var fragment = _parser.Parse("SELECT name FROM dbo.account");
        var result = _builder.Plan(fragment);

        result.RootNode.Should().BeAssignableTo<FetchXmlScanNode>(
            "2-part name dbo.account should remain a local FetchXmlScanNode");
    }

    // ────────────────────────────────────────────
    //  Helper: find node type in plan tree
    // ────────────────────────────────────────────

    private static T? FindNode<T>(IQueryPlanNode node) where T : class, IQueryPlanNode
    {
        if (node is T match) return match;
        foreach (var child in node.Children)
        {
            var found = FindNode<T>(child);
            if (found != null) return found;
        }
        return null;
    }

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

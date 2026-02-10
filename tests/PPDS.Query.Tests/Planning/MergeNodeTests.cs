using FluentAssertions;
using Microsoft.SqlServer.TransactSql.ScriptDom;
using Moq;
using PPDS.Dataverse.Query;
using PPDS.Dataverse.Query.Planning;
using PPDS.Dataverse.Sql.Ast;
using PPDS.Dataverse.Sql.Transpilation;
using PPDS.Query.Parsing;
using PPDS.Query.Planning;
using PPDS.Query.Planning.Nodes;
using Xunit;

namespace PPDS.Query.Tests.Planning;

[Trait("Category", "Unit")]
public class MergeNodeTests
{
    // ────────────────────────────────────────────
    //  Constructor validation
    // ────────────────────────────────────────────

    [Fact]
    public void Constructor_NullSource_ThrowsArgumentNullException()
    {
        var matchColumns = new List<MergeMatchColumn>
        {
            new("id", "accountid")
        };

        var act = () => new MergeNode(null!, "account", matchColumns);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Constructor_NullTargetEntity_ThrowsArgumentNullException()
    {
        var source = TestSourceNode.Create("source");
        var matchColumns = new List<MergeMatchColumn>
        {
            new("id", "accountid")
        };

        var act = () => new MergeNode(source, null!, matchColumns);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Constructor_NullMatchColumns_ThrowsArgumentNullException()
    {
        var source = TestSourceNode.Create("source");
        var act = () => new MergeNode(source, "account", null!);
        act.Should().Throw<ArgumentNullException>();
    }

    // ────────────────────────────────────────────
    //  Description and metadata
    // ────────────────────────────────────────────

    [Fact]
    public void Description_ContainsTargetEntity()
    {
        var source = TestSourceNode.Create("source");
        var matchColumns = new List<MergeMatchColumn> { new("id", "accountid") };
        var node = new MergeNode(source, "account", matchColumns);

        node.Description.Should().Contain("account");
    }

    [Fact]
    public void EstimatedRows_Returns1()
    {
        var source = TestSourceNode.Create("source");
        var matchColumns = new List<MergeMatchColumn> { new("id", "accountid") };
        var node = new MergeNode(source, "account", matchColumns);

        node.EstimatedRows.Should().Be(1);
    }

    [Fact]
    public void Children_ReturnsSingleInput()
    {
        var source = TestSourceNode.Create("source");
        var matchColumns = new List<MergeMatchColumn> { new("id", "accountid") };
        var node = new MergeNode(source, "account", matchColumns);

        node.Children.Should().HaveCount(1);
    }

    // ────────────────────────────────────────────
    //  Execution: WHEN NOT MATCHED INSERT
    // ────────────────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_WhenNotMatched_Insert_CountsInserts()
    {
        var sourceRows = TestSourceNode.Create("source",
            TestSourceNode.MakeRow("source", ("name", "Contoso"), ("city", "Redmond")),
            TestSourceNode.MakeRow("source", ("name", "Fabrikam"), ("city", "Seattle")));

        var matchColumns = new List<MergeMatchColumn> { new("name", "name") };
        var whenNotMatched = MergeWhenNotMatched.Insert(
            new List<string> { "name", "city" });

        var node = new MergeNode(sourceRows, "account", matchColumns,
            whenNotMatched: whenNotMatched);

        var rows = await TestHelpers.CollectRowsAsync(node);

        rows.Should().HaveCount(1);
        rows[0].Values["source_count"].Value.Should().Be(2L);
        rows[0].Values["inserted_count"].Value.Should().Be(2L);
    }

    // ────────────────────────────────────────────
    //  Execution: empty source yields zero counts
    // ────────────────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_EmptySource_YieldsZeroCounts()
    {
        var sourceRows = TestSourceNode.Create("source");

        var matchColumns = new List<MergeMatchColumn> { new("id", "accountid") };
        var whenNotMatched = MergeWhenNotMatched.Insert();

        var node = new MergeNode(sourceRows, "account", matchColumns,
            whenNotMatched: whenNotMatched);

        var rows = await TestHelpers.CollectRowsAsync(node);

        rows.Should().HaveCount(1);
        rows[0].Values["source_count"].Value.Should().Be(0L);
        rows[0].Values["inserted_count"].Value.Should().Be(0L);
        rows[0].Values["updated_count"].Value.Should().Be(0L);
        rows[0].Values["deleted_count"].Value.Should().Be(0L);
    }

    // ────────────────────────────────────────────
    //  MergeMatchColumn construction
    // ────────────────────────────────────────────

    [Fact]
    public void MergeMatchColumn_SetsProperties()
    {
        var col = new MergeMatchColumn("src_id", "tgt_id");
        col.SourceColumn.Should().Be("src_id");
        col.TargetColumn.Should().Be("tgt_id");
    }

    // ────────────────────────────────────────────
    //  MergeWhenMatched construction
    // ────────────────────────────────────────────

    [Fact]
    public void MergeWhenMatched_Update_SetsAction()
    {
        var setClauses = new List<SqlSetClause>
        {
            new("name", new SqlLiteralExpression(SqlLiteral.String("Updated")))
        };

        var wm = MergeWhenMatched.Update(setClauses);
        wm.Action.Should().Be(PPDS.Query.Planning.Nodes.MergeAction.Update);
        wm.SetClauses.Should().HaveCount(1);
    }

    [Fact]
    public void MergeWhenMatched_Delete_SetsAction()
    {
        var wm = MergeWhenMatched.Delete();
        wm.Action.Should().Be(PPDS.Query.Planning.Nodes.MergeAction.Delete);
        wm.SetClauses.Should().BeNull();
    }

    // ────────────────────────────────────────────
    //  MergeWhenNotMatched construction
    // ────────────────────────────────────────────

    [Fact]
    public void MergeWhenNotMatched_Insert_SetsAction()
    {
        var wnm = MergeWhenNotMatched.Insert(
            new List<string> { "name" },
            new List<ISqlExpression> { new SqlLiteralExpression(SqlLiteral.String("test")) });

        wnm.Action.Should().Be(PPDS.Query.Planning.Nodes.MergeAction.Insert);
        wnm.Columns.Should().HaveCount(1);
        wnm.Values.Should().HaveCount(1);
    }

    // ────────────────────────────────────────────
    //  ExecutionPlanBuilder: MERGE plan detection
    // ────────────────────────────────────────────

    [Fact]
    public void Plan_MergeStatement_ProducesMergeNode()
    {
        var parser = new QueryParser();
        var mockFetchXmlService = new Mock<IFetchXmlGeneratorService>();
        mockFetchXmlService
            .Setup(s => s.Generate(It.IsAny<TSqlFragment>()))
            .Returns(TranspileResult.Simple(
                "<fetch><entity name=\"account\"><all-attributes /></entity></fetch>"));

        var builder = new ExecutionPlanBuilder(mockFetchXmlService.Object);

        var sql = @"
            MERGE INTO account AS target
            USING source_table AS src
            ON target.accountid = src.id
            WHEN MATCHED THEN
                UPDATE SET name = src.name
            WHEN NOT MATCHED THEN
                INSERT (name) VALUES (src.name);";

        var fragment = parser.Parse(sql);
        var result = builder.Plan(fragment);

        result.RootNode.Should().BeOfType<MergeNode>();
        result.EntityLogicalName.Should().Be("account");
    }
}

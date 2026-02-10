using FluentAssertions;
using Microsoft.SqlServer.TransactSql.ScriptDom;
using Moq;
using PPDS.Dataverse.Query;
using PPDS.Dataverse.Query.Planning;
using PPDS.Dataverse.Sql.Transpilation;
using PPDS.Query.Parsing;
using PPDS.Query.Planning;
using PPDS.Query.Planning.Nodes;
using Xunit;

namespace PPDS.Query.Tests.Planning;

[Trait("Category", "Unit")]
public class StringSplitNodeTests
{
    // ────────────────────────────────────────────
    //  Constructor validation
    // ────────────────────────────────────────────

    [Fact]
    public void Constructor_NullInputString_ThrowsArgumentNullException()
    {
        var act = () => new StringSplitNode(null!, ",");
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Constructor_NullSeparator_ThrowsArgumentNullException()
    {
        var act = () => new StringSplitNode("a,b", null!);
        act.Should().Throw<ArgumentNullException>();
    }

    // ────────────────────────────────────────────
    //  Description and metadata
    // ────────────────────────────────────────────

    [Fact]
    public void Description_ContainsFunctionName()
    {
        var node = new StringSplitNode("red,green,blue", ",");
        node.Description.Should().Contain("StringSplit");
    }

    [Fact]
    public void Children_ReturnsEmpty()
    {
        var node = new StringSplitNode("a,b", ",");
        node.Children.Should().BeEmpty();
    }

    // ────────────────────────────────────────────
    //  Execution: basic comma-separated split
    // ────────────────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_CommaSeparated_SplitsCorrectly()
    {
        var node = new StringSplitNode("red,green,blue", ",");
        var rows = await TestHelpers.CollectRowsAsync(node);

        rows.Should().HaveCount(3);
        rows[0].Values["value"].Value.Should().Be("red");
        rows[1].Values["value"].Value.Should().Be("green");
        rows[2].Values["value"].Value.Should().Be("blue");
    }

    // ────────────────────────────────────────────
    //  Execution: single element (no separator found)
    // ────────────────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_NoSeparatorFound_ReturnsSingleRow()
    {
        var node = new StringSplitNode("hello", ",");
        var rows = await TestHelpers.CollectRowsAsync(node);

        rows.Should().HaveCount(1);
        rows[0].Values["value"].Value.Should().Be("hello");
    }

    // ────────────────────────────────────────────
    //  Execution: empty string returns no rows
    // ────────────────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_EmptyString_ReturnsNoRows()
    {
        var node = new StringSplitNode("", ",");
        var rows = await TestHelpers.CollectRowsAsync(node);

        rows.Should().BeEmpty();
    }

    // ────────────────────────────────────────────
    //  Execution: multi-character separator
    // ────────────────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_MultiCharSeparator_SplitsCorrectly()
    {
        var node = new StringSplitNode("a||b||c", "||");
        var rows = await TestHelpers.CollectRowsAsync(node);

        rows.Should().HaveCount(3);
        rows[0].Values["value"].Value.Should().Be("a");
        rows[1].Values["value"].Value.Should().Be("b");
        rows[2].Values["value"].Value.Should().Be("c");
    }

    // ────────────────────────────────────────────
    //  Execution: trailing separator produces empty value
    // ────────────────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_TrailingSeparator_ProducesEmptyValue()
    {
        var node = new StringSplitNode("a,b,", ",");
        var rows = await TestHelpers.CollectRowsAsync(node);

        rows.Should().HaveCount(3);
        rows[0].Values["value"].Value.Should().Be("a");
        rows[1].Values["value"].Value.Should().Be("b");
        rows[2].Values["value"].Value.Should().Be("");
    }

    // ────────────────────────────────────────────
    //  Execution: enable_ordinal includes ordinal column
    // ────────────────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_EnableOrdinal_IncludesOrdinalColumn()
    {
        var node = new StringSplitNode("x,y,z", ",", enableOrdinal: true);
        var rows = await TestHelpers.CollectRowsAsync(node);

        rows.Should().HaveCount(3);
        rows[0].Values["ordinal"].Value.Should().Be(1);
        rows[1].Values["ordinal"].Value.Should().Be(2);
        rows[2].Values["ordinal"].Value.Should().Be(3);
    }

    // ────────────────────────────────────────────
    //  Execution: spaces in values are preserved
    // ────────────────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_SpacesInValues_ArePreserved()
    {
        var node = new StringSplitNode("hello world, foo bar, baz", ",");
        var rows = await TestHelpers.CollectRowsAsync(node);

        rows.Should().HaveCount(3);
        rows[0].Values["value"].Value.Should().Be("hello world");
        rows[1].Values["value"].Value.Should().Be(" foo bar");
        rows[2].Values["value"].Value.Should().Be(" baz");
    }

    // ────────────────────────────────────────────
    //  ExecutionPlanBuilder: STRING_SPLIT plan detection
    // ────────────────────────────────────────────

    [Fact]
    public void Plan_StringSplit_ProducesStringSplitNode()
    {
        var parser = new QueryParser();
        var mockFetchXmlService = new Mock<IFetchXmlGeneratorService>();
        mockFetchXmlService
            .Setup(s => s.Generate(It.IsAny<TSqlFragment>()))
            .Returns(TranspileResult.Simple(
                "<fetch><entity name=\"account\"><all-attributes /></entity></fetch>"));

        var builder = new ExecutionPlanBuilder(mockFetchXmlService.Object);

        var sql = "SELECT value FROM STRING_SPLIT('red,green,blue', ',')";

        var fragment = parser.Parse(sql);
        var result = builder.Plan(fragment);

        result.RootNode.Should().BeOfType<StringSplitNode>();
        result.EntityLogicalName.Should().Be("string_split");
    }
}

using FluentAssertions;
using PPDS.Dataverse.Query;
using PPDS.Dataverse.Query.Planning;
using PPDS.Query.Planning.Nodes;
using Xunit;

namespace PPDS.Query.Tests.Planning;

[Trait("Category", "Unit")]
public class OffsetFetchNodeTests
{
    // ────────────────────────────────────────────
    //  Constructor validation
    // ────────────────────────────────────────────

    [Fact]
    public void Constructor_NullInput_ThrowsArgumentNullException()
    {
        var act = () => new OffsetFetchNode(null!, 0, 5);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Constructor_NegativeOffset_ThrowsArgumentOutOfRangeException()
    {
        var source = TestSourceNode.Create("test");
        var act = () => new OffsetFetchNode(source, -1, 5);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    // ────────────────────────────────────────────
    //  Description and metadata
    // ────────────────────────────────────────────

    [Fact]
    public void Description_IncludesOffsetAndFetch()
    {
        var source = TestSourceNode.Create("test");
        var node = new OffsetFetchNode(source, 10, 5);
        node.Description.Should().Contain("OFFSET 10").And.Contain("FETCH 5");
    }

    [Fact]
    public void Children_ReturnsSingleInput()
    {
        var source = TestSourceNode.Create("test");
        var node = new OffsetFetchNode(source, 0, 5);
        node.Children.Should().HaveCount(1);
    }

    // ────────────────────────────────────────────
    //  Execution: OFFSET only
    // ────────────────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_OffsetOnly_SkipsRows()
    {
        var source = TestSourceNode.Create("account",
            TestSourceNode.MakeRow("account", ("name", "A")),
            TestSourceNode.MakeRow("account", ("name", "B")),
            TestSourceNode.MakeRow("account", ("name", "C")),
            TestSourceNode.MakeRow("account", ("name", "D")),
            TestSourceNode.MakeRow("account", ("name", "E")));

        var node = new OffsetFetchNode(source, 2);
        var rows = await TestHelpers.CollectRowsAsync(node);

        rows.Should().HaveCount(3);
        rows[0].Values["name"].Value.Should().Be("C");
        rows[1].Values["name"].Value.Should().Be("D");
        rows[2].Values["name"].Value.Should().Be("E");
    }

    // ────────────────────────────────────────────
    //  Execution: OFFSET + FETCH
    // ────────────────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_OffsetAndFetch_SkipsAndLimits()
    {
        var source = TestSourceNode.Create("account",
            TestSourceNode.MakeRow("account", ("name", "A")),
            TestSourceNode.MakeRow("account", ("name", "B")),
            TestSourceNode.MakeRow("account", ("name", "C")),
            TestSourceNode.MakeRow("account", ("name", "D")),
            TestSourceNode.MakeRow("account", ("name", "E")));

        var node = new OffsetFetchNode(source, 1, 2);
        var rows = await TestHelpers.CollectRowsAsync(node);

        rows.Should().HaveCount(2);
        rows[0].Values["name"].Value.Should().Be("B");
        rows[1].Values["name"].Value.Should().Be("C");
    }

    // ────────────────────────────────────────────
    //  Execution: OFFSET beyond row count
    // ────────────────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_OffsetBeyondRowCount_ReturnsEmpty()
    {
        var source = TestSourceNode.Create("account",
            TestSourceNode.MakeRow("account", ("name", "A")),
            TestSourceNode.MakeRow("account", ("name", "B")));

        var node = new OffsetFetchNode(source, 10, 5);
        var rows = await TestHelpers.CollectRowsAsync(node);

        rows.Should().BeEmpty();
    }

    // ────────────────────────────────────────────
    //  Execution: Zero offset returns all
    // ────────────────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_ZeroOffset_NoFetch_ReturnsAll()
    {
        var source = TestSourceNode.Create("account",
            TestSourceNode.MakeRow("account", ("name", "A")),
            TestSourceNode.MakeRow("account", ("name", "B")));

        var node = new OffsetFetchNode(source, 0);
        var rows = await TestHelpers.CollectRowsAsync(node);

        rows.Should().HaveCount(2);
    }

    // ────────────────────────────────────────────
    //  Execution: Fetch zero returns empty
    // ────────────────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_FetchZero_ReturnsEmpty()
    {
        var source = TestSourceNode.Create("account",
            TestSourceNode.MakeRow("account", ("name", "A")),
            TestSourceNode.MakeRow("account", ("name", "B")));

        var node = new OffsetFetchNode(source, 0, 0);
        var rows = await TestHelpers.CollectRowsAsync(node);

        rows.Should().BeEmpty();
    }

    // ────────────────────────────────────────────
    //  EstimatedRows
    // ────────────────────────────────────────────

    [Fact]
    public void EstimatedRows_CalculatesCorrectly()
    {
        var source = TestSourceNode.Create("account",
            TestSourceNode.MakeRow("account", ("name", "A")),
            TestSourceNode.MakeRow("account", ("name", "B")),
            TestSourceNode.MakeRow("account", ("name", "C")),
            TestSourceNode.MakeRow("account", ("name", "D")),
            TestSourceNode.MakeRow("account", ("name", "E")));

        var node = new OffsetFetchNode(source, 2, 2);
        node.EstimatedRows.Should().Be(2);
    }
}

using FluentAssertions;
using PPDS.Dataverse.Query;
using PPDS.Dataverse.Query.Planning;
using PPDS.Query.Planning.Nodes;
using Xunit;

namespace PPDS.Query.Tests.Planning;

[Trait("Category", "Unit")]
public class CteScanNodeTests
{
    // ────────────────────────────────────────────
    //  Constructor validation
    // ────────────────────────────────────────────

    [Fact]
    public void Constructor_NullName_ThrowsArgumentNullException()
    {
        var act = () => new CteScanNode(null!, new List<QueryRow>());
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Constructor_NullRows_ThrowsArgumentNullException()
    {
        var act = () => new CteScanNode("cte1", null!);
        act.Should().Throw<ArgumentNullException>();
    }

    // ────────────────────────────────────────────
    //  Description and metadata
    // ────────────────────────────────────────────

    [Fact]
    public void Description_IncludesCteNameAndRowCount()
    {
        var rows = new List<QueryRow>
        {
            TestSourceNode.MakeRow("account", ("name", "A")),
            TestSourceNode.MakeRow("account", ("name", "B"))
        };

        var node = new CteScanNode("my_cte", rows);

        node.Description.Should().Contain("my_cte");
        node.Description.Should().Contain("2 rows");
    }

    [Fact]
    public void CteName_IsAccessible()
    {
        var node = new CteScanNode("employees", new List<QueryRow>());
        node.CteName.Should().Be("employees");
    }

    [Fact]
    public void Children_IsEmpty()
    {
        var node = new CteScanNode("cte", new List<QueryRow>());
        node.Children.Should().BeEmpty();
    }

    [Fact]
    public void EstimatedRows_ReturnsRowCount()
    {
        var rows = new List<QueryRow>
        {
            TestSourceNode.MakeRow("account", ("name", "A")),
            TestSourceNode.MakeRow("account", ("name", "B")),
            TestSourceNode.MakeRow("account", ("name", "C"))
        };

        var node = new CteScanNode("cte", rows);
        node.EstimatedRows.Should().Be(3);
    }

    // ────────────────────────────────────────────
    //  Execution
    // ────────────────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_ReturnsAllMaterializedRows()
    {
        var rows = new List<QueryRow>
        {
            TestSourceNode.MakeRow("account", ("name", "A"), ("revenue", 100)),
            TestSourceNode.MakeRow("account", ("name", "B"), ("revenue", 200)),
            TestSourceNode.MakeRow("account", ("name", "C"), ("revenue", 300))
        };

        var node = new CteScanNode("accounts_cte", rows);
        var result = await TestHelpers.CollectRowsAsync(node);

        result.Should().HaveCount(3);
        result[0].Values["name"].Value.Should().Be("A");
        result[1].Values["name"].Value.Should().Be("B");
        result[2].Values["name"].Value.Should().Be("C");
    }

    [Fact]
    public async Task ExecuteAsync_EmptyRows_ReturnsEmpty()
    {
        var node = new CteScanNode("empty_cte", new List<QueryRow>());
        var result = await TestHelpers.CollectRowsAsync(node);

        result.Should().BeEmpty();
    }

    [Fact]
    public async Task ExecuteAsync_CanBeCalledMultipleTimes()
    {
        var rows = new List<QueryRow>
        {
            TestSourceNode.MakeRow("account", ("name", "A"))
        };

        var node = new CteScanNode("cte", rows);

        var result1 = await TestHelpers.CollectRowsAsync(node);
        var result2 = await TestHelpers.CollectRowsAsync(node);

        result1.Should().HaveCount(1);
        result2.Should().HaveCount(1);
    }
}

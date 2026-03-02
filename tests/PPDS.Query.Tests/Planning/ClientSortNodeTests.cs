using FluentAssertions;
using PPDS.Dataverse.Query;
using PPDS.Dataverse.Query.Execution;
using PPDS.Dataverse.Query.Planning;
using PPDS.Dataverse.Query.Planning.Nodes;
using PPDS.Query.Planning.Nodes;
using Xunit;

namespace PPDS.Query.Tests.Planning;

[Trait("Category", "Unit")]
public class ClientSortNodeTests
{
    // Dummy scalar expression — ClientSortNode uses ColumnName for lookup, not Value.
    private static readonly CompiledScalarExpression s_noop = _ => null;

    [Fact]
    public async Task ExecuteAsync_SortsAscending()
    {
        var source = TestSourceNode.Create("t",
            TestSourceNode.MakeRow("t", ("name", "Charlie")),
            TestSourceNode.MakeRow("t", ("name", "Alice")),
            TestSourceNode.MakeRow("t", ("name", "Bob")));

        var sort = new ClientSortNode(source, new[]
        {
            new CompiledOrderByItem("name", s_noop, false)
        });

        var rows = await TestHelpers.CollectRowsAsync(sort);

        rows.Should().HaveCount(3);
        rows[0].Values["name"].Value.Should().Be("Alice");
        rows[1].Values["name"].Value.Should().Be("Bob");
        rows[2].Values["name"].Value.Should().Be("Charlie");
    }

    [Fact]
    public async Task ExecuteAsync_SortsDescending()
    {
        var source = TestSourceNode.Create("t",
            TestSourceNode.MakeRow("t", ("val", 1)),
            TestSourceNode.MakeRow("t", ("val", 3)),
            TestSourceNode.MakeRow("t", ("val", 2)));

        var sort = new ClientSortNode(source, new[]
        {
            new CompiledOrderByItem("val", s_noop, true)
        });

        var rows = await TestHelpers.CollectRowsAsync(sort);

        rows.Should().HaveCount(3);
        rows[0].Values["val"].Value.Should().Be(3);
        rows[1].Values["val"].Value.Should().Be(2);
        rows[2].Values["val"].Value.Should().Be(1);
    }

    [Fact]
    public async Task ExecuteAsync_EmptyInput_ReturnsEmpty()
    {
        var source = TestSourceNode.Create("t");

        var sort = new ClientSortNode(source, new[]
        {
            new CompiledOrderByItem("name", s_noop, false)
        });

        var rows = await TestHelpers.CollectRowsAsync(sort);
        rows.Should().BeEmpty();
    }

    [Fact]
    public async Task ExecuteAsync_SingleRow_ReturnsSameRow()
    {
        var source = TestSourceNode.Create("t",
            TestSourceNode.MakeRow("t", ("name", "Alice")));

        var sort = new ClientSortNode(source, new[]
        {
            new CompiledOrderByItem("name", s_noop, false)
        });

        var rows = await TestHelpers.CollectRowsAsync(sort);
        rows.Should().ContainSingle();
        rows[0].Values["name"].Value.Should().Be("Alice");
    }
}

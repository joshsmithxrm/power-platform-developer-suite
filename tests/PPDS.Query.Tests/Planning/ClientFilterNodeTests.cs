using FluentAssertions;
using PPDS.Dataverse.Query;
using PPDS.Dataverse.Query.Execution;
using PPDS.Dataverse.Query.Planning;
using PPDS.Dataverse.Query.Planning.Nodes;
using Xunit;

namespace PPDS.Query.Tests.Planning;

[Trait("Category", "Unit")]
public class ClientFilterNodeTests
{
    [Fact]
    public async Task ExecuteAsync_MatchingRows_ReturnsFiltered()
    {
        var source = TestSourceNode.Create("t",
            TestSourceNode.MakeRow("t", ("val", 1)),
            TestSourceNode.MakeRow("t", ("val", 2)),
            TestSourceNode.MakeRow("t", ("val", 3)));

        CompiledPredicate predicate = values =>
        {
            var v = values.TryGetValue("val", out var qv) ? qv.Value : null;
            return v is int i && i >= 2;
        };

        var filter = new ClientFilterNode(source, predicate, "val >= 2");

        var rows = await TestHelpers.CollectRowsAsync(filter);

        rows.Should().HaveCount(2);
        rows[0].Values["val"].Value.Should().Be(2);
        rows[1].Values["val"].Value.Should().Be(3);
    }

    [Fact]
    public async Task ExecuteAsync_NoMatch_ReturnsEmpty()
    {
        var source = TestSourceNode.Create("t",
            TestSourceNode.MakeRow("t", ("val", 1)),
            TestSourceNode.MakeRow("t", ("val", 2)));

        CompiledPredicate predicate = _ => false;

        var filter = new ClientFilterNode(source, predicate, "false");

        var rows = await TestHelpers.CollectRowsAsync(filter);
        rows.Should().BeEmpty();
    }

    [Fact]
    public async Task ExecuteAsync_AllMatch_ReturnsAll()
    {
        var source = TestSourceNode.Create("t",
            TestSourceNode.MakeRow("t", ("val", 1)),
            TestSourceNode.MakeRow("t", ("val", 2)));

        CompiledPredicate predicate = _ => true;

        var filter = new ClientFilterNode(source, predicate, "true");

        var rows = await TestHelpers.CollectRowsAsync(filter);
        rows.Should().HaveCount(2);
    }
}

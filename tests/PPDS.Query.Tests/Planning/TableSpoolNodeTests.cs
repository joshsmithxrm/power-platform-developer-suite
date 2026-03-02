using FluentAssertions;
using PPDS.Query.Planning.Nodes;
using Xunit;

namespace PPDS.Query.Tests.Planning;

[Trait("Category", "Unit")]
public class TableSpoolNodeTests
{
    [Fact]
    public async Task ExecuteAsync_MaterializesAndYieldsAllRows()
    {
        var source = TestSourceNode.Create("account",
            TestSourceNode.MakeRow("account", ("name", "Contoso")),
            TestSourceNode.MakeRow("account", ("name", "Fabrikam")));

        var spool = new TableSpoolNode(source);
        var rows = await TestHelpers.CollectRowsAsync(spool);

        rows.Should().HaveCount(2);
        rows[0].Values["name"].Value.Should().Be("Contoso");
        rows[1].Values["name"].Value.Should().Be("Fabrikam");
    }

    [Fact]
    public async Task ExecuteAsync_CanBeReadMultipleTimes()
    {
        var source = TestSourceNode.Create("account",
            TestSourceNode.MakeRow("account", ("name", "Contoso")));

        var spool = new TableSpoolNode(source);
        var context = TestHelpers.CreateTestContext();

        var rows1 = await TestHelpers.CollectRowsAsync(spool, context);
        var rows2 = await TestHelpers.CollectRowsAsync(spool, context);

        rows1.Should().HaveCount(1);
        rows2.Should().HaveCount(1);
    }

    [Fact]
    public async Task ExecuteAsync_EmptySource_YieldsNoRows()
    {
        var source = TestSourceNode.Create("account");
        var spool = new TableSpoolNode(source);
        var rows = await TestHelpers.CollectRowsAsync(spool);

        rows.Should().BeEmpty();
    }

    [Fact]
    public async Task MaterializedRows_AvailableAfterExecution()
    {
        var source = TestSourceNode.Create("account",
            TestSourceNode.MakeRow("account", ("id", 1)),
            TestSourceNode.MakeRow("account", ("id", 2)));

        var spool = new TableSpoolNode(source);
        await TestHelpers.CollectRowsAsync(spool);

        spool.MaterializedRows.Should().HaveCount(2);
    }
}

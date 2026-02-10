using FluentAssertions;
using PPDS.Dataverse.Query;
using PPDS.Dataverse.Query.Planning;
using PPDS.Query.Execution;
using PPDS.Query.Planning.Nodes;
using Xunit;

namespace PPDS.Query.Tests.Planning;

[Trait("Category", "Unit")]
public class TempTableScanNodeTests
{
    // ────────────────────────────────────────────
    //  Constructor validation
    // ────────────────────────────────────────────

    [Fact]
    public void Constructor_NullTableName_ThrowsArgumentException()
    {
        var ctx = new SessionContext();
        var act = () => new TempTableScanNode(null!, ctx);
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Constructor_NameWithoutHash_ThrowsArgumentException()
    {
        var ctx = new SessionContext();
        var act = () => new TempTableScanNode("temp", ctx);
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Constructor_NullSessionContext_ThrowsArgumentNullException()
    {
        var act = () => new TempTableScanNode("#temp", null!);
        act.Should().Throw<ArgumentNullException>();
    }

    // ────────────────────────────────────────────
    //  Description and metadata
    // ────────────────────────────────────────────

    [Fact]
    public void Description_ContainsTableName()
    {
        var ctx = new SessionContext();
        ctx.CreateTempTable("#results", new[] { "id" });
        var node = new TempTableScanNode("#results", ctx);

        node.Description.Should().Contain("#results");
    }

    [Fact]
    public void Children_IsEmpty()
    {
        var ctx = new SessionContext();
        ctx.CreateTempTable("#temp", new[] { "id" });
        var node = new TempTableScanNode("#temp", ctx);
        node.Children.Should().BeEmpty();
    }

    // ────────────────────────────────────────────
    //  Execution
    // ────────────────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_ReturnsAllRows()
    {
        var ctx = new SessionContext();
        ctx.CreateTempTable("#temp", new[] { "name", "value" });

        ctx.InsertIntoTempTable("#temp",
            TestSourceNode.MakeRow("temp", ("name", "X"), ("value", 10)));
        ctx.InsertIntoTempTable("#temp",
            TestSourceNode.MakeRow("temp", ("name", "Y"), ("value", 20)));

        var node = new TempTableScanNode("#temp", ctx);
        var rows = await TestHelpers.CollectRowsAsync(node);

        rows.Should().HaveCount(2);
        rows[0].Values["name"].Value.Should().Be("X");
        rows[1].Values["name"].Value.Should().Be("Y");
    }

    [Fact]
    public async Task ExecuteAsync_EmptyTable_ReturnsEmpty()
    {
        var ctx = new SessionContext();
        ctx.CreateTempTable("#temp", new[] { "id" });

        var node = new TempTableScanNode("#temp", ctx);
        var rows = await TestHelpers.CollectRowsAsync(node);

        rows.Should().BeEmpty();
    }

    [Fact]
    public async Task ExecuteAsync_NonexistentTable_Throws()
    {
        var ctx = new SessionContext();
        var node = new TempTableScanNode("#missing", ctx);

        var act = async () => await TestHelpers.CollectRowsAsync(node);
        await act.Should().ThrowAsync<InvalidOperationException>();
    }
}

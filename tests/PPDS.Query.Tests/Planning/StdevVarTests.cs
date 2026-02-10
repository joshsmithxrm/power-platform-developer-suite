using FluentAssertions;
using PPDS.Dataverse.Query;
using PPDS.Dataverse.Query.Planning;
using PPDS.Dataverse.Query.Planning.Nodes;
using Xunit;

namespace PPDS.Query.Tests.Planning;

[Trait("Category", "Unit")]
public class StdevVarTests
{
    // ════════════════════════════════════════════
    //  ClientAggregateNode: STDEV
    // ════════════════════════════════════════════

    [Fact]
    public async Task ClientAggregateNode_Stdev_ComputesSampleStdDev()
    {
        // Values: 2, 4, 4, 4, 5, 5, 7, 9
        // Mean = 5, Variance = 4.571..., StdDev = 2.138...
        var source = TestSourceNode.Create("account",
            TestSourceNode.MakeRow("account", ("revenue", 2m)),
            TestSourceNode.MakeRow("account", ("revenue", 4m)),
            TestSourceNode.MakeRow("account", ("revenue", 4m)),
            TestSourceNode.MakeRow("account", ("revenue", 4m)),
            TestSourceNode.MakeRow("account", ("revenue", 5m)),
            TestSourceNode.MakeRow("account", ("revenue", 5m)),
            TestSourceNode.MakeRow("account", ("revenue", 7m)),
            TestSourceNode.MakeRow("account", ("revenue", 9m)));

        var aggCols = new List<ClientAggregateColumn>
        {
            new("revenue", "stdev_revenue", ClientAggregateFunction.Stdev)
        };

        var node = new ClientAggregateNode(source, aggCols);
        var rows = await TestHelpers.CollectRowsAsync(node);

        rows.Should().HaveCount(1);
        var stdev = (decimal)rows[0].Values["stdev_revenue"].Value!;
        // Sample stdev of [2,4,4,4,5,5,7,9]: sqrt(32/7) ~ 2.138
        stdev.Should().BeApproximately(2.138m, 0.01m);
    }

    [Fact]
    public async Task ClientAggregateNode_Stdev_SingleValue_ReturnsZero()
    {
        var source = TestSourceNode.Create("account",
            TestSourceNode.MakeRow("account", ("revenue", 42m)));

        var aggCols = new List<ClientAggregateColumn>
        {
            new("revenue", "stdev_revenue", ClientAggregateFunction.Stdev)
        };

        var node = new ClientAggregateNode(source, aggCols);
        var rows = await TestHelpers.CollectRowsAsync(node);

        rows.Should().HaveCount(1);
        rows[0].Values["stdev_revenue"].Value.Should().Be(0m);
    }

    [Fact]
    public async Task ClientAggregateNode_Stdev_Empty_ReturnsNull()
    {
        var source = TestSourceNode.Create("account");

        var aggCols = new List<ClientAggregateColumn>
        {
            new("revenue", "stdev_revenue", ClientAggregateFunction.Stdev)
        };

        var node = new ClientAggregateNode(source, aggCols);
        var rows = await TestHelpers.CollectRowsAsync(node);

        // No rows = no groups = no output
        rows.Should().BeEmpty();
    }

    // ════════════════════════════════════════════
    //  ClientAggregateNode: VAR
    // ════════════════════════════════════════════

    [Fact]
    public async Task ClientAggregateNode_Var_ComputesSampleVariance()
    {
        // Values: 2, 4, 4, 4, 5, 5, 7, 9
        // Mean = 5, Variance = sum((x - 5)^2) / (8-1) = 32/7 ~ 4.571
        var source = TestSourceNode.Create("account",
            TestSourceNode.MakeRow("account", ("revenue", 2m)),
            TestSourceNode.MakeRow("account", ("revenue", 4m)),
            TestSourceNode.MakeRow("account", ("revenue", 4m)),
            TestSourceNode.MakeRow("account", ("revenue", 4m)),
            TestSourceNode.MakeRow("account", ("revenue", 5m)),
            TestSourceNode.MakeRow("account", ("revenue", 5m)),
            TestSourceNode.MakeRow("account", ("revenue", 7m)),
            TestSourceNode.MakeRow("account", ("revenue", 9m)));

        var aggCols = new List<ClientAggregateColumn>
        {
            new("revenue", "var_revenue", ClientAggregateFunction.Var)
        };

        var node = new ClientAggregateNode(source, aggCols);
        var rows = await TestHelpers.CollectRowsAsync(node);

        rows.Should().HaveCount(1);
        var variance = (decimal)rows[0].Values["var_revenue"].Value!;
        // Sample variance of [2,4,4,4,5,5,7,9] = 32/7 ~ 4.571
        variance.Should().BeApproximately(4.571m, 0.01m);
    }

    [Fact]
    public async Task ClientAggregateNode_Var_SingleValue_ReturnsZero()
    {
        var source = TestSourceNode.Create("account",
            TestSourceNode.MakeRow("account", ("revenue", 42m)));

        var aggCols = new List<ClientAggregateColumn>
        {
            new("revenue", "var_revenue", ClientAggregateFunction.Var)
        };

        var node = new ClientAggregateNode(source, aggCols);
        var rows = await TestHelpers.CollectRowsAsync(node);

        rows.Should().HaveCount(1);
        rows[0].Values["var_revenue"].Value.Should().Be(0m);
    }

    // ════════════════════════════════════════════
    //  ClientAggregateNode: with GROUP BY
    // ════════════════════════════════════════════

    [Fact]
    public async Task ClientAggregateNode_Stdev_WithGroupBy_ComputesPerGroup()
    {
        var source = TestSourceNode.Create("account",
            TestSourceNode.MakeRow("account", ("city", "A"), ("revenue", 10m)),
            TestSourceNode.MakeRow("account", ("city", "A"), ("revenue", 20m)),
            TestSourceNode.MakeRow("account", ("city", "A"), ("revenue", 30m)),
            TestSourceNode.MakeRow("account", ("city", "B"), ("revenue", 100m)),
            TestSourceNode.MakeRow("account", ("city", "B"), ("revenue", 200m)));

        var aggCols = new List<ClientAggregateColumn>
        {
            new("revenue", "stdev_revenue", ClientAggregateFunction.Stdev)
        };

        var node = new ClientAggregateNode(source, aggCols,
            groupByColumns: new List<string> { "city" });

        var rows = await TestHelpers.CollectRowsAsync(node);

        rows.Should().HaveCount(2);

        // Find group A and B
        var groupA = rows.First(r =>
            r.Values.TryGetValue("city", out var v) && (string?)v.Value == "A");
        var groupB = rows.First(r =>
            r.Values.TryGetValue("city", out var v) && (string?)v.Value == "B");

        // Group A: [10, 20, 30], mean=20, var=100, stdev=10
        var stdevA = (decimal)groupA.Values["stdev_revenue"].Value!;
        stdevA.Should().Be(10m);

        // Group B: [100, 200], mean=150, var=5000, stdev~70.71
        var stdevB = (decimal)groupB.Values["stdev_revenue"].Value!;
        stdevB.Should().BeApproximately(70.71m, 0.01m);
    }

    // ════════════════════════════════════════════
    //  MergeAggregateNode: STDEV and VAR
    //  (Verify the existing MergeAggregateNode handles these)
    // ════════════════════════════════════════════

    [Fact]
    public async Task MergeAggregateNode_Stdev_ComputesFromRawValues()
    {
        // Simulate individual value rows fed through the merge node
        // Values: 10, 20, 30
        var source = TestSourceNode.Create("account",
            TestSourceNode.MakeRow("account", ("stdev_rev", 10m)),
            TestSourceNode.MakeRow("account", ("stdev_rev", 20m)),
            TestSourceNode.MakeRow("account", ("stdev_rev", 30m)));

        var aggCols = new List<MergeAggregateColumn>
        {
            new("stdev_rev", AggregateFunction.Stdev)
        };

        var node = new MergeAggregateNode(source, aggCols);
        var rows = await TestHelpers.CollectRowsAsync(node);

        rows.Should().HaveCount(1);
        var stdev = (decimal)rows[0].Values["stdev_rev"].Value!;
        // Sample stdev of [10, 20, 30]: sqrt(200/2) = 10
        stdev.Should().Be(10m);
    }

    [Fact]
    public async Task MergeAggregateNode_Var_ComputesFromRawValues()
    {
        // Values: 10, 20, 30
        var source = TestSourceNode.Create("account",
            TestSourceNode.MakeRow("account", ("var_rev", 10m)),
            TestSourceNode.MakeRow("account", ("var_rev", 20m)),
            TestSourceNode.MakeRow("account", ("var_rev", 30m)));

        var aggCols = new List<MergeAggregateColumn>
        {
            new("var_rev", AggregateFunction.Var)
        };

        var node = new MergeAggregateNode(source, aggCols);
        var rows = await TestHelpers.CollectRowsAsync(node);

        rows.Should().HaveCount(1);
        var variance = (decimal)rows[0].Values["var_rev"].Value!;
        // Sample variance of [10, 20, 30]: (100+0+100)/2 = 100
        variance.Should().Be(100m);
    }

    [Fact]
    public async Task MergeAggregateNode_Stdev_SingleValue_ReturnsZero()
    {
        var source = TestSourceNode.Create("account",
            TestSourceNode.MakeRow("account", ("stdev_col", 42m)));

        var aggCols = new List<MergeAggregateColumn>
        {
            new("stdev_col", AggregateFunction.Stdev)
        };

        var node = new MergeAggregateNode(source, aggCols);
        var rows = await TestHelpers.CollectRowsAsync(node);

        rows.Should().HaveCount(1);
        rows[0].Values["stdev_col"].Value.Should().Be(0m);
    }

    [Fact]
    public async Task MergeAggregateNode_Var_NoValues_ReturnsNull()
    {
        var source = TestSourceNode.Create("account",
            TestSourceNode.MakeRow("account", ("other_col", "text")));

        var aggCols = new List<MergeAggregateColumn>
        {
            new("var_col", AggregateFunction.Var)
        };

        var node = new MergeAggregateNode(source, aggCols);
        var rows = await TestHelpers.CollectRowsAsync(node);

        rows.Should().HaveCount(1);
        // No values for var_col means count=0, n<2 returns null
        rows[0].Values["var_col"].Value.Should().BeNull();
    }

    // ════════════════════════════════════════════
    //  ClientAggregateColumn construction
    // ════════════════════════════════════════════

    [Fact]
    public void ClientAggregateColumn_SetsProperties()
    {
        var col = new ClientAggregateColumn("revenue", "stdev_rev", ClientAggregateFunction.Stdev);
        col.SourceColumn.Should().Be("revenue");
        col.OutputAlias.Should().Be("stdev_rev");
        col.Function.Should().Be(ClientAggregateFunction.Stdev);
    }

    // ════════════════════════════════════════════
    //  MapToMergeFunction includes STDEV/VAR
    // ════════════════════════════════════════════

    [Fact]
    public void AggregateFunction_Enum_HasStdevAndVar()
    {
        // Verify the enum values exist
        AggregateFunction.Stdev.Should().BeDefined();
        AggregateFunction.Var.Should().BeDefined();
    }
}

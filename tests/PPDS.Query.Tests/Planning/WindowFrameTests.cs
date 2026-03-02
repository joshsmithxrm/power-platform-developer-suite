using System.Collections.Generic;
using FluentAssertions;
using PPDS.Dataverse.Query;
using PPDS.Dataverse.Query.Execution;
using PPDS.Dataverse.Query.Planning;
using PPDS.Dataverse.Query.Planning.Nodes;
using PPDS.Query.Planning.Nodes;
using Xunit;

namespace PPDS.Query.Tests.Planning;

[Trait("Category", "Unit")]
public class WindowFrameTests
{
    private readonly QueryPlanContext _context;

    public WindowFrameTests()
    {
        var mockExecutor = new Moq.Mock<IQueryExecutor>();

        _context = new QueryPlanContext(mockExecutor.Object);
    }

    // ────────────────────────────────────────────
    //  LAG function
    // ────────────────────────────────────────────

    [Fact]
    public async Task Lag_ReturnsValueFromPreviousRow()
    {
        var source = CreateNumericSource();
        var lagDef = CreateExtendedWindow("lag_val", "LAG", "value", offset: 1, defaultValue: -1);

        var node = new WindowSpoolNode(source, new[] { lagDef });
        var rows = await TestHelpers.CollectRowsAsync(node, _context);

        rows.Should().HaveCount(5);
        rows[0].Values["lag_val"].Value.Should().Be(-1);   // No previous row -> default
        rows[1].Values["lag_val"].Value.Should().Be(10);    // Previous is 10
        rows[2].Values["lag_val"].Value.Should().Be(20);    // Previous is 20
        rows[3].Values["lag_val"].Value.Should().Be(30);    // Previous is 30
        rows[4].Values["lag_val"].Value.Should().Be(40);    // Previous is 40
    }

    [Fact]
    public async Task Lag_Offset2_ReturnsValueFrom2RowsBack()
    {
        var source = CreateNumericSource();
        var lagDef = CreateExtendedWindow("lag_val", "LAG", "value", offset: 2, defaultValue: 0);

        var node = new WindowSpoolNode(source, new[] { lagDef });
        var rows = await TestHelpers.CollectRowsAsync(node, _context);

        rows[0].Values["lag_val"].Value.Should().Be(0);     // No row -2 -> default
        rows[1].Values["lag_val"].Value.Should().Be(0);     // No row -2 -> default
        rows[2].Values["lag_val"].Value.Should().Be(10);    // Row 0 value
    }

    // ────────────────────────────────────────────
    //  LEAD function
    // ────────────────────────────────────────────

    [Fact]
    public async Task Lead_ReturnsValueFromNextRow()
    {
        var source = CreateNumericSource();
        var leadDef = CreateExtendedWindow("lead_val", "LEAD", "value", offset: 1, defaultValue: -1);

        var node = new WindowSpoolNode(source, new[] { leadDef });
        var rows = await TestHelpers.CollectRowsAsync(node, _context);

        rows.Should().HaveCount(5);
        rows[0].Values["lead_val"].Value.Should().Be(20);   // Next is 20
        rows[1].Values["lead_val"].Value.Should().Be(30);   // Next is 30
        rows[2].Values["lead_val"].Value.Should().Be(40);   // Next is 40
        rows[3].Values["lead_val"].Value.Should().Be(50);   // Next is 50
        rows[4].Values["lead_val"].Value.Should().Be(-1);   // No next row -> default
    }

    // ────────────────────────────────────────────
    //  NTILE function
    // ────────────────────────────────────────────

    [Fact]
    public async Task Ntile_DistributesRowsIntoGroups()
    {
        var source = CreateNumericSource(); // 5 rows
        var ntileDef = CreateNtileWindow("ntile_val", 3);

        var node = new WindowSpoolNode(source, new[] { ntileDef });
        var rows = await TestHelpers.CollectRowsAsync(node, _context);

        rows.Should().HaveCount(5);
        // 5 rows into 3 groups: groups of 2, 2, 1
        rows[0].Values["ntile_val"].Value.Should().Be(1);
        rows[1].Values["ntile_val"].Value.Should().Be(1);
        rows[2].Values["ntile_val"].Value.Should().Be(2);
        rows[3].Values["ntile_val"].Value.Should().Be(2);
        rows[4].Values["ntile_val"].Value.Should().Be(3);
    }

    // ────────────────────────────────────────────
    //  FIRST_VALUE function
    // ────────────────────────────────────────────

    [Fact]
    public async Task FirstValue_ReturnsFirstInPartition()
    {
        var source = CreateNumericSource();
        var firstDef = CreateExtendedWindow("first_val", "FIRST_VALUE", "value",
            frame: WindowFrameSpec.FullPartition);

        var node = new WindowSpoolNode(source, new[] { firstDef });
        var rows = await TestHelpers.CollectRowsAsync(node, _context);

        // All rows should have the first value (10)
        foreach (var row in rows)
        {
            row.Values["first_val"].Value.Should().Be(10);
        }
    }

    // ────────────────────────────────────────────
    //  LAST_VALUE function
    // ────────────────────────────────────────────

    [Fact]
    public async Task LastValue_WithFullPartition_ReturnsLastInPartition()
    {
        var source = CreateNumericSource();
        var lastDef = CreateExtendedWindow("last_val", "LAST_VALUE", "value",
            frame: WindowFrameSpec.FullPartition);

        var node = new WindowSpoolNode(source, new[] { lastDef });
        var rows = await TestHelpers.CollectRowsAsync(node, _context);

        // All rows should have the last value (50) with full partition frame
        foreach (var row in rows)
        {
            row.Values["last_val"].Value.Should().Be(50);
        }
    }

    // ────────────────────────────────────────────
    //  Running SUM (UNBOUNDED PRECEDING to CURRENT ROW)
    // ────────────────────────────────────────────

    [Fact]
    public async Task RunningSum_ComputesCumulativeTotal()
    {
        var source = CreateNumericSource();
        var sumDef = CreateExtendedWindow("running_sum", "SUM", "value",
            frame: WindowFrameSpec.RunningTotal);

        var node = new WindowSpoolNode(source, new[] { sumDef });
        var rows = await TestHelpers.CollectRowsAsync(node, _context);

        rows[0].Values["running_sum"].Value.Should().Be(10m);    // 10
        rows[1].Values["running_sum"].Value.Should().Be(30m);    // 10 + 20
        rows[2].Values["running_sum"].Value.Should().Be(60m);    // 10 + 20 + 30
        rows[3].Values["running_sum"].Value.Should().Be(100m);   // 10 + 20 + 30 + 40
        rows[4].Values["running_sum"].Value.Should().Be(150m);   // 10 + 20 + 30 + 40 + 50
    }

    // ────────────────────────────────────────────
    //  Sliding window SUM (N PRECEDING to N FOLLOWING)
    // ────────────────────────────────────────────

    [Fact]
    public async Task SlidingSum_ComputesWindowedTotal()
    {
        var source = CreateNumericSource();
        var sumDef = CreateExtendedWindow("sliding_sum", "SUM", "value",
            frame: WindowFrameSpec.Sliding(1, 1));

        var node = new WindowSpoolNode(source, new[] { sumDef });
        var rows = await TestHelpers.CollectRowsAsync(node, _context);

        rows[0].Values["sliding_sum"].Value.Should().Be(30m);    // 10 + 20 (no preceding)
        rows[1].Values["sliding_sum"].Value.Should().Be(60m);    // 10 + 20 + 30
        rows[2].Values["sliding_sum"].Value.Should().Be(90m);    // 20 + 30 + 40
        rows[3].Values["sliding_sum"].Value.Should().Be(120m);   // 30 + 40 + 50
        rows[4].Values["sliding_sum"].Value.Should().Be(90m);    // 40 + 50 (no following)
    }

    // ────────────────────────────────────────────
    //  Frame resolution
    // ────────────────────────────────────────────

    [Fact]
    public void ResolveFrame_FullPartition_ReturnsFullRange()
    {
        var frame = WindowFrameSpec.FullPartition;
        var (start, end) = WindowSpoolNode.ResolveFrame(frame, 3, 10);
        start.Should().Be(0);
        end.Should().Be(9);
    }

    [Fact]
    public void ResolveFrame_RunningTotal_ReturnsZeroToCurrentRow()
    {
        var frame = WindowFrameSpec.RunningTotal;
        var (start, end) = WindowSpoolNode.ResolveFrame(frame, 5, 10);
        start.Should().Be(0);
        end.Should().Be(5);
    }

    [Fact]
    public void ResolveFrame_Sliding_ReturnsCorrectRange()
    {
        var frame = WindowFrameSpec.Sliding(2, 3);
        var (start, end) = WindowSpoolNode.ResolveFrame(frame, 5, 10);
        start.Should().Be(3);  // 5 - 2
        end.Should().Be(8);    // 5 + 3
    }

    [Fact]
    public void ResolveFrame_Sliding_ClampsToPartitionBounds()
    {
        var frame = WindowFrameSpec.Sliding(10, 10);
        var (start, end) = WindowSpoolNode.ResolveFrame(frame, 2, 5);
        start.Should().Be(0);  // Clamped to 0
        end.Should().Be(4);    // Clamped to 4
    }

    [Fact]
    public void ResolveFrame_Null_DefaultsToRunningTotal()
    {
        var (start, end) = WindowSpoolNode.ResolveFrame(null, 3, 10);
        start.Should().Be(0);
        end.Should().Be(3);
    }

    // ────────────────────────────────────────────
    //  Empty input
    // ────────────────────────────────────────────

    [Fact]
    public async Task EmptyInput_ReturnsEmpty()
    {
        var source = TestSourceNode.Create("test");
        var lagDef = CreateExtendedWindow("lag_val", "LAG", "value");

        var node = new WindowSpoolNode(source, new[] { lagDef });
        var rows = await TestHelpers.CollectRowsAsync(node, _context);

        rows.Should().BeEmpty();
    }

    // ────────────────────────────────────────────
    //  ROW_NUMBER via WindowSpoolNode
    // ────────────────────────────────────────────

    [Fact]
    public async Task RowNumber_AssignsSequentialNumbers()
    {
        var source = CreateNumericSource();
        var rnDef = CreateExtendedWindow("rn", "ROW_NUMBER", null);

        var node = new WindowSpoolNode(source, new[] { rnDef });
        var rows = await TestHelpers.CollectRowsAsync(node, _context);

        for (var i = 0; i < rows.Count; i++)
        {
            rows[i].Values["rn"].Value.Should().Be(i + 1);
        }
    }

    // ════════════════════════════════════════════
    //  Helpers
    // ════════════════════════════════════════════

    private static TestSourceNode CreateNumericSource()
    {
        return TestSourceNode.Create("test",
            TestSourceNode.MakeRow("test", ("id", 1), ("value", 10)),
            TestSourceNode.MakeRow("test", ("id", 2), ("value", 20)),
            TestSourceNode.MakeRow("test", ("id", 3), ("value", 30)),
            TestSourceNode.MakeRow("test", ("id", 4), ("value", 40)),
            TestSourceNode.MakeRow("test", ("id", 5), ("value", 50)));
    }

    private static ExtendedWindowDefinition CreateExtendedWindow(
        string outputName, string functionName, string? operandColumn,
        int offset = 1, object? defaultValue = null, WindowFrameSpec? frame = null)
    {
        CompiledScalarExpression? operand = operandColumn != null
            ? (row => row.TryGetValue(operandColumn, out var qv) ? qv.Value : null)
            : null;

        // Compiled order-by on "id" ascending
        CompiledScalarExpression idExpr = row =>
            row.TryGetValue("id", out var qv) ? qv.Value : null;
        var orderBy = new[] { new CompiledOrderByItem("id", idExpr, false) };

        return new ExtendedWindowDefinition(
            outputName, functionName, operand,
            partitionBy: null,
            orderBy: orderBy,
            frame: frame,
            offset: offset,
            defaultValue: defaultValue);
    }

    private static ExtendedWindowDefinition CreateNtileWindow(string outputName, int groups)
    {
        // Compiled order-by on "id" ascending
        CompiledScalarExpression idExpr = row =>
            row.TryGetValue("id", out var qv) ? qv.Value : null;
        var orderBy = new[] { new CompiledOrderByItem("id", idExpr, false) };

        return new ExtendedWindowDefinition(
            outputName, "NTILE", operand: null,
            partitionBy: null,
            orderBy: orderBy,
            nTileGroups: groups);
    }
}

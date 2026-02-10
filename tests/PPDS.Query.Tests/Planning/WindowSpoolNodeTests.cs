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
public class WindowSpoolNodeTests
{
    [Fact]
    public async Task Rank_WithTies_AssignsRankWithGaps()
    {
        // Input: scores 100, 90, 90, 80 → RANK should be 1, 2, 2, 4
        var rows = TestSourceNode.Create("test",
            TestSourceNode.MakeRow("test", ("name", "A"), ("score", 100)),
            TestSourceNode.MakeRow("test", ("name", "B"), ("score", 90)),
            TestSourceNode.MakeRow("test", ("name", "C"), ("score", 90)),
            TestSourceNode.MakeRow("test", ("name", "D"), ("score", 80)));

        var orderBy = new List<CompiledOrderByItem>
        {
            new("score", r => r.TryGetValue("score", out var v) ? v.Value : null, true) // DESC
        };

        var windowDef = new ExtendedWindowDefinition("rnk", "RANK", null, null, orderBy);
        var node = new WindowSpoolNode(rows, new List<ExtendedWindowDefinition> { windowDef });

        var result = await TestHelpers.CollectRowsAsync(node);

        result.Should().HaveCount(4);
        result[0].Values["rnk"].Value.Should().Be(1);
        result[1].Values["rnk"].Value.Should().Be(2);
        result[2].Values["rnk"].Value.Should().Be(2);
        result[3].Values["rnk"].Value.Should().Be(4);
    }

    [Fact]
    public async Task DenseRank_WithTies_AssignsRankWithoutGaps()
    {
        // Input: scores 100, 90, 90, 80 → DENSE_RANK should be 1, 2, 2, 3
        var rows = TestSourceNode.Create("test",
            TestSourceNode.MakeRow("test", ("name", "A"), ("score", 100)),
            TestSourceNode.MakeRow("test", ("name", "B"), ("score", 90)),
            TestSourceNode.MakeRow("test", ("name", "C"), ("score", 90)),
            TestSourceNode.MakeRow("test", ("name", "D"), ("score", 80)));

        var orderBy = new List<CompiledOrderByItem>
        {
            new("score", r => r.TryGetValue("score", out var v) ? v.Value : null, true)
        };

        var windowDef = new ExtendedWindowDefinition("drnk", "DENSE_RANK", null, null, orderBy);
        var node = new WindowSpoolNode(rows, new List<ExtendedWindowDefinition> { windowDef });

        var result = await TestHelpers.CollectRowsAsync(node);

        result.Should().HaveCount(4);
        result[0].Values["drnk"].Value.Should().Be(1);
        result[1].Values["drnk"].Value.Should().Be(2);
        result[2].Values["drnk"].Value.Should().Be(2);
        result[3].Values["drnk"].Value.Should().Be(3);
    }

    [Fact]
    public async Task Rank_NoTies_SequentialNumbers()
    {
        // Input order: v=3, v=1, v=2. ORDER BY v ASC sorts to 1, 2, 3.
        // Output preserves original row order, so:
        //   row 0 (v=3) → rank 3, row 1 (v=1) → rank 1, row 2 (v=2) → rank 2
        var rows = TestSourceNode.Create("test",
            TestSourceNode.MakeRow("test", ("v", 3)),
            TestSourceNode.MakeRow("test", ("v", 1)),
            TestSourceNode.MakeRow("test", ("v", 2)));

        var orderBy = new List<CompiledOrderByItem>
        {
            new("v", r => r.TryGetValue("v", out var qv) ? qv.Value : null, false)
        };

        var windowDef = new ExtendedWindowDefinition("r", "RANK", null, null, orderBy);
        var node = new WindowSpoolNode(rows, new List<ExtendedWindowDefinition> { windowDef });

        var result = await TestHelpers.CollectRowsAsync(node);

        result.Should().HaveCount(3);
        result[0].Values["r"].Value.Should().Be(3); // v=3 is rank 3 in ASC order
        result[1].Values["r"].Value.Should().Be(1); // v=1 is rank 1 in ASC order
        result[2].Values["r"].Value.Should().Be(2); // v=2 is rank 2 in ASC order
    }
}

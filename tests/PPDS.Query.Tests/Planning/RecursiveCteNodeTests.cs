using FluentAssertions;
using PPDS.Dataverse.Query;
using PPDS.Dataverse.Query.Planning;
using PPDS.Query.Planning.Nodes;
using Xunit;

namespace PPDS.Query.Tests.Planning;

[Trait("Category", "Unit")]
public class RecursiveCteNodeTests
{
    // ────────────────────────────────────────────
    //  Constructor validation
    // ────────────────────────────────────────────

    [Fact]
    public void Constructor_NullName_ThrowsArgumentNullException()
    {
        var anchor = TestSourceNode.Create("test");
        var act = () => new RecursiveCteNode(null!, anchor, _ => TestSourceNode.Create("test"));
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Constructor_NullAnchor_ThrowsArgumentNullException()
    {
        var act = () => new RecursiveCteNode("cte", null!, _ => TestSourceNode.Create("test"));
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Constructor_NullFactory_ThrowsArgumentNullException()
    {
        var anchor = TestSourceNode.Create("test");
        var act = () => new RecursiveCteNode("cte", anchor, null!);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Constructor_NegativeMaxRecursion_ThrowsArgumentOutOfRangeException()
    {
        var anchor = TestSourceNode.Create("test");
        var act = () => new RecursiveCteNode("cte", anchor, _ => TestSourceNode.Create("test"), -1);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    // ────────────────────────────────────────────
    //  Description and metadata
    // ────────────────────────────────────────────

    [Fact]
    public void Description_IncludesCteNameAndMaxRecursion()
    {
        var anchor = TestSourceNode.Create("test");
        var node = new RecursiveCteNode("hierarchy", anchor, _ => TestSourceNode.Create("test"), 50);
        node.Description.Should().Contain("hierarchy").And.Contain("50");
    }

    [Fact]
    public void EstimatedRows_IsUnknown()
    {
        var anchor = TestSourceNode.Create("test");
        var node = new RecursiveCteNode("cte", anchor, _ => TestSourceNode.Create("test"));
        node.EstimatedRows.Should().Be(-1);
    }

    // ────────────────────────────────────────────
    //  Execution: Anchor only (no recursive rows)
    // ────────────────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_NoRecursiveRows_ReturnsAnchorOnly()
    {
        var anchor = TestSourceNode.Create("account",
            TestSourceNode.MakeRow("account", ("name", "Root")));

        // Recursive factory returns empty - no recursion
        var node = new RecursiveCteNode("cte", anchor, _ => TestSourceNode.Create("account"));

        var rows = await TestHelpers.CollectRowsAsync(node);

        rows.Should().HaveCount(1);
        rows[0].Values["name"].Value.Should().Be("Root");
    }

    // ────────────────────────────────────────────
    //  Execution: Simple recursion with depth
    // ────────────────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_SimpleRecursion_ProducesMultipleLevels()
    {
        // Simulate a counter that goes 1 -> 2 -> 3 -> stops
        var anchor = TestSourceNode.Create("nums",
            TestSourceNode.MakeRow("nums", ("n", 1)));

        var node = new RecursiveCteNode("counter", anchor, previousRows =>
        {
            var newRows = new List<QueryRow>();
            foreach (var row in previousRows)
            {
                var n = (int)row.Values["n"].Value!;
                if (n < 3)
                {
                    newRows.Add(TestSourceNode.MakeRow("nums", ("n", n + 1)));
                }
            }
            return TestSourceNode.Create("nums", newRows.ToArray());
        });

        var rows = await TestHelpers.CollectRowsAsync(node);

        rows.Should().HaveCount(3);
        rows.Select(r => (int)r.Values["n"].Value!).Should().BeEquivalentTo(new[] { 1, 2, 3 });
    }

    // ────────────────────────────────────────────
    //  Execution: Max recursion limit
    // ────────────────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_ExceedsMaxRecursion_ThrowsInvalidOperationException()
    {
        var anchor = TestSourceNode.Create("nums",
            TestSourceNode.MakeRow("nums", ("n", 1)));

        // Always returns a row (infinite recursion)
        var node = new RecursiveCteNode("infinite", anchor, previousRows =>
        {
            return TestSourceNode.Create("nums",
                TestSourceNode.MakeRow("nums", ("n", 999)));
        }, maxRecursion: 5);

        var act = async () => await TestHelpers.CollectRowsAsync(node);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*maximum recursion*5*");
    }

    // ────────────────────────────────────────────
    //  Execution: Tree-like recursion (branching)
    // ────────────────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_TreeRecursion_ProducesCorrectResults()
    {
        // Simulate: Root -> (Child1, Child2) -> stop
        var anchor = TestSourceNode.Create("tree",
            TestSourceNode.MakeRow("tree", ("id", "root"), ("level", 0)));

        var iterationCount = 0;
        var node = new RecursiveCteNode("tree_cte", anchor, previousRows =>
        {
            iterationCount++;
            if (iterationCount > 1) // Only one level of children
                return TestSourceNode.Create("tree");

            var newRows = new List<QueryRow>();
            foreach (var row in previousRows)
            {
                var parentId = (string)row.Values["id"].Value!;
                newRows.Add(TestSourceNode.MakeRow("tree",
                    ("id", parentId + "_child1"), ("level", 1)));
                newRows.Add(TestSourceNode.MakeRow("tree",
                    ("id", parentId + "_child2"), ("level", 1)));
            }
            return TestSourceNode.Create("tree", newRows.ToArray());
        });

        var rows = await TestHelpers.CollectRowsAsync(node);

        // Root + 2 children = 3 rows
        rows.Should().HaveCount(3);
    }
}

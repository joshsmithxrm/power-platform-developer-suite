using FluentAssertions;
using PPDS.Dataverse.Query;
using PPDS.Dataverse.Query.Planning;
using PPDS.Query.Planning.Nodes;
using Xunit;

namespace PPDS.Query.Tests.Planning;

[Trait("Category", "Unit")]
public class IntersectExceptNodeTests
{
    // ════════════════════════════════════════════
    //  INTERSECT tests
    // ════════════════════════════════════════════

    [Fact]
    public void IntersectNode_NullLeft_ThrowsArgumentNullException()
    {
        var right = TestSourceNode.Create("test");
        var act = () => new IntersectNode(null!, right);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void IntersectNode_NullRight_ThrowsArgumentNullException()
    {
        var left = TestSourceNode.Create("test");
        var act = () => new IntersectNode(left, null!);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void IntersectNode_Description_IsIntersect()
    {
        var left = TestSourceNode.Create("test");
        var right = TestSourceNode.Create("test");
        var node = new IntersectNode(left, right);
        node.Description.Should().Be("Intersect");
    }

    [Fact]
    public void IntersectNode_Children_ReturnsBothInputs()
    {
        var left = TestSourceNode.Create("test");
        var right = TestSourceNode.Create("test");
        var node = new IntersectNode(left, right);
        node.Children.Should().HaveCount(2);
    }

    [Fact]
    public async Task IntersectNode_ReturnsOnlyCommonRows()
    {
        var left = TestSourceNode.Create("account",
            TestSourceNode.MakeRow("account", ("name", "A")),
            TestSourceNode.MakeRow("account", ("name", "B")),
            TestSourceNode.MakeRow("account", ("name", "C")));

        var right = TestSourceNode.Create("account",
            TestSourceNode.MakeRow("account", ("name", "B")),
            TestSourceNode.MakeRow("account", ("name", "C")),
            TestSourceNode.MakeRow("account", ("name", "D")));

        var node = new IntersectNode(left, right);
        var rows = await TestHelpers.CollectRowsAsync(node);

        rows.Should().HaveCount(2);
        rows.Select(r => r.Values["name"].Value).Should().BeEquivalentTo(new[] { "B", "C" });
    }

    [Fact]
    public async Task IntersectNode_NoOverlap_ReturnsEmpty()
    {
        var left = TestSourceNode.Create("account",
            TestSourceNode.MakeRow("account", ("name", "A")),
            TestSourceNode.MakeRow("account", ("name", "B")));

        var right = TestSourceNode.Create("account",
            TestSourceNode.MakeRow("account", ("name", "C")),
            TestSourceNode.MakeRow("account", ("name", "D")));

        var node = new IntersectNode(left, right);
        var rows = await TestHelpers.CollectRowsAsync(node);

        rows.Should().BeEmpty();
    }

    [Fact]
    public async Task IntersectNode_DeduplicatesOutput()
    {
        // Left has duplicates of "A", right also has "A"
        var left = TestSourceNode.Create("account",
            TestSourceNode.MakeRow("account", ("name", "A")),
            TestSourceNode.MakeRow("account", ("name", "A")),
            TestSourceNode.MakeRow("account", ("name", "B")));

        var right = TestSourceNode.Create("account",
            TestSourceNode.MakeRow("account", ("name", "A")),
            TestSourceNode.MakeRow("account", ("name", "B")));

        var node = new IntersectNode(left, right);
        var rows = await TestHelpers.CollectRowsAsync(node);

        // Should get A and B, each once (set semantics)
        rows.Should().HaveCount(2);
    }

    [Fact]
    public async Task IntersectNode_MultipleColumns_MatchesAllColumns()
    {
        var left = TestSourceNode.Create("account",
            TestSourceNode.MakeRow("account", ("name", "A"), ("code", 1)),
            TestSourceNode.MakeRow("account", ("name", "A"), ("code", 2)));

        var right = TestSourceNode.Create("account",
            TestSourceNode.MakeRow("account", ("name", "A"), ("code", 2)),
            TestSourceNode.MakeRow("account", ("name", "B"), ("code", 1)));

        var node = new IntersectNode(left, right);
        var rows = await TestHelpers.CollectRowsAsync(node);

        // Only ("A", 2) is common
        rows.Should().HaveCount(1);
        rows[0].Values["name"].Value.Should().Be("A");
        rows[0].Values["code"].Value.Should().Be(2);
    }

    // ════════════════════════════════════════════
    //  EXCEPT tests
    // ════════════════════════════════════════════

    [Fact]
    public void ExceptNode_NullLeft_ThrowsArgumentNullException()
    {
        var right = TestSourceNode.Create("test");
        var act = () => new ExceptNode(null!, right);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void ExceptNode_NullRight_ThrowsArgumentNullException()
    {
        var left = TestSourceNode.Create("test");
        var act = () => new ExceptNode(left, null!);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void ExceptNode_Description_IsExcept()
    {
        var left = TestSourceNode.Create("test");
        var right = TestSourceNode.Create("test");
        var node = new ExceptNode(left, right);
        node.Description.Should().Be("Except");
    }

    [Fact]
    public async Task ExceptNode_ReturnsRowsOnlyInLeft()
    {
        var left = TestSourceNode.Create("account",
            TestSourceNode.MakeRow("account", ("name", "A")),
            TestSourceNode.MakeRow("account", ("name", "B")),
            TestSourceNode.MakeRow("account", ("name", "C")));

        var right = TestSourceNode.Create("account",
            TestSourceNode.MakeRow("account", ("name", "B")),
            TestSourceNode.MakeRow("account", ("name", "D")));

        var node = new ExceptNode(left, right);
        var rows = await TestHelpers.CollectRowsAsync(node);

        rows.Should().HaveCount(2);
        rows.Select(r => r.Values["name"].Value).Should().BeEquivalentTo(new[] { "A", "C" });
    }

    [Fact]
    public async Task ExceptNode_FullOverlap_ReturnsEmpty()
    {
        var left = TestSourceNode.Create("account",
            TestSourceNode.MakeRow("account", ("name", "A")),
            TestSourceNode.MakeRow("account", ("name", "B")));

        var right = TestSourceNode.Create("account",
            TestSourceNode.MakeRow("account", ("name", "A")),
            TestSourceNode.MakeRow("account", ("name", "B")));

        var node = new ExceptNode(left, right);
        var rows = await TestHelpers.CollectRowsAsync(node);

        rows.Should().BeEmpty();
    }

    [Fact]
    public async Task ExceptNode_NoOverlap_ReturnsAllLeft()
    {
        var left = TestSourceNode.Create("account",
            TestSourceNode.MakeRow("account", ("name", "A")),
            TestSourceNode.MakeRow("account", ("name", "B")));

        var right = TestSourceNode.Create("account",
            TestSourceNode.MakeRow("account", ("name", "C")),
            TestSourceNode.MakeRow("account", ("name", "D")));

        var node = new ExceptNode(left, right);
        var rows = await TestHelpers.CollectRowsAsync(node);

        rows.Should().HaveCount(2);
    }

    [Fact]
    public async Task ExceptNode_DeduplicatesOutput()
    {
        // Left has duplicates of "A"
        var left = TestSourceNode.Create("account",
            TestSourceNode.MakeRow("account", ("name", "A")),
            TestSourceNode.MakeRow("account", ("name", "A")),
            TestSourceNode.MakeRow("account", ("name", "B")));

        var right = TestSourceNode.Create("account",
            TestSourceNode.MakeRow("account", ("name", "C")));

        var node = new ExceptNode(left, right);
        var rows = await TestHelpers.CollectRowsAsync(node);

        // Set semantics: A appears once, B appears once
        rows.Should().HaveCount(2);
    }

    [Fact]
    public async Task ExceptNode_EmptyRight_ReturnsAllLeftDeduplicated()
    {
        var left = TestSourceNode.Create("account",
            TestSourceNode.MakeRow("account", ("name", "A")),
            TestSourceNode.MakeRow("account", ("name", "B")));

        var right = TestSourceNode.Create("account");

        var node = new ExceptNode(left, right);
        var rows = await TestHelpers.CollectRowsAsync(node);

        rows.Should().HaveCount(2);
    }
}

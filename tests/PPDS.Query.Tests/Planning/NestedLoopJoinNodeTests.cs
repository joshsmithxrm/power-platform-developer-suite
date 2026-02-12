using FluentAssertions;
using PPDS.Dataverse.Query;
using PPDS.Dataverse.Query.Planning;
using PPDS.Query.Planning;
using PPDS.Query.Planning.Nodes;
using Xunit;

namespace PPDS.Query.Tests.Planning;

[Trait("Category", "Unit")]
public class NestedLoopJoinNodeTests
{
    // ────────────────────────────────────────────
    //  INNER JOIN: matching rows only
    // ────────────────────────────────────────────

    [Fact]
    public async Task InnerJoin_ReturnsMatchingRows()
    {
        var left = TestSourceNode.Create("account",
            TestSourceNode.MakeRow("account", ("accountid", 1), ("name", "Contoso")),
            TestSourceNode.MakeRow("account", ("accountid", 2), ("name", "Fabrikam")),
            TestSourceNode.MakeRow("account", ("accountid", 3), ("name", "Northwind")));

        var right = TestSourceNode.Create("contact",
            TestSourceNode.MakeRow("contact", ("contactid", 10), ("parentaccountid", 1), ("fullname", "Alice")),
            TestSourceNode.MakeRow("contact", ("contactid", 11), ("parentaccountid", 2), ("fullname", "Bob")));

        var join = new NestedLoopJoinNode(left, right, "accountid", "parentaccountid", JoinType.Inner);

        var rows = await TestHelpers.CollectRowsAsync(join);

        rows.Should().HaveCount(2);
        rows[0].Values["name"].Value.Should().Be("Contoso");
        rows[0].Values["fullname"].Value.Should().Be("Alice");
        rows[1].Values["name"].Value.Should().Be("Fabrikam");
        rows[1].Values["fullname"].Value.Should().Be("Bob");
    }

    // ────────────────────────────────────────────
    //  LEFT JOIN: all left rows preserved
    // ────────────────────────────────────────────

    [Fact]
    public async Task LeftJoin_ReturnsAllLeftRows_WithNullsForUnmatched()
    {
        var left = TestSourceNode.Create("account",
            TestSourceNode.MakeRow("account", ("accountid", 1), ("name", "Contoso")),
            TestSourceNode.MakeRow("account", ("accountid", 2), ("name", "Fabrikam")),
            TestSourceNode.MakeRow("account", ("accountid", 3), ("name", "Northwind")));

        var right = TestSourceNode.Create("contact",
            TestSourceNode.MakeRow("contact", ("contactid", 10), ("parentaccountid", 1), ("fullname", "Alice")));

        var join = new NestedLoopJoinNode(left, right, "accountid", "parentaccountid", JoinType.Left);

        var rows = await TestHelpers.CollectRowsAsync(join);

        rows.Should().HaveCount(3);

        // Matched
        rows[0].Values["name"].Value.Should().Be("Contoso");
        rows[0].Values["fullname"].Value.Should().Be("Alice");

        // Unmatched
        rows[1].Values["name"].Value.Should().Be("Fabrikam");
        rows[1].Values["fullname"].Value.Should().BeNull();

        rows[2].Values["name"].Value.Should().Be("Northwind");
        rows[2].Values["fullname"].Value.Should().BeNull();
    }

    // ────────────────────────────────────────────
    //  No matches
    // ────────────────────────────────────────────

    [Fact]
    public async Task InnerJoin_NoMatches_ReturnsEmpty()
    {
        var left = TestSourceNode.Create("account",
            TestSourceNode.MakeRow("account", ("accountid", 1), ("name", "Contoso")));

        var right = TestSourceNode.Create("contact",
            TestSourceNode.MakeRow("contact", ("contactid", 10), ("parentaccountid", 99), ("fullname", "Nobody")));

        var join = new NestedLoopJoinNode(left, right, "accountid", "parentaccountid", JoinType.Inner);

        var rows = await TestHelpers.CollectRowsAsync(join);
        rows.Should().BeEmpty();
    }

    // ────────────────────────────────────────────
    //  Empty inputs
    // ────────────────────────────────────────────

    [Fact]
    public async Task InnerJoin_EmptyLeft_ReturnsEmpty()
    {
        var left = TestSourceNode.Create("account");
        var right = TestSourceNode.Create("contact",
            TestSourceNode.MakeRow("contact", ("contactid", 10), ("parentaccountid", 1)));

        var join = new NestedLoopJoinNode(left, right, "accountid", "parentaccountid", JoinType.Inner);

        var rows = await TestHelpers.CollectRowsAsync(join);
        rows.Should().BeEmpty();
    }

    [Fact]
    public async Task InnerJoin_EmptyRight_ReturnsEmpty()
    {
        var left = TestSourceNode.Create("account",
            TestSourceNode.MakeRow("account", ("accountid", 1), ("name", "Contoso")));
        var right = TestSourceNode.Create("contact");

        var join = new NestedLoopJoinNode(left, right, "accountid", "parentaccountid", JoinType.Inner);

        var rows = await TestHelpers.CollectRowsAsync(join);
        rows.Should().BeEmpty();
    }

    // ────────────────────────────────────────────
    //  Multiple matches per outer row
    // ────────────────────────────────────────────

    [Fact]
    public async Task InnerJoin_MultipleMatches_ProducesAllCombinations()
    {
        var left = TestSourceNode.Create("account",
            TestSourceNode.MakeRow("account", ("accountid", 1), ("name", "Contoso")));

        var right = TestSourceNode.Create("contact",
            TestSourceNode.MakeRow("contact", ("contactid", 10), ("parentaccountid", 1), ("fullname", "Alice")),
            TestSourceNode.MakeRow("contact", ("contactid", 11), ("parentaccountid", 1), ("fullname", "Bob")),
            TestSourceNode.MakeRow("contact", ("contactid", 12), ("parentaccountid", 1), ("fullname", "Charlie")));

        var join = new NestedLoopJoinNode(left, right, "accountid", "parentaccountid", JoinType.Inner);

        var rows = await TestHelpers.CollectRowsAsync(join);

        rows.Should().HaveCount(3);
        rows[0].Values["fullname"].Value.Should().Be("Alice");
        rows[1].Values["fullname"].Value.Should().Be("Bob");
        rows[2].Values["fullname"].Value.Should().Be("Charlie");
    }

    // ────────────────────────────────────────────
    //  Combined rows contain columns from both sides
    // ────────────────────────────────────────────

    [Fact]
    public async Task Join_CombinesColumnsFromBothSides()
    {
        var left = TestSourceNode.Create("account",
            TestSourceNode.MakeRow("account", ("accountid", 1), ("name", "Contoso")));

        var right = TestSourceNode.Create("contact",
            TestSourceNode.MakeRow("contact", ("contactid", 10), ("parentaccountid", 1), ("fullname", "Alice")));

        var join = new NestedLoopJoinNode(left, right, "accountid", "parentaccountid", JoinType.Inner);

        var rows = await TestHelpers.CollectRowsAsync(join);

        rows.Should().HaveCount(1);
        var row = rows[0];
        row.Values.Should().ContainKey("accountid");
        row.Values.Should().ContainKey("name");
        row.Values.Should().ContainKey("contactid");
        row.Values.Should().ContainKey("fullname");
        row.Values.Should().ContainKey("parentaccountid");
    }

    // ────────────────────────────────────────────
    //  Description and properties
    // ────────────────────────────────────────────

    [Fact]
    public void Description_ContainsJoinInfo()
    {
        var left = TestSourceNode.Create("account");
        var right = TestSourceNode.Create("contact");

        var join = new NestedLoopJoinNode(left, right, "accountid", "parentaccountid", JoinType.Inner);

        join.Description.Should().Contain("NestedLoopJoin");
        join.Description.Should().Contain("accountid");
    }

    [Fact]
    public void Constructor_NullLeft_ThrowsArgumentNullException()
    {
        var right = TestSourceNode.Create("contact");
        var act = () => new NestedLoopJoinNode(null!, right, "id", "fk");
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Constructor_NullKeyColumn_ThrowsArgumentNullException()
    {
        var left = TestSourceNode.Create("account");
        var right = TestSourceNode.Create("contact");
        var act = () => new NestedLoopJoinNode(left, right, null!, "fk");
        act.Should().Throw<ArgumentNullException>();
    }

    // ────────────────────────────────────────────
    //  CROSS JOIN: Cartesian product
    // ────────────────────────────────────────────

    [Fact]
    public async Task CrossJoin_ProducesCartesianProduct()
    {
        var left = TestSourceNode.Create("a",
            TestSourceNode.MakeRow("a", ("x", 1)),
            TestSourceNode.MakeRow("a", ("x", 2)));
        var right = TestSourceNode.Create("b",
            TestSourceNode.MakeRow("b", ("y", "A")),
            TestSourceNode.MakeRow("b", ("y", "B")),
            TestSourceNode.MakeRow("b", ("y", "C")));

        var join = new NestedLoopJoinNode(left, right, null, null, JoinType.Cross);
        var rows = await TestHelpers.CollectRowsAsync(join);

        rows.Should().HaveCount(6); // 2 x 3
        rows.Select(r => $"{r.Values["x"].Value}-{r.Values["y"].Value}")
            .Should().BeEquivalentTo("1-A", "1-B", "1-C", "2-A", "2-B", "2-C");
    }

    [Fact]
    public async Task CrossJoin_EmptyLeft_ReturnsEmpty()
    {
        var left = TestSourceNode.Create("a");
        var right = TestSourceNode.Create("b",
            TestSourceNode.MakeRow("b", ("y", 1)));

        var join = new NestedLoopJoinNode(left, right, null, null, JoinType.Cross);
        var rows = await TestHelpers.CollectRowsAsync(join);

        rows.Should().BeEmpty();
    }

    // ────────────────────────────────────────────
    //  RIGHT JOIN: all right rows preserved
    // ────────────────────────────────────────────

    [Fact]
    public async Task RightJoin_PreservesAllRightRows()
    {
        var left = TestSourceNode.Create("a",
            TestSourceNode.MakeRow("a", ("id", 1), ("name", "Contoso")));
        var right = TestSourceNode.Create("b",
            TestSourceNode.MakeRow("b", ("aid", 1), ("val", "X")),
            TestSourceNode.MakeRow("b", ("aid", 99), ("val", "Y")));

        var join = new NestedLoopJoinNode(left, right, "id", "aid", JoinType.Right);
        var rows = await TestHelpers.CollectRowsAsync(join);

        rows.Should().HaveCount(2);
        rows[0].Values["name"].Value.Should().Be("Contoso");
        rows[0].Values["val"].Value.Should().Be("X");
        rows[1].Values.TryGetValue("name", out var nameVal);
        (nameVal?.Value).Should().BeNull(); // Left side is null for unmatched right row
        rows[1].Values["val"].Value.Should().Be("Y");
    }

    [Fact]
    public async Task RightJoin_NoMatches_AllRightRowsWithNulls()
    {
        var left = TestSourceNode.Create("a",
            TestSourceNode.MakeRow("a", ("id", 1), ("name", "Contoso")));
        var right = TestSourceNode.Create("b",
            TestSourceNode.MakeRow("b", ("aid", 99), ("val", "X")));

        var join = new NestedLoopJoinNode(left, right, "id", "aid", JoinType.Right);
        var rows = await TestHelpers.CollectRowsAsync(join);

        rows.Should().HaveCount(1);
        rows[0].Values["val"].Value.Should().Be("X");
        rows[0].Values.TryGetValue("name", out var nameVal);
        (nameVal?.Value).Should().BeNull();
    }

    // ────────────────────────────────────────────
    //  FULL OUTER JOIN: all rows preserved
    // ────────────────────────────────────────────

    [Fact]
    public async Task FullOuterJoin_PreservesAllRows()
    {
        var left = TestSourceNode.Create("a",
            TestSourceNode.MakeRow("a", ("id", 1), ("name", "Contoso")),
            TestSourceNode.MakeRow("a", ("id", 2), ("name", "Fabrikam")));
        var right = TestSourceNode.Create("b",
            TestSourceNode.MakeRow("b", ("aid", 1), ("val", "X")),
            TestSourceNode.MakeRow("b", ("aid", 3), ("val", "Z")));

        var join = new NestedLoopJoinNode(left, right, "id", "aid", JoinType.FullOuter);
        var rows = await TestHelpers.CollectRowsAsync(join);

        rows.Should().HaveCount(3);

        // Row 1: id=1 matched
        var matched = rows.Single(r => "Contoso".Equals(r.Values["name"].Value));
        matched.Values["val"].Value.Should().Be("X");

        // Row 2: id=2 unmatched left (val is null)
        var unmatchedLeft = rows.Single(r => "Fabrikam".Equals(r.Values["name"].Value));
        unmatchedLeft.Values["val"].Value.Should().BeNull();

        // Row 3: aid=3 unmatched right (name is null)
        var unmatchedRight = rows.Single(r => "Z".Equals(r.Values["val"].Value));
        unmatchedRight.Values["name"].Value.Should().BeNull();
    }
}

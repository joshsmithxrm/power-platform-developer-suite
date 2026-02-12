using FluentAssertions;
using PPDS.Dataverse.Query;
using PPDS.Dataverse.Query.Planning;
using PPDS.Query.Planning;
using PPDS.Query.Planning.Nodes;
using Xunit;

namespace PPDS.Query.Tests.Planning;

[Trait("Category", "Unit")]
public class HashJoinNodeTests
{
    // ────────────────────────────────────────────
    //  INNER JOIN: matching rows only
    // ────────────────────────────────────────────

    [Fact]
    public async Task InnerJoin_ReturnsMatchingRowsOnly()
    {
        var left = TestSourceNode.Create("account",
            TestSourceNode.MakeRow("account", ("accountid", 1), ("name", "Contoso")),
            TestSourceNode.MakeRow("account", ("accountid", 2), ("name", "Fabrikam")),
            TestSourceNode.MakeRow("account", ("accountid", 3), ("name", "Northwind")));

        var right = TestSourceNode.Create("contact",
            TestSourceNode.MakeRow("contact", ("contactid", 10), ("parentaccountid", 1), ("fullname", "Alice")),
            TestSourceNode.MakeRow("contact", ("contactid", 11), ("parentaccountid", 1), ("fullname", "Bob")),
            TestSourceNode.MakeRow("contact", ("contactid", 12), ("parentaccountid", 2), ("fullname", "Charlie")));

        var join = new HashJoinNode(left, right, "accountid", "parentaccountid", JoinType.Inner);

        var rows = await TestHelpers.CollectRowsAsync(join);

        rows.Should().HaveCount(3);
        rows[0].Values["name"].Value.Should().Be("Contoso");
        rows[0].Values["fullname"].Value.Should().Be("Alice");
        rows[1].Values["name"].Value.Should().Be("Contoso");
        rows[1].Values["fullname"].Value.Should().Be("Bob");
        rows[2].Values["name"].Value.Should().Be("Fabrikam");
        rows[2].Values["fullname"].Value.Should().Be("Charlie");
    }

    [Fact]
    public async Task InnerJoin_NoMatches_ReturnsEmpty()
    {
        var left = TestSourceNode.Create("account",
            TestSourceNode.MakeRow("account", ("accountid", 1), ("name", "Contoso")));

        var right = TestSourceNode.Create("contact",
            TestSourceNode.MakeRow("contact", ("contactid", 10), ("parentaccountid", 99), ("fullname", "Alice")));

        var join = new HashJoinNode(left, right, "accountid", "parentaccountid", JoinType.Inner);

        var rows = await TestHelpers.CollectRowsAsync(join);

        rows.Should().BeEmpty();
    }

    // ────────────────────────────────────────────
    //  LEFT JOIN: all left rows, nulls for unmatched right
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

        var join = new HashJoinNode(left, right, "accountid", "parentaccountid", JoinType.Left);

        var rows = await TestHelpers.CollectRowsAsync(join);

        rows.Should().HaveCount(3);

        // First row: matched
        rows[0].Values["name"].Value.Should().Be("Contoso");
        rows[0].Values["fullname"].Value.Should().Be("Alice");

        // Second row: unmatched - should have null for right side
        rows[1].Values["name"].Value.Should().Be("Fabrikam");
        rows[1].Values["fullname"].Value.Should().BeNull();

        // Third row: unmatched
        rows[2].Values["name"].Value.Should().Be("Northwind");
        rows[2].Values["fullname"].Value.Should().BeNull();
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

        var join = new HashJoinNode(left, right, "accountid", "parentaccountid", JoinType.Inner);

        var rows = await TestHelpers.CollectRowsAsync(join);
        rows.Should().BeEmpty();
    }

    [Fact]
    public async Task InnerJoin_EmptyRight_ReturnsEmpty()
    {
        var left = TestSourceNode.Create("account",
            TestSourceNode.MakeRow("account", ("accountid", 1), ("name", "Contoso")));
        var right = TestSourceNode.Create("contact");

        var join = new HashJoinNode(left, right, "accountid", "parentaccountid", JoinType.Inner);

        var rows = await TestHelpers.CollectRowsAsync(join);
        rows.Should().BeEmpty();
    }

    // ────────────────────────────────────────────
    //  Duplicate keys on the build side
    // ────────────────────────────────────────────

    [Fact]
    public async Task InnerJoin_MultipleMatchesOnBuildSide_ProducesMultipleRows()
    {
        var left = TestSourceNode.Create("account",
            TestSourceNode.MakeRow("account", ("accountid", 1), ("name", "Contoso")));

        var right = TestSourceNode.Create("contact",
            TestSourceNode.MakeRow("contact", ("contactid", 10), ("parentaccountid", 1), ("fullname", "Alice")),
            TestSourceNode.MakeRow("contact", ("contactid", 11), ("parentaccountid", 1), ("fullname", "Bob")),
            TestSourceNode.MakeRow("contact", ("contactid", 12), ("parentaccountid", 1), ("fullname", "Charlie")));

        var join = new HashJoinNode(left, right, "accountid", "parentaccountid", JoinType.Inner);

        var rows = await TestHelpers.CollectRowsAsync(join);

        rows.Should().HaveCount(3);
    }

    // ────────────────────────────────────────────
    //  Description and properties
    // ────────────────────────────────────────────

    [Fact]
    public void Description_ContainsJoinInfo()
    {
        var left = TestSourceNode.Create("account");
        var right = TestSourceNode.Create("contact");

        var join = new HashJoinNode(left, right, "accountid", "parentaccountid", JoinType.Inner);

        join.Description.Should().Contain("HashJoin");
        join.Description.Should().Contain("accountid");
        join.Description.Should().Contain("parentaccountid");
    }

    [Fact]
    public void Children_ReturnsLeftAndRight()
    {
        var left = TestSourceNode.Create("account");
        var right = TestSourceNode.Create("contact");

        var join = new HashJoinNode(left, right, "accountid", "parentaccountid");

        join.Children.Should().HaveCount(2);
    }

    // ────────────────────────────────────────────
    //  Constructor validation
    // ────────────────────────────────────────────

    [Fact]
    public void Constructor_NullLeft_ThrowsArgumentNullException()
    {
        var right = TestSourceNode.Create("contact");
        var act = () => new HashJoinNode(null!, right, "id", "fk");
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Constructor_NullRight_ThrowsArgumentNullException()
    {
        var left = TestSourceNode.Create("account");
        var act = () => new HashJoinNode(left, null!, "id", "fk");
        act.Should().Throw<ArgumentNullException>();
    }

    // ────────────────────────────────────────────
    //  RIGHT JOIN: all right rows preserved
    // ────────────────────────────────────────────

    [Fact]
    public async Task RightJoin_PreservesAllBuildSideRows()
    {
        var left = TestSourceNode.Create("a",
            TestSourceNode.MakeRow("a", ("id", 1), ("name", "Contoso")));
        var right = TestSourceNode.Create("b",
            TestSourceNode.MakeRow("b", ("aid", 1), ("val", "X")),
            TestSourceNode.MakeRow("b", ("aid", 99), ("val", "Y")));

        var join = new HashJoinNode(left, right, "id", "aid", JoinType.Right);
        var rows = await TestHelpers.CollectRowsAsync(join);

        rows.Should().HaveCount(2);
        var matched = rows.Single(r => "X".Equals(r.Values["val"].Value));
        matched.Values["name"].Value.Should().Be("Contoso");
        var unmatched = rows.Single(r => "Y".Equals(r.Values["val"].Value));
        unmatched.Values["name"].Value.Should().BeNull();
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

        var join = new HashJoinNode(left, right, "id", "aid", JoinType.FullOuter);
        var rows = await TestHelpers.CollectRowsAsync(join);

        rows.Should().HaveCount(3);
        // Matched: Contoso + X
        var matched = rows.Single(r => "Contoso".Equals(r.Values["name"].Value));
        matched.Values["val"].Value.Should().Be("X");
        // Unmatched left: Fabrikam + null
        var unmatchedLeft = rows.Single(r => "Fabrikam".Equals(r.Values["name"].Value));
        unmatchedLeft.Values["val"].Value.Should().BeNull();
        // Unmatched right: null + Z
        var unmatchedRight = rows.Single(r => "Z".Equals(r.Values["val"].Value));
        unmatchedRight.Values["name"].Value.Should().BeNull();
    }
}

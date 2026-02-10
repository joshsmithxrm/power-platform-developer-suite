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
}

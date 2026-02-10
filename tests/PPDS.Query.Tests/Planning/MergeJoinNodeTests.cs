using FluentAssertions;
using PPDS.Dataverse.Query;
using PPDS.Dataverse.Query.Planning;
using PPDS.Query.Planning;
using PPDS.Query.Planning.Nodes;
using Xunit;

namespace PPDS.Query.Tests.Planning;

[Trait("Category", "Unit")]
public class MergeJoinNodeTests
{
    // ────────────────────────────────────────────
    //  INNER JOIN: sorted inputs, matching rows only
    // ────────────────────────────────────────────

    [Fact]
    public async Task InnerJoin_SortedInputs_ReturnsMatchingRows()
    {
        // Both inputs sorted by their join key
        var left = TestSourceNode.Create("account",
            TestSourceNode.MakeRow("account", ("accountid", 1), ("name", "Contoso")),
            TestSourceNode.MakeRow("account", ("accountid", 2), ("name", "Fabrikam")),
            TestSourceNode.MakeRow("account", ("accountid", 3), ("name", "Northwind")));

        var right = TestSourceNode.Create("contact",
            TestSourceNode.MakeRow("contact", ("contactid", 10), ("parentaccountid", 1), ("fullname", "Alice")),
            TestSourceNode.MakeRow("contact", ("contactid", 12), ("parentaccountid", 2), ("fullname", "Charlie")));

        var join = new MergeJoinNode(left, right, "accountid", "parentaccountid", JoinType.Inner);

        var rows = await TestHelpers.CollectRowsAsync(join);

        rows.Should().HaveCount(2);
        rows[0].Values["name"].Value.Should().Be("Contoso");
        rows[0].Values["fullname"].Value.Should().Be("Alice");
        rows[1].Values["name"].Value.Should().Be("Fabrikam");
        rows[1].Values["fullname"].Value.Should().Be("Charlie");
    }

    // ────────────────────────────────────────────
    //  LEFT JOIN: all left rows preserved
    // ────────────────────────────────────────────

    [Fact]
    public async Task LeftJoin_SortedInputs_PreservesAllLeftRows()
    {
        var left = TestSourceNode.Create("account",
            TestSourceNode.MakeRow("account", ("accountid", 1), ("name", "Contoso")),
            TestSourceNode.MakeRow("account", ("accountid", 2), ("name", "Fabrikam")),
            TestSourceNode.MakeRow("account", ("accountid", 3), ("name", "Northwind")));

        var right = TestSourceNode.Create("contact",
            TestSourceNode.MakeRow("contact", ("contactid", 10), ("parentaccountid", 1), ("fullname", "Alice")));

        var join = new MergeJoinNode(left, right, "accountid", "parentaccountid", JoinType.Left);

        var rows = await TestHelpers.CollectRowsAsync(join);

        rows.Should().HaveCount(3);
        rows[0].Values["name"].Value.Should().Be("Contoso");
        rows[0].Values["fullname"].Value.Should().Be("Alice");
        rows[1].Values["name"].Value.Should().Be("Fabrikam");
        rows[1].Values["fullname"].Value.Should().BeNull();
        rows[2].Values["name"].Value.Should().Be("Northwind");
        rows[2].Values["fullname"].Value.Should().BeNull();
    }

    // ────────────────────────────────────────────
    //  Duplicate join keys
    // ────────────────────────────────────────────

    [Fact]
    public async Task InnerJoin_DuplicateKeys_ProducesCrossProduct()
    {
        var left = TestSourceNode.Create("account",
            TestSourceNode.MakeRow("account", ("accountid", 1), ("name", "Contoso")),
            TestSourceNode.MakeRow("account", ("accountid", 1), ("name", "Contoso-2")));

        var right = TestSourceNode.Create("contact",
            TestSourceNode.MakeRow("contact", ("contactid", 10), ("parentaccountid", 1), ("fullname", "Alice")),
            TestSourceNode.MakeRow("contact", ("contactid", 11), ("parentaccountid", 1), ("fullname", "Bob")));

        var join = new MergeJoinNode(left, right, "accountid", "parentaccountid", JoinType.Inner);

        var rows = await TestHelpers.CollectRowsAsync(join);

        // 2 left rows * 2 right rows = 4 result rows
        rows.Should().HaveCount(4);
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

        var join = new MergeJoinNode(left, right, "accountid", "parentaccountid", JoinType.Inner);

        var rows = await TestHelpers.CollectRowsAsync(join);
        rows.Should().BeEmpty();
    }

    [Fact]
    public async Task InnerJoin_EmptyRight_ReturnsEmpty()
    {
        var left = TestSourceNode.Create("account",
            TestSourceNode.MakeRow("account", ("accountid", 1), ("name", "Contoso")));
        var right = TestSourceNode.Create("contact");

        var join = new MergeJoinNode(left, right, "accountid", "parentaccountid", JoinType.Inner);

        var rows = await TestHelpers.CollectRowsAsync(join);
        rows.Should().BeEmpty();
    }

    // ────────────────────────────────────────────
    //  No matches at all
    // ────────────────────────────────────────────

    [Fact]
    public async Task InnerJoin_NoMatches_ReturnsEmpty()
    {
        var left = TestSourceNode.Create("account",
            TestSourceNode.MakeRow("account", ("accountid", 1), ("name", "Contoso")),
            TestSourceNode.MakeRow("account", ("accountid", 2), ("name", "Fabrikam")));

        var right = TestSourceNode.Create("contact",
            TestSourceNode.MakeRow("contact", ("contactid", 10), ("parentaccountid", 99)));

        var join = new MergeJoinNode(left, right, "accountid", "parentaccountid", JoinType.Inner);

        var rows = await TestHelpers.CollectRowsAsync(join);
        rows.Should().BeEmpty();
    }

    // ────────────────────────────────────────────
    //  Description and properties
    // ────────────────────────────────────────────

    [Fact]
    public void Description_ContainsJoinInfo()
    {
        var left = TestSourceNode.Create("account");
        var right = TestSourceNode.Create("contact");

        var join = new MergeJoinNode(left, right, "accountid", "parentaccountid", JoinType.Left);

        join.Description.Should().Contain("MergeJoin");
        join.Description.Should().Contain("Left");
        join.Description.Should().Contain("accountid");
    }

    [Fact]
    public void Constructor_NullLeft_ThrowsArgumentNullException()
    {
        var right = TestSourceNode.Create("contact");
        var act = () => new MergeJoinNode(null!, right, "id", "fk");
        act.Should().Throw<ArgumentNullException>();
    }
}

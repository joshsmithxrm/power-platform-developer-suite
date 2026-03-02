using System.Collections.Generic;
using FluentAssertions;
using PPDS.Dataverse.Query;
using PPDS.Dataverse.Query.Planning;
using PPDS.Query.Planning.Nodes;
using Xunit;

namespace PPDS.Query.Tests.Planning;

[Trait("Category", "Unit")]
public class HashSemiJoinNodeTests
{
    [Fact]
    public async Task SemiJoin_ReturnsOnlyMatchingOuterRows()
    {
        var outer = TestSourceNode.Create("account",
            TestSourceNode.MakeRow("account", ("accountid", "1"), ("name", "Contoso")),
            TestSourceNode.MakeRow("account", ("accountid", "2"), ("name", "Fabrikam")),
            TestSourceNode.MakeRow("account", ("accountid", "3"), ("name", "Northwind")));

        var inner = TestSourceNode.Create("contact",
            TestSourceNode.MakeRow("contact", ("parentcustomerid", "1")),
            TestSourceNode.MakeRow("contact", ("parentcustomerid", "3")));

        var node = new HashSemiJoinNode(outer, inner, "accountid", "parentcustomerid", antiSemiJoin: false);
        var rows = await TestHelpers.CollectRowsAsync(node);

        rows.Should().HaveCount(2);
        rows.Select(r => (string?)r.Values["name"].Value).Should().BeEquivalentTo(new[] { "Contoso", "Northwind" });
    }

    [Fact]
    public async Task AntiSemiJoin_ReturnsOnlyNonMatchingOuterRows()
    {
        var outer = TestSourceNode.Create("account",
            TestSourceNode.MakeRow("account", ("accountid", "1"), ("name", "Contoso")),
            TestSourceNode.MakeRow("account", ("accountid", "2"), ("name", "Fabrikam")),
            TestSourceNode.MakeRow("account", ("accountid", "3"), ("name", "Northwind")));

        var inner = TestSourceNode.Create("contact",
            TestSourceNode.MakeRow("contact", ("parentcustomerid", "1")),
            TestSourceNode.MakeRow("contact", ("parentcustomerid", "3")));

        var node = new HashSemiJoinNode(outer, inner, "accountid", "parentcustomerid", antiSemiJoin: true);
        var rows = await TestHelpers.CollectRowsAsync(node);

        rows.Should().HaveCount(1);
        rows[0].Values["name"].Value.Should().Be("Fabrikam");
    }

    [Fact]
    public async Task SemiJoin_EmptyInner_ReturnsNoRows()
    {
        var outer = TestSourceNode.Create("account",
            TestSourceNode.MakeRow("account", ("accountid", "1"), ("name", "Contoso")));
        var inner = TestSourceNode.Create("contact");

        var node = new HashSemiJoinNode(outer, inner, "accountid", "parentcustomerid", antiSemiJoin: false);
        var rows = await TestHelpers.CollectRowsAsync(node);

        rows.Should().BeEmpty();
    }

    [Fact]
    public async Task AntiSemiJoin_EmptyInner_ReturnsAllOuterRows()
    {
        var outer = TestSourceNode.Create("account",
            TestSourceNode.MakeRow("account", ("accountid", "1"), ("name", "Contoso")));
        var inner = TestSourceNode.Create("contact");

        var node = new HashSemiJoinNode(outer, inner, "accountid", "parentcustomerid", antiSemiJoin: true);
        var rows = await TestHelpers.CollectRowsAsync(node);

        rows.Should().HaveCount(1);
    }

    [Fact]
    public async Task SemiJoin_DuplicatesInInner_DoesNotDuplicateOuter()
    {
        var outer = TestSourceNode.Create("account",
            TestSourceNode.MakeRow("account", ("accountid", "1"), ("name", "Contoso")));
        var inner = TestSourceNode.Create("contact",
            TestSourceNode.MakeRow("contact", ("parentcustomerid", "1")),
            TestSourceNode.MakeRow("contact", ("parentcustomerid", "1")),
            TestSourceNode.MakeRow("contact", ("parentcustomerid", "1")));

        var node = new HashSemiJoinNode(outer, inner, "accountid", "parentcustomerid", antiSemiJoin: false);
        var rows = await TestHelpers.CollectRowsAsync(node);

        rows.Should().HaveCount(1);
    }

    [Fact]
    public async Task AntiSemiJoin_NullInInner_ReturnsNoRows()
    {
        var outer = TestSourceNode.Create("account",
            TestSourceNode.MakeRow("account", ("accountid", "1"), ("name", "Contoso")),
            TestSourceNode.MakeRow("account", ("accountid", "2"), ("name", "Fabrikam")));

        var inner = TestSourceNode.Create("contact",
            TestSourceNode.MakeRow("contact", ("parentcustomerid", "1")),
            TestSourceNode.MakeRow("contact", ("parentcustomerid", null)));

        var node = new HashSemiJoinNode(outer, inner, "accountid", "parentcustomerid", antiSemiJoin: true);
        var rows = await TestHelpers.CollectRowsAsync(node);

        rows.Should().BeEmpty();
    }

    [Fact]
    public async Task SemiJoin_NullKeyInOuter_ExcludesRow()
    {
        var outer = TestSourceNode.Create("account",
            TestSourceNode.MakeRow("account", ("accountid", null), ("name", "NullId")),
            TestSourceNode.MakeRow("account", ("accountid", "1"), ("name", "Contoso")));
        var inner = TestSourceNode.Create("contact",
            TestSourceNode.MakeRow("contact", ("parentcustomerid", "1")));

        var node = new HashSemiJoinNode(outer, inner, "accountid", "parentcustomerid", antiSemiJoin: false);
        var rows = await TestHelpers.CollectRowsAsync(node);

        rows.Should().HaveCount(1);
        rows[0].Values["name"].Value.Should().Be("Contoso");
    }

    [Fact]
    public async Task SemiJoin_CaseInsensitiveColumnLookup_StillMatches()
    {
        // Simulate a case-sensitive dictionary (as might come from external data sources)
        // where the column is "AccountId" but the key reference is "accountid"
        var outerRow1 = new QueryRow(
            new Dictionary<string, QueryValue>(StringComparer.Ordinal)
            {
                ["AccountId"] = QueryValue.Simple("1"),
                ["name"] = QueryValue.Simple("Contoso")
            }, "account");
        var outerRow2 = new QueryRow(
            new Dictionary<string, QueryValue>(StringComparer.Ordinal)
            {
                ["AccountId"] = QueryValue.Simple("2"),
                ["name"] = QueryValue.Simple("Fabrikam")
            }, "account");
        var innerRow = new QueryRow(
            new Dictionary<string, QueryValue>(StringComparer.Ordinal)
            {
                ["ParentCustomerId"] = QueryValue.Simple("1")
            }, "contact");

        var outer = TestSourceNode.Create("account", outerRow1, outerRow2);
        var inner = TestSourceNode.Create("contact", innerRow);

        var node = new HashSemiJoinNode(outer, inner,
            outerKeyColumn: "accountid",       // lowercase — doesn't match "AccountId" exactly
            innerKeyColumn: "parentcustomerid", // lowercase — doesn't match "ParentCustomerId" exactly
            antiSemiJoin: false);
        var rows = await TestHelpers.CollectRowsAsync(node);

        rows.Should().HaveCount(1,
            because: "case-insensitive fallback should find AccountId when looking for accountid");
    }
}

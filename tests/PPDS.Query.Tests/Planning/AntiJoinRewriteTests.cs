using FluentAssertions;
using Microsoft.SqlServer.TransactSql.ScriptDom;
using Moq;
using PPDS.Dataverse.Query;
using PPDS.Dataverse.Query.Planning;
using PPDS.Dataverse.Query.Planning.Nodes;
using PPDS.Dataverse.Sql.Transpilation;
using PPDS.Query.Parsing;
using PPDS.Query.Planning;
using PPDS.Query.Planning.Nodes;
using Xunit;

namespace PPDS.Query.Tests.Planning;

/// <summary>
/// Tests for the NOT IN / NOT EXISTS to LEFT OUTER JOIN + IS NULL rewrite optimization.
/// When a NOT IN subquery is simple (single table, single column, optional WHERE),
/// the planner rewrites it as a FetchXML link-entity with link-type="outer" and a null
/// condition instead of using a client-side HashSemiJoinNode.
/// </summary>
[Trait("Category", "Unit")]
public class AntiJoinRewriteTests
{
    private readonly QueryParser _parser = new();
    private readonly Mock<IFetchXmlGeneratorService> _mockFetchXmlService;
    private readonly ExecutionPlanBuilder _builder;

    public AntiJoinRewriteTests()
    {
        _mockFetchXmlService = new Mock<IFetchXmlGeneratorService>();
        _mockFetchXmlService
            .Setup(s => s.Generate(It.IsAny<TSqlFragment>()))
            .Returns(TranspileResult.Simple(
                "<fetch><entity name=\"account\"><all-attributes /></entity></fetch>"));

        _builder = new ExecutionPlanBuilder(_mockFetchXmlService.Object);
    }

    // ────────────────────────────────────────────
    //  Simple NOT IN → LEFT OUTER JOIN rewrite
    // ────────────────────────────────────────────

    [Fact]
    public void NotIn_SimpleSubquery_ProducesLeftJoinPlan()
    {
        var sql = @"SELECT * FROM account
                     WHERE accountid NOT IN (SELECT parentaccountid FROM account WHERE statecode = 1)";
        var fragment = _parser.Parse(sql);
        var result = _builder.Plan(fragment);

        // Verify the plan doesn't use HashSemiJoinNode (client-side)
        // Instead should use outer join approach via FetchXML
        var semiJoinNode = FindNode<HashSemiJoinNode>(result.RootNode);
        semiJoinNode.Should().BeNull(
            "simple NOT IN should be rewritten as LEFT OUTER JOIN, not use client-side HashSemiJoinNode");

        var fetchXml = result.FetchXml;
        fetchXml.Should().Contain("link-type=\"outer\"",
            "NOT IN should be rewritten as LEFT OUTER JOIN for FetchXML pushdown");
    }

    [Fact]
    public void NotIn_SimpleSubquery_FetchXmlContainsNullCondition()
    {
        var sql = @"SELECT * FROM account
                     WHERE accountid NOT IN (SELECT parentaccountid FROM account WHERE statecode = 1)";
        var fragment = _parser.Parse(sql);
        var result = _builder.Plan(fragment);

        var fetchXml = result.FetchXml;
        // The anti-join null check ensures only non-matching rows are returned
        fetchXml.Should().Contain("operator=\"null\"",
            "anti-join rewrite should include a null condition on the joined column");
    }

    [Fact]
    public void NotIn_SimpleSubquery_FetchXmlContainsInnerFilter()
    {
        var sql = @"SELECT * FROM account
                     WHERE accountid NOT IN (SELECT parentaccountid FROM account WHERE statecode = 1)";
        var fragment = _parser.Parse(sql);
        var result = _builder.Plan(fragment);

        var fetchXml = result.FetchXml;
        // The subquery WHERE (statecode = 1) should be pushed into the link-entity filter
        fetchXml.Should().Contain("attribute=\"statecode\"",
            "subquery WHERE conditions should be pushed into the link-entity filter");
        fetchXml.Should().Contain("value=\"1\"",
            "subquery WHERE condition value should be preserved");
    }

    [Fact]
    public void NotIn_SimpleSubquery_NoWhereClause_ProducesLeftJoin()
    {
        var sql = @"SELECT * FROM account
                     WHERE accountid NOT IN (SELECT parentaccountid FROM account)";
        var fragment = _parser.Parse(sql);
        var result = _builder.Plan(fragment);

        var semiJoinNode = FindNode<HashSemiJoinNode>(result.RootNode);
        semiJoinNode.Should().BeNull(
            "simple NOT IN without WHERE should also be rewritten");

        result.FetchXml.Should().Contain("link-type=\"outer\"");
    }

    // ────────────────────────────────────────────
    //  Complex NOT IN → HashSemiJoinNode fallback
    // ────────────────────────────────────────────

    [Fact]
    public void NotIn_SubqueryWithJoin_FallsBackToHashSemiJoin()
    {
        // Complex subquery with a JOIN shouldn't be rewritten
        // The FROM clause has a JOIN, so the subquery is not "simple"
        var mockService = new Mock<IFetchXmlGeneratorService>();
        mockService
            .Setup(s => s.Generate(It.IsAny<TSqlFragment>()))
            .Returns(TranspileResult.Simple(
                "<fetch><entity name=\"account\"><all-attributes /></entity></fetch>"));
        var builder = new ExecutionPlanBuilder(mockService.Object);

        var sql = @"SELECT * FROM account
                     WHERE accountid NOT IN (
                         SELECT a.accountid FROM account a
                         JOIN contact c ON a.accountid = c.parentcustomerid)";
        var fragment = _parser.Parse(sql);
        var result = builder.Plan(fragment);

        // Complex subquery should still use HashSemiJoinNode
        var semiJoinNode = FindNode<HashSemiJoinNode>(result.RootNode);
        semiJoinNode.Should().NotBeNull("Complex subquery with JOIN should use HashSemiJoinNode");
    }

    [Fact]
    public void NotIn_SubqueryWithGroupBy_FallsBackToHashSemiJoin()
    {
        // Subquery with GROUP BY should not be rewritten
        var sql = @"SELECT * FROM account
                     WHERE accountid NOT IN (
                         SELECT parentcustomerid FROM contact GROUP BY parentcustomerid)";
        var fragment = _parser.Parse(sql);
        var result = _builder.Plan(fragment);

        var semiJoinNode = FindNode<HashSemiJoinNode>(result.RootNode);
        semiJoinNode.Should().NotBeNull("Subquery with GROUP BY should use HashSemiJoinNode");
    }

    [Fact]
    public void NotIn_SubqueryWithDistinct_FallsBackToHashSemiJoin()
    {
        // Subquery with DISTINCT should not be rewritten
        var sql = @"SELECT * FROM account
                     WHERE accountid NOT IN (
                         SELECT DISTINCT parentcustomerid FROM contact)";
        var fragment = _parser.Parse(sql);
        var result = _builder.Plan(fragment);

        var semiJoinNode = FindNode<HashSemiJoinNode>(result.RootNode);
        semiJoinNode.Should().NotBeNull("Subquery with DISTINCT should use HashSemiJoinNode");
    }

    [Fact]
    public void NotIn_SubqueryWithTop_FallsBackToHashSemiJoin()
    {
        // Subquery with TOP should not be rewritten
        var sql = @"SELECT * FROM account
                     WHERE accountid NOT IN (
                         SELECT TOP 10 parentcustomerid FROM contact)";
        var fragment = _parser.Parse(sql);
        var result = _builder.Plan(fragment);

        var semiJoinNode = FindNode<HashSemiJoinNode>(result.RootNode);
        semiJoinNode.Should().NotBeNull("Subquery with TOP should use HashSemiJoinNode");
    }

    [Fact]
    public void NotIn_SubqueryWithExpressionColumn_FallsBackToHashSemiJoin()
    {
        // Subquery selecting an expression (not a plain column) should not be rewritten.
        // The anti-join detection correctly rejects this, and the fallback path also cannot
        // resolve the column name from a function expression, so it throws.
        var sql = @"SELECT * FROM account
                     WHERE accountid NOT IN (
                         SELECT UPPER(parentcustomerid) FROM contact)";
        var fragment = _parser.Parse(sql);

        // Function expressions in subquery SELECT can't determine inner column name
        var act = () => _builder.Plan(fragment);
        act.Should().Throw<QueryParseException>(
            "Subquery with expression column cannot determine inner column name");
    }

    [Fact]
    public void NotIn_SubqueryWithOrInWhere_FallsBackToHashSemiJoin()
    {
        // Subquery WHERE with OR should not be rewritten (OR is too complex for simple filter)
        var sql = @"SELECT * FROM account
                     WHERE accountid NOT IN (
                         SELECT parentcustomerid FROM contact WHERE statecode = 0 OR statecode = 1)";
        var fragment = _parser.Parse(sql);
        var result = _builder.Plan(fragment);

        var semiJoinNode = FindNode<HashSemiJoinNode>(result.RootNode);
        semiJoinNode.Should().NotBeNull("Subquery with OR in WHERE should use HashSemiJoinNode");
    }

    // ────────────────────────────────────────────
    //  IN (positive) always uses HashSemiJoinNode
    // ────────────────────────────────────────────

    [Fact]
    public void In_SimpleSubquery_StillUsesHashSemiJoin()
    {
        // Positive IN (not NOT IN) should NOT be rewritten — rewrite only applies to anti-join
        var sql = @"SELECT * FROM account
                     WHERE accountid IN (SELECT parentcustomerid FROM contact)";
        var fragment = _parser.Parse(sql);
        var result = _builder.Plan(fragment);

        var semiJoinNode = FindNode<HashSemiJoinNode>(result.RootNode);
        semiJoinNode.Should().NotBeNull(
            "positive IN should always use HashSemiJoinNode (rewrite only applies to NOT IN)");
    }

    // ────────────────────────────────────────────
    //  Mixed: NOT IN + other WHERE conditions
    // ────────────────────────────────────────────

    [Fact]
    public void NotIn_WithAdditionalWhereCondition_RewritesToOuterJoin()
    {
        var sql = @"SELECT * FROM account
                     WHERE statecode = 0
                     AND accountid NOT IN (SELECT parentaccountid FROM account)";
        var fragment = _parser.Parse(sql);
        var result = _builder.Plan(fragment);

        // The NOT IN part should be rewritten, other WHERE conditions stay in FetchXML
        var semiJoinNode = FindNode<HashSemiJoinNode>(result.RootNode);
        semiJoinNode.Should().BeNull(
            "simple NOT IN combined with other conditions should still be rewritten");
        result.FetchXml.Should().Contain("link-type=\"outer\"");
    }

    // ────────────────────────────────────────────
    //  FetchXML structure validation
    // ────────────────────────────────────────────

    [Fact]
    public void NotIn_SimpleSubquery_FetchXmlHasEntityNameAttribute()
    {
        var sql = @"SELECT * FROM account
                     WHERE accountid NOT IN (SELECT parentaccountid FROM account)";
        var fragment = _parser.Parse(sql);
        var result = _builder.Plan(fragment);

        var fetchXml = result.FetchXml;
        // The null condition should reference the alias via entityname attribute
        fetchXml.Should().Contain("entityname=",
            "null condition should use entityname attribute to reference link-entity alias");
    }

    [Fact]
    public void NotIn_SimpleSubquery_FetchXmlHasAlias()
    {
        var sql = @"SELECT * FROM account
                     WHERE accountid NOT IN (SELECT parentaccountid FROM account)";
        var fragment = _parser.Parse(sql);
        var result = _builder.Plan(fragment);

        var fetchXml = result.FetchXml;
        // The link-entity should have an alias for the entityname reference
        fetchXml.Should().Contain("alias=",
            "link-entity should have an alias for anti-join null condition reference");
    }

    // ────────────────────────────────────────────
    //  Helper: find node type in plan tree
    // ────────────────────────────────────────────

    private static T? FindNode<T>(IQueryPlanNode node) where T : class, IQueryPlanNode
    {
        if (node is T match) return match;
        foreach (var child in node.Children)
        {
            var found = FindNode<T>(child);
            if (found != null) return found;
        }
        return null;
    }

    private static bool ContainsNodeOfType<T>(IQueryPlanNode node) where T : IQueryPlanNode
    {
        if (node is T) return true;
        return node.Children.Any(ContainsNodeOfType<T>);
    }
}

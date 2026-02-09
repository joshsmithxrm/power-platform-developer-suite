using FluentAssertions;
using PPDS.Dataverse.Query.Planning.Rewrites;
using PPDS.Dataverse.Sql.Ast;
using PPDS.Dataverse.Sql.Parsing;
using Xunit;

namespace PPDS.Dataverse.Tests.Query.Planning.Rewrites;

[Trait("Category", "TuiUnit")]
public class InSubqueryRewriteTests
{
    [Fact]
    public void TryRewrite_SimpleInSubquery_ProducesJoin()
    {
        var stmt = SqlParser.Parse(
            "SELECT name FROM account WHERE accountid IN (SELECT accountid FROM opportunity)");

        var result = InSubqueryToJoinRewrite.TryRewrite(stmt);

        result.IsRewritten.Should().BeTrue();
        var rewritten = result.RewrittenStatement!;
        rewritten.Joins.Should().HaveCount(1);
        rewritten.Joins[0].Type.Should().Be(SqlJoinType.Inner);
        rewritten.Joins[0].Table.TableName.Should().Be("opportunity");
        rewritten.Joins[0].Table.Alias.Should().Be("opportunity_sub0");
        rewritten.Joins[0].LeftColumn.ColumnName.Should().Be("accountid");
        rewritten.Joins[0].RightColumn.TableName.Should().Be("opportunity_sub0");
        rewritten.Joins[0].RightColumn.ColumnName.Should().Be("accountid");
    }

    [Fact]
    public void TryRewrite_SimpleInSubquery_AddsDistinct()
    {
        var stmt = SqlParser.Parse(
            "SELECT name FROM account WHERE accountid IN (SELECT accountid FROM opportunity)");

        var result = InSubqueryToJoinRewrite.TryRewrite(stmt);

        result.IsRewritten.Should().BeTrue();
        result.RewrittenStatement!.Distinct.Should().BeTrue();
    }

    [Fact]
    public void TryRewrite_SimpleInSubquery_RemovesInConditionFromWhere()
    {
        var stmt = SqlParser.Parse(
            "SELECT name FROM account WHERE accountid IN (SELECT accountid FROM opportunity)");

        var result = InSubqueryToJoinRewrite.TryRewrite(stmt);

        result.IsRewritten.Should().BeTrue();
        // The IN subquery was the only condition, so WHERE should be null
        result.RewrittenStatement!.Where.Should().BeNull();
    }

    [Fact]
    public void TryRewrite_InSubqueryWithOuterWhere_PreservesOtherConditions()
    {
        var stmt = SqlParser.Parse(
            "SELECT name FROM account WHERE statecode = 0 AND accountid IN (SELECT accountid FROM opportunity)");

        var result = InSubqueryToJoinRewrite.TryRewrite(stmt);

        result.IsRewritten.Should().BeTrue();
        var rewritten = result.RewrittenStatement!;

        // The comparison condition should remain
        var remaining = (SqlComparisonCondition)rewritten.Where!;
        remaining.Column.ColumnName.Should().Be("statecode");
        remaining.Value.Value.Should().Be("0");

        // Join should be added
        rewritten.Joins.Should().HaveCount(1);
    }

    [Fact]
    public void TryRewrite_InSubqueryWithSubqueryWhere_MergesConditions()
    {
        var stmt = SqlParser.Parse(
            "SELECT name FROM account WHERE accountid IN (SELECT accountid FROM opportunity WHERE statecode = 0)");

        var result = InSubqueryToJoinRewrite.TryRewrite(stmt);

        result.IsRewritten.Should().BeTrue();
        var rewritten = result.RewrittenStatement!;

        // Subquery's WHERE becomes a condition re-qualified to the new alias
        var mergedWhere = (SqlComparisonCondition)rewritten.Where!;
        mergedWhere.Column.TableName.Should().Be("opportunity_sub0");
        mergedWhere.Column.ColumnName.Should().Be("statecode");
        mergedWhere.Value.Value.Should().Be("0");
    }

    [Fact]
    public void TryRewrite_NotInSubquery_FallsBack()
    {
        var stmt = SqlParser.Parse(
            "SELECT name FROM account WHERE accountid NOT IN (SELECT accountid FROM opportunity)");

        var result = InSubqueryToJoinRewrite.TryRewrite(stmt);

        result.IsRewritten.Should().BeFalse();
        result.FallbackCondition.Should().NotBeNull();
        result.FallbackCondition!.IsNegated.Should().BeTrue();
    }

    [Fact]
    public void TryRewrite_NoInSubquery_PassesThrough()
    {
        var stmt = SqlParser.Parse(
            "SELECT name FROM account WHERE statecode = 0");

        var result = InSubqueryToJoinRewrite.TryRewrite(stmt);

        result.IsRewritten.Should().BeTrue();
        result.RewrittenStatement.Should().NotBeNull();
        // Should be essentially unchanged
        result.RewrittenStatement!.Joins.Should().BeEmpty();
    }

    [Fact]
    public void TryRewrite_NoWhereClause_PassesThrough()
    {
        var stmt = SqlParser.Parse("SELECT name FROM account");

        var result = InSubqueryToJoinRewrite.TryRewrite(stmt);

        result.IsRewritten.Should().BeTrue();
        result.RewrittenStatement.Should().NotBeNull();
    }

    [Fact]
    public void TryRewrite_UniqueAlias_AvoidsConflicts()
    {
        // The outer query already has a join to opportunity
        var stmt = SqlParser.Parse(
            "SELECT a.name FROM account a " +
            "INNER JOIN opportunity ON a.accountid = opportunity.accountid " +
            "WHERE a.accountid IN (SELECT accountid FROM opportunity)");

        var result = InSubqueryToJoinRewrite.TryRewrite(stmt);

        result.IsRewritten.Should().BeTrue();
        var rewritten = result.RewrittenStatement!;
        // Should have 2 joins: original + rewrite
        rewritten.Joins.Should().HaveCount(2);
        // The new join alias should not collide with 'opportunity'
        rewritten.Joins[1].Table.Alias.Should().Be("opportunity_sub0");
    }

    [Fact]
    public void TryRewrite_BothOuterAndSubqueryWhere_MergesAll()
    {
        var stmt = SqlParser.Parse(
            "SELECT name FROM account " +
            "WHERE statecode = 0 AND accountid IN (SELECT accountid FROM opportunity WHERE revenue > 1000)");

        var result = InSubqueryToJoinRewrite.TryRewrite(stmt);

        result.IsRewritten.Should().BeTrue();
        var rewritten = result.RewrittenStatement!;

        // Both the outer statecode=0 and the subquery's revenue>1000 (re-qualified) should remain
        var logical = (SqlLogicalCondition)rewritten.Where!;
        logical.Conditions.Should().HaveCount(2);

        // First: outer condition
        var outerCond = (SqlComparisonCondition)logical.Conditions[0];
        outerCond.Column.ColumnName.Should().Be("statecode");

        // Second: merged subquery condition, re-qualified to alias
        var mergedCond = (SqlComparisonCondition)logical.Conditions[1];
        mergedCond.Column.TableName.Should().Be("opportunity_sub0");
        mergedCond.Column.ColumnName.Should().Be("revenue");
    }

    [Fact]
    public void TryRewrite_SubqueryWithGroupBy_FallsBack()
    {
        var stmt = SqlParser.Parse(
            "SELECT name FROM account WHERE accountid IN (SELECT accountid FROM opportunity GROUP BY accountid)");

        var result = InSubqueryToJoinRewrite.TryRewrite(stmt);

        result.IsRewritten.Should().BeFalse();
        result.FallbackCondition.Should().NotBeNull();
    }

    [Fact]
    public void TryRewrite_SubqueryWithMultipleColumns_FallsBack()
    {
        // Subquery selects two columns - not a simple single-column subquery
        // We cannot parse this because the subquery has 2 columns in the SELECT.
        // Actually, the parser will parse it fine, but the rewrite should reject it.
        // However, IN subqueries semantically only return one column.
        // Let's test the rewrite's validation of the subquery structure.
        var subquery = new SqlSelectStatement(
            new ISqlSelectColumn[] { SqlColumnRef.Simple("accountid"), SqlColumnRef.Simple("name") },
            new SqlTableRef("opportunity"));
        var inSub = new SqlInSubqueryCondition(
            SqlColumnRef.Simple("accountid"), subquery, false);
        var stmt = new SqlSelectStatement(
            new ISqlSelectColumn[] { SqlColumnRef.Simple("name") },
            new SqlTableRef("account"),
            where: inSub);

        var result = InSubqueryToJoinRewrite.TryRewrite(stmt);

        result.IsRewritten.Should().BeFalse();
        result.FallbackCondition.Should().NotBeNull();
    }

    [Fact]
    public void TryRewrite_PreservesOriginalColumns()
    {
        var stmt = SqlParser.Parse(
            "SELECT name, revenue FROM account WHERE accountid IN (SELECT accountid FROM opportunity)");

        var result = InSubqueryToJoinRewrite.TryRewrite(stmt);

        result.IsRewritten.Should().BeTrue();
        result.RewrittenStatement!.Columns.Should().HaveCount(2);
    }

    [Fact]
    public void TryRewrite_PreservesOrderBy()
    {
        var stmt = SqlParser.Parse(
            "SELECT name FROM account WHERE accountid IN (SELECT accountid FROM opportunity) ORDER BY name ASC");

        var result = InSubqueryToJoinRewrite.TryRewrite(stmt);

        result.IsRewritten.Should().BeTrue();
        result.RewrittenStatement!.OrderBy.Should().HaveCount(1);
        result.RewrittenStatement.OrderBy[0].Column.ColumnName.Should().Be("name");
    }

    [Fact]
    public void TryRewrite_PreservesTop()
    {
        var stmt = SqlParser.Parse(
            "SELECT TOP 5 name FROM account WHERE accountid IN (SELECT accountid FROM opportunity)");

        var result = InSubqueryToJoinRewrite.TryRewrite(stmt);

        result.IsRewritten.Should().BeTrue();
        result.RewrittenStatement!.Top.Should().Be(5);
    }
}

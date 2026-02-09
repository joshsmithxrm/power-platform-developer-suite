using FluentAssertions;
using PPDS.Dataverse.Query.Planning.Rewrites;
using PPDS.Dataverse.Sql.Ast;
using PPDS.Dataverse.Sql.Parsing;
using Xunit;

namespace PPDS.Dataverse.Tests.Query.Planning.Rewrites;

[Trait("Category", "TuiUnit")]
public class ExistsRewriteTests
{
    [Fact]
    public void TryRewrite_ExistsWithCorrelation_ProducesInnerJoin()
    {
        var stmt = SqlParser.Parse(
            "SELECT name FROM account a " +
            "WHERE EXISTS (SELECT 1 FROM contact c WHERE c.parentcustomerid = a.accountid)");

        var result = ExistsToJoinRewrite.TryRewrite(stmt);

        result.Joins.Should().HaveCount(1);
        result.Joins[0].Type.Should().Be(SqlJoinType.Inner);
        result.Joins[0].Table.TableName.Should().Be("contact");
        result.Joins[0].Table.Alias.Should().StartWith("contact_exists");
    }

    [Fact]
    public void TryRewrite_ExistsWithCorrelation_JoinOnConditionIsCorrect()
    {
        var stmt = SqlParser.Parse(
            "SELECT name FROM account a " +
            "WHERE EXISTS (SELECT 1 FROM contact c WHERE c.parentcustomerid = a.accountid)");

        var result = ExistsToJoinRewrite.TryRewrite(stmt);

        var join = result.Joins[0];
        // Outer column: a.accountid
        join.LeftColumn.TableName.Should().Be("a");
        join.LeftColumn.ColumnName.Should().Be("accountid");
        // Inner column: contact_exists0.parentcustomerid
        join.RightColumn.ColumnName.Should().Be("parentcustomerid");
        join.RightColumn.TableName.Should().Be(join.Table.Alias);
    }

    [Fact]
    public void TryRewrite_ExistsWithCorrelation_AddsDistinct()
    {
        var stmt = SqlParser.Parse(
            "SELECT name FROM account a " +
            "WHERE EXISTS (SELECT 1 FROM contact c WHERE c.parentcustomerid = a.accountid)");

        var result = ExistsToJoinRewrite.TryRewrite(stmt);

        result.Distinct.Should().BeTrue();
    }

    [Fact]
    public void TryRewrite_ExistsWithCorrelation_RemovesExistsFromWhere()
    {
        var stmt = SqlParser.Parse(
            "SELECT name FROM account a " +
            "WHERE EXISTS (SELECT 1 FROM contact c WHERE c.parentcustomerid = a.accountid)");

        var result = ExistsToJoinRewrite.TryRewrite(stmt);

        // The correlated comparison was the only subquery WHERE condition,
        // and the EXISTS was the only outer WHERE condition, so WHERE should be null
        result.Where.Should().BeNull();
    }

    [Fact]
    public void TryRewrite_NotExistsWithCorrelation_ProducesLeftJoinWithIsNull()
    {
        var stmt = SqlParser.Parse(
            "SELECT name FROM account a " +
            "WHERE NOT EXISTS (SELECT 1 FROM contact c WHERE c.parentcustomerid = a.accountid)");

        var result = ExistsToJoinRewrite.TryRewrite(stmt);

        // Should produce LEFT JOIN
        result.Joins.Should().HaveCount(1);
        result.Joins[0].Type.Should().Be(SqlJoinType.Left);

        // WHERE should have IS NULL condition on the joined entity's column
        var isNull = (SqlNullCondition)result.Where!;
        isNull.IsNegated.Should().BeFalse();
        isNull.Column.TableName.Should().Be(result.Joins[0].Table.Alias);
        isNull.Column.ColumnName.Should().Be("parentcustomerid");
    }

    [Fact]
    public void TryRewrite_ExistsWithAdditionalSubqueryWhere_MergesConditions()
    {
        var stmt = SqlParser.Parse(
            "SELECT name FROM account a " +
            "WHERE EXISTS (" +
            "  SELECT 1 FROM contact c " +
            "  WHERE c.parentcustomerid = a.accountid AND c.statecode = 0" +
            ")");

        var result = ExistsToJoinRewrite.TryRewrite(stmt);

        result.Joins.Should().HaveCount(1);

        // The non-correlated subquery condition (statecode = 0) should be merged
        // into the outer WHERE, re-qualified to the new alias
        var mergedWhere = (SqlComparisonCondition)result.Where!;
        mergedWhere.Column.TableName.Should().StartWith("contact_exists");
        mergedWhere.Column.ColumnName.Should().Be("statecode");
        mergedWhere.Value.Value.Should().Be("0");
    }

    [Fact]
    public void TryRewrite_ExistsWithOuterWhereCondition_PreservesOtherConditions()
    {
        var stmt = SqlParser.Parse(
            "SELECT name FROM account a " +
            "WHERE statecode = 0 " +
            "AND EXISTS (SELECT 1 FROM contact c WHERE c.parentcustomerid = a.accountid)");

        var result = ExistsToJoinRewrite.TryRewrite(stmt);

        result.Joins.Should().HaveCount(1);

        // The outer statecode condition should remain
        var remaining = (SqlComparisonCondition)result.Where!;
        remaining.Column.ColumnName.Should().Be("statecode");
        remaining.Value.Value.Should().Be("0");
    }

    [Fact]
    public void TryRewrite_NoExistsCondition_ReturnsOriginal()
    {
        var stmt = SqlParser.Parse("SELECT name FROM account WHERE statecode = 0");

        var result = ExistsToJoinRewrite.TryRewrite(stmt);

        // Should be the original statement unchanged
        result.Joins.Should().BeEmpty();
        result.Where.Should().NotBeNull();
    }

    [Fact]
    public void TryRewrite_NoWhereClause_ReturnsOriginal()
    {
        var stmt = SqlParser.Parse("SELECT name FROM account");

        var result = ExistsToJoinRewrite.TryRewrite(stmt);

        result.Joins.Should().BeEmpty();
    }

    [Fact]
    public void TryRewrite_ExistsNoCorrelation_PassesThrough()
    {
        // EXISTS without a correlated reference cannot be rewritten to JOIN
        var stmt = SqlParser.Parse(
            "SELECT name FROM account a " +
            "WHERE EXISTS (SELECT 1 FROM contact c WHERE c.statecode = 0)");

        var result = ExistsToJoinRewrite.TryRewrite(stmt);

        // Should not be rewritten (no correlation detected)
        result.Joins.Should().BeEmpty();
        var exists = result.Where as SqlExistsCondition;
        exists.Should().NotBeNull("EXISTS without correlation should pass through unchanged");
    }

    [Fact]
    public void TryRewrite_ExistsNoSubqueryWhere_PassesThrough()
    {
        // EXISTS without WHERE cannot be correlated
        var stmt = SqlParser.Parse(
            "SELECT name FROM account a " +
            "WHERE EXISTS (SELECT 1 FROM contact)");

        var result = ExistsToJoinRewrite.TryRewrite(stmt);

        // Should not be rewritten
        result.Joins.Should().BeEmpty();
        var exists = result.Where as SqlExistsCondition;
        exists.Should().NotBeNull();
    }

    [Fact]
    public void TryRewrite_ExistsReversedCorrelation_StillWorks()
    {
        // The correlated reference has columns in reversed order: outer = inner
        var stmt = SqlParser.Parse(
            "SELECT name FROM account a " +
            "WHERE EXISTS (SELECT 1 FROM contact c WHERE a.accountid = c.parentcustomerid)");

        var result = ExistsToJoinRewrite.TryRewrite(stmt);

        result.Joins.Should().HaveCount(1);
        result.Joins[0].Type.Should().Be(SqlJoinType.Inner);

        // Join should still be correct regardless of column order
        var join = result.Joins[0];
        join.LeftColumn.TableName.Should().Be("a");
        join.LeftColumn.ColumnName.Should().Be("accountid");
        join.RightColumn.ColumnName.Should().Be("parentcustomerid");
    }

    [Fact]
    public void TryRewrite_NotExistsWithAdditionalSubqueryWhere_MergesWithIsNull()
    {
        var stmt = SqlParser.Parse(
            "SELECT name FROM account a " +
            "WHERE NOT EXISTS (" +
            "  SELECT 1 FROM contact c " +
            "  WHERE c.parentcustomerid = a.accountid AND c.statecode = 0" +
            ")");

        var result = ExistsToJoinRewrite.TryRewrite(stmt);

        result.Joins.Should().HaveCount(1);
        result.Joins[0].Type.Should().Be(SqlJoinType.Left);

        // WHERE should combine the IS NULL and the re-qualified statecode condition
        var logical = (SqlLogicalCondition)result.Where!;
        logical.Operator.Should().Be(SqlLogicalOperator.And);
        logical.Conditions.Should().HaveCount(2);
    }

    [Fact]
    public void TryRewrite_PreservesOriginalColumns()
    {
        var stmt = SqlParser.Parse(
            "SELECT name, revenue FROM account a " +
            "WHERE EXISTS (SELECT 1 FROM contact c WHERE c.parentcustomerid = a.accountid)");

        var result = ExistsToJoinRewrite.TryRewrite(stmt);

        result.Columns.Should().HaveCount(2);
    }

    [Fact]
    public void TryRewrite_PreservesOrderBy()
    {
        var stmt = SqlParser.Parse(
            "SELECT name FROM account a " +
            "WHERE EXISTS (SELECT 1 FROM contact c WHERE c.parentcustomerid = a.accountid) " +
            "ORDER BY name ASC");

        var result = ExistsToJoinRewrite.TryRewrite(stmt);

        result.OrderBy.Should().HaveCount(1);
        result.OrderBy[0].Column.ColumnName.Should().Be("name");
    }

    [Fact]
    public void TryRewrite_PreservesTop()
    {
        var stmt = SqlParser.Parse(
            "SELECT TOP 5 name FROM account a " +
            "WHERE EXISTS (SELECT 1 FROM contact c WHERE c.parentcustomerid = a.accountid)");

        var result = ExistsToJoinRewrite.TryRewrite(stmt);

        result.Top.Should().Be(5);
    }

    [Fact]
    public void TryRewrite_UniqueAlias_AvoidsConflicts()
    {
        // Outer query already has a join with contact
        var stmt = SqlParser.Parse(
            "SELECT a.name FROM account a " +
            "INNER JOIN contact ON a.accountid = contact.parentcustomerid " +
            "WHERE EXISTS (SELECT 1 FROM contact c WHERE c.parentcustomerid = a.accountid)");

        var result = ExistsToJoinRewrite.TryRewrite(stmt);

        // Should have 2 joins: original + rewrite
        result.Joins.Should().HaveCount(2);
        // The new join alias should not collide with 'contact'
        result.Joins[1].Table.Alias.Should().Be("contact_exists0");
    }
}

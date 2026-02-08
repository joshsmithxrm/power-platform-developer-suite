using FluentAssertions;
using PPDS.Dataverse.Sql.Ast;
using PPDS.Dataverse.Sql.Parsing;
using Xunit;

namespace PPDS.Dataverse.Tests.Sql.Parsing;

[Trait("Category", "TuiUnit")]
public class HavingParserTests
{
    [Fact]
    public void Parse_HavingWithComparison_SetsHavingProperty()
    {
        // Arrange
        var sql = "SELECT ownerid, COUNT(*) AS cnt FROM account GROUP BY ownerid HAVING cnt > 5";

        // Act
        var result = SqlParser.Parse(sql);

        // Assert
        result.Having.Should().NotBeNull();
        var comparison = result.Having as SqlComparisonCondition;
        comparison.Should().NotBeNull();
        comparison!.Column.ColumnName.Should().Be("cnt");
        comparison.Operator.Should().Be(SqlComparisonOperator.GreaterThan);
        comparison.Value.Value.Should().Be("5");
    }

    [Fact]
    public void Parse_HavingWithAndCondition_ParsesLogicalCondition()
    {
        // Arrange
        var sql = "SELECT ownerid, COUNT(*) AS cnt, SUM(revenue) AS total FROM account GROUP BY ownerid HAVING cnt > 5 AND total > 1000";

        // Act
        var result = SqlParser.Parse(sql);

        // Assert
        result.Having.Should().NotBeNull();
        var logical = result.Having as SqlLogicalCondition;
        logical.Should().NotBeNull();
        logical!.Operator.Should().Be(SqlLogicalOperator.And);
        logical.Conditions.Should().HaveCount(2);
    }

    [Fact]
    public void Parse_HavingWithOrCondition_ParsesLogicalCondition()
    {
        // Arrange
        var sql = "SELECT ownerid, COUNT(*) AS cnt FROM account GROUP BY ownerid HAVING cnt > 10 OR cnt < 2";

        // Act
        var result = SqlParser.Parse(sql);

        // Assert
        result.Having.Should().NotBeNull();
        var logical = result.Having as SqlLogicalCondition;
        logical.Should().NotBeNull();
        logical!.Operator.Should().Be(SqlLogicalOperator.Or);
        logical.Conditions.Should().HaveCount(2);
    }

    [Fact]
    public void Parse_HavingWithoutGroupBy_StillParses()
    {
        // Arrange - edge case: HAVING without GROUP BY
        var sql = "SELECT COUNT(*) AS cnt FROM account HAVING cnt > 0";

        // Act
        var result = SqlParser.Parse(sql);

        // Assert
        result.Having.Should().NotBeNull();
        result.GroupBy.Should().BeEmpty();
    }

    [Fact]
    public void Parse_SelectWithoutHaving_HasNullHavingProperty()
    {
        // Arrange
        var sql = "SELECT name FROM account";

        // Act
        var result = SqlParser.Parse(sql);

        // Assert
        result.Having.Should().BeNull();
    }

    [Fact]
    public void Parse_GroupByWithoutHaving_HasNullHavingProperty()
    {
        // Arrange
        var sql = "SELECT ownerid, COUNT(*) FROM account GROUP BY ownerid";

        // Act
        var result = SqlParser.Parse(sql);

        // Assert
        result.Having.Should().BeNull();
    }

    [Fact]
    public void Parse_HavingWithOrderBy_BothParsed()
    {
        // Arrange
        var sql = "SELECT ownerid, COUNT(*) AS cnt FROM account GROUP BY ownerid HAVING cnt > 5 ORDER BY cnt DESC";

        // Act
        var result = SqlParser.Parse(sql);

        // Assert
        result.Having.Should().NotBeNull();
        result.OrderBy.Should().HaveCount(1);
        result.OrderBy[0].Direction.Should().Be(SqlSortDirection.Descending);
    }

    [Fact]
    public void Parse_HavingWithEquals_ParsesComparison()
    {
        // Arrange
        var sql = "SELECT statecode, COUNT(*) AS cnt FROM account GROUP BY statecode HAVING cnt = 10";

        // Act
        var result = SqlParser.Parse(sql);

        // Assert
        var comparison = result.Having as SqlComparisonCondition;
        comparison.Should().NotBeNull();
        comparison!.Operator.Should().Be(SqlComparisonOperator.Equal);
    }

    [Fact]
    public void Parse_ComplexQueryWithHaving_AllClausesParsed()
    {
        // Arrange
        var sql = @"
            SELECT
                ownerid,
                COUNT(*) AS cnt,
                SUM(revenue) AS total_revenue
            FROM account
            WHERE statecode = 0
            GROUP BY ownerid
            HAVING cnt > 5 AND total_revenue > 1000000
            ORDER BY total_revenue DESC";

        // Act
        var result = SqlParser.Parse(sql);

        // Assert
        result.Where.Should().NotBeNull();
        result.GroupBy.Should().HaveCount(1);
        result.Having.Should().NotBeNull();
        result.OrderBy.Should().HaveCount(1);
        result.Columns.Should().HaveCount(3);
    }
}

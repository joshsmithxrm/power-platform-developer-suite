using FluentAssertions;
using PPDS.Dataverse.Sql.Ast;
using PPDS.Dataverse.Sql.Parsing;
using Xunit;

namespace PPDS.Dataverse.Tests.Sql.Parsing;

public class SqlParserTests
{
    #region Basic SELECT

    [Fact]
    public void Parse_SimpleSelect_ReturnsValidAst()
    {
        // Arrange
        var parser = new SqlParser("SELECT name FROM account");

        // Act
        var result = parser.Parse();

        // Assert
        result.Should().NotBeNull();
        result.From.TableName.Should().Be("account");
        result.Columns.Should().HaveCount(1);
    }

    [Fact]
    public void Parse_SelectStar_ReturnsWildcardColumn()
    {
        // Arrange
        var parser = new SqlParser("SELECT * FROM account");

        // Act
        var result = parser.Parse();

        // Assert
        result.IsSelectAll().Should().BeTrue();
        result.Columns.Should().HaveCount(1);
        var column = result.Columns[0] as SqlColumnRef;
        column.Should().NotBeNull();
        column!.IsWildcard.Should().BeTrue();
    }

    [Fact]
    public void Parse_MultipleColumns_ReturnsAllColumns()
    {
        // Arrange
        var parser = new SqlParser("SELECT name, accountid, revenue FROM account");

        // Act
        var result = parser.Parse();

        // Assert
        result.Columns.Should().HaveCount(3);
    }

    [Fact]
    public void Parse_ColumnWithAlias_CapturesAlias()
    {
        // Arrange
        var parser = new SqlParser("SELECT name AS accountname FROM account");

        // Act
        var result = parser.Parse();

        // Assert
        var column = result.Columns[0] as SqlColumnRef;
        column.Should().NotBeNull();
        column!.Alias.Should().Be("accountname");
    }

    [Fact]
    public void Parse_TableWithAlias_CapturesAlias()
    {
        // Arrange
        var parser = new SqlParser("SELECT a.name FROM account a");

        // Act
        var result = parser.Parse();

        // Assert
        result.From.Alias.Should().Be("a");
    }

    [Fact]
    public void Parse_QualifiedColumn_CapturesTableName()
    {
        // Arrange
        var parser = new SqlParser("SELECT a.name FROM account a");

        // Act
        var result = parser.Parse();

        // Assert
        var column = result.Columns[0] as SqlColumnRef;
        column.Should().NotBeNull();
        column!.TableName.Should().Be("a");
        column.ColumnName.Should().Be("name");
    }

    #endregion

    #region TOP / DISTINCT

    [Fact]
    public void Parse_TopClause_CapturesLimit()
    {
        // Arrange
        var parser = new SqlParser("SELECT TOP 10 name FROM account");

        // Act
        var result = parser.Parse();

        // Assert
        result.Top.Should().Be(10);
    }

    [Fact]
    public void Parse_Distinct_SetsDistinctFlag()
    {
        // Arrange
        var parser = new SqlParser("SELECT DISTINCT name FROM account");

        // Act
        var result = parser.Parse();

        // Assert
        result.Distinct.Should().BeTrue();
    }

    [Fact]
    public void Parse_TopAndDistinct_HandlesBoth()
    {
        // Arrange
        var parser = new SqlParser("SELECT DISTINCT TOP 5 name FROM account");

        // Act
        var result = parser.Parse();

        // Assert
        result.Distinct.Should().BeTrue();
        result.Top.Should().Be(5);
    }

    #endregion

    #region WHERE Clause

    [Fact]
    public void Parse_WhereEquals_CreatesComparisonCondition()
    {
        // Arrange
        var parser = new SqlParser("SELECT name FROM account WHERE statecode = 0");

        // Act
        var result = parser.Parse();

        // Assert
        result.Where.Should().NotBeNull();
        var condition = result.Where as SqlComparisonCondition;
        condition.Should().NotBeNull();
        condition!.Operator.Should().Be(SqlComparisonOperator.Equal);
    }

    [Fact]
    public void Parse_WhereNotEquals_CreatesComparisonCondition()
    {
        // Arrange
        var parser = new SqlParser("SELECT name FROM account WHERE statecode <> 1");

        // Act
        var result = parser.Parse();

        // Assert
        var condition = result.Where as SqlComparisonCondition;
        condition.Should().NotBeNull();
        condition!.Operator.Should().Be(SqlComparisonOperator.NotEqual);
    }

    [Theory]
    [InlineData("<", SqlComparisonOperator.LessThan)]
    [InlineData(">", SqlComparisonOperator.GreaterThan)]
    [InlineData("<=", SqlComparisonOperator.LessThanOrEqual)]
    [InlineData(">=", SqlComparisonOperator.GreaterThanOrEqual)]
    public void Parse_ComparisonOperators_ReturnsCorrectOperator(string op, SqlComparisonOperator expected)
    {
        // Arrange
        var parser = new SqlParser($"SELECT name FROM account WHERE revenue {op} 1000");

        // Act
        var result = parser.Parse();

        // Assert
        var condition = result.Where as SqlComparisonCondition;
        condition.Should().NotBeNull();
        condition!.Operator.Should().Be(expected);
    }

    [Fact]
    public void Parse_WhereWithStringLiteral_CapturesValue()
    {
        // Arrange
        var parser = new SqlParser("SELECT name FROM account WHERE name = 'Contoso'");

        // Act
        var result = parser.Parse();

        // Assert
        var condition = result.Where as SqlComparisonCondition;
        condition.Should().NotBeNull();
        condition!.Value.Value.Should().Be("Contoso");
    }

    [Fact]
    public void Parse_WhereAnd_CreatesLogicalCondition()
    {
        // Arrange
        var parser = new SqlParser("SELECT name FROM account WHERE statecode = 0 AND revenue > 1000");

        // Act
        var result = parser.Parse();

        // Assert
        var condition = result.Where as SqlLogicalCondition;
        condition.Should().NotBeNull();
        condition!.Operator.Should().Be(SqlLogicalOperator.And);
        condition.Conditions.Should().HaveCount(2);
    }

    [Fact]
    public void Parse_WhereOr_CreatesLogicalCondition()
    {
        // Arrange
        var parser = new SqlParser("SELECT name FROM account WHERE statecode = 0 OR statecode = 1");

        // Act
        var result = parser.Parse();

        // Assert
        var condition = result.Where as SqlLogicalCondition;
        condition.Should().NotBeNull();
        condition!.Operator.Should().Be(SqlLogicalOperator.Or);
    }

    [Fact]
    public void Parse_WhereIsNull_CreatesNullCondition()
    {
        // Arrange
        var parser = new SqlParser("SELECT name FROM account WHERE parentaccountid IS NULL");

        // Act
        var result = parser.Parse();

        // Assert
        var condition = result.Where as SqlNullCondition;
        condition.Should().NotBeNull();
        condition!.IsNegated.Should().BeFalse();
    }

    [Fact]
    public void Parse_WhereIsNotNull_CreatesNegatedNullCondition()
    {
        // Arrange
        var parser = new SqlParser("SELECT name FROM account WHERE parentaccountid IS NOT NULL");

        // Act
        var result = parser.Parse();

        // Assert
        var condition = result.Where as SqlNullCondition;
        condition.Should().NotBeNull();
        condition!.IsNegated.Should().BeTrue();
    }

    [Fact]
    public void Parse_WhereLike_CreatesLikeCondition()
    {
        // Arrange
        var parser = new SqlParser("SELECT name FROM account WHERE name LIKE '%contoso%'");

        // Act
        var result = parser.Parse();

        // Assert
        var condition = result.Where as SqlLikeCondition;
        condition.Should().NotBeNull();
        condition!.Pattern.Should().Be("%contoso%");
    }

    [Fact]
    public void Parse_WhereIn_CreatesInCondition()
    {
        // Arrange
        var parser = new SqlParser("SELECT name FROM account WHERE statecode IN (0, 1, 2)");

        // Act
        var result = parser.Parse();

        // Assert
        var condition = result.Where as SqlInCondition;
        condition.Should().NotBeNull();
        condition!.Values.Should().HaveCount(3);
    }

    [Fact]
    public void Parse_WhereWithParentheses_HandlesPrecedence()
    {
        // Arrange
        var parser = new SqlParser("SELECT name FROM account WHERE (statecode = 0 OR statecode = 1) AND revenue > 1000");

        // Act
        var result = parser.Parse();

        // Assert
        result.Where.Should().NotBeNull();
        var topCondition = result.Where as SqlLogicalCondition;
        topCondition.Should().NotBeNull();
        topCondition!.Operator.Should().Be(SqlLogicalOperator.And);
    }

    #endregion

    #region ORDER BY

    [Fact]
    public void Parse_OrderByAsc_CapturesOrderBy()
    {
        // Arrange
        var parser = new SqlParser("SELECT name FROM account ORDER BY name ASC");

        // Act
        var result = parser.Parse();

        // Assert
        result.OrderBy.Should().HaveCount(1);
        result.OrderBy[0].Direction.Should().Be(SqlSortDirection.Ascending);
    }

    [Fact]
    public void Parse_OrderByDesc_CapturesOrderBy()
    {
        // Arrange
        var parser = new SqlParser("SELECT name FROM account ORDER BY revenue DESC");

        // Act
        var result = parser.Parse();

        // Assert
        result.OrderBy.Should().HaveCount(1);
        result.OrderBy[0].Direction.Should().Be(SqlSortDirection.Descending);
    }

    [Fact]
    public void Parse_OrderByMultiple_CapturesAllColumns()
    {
        // Arrange
        var parser = new SqlParser("SELECT name FROM account ORDER BY statecode ASC, name DESC");

        // Act
        var result = parser.Parse();

        // Assert
        result.OrderBy.Should().HaveCount(2);
    }

    [Fact]
    public void Parse_OrderByDefaultAsc_WhenNoDirectionSpecified()
    {
        // Arrange
        var parser = new SqlParser("SELECT name FROM account ORDER BY name");

        // Act
        var result = parser.Parse();

        // Assert
        result.OrderBy.Should().HaveCount(1);
        result.OrderBy[0].Direction.Should().Be(SqlSortDirection.Ascending);
    }

    #endregion

    #region JOIN

    [Fact]
    public void Parse_InnerJoin_CreatesJoinClause()
    {
        // Arrange
        var parser = new SqlParser("SELECT a.name FROM account a INNER JOIN contact c ON a.accountid = c.parentcustomerid");

        // Act
        var result = parser.Parse();

        // Assert
        result.Joins.Should().HaveCount(1);
        result.Joins[0].Type.Should().Be(SqlJoinType.Inner);
        result.Joins[0].Table.TableName.Should().Be("contact");
        result.Joins[0].Table.Alias.Should().Be("c");
    }

    [Fact]
    public void Parse_LeftJoin_CreatesJoinClause()
    {
        // Arrange
        var parser = new SqlParser("SELECT a.name FROM account a LEFT JOIN contact c ON a.accountid = c.parentcustomerid");

        // Act
        var result = parser.Parse();

        // Assert
        result.Joins.Should().HaveCount(1);
        result.Joins[0].Type.Should().Be(SqlJoinType.Left);
    }

    [Fact]
    public void Parse_RightJoin_CreatesJoinClause()
    {
        // Arrange
        var parser = new SqlParser("SELECT a.name FROM account a RIGHT JOIN contact c ON a.accountid = c.parentcustomerid");

        // Act
        var result = parser.Parse();

        // Assert
        result.Joins.Should().HaveCount(1);
        result.Joins[0].Type.Should().Be(SqlJoinType.Right);
    }

    [Fact]
    public void Parse_MultipleJoins_CapturesAll()
    {
        // Arrange
        var parser = new SqlParser(@"
            SELECT a.name
            FROM account a
            INNER JOIN contact c ON a.accountid = c.parentcustomerid
            LEFT JOIN systemuser u ON a.ownerid = u.systemuserid");

        // Act
        var result = parser.Parse();

        // Assert
        result.Joins.Should().HaveCount(2);
    }

    #endregion

    #region Aggregates

    [Fact]
    public void Parse_CountStar_CreatesAggregateColumn()
    {
        // Arrange
        var parser = new SqlParser("SELECT COUNT(*) FROM account");

        // Act
        var result = parser.Parse();

        // Assert
        result.HasAggregates().Should().BeTrue();
        var aggregate = result.Columns[0] as SqlAggregateColumn;
        aggregate.Should().NotBeNull();
        aggregate!.Function.Should().Be(SqlAggregateFunction.Count);
        aggregate.IsCountAll.Should().BeTrue();
    }

    [Fact]
    public void Parse_CountColumn_CreatesAggregateColumn()
    {
        // Arrange
        var parser = new SqlParser("SELECT COUNT(accountid) FROM account");

        // Act
        var result = parser.Parse();

        // Assert
        var aggregate = result.Columns[0] as SqlAggregateColumn;
        aggregate.Should().NotBeNull();
        aggregate!.Function.Should().Be(SqlAggregateFunction.Count);
        aggregate.IsCountAll.Should().BeFalse();
    }

    [Fact]
    public void Parse_CountDistinct_SetsDistinctFlag()
    {
        // Arrange
        var parser = new SqlParser("SELECT COUNT(DISTINCT ownerid) FROM account");

        // Act
        var result = parser.Parse();

        // Assert
        var aggregate = result.Columns[0] as SqlAggregateColumn;
        aggregate.Should().NotBeNull();
        aggregate!.IsDistinct.Should().BeTrue();
    }

    [Theory]
    [InlineData("SUM(revenue)", SqlAggregateFunction.Sum)]
    [InlineData("AVG(revenue)", SqlAggregateFunction.Avg)]
    [InlineData("MIN(revenue)", SqlAggregateFunction.Min)]
    [InlineData("MAX(revenue)", SqlAggregateFunction.Max)]
    public void Parse_AggregateFunction_ReturnsCorrectFunction(string aggregate, SqlAggregateFunction expected)
    {
        // Arrange
        var parser = new SqlParser($"SELECT {aggregate} FROM account");

        // Act
        var result = parser.Parse();

        // Assert
        var agg = result.Columns[0] as SqlAggregateColumn;
        agg.Should().NotBeNull();
        agg!.Function.Should().Be(expected);
    }

    [Fact]
    public void Parse_AggregateWithAlias_CapturesAlias()
    {
        // Arrange
        var parser = new SqlParser("SELECT COUNT(*) AS total FROM account");

        // Act
        var result = parser.Parse();

        // Assert
        var aggregate = result.Columns[0] as SqlAggregateColumn;
        aggregate.Should().NotBeNull();
        aggregate!.Alias.Should().Be("total");
    }

    #endregion

    #region GROUP BY

    [Fact]
    public void Parse_GroupBy_CapturesGroupByColumns()
    {
        // Arrange
        var parser = new SqlParser("SELECT statecode, COUNT(*) FROM account GROUP BY statecode");

        // Act
        var result = parser.Parse();

        // Assert
        result.GroupBy.Should().HaveCount(1);
        result.GroupBy[0].ColumnName.Should().Be("statecode");
    }

    [Fact]
    public void Parse_GroupByMultiple_CapturesAllColumns()
    {
        // Arrange
        var parser = new SqlParser("SELECT statecode, ownerid, COUNT(*) FROM account GROUP BY statecode, ownerid");

        // Act
        var result = parser.Parse();

        // Assert
        result.GroupBy.Should().HaveCount(2);
    }

    #endregion

    #region Error Handling

    [Fact]
    public void Parse_MissingFrom_ThrowsSqlParseException()
    {
        // Arrange
        var parser = new SqlParser("SELECT name");

        // Act & Assert
        var ex = Assert.Throws<SqlParseException>(() => parser.Parse());
        ex.Message.ToUpperInvariant().Should().Contain("FROM");
    }

    [Fact]
    public void Parse_InvalidSyntax_ThrowsSqlParseException()
    {
        // Arrange
        var parser = new SqlParser("SELECT FROM account");

        // Act & Assert
        Assert.Throws<SqlParseException>(() => parser.Parse());
    }

    [Fact]
    public void Parse_EmptyQuery_ThrowsSqlParseException()
    {
        // Arrange
        var parser = new SqlParser("");

        // Act & Assert
        Assert.Throws<SqlParseException>(() => parser.Parse());
    }

    [Fact]
    public void Parse_Exception_IncludesPosition()
    {
        // Arrange
        var parser = new SqlParser("SELECT name FORM account"); // FORM instead of FROM

        // Act & Assert
        var ex = Assert.Throws<SqlParseException>(() => parser.Parse());
        ex.Position.Should().BeGreaterThan(0);
    }

    #endregion

    #region Complex Queries

    [Fact]
    public void Parse_ComplexQuery_HandlesAllClauses()
    {
        // Arrange
        var sql = @"
            SELECT DISTINCT TOP 100
                a.name,
                a.revenue,
                c.fullname AS contactname
            FROM account a
            INNER JOIN contact c ON a.primarycontactid = c.contactid
            WHERE a.statecode = 0
              AND a.revenue > 1000000
            ORDER BY a.revenue DESC, a.name ASC";

        var parser = new SqlParser(sql);

        // Act
        var result = parser.Parse();

        // Assert
        result.Distinct.Should().BeTrue();
        result.Top.Should().Be(100);
        result.Columns.Should().HaveCount(3);
        result.From.TableName.Should().Be("account");
        result.From.Alias.Should().Be("a");
        result.Joins.Should().HaveCount(1);
        result.Where.Should().NotBeNull();
        result.OrderBy.Should().HaveCount(2);
    }

    [Fact]
    public void Parse_AggregateWithGroupBy_HandlesCorrectly()
    {
        // Arrange
        var sql = @"
            SELECT
                statecode,
                COUNT(*) AS cnt,
                SUM(revenue) AS total_revenue
            FROM account
            GROUP BY statecode
            ORDER BY cnt DESC";

        var parser = new SqlParser(sql);

        // Act
        var result = parser.Parse();

        // Assert
        result.HasAggregates().Should().BeTrue();
        result.GroupBy.Should().HaveCount(1);
        result.OrderBy.Should().HaveCount(1);
    }

    #endregion
}

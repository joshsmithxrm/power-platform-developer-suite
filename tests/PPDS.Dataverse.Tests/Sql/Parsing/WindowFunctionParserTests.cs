using FluentAssertions;
using PPDS.Dataverse.Sql.Ast;
using PPDS.Dataverse.Sql.Parsing;
using Xunit;

namespace PPDS.Dataverse.Tests.Sql.Parsing;

[Trait("Category", "TuiUnit")]
public class WindowFunctionParserTests
{
    [Fact]
    public void Parse_RowNumber_WithOrderBy()
    {
        var sql = "SELECT name, ROW_NUMBER() OVER (ORDER BY revenue DESC) AS rn FROM account";

        var result = SqlParser.Parse(sql);

        result.Columns.Should().HaveCount(2);

        // First column: name
        var nameCol = (SqlColumnRef)result.Columns[0];
        nameCol.ColumnName.Should().Be("name");

        // Second column: ROW_NUMBER() OVER (ORDER BY revenue DESC) AS rn
        var computed = (SqlComputedColumn)result.Columns[1];
        computed.Alias.Should().Be("rn");

        var windowExpr = (SqlWindowExpression)computed.Expression;
        windowExpr.FunctionName.Should().Be("ROW_NUMBER");
        windowExpr.Operand.Should().BeNull();
        windowExpr.PartitionBy.Should().BeNull();
        windowExpr.OrderBy.Should().HaveCount(1);
        windowExpr.OrderBy![0].Column.ColumnName.Should().Be("revenue");
        windowExpr.OrderBy[0].Direction.Should().Be(SqlSortDirection.Descending);
    }

    [Fact]
    public void Parse_SumOver_WithPartitionBy()
    {
        var sql = "SELECT name, SUM(revenue) OVER (PARTITION BY industrycode) AS total_revenue FROM account";

        var result = SqlParser.Parse(sql);

        result.Columns.Should().HaveCount(2);

        var computed = (SqlComputedColumn)result.Columns[1];
        computed.Alias.Should().Be("total_revenue");

        var windowExpr = (SqlWindowExpression)computed.Expression;
        windowExpr.FunctionName.Should().Be("SUM");

        // Operand should be a column reference to "revenue"
        var operand = (SqlColumnExpression)windowExpr.Operand;
        operand.Column.ColumnName.Should().Be("revenue");

        // PARTITION BY industrycode
        windowExpr.PartitionBy.Should().HaveCount(1);
        var partCol = (SqlColumnExpression)windowExpr.PartitionBy![0];
        partCol.Column.ColumnName.Should().Be("industrycode");

        // No ORDER BY
        windowExpr.OrderBy.Should().BeNull();
    }

    [Fact]
    public void Parse_Rank_WithPartitionAndOrderBy()
    {
        var sql = "SELECT name, RANK() OVER (PARTITION BY ownerid ORDER BY createdon) AS rnk FROM account";

        var result = SqlParser.Parse(sql);

        result.Columns.Should().HaveCount(2);

        var computed = (SqlComputedColumn)result.Columns[1];
        computed.Alias.Should().Be("rnk");

        var windowExpr = (SqlWindowExpression)computed.Expression;
        windowExpr.FunctionName.Should().Be("RANK");
        windowExpr.Operand.Should().BeNull();

        windowExpr.PartitionBy.Should().HaveCount(1);
        var partCol = (SqlColumnExpression)windowExpr.PartitionBy![0];
        partCol.Column.ColumnName.Should().Be("ownerid");

        windowExpr.OrderBy.Should().HaveCount(1);
        windowExpr.OrderBy![0].Column.ColumnName.Should().Be("createdon");
        windowExpr.OrderBy[0].Direction.Should().Be(SqlSortDirection.Ascending);
    }

    [Fact]
    public void Parse_DenseRank_WithOrderByDesc()
    {
        var sql = "SELECT name, DENSE_RANK() OVER (ORDER BY revenue DESC) AS dr FROM account";

        var result = SqlParser.Parse(sql);

        result.Columns.Should().HaveCount(2);

        var computed = (SqlComputedColumn)result.Columns[1];
        computed.Alias.Should().Be("dr");

        var windowExpr = (SqlWindowExpression)computed.Expression;
        windowExpr.FunctionName.Should().Be("DENSE_RANK");
        windowExpr.Operand.Should().BeNull();
        windowExpr.PartitionBy.Should().BeNull();
        windowExpr.OrderBy.Should().HaveCount(1);
        windowExpr.OrderBy![0].Column.ColumnName.Should().Be("revenue");
        windowExpr.OrderBy[0].Direction.Should().Be(SqlSortDirection.Descending);
    }

    [Fact]
    public void Parse_CountStarOver_WithPartitionBy()
    {
        var sql = "SELECT name, COUNT(*) OVER (PARTITION BY statecode) AS cnt FROM account";

        var result = SqlParser.Parse(sql);

        result.Columns.Should().HaveCount(2);

        var computed = (SqlComputedColumn)result.Columns[1];
        computed.Alias.Should().Be("cnt");

        var windowExpr = (SqlWindowExpression)computed.Expression;
        windowExpr.FunctionName.Should().Be("COUNT");
        windowExpr.IsCountStar.Should().BeTrue();
        windowExpr.Operand.Should().BeNull();

        windowExpr.PartitionBy.Should().HaveCount(1);
        var partCol = (SqlColumnExpression)windowExpr.PartitionBy![0];
        partCol.Column.ColumnName.Should().Be("statecode");
    }

    [Fact]
    public void Parse_WindowFunction_WithAlias()
    {
        var sql = "SELECT ROW_NUMBER() OVER (ORDER BY name ASC) AS row_num FROM account";

        var result = SqlParser.Parse(sql);

        result.Columns.Should().HaveCount(1);

        var computed = (SqlComputedColumn)result.Columns[0];
        computed.Alias.Should().Be("row_num");

        var windowExpr = (SqlWindowExpression)computed.Expression;
        windowExpr.FunctionName.Should().Be("ROW_NUMBER");
        windowExpr.OrderBy.Should().HaveCount(1);
        windowExpr.OrderBy![0].Column.ColumnName.Should().Be("name");
        windowExpr.OrderBy[0].Direction.Should().Be(SqlSortDirection.Ascending);
    }

    [Fact]
    public void Parse_AggregateWithoutOver_IsNotWindowFunction()
    {
        // Regular aggregate, not a window function
        var sql = "SELECT COUNT(*) AS cnt FROM account";

        var result = SqlParser.Parse(sql);

        result.Columns.Should().HaveCount(1);

        // Should be SqlAggregateColumn, not SqlComputedColumn with SqlWindowExpression
        var aggCol = (SqlAggregateColumn)result.Columns[0];
        aggCol.Function.Should().Be(SqlAggregateFunction.Count);
    }

    [Fact]
    public void Parse_MultipleWindowFunctions()
    {
        var sql = @"SELECT name,
            ROW_NUMBER() OVER (ORDER BY revenue DESC) AS rn,
            RANK() OVER (ORDER BY revenue DESC) AS rnk,
            DENSE_RANK() OVER (ORDER BY revenue DESC) AS dr
            FROM account";

        var result = SqlParser.Parse(sql);

        result.Columns.Should().HaveCount(4);

        // name
        result.Columns[0].Should().BeOfType<SqlColumnRef>();

        // ROW_NUMBER
        var rn = (SqlWindowExpression)((SqlComputedColumn)result.Columns[1]).Expression;
        rn.FunctionName.Should().Be("ROW_NUMBER");

        // RANK
        var rnk = (SqlWindowExpression)((SqlComputedColumn)result.Columns[2]).Expression;
        rnk.FunctionName.Should().Be("RANK");

        // DENSE_RANK
        var dr = (SqlWindowExpression)((SqlComputedColumn)result.Columns[3]).Expression;
        dr.FunctionName.Should().Be("DENSE_RANK");
    }

    [Fact]
    public void Parse_AvgOver_WithPartitionBy()
    {
        var sql = "SELECT name, AVG(revenue) OVER (PARTITION BY industrycode) AS avg_rev FROM account";

        var result = SqlParser.Parse(sql);

        result.Columns.Should().HaveCount(2);

        var computed = (SqlComputedColumn)result.Columns[1];
        computed.Alias.Should().Be("avg_rev");

        var windowExpr = (SqlWindowExpression)computed.Expression;
        windowExpr.FunctionName.Should().Be("AVG");

        var operand = (SqlColumnExpression)windowExpr.Operand;
        operand.Column.ColumnName.Should().Be("revenue");

        windowExpr.PartitionBy.Should().HaveCount(1);
    }

    [Fact]
    public void Parse_MinMaxOver_WithPartitionBy()
    {
        var sql = "SELECT MIN(revenue) OVER (PARTITION BY industrycode) AS min_rev, MAX(revenue) OVER (PARTITION BY industrycode) AS max_rev FROM account";

        var result = SqlParser.Parse(sql);

        result.Columns.Should().HaveCount(2);

        var minComputed = (SqlComputedColumn)result.Columns[0];
        var minWindow = (SqlWindowExpression)minComputed.Expression;
        minWindow.FunctionName.Should().Be("MIN");

        var maxComputed = (SqlComputedColumn)result.Columns[1];
        var maxWindow = (SqlWindowExpression)maxComputed.Expression;
        maxWindow.FunctionName.Should().Be("MAX");
    }
}

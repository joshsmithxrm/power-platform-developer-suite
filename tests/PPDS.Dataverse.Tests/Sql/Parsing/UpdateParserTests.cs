using PPDS.Dataverse.Sql.Ast;
using PPDS.Dataverse.Sql.Parsing;
using Xunit;

namespace PPDS.Dataverse.Tests.Sql.Parsing;

[Trait("Category", "TuiUnit")]
public class UpdateParserTests
{
    [Fact]
    public void ParsesSimpleUpdate()
    {
        var sql = "UPDATE account SET name = 'Updated' WHERE statecode = 0";
        var statement = SqlParser.ParseSql(sql);

        var update = Assert.IsType<SqlUpdateStatement>(statement);
        Assert.Equal("account", update.TargetTable.TableName);
        Assert.Single(update.SetClauses);
        Assert.Equal("name", update.SetClauses[0].ColumnName);
        var valueExpr = Assert.IsType<SqlLiteralExpression>(update.SetClauses[0].Value);
        Assert.Equal("Updated", valueExpr.Value.Value);
        Assert.NotNull(update.Where);
    }

    [Fact]
    public void ParsesUpdateWithMultipleSetClauses()
    {
        var sql = "UPDATE account SET name = 'Updated', revenue = 0 WHERE statecode = 0";
        var statement = SqlParser.ParseSql(sql);

        var update = Assert.IsType<SqlUpdateStatement>(statement);
        Assert.Equal(2, update.SetClauses.Count);
        Assert.Equal("name", update.SetClauses[0].ColumnName);
        Assert.Equal("revenue", update.SetClauses[1].ColumnName);
    }

    [Fact]
    public void ParsesUpdateWithComputedExpression()
    {
        var sql = "UPDATE account SET revenue = revenue * 1.1 WHERE statecode = 0";
        var statement = SqlParser.ParseSql(sql);

        var update = Assert.IsType<SqlUpdateStatement>(statement);
        Assert.Single(update.SetClauses);
        Assert.Equal("revenue", update.SetClauses[0].ColumnName);
        var binExpr = Assert.IsType<SqlBinaryExpression>(update.SetClauses[0].Value);
        Assert.Equal(SqlBinaryOperator.Multiply, binExpr.Operator);
    }

    [Fact]
    public void ParsesUpdateWithAlias()
    {
        var sql = "UPDATE account SET name = 'Updated' WHERE statecode = 0";
        var statement = SqlParser.ParseSql(sql);

        var update = Assert.IsType<SqlUpdateStatement>(statement);
        Assert.Equal("account", update.TargetTable.TableName);
    }

    [Fact]
    public void ParsesUpdateWithFromJoin()
    {
        var sql = "UPDATE a SET a.name = 'X' FROM account a JOIN contact c ON a.accountid = c.parentcustomerid WHERE c.statecode = 0";
        var statement = SqlParser.ParseSql(sql);

        var update = Assert.IsType<SqlUpdateStatement>(statement);
        Assert.Equal("a", update.TargetTable.TableName);
        Assert.NotNull(update.FromTable);
        Assert.Equal("account", update.FromTable!.TableName);
        Assert.NotNull(update.Joins);
        Assert.Single(update.Joins!);
        Assert.NotNull(update.Where);
    }

    [Fact]
    public void ParseError_UpdateWithoutWhere()
    {
        var sql = "UPDATE account SET name = 'Updated'";
        var ex = Assert.Throws<SqlParseException>(() => SqlParser.ParseSql(sql));
        Assert.Contains("UPDATE without WHERE is not allowed", ex.Message);
        Assert.Contains("Add a WHERE clause", ex.Message);
    }

    [Fact]
    public void UpdateIsCaseInsensitive()
    {
        var sql = "update account set name = 'Updated' where statecode = 0";
        var statement = SqlParser.ParseSql(sql);

        var update = Assert.IsType<SqlUpdateStatement>(statement);
        Assert.Equal("account", update.TargetTable.TableName);
    }

    [Fact]
    public void ParsesUpdateWithNullValue()
    {
        var sql = "UPDATE account SET revenue = NULL WHERE statecode = 0";
        var statement = SqlParser.ParseSql(sql);

        var update = Assert.IsType<SqlUpdateStatement>(statement);
        var valueExpr = Assert.IsType<SqlLiteralExpression>(update.SetClauses[0].Value);
        Assert.Equal(SqlLiteralType.Null, valueExpr.Value.Type);
    }

    [Fact]
    public void UpdateHasCorrectSourcePosition()
    {
        var sql = "UPDATE account SET name = 'Updated' WHERE statecode = 0";
        var statement = SqlParser.ParseSql(sql);

        var update = Assert.IsType<SqlUpdateStatement>(statement);
        Assert.Equal(0, update.SourcePosition);
    }

    [Fact]
    public void ParsesUpdateWithFunctionExpression()
    {
        var sql = "UPDATE account SET name = UPPER(name) WHERE statecode = 0";
        var statement = SqlParser.ParseSql(sql);

        var update = Assert.IsType<SqlUpdateStatement>(statement);
        var funcExpr = Assert.IsType<SqlFunctionExpression>(update.SetClauses[0].Value);
        Assert.Equal("UPPER", funcExpr.FunctionName);
    }
}

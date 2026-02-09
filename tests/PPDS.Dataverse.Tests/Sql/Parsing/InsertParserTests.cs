using PPDS.Dataverse.Sql.Ast;
using PPDS.Dataverse.Sql.Parsing;
using Xunit;

namespace PPDS.Dataverse.Tests.Sql.Parsing;

[Trait("Category", "TuiUnit")]
public class InsertParserTests
{
    [Fact]
    public void ParsesInsertValues_SingleRow()
    {
        var sql = "INSERT INTO account (name, revenue) VALUES ('Contoso', 1000000)";
        var statement = SqlParser.ParseSql(sql);

        var insert = Assert.IsType<SqlInsertStatement>(statement);
        Assert.Equal("account", insert.TargetEntity);
        Assert.Equal(2, insert.Columns.Count);
        Assert.Equal("name", insert.Columns[0]);
        Assert.Equal("revenue", insert.Columns[1]);
        Assert.NotNull(insert.ValueRows);
        Assert.Single(insert.ValueRows!);
        Assert.Equal(2, insert.ValueRows![0].Count);

        // First value: string literal 'Contoso'
        var nameExpr = Assert.IsType<SqlLiteralExpression>(insert.ValueRows[0][0]);
        Assert.Equal("Contoso", nameExpr.Value.Value);

        // Second value: numeric literal 1000000
        var revExpr = Assert.IsType<SqlLiteralExpression>(insert.ValueRows[0][1]);
        Assert.Equal("1000000", revExpr.Value.Value);

        Assert.Null(insert.SourceQuery);
    }

    [Fact]
    public void ParsesInsertValues_MultipleRows()
    {
        var sql = "INSERT INTO account (name, revenue) VALUES ('Contoso', 1000000), ('Fabrikam', 2000000)";
        var statement = SqlParser.ParseSql(sql);

        var insert = Assert.IsType<SqlInsertStatement>(statement);
        Assert.Equal("account", insert.TargetEntity);
        Assert.NotNull(insert.ValueRows);
        Assert.Equal(2, insert.ValueRows!.Count);

        // First row
        var row1Name = Assert.IsType<SqlLiteralExpression>(insert.ValueRows[0][0]);
        Assert.Equal("Contoso", row1Name.Value.Value);

        // Second row
        var row2Name = Assert.IsType<SqlLiteralExpression>(insert.ValueRows[1][0]);
        Assert.Equal("Fabrikam", row2Name.Value.Value);
    }

    [Fact]
    public void ParsesInsertValues_WithNull()
    {
        var sql = "INSERT INTO account (name, revenue) VALUES ('Contoso', NULL)";
        var statement = SqlParser.ParseSql(sql);

        var insert = Assert.IsType<SqlInsertStatement>(statement);
        var revExpr = Assert.IsType<SqlLiteralExpression>(insert.ValueRows![0][1]);
        Assert.Equal(SqlLiteralType.Null, revExpr.Value.Type);
    }

    [Fact]
    public void ParsesInsertValues_WithNegativeNumber()
    {
        var sql = "INSERT INTO account (name, revenue) VALUES ('Contoso', -500)";
        var statement = SqlParser.ParseSql(sql);

        var insert = Assert.IsType<SqlInsertStatement>(statement);
        var revExpr = Assert.IsType<SqlLiteralExpression>(insert.ValueRows![0][1]);
        Assert.Equal("-500", revExpr.Value.Value);
    }

    [Fact]
    public void ParsesInsertSelect()
    {
        var sql = "INSERT INTO account (name) SELECT fullname FROM contact WHERE statecode = 0";
        var statement = SqlParser.ParseSql(sql);

        var insert = Assert.IsType<SqlInsertStatement>(statement);
        Assert.Equal("account", insert.TargetEntity);
        Assert.Single(insert.Columns);
        Assert.Equal("name", insert.Columns[0]);
        Assert.Null(insert.ValueRows);
        Assert.NotNull(insert.SourceQuery);
        Assert.Equal("contact", insert.SourceQuery!.From.TableName);
        Assert.NotNull(insert.SourceQuery.Where);
    }

    [Fact]
    public void ParsesInsertSelect_MultipleColumns()
    {
        var sql = "INSERT INTO account (name, revenue) SELECT fullname, annualincome FROM contact";
        var statement = SqlParser.ParseSql(sql);

        var insert = Assert.IsType<SqlInsertStatement>(statement);
        Assert.Equal(2, insert.Columns.Count);
        Assert.NotNull(insert.SourceQuery);
        Assert.Equal(2, insert.SourceQuery!.Columns.Count);
    }

    [Fact]
    public void ParseError_InsertValuesCountMismatch()
    {
        var sql = "INSERT INTO account (name, revenue) VALUES ('Contoso')";
        Assert.Throws<SqlParseException>(() => SqlParser.ParseSql(sql));
    }

    [Fact]
    public void ParseError_InsertWithoutColumnsIsError()
    {
        // Missing column list
        var sql = "INSERT INTO account VALUES ('Contoso')";
        Assert.Throws<SqlParseException>(() => SqlParser.ParseSql(sql));
    }

    [Fact]
    public void InsertIsCaseInsensitive()
    {
        var sql = "insert into account (name) values ('Contoso')";
        var statement = SqlParser.ParseSql(sql);

        var insert = Assert.IsType<SqlInsertStatement>(statement);
        Assert.Equal("account", insert.TargetEntity);
    }

    [Fact]
    public void ParsesInsertValues_WithExpression()
    {
        var sql = "INSERT INTO account (name, revenue) VALUES ('Contoso', 100 * 10)";
        var statement = SqlParser.ParseSql(sql);

        var insert = Assert.IsType<SqlInsertStatement>(statement);
        var revExpr = Assert.IsType<SqlBinaryExpression>(insert.ValueRows![0][1]);
        Assert.Equal(SqlBinaryOperator.Multiply, revExpr.Operator);
    }

    [Fact]
    public void InsertHasCorrectSourcePosition()
    {
        var sql = "INSERT INTO account (name) VALUES ('Contoso')";
        var statement = SqlParser.ParseSql(sql);

        var insert = Assert.IsType<SqlInsertStatement>(statement);
        Assert.Equal(0, insert.SourcePosition);
    }
}

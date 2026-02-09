using PPDS.Dataverse.Sql.Ast;
using PPDS.Dataverse.Sql.Parsing;
using Xunit;

namespace PPDS.Dataverse.Tests.Sql.Parsing;

[Trait("Category", "TuiUnit")]
public class VariableParserTests
{
    [Fact]
    public void ParsesDeclare_WithInitialValue()
    {
        var sql = "DECLARE @threshold MONEY = 1000000";
        var stmt = SqlParser.ParseSql(sql);

        var declare = Assert.IsType<SqlDeclareStatement>(stmt);
        Assert.Equal("@threshold", declare.VariableName);
        Assert.Equal("MONEY", declare.TypeName);
        Assert.NotNull(declare.InitialValue);

        var lit = Assert.IsType<SqlLiteralExpression>(declare.InitialValue);
        Assert.Equal("1000000", lit.Value.Value);
        Assert.Equal(SqlLiteralType.Number, lit.Value.Type);
    }

    [Fact]
    public void ParsesDeclare_WithoutInitialValue()
    {
        var sql = "DECLARE @name NVARCHAR(100)";
        var stmt = SqlParser.ParseSql(sql);

        var declare = Assert.IsType<SqlDeclareStatement>(stmt);
        Assert.Equal("@name", declare.VariableName);
        Assert.Equal("NVARCHAR(100)", declare.TypeName);
        Assert.Null(declare.InitialValue);
    }

    [Fact]
    public void ParsesDeclare_SimpleType()
    {
        var sql = "DECLARE @count INT";
        var stmt = SqlParser.ParseSql(sql);

        var declare = Assert.IsType<SqlDeclareStatement>(stmt);
        Assert.Equal("@count", declare.VariableName);
        Assert.Equal("INT", declare.TypeName);
        Assert.Null(declare.InitialValue);
    }

    [Fact]
    public void ParsesDeclare_WithStringInitialValue()
    {
        var sql = "DECLARE @prefix NVARCHAR(50) = 'contoso'";
        var stmt = SqlParser.ParseSql(sql);

        var declare = Assert.IsType<SqlDeclareStatement>(stmt);
        Assert.Equal("@prefix", declare.VariableName);
        Assert.Equal("NVARCHAR(50)", declare.TypeName);

        var lit = Assert.IsType<SqlLiteralExpression>(declare.InitialValue);
        Assert.Equal("contoso", lit.Value.Value);
        Assert.Equal(SqlLiteralType.String, lit.Value.Type);
    }

    [Fact]
    public void ParsesSetVariable()
    {
        var sql = "SET @threshold = 5000";
        var stmt = SqlParser.ParseSql(sql);

        var set = Assert.IsType<SqlSetVariableStatement>(stmt);
        Assert.Equal("@threshold", set.VariableName);

        var lit = Assert.IsType<SqlLiteralExpression>(set.Value);
        Assert.Equal("5000", lit.Value.Value);
    }

    [Fact]
    public void ParsesSetVariable_WithExpression()
    {
        var sql = "SET @total = 100 + 200";
        var stmt = SqlParser.ParseSql(sql);

        var set = Assert.IsType<SqlSetVariableStatement>(stmt);
        Assert.Equal("@total", set.VariableName);

        var bin = Assert.IsType<SqlBinaryExpression>(set.Value);
        Assert.Equal(SqlBinaryOperator.Add, bin.Operator);
    }

    [Fact]
    public void ParsesVariableInWhereCondition()
    {
        var sql = "SELECT name FROM account WHERE revenue > @threshold";
        var stmt = SqlParser.Parse(sql);

        Assert.Equal("account", stmt.From.TableName);

        // The right side is a variable expression, so it's an ExpressionCondition
        var exprCond = Assert.IsType<SqlExpressionCondition>(stmt.Where);
        Assert.Equal(SqlComparisonOperator.GreaterThan, exprCond.Operator);

        var leftCol = Assert.IsType<SqlColumnExpression>(exprCond.Left);
        Assert.Equal("revenue", leftCol.Column.ColumnName);

        var rightVar = Assert.IsType<SqlVariableExpression>(exprCond.Right);
        Assert.Equal("@threshold", rightVar.VariableName);
    }

    [Fact]
    public void ParsesVariableInSelectExpression()
    {
        var sql = "SELECT @count FROM account WHERE statecode = 0";
        var stmt = SqlParser.Parse(sql);

        // @count in SELECT list should become a SqlComputedColumn with SqlVariableExpression
        var computed = Assert.IsType<SqlComputedColumn>(stmt.Columns[0]);
        var varExpr = Assert.IsType<SqlVariableExpression>(computed.Expression);
        Assert.Equal("@count", varExpr.VariableName);
    }

    [Fact]
    public void LexerTokenizesVariable()
    {
        var lexer = new SqlLexer("@threshold");
        var result = lexer.Tokenize();

        Assert.Equal(SqlTokenType.Variable, result.Tokens[0].Type);
        Assert.Equal("@threshold", result.Tokens[0].Value);
    }

    [Fact]
    public void LexerTokenizesDeclareKeyword()
    {
        var lexer = new SqlLexer("DECLARE");
        var result = lexer.Tokenize();

        Assert.Equal(SqlTokenType.Declare, result.Tokens[0].Type);
    }

    [Fact]
    public void LexerTokenizesDeclareCaseInsensitive()
    {
        var lexer = new SqlLexer("declare");
        var result = lexer.Tokenize();

        Assert.Equal(SqlTokenType.Declare, result.Tokens[0].Type);
    }

    [Fact]
    public void LexerTokenizesVariableWithUnderscore()
    {
        var lexer = new SqlLexer("@my_var");
        var result = lexer.Tokenize();

        Assert.Equal(SqlTokenType.Variable, result.Tokens[0].Type);
        Assert.Equal("@my_var", result.Tokens[0].Value);
    }

    [Fact]
    public void ParsesVariableInSetClauseExpression()
    {
        var sql = "SET @result = @a + @b";
        var stmt = SqlParser.ParseSql(sql);

        var set = Assert.IsType<SqlSetVariableStatement>(stmt);
        Assert.Equal("@result", set.VariableName);

        var bin = Assert.IsType<SqlBinaryExpression>(set.Value);
        var leftVar = Assert.IsType<SqlVariableExpression>(bin.Left);
        Assert.Equal("@a", leftVar.VariableName);
        var rightVar = Assert.IsType<SqlVariableExpression>(bin.Right);
        Assert.Equal("@b", rightVar.VariableName);
    }
}

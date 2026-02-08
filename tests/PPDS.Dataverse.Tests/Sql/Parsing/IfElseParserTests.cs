using PPDS.Dataverse.Sql.Ast;
using PPDS.Dataverse.Sql.Parsing;
using Xunit;

namespace PPDS.Dataverse.Tests.Sql.Parsing;

[Trait("Category", "TuiUnit")]
public class IfElseParserTests
{
    [Fact]
    public void ParsesIfWithBeginEnd_ThenBlock()
    {
        var sql = "IF @count > 0 BEGIN SELECT name FROM account END";
        var stmt = SqlParser.ParseSql(sql);

        var ifStmt = Assert.IsType<SqlIfStatement>(stmt);

        // Verify condition: @count > 0
        var cond = Assert.IsType<SqlExpressionCondition>(ifStmt.Condition);
        Assert.Equal(SqlComparisonOperator.GreaterThan, cond.Operator);
        var leftVar = Assert.IsType<SqlVariableExpression>(cond.Left);
        Assert.Equal("@count", leftVar.VariableName);
        var rightLit = Assert.IsType<SqlLiteralExpression>(cond.Right);
        Assert.Equal("0", rightLit.Value.Value);

        // Verify THEN block has one SELECT statement
        Assert.Single(ifStmt.ThenBlock.Statements);
        var selectStmt = Assert.IsType<SqlSelectStatement>(ifStmt.ThenBlock.Statements[0]);
        Assert.Equal("account", selectStmt.From.TableName);

        // No ELSE block
        Assert.Null(ifStmt.ElseBlock);
    }

    [Fact]
    public void ParsesIfElse_WithBeginEnd()
    {
        var sql = "IF @x = 1 BEGIN SELECT 'one' FROM account END ELSE BEGIN SELECT 'other' FROM account END";
        var stmt = SqlParser.ParseSql(sql);

        var ifStmt = Assert.IsType<SqlIfStatement>(stmt);

        // Verify condition: @x = 1
        var cond = Assert.IsType<SqlExpressionCondition>(ifStmt.Condition);
        Assert.Equal(SqlComparisonOperator.Equal, cond.Operator);

        // Verify THEN block
        Assert.Single(ifStmt.ThenBlock.Statements);
        Assert.IsType<SqlSelectStatement>(ifStmt.ThenBlock.Statements[0]);

        // Verify ELSE block
        Assert.NotNull(ifStmt.ElseBlock);
        Assert.Single(ifStmt.ElseBlock!.Statements);
        Assert.IsType<SqlSelectStatement>(ifStmt.ElseBlock.Statements[0]);
    }

    [Fact]
    public void ParsesNestedIfElse()
    {
        var sql = @"IF @x = 1 BEGIN
            IF @y = 2 BEGIN
                SELECT 'inner' FROM account
            END
        END ELSE BEGIN
            SELECT 'outer_else' FROM account
        END";
        var stmt = SqlParser.ParseSql(sql);

        var outerIf = Assert.IsType<SqlIfStatement>(stmt);

        // THEN block contains a nested IF
        Assert.Single(outerIf.ThenBlock.Statements);
        var innerIf = Assert.IsType<SqlIfStatement>(outerIf.ThenBlock.Statements[0]);

        // Inner IF has a THEN block with a SELECT
        Assert.Single(innerIf.ThenBlock.Statements);
        Assert.IsType<SqlSelectStatement>(innerIf.ThenBlock.Statements[0]);
        Assert.Null(innerIf.ElseBlock);

        // Outer ELSE block has a SELECT
        Assert.NotNull(outerIf.ElseBlock);
        Assert.Single(outerIf.ElseBlock!.Statements);
        Assert.IsType<SqlSelectStatement>(outerIf.ElseBlock.Statements[0]);
    }

    [Fact]
    public void ParsesBeginEndBlock_WithMultipleStatements()
    {
        var sql = @"IF @flag = 1 BEGIN
            DECLARE @name NVARCHAR(100);
            SET @name = 'test';
            SELECT @name FROM account
        END";
        var stmt = SqlParser.ParseSql(sql);

        var ifStmt = Assert.IsType<SqlIfStatement>(stmt);

        // THEN block has 3 statements: DECLARE, SET, SELECT
        Assert.Equal(3, ifStmt.ThenBlock.Statements.Count);
        Assert.IsType<SqlDeclareStatement>(ifStmt.ThenBlock.Statements[0]);
        Assert.IsType<SqlSetVariableStatement>(ifStmt.ThenBlock.Statements[1]);
        Assert.IsType<SqlSelectStatement>(ifStmt.ThenBlock.Statements[2]);
    }

    [Fact]
    public void ParsesIfWithSimpleCondition_NoVariables()
    {
        var sql = "IF 1 = 1 BEGIN SELECT name FROM account END";
        var stmt = SqlParser.ParseSql(sql);

        var ifStmt = Assert.IsType<SqlIfStatement>(stmt);

        // Condition: 1 = 1 (both sides are expressions)
        var cond = Assert.IsType<SqlExpressionCondition>(ifStmt.Condition);
        Assert.Equal(SqlComparisonOperator.Equal, cond.Operator);

        var leftLit = Assert.IsType<SqlLiteralExpression>(cond.Left);
        Assert.Equal("1", leftLit.Value.Value);
        var rightLit = Assert.IsType<SqlLiteralExpression>(cond.Right);
        Assert.Equal("1", rightLit.Value.Value);
    }

    [Fact]
    public void ParsesMultiStatementScript_WithDeclareSetSelect()
    {
        var sql = @"DECLARE @threshold MONEY = 1000;
                     SET @threshold = 5000;
                     SELECT name FROM account";
        var stmt = SqlParser.ParseSql(sql);

        // Multi-statement → returns SqlBlockStatement
        var block = Assert.IsType<SqlBlockStatement>(stmt);
        Assert.Equal(3, block.Statements.Count);
        Assert.IsType<SqlDeclareStatement>(block.Statements[0]);
        Assert.IsType<SqlSetVariableStatement>(block.Statements[1]);
        Assert.IsType<SqlSelectStatement>(block.Statements[2]);
    }

    [Fact]
    public void ParsesMultiStatementScript_WithIfElse()
    {
        var sql = @"DECLARE @x INT = 1;
                     IF @x = 1 BEGIN
                         SELECT 'yes' FROM account
                     END ELSE BEGIN
                         SELECT 'no' FROM account
                     END";
        var stmt = SqlParser.ParseSql(sql);

        var block = Assert.IsType<SqlBlockStatement>(stmt);
        Assert.Equal(2, block.Statements.Count);
        Assert.IsType<SqlDeclareStatement>(block.Statements[0]);
        Assert.IsType<SqlIfStatement>(block.Statements[1]);
    }

    [Fact]
    public void LexerTokenizesIfKeyword()
    {
        var lexer = new SqlLexer("IF");
        var result = lexer.Tokenize();
        Assert.Equal(SqlTokenType.If, result.Tokens[0].Type);
    }

    [Fact]
    public void LexerTokenizesBeginKeyword()
    {
        var lexer = new SqlLexer("BEGIN");
        var result = lexer.Tokenize();
        Assert.Equal(SqlTokenType.Begin, result.Tokens[0].Type);
    }

    [Fact]
    public void LexerTokenizesSemicolon()
    {
        var lexer = new SqlLexer("SELECT 1; SELECT 2");
        var result = lexer.Tokenize();

        // Find the semicolon token
        var semicolonFound = false;
        foreach (var token in result.Tokens)
        {
            if (token.Type == SqlTokenType.Semicolon)
            {
                semicolonFound = true;
                break;
            }
        }
        Assert.True(semicolonFound, "Expected Semicolon token");
    }

    [Fact]
    public void ParsesIfWithCaseInsensitiveKeywords()
    {
        var sql = "if @x = 1 begin select name from account end";
        var stmt = SqlParser.ParseSql(sql);

        var ifStmt = Assert.IsType<SqlIfStatement>(stmt);
        Assert.Single(ifStmt.ThenBlock.Statements);
        Assert.IsType<SqlSelectStatement>(ifStmt.ThenBlock.Statements[0]);
    }

    [Fact]
    public void ParsesIfWithStringComparison()
    {
        var sql = "IF @status = 'active' BEGIN SELECT name FROM account END";
        var stmt = SqlParser.ParseSql(sql);

        var ifStmt = Assert.IsType<SqlIfStatement>(stmt);
        var cond = Assert.IsType<SqlExpressionCondition>(ifStmt.Condition);
        Assert.Equal(SqlComparisonOperator.Equal, cond.Operator);

        var rightLit = Assert.IsType<SqlLiteralExpression>(cond.Right);
        Assert.Equal("active", rightLit.Value.Value);
        Assert.Equal(SqlLiteralType.String, rightLit.Value.Type);
    }
}

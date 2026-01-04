using FluentAssertions;
using PPDS.Dataverse.Sql.Parsing;
using Xunit;

namespace PPDS.Dataverse.Tests.Sql.Parsing;

public class SqlLexerTests
{
    #region Basic Tokenization

    [Fact]
    public void Tokenize_SimpleSelect_ReturnsCorrectTokens()
    {
        // Arrange
        var sql = "SELECT name FROM account";

        // Act
        var result = new SqlLexer(sql).Tokenize();

        // Assert
        result.Tokens.Should().HaveCount(5); // SELECT, name, FROM, account, EOF
        result.Tokens[0].Type.Should().Be(SqlTokenType.Select);
        result.Tokens[1].Type.Should().Be(SqlTokenType.Identifier);
        result.Tokens[1].Value.Should().Be("name");
        result.Tokens[2].Type.Should().Be(SqlTokenType.From);
        result.Tokens[3].Type.Should().Be(SqlTokenType.Identifier);
        result.Tokens[3].Value.Should().Be("account");
        result.Tokens[4].Type.Should().Be(SqlTokenType.Eof);
    }

    [Fact]
    public void Tokenize_SelectStar_ReturnsStarToken()
    {
        // Arrange
        var sql = "SELECT * FROM account";

        // Act
        var result = new SqlLexer(sql).Tokenize();

        // Assert
        result.Tokens.Should().Contain(t => t.Type == SqlTokenType.Star);
    }

    [Fact]
    public void Tokenize_EmptyInput_ReturnsOnlyEof()
    {
        // Arrange
        var sql = "";

        // Act
        var result = new SqlLexer(sql).Tokenize();

        // Assert
        result.Tokens.Should().HaveCount(1);
        result.Tokens[0].Type.Should().Be(SqlTokenType.Eof);
    }

    #endregion

    #region Keywords

    [Theory]
    [InlineData("SELECT", SqlTokenType.Select)]
    [InlineData("FROM", SqlTokenType.From)]
    [InlineData("WHERE", SqlTokenType.Where)]
    [InlineData("AND", SqlTokenType.And)]
    [InlineData("OR", SqlTokenType.Or)]
    [InlineData("NOT", SqlTokenType.Not)]
    [InlineData("NULL", SqlTokenType.Null)]
    [InlineData("IS", SqlTokenType.Is)]
    [InlineData("IN", SqlTokenType.In)]
    [InlineData("LIKE", SqlTokenType.Like)]
    [InlineData("ORDER", SqlTokenType.Order)]
    [InlineData("BY", SqlTokenType.By)]
    [InlineData("ASC", SqlTokenType.Asc)]
    [InlineData("DESC", SqlTokenType.Desc)]
    [InlineData("TOP", SqlTokenType.Top)]
    [InlineData("DISTINCT", SqlTokenType.Distinct)]
    [InlineData("JOIN", SqlTokenType.Join)]
    [InlineData("INNER", SqlTokenType.Inner)]
    [InlineData("LEFT", SqlTokenType.Left)]
    [InlineData("RIGHT", SqlTokenType.Right)]
    [InlineData("ON", SqlTokenType.On)]
    [InlineData("AS", SqlTokenType.As)]
    [InlineData("GROUP", SqlTokenType.Group)]
    [InlineData("COUNT", SqlTokenType.Count)]
    [InlineData("SUM", SqlTokenType.Sum)]
    [InlineData("AVG", SqlTokenType.Avg)]
    [InlineData("MIN", SqlTokenType.Min)]
    [InlineData("MAX", SqlTokenType.Max)]
    public void Tokenize_Keyword_ReturnsCorrectType(string keyword, SqlTokenType expectedType)
    {
        // Act
        var result = new SqlLexer(keyword).Tokenize();

        // Assert
        result.Tokens[0].Type.Should().Be(expectedType);
    }

    [Fact]
    public void Tokenize_KeywordsAreCaseInsensitive()
    {
        // Arrange
        var sql1 = "SELECT";
        var sql2 = "select";
        var sql3 = "Select";

        // Act
        var result1 = new SqlLexer(sql1).Tokenize();
        var result2 = new SqlLexer(sql2).Tokenize();
        var result3 = new SqlLexer(sql3).Tokenize();

        // Assert
        result1.Tokens[0].Type.Should().Be(SqlTokenType.Select);
        result2.Tokens[0].Type.Should().Be(SqlTokenType.Select);
        result3.Tokens[0].Type.Should().Be(SqlTokenType.Select);
    }

    #endregion

    #region Operators

    [Theory]
    [InlineData("=", SqlTokenType.Equals)]
    [InlineData("<>", SqlTokenType.NotEquals)]
    [InlineData("!=", SqlTokenType.NotEquals)]
    [InlineData("<", SqlTokenType.LessThan)]
    [InlineData(">", SqlTokenType.GreaterThan)]
    [InlineData("<=", SqlTokenType.LessThanOrEqual)]
    [InlineData(">=", SqlTokenType.GreaterThanOrEqual)]
    public void Tokenize_Operator_ReturnsCorrectType(string op, SqlTokenType expectedType)
    {
        // Act
        var result = new SqlLexer(op).Tokenize();

        // Assert
        result.Tokens[0].Type.Should().Be(expectedType);
    }

    [Theory]
    [InlineData(",", SqlTokenType.Comma)]
    [InlineData(".", SqlTokenType.Dot)]
    [InlineData("(", SqlTokenType.LeftParen)]
    [InlineData(")", SqlTokenType.RightParen)]
    public void Tokenize_Punctuation_ReturnsCorrectType(string punct, SqlTokenType expectedType)
    {
        // Act
        var result = new SqlLexer(punct).Tokenize();

        // Assert
        result.Tokens[0].Type.Should().Be(expectedType);
    }

    #endregion

    #region String Literals

    [Fact]
    public void Tokenize_SingleQuotedString_ReturnsStringToken()
    {
        // Arrange
        var sql = "'hello world'";

        // Act
        var result = new SqlLexer(sql).Tokenize();

        // Assert
        result.Tokens[0].Type.Should().Be(SqlTokenType.String);
        result.Tokens[0].Value.Should().Be("hello world");
    }

    [Fact]
    public void Tokenize_EscapedQuoteInString_HandlesCorrectly()
    {
        // Arrange
        var sql = "'it''s a test'";

        // Act
        var result = new SqlLexer(sql).Tokenize();

        // Assert
        result.Tokens[0].Type.Should().Be(SqlTokenType.String);
        result.Tokens[0].Value.Should().Be("it's a test");
    }

    [Fact]
    public void Tokenize_EmptyString_ReturnsEmptyStringValue()
    {
        // Arrange
        var sql = "''";

        // Act
        var result = new SqlLexer(sql).Tokenize();

        // Assert
        result.Tokens[0].Type.Should().Be(SqlTokenType.String);
        result.Tokens[0].Value.Should().Be("");
    }

    #endregion

    #region Numbers

    [Fact]
    public void Tokenize_Integer_ReturnsNumberToken()
    {
        // Arrange
        var sql = "123";

        // Act
        var result = new SqlLexer(sql).Tokenize();

        // Assert
        result.Tokens[0].Type.Should().Be(SqlTokenType.Number);
        result.Tokens[0].Value.Should().Be("123");
    }

    [Fact]
    public void Tokenize_Decimal_ReturnsNumberToken()
    {
        // Arrange
        var sql = "123.456";

        // Act
        var result = new SqlLexer(sql).Tokenize();

        // Assert
        result.Tokens[0].Type.Should().Be(SqlTokenType.Number);
        result.Tokens[0].Value.Should().Be("123.456");
    }

    [Fact]
    public void Tokenize_NegativeNumber_ReturnsNumberToken()
    {
        // Arrange - negative numbers are parsed as minus + number in most SQL
        var sql = "-42";

        // Act
        var result = new SqlLexer(sql).Tokenize();

        // Assert - may be parsed as minus and number depending on implementation
        result.Tokens.Should().Contain(t => t.Type == SqlTokenType.Number);
    }

    #endregion

    #region Identifiers

    [Fact]
    public void Tokenize_SimpleIdentifier_ReturnsIdentifierToken()
    {
        // Arrange
        var sql = "accountid";

        // Act
        var result = new SqlLexer(sql).Tokenize();

        // Assert
        result.Tokens[0].Type.Should().Be(SqlTokenType.Identifier);
        result.Tokens[0].Value.Should().Be("accountid");
    }

    [Fact]
    public void Tokenize_BracketedIdentifier_ReturnsIdentifierToken()
    {
        // Arrange
        var sql = "[my column]";

        // Act
        var result = new SqlLexer(sql).Tokenize();

        // Assert
        result.Tokens[0].Type.Should().Be(SqlTokenType.Identifier);
        result.Tokens[0].Value.Should().Be("my column");
    }

    [Fact]
    public void Tokenize_IdentifierWithUnderscore_ReturnsIdentifierToken()
    {
        // Arrange
        var sql = "account_name";

        // Act
        var result = new SqlLexer(sql).Tokenize();

        // Assert
        result.Tokens[0].Type.Should().Be(SqlTokenType.Identifier);
        result.Tokens[0].Value.Should().Be("account_name");
    }

    [Fact]
    public void Tokenize_QualifiedColumn_ReturnsCorrectTokens()
    {
        // Arrange
        var sql = "a.name";

        // Act
        var result = new SqlLexer(sql).Tokenize();

        // Assert
        result.Tokens.Should().HaveCount(4); // a, ., name, EOF
        result.Tokens[0].Type.Should().Be(SqlTokenType.Identifier);
        result.Tokens[0].Value.Should().Be("a");
        result.Tokens[1].Type.Should().Be(SqlTokenType.Dot);
        result.Tokens[2].Type.Should().Be(SqlTokenType.Identifier);
        result.Tokens[2].Value.Should().Be("name");
    }

    #endregion

    #region Comments

    [Fact]
    public void Tokenize_LineComment_CapturesComment()
    {
        // Arrange
        var sql = "SELECT -- this is a comment\nname FROM account";

        // Act
        var result = new SqlLexer(sql).Tokenize();

        // Assert
        result.Comments.Should().HaveCount(1);
        result.Comments[0].Text.Should().Contain("this is a comment");
    }

    [Fact]
    public void Tokenize_BlockComment_CapturesComment()
    {
        // Arrange
        var sql = "SELECT /* block comment */ name FROM account";

        // Act
        var result = new SqlLexer(sql).Tokenize();

        // Assert
        result.Comments.Should().HaveCount(1);
        result.Comments[0].Text.Should().Contain("block comment");
    }

    [Fact]
    public void Tokenize_MultiLineBlockComment_CapturesComment()
    {
        // Arrange
        var sql = "SELECT /* multi\nline\ncomment */ name FROM account";

        // Act
        var result = new SqlLexer(sql).Tokenize();

        // Assert
        result.Comments.Should().HaveCount(1);
        result.Comments[0].Text.Should().Contain("multi");
        result.Comments[0].Text.Should().Contain("comment");
    }

    #endregion

    #region Complex Queries

    [Fact]
    public void Tokenize_WhereClause_ReturnsCorrectTokens()
    {
        // Arrange
        var sql = "SELECT name FROM account WHERE statecode = 0";

        // Act
        var result = new SqlLexer(sql).Tokenize();

        // Assert
        result.Tokens.Should().Contain(t => t.Type == SqlTokenType.Where);
        result.Tokens.Should().Contain(t => t.Type == SqlTokenType.Equals);
        result.Tokens.Should().Contain(t => t.Type == SqlTokenType.Number && t.Value == "0");
    }

    [Fact]
    public void Tokenize_JoinQuery_ReturnsCorrectTokens()
    {
        // Arrange
        var sql = "SELECT a.name FROM account a INNER JOIN contact c ON a.accountid = c.parentcustomerid";

        // Act
        var result = new SqlLexer(sql).Tokenize();

        // Assert
        result.Tokens.Should().Contain(t => t.Type == SqlTokenType.Inner);
        result.Tokens.Should().Contain(t => t.Type == SqlTokenType.Join);
        result.Tokens.Should().Contain(t => t.Type == SqlTokenType.On);
    }

    [Fact]
    public void Tokenize_AggregateQuery_ReturnsCorrectTokens()
    {
        // Arrange
        var sql = "SELECT COUNT(*), SUM(revenue) FROM account GROUP BY statecode";

        // Act
        var result = new SqlLexer(sql).Tokenize();

        // Assert
        result.Tokens.Should().Contain(t => t.Type == SqlTokenType.Count);
        result.Tokens.Should().Contain(t => t.Type == SqlTokenType.Sum);
        result.Tokens.Should().Contain(t => t.Type == SqlTokenType.Group);
        result.Tokens.Should().Contain(t => t.Type == SqlTokenType.By);
    }

    [Fact]
    public void Tokenize_InClause_ReturnsCorrectTokens()
    {
        // Arrange
        var sql = "SELECT name FROM account WHERE statecode IN (0, 1, 2)";

        // Act
        var result = new SqlLexer(sql).Tokenize();

        // Assert
        result.Tokens.Should().Contain(t => t.Type == SqlTokenType.In);
        result.Tokens.Should().Contain(t => t.Type == SqlTokenType.LeftParen);
        result.Tokens.Should().Contain(t => t.Type == SqlTokenType.RightParen);
    }

    [Fact]
    public void Tokenize_LikeClause_ReturnsCorrectTokens()
    {
        // Arrange
        var sql = "SELECT name FROM account WHERE name LIKE '%contoso%'";

        // Act
        var result = new SqlLexer(sql).Tokenize();

        // Assert
        result.Tokens.Should().Contain(t => t.Type == SqlTokenType.Like);
        result.Tokens.Should().Contain(t => t.Type == SqlTokenType.String && t.Value == "%contoso%");
    }

    #endregion

    #region Token Positions

    [Fact]
    public void Tokenize_TracksPositions()
    {
        // Arrange
        var sql = "SELECT name";

        // Act
        var result = new SqlLexer(sql).Tokenize();

        // Assert
        result.Tokens[0].Position.Should().Be(0); // SELECT
        result.Tokens[1].Position.Should().Be(7); // name
    }

    #endregion
}

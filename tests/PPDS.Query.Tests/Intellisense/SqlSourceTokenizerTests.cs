using FluentAssertions;
using PPDS.Dataverse.Sql.Intellisense;
using Xunit;

using SqlSourceTokenizer = PPDS.Query.Intellisense.SqlSourceTokenizer;

namespace PPDS.Query.Tests.Intellisense;

[Trait("Category", "Unit")]
public class SqlSourceTokenizerTests
{
    private readonly SqlSourceTokenizer _tokenizer = new();

    // ────────────────────────────────────────────
    //  Keywords
    // ────────────────────────────────────────────

    [Theory]
    [InlineData("SELECT")]
    [InlineData("FROM")]
    [InlineData("WHERE")]
    [InlineData("ORDER")]
    [InlineData("JOIN")]
    [InlineData("INSERT")]
    [InlineData("UPDATE")]
    [InlineData("DELETE")]
    public void Tokenize_Keywords_ClassifiedAsKeyword(string keyword)
    {
        var tokens = _tokenizer.Tokenize(keyword);

        tokens.Should().ContainSingle()
            .Which.Type.Should().Be(SourceTokenType.Keyword);
    }

    // ────────────────────────────────────────────
    //  Identifiers
    // ────────────────────────────────────────────

    [Fact]
    public void Tokenize_IdentifierAfterFrom_ClassifiedAsIdentifier()
    {
        var tokens = _tokenizer.Tokenize("FROM account");

        // "account" should be Identifier
        tokens.Should().Contain(t => t.Type == SourceTokenType.Identifier);
    }

    [Fact]
    public void Tokenize_ColumnName_ClassifiedAsIdentifier()
    {
        var tokens = _tokenizer.Tokenize("SELECT name FROM account");

        // "name" should be classified as Identifier
        tokens.Should().Contain(t => t.Type == SourceTokenType.Identifier);
    }

    // ────────────────────────────────────────────
    //  String literals
    // ────────────────────────────────────────────

    [Fact]
    public void Tokenize_SingleQuotedString_ClassifiedAsStringLiteral()
    {
        var tokens = _tokenizer.Tokenize("SELECT * FROM account WHERE name = 'Contoso'");

        tokens.Should().Contain(t => t.Type == SourceTokenType.StringLiteral);
    }

    [Fact]
    public void Tokenize_UnicodeString_ClassifiedAsStringLiteral()
    {
        var tokens = _tokenizer.Tokenize("SELECT * FROM account WHERE name = N'Contoso'");

        tokens.Should().Contain(t => t.Type == SourceTokenType.StringLiteral);
    }

    // ────────────────────────────────────────────
    //  Numbers
    // ────────────────────────────────────────────

    [Fact]
    public void Tokenize_IntegerLiteral_ClassifiedAsNumericLiteral()
    {
        var tokens = _tokenizer.Tokenize("SELECT TOP 10 name FROM account");

        tokens.Should().Contain(t => t.Type == SourceTokenType.NumericLiteral);
    }

    [Fact]
    public void Tokenize_DecimalLiteral_ClassifiedAsNumericLiteral()
    {
        var tokens = _tokenizer.Tokenize("SELECT * FROM account WHERE revenue > 1000.50");

        tokens.Should().Contain(t => t.Type == SourceTokenType.NumericLiteral);
    }

    // ────────────────────────────────────────────
    //  Comments
    // ────────────────────────────────────────────

    [Fact]
    public void Tokenize_SingleLineComment_ClassifiedAsComment()
    {
        var tokens = _tokenizer.Tokenize("-- This is a comment\nSELECT name FROM account");

        tokens.Should().Contain(t => t.Type == SourceTokenType.Comment);
    }

    [Fact]
    public void Tokenize_MultiLineComment_ClassifiedAsComment()
    {
        var tokens = _tokenizer.Tokenize("/* block comment */ SELECT name FROM account");

        tokens.Should().Contain(t => t.Type == SourceTokenType.Comment);
    }

    // ────────────────────────────────────────────
    //  Operators
    // ────────────────────────────────────────────

    [Fact]
    public void Tokenize_EqualsSign_ClassifiedAsOperator()
    {
        var tokens = _tokenizer.Tokenize("SELECT * FROM account WHERE statecode = 0");

        tokens.Should().Contain(t => t.Type == SourceTokenType.Operator);
    }

    [Fact]
    public void Tokenize_LessThan_ClassifiedAsOperator()
    {
        var tokens = _tokenizer.Tokenize("SELECT * FROM account WHERE revenue < 5000");

        tokens.Should().Contain(t => t.Type == SourceTokenType.Operator);
    }

    // ────────────────────────────────────────────
    //  Empty / null input
    // ────────────────────────────────────────────

    [Fact]
    public void Tokenize_EmptyString_ReturnsEmptyList()
    {
        var tokens = _tokenizer.Tokenize("");

        tokens.Should().BeEmpty();
    }

    [Fact]
    public void Tokenize_NullString_ReturnsEmptyList()
    {
        var tokens = _tokenizer.Tokenize(null!);

        tokens.Should().BeEmpty();
    }

    // ────────────────────────────────────────────
    //  Known functions
    // ────────────────────────────────────────────

    [Theory]
    [InlineData("COUNT")]
    [InlineData("SUM")]
    [InlineData("AVG")]
    [InlineData("MIN")]
    [InlineData("MAX")]
    public void Tokenize_AggregateFunctions_ClassifiedAsFunction(string funcName)
    {
        var sql = $"SELECT {funcName}(revenue) FROM account";
        var tokens = _tokenizer.Tokenize(sql);

        tokens.Should().Contain(t => t.Type == SourceTokenType.Function);
    }

    [Theory]
    [InlineData("ROW_NUMBER")]
    [InlineData("RANK")]
    [InlineData("DENSE_RANK")]
    public void Tokenize_WindowFunctions_ClassifiedAsFunction(string funcName)
    {
        var sql = $"SELECT {funcName}() OVER (ORDER BY name) FROM account";
        var tokens = _tokenizer.Tokenize(sql);

        tokens.Should().Contain(t => t.Type == SourceTokenType.Function);
    }

    // ────────────────────────────────────────────
    //  Punctuation
    // ────────────────────────────────────────────

    [Fact]
    public void Tokenize_Comma_ClassifiedAsPunctuation()
    {
        var tokens = _tokenizer.Tokenize("SELECT name, revenue FROM account");

        tokens.Should().Contain(t => t.Type == SourceTokenType.Punctuation);
    }

    // ────────────────────────────────────────────
    //  Variables
    // ────────────────────────────────────────────

    [Fact]
    public void Tokenize_Variable_ClassifiedAsVariable()
    {
        var tokens = _tokenizer.Tokenize("SELECT * FROM account WHERE name = @name");

        tokens.Should().Contain(t => t.Type == SourceTokenType.Variable);
    }

    // ────────────────────────────────────────────
    //  Token positions are correct
    // ────────────────────────────────────────────

    [Fact]
    public void Tokenize_TokensHaveValidPositions()
    {
        var sql = "SELECT name FROM account";
        var tokens = _tokenizer.Tokenize(sql);

        foreach (var token in tokens)
        {
            token.Start.Should().BeGreaterThanOrEqualTo(0);
            token.Length.Should().BeGreaterThan(0);
            (token.Start + token.Length).Should().BeLessThanOrEqualTo(sql.Length);
        }
    }

    // ────────────────────────────────────────────
    //  Invalid SQL still tokenizes (never throws)
    // ────────────────────────────────────────────

    [Fact]
    public void Tokenize_InvalidSql_DoesNotThrow()
    {
        var act = () => _tokenizer.Tokenize("SELECTT FROMM WHEREE");

        act.Should().NotThrow();
    }
}

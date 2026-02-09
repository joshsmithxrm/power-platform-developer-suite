using PPDS.Dataverse.Sql.Intellisense;
using Xunit;

namespace PPDS.Cli.Tests.Tui;

/// <summary>
/// Unit tests for <see cref="SqlSourceTokenizer"/>.
/// Verifies correct mapping of SQL tokens to source token types with accurate positions.
/// </summary>
[Trait("Category", "TuiUnit")]
public class SqlSourceTokenizerTests
{
    private readonly SqlSourceTokenizer _tokenizer = new();

    [Fact]
    public void Tokenize_EmptyString_ReturnsEmpty()
    {
        var tokens = _tokenizer.Tokenize("");
        Assert.Empty(tokens);
    }

    [Fact]
    public void Tokenize_NullString_ReturnsEmpty()
    {
        var tokens = _tokenizer.Tokenize(null!);
        Assert.Empty(tokens);
    }

    [Fact]
    public void Tokenize_SimpleSelect_IdentifiesKeywordsAndIdentifiers()
    {
        var tokens = _tokenizer.Tokenize("SELECT name FROM account");

        Assert.Equal(4, tokens.Count);

        Assert.Equal(SourceTokenType.Keyword, tokens[0].Type);
        Assert.Equal(0, tokens[0].Start);
        Assert.Equal(6, tokens[0].Length); // SELECT

        Assert.Equal(SourceTokenType.Identifier, tokens[1].Type);
        Assert.Equal(7, tokens[1].Start);
        Assert.Equal(4, tokens[1].Length); // name

        Assert.Equal(SourceTokenType.Keyword, tokens[2].Type);
        Assert.Equal(12, tokens[2].Start);
        Assert.Equal(4, tokens[2].Length); // FROM

        Assert.Equal(SourceTokenType.Identifier, tokens[3].Type);
        Assert.Equal(17, tokens[3].Start);
        Assert.Equal(7, tokens[3].Length); // account
    }

    [Fact]
    public void Tokenize_StringLiteral_MapsCorrectly()
    {
        var tokens = _tokenizer.Tokenize("SELECT name FROM account WHERE name = 'Contoso'");

        // Find the string token
        var stringToken = tokens[tokens.Count - 1];
        Assert.Equal(SourceTokenType.StringLiteral, stringToken.Type);
        Assert.Equal(9, stringToken.Length); // 'Contoso' = ' + 7 chars + ' = 9
    }

    [Fact]
    public void Tokenize_NumericLiteral_MapsCorrectly()
    {
        var tokens = _tokenizer.Tokenize("SELECT TOP 100 name FROM account");

        // TOP=keyword, 100=number
        Assert.Equal(SourceTokenType.Keyword, tokens[1].Type); // TOP
        Assert.Equal(SourceTokenType.NumericLiteral, tokens[2].Type); // 100
        Assert.Equal(3, tokens[2].Length);
    }

    [Fact]
    public void Tokenize_AggregateFunctions_MapToFunction()
    {
        var tokens = _tokenizer.Tokenize("SELECT COUNT(name) FROM account");

        Assert.Equal(SourceTokenType.Function, tokens[1].Type); // COUNT
        Assert.Equal(SourceTokenType.Punctuation, tokens[2].Type); // (
    }

    [Fact]
    public void Tokenize_WindowFunctions_MapToFunction()
    {
        var tokens = _tokenizer.Tokenize("ROW_NUMBER RANK DENSE_RANK");

        Assert.Equal(3, tokens.Count);
        Assert.All(tokens, t => Assert.Equal(SourceTokenType.Function, t.Type));
    }

    [Fact]
    public void Tokenize_CastConvertIif_MapToFunction()
    {
        var tokens = _tokenizer.Tokenize("CAST CONVERT IIF");

        Assert.Equal(3, tokens.Count);
        Assert.All(tokens, t => Assert.Equal(SourceTokenType.Function, t.Type));
    }

    [Fact]
    public void Tokenize_Operators_MapCorrectly()
    {
        var tokens = _tokenizer.Tokenize("a = b <> c < d > e <= f >= g");

        // Operators: =, <>, <, >, <=, >=
        Assert.Equal(SourceTokenType.Operator, tokens[1].Type); // =
        Assert.Equal(SourceTokenType.Operator, tokens[3].Type); // <>
        Assert.Equal(SourceTokenType.Operator, tokens[5].Type); // <
        Assert.Equal(SourceTokenType.Operator, tokens[7].Type); // >
        Assert.Equal(SourceTokenType.Operator, tokens[9].Type); // <=
        Assert.Equal(SourceTokenType.Operator, tokens[11].Type); // >=
    }

    [Fact]
    public void Tokenize_ArithmeticOperators_MapCorrectly()
    {
        var tokens = _tokenizer.Tokenize("a + b - c * d / e % f");

        Assert.Equal(SourceTokenType.Operator, tokens[1].Type); // +
        Assert.Equal(SourceTokenType.Operator, tokens[3].Type); // -
        Assert.Equal(SourceTokenType.Operator, tokens[5].Type); // *
        Assert.Equal(SourceTokenType.Operator, tokens[7].Type); // /
        Assert.Equal(SourceTokenType.Operator, tokens[9].Type); // %
    }

    [Fact]
    public void Tokenize_Punctuation_MapCorrectly()
    {
        var tokens = _tokenizer.Tokenize("a, b.c (d);");

        Assert.Equal(SourceTokenType.Punctuation, tokens[1].Type); // ,
        Assert.Equal(SourceTokenType.Punctuation, tokens[3].Type); // .
        Assert.Equal(SourceTokenType.Punctuation, tokens[5].Type); // (
        Assert.Equal(SourceTokenType.Punctuation, tokens[7].Type); // )
        Assert.Equal(SourceTokenType.Punctuation, tokens[8].Type); // ;
    }

    [Fact]
    public void Tokenize_Variable_MapsCorrectly()
    {
        var tokens = _tokenizer.Tokenize("DECLARE @myVar");

        Assert.Equal(SourceTokenType.Keyword, tokens[0].Type); // DECLARE
        Assert.Equal(SourceTokenType.Variable, tokens[1].Type); // @myVar
    }

    [Fact]
    public void Tokenize_LineComment_MapsCorrectly()
    {
        var tokens = _tokenizer.Tokenize("SELECT name -- get names\nFROM account");

        // Should have: SELECT, name, -- get names, FROM, account
        Assert.True(tokens.Count >= 4);

        var commentToken = tokens[2];
        Assert.Equal(SourceTokenType.Comment, commentToken.Type);
    }

    [Fact]
    public void Tokenize_BlockComment_MapsCorrectly()
    {
        var tokens = _tokenizer.Tokenize("SELECT /* columns */ name FROM account");

        // Should have: SELECT, /* columns */, name, FROM, account
        Assert.True(tokens.Count >= 4);

        var commentToken = tokens[1];
        Assert.Equal(SourceTokenType.Comment, commentToken.Type);
    }

    [Fact]
    public void Tokenize_BracketedIdentifier_CorrectLength()
    {
        var tokens = _tokenizer.Tokenize("SELECT [my column] FROM account");

        // [my column] = [ + 9 chars + ] = 11 chars including brackets
        Assert.Equal(SourceTokenType.Identifier, tokens[1].Type);
        Assert.Equal(11, tokens[1].Length);
    }

    [Fact]
    public void Tokenize_EscapedStringLiteral_CorrectLength()
    {
        var tokens = _tokenizer.Tokenize("WHERE name = 'O''Brien'");

        var stringToken = tokens[tokens.Count - 1];
        Assert.Equal(SourceTokenType.StringLiteral, stringToken.Type);
        Assert.Equal(10, stringToken.Length); // 'O''Brien' = 10 chars
    }

    [Fact]
    public void Tokenize_ComplexQuery_TokensAreSorted()
    {
        var sql = "SELECT a.name, COUNT(b.id) FROM account a JOIN contact b ON a.id = b.accountid WHERE a.name LIKE '%test%' ORDER BY a.name ASC";
        var tokens = _tokenizer.Tokenize(sql);

        // Verify tokens are in order
        for (int i = 1; i < tokens.Count; i++)
        {
            Assert.True(tokens[i].Start >= tokens[i - 1].Start,
                $"Token at index {i} (pos {tokens[i].Start}) should not precede token at index {i - 1} (pos {tokens[i - 1].Start})");
        }
    }

    [Fact]
    public void Tokenize_AllKeywords_MapToKeyword()
    {
        var keywords = new[] { "SELECT", "FROM", "WHERE", "AND", "OR", "ORDER", "BY",
            "ASC", "DESC", "TOP", "LIMIT", "IS", "NULL", "NOT", "IN", "LIKE",
            "JOIN", "INNER", "LEFT", "RIGHT", "OUTER", "ON", "AS", "DISTINCT",
            "GROUP", "HAVING", "CASE", "WHEN", "THEN", "ELSE", "END",
            "EXISTS", "UNION", "ALL", "INSERT", "INTO", "VALUES",
            "UPDATE", "SET", "DELETE", "BETWEEN", "OVER", "PARTITION",
            "DECLARE", "IF", "BEGIN" };

        foreach (var keyword in keywords)
        {
            var tokens = _tokenizer.Tokenize(keyword);
            Assert.True(tokens.Count >= 1, $"Expected token for '{keyword}'");
            Assert.True(tokens[0].Type == SourceTokenType.Keyword,
                $"Expected '{keyword}' to map to Keyword, got {tokens[0].Type}");
        }
    }

    [Fact]
    public void Tokenize_DecimalNumber_CorrectLengthAndType()
    {
        var tokens = _tokenizer.Tokenize("3.14");

        Assert.Single(tokens);
        Assert.Equal(SourceTokenType.NumericLiteral, tokens[0].Type);
        Assert.Equal(4, tokens[0].Length);
    }

    [Fact]
    public void Tokenize_MultiLineWithComments_HandlesCorrectly()
    {
        var sql = "-- header comment\nSELECT name\n/* block */\nFROM account";
        var tokens = _tokenizer.Tokenize(sql);

        // Should have: comment, SELECT, name, comment, FROM, account
        Assert.True(tokens.Count >= 5);

        Assert.Equal(SourceTokenType.Comment, tokens[0].Type); // -- header comment
        Assert.Equal(SourceTokenType.Keyword, tokens[1].Type); // SELECT
        Assert.Equal(SourceTokenType.Identifier, tokens[2].Type); // name
        Assert.Equal(SourceTokenType.Comment, tokens[3].Type); // /* block */
        Assert.Equal(SourceTokenType.Keyword, tokens[4].Type); // FROM
    }
}

using PPDS.Dataverse.Sql.Intellisense;
using PPDS.Query.Intellisense;
using Xunit;

namespace PPDS.Cli.Tests.Tui;

/// <summary>
/// Unit tests for <see cref="SqlCursorContext"/>.
/// Verifies cursor context detection at various positions in SQL text.
/// </summary>
[Trait("Category", "TuiUnit")]
public class SqlCursorContextTests
{
    #region Empty / Null Input

    [Fact]
    public void Analyze_EmptyString_ReturnsKeywordWithStatementStart()
    {
        var result = SqlCursorContext.Analyze("", 0);

        Assert.Equal(SqlCompletionContextKind.Keyword, result.Kind);
        Assert.NotNull(result.KeywordSuggestions);
        Assert.Contains("SELECT", result.KeywordSuggestions);
    }

    [Fact]
    public void Analyze_NullString_ReturnsKeywordWithStatementStart()
    {
        var result = SqlCursorContext.Analyze(null!, 0);

        Assert.Equal(SqlCompletionContextKind.Keyword, result.Kind);
        Assert.NotNull(result.KeywordSuggestions);
        Assert.Contains("SELECT", result.KeywordSuggestions);
    }

    #endregion

    #region After FROM / JOIN — Entity Context

    [Fact]
    public void Analyze_CursorAfterFrom_ReturnsEntityContext()
    {
        // "SELECT name FROM |"
        var sql = "SELECT name FROM ";
        var result = SqlCursorContext.Analyze(sql, sql.Length);

        Assert.Equal(SqlCompletionContextKind.Entity, result.Kind);
    }

    [Fact]
    public void Analyze_CursorAfterFromWithPrefix_ReturnsEntityContext()
    {
        // "SELECT name FROM acc|"
        var sql = "SELECT name FROM acc";
        var result = SqlCursorContext.Analyze(sql, sql.Length);

        Assert.Equal(SqlCompletionContextKind.Entity, result.Kind);
        Assert.Equal("acc", result.Prefix);
    }

    [Fact]
    public void Analyze_CursorAfterJoin_ReturnsEntityContext()
    {
        // "SELECT * FROM account a JOIN |"
        var sql = "SELECT * FROM account a JOIN ";
        var result = SqlCursorContext.Analyze(sql, sql.Length);

        Assert.Equal(SqlCompletionContextKind.Entity, result.Kind);
    }

    [Fact]
    public void Analyze_CursorAfterLeftJoin_ReturnsEntityContext()
    {
        // "SELECT * FROM account a LEFT JOIN |"
        var sql = "SELECT * FROM account a LEFT JOIN ";
        var result = SqlCursorContext.Analyze(sql, sql.Length);

        Assert.Equal(SqlCompletionContextKind.Entity, result.Kind);
    }

    #endregion

    #region SELECT Column List — Attribute Context

    [Fact]
    public void Analyze_CursorInSelectColumnList_ReturnsAttributeContext()
    {
        // "SELECT name, | FROM account"
        var sql = "SELECT name, ";
        var result = SqlCursorContext.Analyze(sql, sql.Length);

        // In partial SQL, after comma in SELECT, should show attributes
        Assert.Equal(SqlCompletionContextKind.Attribute, result.Kind);
    }

    [Fact]
    public void Analyze_CursorAfterSelectKeyword_ReturnsKeywordContext()
    {
        // "SELECT |"
        var sql = "SELECT ";
        var result = SqlCursorContext.Analyze(sql, sql.Length);

        // After SELECT with no columns yet, offer attribute context or keywords like DISTINCT/TOP
        Assert.True(
            result.Kind == SqlCompletionContextKind.Keyword ||
            result.Kind == SqlCompletionContextKind.Attribute,
            $"Expected Keyword or Attribute but got {result.Kind}");
    }

    #endregion

    #region After alias. — Qualified Attribute Context

    [Fact]
    public void Analyze_CursorAfterAliasDot_ReturnsAttributeForSpecificEntity()
    {
        // "SELECT a.| FROM account a"
        // Note: parse will fail because a. is incomplete, falls back to lexer
        var sql = "SELECT a. FROM account a";
        var cursorPos = "SELECT a.".Length;
        var result = SqlCursorContext.Analyze(sql, cursorPos);

        Assert.Equal(SqlCompletionContextKind.Attribute, result.Kind);
        Assert.Equal("account", result.CurrentEntity);
    }

    [Fact]
    public void Analyze_CursorAfterAliasDotWithPrefix_ReturnsAttributeForSpecificEntity()
    {
        // "SELECT a.na| FROM account a"
        var sql = "SELECT a.na FROM account a";
        var cursorPos = "SELECT a.na".Length;
        var result = SqlCursorContext.Analyze(sql, cursorPos);

        Assert.Equal(SqlCompletionContextKind.Attribute, result.Kind);
        Assert.Equal("account", result.CurrentEntity);
        Assert.Equal("na", result.Prefix);
    }

    #endregion

    #region After WHERE / AND / OR — Attribute Context

    [Fact]
    public void Analyze_CursorAfterWhere_ReturnsAttributeContext()
    {
        // "SELECT name FROM account WHERE |"
        var sql = "SELECT name FROM account WHERE ";
        var result = SqlCursorContext.Analyze(sql, sql.Length);

        Assert.Equal(SqlCompletionContextKind.Attribute, result.Kind);
    }

    [Fact]
    public void Analyze_CursorAfterAnd_ReturnsAttributeContext()
    {
        // "SELECT name FROM account WHERE name = 'x' AND |"
        var sql = "SELECT name FROM account WHERE name = 'x' AND ";
        var result = SqlCursorContext.Analyze(sql, sql.Length);

        Assert.Equal(SqlCompletionContextKind.Attribute, result.Kind);
    }

    #endregion

    #region After ORDER BY — Attribute Context

    [Fact]
    public void Analyze_CursorAfterOrderBy_ReturnsAttributeContext()
    {
        // "SELECT name FROM account ORDER BY |"
        var sql = "SELECT name FROM account ORDER BY ";
        var result = SqlCursorContext.Analyze(sql, sql.Length);

        Assert.Equal(SqlCompletionContextKind.Attribute, result.Kind);
    }

    #endregion

    #region After ON in JOIN — Attribute Context

    [Fact]
    public void Analyze_CursorAfterOn_ReturnsAttributeContext()
    {
        // "SELECT * FROM account a JOIN contact c ON |"
        var sql = "SELECT * FROM account a JOIN contact c ON ";
        var result = SqlCursorContext.Analyze(sql, sql.Length);

        Assert.Equal(SqlCompletionContextKind.Attribute, result.Kind);
        Assert.Null(result.CurrentEntity); // both tables
    }

    #endregion

    #region Context-Filtered Keywords

    [Fact]
    public void Analyze_CursorAfterFromEntity_ReturnsKeywordsLikeWhereJoin()
    {
        // "SELECT name FROM account |"
        var sql = "SELECT name FROM account ";
        var result = SqlCursorContext.Analyze(sql, sql.Length);

        Assert.Equal(SqlCompletionContextKind.Keyword, result.Kind);
        Assert.NotNull(result.KeywordSuggestions);
        Assert.Contains("WHERE", result.KeywordSuggestions);
        Assert.Contains("JOIN", result.KeywordSuggestions);
        Assert.Contains("ORDER BY", result.KeywordSuggestions);
    }

    [Fact]
    public void Analyze_CursorAfterJoinEntity_ReturnsOnKeyword()
    {
        // "SELECT * FROM account a JOIN contact |"
        var sql = "SELECT * FROM account a JOIN contact ";
        var result = SqlCursorContext.Analyze(sql, sql.Length);

        Assert.Equal(SqlCompletionContextKind.Keyword, result.Kind);
        Assert.NotNull(result.KeywordSuggestions);
        Assert.Contains("ON", result.KeywordSuggestions);
    }

    [Fact]
    public void Analyze_CursorAfterOrderByAttribute_ReturnsAscDesc()
    {
        // "SELECT name FROM account ORDER BY name |"
        var sql = "SELECT name FROM account ORDER BY name ";
        var result = SqlCursorContext.Analyze(sql, sql.Length);

        Assert.Equal(SqlCompletionContextKind.Keyword, result.Kind);
        Assert.NotNull(result.KeywordSuggestions);
        Assert.Contains("ASC", result.KeywordSuggestions);
        Assert.Contains("DESC", result.KeywordSuggestions);
    }

    [Fact]
    public void Analyze_CursorAfterOrderByAscDesc_ReturnsLimit()
    {
        // "SELECT name FROM account ORDER BY name ASC |"
        var sql = "SELECT name FROM account ORDER BY name ASC ";
        var result = SqlCursorContext.Analyze(sql, sql.Length);

        Assert.Equal(SqlCompletionContextKind.Keyword, result.Kind);
        Assert.NotNull(result.KeywordSuggestions);
        Assert.Contains("LIMIT", result.KeywordSuggestions);
    }

    #endregion

    #region Alias Map

    [Fact]
    public void Analyze_BuildsAliasMapFromParsedStatement()
    {
        // Complete SQL that parses successfully
        var sql = "SELECT a.name FROM account a JOIN contact c ON a.accountid = c.parentcustomerid WHERE ";
        var result = SqlCursorContext.Analyze(sql, sql.Length);

        Assert.True(result.AliasMap.TryGetValue("a", out var aliasA), "Alias map should contain 'a'");
        Assert.True(result.AliasMap.TryGetValue("c", out var aliasC), "Alias map should contain 'c'");
        Assert.Equal("account", aliasA);
        Assert.Equal("contact", aliasC);
    }

    [Fact]
    public void Analyze_BuildsAliasMapFromPartialSql()
    {
        // Partial SQL that fails parse, falls to lexer
        var sql = "SELECT FROM account a JOIN contact c ON ";
        var result = SqlCursorContext.Analyze(sql, sql.Length);

        Assert.True(result.AliasMap.TryGetValue("a", out _), "Alias map should contain 'a'");
        Assert.True(result.AliasMap.TryGetValue("c", out _), "Alias map should contain 'c'");
    }

    #endregion

    #region Prefix Extraction

    [Fact]
    public void Analyze_ExtractsPrefixFromPartialInput()
    {
        // "SELECT na|" — prefix should be "na"
        var sql = "SELECT na";
        var result = SqlCursorContext.Analyze(sql, sql.Length);

        // "na" is partial -- could be keyword or attribute prefix
        Assert.Equal("na", result.Prefix);
    }

    [Fact]
    public void Analyze_NoPrefixAtSpaceBoundary()
    {
        var sql = "SELECT ";
        var result = SqlCursorContext.Analyze(sql, sql.Length);

        Assert.Equal("", result.Prefix);
    }

    #endregion

    #region String Literal / Escaped Strings

    [Fact]
    public void Analyze_CursorInsideEscapedString_ReturnsNone()
    {
        // "SELECT * FROM t WHERE name = 'O''Brien'" -- cursor at position 33 is inside the string
        var result = SqlCursorContext.Analyze("SELECT * FROM t WHERE name = 'O''Brien'", 33);
        Assert.Equal(SqlCompletionContextKind.None, result.Kind);
    }

    [Fact]
    public void Analyze_CursorInsideSimpleString_ReturnsNone()
    {
        // "SELECT * FROM t WHERE name = 'hello'" -- cursor at position 32 is inside the string
        var result = SqlCursorContext.Analyze("SELECT * FROM t WHERE name = 'hello'", 32);
        Assert.Equal(SqlCompletionContextKind.None, result.Kind);
    }

    [Fact]
    public void Analyze_CursorAfterEscapedStringClosingQuote_DoesNotReturnNone()
    {
        // "SELECT * FROM t WHERE name = 'O''Brien' " -- cursor after closing quote + space
        var sql = "SELECT * FROM t WHERE name = 'O''Brien' ";
        var result = SqlCursorContext.Analyze(sql, sql.Length);
        Assert.NotEqual(SqlCompletionContextKind.None, result.Kind);
    }

    #endregion

    #region Statement Start

    [Fact]
    public void Analyze_CursorAtVeryStart_ReturnsStatementStartKeywords()
    {
        var sql = "S";
        var result = SqlCursorContext.Analyze(sql, 0);

        Assert.Equal(SqlCompletionContextKind.Keyword, result.Kind);
        Assert.NotNull(result.KeywordSuggestions);
        Assert.Contains("SELECT", result.KeywordSuggestions);
        Assert.Contains("INSERT", result.KeywordSuggestions);
        Assert.Contains("UPDATE", result.KeywordSuggestions);
        Assert.Contains("DELETE", result.KeywordSuggestions);
    }

    #endregion
}

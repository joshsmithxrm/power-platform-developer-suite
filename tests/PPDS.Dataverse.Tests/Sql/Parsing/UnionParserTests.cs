using PPDS.Dataverse.Sql.Ast;
using PPDS.Dataverse.Sql.Parsing;
using Xunit;

namespace PPDS.Dataverse.Tests.Sql.Parsing;

[Trait("Category", "TuiUnit")]
public class UnionParserTests
{
    [Fact]
    public void ParsesUnionAll_TwoSelects()
    {
        var sql = "SELECT name FROM account UNION ALL SELECT name FROM contact";
        var statement = SqlParser.ParseSql(sql);

        var union = Assert.IsType<SqlUnionStatement>(statement);
        Assert.Equal(2, union.Queries.Count);
        Assert.Single(union.IsUnionAll);
        Assert.True(union.IsUnionAll[0]);
        Assert.Equal("account", union.Queries[0].From.TableName);
        Assert.Equal("contact", union.Queries[1].From.TableName);
    }

    [Fact]
    public void ParsesUnion_WithoutAll_TwoSelects()
    {
        var sql = "SELECT name FROM account UNION SELECT name FROM contact";
        var statement = SqlParser.ParseSql(sql);

        var union = Assert.IsType<SqlUnionStatement>(statement);
        Assert.Equal(2, union.Queries.Count);
        Assert.Single(union.IsUnionAll);
        Assert.False(union.IsUnionAll[0]); // UNION without ALL
    }

    [Fact]
    public void ParsesThreeWayUnion()
    {
        var sql = "SELECT name FROM account UNION ALL SELECT name FROM contact UNION SELECT name FROM lead";
        var statement = SqlParser.ParseSql(sql);

        var union = Assert.IsType<SqlUnionStatement>(statement);
        Assert.Equal(3, union.Queries.Count);
        Assert.Equal(2, union.IsUnionAll.Count);
        Assert.True(union.IsUnionAll[0]);   // first boundary: UNION ALL
        Assert.False(union.IsUnionAll[1]);  // second boundary: UNION
        Assert.Equal("account", union.Queries[0].From.TableName);
        Assert.Equal("contact", union.Queries[1].From.TableName);
        Assert.Equal("lead", union.Queries[2].From.TableName);
    }

    [Fact]
    public void ParsesUnionWithMultipleColumns()
    {
        var sql = "SELECT name, revenue FROM account UNION ALL SELECT fullname, annualincome FROM contact";
        var statement = SqlParser.ParseSql(sql);

        var union = Assert.IsType<SqlUnionStatement>(statement);
        Assert.Equal(2, union.Queries[0].Columns.Count);
        Assert.Equal(2, union.Queries[1].Columns.Count);
    }

    [Fact]
    public void ParsesUnionWithWhereClause()
    {
        var sql = "SELECT name FROM account WHERE revenue > 1000 UNION ALL SELECT name FROM contact WHERE statecode = 0";
        var statement = SqlParser.ParseSql(sql);

        var union = Assert.IsType<SqlUnionStatement>(statement);
        Assert.NotNull(union.Queries[0].Where);
        Assert.NotNull(union.Queries[1].Where);
    }

    [Fact]
    public void ParsesUnionWithTopOnBranch()
    {
        var sql = "SELECT TOP 10 name FROM account UNION ALL SELECT TOP 5 name FROM contact";
        var statement = SqlParser.ParseSql(sql);

        var union = Assert.IsType<SqlUnionStatement>(statement);
        Assert.Equal(10, union.Queries[0].Top);
        Assert.Equal(5, union.Queries[1].Top);
    }

    [Fact]
    public void PlainSelectStillWorks()
    {
        // Ensure plain SELECT without UNION still works via ParseStatement
        var sql = "SELECT name FROM account WHERE revenue > 1000";
        var statement = SqlParser.ParseSql(sql);

        Assert.IsType<SqlSelectStatement>(statement);
    }

    [Fact]
    public void PlainSelectStillWorksViaParseMethod()
    {
        // The Parse() method should still return SqlSelectStatement for non-union queries
        var result = SqlParser.Parse("SELECT name FROM account");
        Assert.NotNull(result);
        Assert.Equal("account", result.From.TableName);
    }

    [Fact]
    public void ParseThrowsForUnionViaParse()
    {
        // Parse() expects SqlSelectStatement, not SqlUnionStatement
        var sql = "SELECT name FROM account UNION ALL SELECT name FROM contact";
        Assert.Throws<SqlParseException>(() => SqlParser.Parse(sql));
    }

    [Fact]
    public void LexerTokenizesUnionKeyword()
    {
        var lexer = new SqlLexer("UNION ALL");
        var result = lexer.Tokenize();

        Assert.Equal(SqlTokenType.Union, result.Tokens[0].Type);
        Assert.Equal(SqlTokenType.All, result.Tokens[1].Type);
        Assert.Equal(SqlTokenType.Eof, result.Tokens[2].Type);
    }

    [Fact]
    public void UnionIsCaseInsensitive()
    {
        var sql = "SELECT name FROM account union all SELECT name FROM contact";
        var statement = SqlParser.ParseSql(sql);

        var union = Assert.IsType<SqlUnionStatement>(statement);
        Assert.Equal(2, union.Queries.Count);
        Assert.True(union.IsUnionAll[0]);
    }

    [Fact]
    public void UnionStatementHasCorrectSourcePosition()
    {
        var sql = "SELECT name FROM account UNION ALL SELECT name FROM contact";
        var statement = SqlParser.ParseSql(sql);

        var union = Assert.IsType<SqlUnionStatement>(statement);
        Assert.Equal(0, union.SourcePosition);
    }

    [Fact]
    public void SqlUnionStatement_RequiresAtLeastTwoQueries()
    {
        var query = new SqlSelectStatement(
            new[] { SqlColumnRef.Simple("name") },
            new SqlTableRef("account"));

        Assert.Throws<System.ArgumentException>(() =>
            new SqlUnionStatement(new[] { query }, System.Array.Empty<bool>()));
    }

    [Fact]
    public void SqlUnionStatement_IsUnionAllCountMustMatchQueryBoundaries()
    {
        var q1 = new SqlSelectStatement(
            new[] { SqlColumnRef.Simple("name") },
            new SqlTableRef("account"));
        var q2 = new SqlSelectStatement(
            new[] { SqlColumnRef.Simple("name") },
            new SqlTableRef("contact"));

        // 2 queries require exactly 1 IsUnionAll flag
        Assert.Throws<System.ArgumentException>(() =>
            new SqlUnionStatement(new[] { q1, q2 }, new[] { true, false }));
    }
}

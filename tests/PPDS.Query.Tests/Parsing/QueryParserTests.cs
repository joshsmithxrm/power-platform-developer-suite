using FluentAssertions;
using Microsoft.SqlServer.TransactSql.ScriptDom;
using PPDS.Query.Parsing;
using Xunit;

namespace PPDS.Query.Tests.Parsing;

[Trait("Category", "Unit")]
public class QueryParserTests
{
    private readonly QueryParser _parser = new();

    // ────────────────────────────────────────────
    //  Parse: simple SELECT returns TSqlFragment
    // ────────────────────────────────────────────

    [Fact]
    public void Parse_SimpleSelect_ReturnsTSqlFragment()
    {
        var fragment = _parser.Parse("SELECT name FROM account");

        fragment.Should().NotBeNull();
        fragment.Should().BeAssignableTo<TSqlFragment>();
    }

    // ────────────────────────────────────────────
    //  Parse: SELECT with WHERE
    // ────────────────────────────────────────────

    [Fact]
    public void Parse_SelectWithWhere_ReturnsTSqlFragment()
    {
        var fragment = _parser.Parse("SELECT name FROM account WHERE revenue > 1000");

        fragment.Should().NotBeNull();
    }

    // ────────────────────────────────────────────
    //  Parse: SELECT with JOIN
    // ────────────────────────────────────────────

    [Fact]
    public void Parse_SelectWithJoin_ReturnsTSqlFragment()
    {
        var fragment = _parser.Parse(
            "SELECT a.name, c.fullname FROM account a INNER JOIN contact c ON a.accountid = c.parentcustomerid");

        fragment.Should().NotBeNull();
    }

    // ────────────────────────────────────────────
    //  Parse: SELECT with ORDER BY
    // ────────────────────────────────────────────

    [Fact]
    public void Parse_SelectWithOrderBy_ReturnsTSqlFragment()
    {
        var fragment = _parser.Parse("SELECT name FROM account ORDER BY name ASC");

        fragment.Should().NotBeNull();
    }

    // ────────────────────────────────────────────
    //  Parse: SELECT with GROUP BY
    // ────────────────────────────────────────────

    [Fact]
    public void Parse_SelectWithGroupBy_ReturnsTSqlFragment()
    {
        var fragment = _parser.Parse(
            "SELECT statecode, COUNT(*) AS cnt FROM account GROUP BY statecode");

        fragment.Should().NotBeNull();
    }

    // ────────────────────────────────────────────
    //  Parse: SELECT with TOP
    // ────────────────────────────────────────────

    [Fact]
    public void Parse_SelectWithTop_ReturnsTSqlFragment()
    {
        var fragment = _parser.Parse("SELECT TOP 10 name FROM account");

        fragment.Should().NotBeNull();
    }

    // ────────────────────────────────────────────
    //  Parse: SELECT with DISTINCT
    // ────────────────────────────────────────────

    [Fact]
    public void Parse_SelectWithDistinct_ReturnsTSqlFragment()
    {
        var fragment = _parser.Parse("SELECT DISTINCT name FROM account");

        fragment.Should().NotBeNull();
    }

    // ────────────────────────────────────────────
    //  Parse: INSERT statement
    // ────────────────────────────────────────────

    [Fact]
    public void Parse_InsertStatement_ReturnsTSqlFragment()
    {
        var fragment = _parser.Parse(
            "INSERT INTO account (name, revenue) VALUES ('Contoso', 1000000)");

        fragment.Should().NotBeNull();
    }

    // ────────────────────────────────────────────
    //  Parse: UPDATE statement
    // ────────────────────────────────────────────

    [Fact]
    public void Parse_UpdateStatement_ReturnsTSqlFragment()
    {
        var fragment = _parser.Parse(
            "UPDATE account SET revenue = 2000000 WHERE name = 'Contoso'");

        fragment.Should().NotBeNull();
    }

    // ────────────────────────────────────────────
    //  Parse: DELETE statement
    // ────────────────────────────────────────────

    [Fact]
    public void Parse_DeleteStatement_ReturnsTSqlFragment()
    {
        var fragment = _parser.Parse("DELETE FROM account WHERE name = 'Contoso'");

        fragment.Should().NotBeNull();
    }

    // ────────────────────────────────────────────
    //  Parse: UNION / UNION ALL
    // ────────────────────────────────────────────

    [Fact]
    public void Parse_UnionAll_ReturnsTSqlFragment()
    {
        var fragment = _parser.Parse(
            "SELECT name FROM account UNION ALL SELECT fullname FROM contact");

        fragment.Should().NotBeNull();
    }

    [Fact]
    public void Parse_Union_ReturnsTSqlFragment()
    {
        var fragment = _parser.Parse(
            "SELECT name FROM account UNION SELECT fullname FROM contact");

        fragment.Should().NotBeNull();
    }

    // ────────────────────────────────────────────
    //  ParseStatement: returns first statement
    // ────────────────────────────────────────────

    [Fact]
    public void ParseStatement_SimpleSelect_ReturnsSelectStatement()
    {
        var statement = _parser.ParseStatement("SELECT name FROM account");

        statement.Should().NotBeNull();
        statement.Should().BeOfType<SelectStatement>();
    }

    [Fact]
    public void ParseStatement_Insert_ReturnsInsertStatement()
    {
        var statement = _parser.ParseStatement(
            "INSERT INTO account (name) VALUES ('Contoso')");

        statement.Should().BeOfType<InsertStatement>();
    }

    [Fact]
    public void ParseStatement_Update_ReturnsUpdateStatement()
    {
        var statement = _parser.ParseStatement(
            "UPDATE account SET name = 'Fabrikam' WHERE accountid = '00000000-0000-0000-0000-000000000001'");

        statement.Should().BeOfType<UpdateStatement>();
    }

    [Fact]
    public void ParseStatement_Delete_ReturnsDeleteStatement()
    {
        var statement = _parser.ParseStatement("DELETE FROM account WHERE name = 'Contoso'");

        statement.Should().BeOfType<DeleteStatement>();
    }

    // ────────────────────────────────────────────
    //  ParseBatch: returns all statements
    // ────────────────────────────────────────────

    [Fact]
    public void ParseBatch_MultipleStatements_ReturnsAllStatements()
    {
        var sql = "SELECT name FROM account; SELECT fullname FROM contact";
        var statements = _parser.ParseBatch(sql);

        statements.Should().HaveCount(2);
        statements[0].Should().BeOfType<SelectStatement>();
        statements[1].Should().BeOfType<SelectStatement>();
    }

    [Fact]
    public void ParseBatch_SingleStatement_ReturnsSingleElement()
    {
        var statements = _parser.ParseBatch("SELECT name FROM account");

        statements.Should().HaveCount(1);
    }

    // ────────────────────────────────────────────
    //  TryParse: false on invalid SQL
    // ────────────────────────────────────────────

    [Fact]
    public void TryParse_InvalidSql_ReturnsFalse()
    {
        var result = _parser.TryParse(
            "SELECTT name FROMM account",
            out var fragment,
            out var errors);

        result.Should().BeFalse();
        fragment.Should().BeNull();
        errors.Should().NotBeEmpty();
    }

    [Fact]
    public void TryParse_ValidSql_ReturnsTrue()
    {
        var result = _parser.TryParse(
            "SELECT name FROM account",
            out var fragment,
            out var errors);

        result.Should().BeTrue();
        fragment.Should().NotBeNull();
        errors.Should().BeEmpty();
    }

    // ────────────────────────────────────────────
    //  Invalid SQL throws QueryParseException
    // ────────────────────────────────────────────

    [Fact]
    public void Parse_InvalidSql_ThrowsQueryParseException()
    {
        var act = () => _parser.Parse("SELECTT name FROMM account");

        act.Should().Throw<QueryParseException>();
    }

    [Fact]
    public void Parse_InvalidSql_ExceptionContainsLineAndColumn()
    {
        var act = () => _parser.Parse("SELECTT name FROMM account");

        var exception = act.Should().Throw<QueryParseException>().Which;
        exception.Errors.Should().NotBeEmpty();
        exception.Errors[0].Line.Should().BeGreaterThan(0);
        exception.Errors[0].Column.Should().BeGreaterThanOrEqualTo(0);
        exception.ErrorCode.Should().Be("QUERY_PARSE_ERROR");
    }

    [Fact]
    public void Parse_InvalidSql_ExceptionMessageContainsPosition()
    {
        var act = () => _parser.Parse("SELECTT name FROMM account");

        var exception = act.Should().Throw<QueryParseException>().Which;
        exception.Message.Should().Contain("line");
    }

    // ────────────────────────────────────────────
    //  Null/empty input throws ArgumentNullException
    // ────────────────────────────────────────────

    [Fact]
    public void Parse_NullInput_ThrowsArgumentNullException()
    {
        var act = () => _parser.Parse(null!);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void TryParse_NullInput_ThrowsArgumentNullException()
    {
        var act = () => _parser.TryParse(null!, out _, out _);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void ParseStatement_NullInput_ThrowsArgumentNullException()
    {
        var act = () => _parser.ParseStatement(null!);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void ParseBatch_NullInput_ThrowsArgumentNullException()
    {
        var act = () => _parser.ParseBatch(null!);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Parse_EmptyString_ReturnsFragment()
    {
        // Empty string is syntactically valid (empty batch)
        var fragment = _parser.Parse("");

        fragment.Should().NotBeNull();
    }

    // ────────────────────────────────────────────
    //  GetStatementType
    // ────────────────────────────────────────────

    [Fact]
    public void GetStatementType_Select_ReturnsSelectStatementType()
    {
        var type = _parser.GetStatementType("SELECT name FROM account");

        type.Should().Be(typeof(SelectStatement));
    }

    [Fact]
    public void GetStatementType_Insert_ReturnsInsertStatementType()
    {
        var type = _parser.GetStatementType(
            "INSERT INTO account (name) VALUES ('Contoso')");

        type.Should().Be(typeof(InsertStatement));
    }
}

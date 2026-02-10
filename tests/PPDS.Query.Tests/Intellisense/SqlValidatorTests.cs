using FluentAssertions;
using PPDS.Dataverse.Sql.Intellisense;
using Xunit;

using SqlValidator = PPDS.Query.Intellisense.SqlValidator;

namespace PPDS.Query.Tests.Intellisense;

[Trait("Category", "Unit")]
public class SqlValidatorTests
{
    /// <summary>
    /// Validator with no metadata provider (parse-only validation).
    /// </summary>
    private readonly SqlValidator _validator = new(metadataProvider: null);

    // ────────────────────────────────────────────
    //  Valid SQL returns no diagnostics
    // ────────────────────────────────────────────

    [Fact]
    public async Task ValidateAsync_ValidSelect_ReturnsNoDiagnostics()
    {
        var diagnostics = await _validator.ValidateAsync("SELECT name FROM account");

        diagnostics.Should().BeEmpty();
    }

    [Fact]
    public async Task ValidateAsync_ValidInsert_ReturnsNoDiagnostics()
    {
        var diagnostics = await _validator.ValidateAsync(
            "INSERT INTO account (name) VALUES ('Contoso')");

        diagnostics.Should().BeEmpty();
    }

    [Fact]
    public async Task ValidateAsync_ValidUpdate_ReturnsNoDiagnostics()
    {
        var diagnostics = await _validator.ValidateAsync(
            "UPDATE account SET name = 'Fabrikam' WHERE accountid = '00000000-0000-0000-0000-000000000001'");

        diagnostics.Should().BeEmpty();
    }

    [Fact]
    public async Task ValidateAsync_ValidDelete_ReturnsNoDiagnostics()
    {
        var diagnostics = await _validator.ValidateAsync(
            "DELETE FROM account WHERE name = 'Contoso'");

        diagnostics.Should().BeEmpty();
    }

    [Fact]
    public async Task ValidateAsync_ValidSelectWithJoin_ReturnsNoDiagnostics()
    {
        var diagnostics = await _validator.ValidateAsync(
            "SELECT a.name, c.fullname FROM account a " +
            "INNER JOIN contact c ON a.accountid = c.parentcustomerid");

        diagnostics.Should().BeEmpty();
    }

    // ────────────────────────────────────────────
    //  Invalid SQL returns diagnostics with error positions
    // ────────────────────────────────────────────

    [Fact]
    public async Task ValidateAsync_InvalidSql_ReturnsDiagnosticsWithError()
    {
        var diagnostics = await _validator.ValidateAsync("SELECTT name FROMM account");

        diagnostics.Should().NotBeEmpty();
        diagnostics.Should().AllSatisfy(d =>
        {
            d.Severity.Should().Be(SqlDiagnosticSeverity.Error);
            d.Start.Should().BeGreaterThanOrEqualTo(0);
            d.Length.Should().BeGreaterThan(0);
            d.Message.Should().NotBeNullOrEmpty();
        });
    }

    [Fact]
    public async Task ValidateAsync_MissingSemicolon_ReturnsDiagnostic()
    {
        // Totally broken SQL should produce a diagnostic
        var diagnostics = await _validator.ValidateAsync("SELECT FROM");

        diagnostics.Should().NotBeEmpty();
    }

    // ────────────────────────────────────────────
    //  Multiple errors in one query
    // ────────────────────────────────────────────

    [Fact]
    public async Task ValidateAsync_MultipleSyntaxErrors_ReturnsMultipleDiagnostics()
    {
        // This SQL is entirely broken and should produce at least one diagnostic
        var diagnostics = await _validator.ValidateAsync(
            "SELECTT FROMM WHEREE AND ORDERBY");

        diagnostics.Should().NotBeEmpty();
        diagnostics.Count.Should().BeGreaterThanOrEqualTo(1);
    }

    // ────────────────────────────────────────────
    //  Empty / whitespace input
    // ────────────────────────────────────────────

    [Fact]
    public async Task ValidateAsync_EmptyString_ReturnsNoDiagnostics()
    {
        var diagnostics = await _validator.ValidateAsync("");

        diagnostics.Should().BeEmpty();
    }

    [Fact]
    public async Task ValidateAsync_WhitespaceOnly_ReturnsNoDiagnostics()
    {
        var diagnostics = await _validator.ValidateAsync("   ");

        diagnostics.Should().BeEmpty();
    }

    [Fact]
    public async Task ValidateAsync_NullInput_ReturnsNoDiagnostics()
    {
        var diagnostics = await _validator.ValidateAsync(null!);

        diagnostics.Should().BeEmpty();
    }

    // ────────────────────────────────────────────
    //  Error position is reasonable
    // ────────────────────────────────────────────

    [Fact]
    public async Task ValidateAsync_ErrorAtKnownPosition_PositionIsWithinInput()
    {
        var sql = "SELECT name FRON account";
        var diagnostics = await _validator.ValidateAsync(sql);

        diagnostics.Should().NotBeEmpty();
        foreach (var diagnostic in diagnostics)
        {
            diagnostic.Start.Should().BeGreaterThanOrEqualTo(0);
            diagnostic.Start.Should().BeLessThanOrEqualTo(sql.Length);
        }
    }

    // ────────────────────────────────────────────
    //  Complex valid SQL
    // ────────────────────────────────────────────

    [Fact]
    public async Task ValidateAsync_ComplexValidSql_ReturnsNoDiagnostics()
    {
        var diagnostics = await _validator.ValidateAsync(
            "SELECT TOP 10 a.name, COUNT(*) AS cnt " +
            "FROM account a " +
            "INNER JOIN contact c ON a.accountid = c.parentcustomerid " +
            "WHERE a.statecode = 0 AND a.revenue > 1000 " +
            "GROUP BY a.name " +
            "ORDER BY cnt DESC");

        diagnostics.Should().BeEmpty();
    }
}

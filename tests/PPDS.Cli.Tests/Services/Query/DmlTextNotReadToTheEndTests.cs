using PPDS.Cli.Infrastructure.Errors;
using PPDS.Cli.Services.Query;
using PPDS.Dataverse.Query;
using Xunit;

namespace PPDS.Cli.Tests.Services.Query;

/// <summary>
/// A text the SQL parser does not read to its end is refused before anything is read or written (#1955). The parser took
/// U+FFFF outside a string literal as the end of the text, so <c>DELETE FROM account WHERE name = 'a' &lt;U+FFFF&gt; AND
/// name = 'b'</c> deleted every row named 'a'. The refusal is a parse error (<c>Query.ParseError</c>), for every statement
/// kind and every kind of request.
/// </summary>
[Trait("Category", "Unit")]
public sealed class DmlTextNotReadToTheEndTests
{
    private static readonly DmlSafetyOptions[] EveryRequest =
        [new DmlSafetyOptions { IsConfirmed = true }, new DmlSafetyOptions(), new DmlSafetyOptions { IsDryRun = true }];

    [Theory]
    [InlineData("DELETE FROM account WHERE name = 'n1' ￿ AND name = 'n2'")]
    [InlineData("UPDATE account SET description = 'x' WHERE name = 'n1' ￿ AND name = 'n2'")]
    [InlineData("INSERT INTO account (name) SELECT name FROM account WHERE name = 'n1' ￿ AND name = 'n2'")]
    [InlineData("DELETE FROM account WHERE name = 'n1' -- note￿\n AND name = 'n2'")]
    [InlineData("UPDATE account SET description = 'x' WHERE name = 'n1'; ￿ DELETE FROM account WHERE name = 'n2'")]
    [InlineData("￿DELETE FROM account WHERE name = 'n1'")]
    [InlineData("SELECT name FROM account WHERE name = 'n1' ￿ AND name = 'n2'")]
    public async Task ATextWithUFFFFOutsideALiteral_IsRefusedAsAParseError_BeforeAnythingIsRead(string sql)
    {
        foreach (var safety in EveryRequest)
        {
            var h = DmlTestHarness.Create(rows: 2);

            var ex = await Assert.ThrowsAsync<PpdsException>(() => h.Service.ExecuteAsync(new SqlQueryRequest { Sql = sql, DmlSafety = safety }));

            Assert.Equal(ErrorCodes.Query.ParseError, ex.ErrorCode);
            Assert.Contains("U+FFFF", ex.Message);
            Assert.DoesNotContain("n1", ex.Message);
            Assert.Empty(h.Reads);
            h.AssertNothingChanged();
        }
    }

    [Fact]
    public async Task TheRefusal_HasTheExitCodeOfAnyOtherParseError()
    {
        var notRead = await Assert.ThrowsAsync<PpdsException>(() => DmlTestHarness.Create(rows: 1).Service.ExecuteAsync(
            new SqlQueryRequest { Sql = "DELETE FROM account WHERE name = 'n1' ￿ AND name = 'n2'" }));
        var syntax = await Assert.ThrowsAsync<PpdsException>(() => DmlTestHarness.Create(rows: 1).Service.ExecuteAsync(
            new SqlQueryRequest { Sql = "DELETE FROM account WHERE name = 'n1' AND" }));

        Assert.Equal(syntax.ErrorCode, notRead.ErrorCode);
        Assert.Equal(ExceptionMapper.ToExitCode(syntax), ExceptionMapper.ToExitCode(notRead));
    }

    /// <summary>The same statement without U+FFFF reads with both conditions, so the refusal is what stops the one-condition read.</summary>
    [Fact]
    public async Task WithoutUFFFF_TheDeleteReadsWithBothConditions()
    {
        var h = DmlTestHarness.Create(rows: 2);

        await h.Service.ExecuteAsync(new SqlQueryRequest
        {
            Sql = "DELETE FROM account WHERE name = 'n1' AND name = 'n2'",
            DmlSafety = new DmlSafetyOptions { IsConfirmed = true }
        });

        var read = Assert.Single(h.Reads).FetchXml;
        Assert.Contains("value=\"n1\"", read);
        Assert.Contains("value=\"n2\"", read);
    }
}

using PPDS.Cli.Infrastructure.Errors;
using PPDS.Cli.Services.Query;
using PPDS.Dataverse.Query;
using PPDS.Tests.Shared;
using Xunit;

namespace PPDS.Cli.Tests.Services.Query;

/// <summary>
/// Two forms the live run on 7120082d showed are not stored as written, refused on the branch ("prove or refuse"). The table
/// cells are in <see cref="DmlValueAdmissionTable"/>; these are what a cell cannot state: a copied value, the message, and a
/// precision that was not read.
/// <list type="bullet">
/// <item>E49: a floating-point value with more decimal places than the column keeps is stored rounded (47.123456 on a column
/// of precision 5 was stored as 47.12346).</item>
/// <item>E73: written text with a character XML 1.0 does not allow is refused by Dataverse as an invalid character (U+0000,
/// U+0001, U+FFFE), after the branch had admitted it.</item>
/// </list>
/// </summary>
[Trait("Category", "Unit")]
public sealed class DmlLiveObservedRefusalsTests
{
    private static readonly DmlSafetyOptions Confirmed = new() { IsConfirmed = true };

    private static readonly DmlSafetyOptions[] EveryRequest = [Confirmed, new DmlSafetyOptions(), new DmlSafetyOptions { IsDryRun = true }];

    private static async Task<PpdsException> RefusedBeforeAnythingIsReadAsync(string sql, string code)
    {
        PpdsException? first = null;
        foreach (var safety in EveryRequest)
        {
            var h = DmlTestHarness.Create(rows: 1);
            var ex = await Assert.ThrowsAsync<PpdsException>(() => h.Service.ExecuteAsync(new SqlQueryRequest { Sql = sql, DmlSafety = safety }));
            Assert.Equal(code, ex.ErrorCode);
            Assert.Empty(h.Reads);
            h.AssertNothingChanged();
            first ??= ex;
        }
        return first!;
    }

    // ---- E49: floating point and the column's precision -------------------------------------------------------------

    [Fact]
    public async Task AFloatingPointValue_WithMorePlacesThanTheColumnKeeps_IsRefused_AndOneAtThePlacesIsWritten()
    {
        var ex = await RefusedBeforeAnythingIsReadAsync("UPDATE account SET address1_latitude = 47.123456 WHERE name = 'n1'", ErrorCodes.Query.DmlValueStoredDifferently);
        Assert.Contains("more decimal places than the column keeps (5)", ex.Message);

        var h = DmlTestHarness.Create(rows: 1);
        await h.Service.ExecuteAsync(new SqlQueryRequest { Sql = "UPDATE account SET address1_latitude = 47.12345 WHERE name = 'n1'", DmlSafety = Confirmed });
        Assert.Equal(47.12345d, Assert.Single(h.WrittenEntities)["address1_latitude"]);
    }

    [Theory]
    [InlineData("UPDATE account SET description = 'x' WHERE address1_latitude = 47.123456")]
    [InlineData("UPDATE account SET description = 'x' WHERE address1_latitude < 47.123456")]
    [InlineData("DELETE FROM account WHERE address1_latitude BETWEEN 1 AND 47.123456")]
    public async Task AComparedFloatingPointOperand_WithMorePlacesThanTheColumnKeeps_IsRefused(string sql)
    {
        var ex = await RefusedBeforeAnythingIsReadAsync(sql, ErrorCodes.Query.DmlUnsupportedConstruct);
        Assert.Contains("more decimal places than the column keeps (5)", ex.Message);
    }

    /// <summary>A precision that was not read admits no decimal places, written or compared; a whole number is written.</summary>
    [Fact]
    public async Task AFloatingPointColumnWhosePrecisionWasNotRead_TakesWholeNumbersOnly()
    {
        var ex = await RefusedBeforeAnythingIsReadAsync("UPDATE account SET ppds_unreadfloat = 1.5 WHERE name = 'n1'", ErrorCodes.Query.DmlValueStoredDifferently);
        Assert.Contains("decimal places were not read", ex.Message);
        await RefusedBeforeAnythingIsReadAsync("DELETE FROM account WHERE ppds_unreadfloat = 1.5", ErrorCodes.Query.DmlUnsupportedConstruct);

        var h = DmlTestHarness.Create(rows: 1);
        await h.Service.ExecuteAsync(new SqlQueryRequest { Sql = "UPDATE account SET ppds_unreadfloat = 2 WHERE ppds_unreadfloat = 3", DmlSafety = Confirmed });
        Assert.Single(h.WrittenEntities);
    }

    /// <summary>
    /// A copied value with more places than the floating-point target keeps is refused when the statement runs, before its
    /// first write, as for a decimal target; one with fewer is written.
    /// </summary>
    [Theory]
    [InlineData(47.123456, false)]
    [InlineData(47.12345, true)]
    public async Task ACopiedValue_IntoAFloatingPointColumn_IsCheckedForItsPlaces(double source, bool written)
    {
        var h = DmlTestHarness.Create(rows: 2, shapeRow: (_, row) => row["revenue"] = QueryValue.Simple((decimal)source));
        var request = new SqlQueryRequest { Sql = "UPDATE account SET address1_latitude = revenue WHERE name = 'n1'", DmlSafety = Confirmed };

        if (written)
        {
            await h.Service.ExecuteAsync(request);
            Assert.Equal(2, h.WrittenEntities.Count);
            return;
        }

        var ex = await Assert.ThrowsAsync<PpdsException>(() => h.Service.ExecuteAsync(request));
        Assert.Equal(ErrorCodes.Query.DmlValueStoredDifferently, ex.ErrorCode);
        Assert.Contains("a floating-point column that keeps 5 places", ex.Message);
        h.AssertNothingChanged();
    }

    // ---- E73: characters Dataverse refuses in written text -------------------------------------------------------------

    /// <summary>The refusal names the code point and its position, not the text.</summary>
    [Theory]
    [InlineData("N'secret<U+0000>b'", "U+0000 at character 7")]
    [InlineData("N'a<U+0001>secret'", "U+0001 at character 2")]
    [InlineData("N'ab<U+FFFE>secret'", "U+FFFE at character 3")]
    public async Task WrittenText_WithACharacterXmlDoesNotAllow_IsRefused_NamingTheCodePointAndItsPosition(string literal, string named)
    {
        foreach (var sql in new[]
                 {
                     $"UPDATE account SET name = {literal} WHERE name = 'n1'",
                     $"INSERT INTO account (name) VALUES ({literal})",
                     $"INSERT INTO account (name) SELECT {literal} AS name FROM account WHERE name = 'n1'",
                 })
        {
            var ex = await RefusedBeforeAnythingIsReadAsync(DmlValueAdmissionTable.Sql(sql), ErrorCodes.Query.DmlUnsupportedConstruct);
            Assert.Contains(named, ex.Message);
            Assert.Contains("XML 1.0 does not allow", ex.Message);
            Assert.DoesNotContain("secret", ex.Message);
        }
    }

    /// <summary>
    /// A copied text value with such a character is refused when the statement runs, before its first write. Dataverse refused
    /// these characters on write, so a column written through it should not hold one; whether any other path stores one is
    /// not known, so the copy is checked too.
    /// </summary>
    [Fact]
    public async Task ACopiedText_WithACharacterXmlDoesNotAllow_IsRefusedBeforeTheFirstWrite()
    {
        var h = DmlTestHarness.Create(rows: 2, shapeRow: (i, row) => row["name"] = QueryValue.Simple(i == 1 ? "a\u0001b" : "ok"));

        var ex = await Assert.ThrowsAsync<PpdsException>(() => h.Service.ExecuteAsync(new SqlQueryRequest
        {
            Sql = "UPDATE account SET description = name WHERE name = 'n1'",
            DmlSafety = Confirmed
        }));

        Assert.Equal(ErrorCodes.Query.DmlUnsupportedConstruct, ex.ErrorCode);
        Assert.Contains("a value of the source holds U+0001 at character 2", ex.Message);
        h.AssertNothingChanged();
    }
}

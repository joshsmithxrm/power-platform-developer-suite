using System.Xml.Linq;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Cli.Services.Query;
using PPDS.Dataverse.Query;
using Xunit;

namespace PPDS.Cli.Tests.Services.Query;

/// <summary>
/// The narrow check of the value-admission round (#1698, review round 3), end to end through <see cref="SqlQueryService"/>:
/// <list type="bullet">
/// <item>number literals of more than 38 digits (item 1; the cells are in the admission table);</item>
/// <item>the pass's decimal and floating-point checks on values read from a column (item 2);</item>
/// <item>the status message without the script's values (item 4);</item>
/// <item>negative zero sent as +0 (item 5);</item>
/// <item>a compared date and time sent as its UTC instant (item 6);</item>
/// <item>compared text with a control character refused before anything is read (item 7).</item>
/// </list>
/// The date case rows (item 3) are in the admission table.
/// </summary>
[Trait("Category", "Unit")]
public sealed class DmlReviewRound3FixTests
{
    private static readonly DmlSafetyOptions Confirmed = new() { IsConfirmed = true };

    private static readonly DmlSafetyOptions[] EveryRequest = [Confirmed, new DmlSafetyOptions(), new DmlSafetyOptions { IsDryRun = true }];

    private static async Task<DmlTestHarness> RefusedInThePassAsync(string sql, string column, object source)
    {
        var h = DmlTestHarness.Create(rows: 3, shapeRow: (i, row) => row[column] = QueryValue.Simple(i == 1 ? source : 2m));

        var ex = await Assert.ThrowsAsync<PpdsException>(() => h.Service.ExecuteAsync(new SqlQueryRequest { Sql = sql, DmlSafety = Confirmed }));

        Assert.Equal(ErrorCodes.Query.DmlValueStoredDifferently, ex.ErrorCode);
        Assert.Contains("This statement wrote nothing.", ex.Message);
        Assert.Empty(h.Writes);
        Assert.Empty(h.WrittenEntities);
        return h;
    }

    private static async Task<object?> WrittenAsync(string sql, string sourceColumn, object source, string target)
    {
        var h = DmlTestHarness.Create(rows: 1, shapeRow: (_, row) => row[sourceColumn] = QueryValue.Simple(source));

        await h.Service.ExecuteAsync(new SqlQueryRequest { Sql = sql, DmlSafety = Confirmed });

        var entity = Assert.Single(h.WrittenEntities);
        return entity[target];
    }

    // ---- item 2: the pass's decimal check ----------------------------------------------------------------------

    /// <summary>11 places into a decimal column that keeps the default 10: Dataverse would round it.</summary>
    [Fact]
    public async Task Update_ADecimalWithMorePlacesThanTheTargetDecimalKeeps_WritesNothing()
        => await RefusedInThePassAsync("UPDATE account SET revenue = revenue WHERE name LIKE '%'", "revenue", 0.00000000001m);

    /// <summary>3 places into a decimal column whose metadata keeps 2.</summary>
    [Fact]
    public async Task Update_ADecimalIntoADecimalOfPrecisionTwo_WithThreePlaces_WritesNothing()
        => await RefusedInThePassAsync("UPDATE account SET ppds_rate = revenue WHERE name LIKE '%'", "revenue", 1.234m);

    [Fact]
    public async Task InsertSelect_ADecimalIntoADecimalOfPrecisionTwo_WithThreePlaces_WritesNothing()
        => await RefusedInThePassAsync("INSERT INTO account (ppds_rate) SELECT revenue FROM account WHERE name LIKE '%'", "revenue", 1.234m);

    /// <summary>A currency value with 4 places into a decimal column that keeps 2.</summary>
    [Fact]
    public async Task Update_ACurrencyWithFourPlacesIntoADecimalOfPrecisionTwo_WritesNothing()
        => await RefusedInThePassAsync("UPDATE account SET ppds_rate = creditlimit WHERE name LIKE '%'", "creditlimit", 1.2345m);

    [Fact]
    public async Task InsertSelect_ACurrencyWithFourPlacesIntoADecimalOfPrecisionTwo_WritesNothing()
        => await RefusedInThePassAsync("INSERT INTO account (ppds_rate) SELECT creditlimit FROM account WHERE name LIKE '%'", "creditlimit", 1.2345m);

    /// <summary>The same copies are written when every value fits the target's places.</summary>
    [Theory]
    [InlineData("UPDATE account SET ppds_rate = revenue WHERE name = 'n0'", "revenue", "1.23")]
    [InlineData("UPDATE account SET ppds_rate = creditlimit WHERE name = 'n0'", "creditlimit", "1.2")]
    [InlineData("UPDATE account SET revenue = revenue WHERE name = 'n0'", "revenue", "0.0000000001")]
    public async Task ACopyThatFitsTheTargetsPlaces_IsWrittenExactly(string sql, string source, string value)
    {
        var written = await WrittenAsync(sql, source, decimal.Parse(value, System.Globalization.CultureInfo.InvariantCulture),
            sql.Split(' ')[3]);

        Assert.Equal(decimal.Parse(value, System.Globalization.CultureInfo.InvariantCulture), Assert.IsType<decimal>(written));
    }

    // ---- item 2: the pass's floating-point check (mutant A31) --------------------------------------------------

    /// <summary>A decimal whose digits a double cannot hold, copied to a floating-point column.</summary>
    [Fact]
    public async Task Update_ADecimalADoubleCannotHold_IntoAFloatingPointColumn_WritesNothing()
        => await RefusedInThePassAsync("UPDATE account SET address1_latitude = revenue WHERE name LIKE '%'", "revenue", 0.1234567890123456789m);

    [Fact]
    public async Task InsertSelect_ADecimalADoubleCannotHold_IntoAFloatingPointColumn_WritesNothing()
        => await RefusedInThePassAsync("INSERT INTO account (address1_latitude) SELECT revenue FROM account WHERE name LIKE '%'", "revenue", 0.1234567890123456789m);

    [Fact]
    public async Task ADecimalADoubleHolds_IsWrittenToAFloatingPointColumn()
    {
        var written = await WrittenAsync("UPDATE account SET address1_latitude = revenue WHERE name = 'n0'", "revenue", 2.5m, "address1_latitude");

        Assert.Equal(2.5, Assert.IsType<double>(written));
    }

    // ---- item 4: the status message ----------------------------------------------------------------------------

    [Theory]
    [InlineData("UPDATE account SET statecode = 1, statuscode = 1 WHERE name = 'n1'")]
    [InlineData("UPDATE account SET statuscode = 2, statecode = 0 WHERE name = 'n1'")]
    public async Task AStatusReasonOfAnotherStatus_IsRefused_WithoutRepeatingEitherValue(string sql)
    {
        foreach (var options in EveryRequest)
        {
            var h = DmlTestHarness.Create(rows: 1);

            var ex = await Assert.ThrowsAsync<PpdsException>(() => h.Service.ExecuteAsync(new SqlQueryRequest { Sql = sql, DmlSafety = options }));

            Assert.Equal(ErrorCodes.Query.DmlUnsupportedConstruct, ex.ErrorCode);
            Assert.Contains("is a status reason of another statecode", ex.Message);
            Assert.DoesNotMatch(@"(statuscode|statecode) \d|not \d", ex.Message);
            Assert.Matches(@"statuscode value at line 1, column \d+ .* the value at line 1, column \d+", ex.Message);
            Assert.Empty(h.Reads);
            h.AssertNothingChanged();
        }
    }

    // ---- item 5: negative zero ---------------------------------------------------------------------------------

    [Theory]
    [InlineData("-0.0")]
    [InlineData("-0.000")]
    [InlineData("-0")]
    public async Task NegativeZero_IsSentAsPositiveZero_ToAFloatingPointColumn(string literal)
    {
        var h = DmlTestHarness.Create(rows: 1);

        await h.Service.ExecuteAsync(new SqlQueryRequest { Sql = $"UPDATE account SET address1_latitude = {literal} WHERE name = 'n0'", DmlSafety = Confirmed });

        var sent = Assert.IsType<double>(Assert.Single(h.WrittenEntities)["address1_latitude"]);
        Assert.Equal(0L, BitConverter.DoubleToInt64Bits(sent));
    }

    [Theory]
    [InlineData("UPDATE account SET address1_latitude = revenue WHERE name = 'n0'")]
    [InlineData("INSERT INTO account (address1_latitude) SELECT revenue FROM account WHERE name = 'n0'")]
    public async Task ANegativeZeroDecimal_CopiedToAFloatingPointColumn_IsSentAsPositiveZero(string sql)
    {
        var negativeZero = decimal.Parse("-0.0", System.Globalization.CultureInfo.InvariantCulture);
        Assert.Equal(long.MinValue, BitConverter.DoubleToInt64Bits((double)negativeZero)); // the source does convert to -0

        var sent = Assert.IsType<double>(await WrittenAsync(sql, "revenue", negativeZero, "address1_latitude"));

        Assert.Equal(0L, BitConverter.DoubleToInt64Bits(sent));
    }

    // ---- item 6: a compared date and time is sent as its UTC instant -------------------------------------------

    private static IReadOnlyList<string> ConditionValues(DmlTestHarness h, string column)
        => h.Reads.SelectMany(r => XDocument.Parse(r.FetchXml).Descendants("condition"))
            .Where(c => (string?)c.Attribute("attribute") == column)
            .SelectMany(c => c.Attribute("value") is { } v ? [v.Value] : c.Elements("value").Select(x => x.Value))
            .ToList();

    [Theory]
    [InlineData("UPDATE account SET description = 'x' WHERE followupby = '2024-01-15T09:30:00+05:00'", "2024-01-15T04:30:00Z")]
    [InlineData("UPDATE account SET description = 'x' WHERE followupby <> '2024-01-15T09:30:00-05:30'", "2024-01-15T15:00:00Z")]
    [InlineData("UPDATE account SET description = 'x' WHERE followupby < N'2024-01-15T00:30:00+01:00'", "2024-01-14T23:30:00Z")]
    [InlineData("UPDATE account SET description = 'x' WHERE followupby BETWEEN '2024-01-15T09:30:00+05:00' AND '2024-01-16T00:00:00-01:00'", "2024-01-15T04:30:00Z|2024-01-16T01:00:00Z")]
    [InlineData("UPDATE account SET description = 'x' WHERE followupby = '2024-01-15T09:30:00+05:00' OR followupby = '2024-01-15T09:30:00Z'", "2024-01-15T04:30:00Z|2024-01-15T09:30:00Z")]
    [InlineData("DELETE FROM account WHERE followupby = '2024-01-15T09:30:00+14:00'", "2024-01-14T19:30:00Z")]
    [InlineData("INSERT INTO account (name) SELECT name FROM account WHERE followupby >= '2024-01-01T00:00:00+02:00'", "2023-12-31T22:00:00Z")]
    public async Task ADateAndTimeInTheWhereOfAWrite_IsSentAsItsUtcInstant(string sql, string expected)
    {
        var h = DmlTestHarness.Create(rows: 1);

        await h.Service.ExecuteAsync(new SqlQueryRequest { Sql = sql, DmlSafety = Confirmed });

        Assert.Equal(expected.Split('|'), ConditionValues(h, "followupby"));
    }

    // ---- item 7: compared text with a control character ---------------------------------------------------------

    public static TheoryData<string> ControlCharacterOperands => new()
    {
        "UPDATE account SET description = 'x' WHERE name = 'a\u0001b'",
        "UPDATE account SET description = 'x' WHERE name = N'a\u0001b'",
        "UPDATE account SET description = 'x' WHERE name <> 'a\u001Fb'",
        "UPDATE account SET description = 'x' WHERE name IN ('n1', N'\u0000')",
        "UPDATE account SET description = 'x' WHERE name LIKE 'a\u0008%'",
        "UPDATE account SET description = 'x' WHERE name NOT LIKE N'%\u000B'",
        "DELETE FROM account WHERE name = N'a\u000Cb'",
        "DELETE FROM account WHERE name = 'n1' OR description = '\u000E'",
        "INSERT INTO account (name) SELECT name FROM account WHERE name = 'a\u0007'",
    };

    [Theory]
    [MemberData(nameof(ControlCharacterOperands))]
    public async Task ComparedTextWithAControlCharacter_IsRefusedBeforeAnythingIsRead(string sql)
    {
        foreach (var options in EveryRequest)
        {
            var h = DmlTestHarness.Create(rows: 1);

            var ex = await Assert.ThrowsAsync<PpdsException>(() => h.Service.ExecuteAsync(new SqlQueryRequest { Sql = sql, DmlSafety = options }));

            Assert.Equal(ErrorCodes.Query.DmlUnsupportedConstruct, ex.ErrorCode);
            Assert.Contains("control character", ex.Message);
            Assert.Empty(h.Reads);
            h.AssertNothingChanged();
        }
    }

    [Theory]
    [InlineData("UPDATE account SET description = 'x' WHERE name = 'a\tb'")]
    [InlineData("UPDATE account SET description = 'x' WHERE name = N'a\nb'")]
    [InlineData("UPDATE account SET description = 'x' WHERE name = 'a\rb'")]
    public async Task ComparedTextWithTabLineFeedOrCarriageReturn_IsStillRead(string sql)
    {
        var h = DmlTestHarness.Create(rows: 1);

        await h.Service.ExecuteAsync(new SqlQueryRequest { Sql = sql, DmlSafety = Confirmed });

        Assert.Single(h.Reads);
    }
}

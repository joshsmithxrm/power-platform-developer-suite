using System.Globalization;
using Microsoft.Xrm.Sdk;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Cli.Services.Query;
using PPDS.Tests.Shared;
using Xunit;

namespace PPDS.Cli.Tests.Services.Query;

/// <summary>
/// The admission table (<see cref="DmlValueAdmissionTable"/>), end to end through <see cref="SqlQueryService"/>: each cell
/// is refused with its code before anything is read or written, or admitted with exactly the value the table gives sent
/// in the bulk write. The same cells are compared with the reference SQL Server in PPDS.Query.DifferentialTests.
/// </summary>
[Trait("Category", "Unit")]
public sealed class DmlValueAdmissionTableTests
{
    private static readonly DmlSafetyOptions Confirmed = new() { IsConfirmed = true };

    public static TheoryData<string, string, string> Cells()
    {
        var data = new TheoryData<string, string, string>();
        foreach (var (literal, target, cell) in DmlValueAdmissionTable.Cells())
            data.Add(literal, target.Kind, cell);
        return data;
    }

    [Fact]
    public void TheTable_HasTenTargetKinds_AndEveryRowHasACellForEach()
    {
        Assert.Equal(10, DmlValueAdmissionTable.Targets.Length);
        Assert.All(DmlValueAdmissionTable.Rows, row => Assert.Equal(10, row.Cells.Length));
        Assert.True(DmlValueAdmissionTable.Rows.Length >= 100, $"{DmlValueAdmissionTable.Rows.Length} rows");
    }

    [Theory]
    [MemberData(nameof(Cells))]
    public async Task EachCell_IsRefusedWithItsCode_OrSendsExactlyItsValue(string literal, string kind, string cell)
    {
        var target = DmlValueAdmissionTable.Targets.Single(t => t.Kind == kind);
        var sql = DmlValueAdmissionTable.Statement(literal, target);
        var h = DmlTestHarness.Create(rows: 1);

        if (cell is "U" or "S")
        {
            var ex = await Assert.ThrowsAsync<PpdsException>(() => h.Service.ExecuteAsync(new SqlQueryRequest { Sql = sql, DmlSafety = Confirmed }));
            Assert.Equal(cell == "U" ? ErrorCodes.Query.DmlUnsupportedConstruct : ErrorCodes.Query.DmlValueStoredDifferently, ex.ErrorCode);
            Assert.Empty(h.Reads);
            h.AssertNothingChanged();
            return;
        }

        await h.Service.ExecuteAsync(new SqlQueryRequest { Sql = sql, DmlSafety = Confirmed });

        var entity = Assert.Single(h.WrittenEntities);
        AssertSent(cell[1..], kind, entity.Contains(target.Column) ? entity[target.Column] : "<missing>");
    }

    public static TheoryData<string, string, string, string> CompareCases()
    {
        var data = new TheoryData<string, string, string, string>();
        foreach (var c in DmlValueAdmissionTable.CompareCases)
            data.Add(c.Kind, c.Operator, c.Literal, c.Cell);
        return data;
    }

    /// <summary>
    /// A literal compared in a write's WHERE: refused before anything is read, or admitted with exactly the text the table
    /// gives carried by the FetchXML condition that is sent (a date and time as its UTC instant, whatever offset it was written
    /// with): the harness records the FetchXML after the executor's serialisation.
    /// </summary>
    [Theory]
    [MemberData(nameof(CompareCases))]
    public async Task EachComparedLiteral_IsRefusedBeforeAnythingIsRead_OrCarriesExactlyItsText(string kind, string op, string literal, string cell)
    {
        var target = DmlValueAdmissionTable.CompareTarget(kind);
        var sql = DmlValueAdmissionTable.CompareStatement(literal, target, op);
        var h = DmlTestHarness.Create(rows: 1);

        if (cell == "U")
        {
            var ex = await Assert.ThrowsAsync<PpdsException>(() => h.Service.ExecuteAsync(new SqlQueryRequest { Sql = sql, DmlSafety = Confirmed }));
            Assert.Equal(ErrorCodes.Query.DmlUnsupportedConstruct, ex.ErrorCode);
            Assert.Empty(h.Reads);
            h.AssertNothingChanged();
            return;
        }

        await h.Service.ExecuteAsync(new SqlQueryRequest { Sql = sql, DmlSafety = Confirmed });

        var read = Assert.Single(h.Reads);
        Assert.Equal(cell[1..], DmlReviewRound6FixTests.ConditionValue(read.FetchXml, target.Column));
    }

    /// <summary>The value sent is exactly the expected one, compared as a value of the target's kind.</summary>
    internal static void AssertSent(string expected, string kind, object? sent)
    {
        if (expected == "null")
        {
            Assert.Null(sent);
            return;
        }

        switch (kind)
        {
            case "text":
                Assert.Equal(expected, Assert.IsType<string>(sent));
                break;
            case "whole number":
                Assert.Equal(int.Parse(expected, CultureInfo.InvariantCulture), Assert.IsType<int>(sent));
                break;
            case "decimal":
                Assert.Equal(decimal.Parse(expected, NumberStyles.Float, CultureInfo.InvariantCulture), Assert.IsType<decimal>(sent));
                break;
            case "currency":
                Assert.Equal(decimal.Parse(expected, NumberStyles.Float, CultureInfo.InvariantCulture), Assert.IsType<Money>(sent).Value);
                break;
            case "floating point":
                // Bit for bit, so negative zero is not taken for +0.
                Assert.Equal(BitConverter.DoubleToInt64Bits(double.Parse(expected, NumberStyles.Float, CultureInfo.InvariantCulture)),
                    BitConverter.DoubleToInt64Bits(Assert.IsType<double>(sent)));
                break;
            case "date and time":
                var instant = Assert.IsType<DateTime>(sent);
                Assert.Equal(DateTimeKind.Utc, instant.Kind);
                Assert.Equal(DateTime.ParseExact(expected, "yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture), DateTime.SpecifyKind(instant, DateTimeKind.Unspecified));
                break;
            case "date only":
                var date = Assert.IsType<DateTime>(sent);
                Assert.Equal(DateTime.ParseExact(expected, "yyyy-MM-dd", CultureInfo.InvariantCulture), date);
                Assert.Equal(TimeSpan.Zero, date.TimeOfDay);
                break;
            case "yes/no":
                Assert.Equal(expected == "1", Assert.IsType<bool>(sent));
                break;
            case "choice":
                Assert.Equal(int.Parse(expected, CultureInfo.InvariantCulture), Assert.IsType<OptionSetValue>(sent).Value);
                break;
            case "lookup":
                var reference = Assert.IsType<EntityReference>(sent);
                Assert.Equal(Guid.Parse(expected), reference.Id);
                Assert.Equal("account", reference.LogicalName);
                break;
            default:
                Assert.Fail($"No kind {kind}.");
                break;
        }
    }
}

using FluentAssertions;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Cli.Services.Query;
using Xunit;

namespace PPDS.Cli.Tests.Services.Query.Faithful;

/// <summary>
/// The SELECT forms the maintainer decided to refuse (2026-11-24, "Refuse all seven"): each was right on main only
/// because of the data, and the translation behind it selects wrong rows for its neighbours. Each is refused with a
/// pinned error code and a message that says what to write instead. And the wide-decimal CAST, where the maintainer
/// decided "Value when it fits".
/// </summary>
[Trait("Category", "Unit")]
public sealed class SelectRefusalsByDecisionTests
{
    private static SqlQueryService Service()
    {
        var table = new FakeTable("account")
            .Column("accountnumber", FakeColumnType.String).Column("name", FakeColumnType.String)
            .Column("description", FakeColumnType.String).Column("numberofemployees", FakeColumnType.Integer)
            .Column("revenue", FakeColumnType.Decimal);
        table.Row(("accountnumber", "k1"), ("name", "Alpha"), ("numberofemployees", 5), ("revenue", 2.5m));
        table.Row(("accountnumber", "k2"), ("name", null), ("numberofemployees", null), ("revenue", 12345678901234567890123m));
        return new FakeDataverse().Add(table).CreateSqlQueryService();
    }

    private static async Task<PpdsException> RefusalAsync(string sql)
        => (await FluentActions.Awaiting(() => Service().ExecuteAsync(new SqlQueryRequest { Sql = sql }))
            .Should().ThrowAsync<PpdsException>()).Which;

    [Theory]
    // col = NULL, in its three forms of the audit (text, whole number, the faithful row)
    [InlineData("SELECT accountnumber FROM account WHERE name = NULL", ErrorCodes.Query.ParseError, "Use IS NULL or IS NOT NULL")]
    [InlineData("SELECT accountnumber FROM account WHERE numberofemployees = NULL", ErrorCodes.Query.ParseError, "Use IS NULL or IS NOT NULL")]
    [InlineData("SELECT accountnumber FROM account WHERE description = NULL", ErrorCodes.Query.ParseError, "Use IS NULL or IS NOT NULL")]
    // col IN (5, NULL)
    [InlineData("SELECT accountnumber FROM account WHERE numberofemployees IN (5, NULL)", ErrorCodes.Query.ParseError, "in an IN list, leave NULL out, and add OR <column> IS NULL")]
    // a variable holding NULL (refused while the script runs, so the service reports ExecutionFailed)
    [InlineData("DECLARE @n nvarchar(20) = NULL; SELECT accountnumber FROM account WHERE name = @n", ErrorCodes.Query.ExecutionFailed, "Use a literal value, or IS NULL or IS NOT NULL")]
    // NOT NOT x = 5
    [InlineData("SELECT accountnumber FROM account WHERE NOT NOT numberofemployees = 5", ErrorCodes.Query.ParseError, "Write the condition without NOT")]
    // TOP 3 WITH TIES
    [InlineData("SELECT TOP 3 WITH TIES accountnumber, numberofemployees FROM account WHERE numberofemployees IS NOT NULL ORDER BY numberofemployees", ErrorCodes.Query.ParseError, "for example TOP (3), without PERCENT or WITH TIES")]
    public async Task EachRefusedForm_HasItsCode_AndNamesWhatToWriteInstead(string sql, string code, string replacement)
    {
        var refusal = await RefusalAsync(sql);

        refusal.ErrorCode.Should().Be(code);
        refusal.UserMessage.Should().Contain(replacement);
    }

    [Theory]
    [InlineData("SELECT accountnumber FROM account WHERE name IS NULL", "k2")]
    [InlineData("SELECT accountnumber FROM account WHERE numberofemployees IN (5) OR numberofemployees IS NULL", "k1,k2")]
    [InlineData("SELECT accountnumber FROM account WHERE numberofemployees = 5", "k1")]
    [InlineData("SELECT TOP (1) accountnumber FROM account ORDER BY accountnumber", "k1")]
    public async Task TheReplacementTheMessageNames_Runs(string sql, string expected)
    {
        var result = await Service().ExecuteAsync(new SqlQueryRequest { Sql = sql });

        string.Join(",", result.Result.Records.Select(r => r["accountnumber"].Value).OrderBy(v => v)).Should().Be(expected);
    }

    // ---- CAST to decimal(p, s) wider than 28 digits ("Value when it fits") ---------------------------------------

    [Fact]
    public async Task WideDecimalCast_ThatFits_ReturnsTheValue()
    {
        var result = await Service().ExecuteAsync(new SqlQueryRequest
        {
            Sql = "SELECT accountnumber, CAST(revenue AS decimal(38, 6)) AS v FROM account WHERE accountnumber = 'k1'"
        });

        result.Result.Records.Single()["v"].Value.Should().Be(2.5m);
    }

    [Fact]
    public async Task WideDecimalCast_ThatDoesNotFit_IsRefusedWithACode()
    {
        // 23 digits before the point plus 6 places: 29 digits.
        var refusal = await RefusalAsync("SELECT accountnumber, CAST(revenue AS decimal(38, 6)) AS v FROM account WHERE accountnumber = 'k2'");

        // The engine refuses it with NotSupportedException, which keeps its own code (review round 6, S9).
        refusal.ErrorCode.Should().Be(ErrorCodes.Operation.NotSupported);
        refusal.UserMessage.Should().Contain("needs more than 28 digits").And.Contain("Use a precision of at most 28");
    }
}

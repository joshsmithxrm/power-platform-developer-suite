using System.Text.Json;
using PPDS.Cli.Commands.Serve.Handlers;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Cli.Infrastructure.Output;
using PPDS.Cli.Services.Query;
using PPDS.Dataverse.Query.Planning;
using PPDS.Dataverse.Query.Planning.Nodes;
using Xunit;

namespace PPDS.Cli.Tests.Services.Query;

/// <summary>Reporting edge cases: timeouts, swallowed interruptions, row-cap wording, sanitiser joiners.</summary>
[Trait("Category", "Unit")]
public class DmlReportingEdgeTests
{
    private static DmlOutcome Outcome(params DmlStatementOutcome[] statements) => DmlOutcome.From(statements)!;

    [Fact]
    public void Rpc_TimeoutAfterDmlRan_IsAnInterruption_NotACancellation()
    {
        var timeout = new TaskCanceledException("timed out");
        DmlOutcome.Attach(timeout, Outcome(new DmlStatementOutcome(DmlOperation.Delete, "account", 2, 0, null, "1")));

        Assert.True(RpcMethodHandler.TryMapQuerySqlFailure(timeout, "x", out var rpc, cancellationRequested: false));

        var data = JsonDocument.Parse(JsonSerializer.Serialize(rpc!.ErrorData, rpc.ErrorData!.GetType())).RootElement;
        Assert.Equal(ErrorCodes.Query.DmlInterrupted, data.GetProperty("code").GetString());
        Assert.Contains("timed out", data.GetProperty("message").GetString());
        Assert.Contains("#1 DELETE account", data.GetProperty("dmlCommitted").GetString());
    }

    [Fact]
    public void SwallowedInterruptedStatement_IsNotReportedAsAllZeroRowsFailed()
    {
        var outcome = Outcome(
            new DmlStatementOutcome(DmlOperation.Delete, "account", 0, 0, null, "1"),
            new DmlStatementOutcome(DmlOperation.Update, "contact", 0, 0, null, "2", Interrupted: true));

        Assert.True(outcome.HasFailures);
        Assert.False(outcome.IsTotalFailure);
        Assert.Equal(ErrorCodes.Query.DmlInterrupted, outcome.ErrorCode);
        Assert.Equal(ExitCodes.Failure, outcome.ExitCode);
        Assert.DoesNotContain("all 0 row(s) failed", outcome.Message);
        Assert.Contains("may be partly applied", outcome.Message);
    }

    [Fact]
    public async Task RowCapOnASelect_IsNotValidated_ItIsADmlOption()
    {
        var h = DmlTestHarness.Create(rows: 2);

        var result = await h.Service.ExecuteAsync(new SqlQueryRequest
        {
            Sql = "SELECT name FROM account",
            DmlSafety = new DmlSafetyOptions { RowCap = 0 }
        });

        Assert.Equal(2, result.Result.Count);
    }

    [Fact]
    public async Task InsertValuesOverTheCap_IsToldToSplitTheInsert_NotToNarrowAWhere()
    {
        var h = DmlTestHarness.Create(rows: 1);

        var ex = await Assert.ThrowsAsync<PpdsDmlRowCapExceededException>(() => h.Service.ExecuteAsync(new SqlQueryRequest
        {
            Sql = "INSERT INTO account (name) VALUES ('a'), ('b'), ('c')",
            DmlSafety = new DmlSafetyOptions { IsConfirmed = true, RowCap = 2 }
        }));

        Assert.Contains("Split the INSERT", ex.Message);
        Assert.DoesNotContain("WHERE", ex.Message);
    }

    [Theory]
    [InlineData("\u0645\u06CC\u200C\u062E\u0648\u0627\u0647\u0645")]           // Persian, ZWNJ between letters
    [InlineData("\u0915\u094D\u200D\u0937")]                                     // Devanagari, ZWJ after a virama
    [InlineData("\U0001F469\u200D\U0001F4BB")]                                   // emoji ZWJ sequence
    public void JoinersBetweenLettersMarksOrEmoji_AreKept(string text)
        => Assert.Equal(text, TerminalText.Sanitize(text));

    [Theory]
    [InlineData("a\u200Db", "a\\u200Db")]
    [InlineData("\u200C\u0645", "\\u200C\u0645")]
    [InlineData("\u0645 \u200C\u0645", "\u0645 \\u200C\u0645")]
    public void JoinersElsewhere_AreEscaped(string text, string expected)
        => Assert.Equal(expected, TerminalText.Sanitize(text));
}

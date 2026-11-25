using PPDS.Cli.Commands;
using PPDS.Cli.Commands.Query;
using PPDS.Cli.Commands.Serve.Handlers;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Cli.Infrastructure.Output;
using PPDS.Cli.Services.Query;
using PPDS.Cli.Tests.TestHelpers;
using PPDS.Cli.Tui.Screens;
using PPDS.Dataverse.BulkOperations;
using PPDS.Dataverse.Query.Planning;
using PPDS.Dataverse.Query.Planning.Nodes;
using Xunit;

namespace PPDS.Cli.Tests.Services.Query;

/// <summary>
/// "Stop at that statement" (#1698): in a script of several writes, a statement that finishes with any failed row ends
/// the script. The statements after it do not run, the outcome names them, and the request is classified as before (a
/// partial success when some row was written, a failure when none was).
/// </summary>
[Trait("Category", "Unit")]
[Collection(nameof(ConsoleCaptureCollection))]
public sealed class DmlStopAtThatStatementTests
{
    private static readonly DmlSafetyOptions Confirmed = new() { IsConfirmed = true };

    private const string TwoStatements =
        "UPDATE account SET name = 'a' WHERE name = 'x'; DELETE FROM contact WHERE lastname = 'y'";

    private const string ThreeStatements =
        "UPDATE account SET name = 'a' WHERE name = 'x'; DELETE FROM contact WHERE lastname = 'y'; INSERT INTO account (name) VALUES ('n')";

    /// <summary>A harness whose write number <paramref name="failAt"/> (1-based; 0 for none) has one failed row.</summary>
    private static DmlTestHarness Harness(int failAt, bool everyRowFails = false)
    {
        var h = DmlTestHarness.Create(rows: 3, contactRows: 2);
        h.ResultOf = (write, rows) => write != failAt
            ? null
            : new BulkOperationResult
            {
                SuccessCount = everyRowFails ? 0 : rows - 1,
                FailureCount = everyRowFails ? rows : 1,
                Errors = new[] { new BulkOperationError { Message = "privilege check failed" } }
            };
        return h;
    }

    [Theory]
    [InlineData(1, new[] { "Update" }, "Statement 2 was not run because statement 1 had failed rows.")]
    [InlineData(2, new[] { "Update", "Delete" }, null)]
    [InlineData(0, new[] { "Update", "Delete" }, null)]
    public async Task TwoStatements_AFailedRowStopsTheScriptAtThatStatement(int failAt, string[] written, string? note)
    {
        var h = Harness(failAt);

        var result = await h.Service.ExecuteAsync(new SqlQueryRequest { Sql = TwoStatements, DmlSafety = Confirmed });

        Assert.Equal(written, h.Writes.Select(w => w.Operation));
        var outcome = result.DmlOutcome!;
        Assert.Equal(new[] { "1", "2" }, outcome.Statements.Select(s => s.Position));
        Assert.Equal(written.Length, outcome.Ran.Count);
        Assert.Equal(note, outcome.NotRunNote);
        if (note != null)
        {
            Assert.EndsWith(" " + note, outcome.Message);
            var notRun = Assert.Single(outcome.NotRun);
            Assert.Equal((DmlOperation.Delete, "contact", 0L, 0L, "2"),
                (notRun.Operation, notRun.EntityLogicalName, notRun.AffectedRows, notRun.FailedRows, notRun.Position));
        }
        else
        {
            Assert.Empty(outcome.NotRun);
            Assert.DoesNotContain("not run", outcome.Message ?? string.Empty);
        }
    }

    [Theory]
    [InlineData(1, 1, "Statements 2 to 3 were not run because statement 1 had failed rows.")]
    [InlineData(2, 2, "Statement 3 was not run because statement 2 had failed rows; statement 1 ran with no failed rows.")]
    [InlineData(3, 3, null)]
    [InlineData(0, 3, null)]
    public async Task ThreeStatements_AFailedRowInTheFirstMiddleOrLastStopsThere(int failAt, int ran, string? note)
    {
        var h = Harness(failAt);

        var result = await h.Service.ExecuteAsync(new SqlQueryRequest { Sql = ThreeStatements, DmlSafety = Confirmed });

        Assert.Equal(new[] { "Update", "Delete", "Create" }.Take(ran), h.Writes.Select(w => w.Operation));
        var outcome = result.DmlOutcome!;
        Assert.Equal(new[] { "1", "2", "3" }, outcome.Statements.Select(s => s.Position));
        Assert.Equal(Enumerable.Range(1, 3).Select(i => i > ran), outcome.Statements.Select(s => s.NotRun));
        Assert.Equal(note, outcome.NotRunNote);
        if (failAt == 0)
        {
            Assert.False(outcome.HasFailures);
            Assert.Equal(ExitCodes.Success, outcome.ExitCode);
            return;
        }

        // The classification of a partially succeeded request is unchanged: exit 1, Operation.PartialFailure.
        Assert.Equal(ExitCodes.PartialSuccess, outcome.ExitCode);
        Assert.Equal(ErrorCodes.Operation.PartialFailure, outcome.ErrorCode);
        if (note != null)
            Assert.EndsWith(" " + note, outcome.Message);
        Assert.Contains("privilege check failed", outcome.Message);
    }

    [Fact]
    public async Task NotRunStatements_AreNamedAsTheirPlansNameThem()
    {
        var h = Harness(failAt: 1);

        var outcome = (await h.Service.ExecuteAsync(new SqlQueryRequest { Sql = ThreeStatements, DmlSafety = Confirmed })).DmlOutcome!;

        Assert.Equal(new[] { (DmlOperation.Delete, "contact"), (DmlOperation.Insert, "account") },
            outcome.NotRun.Select(s => (s.Operation, s.EntityLogicalName)));
        (long Affected, long Failed, string? Error, bool Interrupted) nothingWritten = (0L, 0L, null, false);
        Assert.All(outcome.NotRun, s => Assert.Equal(nothingWritten, (s.AffectedRows, s.FailedRows, s.ErrorMessage, s.Interrupted)));
    }

    [Fact]
    public async Task EveryRowOfTheFirstStatementFailed_IsAFailure_AndTheRestDidNotRun()
    {
        var h = Harness(failAt: 1, everyRowFails: true);

        var outcome = (await h.Service.ExecuteAsync(new SqlQueryRequest { Sql = ThreeStatements, DmlSafety = Confirmed })).DmlOutcome!;

        Assert.Single(h.Writes);
        // No row was written by any statement: a failure (exit 2, Query.ExecutionFailed), as for a single statement.
        Assert.Equal(ExitCodes.Failure, outcome.ExitCode);
        Assert.Equal(ErrorCodes.Query.ExecutionFailed, outcome.ErrorCode);
        Assert.Equal(
            "DML failed (UPDATE account): all 3 row(s) failed. Example error: privilege check failed. " +
            "Statements 2 to 3 were not run because statement 1 had failed rows.",
            outcome.Message);
    }

    [Fact]
    public async Task AFailedRowInTheMiddle_TheMessageNamesEveryStatementOnce()
    {
        var h = Harness(failAt: 2);

        var outcome = (await h.Service.ExecuteAsync(new SqlQueryRequest { Sql = ThreeStatements, DmlSafety = Confirmed })).DmlOutcome!;

        Assert.Equal(
            "DML partially succeeded across 2 statements: 4 row(s) affected, 1 row(s) failed. " +
            "Failed statements: #2 DELETE contact: 1 affected, 1 failed (example error: privilege check failed). " +
            "Statement 3 was not run because statement 2 had failed rows; statement 1 ran with no failed rows.",
            outcome.Message);
    }

    /// <summary>A row-cap refusal in the middle already stopped the script; it still does, and reports what ran.</summary>
    [Fact]
    public async Task RowCapRefusalInTheMiddle_StillStopsTheScript()
    {
        var h = DmlTestHarness.Create(rows: 5, contactRows: 1);

        var ex = await Assert.ThrowsAsync<PpdsDmlRowCapExceededException>(() => h.Service.ExecuteAsync(new SqlQueryRequest
        {
            Sql = "UPDATE contact SET lastname = 'b' WHERE lastname = 'y'; DELETE FROM account WHERE name = 'x'; UPDATE contact SET lastname = 'c' WHERE lastname = 'z'",
            DmlSafety = new DmlSafetyOptions { IsConfirmed = true, RowCap = 2 }
        }));

        Assert.Equal(new[] { ("Update", "contact", 1) }, h.Writes);
        Assert.Contains("#1 UPDATE contact: 1 affected", DmlOutcome.From(ex)!.DescribeCommitted());
    }

    [Fact]
    public async Task DryRunOfTheSameScript_IsUnaffected()
    {
        var h = Harness(failAt: 1);

        var result = await h.Service.ExecuteAsync(new SqlQueryRequest
        {
            Sql = ThreeStatements,
            DmlSafety = new DmlSafetyOptions { IsDryRun = true }
        });

        h.AssertNothingChanged();
        Assert.Null(result.DmlOutcome);
        Assert.NotNull(result.DryRunPlan);
        Assert.True(result.DmlSafetyResult!.IsDryRun);
    }

    [Fact]
    public async Task Streaming_TheFinalChunkNamesTheStatementsNotRun()
    {
        var h = Harness(failAt: 1);

        var chunks = new List<SqlQueryStreamChunk>();
        await foreach (var chunk in h.Service.ExecuteStreamingAsync(new SqlQueryRequest { Sql = ThreeStatements, DmlSafety = Confirmed }))
            chunks.Add(chunk);

        Assert.Single(h.Writes);
        Assert.Equal("Statements 2 to 3 were not run because statement 1 had failed rows.", chunks[^1].DmlOutcome!.NotRunNote);
    }

    // ---- surfaces ------------------------------------------------------------------------------

    private static async Task<SqlQueryResult> StoppedAtTheFirstAsync()
    {
        var h = Harness(failAt: 1);
        return await h.Service.ExecuteAsync(new SqlQueryRequest { Sql = ThreeStatements, DmlSafety = Confirmed });
    }

    [Fact]
    public async Task Cli_TextOutput_SaysWhichStatementsWereNotRun_OnStderr()
    {
        var result = await StoppedAtTheFirstAsync();

        var (exit, stdout, stderr) = RunCli(result, OutputFormat.Text);

        Assert.Equal(ExitCodes.PartialSuccess, exit);
        Assert.Contains("Statements 2 to 3 were not run because statement 1 had failed rows.", stderr);
        Assert.DoesNotContain("not run", stdout);
    }

    [Fact]
    public async Task Cli_JsonOutput_SaysWhichStatementsWereNotRun_InTheError()
    {
        var result = await StoppedAtTheFirstAsync();

        var (exit, stdout, _) = RunCli(result, OutputFormat.Json);

        Assert.Equal(ExitCodes.PartialSuccess, exit);
        Assert.Contains("Statements 2 to 3 were not run because statement 1 had failed rows.", stdout);
        Assert.Contains(ErrorCodes.Operation.PartialFailure, stdout);
    }

    [Fact]
    public async Task Daemon_TheResponseSaysWhichStatementsWereNotRun()
    {
        var result = await StoppedAtTheFirstAsync();

        var response = RpcMethodHandler.MapToResponse(result);

        Assert.False(response.Success);
        Assert.Equal(2, response.AffectedRows);
        Assert.Equal(1, response.FailedRows);
        Assert.EndsWith("Statements 2 to 3 were not run because statement 1 had failed rows.", response.DmlError);
    }

    [Fact]
    public void Tui_StatusLine_KeepsTheNotRunSentenceWhole_WhenTheMessageIsShortened()
    {
        var outcome = DmlOutcome.From(new[]
        {
            new DmlStatementOutcome(DmlOperation.Update, "account", 1, 1, "line one\nline two\n" + new string('x', 400), "1"),
            new DmlStatementOutcome(DmlOperation.Delete, "contact", 0, 0, null, "2", NotRun: true),
            new DmlStatementOutcome(DmlOperation.Insert, "account", 0, 0, null, "3", NotRun: true)
        })!;

        var status = SqlQueryPresenter.FormatCompletionStatus(1, 5, " via Dataverse", outcome);

        Assert.DoesNotContain("\n", status);
        Assert.Contains("...", status);
        Assert.EndsWith("Statements 2 to 3 were not run because statement 1 had failed rows. (5ms via Dataverse)", status);
    }

    private static (int exit, string stdout, string stderr) RunCli(SqlQueryResult result, OutputFormat format)
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var originalOut = Console.Out;
        var originalError = Console.Error;
        try
        {
            Console.SetOut(stdout);
            Console.SetError(stderr);
            IOutputWriter writer = format == OutputFormat.Json ? new JsonOutputWriter(stdout) : new TextOutputWriter();
            var exit = SqlCommand.WriteResult(result, format, verbose: false, writer, fetchXml: null, pagingHint: null);
            return (exit, stdout.ToString(), stderr.ToString());
        }
        finally
        {
            Console.SetOut(originalOut);
            Console.SetError(originalError);
        }
    }
}

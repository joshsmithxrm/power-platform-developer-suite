using System.Text.Json;
using PPDS.Cli.Commands.Serve.Handlers;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Cli.Services.Query;
using PPDS.Dataverse.Progress;
using PPDS.Dataverse.Resilience;
using Xunit;

namespace PPDS.Cli.Tests.Services.Query;

/// <summary>
/// A run interrupted by an error or a cancellation reports what already ran (never rolled back) and the statement
/// that was in flight (may be partly applied) on every surface, and the service wraps the failure in a PpdsException.
/// RPC assertions go through <see cref="RpcMethodHandler.TryMapQuerySqlFailure"/>, the handler's real catch.
/// </summary>
[Trait("Category", "Unit")]
public class DmlInterruptedRunTests
{
    private static readonly DmlSafetyOptions Confirmed = new() { IsConfirmed = true };

    private const string TwoDeletes = "DELETE FROM account WHERE name = 'x'; DELETE FROM contact WHERE lastname = 'y'";

    /// <summary>The bulk call for <paramref name="entity"/> reports progress, then throws <paramref name="failure"/>.</summary>
    private static DmlTestHarness FailingOn(string entity, Exception failure, long reportedDone = 0)
    {
        var h = DmlTestHarness.Create(rows: 3);
        h.OnWrite = (e, n, progress) =>
        {
            if (e != entity) return;
            if (reportedDone > 0)
                progress?.Report(new ProgressSnapshot { Succeeded = reportedDone, Total = n });
            throw failure;
        };
        return h;
    }

    [Fact]
    public async Task SingleStatement_FailingPartWay_IsReportedAsMaybePartlyApplied_WithTheReportedCounts()
    {
        var h = FailingOn("account", new InvalidOperationException("connection lost"), reportedDone: 2);

        var ex = await Assert.ThrowsAsync<PpdsException>(() => h.Service.ExecuteAsync(
            new SqlQueryRequest { Sql = "DELETE FROM account WHERE name = 'x'", DmlSafety = Confirmed }));

        Assert.Equal(ErrorCodes.Query.ExecutionFailed, ex.ErrorCode);
        Assert.Contains("connection lost", ex.UserMessage);
        var note = DmlOutcome.From(ex)!.DescribeCommitted();
        Assert.Contains("#1 DELETE account was running when the run was interrupted and may be partly applied", note);
        Assert.Contains("2 row(s) had been reported as changed", note);
        Assert.Contains("exact number of changed rows is unknown", note);
        Assert.DoesNotContain("rolled back:", note); // no completed statement, and never a rollback claim
        Assert.DoesNotContain("were rolled back", note);
    }

    [Fact]
    public async Task Script_SecondStatementFails_ReportsTheFirstAsCommitted_AndTheSecondAsInFlight()
    {
        var h = FailingOn("contact", new InvalidOperationException("connection lost"));

        var ex = await Assert.ThrowsAsync<PpdsException>(() => h.Service.ExecuteAsync(
            new SqlQueryRequest { Sql = TwoDeletes, DmlSafety = Confirmed }));

        var note = DmlOutcome.From(ex)!.DescribeCommitted();
        Assert.Contains("Statements that already ran before the interruption were not rolled back: #1 DELETE account: 3 affected, 0 failed.", note);
        Assert.Contains("#2 DELETE contact was running when the run was interrupted and may be partly applied: the number of rows it changed is unknown.", note);
    }

    [Fact]
    public async Task CancelledPartWay_StaysACancellation_WithTheInFlightStatementAttached()
    {
        using var cts = new CancellationTokenSource();
        var h = FailingOn("contact", new OperationCanceledException(cts.Token));

        var ex = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => h.Service.ExecuteAsync(
            new SqlQueryRequest { Sql = TwoDeletes, DmlSafety = Confirmed }));

        var note = DmlOutcome.From(ex)!.DescribeCommitted();
        Assert.Contains("#1 DELETE account: 3 affected", note);
        Assert.Contains("#2 DELETE contact was running", note);
    }

    [Fact]
    public async Task Streaming_FailingPartWay_IsWrappedAndReported()
    {
        var h = FailingOn("account", new InvalidOperationException("connection lost"));

        var ex = await Assert.ThrowsAsync<PpdsException>(async () =>
        {
            await foreach (var _ in h.Service.ExecuteStreamingAsync(
                new SqlQueryRequest { Sql = "DELETE FROM account WHERE name = 'x'", DmlSafety = Confirmed })) { }
        });

        Assert.Contains("may be partly applied", DmlOutcome.From(ex)!.DescribeCommitted());
    }

    [Fact]
    public async Task AuthFailureAfterDmlRan_IsWrapped_AndNeverAsksToReauthenticateAndRetry()
    {
        var h = FailingOn("contact", new DataverseAuthenticationException("token expired", requiresReauthentication: true));

        var ex = await Assert.ThrowsAsync<PpdsAuthException>(() => h.Service.ExecuteAsync(
            new SqlQueryRequest { Sql = TwoDeletes, DmlSafety = Confirmed }));

        Assert.Equal(ErrorCodes.Auth.Expired, ex.ErrorCode);
        Assert.False(ex.RequiresReauthentication); // a retry would run statement #1 again
        Assert.NotNull(DmlOutcome.From(ex));
    }

    [Fact]
    public async Task FailureBeforeAnyDml_IsNotDecorated()
    {
        var h = DmlTestHarness.Create(rows: 3);

        var ex = await Assert.ThrowsAnyAsync<Exception>(() => h.Service.ExecuteAsync(
            new SqlQueryRequest { Sql = "SELECT 1/0", DmlSafety = Confirmed }));

        Assert.Null(DmlOutcome.From(ex));
    }

    // ---- RPC: the handler's real mapping --------------------------------------------------------

    private static JsonElement Data(RpcException rpc)
        => JsonDocument.Parse(JsonSerializer.Serialize(rpc.ErrorData, rpc.ErrorData!.GetType())).RootElement;

    [Fact]
    public async Task Rpc_RowCapRefusal_GoesThroughTheHandlerMapping_WithItsFlagAndWhatAlreadyRan()
    {
        var h = DmlTestHarness.Create(rows: 2, contactRows: 5);
        var ex = await Assert.ThrowsAsync<PpdsDmlRowCapExceededException>(() => h.Service.ExecuteAsync(new SqlQueryRequest
        {
            Sql = TwoDeletes,
            DmlSafety = new DmlSafetyOptions { IsConfirmed = true, RowCap = 2 }
        }));

        Assert.True(RpcMethodHandler.TryMapQuerySqlFailure(ex, TwoDeletes, out var rpc));

        Assert.Equal(ErrorCodes.Query.DmlRowCapExceeded, rpc!.StructuredErrorCode);
        var data = Data(rpc);
        Assert.Equal("Query.DmlRowCapExceeded", data.GetProperty("code").GetString());
        Assert.True(data.GetProperty("dmlRowCapExceeded").GetBoolean());
        Assert.False(data.GetProperty("dmlBlocked").GetBoolean());
        Assert.Contains("#1 DELETE account: 2 affected", data.GetProperty("dmlCommitted").GetString());
        Assert.Contains("not rolled back", data.GetProperty("message").GetString()); // shown by clients that read only the message
    }

    [Fact]
    public async Task Rpc_InterruptedScript_GoesThroughTheHandlerMapping()
    {
        var h = FailingOn("contact", new InvalidOperationException("connection lost"));
        var ex = await Assert.ThrowsAsync<PpdsException>(() => h.Service.ExecuteAsync(
            new SqlQueryRequest { Sql = TwoDeletes, DmlSafety = Confirmed }));

        Assert.True(RpcMethodHandler.TryMapQuerySqlFailure(ex, TwoDeletes, out var rpc));

        var data = Data(rpc!);
        Assert.Equal(ErrorCodes.Query.ExecutionFailed, data.GetProperty("code").GetString());
        Assert.Contains("#2 DELETE contact was running", data.GetProperty("dmlCommitted").GetString());
        Assert.Contains("connection lost", data.GetProperty("message").GetString());
    }

    [Fact]
    public async Task Rpc_CancelledAfterDmlRan_IsReportedInsteadOfABareCancellation()
    {
        using var cts = new CancellationTokenSource();
        var h = FailingOn("contact", new OperationCanceledException(cts.Token));
        var ex = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => h.Service.ExecuteAsync(
            new SqlQueryRequest { Sql = TwoDeletes, DmlSafety = Confirmed }));

        Assert.True(RpcMethodHandler.TryMapQuerySqlFailure(ex, TwoDeletes, out var rpc));

        var data = Data(rpc!);
        Assert.Equal(ErrorCodes.Operation.Cancelled, data.GetProperty("code").GetString());
        Assert.Contains("#1 DELETE account: 3 affected", data.GetProperty("dmlCommitted").GetString());
    }

    [Fact]
    public void Rpc_CancellationWithoutDml_StaysABareCancellation()
    {
        Assert.False(RpcMethodHandler.TryMapQuerySqlFailure(new OperationCanceledException(), "SELECT 1", out _));
    }

    [Fact]
    public async Task Rpc_ConfirmationRequired_AndBlocked_CarryTheirFlags()
    {
        var h = DmlTestHarness.Create(rows: 1);
        var confirm = await Assert.ThrowsAsync<PpdsDmlConfirmationRequiredException>(() => h.Service.ExecuteAsync(
            new SqlQueryRequest { Sql = "DELETE FROM account WHERE name = 'x'", DmlSafety = new DmlSafetyOptions() }));
        var blocked = await Assert.ThrowsAsync<PpdsException>(() => h.Service.ExecuteAsync(
            new SqlQueryRequest { Sql = "DELETE FROM account", DmlSafety = Confirmed }));

        Assert.True(RpcMethodHandler.TryMapQuerySqlFailure(confirm, "x", out var confirmRpc));
        Assert.True(RpcMethodHandler.TryMapQuerySqlFailure(blocked, "x", out var blockedRpc));

        Assert.True(Data(confirmRpc!).GetProperty("dmlConfirmationRequired").GetBoolean());
        Assert.True(Data(blockedRpc!).GetProperty("dmlBlocked").GetBoolean());
    }
}

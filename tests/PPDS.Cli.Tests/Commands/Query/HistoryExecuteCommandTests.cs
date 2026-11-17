using System.CommandLine;
using Moq;
using PPDS.Auth.Profiles;
using PPDS.Cli.Commands.Query;
using PPDS.Cli.Commands.Query.History;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Cli.Services.Query;
using PPDS.Cli.Tests.Services.Shared;
using PPDS.Dataverse.BulkOperations;
using PPDS.Dataverse.Query;
using Xunit;

namespace PPDS.Cli.Tests.Commands.Query;

/// <summary>
/// 'ppds query history execute' must behave exactly like 'ppds query sql' with the same DML guard options
/// (a null DmlSafety makes the service skip every DML check).
/// </summary>
[Trait("Category", "Unit")]
public class HistoryExecuteCommandTests
{
    private const string DeleteSql = "DELETE FROM account WHERE name = 'x'";
    private const string UpdateSql = "UPDATE account SET name = 'y' WHERE name = 'x'";
    private static readonly Guid RowId = Guid.Parse("00000000-0000-0000-0000-000000000001");

    // ---- option surface -------------------------------------------------

    [Fact]
    public void Execute_HasTheSameDmlGuardOptionsAsSql_WithIdenticalHelpAndType()
    {
        var sql = SqlCommand.Create();
        var execute = ExecuteCommand.Create();

        foreach (var name in new[] { "--confirm", "--dry-run", "--no-limit" })
        {
            var fromSql = sql.Options.Single(o => o.Name == name);
            var fromExecute = execute.Options.Single(o => o.Name == name);
            Assert.Equal(fromSql.Description, fromExecute.Description);
            Assert.Equal(fromSql.ValueType, fromExecute.ValueType);
        }
    }

    [Fact]
    public void Execute_DmlOptionsDefaultToOff()
    {
        var parsed = ExecuteCommand.Create().Parse("abc");

        Assert.Empty(parsed.Errors);
        foreach (var name in new[] { "--confirm", "--dry-run", "--no-limit" })
        {
            var option = (Option<bool>)ExecuteCommand.Create().Options.Single(o => o.Name == name);
            Assert.False(parsed.GetValue(ExecuteCommand.Create().Options.OfType<Option<bool>>().Single(o => o.Name == name)));
            Assert.NotNull(option);
        }
    }

    [Fact]
    public void Execute_DryRunWithCsv_IsRejected_LikeSql()
    {
        var parsed = ExecuteCommand.Create().Parse("abc --dry-run --output-format Csv");

        Assert.Contains(parsed.Errors, e => e.Message.Contains("CSV output is not supported with --dry-run"));
    }

    [Fact]
    public void Execute_DryRunWithJson_Parses()
    {
        Assert.Empty(ExecuteCommand.Create().Parse("abc --dry-run --output-format Json").Errors);
    }

    [Fact]
    public void OptionSet_GetOptions_MapsEachFlag()
    {
        var set = new DmlSafetyOptionSet();
        var command = new Command("t");
        set.AddTo(command);

        var parsed = command.Parse("--confirm --no-limit");
        var options = set.GetOptions(parsed);

        Assert.True(options.IsConfirmed);
        Assert.False(options.IsDryRun);
        Assert.True(options.NoLimit);
    }

    // ---- replay behaviour (same service, same options as 'query sql') ----

    private static SqlQueryRequest HistoryRequest(string sql, bool confirm = false, bool dryRun = false, bool noLimit = false)
        => ExecuteCommand.BuildRequest(sql, top: null,
            new DmlSafetyOptions { IsConfirmed = confirm, IsDryRun = dryRun, NoLimit = noLimit });

    /// <summary>The request 'query sql' builds for the same statement and flags (SqlCommand.ExecuteAsync).</summary>
    private static SqlQueryRequest SqlRequest(string sql, bool confirm = false, bool dryRun = false, bool noLimit = false)
        => new()
        {
            Sql = sql,
            DmlSafety = new DmlSafetyOptions { IsConfirmed = confirm, IsDryRun = dryRun, NoLimit = noLimit }
        };

    private static (SqlQueryService service, Mock<IQueryExecutor> executor, Mock<IBulkOperationExecutor> bulk) Create(
        ProtectionLevel level, bool strict = true)
    {
        var executor = new Mock<IQueryExecutor>(strict ? MockBehavior.Strict : MockBehavior.Loose);
        var bulk = new Mock<IBulkOperationExecutor>(strict ? MockBehavior.Strict : MockBehavior.Loose);
        var service = new SqlQueryService(executor.Object, guard: new InactiveFakeShakedownGuard(), bulkOperationExecutor: bulk.Object)
        {
            EnvironmentProtectionLevel = level
        };
        return (service, executor, bulk);
    }

    private static async Task<string> OutcomeOf(SqlQueryService service, SqlQueryRequest request)
    {
        try
        {
            var result = await service.ExecuteAsync(request);
            return $"ok dryRun={result.DmlSafetyResult?.IsDryRun} cap={result.DmlSafetyResult?.RowCap} " +
                   $"confirm={result.DmlSafetyResult?.RequiresConfirmation}";
        }
        catch (PpdsException ex)
        {
            return $"{ex.ErrorCode}: {ex.Message}";
        }
    }

    [Theory]
    [InlineData(DeleteSql)]
    [InlineData(UpdateSql)]
    public async Task ReplayedDml_WithoutConfirm_IsRefused_WithExitCode11_AndNothingExecuted(string sql)
    {
        var (service, executor, bulk) = Create(ProtectionLevel.Development);

        var ex = await Assert.ThrowsAsync<PpdsException>(() => service.ExecuteAsync(HistoryRequest(sql)));

        Assert.Equal(ErrorCodes.Query.DmlConfirmationRequired, ex.ErrorCode);
        Assert.Equal(ExitCodes.ConfirmationRequired, ExceptionMapper.ToExitCode(ex));
        executor.VerifyNoOtherCalls();
        bulk.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData(DeleteSql, true)]
    [InlineData(UpdateSql, false)]
    public async Task ReplayedDml_WithConfirm_Executes(string sql, bool isDelete)
    {
        var (service, executor, bulk) = Create(ProtectionLevel.Development, strict: false);
        var row = new Dictionary<string, QueryValue> { ["accountid"] = QueryValue.Simple(RowId) };
        var fetched = new QueryResult
        {
            EntityLogicalName = "account",
            Columns = new[] { new QueryColumn { LogicalName = "accountid" } },
            Records = new[] { (IReadOnlyDictionary<string, QueryValue>)row },
            Count = 1
        };
        executor.Setup(x => x.ExecuteFetchXmlAsync(It.IsAny<string>(), It.IsAny<int?>(), It.IsAny<string?>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(fetched);
        executor.Setup(x => x.ExecuteFetchXmlAsync(It.IsAny<string>(), It.IsAny<int?>(), It.IsAny<string?>(), It.IsAny<bool>(), It.IsAny<QueryExecutionOptions?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(fetched);
        var ok = new BulkOperationResult { SuccessCount = 1 };
        bulk.Setup(b => b.DeleteMultipleAsync(It.IsAny<string>(), It.IsAny<IEnumerable<Guid>>(), It.IsAny<BulkOperationOptions?>(), It.IsAny<PPDS.Dataverse.Client.DataverseClientOptions?>(), It.IsAny<IProgress<PPDS.Dataverse.Progress.ProgressSnapshot>?>(), It.IsAny<CancellationToken>())).ReturnsAsync(ok);
        bulk.Setup(b => b.UpdateMultipleAsync(It.IsAny<string>(), It.IsAny<IEnumerable<Microsoft.Xrm.Sdk.Entity>>(), It.IsAny<BulkOperationOptions?>(), It.IsAny<PPDS.Dataverse.Client.DataverseClientOptions?>(), It.IsAny<IProgress<PPDS.Dataverse.Progress.ProgressSnapshot>?>(), It.IsAny<CancellationToken>())).ReturnsAsync(ok);

        var result = await service.ExecuteAsync(HistoryRequest(sql, confirm: true));

        Assert.False(result.DmlOutcome!.HasFailures);
        Assert.Equal(1, result.DmlOutcome.AffectedRows);
        if (isDelete)
            bulk.Verify(b => b.DeleteMultipleAsync("account", It.IsAny<IEnumerable<Guid>>(), It.IsAny<BulkOperationOptions?>(), It.IsAny<PPDS.Dataverse.Client.DataverseClientOptions?>(), It.IsAny<IProgress<PPDS.Dataverse.Progress.ProgressSnapshot>?>(), It.IsAny<CancellationToken>()), Times.Once);
        else
            bulk.Verify(b => b.UpdateMultipleAsync("account", It.IsAny<IEnumerable<Microsoft.Xrm.Sdk.Entity>>(), It.IsAny<BulkOperationOptions?>(), It.IsAny<PPDS.Dataverse.Client.DataverseClientOptions?>(), It.IsAny<IProgress<PPDS.Dataverse.Progress.ProgressSnapshot>?>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Theory]
    [InlineData(DeleteSql)]
    [InlineData(UpdateSql)]
    public async Task ReplayedDml_DryRun_ReturnsPlanAndExecutesNothing(string sql)
    {
        var (service, executor, bulk) = Create(ProtectionLevel.Development);

        var result = await service.ExecuteAsync(HistoryRequest(sql, dryRun: true));

        Assert.True(result.DmlSafetyResult!.IsDryRun);
        Assert.NotNull(result.DryRunPlan);
        executor.VerifyNoOtherCalls();
        bulk.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData("DELETE FROM account", false)]
    [InlineData("DELETE FROM account", true)]
    [InlineData("UPDATE account SET name = 'x'", true)]
    public async Task ReplayedDmlWithoutWhere_IsBlocked_EvenConfirmed_LikeSql(string sql, bool confirm)
    {
        var (service, executor, bulk) = Create(ProtectionLevel.Development);

        var ex = await Assert.ThrowsAsync<PpdsException>(() => service.ExecuteAsync(HistoryRequest(sql, confirm: confirm)));

        Assert.Equal(ErrorCodes.Query.DmlBlocked, ex.ErrorCode);
        Assert.Equal(await OutcomeOf(service, SqlRequest(sql, confirm: confirm)), $"{ex.ErrorCode}: {ex.Message}");
        executor.VerifyNoOtherCalls();
        bulk.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReplayedDml_RowCap_MatchesSql_AndNoLimitChangesIt(bool noLimit)
    {
        var (service, _, _) = Create(ProtectionLevel.Development);

        var history = await OutcomeOf(service, HistoryRequest(DeleteSql, dryRun: true, noLimit: noLimit));
        var sql = await OutcomeOf(service, SqlRequest(DeleteSql, dryRun: true, noLimit: noLimit));

        Assert.Equal(sql, history);
        if (!noLimit)
            Assert.Contains($"cap={DmlSafetyGuard.DefaultRowCap}", history);
        else
            Assert.DoesNotContain($"cap={DmlSafetyGuard.DefaultRowCap}", history);
    }

    [Theory]
    [InlineData(ProtectionLevel.Production, false, false)]
    [InlineData(ProtectionLevel.Production, true, true)]
    [InlineData(ProtectionLevel.Production, false, true)]
    [InlineData(ProtectionLevel.Test, false, false)]
    [InlineData(ProtectionLevel.Test, false, true)]
    [InlineData(ProtectionLevel.Development, false, false)]
    public async Task ReplayedDml_ProtectedEnvironment_IsExactlyAsHardAsSql(ProtectionLevel level, bool confirm, bool dryRun)
    {
        var (service, _, _) = Create(level);

        var history = await OutcomeOf(service, HistoryRequest(DeleteSql, confirm, dryRun));
        var sql = await OutcomeOf(service, SqlRequest(DeleteSql, confirm, dryRun));

        Assert.Equal(sql, history);
    }

    [Fact]
    public async Task ReplayedDml_Production_WithoutConfirm_IsRefused()
    {
        var (service, executor, bulk) = Create(ProtectionLevel.Production);

        var ex = await Assert.ThrowsAsync<PpdsException>(() => service.ExecuteAsync(HistoryRequest(DeleteSql)));

        Assert.Equal(ErrorCodes.Query.DmlConfirmationRequired, ex.ErrorCode);
        executor.VerifyNoOtherCalls();
        bulk.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task ReplayedSelect_IsUnchanged_NoGuardInvolved()
    {
        var (service, executor, _) = Create(ProtectionLevel.Production, strict: false);
        var fetched = QueryResult.Empty("account");
        executor.Setup(x => x.ExecuteFetchXmlAsync(It.IsAny<string>(), It.IsAny<int?>(), It.IsAny<string?>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(fetched);
        executor.Setup(x => x.ExecuteFetchXmlAsync(It.IsAny<string>(), It.IsAny<int?>(), It.IsAny<string?>(), It.IsAny<bool>(), It.IsAny<QueryExecutionOptions?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(fetched);

        var result = await service.ExecuteAsync(HistoryRequest("SELECT name FROM account"));

        Assert.Null(result.DmlOutcome);
        Assert.NotNull(result.Result);
    }

    [Fact]
    public void BuildRequest_AlwaysCarriesDmlSafety()
    {
        var request = ExecuteCommand.BuildRequest(DeleteSql, 5, new DmlSafetyOptions());

        Assert.NotNull(request.DmlSafety);
        Assert.Equal(5, request.TopOverride);
    }
}

using System.CommandLine;
using System.Text.Json;
using Moq;
using PPDS.Auth.Profiles;
using PPDS.Cli.Commands;
using PPDS.Cli.Commands.Query;
using PPDS.Cli.Commands.Query.History;
using PPDS.Cli.Infrastructure;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Cli.Infrastructure.Output;
using PPDS.Cli.Services.History;
using PPDS.Cli.Services.Query;
using PPDS.Cli.Tests.Services.Shared;
using PPDS.Cli.Tests.TestHelpers;
using PPDS.Dataverse.BulkOperations;
using PPDS.Dataverse.Query;
using Xunit;

namespace PPDS.Cli.Tests.Commands.Query;

/// <summary>
/// 'ppds query history execute' must behave exactly like 'ppds query sql' with the same DML guard options.
/// These tests drive <see cref="ExecuteCommand.ExecuteEntryAsync"/> (the command body behind the profile
/// seam), so they fail if the command stops sending DmlSafety, stops configuring the environment safety
/// settings, or stops showing the statement.
/// </summary>
[Trait("Category", "Unit")]
[Collection(nameof(ConsoleCaptureCollection))]
public class HistoryExecuteCommandTests : IDisposable
{
    private const string EnvUrl = "https://org.crm.dynamics.com";
    private const string DeleteSql = "DELETE FROM account WHERE name = 'x'";
    private const string UpdateSql = "UPDATE account SET name = 'y' WHERE name = 'x'";

    private readonly string _configPath = Path.Combine(Path.GetTempPath(), $"ppds-env-{Guid.NewGuid():N}.json");
    private readonly EnvironmentConfigStore _store;

    public HistoryExecuteCommandTests()
    {
        _store = new EnvironmentConfigStore(_configPath);
    }

    public void Dispose()
    {
        _store.Dispose();
        try { File.Delete(_configPath); } catch (IOException) { /* Clean-up of a temporary file is best-effort. */ }
    }

    private void WriteEnvConfig(EnvironmentType? type = null, ProtectionLevel? protection = null)
    {
        var collection = new EnvironmentConfigCollection
        {
            Environments = { new EnvironmentConfig { Url = EnvUrl, Type = type, Protection = protection } }
        };
        File.WriteAllText(_configPath, JsonSerializer.Serialize(collection));
    }

    private static Mock<IQueryHistoryService> History(string sql, string id = "abc123")
    {
        var history = new Mock<IQueryHistoryService>();
        history.Setup(h => h.GetEntryByIdAsync(It.IsAny<string>(), id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new QueryHistoryEntry { Id = id, Sql = sql, ExecutedAt = DateTimeOffset.UtcNow });
        return history;
    }

    private static (SqlQueryService service, Mock<IQueryExecutor> executor, Mock<IBulkOperationExecutor> bulk) Service(
        MockBehavior behavior = MockBehavior.Strict)
    {
        var executor = new Mock<IQueryExecutor>(behavior);
        var bulk = new Mock<IBulkOperationExecutor>(behavior);
        var service = new SqlQueryService(executor.Object, guard: new InactiveFakeShakedownGuard(), bulkOperationExecutor: bulk.Object, metadataProvider: PPDS.Cli.Tests.Services.Query.TestMetadata.Provider);
        return (service, executor, bulk);
    }

    private static void ReturnThreeAccounts(Mock<IQueryExecutor> executor, Mock<IBulkOperationExecutor> bulk, int rows = 1)
    {
        var records = Enumerable.Range(0, rows).Select(_ => (IReadOnlyDictionary<string, QueryValue>)new Dictionary<string, QueryValue>
        {
            ["accountid"] = QueryValue.Simple(Guid.NewGuid())
        }).ToArray();
        var fetched = new QueryResult
        {
            EntityLogicalName = "account",
            Columns = new[] { new QueryColumn { LogicalName = "accountid" } },
            Records = records,
            Count = rows
        };
        executor.Setup(x => x.ExecuteFetchXmlAsync(It.IsAny<string>(), It.IsAny<int?>(), It.IsAny<string?>(), It.IsAny<bool>(), It.IsAny<CancellationToken>())).ReturnsAsync(fetched);
        executor.Setup(x => x.ExecuteFetchXmlAsync(It.IsAny<string>(), It.IsAny<int?>(), It.IsAny<string?>(), It.IsAny<bool>(), It.IsAny<QueryExecutionOptions?>(), It.IsAny<CancellationToken>())).ReturnsAsync(fetched);
        var ok = new BulkOperationResult { SuccessCount = rows };
        bulk.Setup(b => b.DeleteMultipleAsync(It.IsAny<string>(), It.IsAny<IEnumerable<Guid>>(), It.IsAny<BulkOperationOptions?>(), It.IsAny<PPDS.Dataverse.Client.DataverseClientOptions?>(), It.IsAny<IProgress<PPDS.Dataverse.Progress.ProgressSnapshot>?>(), It.IsAny<CancellationToken>())).ReturnsAsync(ok);
        bulk.Setup(b => b.UpdateMultipleAsync(It.IsAny<string>(), It.IsAny<IEnumerable<Microsoft.Xrm.Sdk.Entity>>(), It.IsAny<BulkOperationOptions?>(), It.IsAny<PPDS.Dataverse.Client.DataverseClientOptions?>(), It.IsAny<IProgress<PPDS.Dataverse.Progress.ProgressSnapshot>?>(), It.IsAny<CancellationToken>())).ReturnsAsync(ok);
    }

    private async Task<(int exit, string stdout, string stderr)> Replay(
        SqlQueryService service, string sql, DmlSafetyOptions options, int? top = null,
        OutputFormat format = OutputFormat.Json, string id = "abc123")
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var originalError = Console.Error;
        var originalOut = Console.Out;
        try
        {
            Console.SetError(stderr);
            Console.SetOut(stdout);
            var globalOptions = new GlobalOptionValues { OutputFormat = format };
            IOutputWriter writer = format == OutputFormat.Json ? new JsonOutputWriter(stdout) : new TextOutputWriter();
            var exit = await ExecuteCommand.ExecuteEntryAsync(
                id, top, options, EnvUrl, History(sql, id).Object, service, _store,
                wireRemoteExecutors: null, writeHeader: null, globalOptions, writer, CancellationToken.None);
            return (exit, stdout.ToString(), stderr.ToString());
        }
        finally
        {
            Console.SetError(originalError);
            Console.SetOut(originalOut);
        }
    }

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
        var command = ExecuteCommand.Create();
        var parsed = command.Parse("abc");

        Assert.Empty(parsed.Errors);
        var options = DmlSafetyOptionSet.Read(command, parsed);
        Assert.False(options.IsConfirmed);
        Assert.False(options.IsDryRun);
        Assert.False(options.NoLimit);
    }

    [Theory]
    [InlineData("", false, false, false)]
    [InlineData("--confirm", true, false, false)]
    [InlineData("--dry-run", false, true, false)]
    [InlineData("--no-limit", false, false, true)]
    [InlineData("--confirm --no-limit", true, false, true)]
    public void BothCommands_ParseTheGuardFlagsToTheSameOptions(string flags, bool confirm, bool dryRun, bool noLimit)
    {
        var sql = SqlCommand.Create();
        var execute = ExecuteCommand.Create();

        var fromSql = DmlSafetyOptionSet.Read(sql, sql.Parse($"\"{DeleteSql}\" {flags}"));
        var fromExecute = DmlSafetyOptionSet.Read(execute, execute.Parse($"abc {flags}"));

        Assert.Equal((confirm, dryRun, noLimit), (fromSql.IsConfirmed, fromSql.IsDryRun, fromSql.NoLimit));
        Assert.Equal((fromSql.IsConfirmed, fromSql.IsDryRun, fromSql.NoLimit),
            (fromExecute.IsConfirmed, fromExecute.IsDryRun, fromExecute.NoLimit));
    }

    [Fact]
    public void Execute_DryRunWithCsv_IsRejected_LikeSql()
    {
        var execute = ExecuteCommand.Create().Parse("abc --dry-run --output-format Csv");
        var sql = SqlCommand.Create().Parse($"\"{DeleteSql}\" --dry-run --output-format Csv");

        Assert.Contains(execute.Errors, e => e.Message.Contains("CSV output is not supported with --dry-run"));
        Assert.Contains(sql.Errors, e => e.Message.Contains("CSV output is not supported with --dry-run"));
    }

    [Fact]
    public void Execute_DryRunWithJson_Parses()
    {
        Assert.Empty(ExecuteCommand.Create().Parse("abc --dry-run --output-format Json").Errors);
    }

    // ---- ExecuteEntryAsync: what the command actually does ---------------

    [Theory]
    [InlineData(DeleteSql)]
    [InlineData(UpdateSql)]
    public async Task Replay_DmlWithoutConfirm_IsRefused_Exit11_StatementShownFirst_NothingExecuted(string sql)
    {
        WriteEnvConfig(EnvironmentType.Development);
        var (service, executor, bulk) = Service();

        var (exit, stdout, stderr) = await Replay(service, sql, new DmlSafetyOptions());

        Assert.Equal(ExitCodes.ConfirmationRequired, exit);
        Assert.Contains(sql, stderr);
        using var json = JsonDocument.Parse(stdout);
        Assert.Equal(ErrorCodes.Query.DmlConfirmationRequired, json.RootElement.GetProperty("error").GetProperty("code").GetString());
        executor.VerifyNoOtherCalls();
        bulk.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData(DeleteSql, true)]
    [InlineData(UpdateSql, false)]
    public async Task Replay_DmlWithConfirm_Executes(string sql, bool isDelete)
    {
        WriteEnvConfig(EnvironmentType.Development);
        var (service, executor, bulk) = Service(MockBehavior.Loose);
        ReturnThreeAccounts(executor, bulk);

        var (exit, stdout, stderr) = await Replay(service, sql, new DmlSafetyOptions { IsConfirmed = true });

        Assert.Equal(ExitCodes.Success, exit);
        Assert.Contains(sql, stderr);
        using var json = JsonDocument.Parse(stdout);
        Assert.True(json.RootElement.GetProperty("success").GetBoolean());
        if (isDelete)
            bulk.Verify(b => b.DeleteMultipleAsync("account", It.IsAny<IEnumerable<Guid>>(), It.IsAny<BulkOperationOptions?>(), It.IsAny<PPDS.Dataverse.Client.DataverseClientOptions?>(), It.IsAny<IProgress<PPDS.Dataverse.Progress.ProgressSnapshot>?>(), It.IsAny<CancellationToken>()), Times.Once);
        else
            bulk.Verify(b => b.UpdateMultipleAsync("account", It.IsAny<IEnumerable<Microsoft.Xrm.Sdk.Entity>>(), It.IsAny<BulkOperationOptions?>(), It.IsAny<PPDS.Dataverse.Client.DataverseClientOptions?>(), It.IsAny<IProgress<PPDS.Dataverse.Progress.ProgressSnapshot>?>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Theory]
    [InlineData(DeleteSql, false, DmlSafetyGuard.DefaultRowCap)]
    [InlineData(UpdateSql, false, DmlSafetyGuard.DefaultRowCap)]
    [InlineData(DeleteSql, true, int.MaxValue)]
    public async Task Replay_DryRun_PrintsPlan_WithTheRowCap_ExecutesNothing(string sql, bool noLimit, int expectedCap)
    {
        WriteEnvConfig(EnvironmentType.Development);
        var (service, executor, bulk) = Service();

        var (exit, stdout, stderr) = await Replay(service, sql, new DmlSafetyOptions { IsDryRun = true, NoLimit = noLimit });

        Assert.Equal(ExitCodes.Success, exit);
        using var json = JsonDocument.Parse(stdout);
        Assert.True(json.RootElement.GetProperty("data").GetProperty("dryRun").GetBoolean());
        Assert.Equal(expectedCap, json.RootElement.GetProperty("data").GetProperty("rowCap").GetInt32());
        Assert.Contains("--dry-run", stderr);
        executor.VerifyNoOtherCalls();
        bulk.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData("DELETE FROM account")]
    [InlineData("UPDATE account SET name = 'x'")]
    public async Task Replay_DmlWithoutWhere_IsBlocked_EvenConfirmed(string sql)
    {
        WriteEnvConfig(EnvironmentType.Development);
        var (service, executor, bulk) = Service();

        var (exit, stdout, _) = await Replay(service, sql, new DmlSafetyOptions { IsConfirmed = true });

        Assert.Equal(ExitCodes.Failure, exit);
        using var json = JsonDocument.Parse(stdout);
        Assert.Equal(ErrorCodes.Query.DmlBlocked, json.RootElement.GetProperty("error").GetProperty("code").GetString());
        executor.VerifyNoOtherCalls();
        bulk.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Replay_AppliesTheEnvironmentSafetyConfiguration_FromTheStore()
    {
        WriteEnvConfig(EnvironmentType.Development);
        var (service, _, _) = Service();
        Assert.Equal(ProtectionLevel.Production, service.EnvironmentProtectionLevel); // the fail-closed default

        await Replay(service, "SELECT name FROM account", new DmlSafetyOptions(), format: OutputFormat.Json);

        Assert.Equal(ProtectionLevel.Development, service.EnvironmentProtectionLevel);
    }

    [Fact]
    public async Task Replay_ExplicitProtectionOverride_WinsOverTheEnvironmentType()
    {
        WriteEnvConfig(EnvironmentType.Development, ProtectionLevel.Test);
        var (service, _, _) = Service();

        await Replay(service, "SELECT name FROM account", new DmlSafetyOptions());

        Assert.Equal(ProtectionLevel.Test, service.EnvironmentProtectionLevel);
    }

    [Theory]
    [InlineData(ProtectionLevel.Production, "", ExitCodes.ConfirmationRequired, false)]
    [InlineData(ProtectionLevel.Production, "dry-run", ExitCodes.Success, false)]
    [InlineData(ProtectionLevel.Production, "confirm", ExitCodes.Success, true)]
    [InlineData(ProtectionLevel.Test, "", ExitCodes.ConfirmationRequired, false)]
    [InlineData(ProtectionLevel.Test, "dry-run", ExitCodes.Success, false)]
    [InlineData(ProtectionLevel.Test, "confirm", ExitCodes.Success, true)]
    public async Task Replay_ProtectedEnvironment_IsExactlyAsHardAsSql(
        ProtectionLevel protection, string flag, int expectedExit, bool executes)
    {
        WriteEnvConfig(EnvironmentType.Sandbox, protection);
        var (service, executor, bulk) = Service(MockBehavior.Loose);
        ReturnThreeAccounts(executor, bulk);
        var options = new DmlSafetyOptions { IsConfirmed = flag == "confirm", IsDryRun = flag == "dry-run" };

        var (exit, _, _) = await Replay(service, DeleteSql, options);

        Assert.Equal(expectedExit, exit);
        Assert.Equal(protection, service.EnvironmentProtectionLevel);
        bulk.Verify(b => b.DeleteMultipleAsync(It.IsAny<string>(), It.IsAny<IEnumerable<Guid>>(), It.IsAny<BulkOperationOptions?>(), It.IsAny<PPDS.Dataverse.Client.DataverseClientOptions?>(), It.IsAny<IProgress<PPDS.Dataverse.Progress.ProgressSnapshot>?>(), It.IsAny<CancellationToken>()),
            executes ? Times.Once() : Times.Never());
    }

    [Fact]
    public async Task Replay_ProductionEnvironment_StillRefusesUnconfirmedDml()
    {
        WriteEnvConfig(EnvironmentType.Production);
        var (service, executor, bulk) = Service();

        var (exit, _, _) = await Replay(service, DeleteSql, new DmlSafetyOptions());

        Assert.Equal(ExitCodes.ConfirmationRequired, exit);
        executor.VerifyNoOtherCalls();
        bulk.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Replay_Select_IsUnchanged()
    {
        WriteEnvConfig(EnvironmentType.Production);
        var (service, executor, _) = Service(MockBehavior.Loose);
        var fetched = QueryResult.Empty("account");
        executor.Setup(x => x.ExecuteFetchXmlAsync(It.IsAny<string>(), It.IsAny<int?>(), It.IsAny<string?>(), It.IsAny<bool>(), It.IsAny<CancellationToken>())).ReturnsAsync(fetched);
        executor.Setup(x => x.ExecuteFetchXmlAsync(It.IsAny<string>(), It.IsAny<int?>(), It.IsAny<string?>(), It.IsAny<bool>(), It.IsAny<QueryExecutionOptions?>(), It.IsAny<CancellationToken>())).ReturnsAsync(fetched);

        var (exit, stdout, _) = await Replay(service, "SELECT name FROM account", new DmlSafetyOptions());

        Assert.Equal(ExitCodes.Success, exit);
        using var json = JsonDocument.Parse(stdout);
        Assert.True(json.RootElement.GetProperty("success").GetBoolean());
    }

    [Fact]
    public void HistoryGet_TextOutput_KeepsSqlExactWhenRedirected_SanitisesItOnATerminal_AndAlwaysSanitisesTheError()
    {
        var esc = ((char)0x1B).ToString();
        var entry = new QueryHistoryEntry
        {
            Id = "abc", Sql = $"SELECT 1 /*{esc}[2K*/\r\nFROM account", ExecutedAt = DateTimeOffset.UtcNow,
            ErrorMessage = $"bad{esc}[31m"
        };

        using var redirected = new StringWriter();
        PPDS.Cli.Commands.Query.History.GetCommand.WriteTextOutput(entry, redirected, toTerminal: false);
        using var terminal = new StringWriter();
        PPDS.Cli.Commands.Query.History.GetCommand.WriteTextOutput(entry, terminal, toTerminal: true);

        Assert.Contains(entry.Sql, redirected.ToString(), StringComparison.Ordinal); // data stays exact
        Assert.DoesNotContain(esc, terminal.ToString(), StringComparison.Ordinal);
        Assert.Contains("Error:        bad\\u001B[31m", redirected.ToString());
        Assert.Contains("Error:        bad\\u001B[31m", terminal.ToString());
    }

    [Fact]
    public async Task Replay_UnknownId_IsNotFound()
    {
        WriteEnvConfig();
        var (service, _, _) = Service();
        var history = new Mock<IQueryHistoryService>();
        history.Setup(h => h.GetEntryByIdAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(default(QueryHistoryEntry));
        using var stdout = new StringWriter();

        var exit = await ExecuteCommand.ExecuteEntryAsync(
            "nope", null, new DmlSafetyOptions(), EnvUrl, history.Object, service, _store, null, null,
            new GlobalOptionValues { OutputFormat = OutputFormat.Json }, new JsonOutputWriter(stdout), CancellationToken.None);

        Assert.Equal(ExitCodes.NotFoundError, exit);
    }

    [Fact]
    public async Task Replay_EscapesControlCharactersInTheEchoedStatement()
    {
        WriteEnvConfig(EnvironmentType.Development);
        var (service, _, _) = Service();
        var esc = ((char)0x1B).ToString();
        var sql = $"DELETE FROM account WHERE 1=1 /*{esc}[2K\rSELECT 1 */";

        var (_, _, stderr) = await Replay(service, sql, new DmlSafetyOptions());

        Assert.DoesNotContain(esc, stderr, StringComparison.Ordinal);
        Assert.DoesNotContain("\r", stderr.Replace("\r\n", "\n"), StringComparison.Ordinal); // no lone carriage returns
        Assert.Contains("\\u001B[2K\\u000DSELECT 1", stderr);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Replay_DmlWithExplicitTop_IsRefused_Exit3_NothingExecuted(bool dryRun)
    {
        WriteEnvConfig(EnvironmentType.Development);
        var (service, executor, bulk) = Service();

        var (exit, stdout, stderr) = await Replay(
            service, DeleteSql, new DmlSafetyOptions { IsConfirmed = true, IsDryRun = dryRun }, top: 5);

        Assert.Equal(ExitCodes.InvalidArguments, exit);
        Assert.Contains(DeleteSql, stderr); // the statement was shown before the refusal
        using var json = JsonDocument.Parse(stdout);
        Assert.Contains("--top limits returned rows only", json.RootElement.GetProperty("error").GetProperty("message").GetString());
        executor.VerifyNoOtherCalls();
        bulk.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Replay_SelectWithTop_StillWorks()
    {
        WriteEnvConfig(EnvironmentType.Development);
        var (service, executor, _) = Service(MockBehavior.Loose);
        var fetched = QueryResult.Empty("account");
        executor.Setup(x => x.ExecuteFetchXmlAsync(It.IsAny<string>(), It.IsAny<int?>(), It.IsAny<string?>(), It.IsAny<bool>(), It.IsAny<CancellationToken>())).ReturnsAsync(fetched);
        executor.Setup(x => x.ExecuteFetchXmlAsync(It.IsAny<string>(), It.IsAny<int?>(), It.IsAny<string?>(), It.IsAny<bool>(), It.IsAny<QueryExecutionOptions?>(), It.IsAny<CancellationToken>())).ReturnsAsync(fetched);

        var (exit, _, _) = await Replay(service, "SELECT name FROM account", new DmlSafetyOptions(), top: 5);

        Assert.Equal(ExitCodes.Success, exit);
    }

    [Fact]
    public async Task Replay_DmlOverTheDefaultRowCap_IsRefused_Exit2_UnlessNoLimit()
    {
        WriteEnvConfig(EnvironmentType.Development);
        var (service, executor, bulk) = Service(MockBehavior.Loose);
        ReturnThreeAccounts(executor, bulk, rows: DmlSafetyGuard.DefaultRowCap + 1);

        var (exit, stdout, _) = await Replay(service, DeleteSql, new DmlSafetyOptions { IsConfirmed = true });

        Assert.Equal(ExitCodes.Failure, exit);
        using var json = JsonDocument.Parse(stdout);
        Assert.Equal(ErrorCodes.Query.DmlRowCapExceeded, json.RootElement.GetProperty("error").GetProperty("code").GetString());
        bulk.Verify(b => b.DeleteMultipleAsync(It.IsAny<string>(), It.IsAny<IEnumerable<Guid>>(), It.IsAny<BulkOperationOptions?>(), It.IsAny<PPDS.Dataverse.Client.DataverseClientOptions?>(), It.IsAny<IProgress<PPDS.Dataverse.Progress.ProgressSnapshot>?>(), It.IsAny<CancellationToken>()), Times.Never);

        var (exit2, _, _) = await Replay(service, DeleteSql, new DmlSafetyOptions { IsConfirmed = true, NoLimit = true });

        Assert.Equal(ExitCodes.Success, exit2);
    }

    [Fact]
    public async Task Replay_ScriptInterruptedAfterAStatementRan_ReportsWhatAlreadyRan()
    {
        WriteEnvConfig(EnvironmentType.Development);
        var (service, executor, bulk) = Service(MockBehavior.Loose);
        ReturnThreeAccounts(executor, bulk, rows: 2);
        var calls = 0;
        bulk.Setup(b => b.DeleteMultipleAsync(It.IsAny<string>(), It.IsAny<IEnumerable<Guid>>(), It.IsAny<BulkOperationOptions?>(), It.IsAny<PPDS.Dataverse.Client.DataverseClientOptions?>(), It.IsAny<IProgress<PPDS.Dataverse.Progress.ProgressSnapshot>?>(), It.IsAny<CancellationToken>()))
            .Returns((string _, IEnumerable<Guid> ids, BulkOperationOptions? _, PPDS.Dataverse.Client.DataverseClientOptions? _, IProgress<PPDS.Dataverse.Progress.ProgressSnapshot>? _, CancellationToken _) =>
            {
                if (++calls == 2) throw new InvalidOperationException("connection lost");
                return Task.FromResult(new BulkOperationResult { SuccessCount = ids.Count() });
            });

        var (exit, stdout, stderr) = await Replay(
            service, "DELETE FROM account WHERE name = 'a'; DELETE FROM account WHERE name = 'b'",
            new DmlSafetyOptions { IsConfirmed = true });

        Assert.Equal(ExitCodes.Failure, exit);
        Assert.Contains("not rolled back", stderr);
        Assert.Contains("#1 DELETE account: 2 affected, 0 failed", stderr);
        Assert.Contains("#2 DELETE account was running when the run was interrupted and may be partly applied", stderr);
        // JSON mode also carries the note in the error envelope (additive), for scripts that read only stdout.
        using var json = JsonDocument.Parse(stdout);
        Assert.Contains("#1 DELETE account: 2 affected", json.RootElement.GetProperty("error").GetProperty("dmlCommitted").GetString());
    }

    // ---- request building ------------------------------------------------

    [Theory]
    [InlineData("", false, false, false)]
    [InlineData("--confirm --no-limit", true, false, true)]
    [InlineData("--dry-run", false, true, false)]
    public void BothCommands_BuildEquivalentGuardedRequests(string flags, bool confirm, bool dryRun, bool noLimit)
    {
        var sql = SqlCommand.Create();
        var execute = ExecuteCommand.Create();
        var sqlOptions = DmlSafetyOptionSet.Read(sql, sql.Parse($"\"{DeleteSql}\" {flags}"));
        var executeOptions = DmlSafetyOptionSet.Read(execute, execute.Parse($"abc {flags}"));

        var fromSql = SqlCommand.BuildRequest(DeleteSql, null, null, null, false, false, sqlOptions);
        var fromHistory = ExecuteCommand.BuildRequest(DeleteSql, null, executeOptions);

        Assert.NotNull(fromSql.DmlSafety);
        Assert.NotNull(fromHistory.DmlSafety);
        Assert.Equal(
            (fromSql.Sql, fromSql.TopOverride, fromSql.DmlSafety!.IsConfirmed, fromSql.DmlSafety.IsDryRun, fromSql.DmlSafety.NoLimit, fromSql.DmlSafety.RowCap),
            (fromHistory.Sql, fromHistory.TopOverride, fromHistory.DmlSafety!.IsConfirmed, fromHistory.DmlSafety.IsDryRun, fromHistory.DmlSafety.NoLimit, fromHistory.DmlSafety.RowCap));
        Assert.Equal((confirm, dryRun, noLimit), (fromHistory.DmlSafety.IsConfirmed, fromHistory.DmlSafety.IsDryRun, fromHistory.DmlSafety.NoLimit));
    }

    [Fact]
    public void BuildRequest_AlwaysCarriesTheGivenDmlSafety()
    {
        var options = new DmlSafetyOptions { IsConfirmed = true };

        var request = ExecuteCommand.BuildRequest(DeleteSql, 5, options);

        Assert.Same(options, request.DmlSafety);
        Assert.Equal(5, request.TopOverride);
    }
}

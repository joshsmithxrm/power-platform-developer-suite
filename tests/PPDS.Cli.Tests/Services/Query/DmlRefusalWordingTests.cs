using System.Text.Json;
using PPDS.Cli.Commands.Query;
using PPDS.Cli.Commands.Serve.Handlers;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Cli.Services.Query;
using PPDS.Cli.Tui.Screens;
using Xunit;

namespace PPDS.Cli.Tests.Services.Query;

/// <summary>
/// Guard refusals are surface-neutral in the service; the CLI names its flags, the TUI says what it cannot do,
/// and RPC carries structured data so a client can word it.
/// </summary>
[Trait("Category", "Unit")]
public class DmlRefusalWordingTests
{
    private const string DeleteSql = "DELETE FROM account WHERE name = 'x'";

    [Fact]
    public async Task ConfirmationRequired_ServiceMessage_NamesNoCliFlag_TheCliWordsItWithItsFlags()
    {
        var h = DmlTestHarness.Create(rows: 1);

        var ex = await Assert.ThrowsAsync<PpdsDmlConfirmationRequiredException>(() => h.Service.ExecuteAsync(
            new SqlQueryRequest { Sql = DeleteSql, DmlSafety = new DmlSafetyOptions() }));

        Assert.Equal(SqlQueryService.ConfirmationRequiredMessage, ex.UserMessage);
        Assert.DoesNotContain("--", ex.UserMessage);
        Assert.Contains("Nothing was executed", ex.UserMessage);

        var cli = DmlSafetyOptionSet.WithCliWording(ExceptionMapper.Map(ex), ex);
        Assert.Equal("DML operations require --confirm to execute. Use --dry-run to preview the operation.", cli.Message);
        Assert.Equal(ErrorCodes.Query.DmlConfirmationRequired, cli.Code);
    }

    [Fact]
    public async Task ConfirmationRequired_Rpc_ListsTheEnvironmentItWouldWriteTo()
    {
        var h = DmlTestHarness.Create(rows: 1, protection: PPDS.Auth.Profiles.ProtectionLevel.Production);
        h.Service.ConfigureEnvironment("https://prod.crm.dynamics.com/", new PPDS.Auth.Profiles.EnvironmentConfig
        {
            Url = "https://prod.crm.dynamics.com/",
            Label = "PROD",
            Type = PPDS.Auth.Profiles.EnvironmentType.Production
        });

        var ex = await Assert.ThrowsAsync<PpdsDmlConfirmationRequiredException>(() => h.Service.ExecuteAsync(
            new SqlQueryRequest { Sql = DeleteSql, DmlSafety = new DmlSafetyOptions() }));
        Assert.True(RpcMethodHandler.TryMapQuerySqlFailure(ex, DeleteSql, out var rpc));

        var data = JsonDocument.Parse(JsonSerializer.Serialize(rpc!.ErrorData, rpc.ErrorData!.GetType())).RootElement;
        Assert.True(data.GetProperty("dmlConfirmationRequired").GetBoolean());
        var target = Assert.Single(data.GetProperty("dmlTargets").EnumerateArray().ToList());
        Assert.Equal("PROD", target.GetProperty("label").GetString());
        Assert.Equal("https://prod.crm.dynamics.com/", target.GetProperty("url").GetString());
        Assert.Equal("Production", target.GetProperty("type").GetString());
        Assert.Equal("Production", target.GetProperty("protection").GetString());
    }

    [Fact]
    public async Task ConfirmationRequired_ForAnUnconfiguredEnvironment_ReportsItsUrlAndTheProtectionApplied()
    {
        var h = DmlTestHarness.Create(rows: 1);
        h.Service.ConfigureEnvironment("https://dev.crm.dynamics.com/", config: null);

        var ex = await Assert.ThrowsAsync<PpdsDmlConfirmationRequiredException>(() => h.Service.ExecuteAsync(
            new SqlQueryRequest { Sql = DeleteSql, DmlSafety = new DmlSafetyOptions() }));

        var target = Assert.Single(ex.Targets);
        // No saved entry is Production (#1891's shared rule), whatever level the service had before.
        Assert.Equal(new DmlTarget(null, "https://dev.crm.dynamics.com/", null, "Production"), target);
    }

    [Fact]
    public async Task ReadOnlyCrossEnvironmentWrite_IsRefusedBeforeConfirmationIsAskedFor()
    {
        var h = DmlTestHarness.Create(rows: 1);
        h.Service.RemoteExecutorFactory = _ => new Moq.Mock<PPDS.Dataverse.Query.IQueryExecutor>().Object;
        h.Service.ProfileResolver = new PPDS.Cli.Services.ProfileResolutionService(
        [
            new PPDS.Auth.Profiles.EnvironmentConfig
            {
                Url = "https://qa.crm.dynamics.com/", Label = "QA",
                SafetySettings = new PPDS.Auth.Profiles.QuerySafetySettings { CrossEnvironmentDmlPolicy = PPDS.Auth.Profiles.CrossEnvironmentDmlPolicy.ReadOnly }
            }
        ]);

        // Unconfirmed: the policy refusal comes first, so confirming would not lead to it.
        var ex = await Assert.ThrowsAsync<PpdsException>(() => h.Service.ExecuteAsync(
            new SqlQueryRequest { Sql = "DELETE FROM [QA].account WHERE name = 'x'", DmlSafety = new DmlSafetyOptions() }));

        Assert.Equal(ErrorCodes.Query.DmlBlocked, ex.ErrorCode);
        Assert.Contains("read-only", ex.Message);
    }

    [Fact]
    public async Task RowCap_Rpc_CarriesTheCapOperationAndEntity_AndANeutralMessage()
    {
        var h = DmlTestHarness.Create(rows: 3);
        var ex = await Assert.ThrowsAsync<PpdsDmlRowCapExceededException>(() => h.Service.ExecuteAsync(
            new SqlQueryRequest { Sql = DeleteSql, DmlSafety = new DmlSafetyOptions { IsConfirmed = true, RowCap = 2 } }));

        Assert.True(RpcMethodHandler.TryMapQuerySqlFailure(ex, DeleteSql, out var rpc));

        var data = JsonDocument.Parse(JsonSerializer.Serialize(rpc!.ErrorData, rpc.ErrorData!.GetType())).RootElement;
        Assert.True(data.GetProperty("dmlRowCapExceeded").GetBoolean());
        Assert.Equal(2, data.GetProperty("rowCap").GetInt32());
        Assert.Equal("DELETE", data.GetProperty("operation").GetString());
        Assert.Equal("account", data.GetProperty("entity").GetString());
        Assert.DoesNotContain("--", data.GetProperty("message").GetString());
        Assert.False(data.TryGetProperty("dmlCommitted", out _)); // nothing ran before the refusal
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public async Task NonPositiveRowCap_IsRefusedAsInvalid_BeforeAnythingRuns(int rowCap)
    {
        var h = DmlTestHarness.Create(rows: 3);

        var ex = await Assert.ThrowsAsync<PpdsValidationException>(() => h.Service.ExecuteAsync(
            new SqlQueryRequest { Sql = DeleteSql, DmlSafety = new DmlSafetyOptions { IsConfirmed = true, RowCap = rowCap } }));

        Assert.Contains("positive number", ex.Message);
        h.AssertNothingChanged();
    }

    [Fact]
    public void RowCap_Tui_SaysTheCapCannotBeRemovedThere()
    {
        Assert.Contains("cannot be removed in the TUI", SqlQueryPresenter.RowCapRemedy);
        Assert.Contains("ppds query sql --no-limit", SqlQueryPresenter.RowCapRemedy);
    }

    [Fact]
    public void OtherErrors_AreNotReworded()
    {
        var ex = new PpdsException(ErrorCodes.Query.DmlBlocked, "blocked");
        var error = ExceptionMapper.Map(ex);

        Assert.Same(error, DmlSafetyOptionSet.WithCliWording(error, ex));
    }
}

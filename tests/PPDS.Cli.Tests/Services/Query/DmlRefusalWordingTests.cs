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

        var ex = await Assert.ThrowsAsync<PpdsException>(() => h.Service.ExecuteAsync(
            new SqlQueryRequest { Sql = DeleteSql, DmlSafety = new DmlSafetyOptions() }));

        Assert.Equal(SqlQueryService.ConfirmationRequiredMessage, ex.UserMessage);
        Assert.DoesNotContain("--", ex.UserMessage);
        Assert.Contains("Nothing was executed", ex.UserMessage);

        var cli = DmlSafetyOptionSet.WithCliWording(ExceptionMapper.Map(ex), ex);
        Assert.Equal("DML operations require --confirm to execute. Use --dry-run to preview the operation.", cli.Message);
        Assert.Equal(ErrorCodes.Query.DmlConfirmationRequired, cli.Code);
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

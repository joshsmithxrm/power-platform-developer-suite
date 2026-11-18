using Moq;
using PPDS.Auth.Profiles;
using PPDS.Cli.Commands.Serve.Handlers;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Cli.Services.Query;
using PPDS.Cli.Tests.Services.Shared;
using PPDS.Dataverse.BulkOperations;
using PPDS.Dataverse.Query;
using Xunit;

namespace PPDS.Cli.Tests.Services.Query;

/// <summary>
/// A request without explicit <see cref="SqlQueryRequest.DmlSafety"/> must be guarded with the default
/// (unconfirmed) options instead of skipping the DML guard.
/// </summary>
[Trait("Category", "Unit")]
public class DmlGuardFailClosedTests
{
    private static (SqlQueryService service, Mock<IQueryExecutor> executor, Mock<IBulkOperationExecutor> bulk) Create(
        ProtectionLevel level = ProtectionLevel.Development, MockBehavior behavior = MockBehavior.Strict)
    {
        var executor = new Mock<IQueryExecutor>(behavior);
        var bulk = new Mock<IBulkOperationExecutor>(behavior);
        var service = new SqlQueryService(executor.Object, guard: new InactiveFakeShakedownGuard(), bulkOperationExecutor: bulk.Object, metadataProvider: TestMetadata.Provider)
        {
            EnvironmentProtectionLevel = level
        };
        return (service, executor, bulk);
    }

    [Theory]
    [InlineData("DELETE FROM account WHERE name = 'x'")]
    [InlineData("UPDATE account SET name = 'y' WHERE name = 'x'")]
    [InlineData("INSERT INTO account (name) VALUES ('x')")]
    [InlineData("SELECT name FROM account; DELETE FROM account WHERE name = 'x'")]
    public async Task NullDmlSafety_Dml_IsRefusedAsConfirmationRequired_AndNothingExecutes(string sql)
    {
        var (service, executor, bulk) = Create();

        var ex = await Assert.ThrowsAsync<PpdsDmlConfirmationRequiredException>(() => service.ExecuteAsync(new SqlQueryRequest { Sql = sql }));

        Assert.Equal(ErrorCodes.Query.DmlConfirmationRequired, ex.ErrorCode);
        executor.VerifyNoOtherCalls();
        bulk.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task NullDmlSafety_Streaming_Dml_IsRefused()
    {
        var (service, executor, bulk) = Create();

        var ex = await Assert.ThrowsAsync<PpdsDmlConfirmationRequiredException>(async () =>
        {
            await foreach (var _ in service.ExecuteStreamingAsync(new SqlQueryRequest { Sql = "DELETE FROM account WHERE name = 'x'" })) { }
        });

        Assert.Equal(ErrorCodes.Query.DmlConfirmationRequired, ex.ErrorCode);
        executor.VerifyNoOtherCalls();
        bulk.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData("DELETE FROM account")]
    [InlineData("UPDATE account SET name = 'x'")]
    public async Task NullDmlSafety_DmlWithoutWhere_IsBlocked(string sql)
    {
        var (service, executor, bulk) = Create();

        var ex = await Assert.ThrowsAsync<PpdsException>(() => service.ExecuteAsync(new SqlQueryRequest { Sql = sql }));

        Assert.Equal(ErrorCodes.Query.DmlBlocked, ex.ErrorCode);
        executor.VerifyNoOtherCalls();
        bulk.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task NullDmlSafety_Production_Dml_IsRefused()
    {
        var (service, _, _) = Create(ProtectionLevel.Production);

        var ex = await Assert.ThrowsAsync<PpdsDmlConfirmationRequiredException>(
            () => service.ExecuteAsync(new SqlQueryRequest { Sql = "DELETE FROM account WHERE name = 'x'" }));

        Assert.Equal(ErrorCodes.Query.DmlConfirmationRequired, ex.ErrorCode);
    }

    [Fact]
    public async Task NullDmlSafety_Select_StillWorks()
    {
        var (service, executor, _) = Create(behavior: MockBehavior.Loose);
        var fetched = QueryResult.Empty("account");
        executor.Setup(x => x.ExecuteFetchXmlAsync(It.IsAny<string>(), It.IsAny<int?>(), It.IsAny<string?>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(fetched);
        executor.Setup(x => x.ExecuteFetchXmlAsync(It.IsAny<string>(), It.IsAny<int?>(), It.IsAny<string?>(), It.IsAny<bool>(), It.IsAny<QueryExecutionOptions?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(fetched);

        var result = await service.ExecuteAsync(new SqlQueryRequest { Sql = "SELECT name FROM account" });

        Assert.Null(result.DmlOutcome);
        Assert.NotNull(result.Result);
    }

    // ---- RPC mapping --------------------------------------------------------

    [Fact]
    public void Rpc_OmittedDmlSafety_MapsToUnconfirmedDefaults()
    {
        var request = RpcMethodHandler.BuildSqlRequest(new QuerySqlRequest { Sql = "DELETE FROM account WHERE name = 'x'" });

        Assert.NotNull(request.DmlSafety);
        Assert.False(request.DmlSafety!.IsConfirmed);
        Assert.False(request.DmlSafety.IsDryRun);
        Assert.False(request.DmlSafety.NoLimit);
    }

    [Fact]
    public async Task Rpc_QuerySqlWithoutDmlSafety_RefusesDml()
    {
        var (service, executor, bulk) = Create();
        var request = RpcMethodHandler.BuildSqlRequest(new QuerySqlRequest { Sql = "DELETE FROM account WHERE name = 'x'" });

        var ex = await Assert.ThrowsAsync<PpdsDmlConfirmationRequiredException>(() => service.ExecuteAsync(request));

        Assert.Equal(ErrorCodes.Query.DmlConfirmationRequired, ex.ErrorCode);
        executor.VerifyNoOtherCalls();
        bulk.VerifyNoOtherCalls();
    }

    [Fact]
    public void Rpc_ExplicitDmlSafety_IsMappedField_ForField()
    {
        var request = RpcMethodHandler.BuildSqlRequest(new QuerySqlRequest
        {
            Sql = "DELETE FROM account WHERE name = 'x'",
            DmlSafety = new DmlSafetyRpcOptions { IsConfirmed = true, NoLimit = true, RowCap = 7 }
        });

        Assert.True(request.DmlSafety!.IsConfirmed);
        Assert.True(request.DmlSafety.NoLimit);
        Assert.Equal(7, request.DmlSafety.RowCap);
    }
}

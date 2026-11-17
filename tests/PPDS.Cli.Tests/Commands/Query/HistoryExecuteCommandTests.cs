using Moq;
using PPDS.Auth.Profiles;
using PPDS.Cli.Commands.Query.History;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Cli.Services.Query;
using PPDS.Cli.Tests.Services.Shared;
using PPDS.Dataverse.Query;
using Xunit;

namespace PPDS.Cli.Tests.Commands.Query;

/// <summary>
/// 'ppds query history execute' must apply the same DML guards as 'ppds query sql'
/// (a null DmlSafety makes the service skip every DML safety check).
/// </summary>
[Trait("Category", "Unit")]
public class HistoryExecuteCommandTests
{
    [Fact]
    public void BuildRequest_AlwaysCarriesDmlSafety_Unconfirmed()
    {
        var request = ExecuteCommand.BuildRequest("DELETE FROM account WHERE name = 'x'", top: 5);

        Assert.NotNull(request.DmlSafety);
        Assert.False(request.DmlSafety!.IsConfirmed);
        Assert.False(request.DmlSafety.IsDryRun);
        Assert.False(request.DmlSafety.NoLimit);
        Assert.Equal(5, request.TopOverride);
    }

    [Theory]
    [InlineData("DELETE FROM account WHERE name = 'x'")]
    [InlineData("UPDATE account SET name = 'y' WHERE name = 'x'")]
    public async Task ReplayedDml_IsStoppedByGuard_WithoutConfirmation(string sql)
    {
        var service = new SqlQueryService(Mock.Of<IQueryExecutor>(), guard: new InactiveFakeShakedownGuard())
        {
            EnvironmentProtectionLevel = ProtectionLevel.Development
        };

        var ex = await Assert.ThrowsAsync<PpdsException>(
            () => service.ExecuteAsync(ExecuteCommand.BuildRequest(sql, null)));

        Assert.Equal(ErrorCodes.Query.DmlConfirmationRequired, ex.ErrorCode);
    }

    [Fact]
    public async Task ReplayedDeleteWithoutWhere_IsBlocked()
    {
        var service = new SqlQueryService(Mock.Of<IQueryExecutor>(), guard: new InactiveFakeShakedownGuard())
        {
            EnvironmentProtectionLevel = ProtectionLevel.Development
        };

        var ex = await Assert.ThrowsAsync<PpdsException>(
            () => service.ExecuteAsync(ExecuteCommand.BuildRequest("DELETE FROM account", null)));

        Assert.Equal(ErrorCodes.Query.DmlBlocked, ex.ErrorCode);
    }
}

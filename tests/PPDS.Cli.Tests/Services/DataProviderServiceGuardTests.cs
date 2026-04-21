using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Cli.Services;
using PPDS.Cli.Tests.Services.Shared;
using PPDS.Dataverse.Pooling;
using Xunit;

namespace PPDS.Cli.Tests.Services;

/// <summary>
/// Guard-wiring regression test for <see cref="DataProviderService"/> — asserts
/// that every mutation method calls
/// <see cref="PPDS.Cli.Infrastructure.Safety.IShakedownGuard.EnsureCanMutate"/>
/// and propagates the resulting <see cref="PpdsException"/>. Covers AC-31.
/// The theory row count MUST equal the 5-method mutation inventory.
/// </summary>
[Trait("Category", "Unit")]
public class DataProviderServiceGuardTests
{
    [Theory]
    [InlineData("RegisterDataSourceAsync")]
    [InlineData("UnregisterDataSourceAsync")]
    [InlineData("RegisterDataProviderAsync")]
    [InlineData("UpdateDataProviderAsync")]
    [InlineData("UnregisterDataProviderAsync")]
    public async Task EveryMutationMethod_Blocks(string methodName)
    {
        // Arrange
        var pool = Mock.Of<IDataverseConnectionPool>();
        var guard = new ActiveFakeShakedownGuard();
        var logger = NullLogger<DataProviderService>.Instance;
        var svc = new DataProviderService(pool, guard, logger);

        // Act + Assert
        var ex = await Assert.ThrowsAsync<PpdsException>(() => InvokeAsync(svc, methodName));
        Assert.Equal(ErrorCodes.Safety.ShakedownActive, ex.ErrorCode);
    }

    private static Task InvokeAsync(DataProviderService svc, string methodName) => methodName switch
    {
        "RegisterDataSourceAsync" => svc.RegisterDataSourceAsync(
            new DataSourceRegistration("prefix_src"),
            CancellationToken.None),
        "UnregisterDataSourceAsync" => svc.UnregisterDataSourceAsync(Guid.NewGuid(), false, CancellationToken.None),
        "RegisterDataProviderAsync" => svc.RegisterDataProviderAsync(
            new DataProviderRegistration(
                Name: "Provider",
                DataSourceId: Guid.NewGuid(),
                RetrievePlugin: null,
                RetrieveMultiplePlugin: null,
                CreatePlugin: null,
                UpdatePlugin: null,
                DeletePlugin: null),
            CancellationToken.None),
        "UpdateDataProviderAsync" => svc.UpdateDataProviderAsync(
            Guid.NewGuid(),
            new DataProviderUpdateRequest(),
            CancellationToken.None),
        "UnregisterDataProviderAsync" => svc.UnregisterDataProviderAsync(Guid.NewGuid(), CancellationToken.None),
        _ => throw new ArgumentOutOfRangeException(nameof(methodName), methodName, null),
    };
}

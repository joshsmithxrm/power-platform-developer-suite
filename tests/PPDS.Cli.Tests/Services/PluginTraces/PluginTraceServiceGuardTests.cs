using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Cli.Services.PluginTraces;
using PPDS.Cli.Tests.Services.Shared;
using PPDS.Dataverse.Pooling;
using Xunit;

namespace PPDS.Cli.Tests.Services.PluginTraces;

/// <summary>
/// Guard-wiring regression test for <see cref="PluginTraceService"/> — asserts
/// that every mutation method calls <see cref="PPDS.Cli.Infrastructure.Safety.IShakedownGuard.EnsureCanMutate"/>
/// and propagates the resulting <see cref="PpdsException"/>. Covers AC-32.
/// The theory row count MUST equal the 6-method mutation inventory.
/// </summary>
[Trait("Category", "Unit")]
public class PluginTraceServiceGuardTests
{
    [Theory]
    [InlineData("DeleteAsync")]
    [InlineData("DeleteByIdsAsync")]
    [InlineData("DeleteByFilterAsync")]
    [InlineData("DeleteAllAsync")]
    [InlineData("DeleteOlderThanAsync")]
    [InlineData("SetSettingsAsync")]
    public async Task EveryMutationMethod_Blocks(string methodName)
    {
        // Arrange
        var guard = new ActiveFakeShakedownGuard();
        var pool = Mock.Of<IDataverseConnectionPool>();
        var logger = NullLogger<PluginTraceService>.Instance;
        var svc = new PluginTraceService(pool, guard, logger);

        // Act + Assert
        var ex = await Assert.ThrowsAsync<PpdsException>(() => InvokeAsync(svc, methodName));
        Assert.Equal(ErrorCodes.Safety.ShakedownActive, ex.ErrorCode);
    }

    private static Task InvokeAsync(PluginTraceService svc, string methodName) => methodName switch
    {
        "DeleteAsync" => svc.DeleteAsync(Guid.NewGuid(), CancellationToken.None),
        "DeleteByIdsAsync" => svc.DeleteByIdsAsync(new[] { Guid.NewGuid() }, null, CancellationToken.None),
        "DeleteByFilterAsync" => svc.DeleteByFilterAsync(new PluginTraceFilter(), null, CancellationToken.None),
        "DeleteAllAsync" => svc.DeleteAllAsync(null, CancellationToken.None),
        "DeleteOlderThanAsync" => svc.DeleteOlderThanAsync(TimeSpan.FromDays(1), null, CancellationToken.None),
        "SetSettingsAsync" => svc.SetSettingsAsync(PluginTraceLogSetting.Off, CancellationToken.None),
        _ => throw new ArgumentOutOfRangeException(nameof(methodName), methodName, null),
    };
}

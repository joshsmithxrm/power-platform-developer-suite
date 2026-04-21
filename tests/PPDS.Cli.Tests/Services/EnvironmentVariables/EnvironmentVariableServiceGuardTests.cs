using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Cli.Services.EnvironmentVariables;
using PPDS.Cli.Tests.Services.Shared;
using PPDS.Dataverse.Pooling;
using Xunit;

namespace PPDS.Cli.Tests.Services.EnvironmentVariables;

/// <summary>
/// Guard-wiring regression test for <see cref="EnvironmentVariableService"/> — asserts
/// that the single mutation method (<see cref="EnvironmentVariableService.SetValueAsync"/>)
/// calls <see cref="PPDS.Cli.Infrastructure.Safety.IShakedownGuard.EnsureCanMutate"/>
/// and propagates the resulting <see cref="PpdsException"/>. Covers AC-34.
/// </summary>
[Trait("Category", "Unit")]
public class EnvironmentVariableServiceGuardTests
{
    [Fact]
    public async Task SetValueAsync_Blocks()
    {
        // Arrange
        var guard = new ActiveFakeShakedownGuard();
        var pool = Mock.Of<IDataverseConnectionPool>();
        var logger = NullLogger<EnvironmentVariableService>.Instance;
        var svc = new EnvironmentVariableService(pool, guard, logger);

        // Act + Assert
        var ex = await Assert.ThrowsAsync<PpdsException>(
            () => svc.SetValueAsync(string.Empty, string.Empty, CancellationToken.None));
        Assert.Equal(ErrorCodes.Safety.ShakedownActive, ex.ErrorCode);
    }
}

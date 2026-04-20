using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Cli.Services.Solutions;
using PPDS.Cli.Services.WebResources;
using PPDS.Cli.Tests.Services.Shared;
using PPDS.Dataverse.Pooling;
using Xunit;

namespace PPDS.Cli.Tests.Services.WebResources;

/// <summary>
/// Guard-wiring regression test for <see cref="WebResourceService"/> — asserts
/// that every mutation method calls <see cref="PPDS.Cli.Infrastructure.Safety.IShakedownGuard.EnsureCanMutate"/>
/// and propagates the resulting <see cref="PpdsException"/>. Covers AC-33.
/// The theory row count MUST equal the 3-method mutation inventory.
/// </summary>
[Trait("Category", "Unit")]
public class WebResourceServiceGuardTests
{
    [Theory]
    [InlineData("UpdateContentAsync")]
    [InlineData("PublishAsync")]
    [InlineData("PublishAllAsync")]
    public async Task EveryMutationMethod_Blocks(string methodName)
    {
        // Arrange
        var guard = new ActiveFakeShakedownGuard();
        var pool = Mock.Of<IDataverseConnectionPool>();
        var solutionService = Mock.Of<ISolutionService>();
        var logger = NullLogger<WebResourceService>.Instance;
        var svc = new WebResourceService(pool, solutionService, guard, logger);

        // Act + Assert
        var ex = await Assert.ThrowsAsync<PpdsException>(() => InvokeAsync(svc, methodName));
        Assert.Equal(ErrorCodes.Safety.ShakedownActive, ex.ErrorCode);
    }

    private static Task InvokeAsync(WebResourceService svc, string methodName) => methodName switch
    {
        "UpdateContentAsync" => svc.UpdateContentAsync(Guid.NewGuid(), string.Empty, CancellationToken.None),
        "PublishAsync" => svc.PublishAsync(new List<Guid> { Guid.NewGuid() }, CancellationToken.None),
        "PublishAllAsync" => svc.PublishAllAsync(CancellationToken.None),
        _ => throw new ArgumentOutOfRangeException(nameof(methodName), methodName, null),
    };
}

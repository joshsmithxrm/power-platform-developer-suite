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
/// Guard-wiring regression test for <see cref="ServiceEndpointService"/> — asserts
/// that every mutation method calls
/// <see cref="PPDS.Cli.Infrastructure.Safety.IShakedownGuard.EnsureCanMutate"/>
/// and propagates the resulting <see cref="PpdsException"/>. Covers AC-29.
/// The theory row count MUST equal the 4-method mutation inventory.
/// </summary>
[Trait("Category", "Unit")]
public class ServiceEndpointServiceGuardTests
{
    [Theory]
    [InlineData("RegisterWebhookAsync")]
    [InlineData("RegisterServiceBusAsync")]
    [InlineData("UpdateAsync")]
    [InlineData("UnregisterAsync")]
    public async Task EveryMutationMethod_Blocks(string methodName)
    {
        // Arrange
        var pool = Mock.Of<IDataverseConnectionPool>();
        var guard = new ActiveFakeShakedownGuard();
        var logger = NullLogger<ServiceEndpointService>.Instance;
        var svc = new ServiceEndpointService(pool, guard, logger);

        // Act + Assert
        var ex = await Assert.ThrowsAsync<PpdsException>(() => InvokeAsync(svc, methodName));
        Assert.Equal(ErrorCodes.Safety.ShakedownActive, ex.ErrorCode);
    }

    private static Task InvokeAsync(ServiceEndpointService svc, string methodName) => methodName switch
    {
        "RegisterWebhookAsync" => svc.RegisterWebhookAsync(
            new WebhookRegistration("name", "https://example.com", "WebhookKey"),
            CancellationToken.None),
        "RegisterServiceBusAsync" => svc.RegisterServiceBusAsync(
            new ServiceBusRegistration("name", "sb://ns.example.com", "q", "Queue", "SASKey"),
            CancellationToken.None),
        "UpdateAsync" => svc.UpdateAsync(Guid.NewGuid(), new ServiceEndpointUpdateRequest(), CancellationToken.None),
        "UnregisterAsync" => svc.UnregisterAsync(Guid.NewGuid(), false, null, CancellationToken.None),
        _ => throw new ArgumentOutOfRangeException(nameof(methodName), methodName, null),
    };
}

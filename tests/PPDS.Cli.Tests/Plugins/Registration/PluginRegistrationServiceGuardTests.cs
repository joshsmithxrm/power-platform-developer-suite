using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Cli.Plugins.Models;
using PPDS.Cli.Plugins.Registration;
using PPDS.Cli.Tests.Services.Shared;
using PPDS.Dataverse.Pooling;
using Xunit;

namespace PPDS.Cli.Tests.Plugins.Registration;

/// <summary>
/// Guard-wiring regression test for <see cref="PluginRegistrationService"/> — asserts
/// that every mutation method calls
/// <see cref="PPDS.Cli.Infrastructure.Safety.IShakedownGuard.EnsureCanMutate"/>
/// and propagates the resulting <see cref="PpdsException"/>. Covers AC-28.
/// The theory row count MUST equal the 18-method mutation inventory.
/// </summary>
[Trait("Category", "Unit")]
public class PluginRegistrationServiceGuardTests
{
    [Theory]
    [InlineData("UpsertAssemblyAsync")]
    [InlineData("UpsertPackageAsync")]
    [InlineData("UpsertPluginTypeAsync")]
    [InlineData("UpsertStepAsync")]
    [InlineData("UpsertImageAsync")]
    [InlineData("DeleteImageAsync")]
    [InlineData("DeleteStepAsync")]
    [InlineData("DeletePluginTypeAsync")]
    [InlineData("UnregisterImageAsync")]
    [InlineData("UnregisterStepAsync")]
    [InlineData("UnregisterPluginTypeAsync")]
    [InlineData("UnregisterAssemblyAsync")]
    [InlineData("UnregisterPackageAsync")]
    [InlineData("UpdateStepAsync")]
    [InlineData("UpdateImageAsync")]
    [InlineData("EnableStepAsync")]
    [InlineData("DisableStepAsync")]
    [InlineData("AddToSolutionAsync")]
    public async Task EveryMutationMethod_Blocks(string methodName)
    {
        // Arrange — guard is active, so every mutation method must throw.
        var pool = Mock.Of<IDataverseConnectionPool>();
        var guard = new ActiveFakeShakedownGuard();
        var logger = NullLogger<PluginRegistrationService>.Instance;
        var svc = new PluginRegistrationService(pool, guard, logger);

        // Act + Assert
        var ex = await Assert.ThrowsAsync<PpdsException>(() => InvokeAsync(svc, methodName));
        Assert.Equal(ErrorCodes.Safety.ShakedownActive, ex.ErrorCode);
    }

    private static Task InvokeAsync(PluginRegistrationService svc, string methodName) => methodName switch
    {
        "UpsertAssemblyAsync" => svc.UpsertAssemblyAsync("asm", new byte[] { 0x00 }, null, CancellationToken.None),
        "UpsertPackageAsync" => svc.UpsertPackageAsync("pkg", new byte[] { 0x00 }, null, CancellationToken.None),
        "UpsertPluginTypeAsync" => svc.UpsertPluginTypeAsync(Guid.NewGuid(), "TypeName", null, CancellationToken.None),
        "UpsertStepAsync" => svc.UpsertStepAsync(
            Guid.NewGuid(),
            "pluginType",
            new PluginStepConfig { Name = "step", Message = "Create", Entity = "account", Stage = "PostOperation" },
            Guid.NewGuid(),
            null,
            null,
            CancellationToken.None),
        "UpsertImageAsync" => svc.UpsertImageAsync(
            Guid.NewGuid(),
            new PluginImageConfig { Name = "img", ImageType = "PostImage" },
            "Create",
            CancellationToken.None),
        "DeleteImageAsync" => svc.DeleteImageAsync(Guid.NewGuid(), CancellationToken.None),
        "DeleteStepAsync" => svc.DeleteStepAsync(Guid.NewGuid(), CancellationToken.None),
        "DeletePluginTypeAsync" => svc.DeletePluginTypeAsync(Guid.NewGuid(), CancellationToken.None),
        "UnregisterImageAsync" => svc.UnregisterImageAsync(Guid.NewGuid(), CancellationToken.None),
        "UnregisterStepAsync" => svc.UnregisterStepAsync(Guid.NewGuid(), false, CancellationToken.None),
        "UnregisterPluginTypeAsync" => svc.UnregisterPluginTypeAsync(Guid.NewGuid(), false, CancellationToken.None),
        "UnregisterAssemblyAsync" => svc.UnregisterAssemblyAsync(Guid.NewGuid(), false, CancellationToken.None),
        "UnregisterPackageAsync" => svc.UnregisterPackageAsync(Guid.NewGuid(), false, CancellationToken.None),
        "UpdateStepAsync" => svc.UpdateStepAsync(Guid.NewGuid(), new StepUpdateRequest(), CancellationToken.None),
        "UpdateImageAsync" => svc.UpdateImageAsync(Guid.NewGuid(), new ImageUpdateRequest(), CancellationToken.None),
        "EnableStepAsync" => svc.EnableStepAsync(Guid.NewGuid(), CancellationToken.None),
        "DisableStepAsync" => svc.DisableStepAsync(Guid.NewGuid(), CancellationToken.None),
        "AddToSolutionAsync" => svc.AddToSolutionAsync(Guid.NewGuid(), 91, "solution", CancellationToken.None),
        _ => throw new ArgumentOutOfRangeException(nameof(methodName), methodName, null),
    };
}

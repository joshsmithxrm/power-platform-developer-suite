using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Cli.Plugins.Registration;
using PPDS.Cli.Services;
using PPDS.Cli.Tests.Services.Shared;
using PPDS.Dataverse.Pooling;
using Xunit;

namespace PPDS.Cli.Tests.Services;

/// <summary>
/// Guard-wiring regression test for <see cref="CustomApiService"/> — asserts
/// that every mutation method calls
/// <see cref="PPDS.Cli.Infrastructure.Safety.IShakedownGuard.EnsureCanMutate"/>
/// and propagates the resulting <see cref="PpdsException"/>. Covers AC-30.
/// The theory row count MUST equal the 7-method mutation inventory.
/// </summary>
[Trait("Category", "Unit")]
public class CustomApiServiceGuardTests
{
    [Theory]
    [InlineData("RegisterAsync")]
    [InlineData("UpdateAsync")]
    [InlineData("UnregisterAsync")]
    [InlineData("AddParameterAsync")]
    [InlineData("UpdateParameterAsync")]
    [InlineData("RemoveParameterAsync")]
    [InlineData("SetPluginTypeAsync")]
    public async Task EveryMutationMethod_Blocks(string methodName)
    {
        // Arrange
        var pool = Mock.Of<IDataverseConnectionPool>();
        var pluginRegistration = Mock.Of<IPluginRegistrationService>();
        var guard = new ActiveFakeShakedownGuard();
        var logger = NullLogger<CustomApiService>.Instance;
        var svc = new CustomApiService(pool, pluginRegistration, guard, logger);

        // Act + Assert
        var ex = await Assert.ThrowsAsync<PpdsException>(() => InvokeAsync(svc, methodName));
        Assert.Equal(ErrorCodes.Safety.ShakedownActive, ex.ErrorCode);
    }

    private static Task InvokeAsync(CustomApiService svc, string methodName) => methodName switch
    {
        "RegisterAsync" => svc.RegisterAsync(
            new CustomApiRegistration(
                UniqueName: "prefix_Api",
                DisplayName: "Api",
                Name: null,
                Description: null,
                PluginTypeId: Guid.NewGuid(),
                BindingType: "Global",
                BoundEntity: null,
                IsFunction: false,
                IsPrivate: false,
                ExecutePrivilegeName: null,
                AllowedProcessingStepType: "None"),
            null,
            CancellationToken.None),
        "UpdateAsync" => svc.UpdateAsync(Guid.NewGuid(), new CustomApiUpdateRequest(), CancellationToken.None),
        "UnregisterAsync" => svc.UnregisterAsync(Guid.NewGuid(), false, null, CancellationToken.None),
        "AddParameterAsync" => svc.AddParameterAsync(
            Guid.NewGuid(),
            new CustomApiParameterRegistration(
                UniqueName: "p",
                DisplayName: "P",
                Name: null,
                Description: null,
                Type: "String",
                LogicalEntityName: null,
                IsOptional: true,
                Direction: "Request"),
            CancellationToken.None),
        "UpdateParameterAsync" => svc.UpdateParameterAsync(
            Guid.NewGuid(),
            new CustomApiParameterUpdateRequest(),
            CancellationToken.None),
        "RemoveParameterAsync" => svc.RemoveParameterAsync(Guid.NewGuid(), CancellationToken.None),
        "SetPluginTypeAsync" => svc.SetPluginTypeAsync(Guid.NewGuid(), null, null, CancellationToken.None),
        _ => throw new ArgumentOutOfRangeException(nameof(methodName), methodName, null),
    };
}

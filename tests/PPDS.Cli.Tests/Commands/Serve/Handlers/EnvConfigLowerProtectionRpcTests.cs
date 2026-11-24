using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using PPDS.Auth.Profiles;
using PPDS.Cli.Commands.Serve.Handlers;
using PPDS.Cli.Infrastructure;
using PPDS.Cli.Services.Environment;
using Xunit;

namespace PPDS.Cli.Tests.Commands.Serve.Handlers;

/// <summary>
/// The daemon lowers an environment's write protection only with <c>confirmLowerProtection</c>, which the
/// extension sends after its own modal (#1829).
/// </summary>
[Trait("Category", "Unit")]
public sealed class EnvConfigLowerProtectionRpcTests : IDisposable
{
    private const string Url = "https://org.crm.dynamics.com";

    private readonly string _tempDir;
    private readonly EnvironmentConfigStore _store;
    private readonly ServiceProvider _authServices;
    private readonly RpcMethodHandler _handler;

    public EnvConfigLowerProtectionRpcTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "ppds-envrpc-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
        _store = new EnvironmentConfigStore(Path.Combine(_tempDir, "environments.json"));
        _authServices = new ServiceCollection()
            .AddSingleton(_store)
            .AddSingleton<IEnvironmentConfigService>(sp => new EnvironmentConfigService(sp.GetRequiredService<EnvironmentConfigStore>()))
            .BuildServiceProvider();
        _handler = new RpcMethodHandler(new Mock<IDaemonConnectionPoolManager>().Object, _authServices);
    }

    public void Dispose()
    {
        _authServices.Dispose();
        _store.Dispose();
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public async Task Set_LoweringWithoutConfirmation_IsRefusedWithStructuredCodeAndNothingIsSaved()
    {
        await _store.SaveConfigAsync(Url, type: EnvironmentType.Production);

        var act = () => _handler.EnvConfigSetAsync(Url, type: "Development");

        var ex = (await act.Should().ThrowAsync<RpcException>()).Which;
        ex.StructuredErrorCode.Should().Be(EnvironmentConfirmationErrorCodes.ProtectionLoweringConfirmationRequired);
        ex.Message.Should().Contain("lowers write protection");
        (await _store.GetConfigAsync(Url))!.Type.Should().Be(EnvironmentType.Production);
    }

    [Fact]
    public async Task Set_LoweringWithConfirmationNamingTheEnvironment_IsSaved()
    {
        await _store.SaveConfigAsync(Url, type: EnvironmentType.Production);

        var response = await _handler.EnvConfigSetAsync(Url, type: "Development", confirmLowerProtection: Url);

        response.Saved.Should().BeTrue();
        response.Type.Should().Be("Development");
    }

    [Fact]
    public async Task Set_ConfirmationNamingAnotherEnvironment_IsRefused()
    {
        await _store.SaveConfigAsync(Url, type: EnvironmentType.Production);

        var act = () => _handler.EnvConfigSetAsync(Url, type: "Development", confirmLowerProtection: "https://other.crm.dynamics.com");

        (await act.Should().ThrowAsync<RpcException>()).Which.StructuredErrorCode
            .Should().Be(EnvironmentConfirmationErrorCodes.TargetMismatch);
        (await _store.GetConfigAsync(Url))!.Type.Should().Be(EnvironmentType.Production);
    }

    [Fact]
    public async Task Set_ChangeThatDoesNotLower_NeedsNoConfirmation()
    {
        await _store.SaveConfigAsync(Url, type: EnvironmentType.Development);

        var response = await _handler.EnvConfigSetAsync(Url, label: "Dev", type: "Production");

        response.Type.Should().Be("Production");
    }

    [Fact]
    public async Task Remove_GoesThroughTheServiceAndNeedsNoConfirmation()
    {
        await _store.SaveConfigAsync(Url, type: EnvironmentType.Production);

        var response = await _handler.EnvConfigRemoveAsync(Url);

        response.Removed.Should().BeTrue();
        (await _store.GetConfigAsync(Url)).Should().BeNull();
    }
}

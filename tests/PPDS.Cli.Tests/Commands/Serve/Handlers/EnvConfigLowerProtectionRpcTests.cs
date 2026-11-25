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

    /// <summary>
    /// Review round 3: env/config/get on the long-lived daemon store reads the file, so a raise another process saved
    /// is shown at once (the daemon's store cached Development).
    /// </summary>
    [Fact]
    public async Task Get_AfterAnotherProcessRaisedTheType_ShowsTheRaise()
    {
        await _store.SaveConfigAsync(Url, type: EnvironmentType.Development);
        (await _handler.EnvConfigGetAsync(Url)).Type.Should().Be("Development");
        (await _store.LoadAsync()).Environments.Single().Type.Should().Be(EnvironmentType.Development); // the daemon's cache

        using (var cli = new EnvironmentConfigStore(_store.FilePath))
            await new EnvironmentConfigService(cli).SaveConfigAsync(Url, type: EnvironmentType.Production);

        var response = await _handler.EnvConfigGetAsync(Url);
        response.Type.Should().Be("Production");
        response.ResolvedType.Should().Be("Production");
    }

    /// <summary>
    /// Review round 4: the type is taken by name only. <c>Enum.TryParse</c> alone accepted "42", which names no type.
    /// </summary>
    [Theory]
    [InlineData("42")]
    [InlineData("-1")]
    [InlineData("1")]
    [InlineData("Production, Development")]
    public async Task Set_ATypeThatIsNotAName_IsInvalidArguments_AndNothingIsSaved(string type)
    {
        await _store.SaveConfigAsync(Url, type: EnvironmentType.Development);

        var ex = (await FluentActions.Awaiting(() => _handler.EnvConfigSetAsync(Url, type: type)).Should().ThrowAsync<RpcException>()).Which;

        ex.StructuredErrorCode.Should().Be(PPDS.Cli.Infrastructure.Errors.ErrorCodes.Validation.InvalidArguments);
        (await _store.ReloadAsync()).Environments.Single().Type.Should().Be(EnvironmentType.Development);
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
    public async Task AddEnvironment_UrlOnly_LeavesEveryWriterAtProduction()
    {
        // The extension's Add Environment sends env/config/set with the URL and nothing else.
        AssertEveryWriterIsProduction(await _store.GetConfigAsync(Url));

        var response = await _handler.EnvConfigSetAsync(Url);

        response.Saved.Should().BeTrue();
        response.Type.Should().BeNull();
        AssertEveryWriterIsProduction(await _store.GetConfigAsync(Url));
    }

    [Fact]
    public async Task EnvListTagging_CreatesEntriesWithoutAType_ThatStayProductionForEveryWriter()
    {
        // env/list tags every discovered environment through this step (discovery itself signs in,
        // so it is not run here). Sandbox and Developer discovery types are recorded but never lower
        // protection: no type is saved.
        const string sandbox = "https://sandbox.crm.dynamics.com";
        const string developer = "https://developer.crm.dynamics.com";
        AssertEveryWriterIsProduction(await _store.GetConfigAsync(sandbox));
        AssertEveryWriterIsProduction(await _store.GetConfigAsync(developer));

        await RpcMethodHandler.TagDiscoveredEnvironmentsAsync(_store,
            [new PPDS.Cli.Commands.Serve.Handlers.EnvironmentInfo { ApiUrl = sandbox, Type = "Sandbox" }, new PPDS.Cli.Commands.Serve.Handlers.EnvironmentInfo { ApiUrl = developer, Type = "Developer" }],
            "profile", CancellationToken.None);

        var tagged = await _store.GetConfigAsync(sandbox);
        tagged!.Type.Should().BeNull();
        tagged.DiscoveredType.Should().Be("Sandbox");
        tagged.Profiles.Should().Contain("profile");
        AssertEveryWriterIsProduction(tagged);
        AssertEveryWriterIsProduction(await _store.GetConfigAsync(developer));
    }

    private static void AssertEveryWriterIsProduction(EnvironmentConfig? config)
    {
        WriteProtectionResolver.ResolveForConfirmation(config).Should().Be(ProtectionLevel.Production);
        WriteProtectionResolver.ResolveForSqlDml(config).Should().Be(ProtectionLevel.Production);
        WriteProtectionResolver.ResolveForCrossEnvironmentTarget(config).Should().Be(ProtectionLevel.Production);
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

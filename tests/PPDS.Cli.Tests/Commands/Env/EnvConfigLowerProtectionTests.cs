using FluentAssertions;
using PPDS.Auth.Profiles;
using PPDS.Cli.Commands.Env;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Cli.Services.Environment;
using Xunit;

namespace PPDS.Cli.Tests.Commands.Env;

/// <summary>
/// <c>ppds env config &lt;url&gt; --type</c> lowers write protection only after an interactive confirmation
/// or with <c>--confirm-lower-protection &lt;url&gt;</c> (#1829). The rule itself is the service's.
/// </summary>
[Trait("Category", "Unit")]
public sealed class EnvConfigLowerProtectionTests : IDisposable
{
    private const string Url = "https://org.crm.dynamics.com";

    private readonly string _tempDir;
    private readonly EnvironmentConfigStore _store;
    private readonly EnvironmentConfigService _service;
    private readonly StringWriter _error = new();

    public EnvConfigLowerProtectionTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "ppds-envcfg-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
        _store = new EnvironmentConfigStore(Path.Combine(_tempDir, "environments.json"));
        _service = new EnvironmentConfigService(_store);
    }

    public void Dispose()
    {
        _error.Dispose();
        _store.Dispose();
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public async Task NonInteractive_WithoutAcknowledgement_IsRefusedWithConfirmationRequiredExitCode()
    {
        await _store.SaveConfigAsync(Url, type: EnvironmentType.Production);

        var exit = await RunAsync(EnvironmentType.Development, acknowledgement: null, interactive: false, input: "");

        exit.Should().Be(ExitCodes.ConfirmationRequired);
        (await _store.GetConfigAsync(Url))!.Type.Should().Be(EnvironmentType.Production);
        _error.ToString().Should().Contain("lowers write protection")
            .And.Contain($"{EnvCommandGroup.ConfirmLowerProtectionOptionName} {Url}");
    }

    [Fact]
    public async Task NonInteractive_WithAcknowledgementNamingTheEnvironment_IsSaved()
    {
        await _store.SaveConfigAsync(Url, type: EnvironmentType.Production);

        var exit = await RunAsync(EnvironmentType.Development, acknowledgement: Url + "/", interactive: false, input: "");

        exit.Should().Be(ExitCodes.Success);
        (await _store.GetConfigAsync(Url))!.Type.Should().Be(EnvironmentType.Development);
    }

    [Fact]
    public async Task Acknowledgement_NamingAnotherEnvironment_IsRefused()
    {
        await _store.SaveConfigAsync(Url, type: EnvironmentType.Production);

        var exit = await RunAsync(EnvironmentType.Development, acknowledgement: "https://other.crm.dynamics.com",
            interactive: false, input: "");

        exit.Should().NotBe(ExitCodes.Success);
        (await _store.GetConfigAsync(Url))!.Type.Should().Be(EnvironmentType.Production);
        _error.ToString().Should().Contain("does not name the environment");
    }

    [Fact]
    public async Task Interactive_TypingTheUrl_ConfirmsAndSaves()
    {
        await _store.SaveConfigAsync(Url, type: EnvironmentType.Production);

        var exit = await RunAsync(EnvironmentType.Development, acknowledgement: null, interactive: true, input: Url + "\n");

        exit.Should().Be(ExitCodes.Success);
        (await _store.GetConfigAsync(Url))!.Type.Should().Be(EnvironmentType.Development);
        _error.ToString().Should().Contain("WARNING: Changing the type of");
    }

    [Fact]
    public async Task Interactive_PressingEnter_CancelsAndSavesNothing()
    {
        await _store.SaveConfigAsync(Url, type: EnvironmentType.Production);

        var exit = await RunAsync(EnvironmentType.Development, acknowledgement: null, interactive: true, input: "\n");

        exit.Should().Be(ExitCodes.Success);
        (await _store.GetConfigAsync(Url))!.Type.Should().Be(EnvironmentType.Production);
        _error.ToString().Should().Contain("Cancelled. No change was saved.");
    }

    [Fact]
    public async Task Interactive_TypingAnotherUrl_IsRefused()
    {
        await _store.SaveConfigAsync(Url, type: EnvironmentType.Production);

        var exit = await RunAsync(EnvironmentType.Development, acknowledgement: null, interactive: true,
            input: "https://other.crm.dynamics.com\n");

        exit.Should().NotBe(ExitCodes.Success);
        (await _store.GetConfigAsync(Url))!.Type.Should().Be(EnvironmentType.Production);
    }

    [Theory]
    [InlineData(EnvironmentType.Production)]
    [InlineData(null)]
    public async Task ChangeThatDoesNotLower_NeedsNoConfirmationAndAsksNothing(EnvironmentType? type)
    {
        await _store.SaveConfigAsync(Url, type: EnvironmentType.Production);

        // No input is available: a prompt would read null and cancel.
        var exit = await RunAsync(type, acknowledgement: null, interactive: true, input: "", label: "Contoso");

        exit.Should().Be(ExitCodes.Success);
        (await _store.GetConfigAsync(Url))!.Label.Should().Be("Contoso");
        _error.ToString().Should().NotContain("WARNING");
    }

    private async Task<int> RunAsync(
        EnvironmentType? type, string? acknowledgement, bool interactive, string input, string? label = null)
    {
        using var reader = new StringReader(input);
        return await EnvCommandGroup.ExecuteConfigSetAsync(
            _service, Url, label, type, color: null, acknowledgement, interactive, reader, _error,
            CancellationToken.None);
    }
}

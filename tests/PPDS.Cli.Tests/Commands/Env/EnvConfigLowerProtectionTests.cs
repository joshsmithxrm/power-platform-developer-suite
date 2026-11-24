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
            .And.Contain(EnvCommandGroup.ConfirmLowerProtectionOptionName);
        // The option is named, but no ready-made command with the address filled in is printed.
        _error.ToString().Should().NotContain($"{EnvCommandGroup.ConfirmLowerProtectionOptionName} {Url}")
            .And.NotContain($"{EnvCommandGroup.ConfirmLowerProtectionOptionName} https://");
    }

    [Fact]
    public async Task NonInteractive_WithAcknowledgementNamingTheEnvironment_IsSaved()
    {
        await _store.SaveConfigAsync(Url, type: EnvironmentType.Production);

        var exit = await RunAsync(EnvironmentType.Development, acknowledgement: Url + "/", interactive: false, input: "");

        exit.Should().Be(ExitCodes.Success);
        (await _store.GetConfigAsync(Url))!.Type.Should().Be(EnvironmentType.Development);
    }

    [Theory]
    [InlineData("https://other.crm.dynamics.com")]
    [InlineData("https://org")]                              // a prefix of the address
    [InlineData("https://org.crm.dynamics.com.example.test")] // the address as a prefix
    [InlineData("https://org.crm.dynamics.com/extra")]
    [InlineData("not-a-url")]                                 // not an address at all
    [InlineData("*")]
    public async Task Acknowledgement_NamingAnotherEnvironment_IsRefused(string acknowledgement)
    {
        await _store.SaveConfigAsync(Url, type: EnvironmentType.Production);

        var exit = await RunAsync(EnvironmentType.Development, acknowledgement: acknowledgement,
            interactive: false, input: "");

        exit.Should().Be(ExitCodes.ConfirmationRequired);
        (await _store.GetConfigAsync(Url))!.Type.Should().Be(EnvironmentType.Production);
        _error.ToString().Should().Contain("Error: ");
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

        // A cancel exits like a refusal: the requested change was not made.
        exit.Should().Be(ExitCodes.ConfirmationRequired);
        (await _store.GetConfigAsync(Url))!.Type.Should().Be(EnvironmentType.Production);
        _error.ToString().Should().Contain("Cancelled. No change was saved.");
    }

    [Theory]
    [InlineData("https://other.crm.dynamics.com")]
    [InlineData("https://org")]
    [InlineData("yes")] // not an address
    public async Task Interactive_TypingAnotherUrl_IsRefused(string typed)
    {
        await _store.SaveConfigAsync(Url, type: EnvironmentType.Production);

        var exit = await RunAsync(EnvironmentType.Development, acknowledgement: null, interactive: true,
            input: typed + "\n");

        exit.Should().Be(ExitCodes.ConfirmationRequired);
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

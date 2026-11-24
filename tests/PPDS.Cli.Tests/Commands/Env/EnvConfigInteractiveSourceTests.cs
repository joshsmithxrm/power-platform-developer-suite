using FluentAssertions;
using PPDS.Auth.Profiles;
using PPDS.Cli.Commands.Env;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Cli.Tests.TestHelpers;
using Xunit;

namespace PPDS.Cli.Tests.Commands.Env;

/// <summary>
/// The full <c>env config</c> command decides "interactive" from whether standard input is redirected
/// (#1829): redirected input never gets a prompt, so a piped "yes" or URL cannot confirm a lowering.
/// </summary>
/// <remarks>
/// Runs the real command, which builds its own services from <c>PPDS_CONFIG_DIR</c>, so it points the
/// variable at a temporary folder and replaces the process-wide console streams; hence the collection.
/// </remarks>
[Trait("Category", "Unit")]
[Collection(EnvironmentVariableMutatingCollection.Name)]
public sealed class EnvConfigInteractiveSourceTests : IDisposable
{
    private const string Url = "https://org.crm.dynamics.com";

    private readonly string? _originalConfigDir;
    private readonly Func<bool> _originalIsInputRedirected;
    private readonly TextReader _originalIn;
    private readonly TextWriter _originalError;
    private readonly TextWriter _originalOut;
    private readonly string _configDir;
    private readonly StringWriter _error = new();

    public EnvConfigInteractiveSourceTests()
    {
        _originalConfigDir = Environment.GetEnvironmentVariable(ProfilePaths.ConfigDirEnvVar);
        _originalIsInputRedirected = EnvCommandGroup.IsInputRedirected;
        _originalIn = Console.In;
        _originalError = Console.Error;
        _originalOut = Console.Out;

        _configDir = Path.Combine(Path.GetTempPath(), "ppds-envcfg-cmd-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_configDir);
        Environment.SetEnvironmentVariable(ProfilePaths.ConfigDirEnvVar, _configDir);
        Console.SetError(_error);
        Console.SetOut(TextWriter.Null);
    }

    public void Dispose()
    {
        EnvCommandGroup.IsInputRedirected = _originalIsInputRedirected;
        Console.SetIn(_originalIn);
        Console.SetError(_originalError);
        Console.SetOut(_originalOut);
        Environment.SetEnvironmentVariable(ProfilePaths.ConfigDirEnvVar, _originalConfigDir);
        _error.Dispose();
        try { Directory.Delete(_configDir, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public void DefaultSource_IsConsoleInputRedirection()
    {
        _originalIsInputRedirected().Should().Be(Console.IsInputRedirected);
    }

    [Fact]
    public async Task RedirectedInput_NeverPrompts_EvenWhenTheUrlIsPiped()
    {
        await SeedProductionAsync();
        EnvCommandGroup.IsInputRedirected = () => true;
        Console.SetIn(new StringReader(Url + "\n"));

        var exit = await InvokeAsync("env", "config", Url, "--type", "Development");

        exit.Should().Be(ExitCodes.ConfirmationRequired);
        _error.ToString().Should().NotContain("Type the environment URL");
        (await TypeAsync()).Should().Be(EnvironmentType.Production);
    }

    [Fact]
    public async Task TerminalInput_Prompts_AndTheTypedUrlConfirms()
    {
        await SeedProductionAsync();
        EnvCommandGroup.IsInputRedirected = () => false;
        Console.SetIn(new StringReader(Url + "\n"));

        var exit = await InvokeAsync("env", "config", Url, "--type", "Development");

        exit.Should().Be(ExitCodes.Success);
        _error.ToString().Should().Contain("Type the environment URL");
        (await TypeAsync()).Should().Be(EnvironmentType.Development);
    }

    [Fact]
    public async Task RedirectedInput_WithTheOption_Saves()
    {
        await SeedProductionAsync();
        EnvCommandGroup.IsInputRedirected = () => true;

        var exit = await InvokeAsync("env", "config", Url, "--type", "Development",
            EnvCommandGroup.ConfirmLowerProtectionOptionName, Url);

        exit.Should().Be(ExitCodes.Success);
        (await TypeAsync()).Should().Be(EnvironmentType.Development);
    }

    private static async Task<int> InvokeAsync(params string[] args)
    {
        var root = new System.CommandLine.RootCommand { EnvCommandGroup.Create() };
        return await root.Parse(args).InvokeAsync();
    }

    private async Task SeedProductionAsync()
    {
        using var store = new EnvironmentConfigStore(Path.Combine(_configDir, "environments.json"));
        await store.SaveConfigAsync(Url, type: EnvironmentType.Production);
    }

    private async Task<EnvironmentType?> TypeAsync()
    {
        using var store = new EnvironmentConfigStore(Path.Combine(_configDir, "environments.json"));
        return (await store.GetConfigAsync(Url))?.Type;
    }
}

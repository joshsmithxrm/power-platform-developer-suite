using System.CommandLine;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using PPDS.Auth.Profiles;
using PPDS.Cli.Commands.Env;
using PPDS.Cli.Commands.Serve.Handlers;
using PPDS.Cli.Infrastructure;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Cli.Services.Environment;
using PPDS.Cli.Tests.TestHelpers;
using Xunit;

namespace PPDS.Cli.Tests.Commands.Env;

/// <summary>
/// The config command's <c>--type</c> and the type command's name take an environment type by member name only, with
/// the daemon's own check (#1829, review round 5). System.CommandLine's enum binding used <c>Enum.TryParse</c>, so
/// <c>--type 1</c> was saved as Production, <c>3</c> as Development and <c>Production,Sandbox</c> as another member.
/// </summary>
/// <remarks>
/// Runs the real commands, which build their own services from <c>PPDS_CONFIG_DIR</c>, so the variable points at a
/// temporary folder; hence the collection.
/// </remarks>
[Trait("Category", "Unit")]
[Collection(EnvironmentVariableMutatingCollection.Name)]
public sealed class EnvTypeByNameOnlyTests : IDisposable
{
    private const string Url = "https://org.crm.dynamics.com";
    private const string Names = "Unknown, Production, Sandbox, Development, Test, Trial";

    private readonly string? _originalConfigDir;
    private readonly Func<bool> _originalIsInputRedirected;
    private readonly TextWriter _originalError;
    private readonly TextWriter _originalOut;
    private readonly string _configDir;
    private readonly StringWriter _error = new();

    public EnvTypeByNameOnlyTests()
    {
        _originalConfigDir = Environment.GetEnvironmentVariable(ProfilePaths.ConfigDirEnvVar);
        _originalIsInputRedirected = EnvCommandGroup.IsInputRedirected;
        _originalError = Console.Error;
        _originalOut = Console.Out;

        _configDir = Path.Combine(Path.GetTempPath(), "ppds-envtype-cmd-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_configDir);
        Environment.SetEnvironmentVariable(ProfilePaths.ConfigDirEnvVar, _configDir);
        EnvCommandGroup.IsInputRedirected = () => true;
        Console.SetError(_error);
        Console.SetOut(TextWriter.Null);
    }

    public void Dispose()
    {
        EnvCommandGroup.IsInputRedirected = _originalIsInputRedirected;
        Console.SetError(_originalError);
        Console.SetOut(_originalOut);
        Environment.SetEnvironmentVariable(ProfilePaths.ConfigDirEnvVar, _originalConfigDir);
        _error.Dispose();
        try { Directory.Delete(_configDir, recursive: true); } catch { /* best effort */ }
    }

    private string FilePath => Path.Combine(_configDir, "environments.json");

    /// <summary>Numbers, numeric text and lists of names: each would bind to a member (or to no member) through Enum.TryParse.</summary>
    private static readonly string[] NotANameValues =
        ["1", "3", "42", "-1", "\"3\"", " 3 ", "Production,Sandbox", "Production, Test", "Prod"];

    public static TheoryData<string> NotAName => new(NotANameValues);

    /// <summary>Parses and invokes the real command as Program does (System.CommandLine's default invocation).</summary>
    private static Task<int> InvokeAsync(params string[] args)
    {
        var root = new RootCommand { EnvCommandGroup.Create() };
        return root.Parse(args).InvokeAsync();
    }

    /// <summary>Exit code 3 (Validation.InvalidArguments) and one line naming the valid values; no stack trace.</summary>
    private void ShouldBeRefusedAsInvalidArguments(int exit, string text)
    {
        exit.Should().Be(ExitCodes.InvalidArguments);
        var error = _error.ToString();
        error.Should().Contain($"Error: Invalid environment type '{text}'. Valid values: {Names}.");
        error.Should().NotContain("Unhandled exception").And.NotContain("   at ");
    }

    private async Task SeedAsync(EnvironmentType type)
    {
        using var store = new EnvironmentConfigStore(FilePath);
        await store.SaveConfigAsync(Url, type: type);
    }

    private async Task<EnvironmentType?> SavedTypeAsync()
    {
        using var store = new EnvironmentConfigStore(FilePath);
        return (await store.GetConfigAsync(Url))?.Type;
    }

    [Theory]
    [MemberData(nameof(NotAName))]
    public async Task ConfigType_ThatIsNotAName_IsInvalidArguments_ListsTheNames_AndNothingIsSaved(string type)
    {
        // Sandbox: a type change to Production (1) is a raise and to Development (3) is not a lowering, so neither
        // would be stopped by the lowering check if the type were bound as before.
        await SeedAsync(EnvironmentType.Sandbox);
        var before = File.ReadAllText(FilePath);

        var exit = await InvokeAsync("env", "config", Url, "--type", type, EnvCommandGroup.ConfirmLowerProtectionOptionName, Url);

        ShouldBeRefusedAsInvalidArguments(exit, type);
        File.ReadAllText(FilePath).Should().Be(before);
    }

    [Theory]
    [MemberData(nameof(NotAName))]
    public async Task TypeCommand_NameThatIsNotAName_IsInvalidArguments_AndNothingIsSaved(string name)
    {
        var exit = await InvokeAsync("env", "type", name, "--color", "Red");

        ShouldBeRefusedAsInvalidArguments(exit, name);
        File.Exists(FilePath).Should().BeFalse();
    }

    public static TheoryData<string, EnvironmentType> MemberNamesInTwoCasings()
    {
        var data = new TheoryData<string, EnvironmentType>();
        foreach (var type in Enum.GetValues<EnvironmentType>())
        {
            data.Add(type.ToString().ToLowerInvariant(), type);
            data.Add(type.ToString().ToUpperInvariant(), type);
        }
        data.Add(" Production ", EnvironmentType.Production);
        return data;
    }

    [Theory]
    [MemberData(nameof(MemberNamesInTwoCasings))]
    public async Task ConfigType_AMemberNameInAnyCase_IsSaved(string type, EnvironmentType expected)
    {
        var exit = await InvokeAsync("env", "config", Url, "--type", type, EnvCommandGroup.ConfirmLowerProtectionOptionName, Url);

        exit.Should().Be(ExitCodes.Success);
        (await SavedTypeAsync()).Should().Be(expected);
    }

    [Theory]
    [MemberData(nameof(MemberNamesInTwoCasings))]
    public async Task TypeCommand_AMemberNameInAnyCase_IsSaved(string name, EnvironmentType expected)
    {
        var exit = await InvokeAsync("env", "type", name, "--color", "Red");

        exit.Should().Be(ExitCodes.Success);
        using var store = new EnvironmentConfigStore(FilePath);
        (await store.LoadAsync()).TypeDefaults.Should().ContainKey(expected);
    }

    /// <summary>The CLI and the daemon give the same verdict on the same text.</summary>
    public static TheoryData<string> VerdictInputs()
    {
        var data = new TheoryData<string>();
        foreach (var value in NotANameValues)
            data.Add(value);
        foreach (var row in MemberNamesInTwoCasings())
            data.Add((string)row[0]);
        return data;
    }

    [Theory]
    [MemberData(nameof(VerdictInputs))]
    public async Task TheCliAndTheDaemon_GiveTheSameVerdict(string type)
    {
        var cliExit = await InvokeAsync("env", "config", Url, "--type", type, EnvCommandGroup.ConfirmLowerProtectionOptionName, Url);
        var cliRefused = cliExit == ExitCodes.InvalidArguments && _error.ToString().Contains("Invalid environment type");

        var daemonDir = Path.Combine(_configDir, "daemon");
        Directory.CreateDirectory(daemonDir);
        using var daemonStore = new EnvironmentConfigStore(Path.Combine(daemonDir, "environments.json"));
        using var services = new ServiceCollection()
            .AddSingleton(daemonStore)
            .AddSingleton<IEnvironmentConfigService>(sp => new EnvironmentConfigService(sp.GetRequiredService<EnvironmentConfigStore>()))
            .BuildServiceProvider();
        var handler = new RpcMethodHandler(new Mock<IDaemonConnectionPoolManager>().Object, services);
        var daemonRefused = await RefusedAsInvalidArgumentsAsync(() =>
            handler.EnvConfigSetAsync(Url, type: type, confirmLowerProtection: Url));

        cliRefused.Should().Be(daemonRefused, $"the CLI and the daemon must agree on '{type}'");
    }

    private static async Task<bool> RefusedAsInvalidArgumentsAsync(Func<Task> action)
    {
        try
        {
            await action();
            return false;
        }
        catch (RpcException ex) when (ex.StructuredErrorCode == ErrorCodes.Validation.InvalidArguments)
        {
            return true;
        }
    }

    /// <summary>A file PPDS cannot read: one line that names the file, the entry and the field, and its exit code.</summary>
    [Theory]
    [InlineData("config", "--list")]
    [InlineData("config", Url)]
    [InlineData("type", "--list")]
    public async Task AFileThatCannotBeRead_IsOneLineNamingTheFile_NotAnUnhandledException(string command, string argument)
    {
        File.WriteAllText(FilePath, $$"""{ "environments": [ { "url": "{{Url}}", "type": 1 } ] }""");
        var before = File.ReadAllText(FilePath);

        var exit = await InvokeAsync("env", command, argument);

        exit.Should().Be(ExitCodes.InvalidArguments);
        var error = _error.ToString();
        error.Should().Contain("Error: The environment configuration file").And.Contain(Path.GetFullPath(FilePath))
            .And.Contain(Url).And.Contain("the field 'type'").And.Contain("by hand");
        error.Should().NotContain("Unhandled exception").And.NotContain("   at ");
        File.ReadAllText(FilePath).Should().Be(before);
    }
}

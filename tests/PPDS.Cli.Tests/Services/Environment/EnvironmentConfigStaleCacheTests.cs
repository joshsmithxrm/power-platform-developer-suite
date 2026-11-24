using FluentAssertions;
using PPDS.Auth.Profiles;
using PPDS.Cli.Commands.Serve.Handlers;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Cli.Services.Environment;
using Xunit;

namespace PPDS.Cli.Tests.Services.Environment;

/// <summary>
/// Two store instances on one file, as a long-lived daemon or TUI and a separate CLI process have. The
/// long-lived one holds a stale cache; none of its writes may undo a raise the other one saved (#1829,
/// review round 2). Every change re-reads the file under the store's lock.
/// </summary>
[Trait("Category", "Unit")]
public sealed class EnvironmentConfigStaleCacheTests : IDisposable
{
    private const string Url = "https://org.crm.dynamics.com";
    private const string Other = "https://other.crm.dynamics.com";

    private readonly string _tempDir;
    private readonly string _path;
    private readonly EnvironmentConfigStore _daemonStore;
    private readonly EnvironmentConfigStore _cliStore;

    public EnvironmentConfigStaleCacheTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "ppds-stale-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
        _path = Path.Combine(_tempDir, "environments.json");
        _daemonStore = new EnvironmentConfigStore(_path);
        _cliStore = new EnvironmentConfigStore(_path);
    }

    public void Dispose()
    {
        _daemonStore.Dispose();
        _cliStore.Dispose();
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best effort */ }
    }

    /// <summary>Steps 1 and 2: the daemon caches Development, then the CLI raises it to Production.</summary>
    private async Task ArrangeStaleDevelopmentThenRaiseAsync()
    {
        await _daemonStore.SaveConfigAsync(Url, type: EnvironmentType.Development);
        await _daemonStore.SaveConfigAsync(Other, label: "Other", type: EnvironmentType.Development);
        (await _daemonStore.GetConfigAsync(Url))!.Type.Should().Be(EnvironmentType.Development); // cached

        await new EnvironmentConfigService(_cliStore).SaveConfigAsync(Url, type: EnvironmentType.Production);

        // The daemon's cache still says Development; the file says Production.
        (await _daemonStore.GetConfigAsync(Url))!.Type.Should().Be(EnvironmentType.Development);
        (await FileStateAsync(Url))!.Type.Should().Be(EnvironmentType.Production);
    }

    [Fact]
    public async Task Assessment_OnTheStaleStore_SeesTheRaiseAndReportsTheLowering()
    {
        await ArrangeStaleDevelopmentThenRaiseAsync();

        // Step 3: the assessment reads the file, not the cache.
        var assessment = await new EnvironmentConfigService(_daemonStore).AssessConfigChangeAsync(Url, EnvironmentType.Development);

        assessment.CurrentType.Should().Be(EnvironmentType.Production);
        assessment.LowersProtection.Should().BeTrue();
    }

    [Fact]
    public async Task EnvListTagging_FromTheStaleStore_KeepsTheRaise()
    {
        await ArrangeStaleDevelopmentThenRaiseAsync();

        // Step 4: env/list tags the environment from the daemon's store.
        await RpcMethodHandler.TagDiscoveredEnvironmentsAsync(_daemonStore,
            [new PPDS.Cli.Commands.Serve.Handlers.EnvironmentInfo { ApiUrl = Url, Type = "Sandbox" }],
            "profile", CancellationToken.None);

        var state = await FileStateAsync(Url);
        state!.Type.Should().Be(EnvironmentType.Production);
        state.Profiles.Should().Contain("profile");
        WriteProtectionResolver.ResolveForSqlDml(state).Should().Be(ProtectionLevel.Production);
    }

    [Fact]
    public async Task ExplicitSave_CarryingTheStaleType_IsALoweringAndIsRefused()
    {
        await ArrangeStaleDevelopmentThenRaiseAsync();
        var before = File.ReadAllText(_path);

        // An extension or TUI save that sends the type it last showed (Development).
        var save = () => new EnvironmentConfigService(_daemonStore).SaveConfigAsync(Url, label: "Dev", type: EnvironmentType.Development);

        (await save.Should().ThrowAsync<PpdsException>()).Which.ErrorCode
            .Should().Be(EnvironmentConfirmationErrorCodes.ProtectionLoweringConfirmationRequired);
        File.ReadAllText(_path).Should().Be(before, "a refused change writes nothing");
        (await FileStateAsync(Url))!.Type.Should().Be(EnvironmentType.Production);
    }

    [Fact]
    public async Task Remove_OfAnotherEnvironment_FromTheStaleStore_KeepsTheRaise()
    {
        await ArrangeStaleDevelopmentThenRaiseAsync();

        (await new EnvironmentConfigService(_daemonStore).RemoveConfigAsync(Other)).Should().BeTrue();

        (await FileStateAsync(Url))!.Type.Should().Be(EnvironmentType.Production);
        (await FileStateAsync(Other)).Should().BeNull();
    }

    [Fact]
    public async Task StoreRemove_OfAnotherEnvironment_FromTheStaleStore_KeepsTheRaise()
    {
        // Directly on the store, as profile delete (ProfileService) removes entries.
        await ArrangeStaleDevelopmentThenRaiseAsync();

        (await _daemonStore.RemoveConfigAsync(Other)).Should().BeTrue();

        (await FileStateAsync(Url))!.Type.Should().Be(EnvironmentType.Production);
        (await FileStateAsync(Other)).Should().BeNull();
    }

    [Fact]
    public async Task LabelOnlySave_FromTheStaleStore_KeepsTheRaise()
    {
        await ArrangeStaleDevelopmentThenRaiseAsync();

        var saved = await new EnvironmentConfigService(_daemonStore).SaveConfigAsync(Url, label: "Contoso");

        saved.Type.Should().Be(EnvironmentType.Production);
        var state = await FileStateAsync(Url);
        state!.Type.Should().Be(EnvironmentType.Production);
        state.Label.Should().Be("Contoso");
    }

    [Fact]
    public async Task TypeDefaultSave_FromTheStaleStore_KeepsTheRaise()
    {
        await ArrangeStaleDevelopmentThenRaiseAsync();

        await new EnvironmentConfigService(_daemonStore).SaveTypeDefaultAsync(EnvironmentType.Development, EnvironmentColor.Blue);

        (await FileStateAsync(Url))!.Type.Should().Be(EnvironmentType.Production);
    }

    [Fact]
    public async Task StoreSave_ChangesOnlyTheFieldsGiven_OnTheFreshEntry()
    {
        await _daemonStore.SaveConfigAsync(Url, label: "Old", type: EnvironmentType.Development, color: EnvironmentColor.Green);
        await _daemonStore.GetConfigAsync(Url); // cache it
        await _cliStore.SaveConfigAsync(Url, label: "New", color: EnvironmentColor.Red);

        await _daemonStore.SaveConfigAsync(Url, discoveredType: "Developer");

        var state = await FileStateAsync(Url);
        state!.Label.Should().Be("New");
        state.Color.Should().Be(EnvironmentColor.Red);
        state.Type.Should().Be(EnvironmentType.Development);
        state.DiscoveredType.Should().Be("Developer");
    }

    [Fact]
    public async Task StoreValidate_Throwing_WritesNothing()
    {
        await _daemonStore.SaveConfigAsync(Url, type: EnvironmentType.Production);
        var before = File.ReadAllText(_path);

        var save = () => _daemonStore.SaveConfigAsync(Url, null, EnvironmentType.Development, null, false, null, null,
            validate: (_, _) => throw new InvalidOperationException("refused"));

        await save.Should().ThrowAsync<InvalidOperationException>();
        File.ReadAllText(_path).Should().Be(before);
        (await _daemonStore.GetConfigAsync(Url))!.Type.Should().Be(EnvironmentType.Production, "the cache must not keep the refused change");
    }

    /// <summary>What the file holds now, read by a third, fresh store.</summary>
    private async Task<EnvironmentConfig?> FileStateAsync(string url)
    {
        using var fresh = new EnvironmentConfigStore(_path);
        return await fresh.GetConfigAsync(url);
    }
}

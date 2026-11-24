using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using PPDS.Auth.Profiles;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Cli.Services;
using PPDS.Cli.Services.Environment;
using Xunit;

namespace PPDS.Cli.Tests.Services.Environment;

/// <summary>
/// A change to an environment's saved configuration that lowers its write protection is itself a
/// protected action (#1829). These tests cover every transition and the service rule shared by the CLI,
/// the TUI, the daemon and the MCP server.
/// </summary>
[Trait("Category", "Unit")]
public sealed class EnvironmentProtectionLoweringTests : IDisposable
{
    private const string Url = "https://org.crm.dynamics.com";

    private readonly string _tempDir;
    private readonly string _storePath;
    private readonly EnvironmentConfigStore _store;

    public EnvironmentProtectionLoweringTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "ppds-lowering-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
        _storePath = Path.Combine(_tempDir, "environments.json");
        _store = new EnvironmentConfigStore(_storePath);
    }

    public void Dispose()
    {
        _store.Dispose();
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best effort */ }
    }

    /// <summary>The saved state before the change.</summary>
    public enum Before
    {
        /// <summary>No entry for the environment.</summary>
        NoEntry,
        /// <summary>An entry without a type, as the daemon's env/list creates.</summary>
        EntryWithoutType,
        TypeProduction,
        TypeUnknown,
        TypeDevelopment,
        TypeSandbox,
        /// <summary>Type Production with a hand-edited explicit protection of Production.</summary>
        ProductionWithExplicitProductionProtection,
        /// <summary>Type Development with a hand-edited explicit protection of Development.</summary>
        DevelopmentWithExplicitDevelopmentProtection
    }

    public static TheoryData<Before, EnvironmentType, bool, string> Transitions => new()
    {
        // before, new type, lowers?, writers lowered (comma-separated, in FindLowering order)
        { Before.NoEntry, EnvironmentType.Production, false, "" },
        { Before.NoEntry, EnvironmentType.Development, true, "WriteConfirmation" },
        { Before.NoEntry, EnvironmentType.Sandbox, true, "WriteConfirmation" },
        { Before.NoEntry, EnvironmentType.Test, true, "WriteConfirmation" },
        { Before.NoEntry, EnvironmentType.Trial, true, "WriteConfirmation" },
        { Before.NoEntry, EnvironmentType.Unknown, false, "" },
        { Before.EntryWithoutType, EnvironmentType.Production, false, "" },
        { Before.EntryWithoutType, EnvironmentType.Development, true, "WriteConfirmation,CrossEnvironmentSqlDml" },
        { Before.EntryWithoutType, EnvironmentType.Unknown, true, "CrossEnvironmentSqlDml" },
        { Before.TypeProduction, EnvironmentType.Production, false, "" },
        { Before.TypeProduction, EnvironmentType.Development, true, "WriteConfirmation,SqlDml,CrossEnvironmentSqlDml" },
        { Before.TypeProduction, EnvironmentType.Sandbox, true, "WriteConfirmation,SqlDml,CrossEnvironmentSqlDml" },
        { Before.TypeProduction, EnvironmentType.Test, true, "WriteConfirmation,SqlDml,CrossEnvironmentSqlDml" },
        { Before.TypeProduction, EnvironmentType.Trial, true, "WriteConfirmation,SqlDml,CrossEnvironmentSqlDml" },
        { Before.TypeProduction, EnvironmentType.Unknown, true, "SqlDml,CrossEnvironmentSqlDml" },
        { Before.TypeUnknown, EnvironmentType.Development, true, "WriteConfirmation" },
        { Before.TypeUnknown, EnvironmentType.Production, false, "" },
        { Before.TypeDevelopment, EnvironmentType.Sandbox, false, "" },
        { Before.TypeDevelopment, EnvironmentType.Production, false, "" },
        { Before.TypeDevelopment, EnvironmentType.Unknown, false, "" },
        { Before.TypeSandbox, EnvironmentType.Development, false, "" },
        { Before.ProductionWithExplicitProductionProtection, EnvironmentType.Development, false, "" },
        { Before.DevelopmentWithExplicitDevelopmentProtection, EnvironmentType.Production, false, "" },
    };

    [Theory]
    [MemberData(nameof(Transitions))]
    public async Task Transition_IsJudgedByEveryWriter(Before before, EnvironmentType newType, bool lowers, string writers)
    {
        await ArrangeAsync(before);
        var service = new EnvironmentConfigService(_store);

        var assessment = await service.AssessConfigChangeAsync(Url, newType);

        assessment.LowersProtection.Should().Be(lowers);
        string.Join(",", assessment.Lowerings.Select(l => l.Writer)).Should().Be(writers);
    }

    [Theory]
    [MemberData(nameof(Transitions))]
    public async Task Transition_LoweringIsRefusedWithoutConfirmationAndNothingIsSaved(
        Before before, EnvironmentType newType, bool lowers, string writers)
    {
        _ = writers;
        await ArrangeAsync(before);
        var snapshot = Snapshot();
        var service = new EnvironmentConfigService(_store);

        var save = () => service.SaveConfigAsync(Url, type: newType);

        if (lowers)
        {
            var ex = (await save.Should().ThrowAsync<PpdsException>()).Which;
            ex.ErrorCode.Should().Be(EnvironmentConfirmationErrorCodes.ProtectionLoweringConfirmationRequired);
            Snapshot().Should().Be(snapshot, "a refused change must not write the store");
        }
        else
        {
            (await save()).Type.Should().Be(newType);
        }
    }

    [Theory]
    [MemberData(nameof(Transitions))]
    public async Task Transition_ConfirmedLoweringIsSaved(Before before, EnvironmentType newType, bool lowers, string writers)
    {
        _ = (lowers, writers);
        await ArrangeAsync(before);
        var service = new EnvironmentConfigService(_store);

        var saved = await service.SaveConfigAsync(Url, type: newType, confirmLowerProtection: Url);

        saved.Type.Should().Be(newType);
    }

    [Theory]
    [MemberData(nameof(Transitions))]
    public async Task Transition_McpPolicyRefusesLoweringEvenWhenConfirmed(
        Before before, EnvironmentType newType, bool lowers, string writers)
    {
        _ = writers;
        await ArrangeAsync(before);
        var snapshot = Snapshot();
        var service = new EnvironmentConfigService(_store, ProtectionLoweringPolicy.Refuse);

        var save = () => service.SaveConfigAsync(Url, type: newType, confirmLowerProtection: Url);

        if (lowers)
        {
            var ex = (await save.Should().ThrowAsync<PpdsException>()).Which;
            ex.ErrorCode.Should().Be(EnvironmentConfirmationErrorCodes.ProtectionLoweringRefused);
            Snapshot().Should().Be(snapshot);
        }
        else
        {
            (await save()).Type.Should().Be(newType);
        }
    }

    [Theory]
    [InlineData("HTTPS://ORG.CRM.DYNAMICS.COM/")]
    [InlineData("https://org.crm.dynamics.com/")]
    public async Task Confirmation_HarmlessUrlVariationsMatch(string confirmation)
    {
        await _store.SaveConfigAsync(Url, type: EnvironmentType.Production);
        var service = new EnvironmentConfigService(_store);

        var saved = await service.SaveConfigAsync(Url, type: EnvironmentType.Development, confirmLowerProtection: confirmation);

        saved.Type.Should().Be(EnvironmentType.Development);
    }

    [Theory]
    [InlineData("https://other.crm.dynamics.com")]
    [InlineData("http://org.crm.dynamics.com")]
    [InlineData("https://org")]                                // a prefix of the address
    [InlineData("https://org.crm.dynamics.co")]                // a shorter prefix ending inside the host
    [InlineData("https://org.crm.dynamics.com.example.test")]  // the address as a prefix
    [InlineData("https://org.crm.dynamics.com/extra")]
    public async Task Confirmation_ForAnotherEnvironment_IsRefusedAndNothingIsSaved(string confirmation)
    {
        await _store.SaveConfigAsync(Url, type: EnvironmentType.Production);
        var snapshot = Snapshot();
        var service = new EnvironmentConfigService(_store);

        var save = () => service.SaveConfigAsync(Url, type: EnvironmentType.Development, confirmLowerProtection: confirmation);

        (await save.Should().ThrowAsync<PpdsException>()).Which.ErrorCode
            .Should().Be(EnvironmentConfirmationErrorCodes.TargetMismatch);
        Snapshot().Should().Be(snapshot);
    }

    [Fact]
    public async Task RequiredMessage_SaysWhatIsLoweredAndNamesNoCommand()
    {
        await _store.SaveConfigAsync(Url, type: EnvironmentType.Production);
        var service = new EnvironmentConfigService(_store);

        var save = () => service.SaveConfigAsync(Url, type: EnvironmentType.Development);

        var message = (await save.Should().ThrowAsync<PpdsException>()).Which.UserMessage;
        message.Should().Contain($"Changing the type of '{Url}' from Production to Development lowers write protection");
        message.Should().Contain("Production to Development");
        message.Should().Contain("No change was saved.");
        message.Should().NotContain("ppds ").And.NotContain("--");
    }

    [Fact]
    public async Task DiscoveredProduction_ContradictionIsReported()
    {
        await _store.SaveConfigAsync(Url, discoveredType: "Production");
        var service = new EnvironmentConfigService(_store);

        var assessment = await service.AssessConfigChangeAsync(Url, EnvironmentType.Development);

        assessment.DiscoveredTypeWarning.Should().Be("Discovery reports this environment as Production.");
        var save = () => service.SaveConfigAsync(Url, type: EnvironmentType.Development);
        (await save.Should().ThrowAsync<PpdsException>()).Which.UserMessage
            .Should().Contain("Discovery reports this environment as Production.");
    }

    [Theory]
    [InlineData("Developer", EnvironmentType.Development)]
    [InlineData("Production", EnvironmentType.Production)]
    [InlineData(null, EnvironmentType.Development)]
    public async Task DiscoveredType_NoContradiction_NoWarning(string? discovered, EnvironmentType newType)
    {
        await _store.SaveConfigAsync(Url, discoveredType: discovered);
        var service = new EnvironmentConfigService(_store);

        (await service.AssessConfigChangeAsync(Url, newType)).DiscoveredTypeWarning.Should().BeNull();
    }

    [Fact]
    public async Task LabelAndColorChanges_NeverNeedConfirmation_EvenOnTheMcpServer()
    {
        await _store.SaveConfigAsync(Url, type: EnvironmentType.Production);
        var service = new EnvironmentConfigService(_store, ProtectionLoweringPolicy.Refuse);

        var saved = await service.SaveConfigAsync(Url, label: "Contoso", color: EnvironmentColor.Blue);

        saved.Label.Should().Be("Contoso");
        saved.Type.Should().Be(EnvironmentType.Production);
    }

    [Theory]
    [InlineData(Before.TypeProduction)]
    [InlineData(Before.EntryWithoutType)]
    [InlineData(Before.DevelopmentWithExplicitDevelopmentProtection)]
    public async Task Remove_NeverLowersProtection_EvenOnTheMcpServer(Before before)
    {
        await ArrangeAsync(before);
        var stored = await _store.GetConfigAsync(Url);
        var service = new EnvironmentConfigService(_store, ProtectionLoweringPolicy.Refuse);

        WriteProtectionResolver.FindLowering(stored, after: null).Should().BeEmpty();
        (await service.RemoveConfigAsync(Url)).Should().BeTrue();
        (await _store.GetConfigAsync(Url)).Should().BeNull();
    }

    [Fact]
    public async Task Assess_SavesNothing()
    {
        var service = new EnvironmentConfigService(_store);

        await service.AssessConfigChangeAsync(Url, EnvironmentType.Development);

        File.Exists(_storePath).Should().BeFalse();
    }

    [Fact]
    public async Task ServiceRegistration_DefaultHost_RequiresConfirmation()
    {
        using var provider = Provider(policy: null);
        await _store.SaveConfigAsync(Url, type: EnvironmentType.Production);

        var save = () => provider.GetRequiredService<IEnvironmentConfigService>()
            .SaveConfigAsync(Url, type: EnvironmentType.Development);

        (await save.Should().ThrowAsync<PpdsException>()).Which.ErrorCode
            .Should().Be(EnvironmentConfirmationErrorCodes.ProtectionLoweringConfirmationRequired);
    }

    [Fact]
    public async Task ServiceRegistration_HostWithRefusePolicy_RefusesConfirmedLowering()
    {
        using var provider = Provider(ProtectionLoweringPolicy.Refuse);
        await _store.SaveConfigAsync(Url, type: EnvironmentType.Production);

        var save = () => provider.GetRequiredService<IEnvironmentConfigService>()
            .SaveConfigAsync(Url, type: EnvironmentType.Development, confirmLowerProtection: Url);

        (await save.Should().ThrowAsync<PpdsException>()).Which.ErrorCode
            .Should().Be(EnvironmentConfirmationErrorCodes.ProtectionLoweringRefused);
    }

    [Fact]
    public void ExitCode_ForUnconfirmedLowering_IsConfirmationRequired()
    {
        var ex = new PpdsException(EnvironmentConfirmationErrorCodes.ProtectionLoweringConfirmationRequired, "x");

        ExceptionMapper.ToExitCode(ex).Should().Be(ExitCodes.ConfirmationRequired);
    }

    private ServiceProvider Provider(ProtectionLoweringPolicy? policy)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(_store); // AddAuthServices uses TryAdd, so this temporary store wins.
        if (policy is not null)
            services.AddSingleton(policy);
        services.AddCliApplicationServices();
        return services.BuildServiceProvider();
    }

    private async Task ArrangeAsync(Before before)
    {
        switch (before)
        {
            case Before.NoEntry:
                return;
            case Before.EntryWithoutType:
                await _store.SaveConfigAsync(Url, discoveredType: "Sandbox", profileName: "p");
                return;
            case Before.TypeProduction:
                await _store.SaveConfigAsync(Url, type: EnvironmentType.Production);
                return;
            case Before.TypeUnknown:
                await _store.SaveConfigAsync(Url, type: EnvironmentType.Unknown);
                return;
            case Before.TypeDevelopment:
                await _store.SaveConfigAsync(Url, type: EnvironmentType.Development);
                return;
            case Before.TypeSandbox:
                await _store.SaveConfigAsync(Url, type: EnvironmentType.Sandbox);
                return;
            case Before.ProductionWithExplicitProductionProtection:
                await SaveWithProtectionAsync(EnvironmentType.Production, ProtectionLevel.Production);
                return;
            case Before.DevelopmentWithExplicitDevelopmentProtection:
                await SaveWithProtectionAsync(EnvironmentType.Development, ProtectionLevel.Development);
                return;
        }
    }

    private async Task SaveWithProtectionAsync(EnvironmentType type, ProtectionLevel protection)
    {
        // An explicit protection value can only be set by editing the file; do the same through the store.
        await _store.SaveConfigAsync(Url, type: type);
        var collection = await _store.LoadAsync();
        collection.Environments.Single().Protection = protection;
        await _store.SaveAsync(collection);
    }

    private string Snapshot() => File.Exists(_storePath) ? File.ReadAllText(_storePath) : "<none>";
}

using System.IO;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.SqlServer.TransactSql.ScriptDom;
using Moq;
using PPDS.Auth.Profiles;
using PPDS.Cli.Services.Environment;
using PPDS.Cli.Services.Query;
using PPDS.Cli.Services.Settings;
using PPDS.Cli.Tests.Mocks;
using PPDS.Cli.Tests.Services.Shared;
using PPDS.Cli.Tui;
using PPDS.Dataverse.Query;
using Xunit;

namespace PPDS.Cli.Tests.Tui;

/// <summary>
/// The TUI is long-lived: its protection decisions must come from the file as it is at the time of each query, not
/// from what the session read at startup (#1829, review round 3). A second store on the same file stands for the CLI
/// or the extension's daemon; the session's own <see cref="InteractiveSession.EnvironmentConfigService"/> stands for
/// the TUI's Environment Config dialog.
/// </summary>
[Trait("Category", "TuiUnit")]
public sealed class InteractiveSessionProtectionFreshnessTests : IDisposable
{
    private const string Url = "https://dev.crm.dynamics.com";
    private const string TargetUrl = "https://target.crm.dynamics.com";

    private readonly TempProfileStore _profiles;
    private readonly string _envPath;
    private readonly EnvironmentConfigStore _tuiStore;
    private readonly EnvironmentConfigStore _otherProcessStore;
    private readonly InteractiveSession _session;

    public InteractiveSessionProtectionFreshnessTests()
    {
        _profiles = new TempProfileStore();
        _envPath = Path.Combine(_profiles.TempDirectory, "environments.json");
        _tuiStore = new EnvironmentConfigStore(_envPath);
        _otherProcessStore = new EnvironmentConfigStore(_envPath);
        var factory = new MockServiceProviderFactory(services =>
        {
            services.AddSingleton<ISqlQueryService>(_ =>
                new SqlQueryService(new Mock<IQueryExecutor>().Object, new InactiveFakeShakedownGuard()));
            return services;
        });
        _session = new InteractiveSession(null, _profiles.Store, _tuiStore, new TuiStateStore(Path.Combine(_profiles.TempDirectory, "tui.json")), factory);
    }

    public void Dispose()
    {
        _session.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _tuiStore.Dispose();
        _otherProcessStore.Dispose();
        _profiles.Dispose();
    }

    private async Task StartAsync()
    {
        await _profiles.SeedProfilesAsync("p", TempProfileStore.CreateTestProfile("p", environmentUrl: Url, environmentName: "Dev"));
        await _session.InitializeAsync();
    }

    private async Task<SqlQueryService> NextQueryServiceAsync()
        => (SqlQueryService)await _session.GetSqlQueryServiceAsync(Url, CancellationToken.None);

    [Fact]
    public async Task ARaiseByAnotherStore_IsSeenByTheNextWriteCheck_OnTheConnectedEnvironment()
    {
        await _otherProcessStore.SaveConfigAsync(Url, type: EnvironmentType.Development); // arrange: straight to the file
        await StartAsync();
        (await NextQueryServiceAsync()).EnvironmentProtectionLevel.Should().Be(ProtectionLevel.Development);

        // The CLI (another process) raises the environment while the TUI is open.
        await new EnvironmentConfigService(_otherProcessStore).SaveConfigAsync(Url, type: EnvironmentType.Production);

        (await NextQueryServiceAsync()).EnvironmentProtectionLevel.Should().Be(ProtectionLevel.Production);
    }

    [Fact]
    public async Task AnEntryRemovedByAnotherStore_IsProductionAtTheNextWriteCheck_NotTheLevelBefore()
    {
        await _otherProcessStore.SaveConfigAsync(Url, type: EnvironmentType.Development); // arrange: straight to the file
        await StartAsync();
        (await NextQueryServiceAsync()).EnvironmentProtectionLevel.Should().Be(ProtectionLevel.Development);

        await _otherProcessStore.RemoveConfigAsync(Url);

        (await NextQueryServiceAsync()).EnvironmentProtectionLevel.Should().Be(ProtectionLevel.Production);
    }

    [Fact]
    public async Task ARaisedCrossEnvironmentTarget_ThroughTheTuisOwnDialog_Asks()
    {
        await _otherProcessStore.SaveConfigAsync(TargetUrl, label: "B", type: EnvironmentType.Development); // arrange
        await StartAsync();
        CrossEnvironmentDecision(await NextQueryServiceAsync()).RequiresConfirmation.Should().BeFalse("a Development target with policy Allow runs");

        // The TUI's Environment Config dialog saves through the session's own service.
        await _session.EnvironmentConfigService.SaveConfigAsync(TargetUrl, type: EnvironmentType.Production);

        CrossEnvironmentDecision(await NextQueryServiceAsync()).RequiresConfirmation.Should().BeTrue();
    }

    [Fact]
    public async Task ARaisedCrossEnvironmentTarget_ThroughAnotherStore_Asks()
    {
        await _otherProcessStore.SaveConfigAsync(TargetUrl, label: "B", type: EnvironmentType.Development); // arrange
        await StartAsync();
        CrossEnvironmentDecision(await NextQueryServiceAsync()).RequiresConfirmation.Should().BeFalse();

        await new EnvironmentConfigService(_otherProcessStore).SaveConfigAsync(TargetUrl, type: EnvironmentType.Production);

        CrossEnvironmentDecision(await NextQueryServiceAsync()).RequiresConfirmation.Should().BeTrue();
    }

    [Fact]
    public async Task ALabelAddedByAnotherStore_IsResolvableByTheNextQuery()
    {
        await StartAsync();
        (await NextQueryServiceAsync()).ProfileResolver!.ResolveByLabel("B").Should().BeNull();

        await _otherProcessStore.SaveConfigAsync(TargetUrl, label: "B");

        var target = (await NextQueryServiceAsync()).ProfileResolver!.ResolveByLabel("B");
        target!.Url.Should().Be(EnvironmentConfig.NormalizeUrl(TargetUrl));
        WriteProtectionResolver.ResolveForCrossEnvironmentTarget(target).Should().Be(ProtectionLevel.Production, "no type is Production");
    }

    [Fact]
    public async Task DuplicateAndReservedLabelsInTheFile_DoNotFailTheQuery_AsInTheDaemon()
    {
        // Review round 4: the session builds the label index on every query, so it must not fail on a file the
        // daemon accepts. Duplicate labels keep the first entry; the reserved label "dbo" is skipped.
        await StartAsync();
        File.WriteAllText(_envPath, $$"""
            { "version": 1, "environments": [
              { "url": "{{Url}}", "type": "Development" },
              { "url": "{{TargetUrl}}", "label": "B", "type": "Production" },
              { "url": "https://second.crm.dynamics.com", "label": "b", "type": "Development" },
              { "url": "https://third.crm.dynamics.com", "label": "dbo" } ] }
            """);

        var service = await NextQueryServiceAsync();

        service.EnvironmentProtectionLevel.Should().Be(ProtectionLevel.Development);
        service.ProfileResolver!.ResolveByLabel("B")!.Url.Should().Be(EnvironmentConfig.NormalizeUrl(TargetUrl));
        CrossEnvironmentDecision(service).RequiresConfirmation.Should().BeTrue("the first B is Production");
        service.ProfileResolver.ResolveByLabel("dbo").Should().BeNull();
    }

    [Theory]
    [InlineData("\"type\": 42")]
    [InlineData("\"type\": 3")]
    [InlineData("\"type\": \"Development\", \"protection\": -1")]
    public async Task AValueThatIsNotAMemberName_FailsTheQueryWiring_SoNoDmlRuns(string fields)
    {
        await _otherProcessStore.SaveConfigAsync(Url, type: EnvironmentType.Development); // arrange
        await StartAsync();
        File.WriteAllText(_envPath, $$"""{ "version": 1, "environments": [ { "url": "{{Url}}", {{fields}} } ] }""");

        await FluentActions.Awaiting(NextQueryServiceAsync).Should().ThrowAsync<System.Text.Json.JsonException>();
    }

    /// <summary>
    /// The decision the query service makes for <c>UPDATE [B].account ...</c> with <c>cross_env_dml_policy</c> Allow:
    /// the target's protection comes from the resolver the session wired for this query.
    /// </summary>
    private static DmlSafetyResult CrossEnvironmentDecision(SqlQueryService service)
    {
        var target = service.ProfileResolver!.ResolveByLabel("B");
        var statement = new TSql160Parser(true).Parse(new StringReader("UPDATE account SET name = 'x' WHERE name = 'y'"), out _)
            is TSqlScript script ? script.Batches[0].Statements[0] : throw new InvalidOperationException();
        return new DmlSafetyGuard().CheckCrossEnvironmentDml(statement,
            new QuerySafetySettings { CrossEnvironmentDmlPolicy = CrossEnvironmentDmlPolicy.Allow }, "Dev", "B",
            WriteProtectionResolver.ResolveForCrossEnvironmentTarget(target));
    }
}

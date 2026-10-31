using FluentAssertions;
using Moq;
using PPDS.Auth.Credentials;
using PPDS.Auth.Profiles;
using PPDS.Dataverse.Pooling;
using PPDS.Mcp.Infrastructure;
using PPDS.Mcp.Tools;
using Microsoft.Extensions.DependencyInjection;
using PPDS.Cli.Services.Environment;
using Xunit;

namespace PPDS.Mcp.Tests.Infrastructure;

public sealed class McpSessionSnapshotTests
{
    [Fact]
    public async Task AuthWho_AndProviderUseSameOverrideAndSwitchedTarget_WithTruthfulMetadata()
    {
        using var fixture = new Fixture();
        fixture.Profile.AuthMethod = (AuthMethod)999; // unsupported provider fails locally; no credential/token access
        fixture.Profile.Environment!.DisplayName = "Initial";
        fixture.Profile.Environment.UniqueName = "initial-unique";
        await fixture.SaveAsync();
        using var context = fixture.Context(new() { Environment = "https://override.example.test", AllowedEnvironments = ["https://next.example.test"] });
        await context.InitializeAsync();
        var tool = new AuthWhoTool(context);
        var overridden = await tool.ExecuteAsync();
        overridden.Environment!.Url.Should().Be("https://override.example.test");
        overridden.Environment.UniqueName.Should().BeNull();
        await using (var provider = await context.CreateServiceProviderAsync())
            provider.GetRequiredService<IEnvironmentConfirmationService>().Context.EnvironmentUrl.Should().Be(overridden.Environment.Url);
        await context.SwitchEnvironmentAsync(new() { Url = "https://next.example.test", DisplayName = "Next", UniqueName = "next-unique", EnvironmentId = "next-id" });
        var switched = await tool.ExecuteAsync();
        switched.Environment!.Url.Should().Be("https://next.example.test");
        switched.Environment.DisplayName.Should().Be("Next");
        switched.Environment.UniqueName.Should().Be("next-unique");
        await using (var provider = await context.CreateServiceProviderAsync())
            provider.GetRequiredService<IEnvironmentConfirmationService>().Context.EnvironmentUrl.Should().Be(switched.Environment.Url);
        fixture.Profile.Environment.Url.Should().Be("https://initial.example.test");
    }

    [Fact]
    public async Task StartupSnapshot_DoesNotFollowActiveProfileOrReturnedObjectChanges()
    {
        using var fixture = new Fixture();
        await fixture.SaveAsync();
        using var context = fixture.Context();
        await context.InitializeAsync();
        fixture.Profile.Username = "other@invalid.test";
        fixture.Profile.Environment!.Url = "https://other.example.test";
        fixture.Collection.ActiveProfileIndex = 99;
        await fixture.Store.SaveAsync(fixture.Collection);
        var first = await context.GetActiveProfileAsync();
        first.Username = "mutated@invalid.test";
        (await context.GetActiveProfileAsync()).Username.Should().Be("original@invalid.test");
        await context.GetPoolAsync();
        fixture.Pools.Verify(p => p.GetOrCreateCapturedPoolAsync(
            It.Is<AuthProfile>(p => p.Username == "original@invalid.test"), "https://initial.example.test", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task MissingProfileAtStartup_DoesNotAdoptLaterProfile()
    {
        using var fixture = new Fixture();
        using var context = fixture.Context();
        await context.InitializeAsync();
        await fixture.SaveAsync();
        var act = () => context.GetPoolAsync();
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*startup*restart*");
        fixture.Pools.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task InFlightPoolRequest_UsesCapturedTargetWhileLaterRequestUsesSwitch_WithoutGlobalMutation()
    {
        using var fixture = new Fixture();
        await fixture.SaveAsync();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<IDataverseConnectionPool>(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Pools.Setup(p => p.GetOrCreateCapturedPoolAsync(It.IsAny<AuthProfile>(), "https://initial.example.test", It.IsAny<CancellationToken>()))
            .Returns(() => { entered.SetResult(); return release.Task; });
        using var context = fixture.Context(new() { AllowedEnvironments = ["https://next.example.test"] });
        await context.InitializeAsync();
        var first = context.GetPoolAsync();
        await entered.Task;
        await context.SwitchEnvironmentAsync(new() { Url = "https://next.example.test" });
        await context.GetPoolAsync();
        release.SetResult(Mock.Of<IDataverseConnectionPool>());
        await first;
        fixture.Pools.Verify(p => p.GetOrCreateCapturedPoolAsync(It.IsAny<AuthProfile>(), "https://next.example.test", It.IsAny<CancellationToken>()), Times.Once);
        fixture.Pools.Verify(p => p.InvalidateEnvironment(It.IsAny<string>()), Times.Never);
        (await fixture.Store.LoadAsync()).ActiveProfile!.Environment!.Url.Should().Be("https://initial.example.test");
    }

    [Fact]
    public async Task SameEnvironmentIsAllowed_UnknownSwitchRejected_AndOptionsAreCopied()
    {
        using var fixture = new Fixture();
        await fixture.SaveAsync();
        var options = new McpSessionOptions();
        using var context = fixture.Context(options);
        options.AllowedEnvironments.Add("https://next.example.test");
        await context.InitializeAsync();
        await context.SwitchEnvironmentAsync(new() { Url = "HTTPS://INITIAL.EXAMPLE.TEST/" });
        var act = () => context.SwitchEnvironmentAsync(new() { Url = "https://next.example.test" });
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*disabled*");
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "ppds-session-" + Guid.NewGuid().ToString("N"));
        public ProfileStore Store { get; }
        public AuthProfile Profile { get; } = new() { Index = 1, Name = "fixture", Username = "original@invalid.test", Environment = new() { Url = "https://initial.example.test" } };
        public ProfileCollection Collection { get; }
        public Mock<IMcpConnectionPoolManager> Pools { get; } = new();
        public Fixture()
        {
            Directory.CreateDirectory(_directory);
            Store = new(Path.Combine(_directory, "profiles.json"));
            Collection = new() { Profiles = [Profile], ActiveProfileIndex = 1 };
            Pools.Setup(p => p.GetOrCreateCapturedPoolAsync(It.IsAny<AuthProfile>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(Mock.Of<IDataverseConnectionPool>());
        }
        public Task SaveAsync() => Store.SaveAsync(Collection);
        public McpToolContext Context(McpSessionOptions? options = null) => new(Pools.Object, Store, Mock.Of<ISecureCredentialStore>(), options ?? new());
        public void Dispose()
        {
            Store.Dispose();
            if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
        }
    }
}

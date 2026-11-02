using FluentAssertions;
using ModelContextProtocol;
using Moq;
using PPDS.Auth.Credentials;
using PPDS.Auth.Discovery;
using PPDS.Auth.Profiles;
using PPDS.Mcp.Infrastructure;
using PPDS.Mcp.Tools;
using Xunit;
using ProfileEnvironment = PPDS.Auth.Profiles.EnvironmentInfo;

namespace PPDS.Mcp.Tests.Infrastructure;

[Trait("Category", "Unit")]
public sealed class McpEnvironmentSelectionTests
{
    private const string Initial = "https://initial.example.test";
    private const string Next = "https://next.example.test";

    [Theory]
    [InlineData("https://blocked.example.test")]
    [InlineData("http://127.0.0.1")]
    [InlineData("blocked.crm.dynamics.com")]
    [InlineData("https://initial.example.test@evil.test")]
    [InlineData("https://initial.example.test?secret=1")]
    [InlineData("https://initial.example.test#fragment")]
    [InlineData("ftp://initial.example.test")]
    [InlineData("https:///bad")]
    [InlineData("//blocked.crm.dynamics.com")]
    public async Task RejectedUrl_DoesNotResolveOrSwitch(string identifier)
    {
        using var fixture = new Fixture();
        using var context = await fixture.CreateAsync();
        var action = () => new EnvSelectTool(context).ExecuteAsync(identifier);
        await action.Should().ThrowAsync<McpException>();
        fixture.DirectCalls.Should().Be(0);
        fixture.DiscoveryCalls.Should().Be(0);
        (await context.GetActiveProfileAsync()).Environment!.Url.Should().Be(Initial);
        fixture.Credentials.VerifyNoOtherCalls();
        fixture.Pools.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData("HTTPS://INITIAL.EXAMPLE.TEST/", Initial)]
    [InlineData("https://next.example.test/", Next)]
    [InlineData("allowed.crm.dynamics.com", "https://allowed.crm.dynamics.com")]
    public async Task AllowedUrl_UsesCapturedProfileAndNormalizedTarget(string identifier, string target)
    {
        using var fixture = new Fixture();
        using var context = await fixture.CreateAsync(new() { AllowedEnvironments = [Next, "https://allowed.crm.dynamics.com"] });
        fixture.Profile.Username = "replacement@invalid.test";
        await fixture.Store.SaveAsync(fixture.Collection);
        var result = await new EnvSelectTool(context).ExecuteAsync(identifier);
        result.Url.Should().Be(target);
        fixture.DirectCalls.Should().Be(1);
        fixture.DiscoveryCalls.Should().Be(0);
        fixture.ObservedProfile!.Username.Should().Be("original@invalid.test");
        fixture.ObservedUrl.Should().Be(target);
        (await context.GetActiveProfileAsync()).Environment!.Url.Should().Be(target);
        (await fixture.Store.LoadAsync()).ActiveProfile!.Environment!.Url.Should().Be(Initial);
    }

    [Theory]
    [InlineData(true, AuthMethod.InteractiveBrowser)]
    [InlineData(false, AuthMethod.InteractiveBrowser)]
    [InlineData(true, AuthMethod.ClientSecret)]
    [InlineData(false, AuthMethod.ClientSecret)]
    public async Task NameSelection_UsesDiscoveryOnly(bool allowed, AuthMethod method)
    {
        using var fixture = new Fixture();
        fixture.Profile.AuthMethod = method;
        fixture.Discovered = [new() { FriendlyName = "Next QA", UniqueName = "next-qa", ApiUrl = Next }];
        using var context = await fixture.CreateAsync(new() { AllowedEnvironments = allowed ? [Next] : [] });
        var tool = new EnvSelectTool(context);
        if (allowed)
        {
            var result = await tool.ExecuteAsync("Next QA");
            result.Url.Should().Be(Next);
            result.ResolutionMethod.Should().Be(method == AuthMethod.ClientSecret ? "BapDiscovery" : "GlobalDiscovery");
        }
        else
        {
            var action = () => tool.ExecuteAsync("Next QA");
            await action.Should().ThrowAsync<McpException>();
        }
        fixture.DirectCalls.Should().Be(0);
        fixture.DiscoveryCalls.Should().Be(1);
        fixture.Credentials.VerifyNoOtherCalls();
        (await context.GetActiveProfileAsync()).Environment!.Url.Should().Be(allowed ? Next : Initial);
    }

    [Theory]
    [InlineData("QA One", "QA Two")]
    [InlineData("QA", "QA")]
    public async Task AmbiguousName_DoesNotSwitch(string firstName, string secondName)
    {
        using var fixture = new Fixture();
        fixture.Discovered = [new() { FriendlyName = firstName, ApiUrl = Next }, new() { FriendlyName = secondName, ApiUrl = "https://another.example.test" }];
        using var context = await fixture.CreateAsync(new() { AllowedEnvironments = [Next, "https://another.example.test"] });
        var action = () => new EnvSelectTool(context).ExecuteAsync("QA");
        await action.Should().ThrowAsync<McpException>();
        fixture.DirectCalls.Should().Be(0);
        (await context.GetActiveProfileAsync()).Environment!.Url.Should().Be(Initial);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cancellation_DoesNotSwitch(bool duringResolution)
    {
        using var fixture = new Fixture();
        using var cancellation = new CancellationTokenSource();
        fixture.DuringDirectResolution = () => cancellation.Cancel();
        using var context = await fixture.CreateAsync(new() { AllowedEnvironments = [Next] });
        if (!duringResolution) cancellation.Cancel();
        var action = () => new EnvSelectTool(context).ExecuteAsync(Next, cancellation.Token);
        await action.Should().ThrowAsync<OperationCanceledException>();
        fixture.DirectCalls.Should().Be(duringResolution ? 1 : 0);
        (await context.GetActiveProfileAsync()).Environment!.Url.Should().Be(Initial);
    }

    [Fact]
    public async Task ResolvedDifferentTarget_IsRejected()
    {
        using var fixture = new Fixture();
        fixture.ReturnedUrl = "https://disallowed.example.test";
        using var context = await fixture.CreateAsync(new() { AllowedEnvironments = [Next] });
        var action = () => new EnvSelectTool(context).ExecuteAsync(Next);
        await action.Should().ThrowAsync<McpException>();
        fixture.DirectCalls.Should().Be(1);
        (await context.GetActiveProfileAsync()).Environment!.Url.Should().Be(Initial);
    }

    [Fact]
    public async Task DiscoveryCancellation_DoesNotSwitch()
    {
        using var fixture = new Fixture();
        using var cancellation = new CancellationTokenSource();
        fixture.Discovered = [new() { FriendlyName = "Next QA", ApiUrl = Next }];
        fixture.DuringDiscovery = () => cancellation.Cancel();
        using var context = await fixture.CreateAsync(new() { AllowedEnvironments = [Next] });
        var action = () => new EnvSelectTool(context).ExecuteAsync("Next QA", cancellation.Token);
        await action.Should().ThrowAsync<OperationCanceledException>();
        fixture.DiscoveryCalls.Should().Be(1);
        fixture.DirectCalls.Should().Be(0);
        (await context.GetActiveProfileAsync()).Environment!.Url.Should().Be(Initial);
    }

    [Fact]
    public async Task UnsupportedNameAuthentication_DoesNotResolve()
    {
        using var fixture = new Fixture();
        fixture.Profile.AuthMethod = AuthMethod.ManagedIdentity;
        using var context = await fixture.CreateAsync(new() { AllowedEnvironments = [Next] });
        var action = () => new EnvSelectTool(context).ExecuteAsync("Next QA");
        await action.Should().ThrowAsync<McpException>();
        fixture.DiscoveryCalls.Should().Be(0);
        fixture.DirectCalls.Should().Be(0);
        fixture.Credentials.VerifyNoOtherCalls();
        (await context.GetActiveProfileAsync()).Environment!.Url.Should().Be(Initial);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "ppds-env-selection-" + Guid.NewGuid().ToString("N"));
        public ProfileStore Store { get; }
        public AuthProfile Profile { get; } = new() { Index = 1, Name = "fixture", Username = "original@invalid.test", Environment = new() { Url = Initial } };
        public ProfileCollection Collection { get; }
        public Mock<ISecureCredentialStore> Credentials { get; } = new(MockBehavior.Strict);
        public Mock<IMcpConnectionPoolManager> Pools { get; } = new(MockBehavior.Strict);
        public IReadOnlyList<DiscoveredEnvironment> Discovered { get; set; } = [];
        public int DirectCalls { get; private set; }
        public int DiscoveryCalls { get; private set; }
        public AuthProfile? ObservedProfile { get; private set; }
        public string? ObservedUrl { get; private set; }
        public string? ReturnedUrl { get; set; }
        public Action? DuringDirectResolution { get; set; }
        public Action? DuringDiscovery { get; set; }

        public Fixture()
        {
            Directory.CreateDirectory(_directory);
            Store = new(Path.Combine(_directory, "profiles.json"));
            Collection = new() { Profiles = [Profile], ActiveProfileIndex = 1 };
        }

        public async Task<McpToolContext> CreateAsync(McpSessionOptions? options = null)
        {
            await Store.SaveAsync(Collection);
            var context = new McpToolContext(Pools.Object, Store, Credentials.Object, options ?? new())
            {
                DirectEnvironmentResolver = (profile, url, _) =>
                {
                    DirectCalls++;
                    ObservedProfile = profile;
                    ObservedUrl = url;
                    DuringDirectResolution?.Invoke();
                    return Task.FromResult(EnvironmentResolutionResult.Succeeded(new ProfileEnvironment { Url = ReturnedUrl ?? url }, ResolutionMethod.DirectConnection));
                },
                EnvironmentDiscovery = (_, _) =>
                {
                    DiscoveryCalls++;
                    DuringDiscovery?.Invoke();
                    return Task.FromResult(Discovered);
                }
            };
            await context.InitializeAsync();
            return context;
        }

        public void Dispose()
        {
            Store.Dispose();
            Directory.Delete(_directory, recursive: true);
        }
    }
}

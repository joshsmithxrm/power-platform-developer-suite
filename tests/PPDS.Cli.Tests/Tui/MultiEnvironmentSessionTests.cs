using PPDS.Auth.Profiles;
using PPDS.Cli.Tests.Mocks;
using PPDS.Cli.Tui;
using Xunit;

namespace PPDS.Cli.Tests.Tui;

[Trait("Category", "TuiUnit")]
public sealed class MultiEnvironmentSessionTests : IDisposable
{
    private readonly TempProfileStore _tempStore;
    private readonly MockServiceProviderFactory _mockFactory;

    public MultiEnvironmentSessionTests()
    {
        _tempStore = new TempProfileStore();
        _mockFactory = new MockServiceProviderFactory();
    }

    public void Dispose()
    {
        _tempStore.Dispose();
    }

    private InteractiveSession CreateSession() =>
        new(null, _tempStore.Store, _mockFactory);

    [Fact]
    public async Task GetServiceProvider_CachesByUrl_IndependentProviders()
    {
        await using var session = CreateSession();

        var providerDev = await session.GetServiceProviderAsync("https://dev.crm.dynamics.com");
        var providerProd = await session.GetServiceProviderAsync("https://prod.crm.dynamics.com");
        var providerDev2 = await session.GetServiceProviderAsync("https://dev.crm.dynamics.com");

        Assert.Same(providerDev, providerDev2);
        Assert.NotSame(providerDev, providerProd);
        Assert.Equal(2, _mockFactory.CreationLog.Count);
    }

    [Fact]
    public async Task GetServiceProvider_ThreeEnvironments_ThreeProviders()
    {
        await using var session = CreateSession();

        await session.GetServiceProviderAsync("https://dev.crm.dynamics.com");
        await session.GetServiceProviderAsync("https://qa.crm4.dynamics.com");
        await session.GetServiceProviderAsync("https://prod.crm.dynamics.com");

        Assert.Equal(3, _mockFactory.CreationLog.Count);
    }

    [Fact]
    public async Task InvalidateAsync_SpecificUrl_OnlyRemovesThatProvider()
    {
        await using var session = CreateSession();

        await session.GetServiceProviderAsync("https://dev.crm.dynamics.com");
        await session.GetServiceProviderAsync("https://prod.crm.dynamics.com");

        await session.InvalidateAsync("https://dev.crm.dynamics.com");

        // Prod should still be cached
        await session.GetServiceProviderAsync("https://prod.crm.dynamics.com");
        Assert.Equal(2, _mockFactory.CreationLog.Count); // No new creation for prod

        // Dev should create a new provider
        await session.GetServiceProviderAsync("https://dev.crm.dynamics.com");
        Assert.Equal(3, _mockFactory.CreationLog.Count); // New creation for dev
    }

    [Fact]
    public async Task InvalidateAsync_AllUrls_ClearsCache()
    {
        await using var session = CreateSession();

        await session.GetServiceProviderAsync("https://dev.crm.dynamics.com");
        await session.GetServiceProviderAsync("https://prod.crm.dynamics.com");

        await session.InvalidateAsync(); // No URL = invalidate all

        await session.GetServiceProviderAsync("https://dev.crm.dynamics.com");
        await session.GetServiceProviderAsync("https://prod.crm.dynamics.com");

        Assert.Equal(4, _mockFactory.CreationLog.Count); // 2 original + 2 recreated
    }

    [Fact]
    public async Task SetActiveProfile_InvalidatesAllProviders()
    {
        var profile1 = TempProfileStore.CreateTestProfile("profile1", environmentUrl: "https://dev.crm.dynamics.com");
        var profile2 = TempProfileStore.CreateTestProfile("profile2", environmentUrl: "https://dev.crm.dynamics.com");
        await _tempStore.SeedProfilesAsync("profile1", profile1, profile2);

        await using var session = new InteractiveSession(null, _tempStore.Store, _mockFactory);

        await session.GetServiceProviderAsync("https://dev.crm.dynamics.com");
        await session.GetServiceProviderAsync("https://prod.crm.dynamics.com");

        await session.SetActiveProfileAsync("profile2", "https://dev.crm.dynamics.com");

        // Give time for fire-and-forget re-warm
        await Task.Delay(200);

        // Both should have been invalidated since credentials changed.
        // SetActiveProfile invalidates all, then re-warms the target environment.
        // So we should see: 2 original + at least 1 re-warm = 3+
        Assert.True(_mockFactory.CreationLog.Count >= 3);
    }

    [Fact]
    public async Task SetEnvironment_DoesNotInvalidateOtherProviders()
    {
        var profile = TempProfileStore.CreateTestProfile("TestProfile", environmentUrl: "https://dev.crm.dynamics.com");
        await _tempStore.SeedProfilesAsync("TestProfile", profile);

        await using var session = new InteractiveSession(null, _tempStore.Store, _mockFactory);

        var providerDev = await session.GetServiceProviderAsync("https://dev.crm.dynamics.com");

        await session.SetEnvironmentAsync("https://prod.crm.dynamics.com", "PROD");

        // Dev provider should still be cached
        var providerDev2 = await session.GetServiceProviderAsync("https://dev.crm.dynamics.com");
        Assert.Same(providerDev, providerDev2);
    }
}

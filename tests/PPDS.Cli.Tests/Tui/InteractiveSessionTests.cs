using PPDS.Auth.Profiles;
using PPDS.Cli.Services.Environment;
using PPDS.Cli.Services.Profile;
using PPDS.Cli.Tui;
using PPDS.Cli.Tui.Infrastructure;
using Xunit;

namespace PPDS.Cli.Tests.Tui;

/// <summary>
/// Unit tests for <see cref="InteractiveSession"/>.
/// </summary>
/// <remarks>
/// These tests verify local service resolution and session lifecycle.
/// Tests for async methods would require integration tests with live credentials.
/// </remarks>
public class InteractiveSessionTests : IAsyncLifetime
{
    private ProfileStore _profileStore = null!;
    private InteractiveSession _session = null!;

    public Task InitializeAsync()
    {
        _profileStore = new ProfileStore();
        _session = new InteractiveSession(profileName: null, _profileStore);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        await _session.DisposeAsync();
    }

    #region Constructor Tests

    [Fact]
    public void Constructor_WithNullProfileName_DoesNotThrow()
    {
        using var store = new ProfileStore();
        var session = new InteractiveSession(profileName: null, store);

        Assert.NotNull(session);
    }

    [Fact]
    public void Constructor_WithProfileName_DoesNotThrow()
    {
        using var store = new ProfileStore();
        var session = new InteractiveSession(profileName: "TestProfile", store);

        Assert.NotNull(session);
    }

    [Fact]
    public void Constructor_WithNullProfileStore_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new InteractiveSession(profileName: null, profileStore: null!));
    }

    [Fact]
    public void Constructor_WithDeviceCodeCallback_DoesNotThrow()
    {
        using var store = new ProfileStore();
        var session = new InteractiveSession(
            profileName: null,
            store,
            deviceCodeCallback: info => { /* handle device code */ });

        Assert.NotNull(session);
    }

    #endregion

    #region Local Service Tests

    [Fact]
    public void GetProfileService_ReturnsNonNullService()
    {
        var service = _session.GetProfileService();

        Assert.NotNull(service);
        Assert.IsAssignableFrom<IProfileService>(service);
    }

    [Fact]
    public void GetProfileService_MultipleCallsReturnSameInstance()
    {
        var service1 = _session.GetProfileService();
        var service2 = _session.GetProfileService();

        // Lazy<T> ensures thread-safe singleton per session
        Assert.Same(service1, service2);
    }

    [Fact]
    public void GetEnvironmentService_ReturnsNonNullService()
    {
        var service = _session.GetEnvironmentService();

        Assert.NotNull(service);
        Assert.IsAssignableFrom<IEnvironmentService>(service);
    }

    [Fact]
    public void GetEnvironmentService_MultipleCallsReturnSameInstance()
    {
        var service1 = _session.GetEnvironmentService();
        var service2 = _session.GetEnvironmentService();

        // Lazy<T> ensures thread-safe singleton per session
        Assert.Same(service1, service2);
    }

    [Fact]
    public void GetProfileStore_ReturnsSameInstancePassedToConstructor()
    {
        var returnedStore = _session.GetProfileStore();

        Assert.Same(_profileStore, returnedStore);
    }

    [Fact]
    public void GetThemeService_ReturnsNonNullService()
    {
        var service = _session.GetThemeService();

        Assert.NotNull(service);
        Assert.IsAssignableFrom<ITuiThemeService>(service);
    }

    [Fact]
    public void GetThemeService_ReturnsWorkingService()
    {
        var service = _session.GetThemeService();

        // Verify the service actually works
        var envType = service.DetectEnvironmentType("https://contoso.crm.dynamics.com");
        Assert.Equal(EnvironmentType.Production, envType);
    }

    #endregion

    #region Environment URL Tests

    [Fact]
    public void CurrentEnvironmentUrl_InitiallyNull()
    {
        using var store = new ProfileStore();
        var session = new InteractiveSession(profileName: null, store);

        Assert.Null(session.CurrentEnvironmentUrl);
    }

    #endregion

    #region Invalidate Tests

    [Fact]
    public async Task InvalidateAsync_WhenNoProvider_DoesNotThrow()
    {
        // Should not throw when there's no active provider
        await _session.InvalidateAsync();
    }

    [Fact]
    public async Task InvalidateAsync_MultipleCallsDoNotThrow()
    {
        // Multiple invalidate calls should be safe
        await _session.InvalidateAsync();
        await _session.InvalidateAsync();
        await _session.InvalidateAsync();
    }

    #endregion

    #region Dispose Tests

    [Fact]
    public async Task DisposeAsync_MultipleCallsDoNotThrow()
    {
        using var store = new ProfileStore();
        var session = new InteractiveSession(profileName: null, store);

        // Multiple dispose calls should be safe
        await session.DisposeAsync();
        await session.DisposeAsync();
        await session.DisposeAsync();
    }

    [Fact]
    public async Task DisposeAsync_LocalServicesStillWork()
    {
        using var store = new ProfileStore();
        var session = new InteractiveSession(profileName: null, store);

        await session.DisposeAsync();

        // Local services should still work after dispose since they use shared ProfileStore
        var profileService = session.GetProfileService();
        Assert.NotNull(profileService);

        var themeService = session.GetThemeService();
        Assert.NotNull(themeService);
    }

    #endregion

    #region Thread Safety Tests

    [Fact]
    public async Task ConcurrentLocalServiceCalls_DoNotThrow()
    {
        var tasks = Enumerable.Range(0, 10).Select(i => Task.Run(() =>
        {
            _ = _session.GetProfileService();
            _ = _session.GetEnvironmentService();
            _ = _session.GetThemeService();
            _ = _session.GetProfileStore();
        }));

        await Task.WhenAll(tasks);
    }

    [Fact]
    public async Task ConcurrentInvalidateCalls_DoNotThrow()
    {
        var tasks = Enumerable.Range(0, 10).Select(_ =>
            _session.InvalidateAsync());

        await Task.WhenAll(tasks);
    }

    #endregion
}

using PPDS.Auth.Profiles;
using PPDS.Cli.Tests.Mocks;
using PPDS.Cli.Tui;
using Xunit;

namespace PPDS.Cli.Tests.Tui;

/// <summary>
/// Tests for InteractiveSession lifecycle management.
/// These tests enable autonomous TUI iteration by Claude.
/// </summary>
[Trait("Category", "TuiUnit")]
public sealed class InteractiveSessionLifecycleTests : IDisposable
{
    private readonly TempProfileStore _tempStore;
    private readonly MockServiceProviderFactory _mockFactory;

    public InteractiveSessionLifecycleTests()
    {
        _tempStore = new TempProfileStore();
        _mockFactory = new MockServiceProviderFactory();
    }

    public void Dispose()
    {
        _tempStore.Dispose();
    }

    #region Initialization Tests

    [Fact]
    public async Task InitializeAsync_WithActiveProfile_SetsEnvironmentWithoutConnecting()
    {
        // Arrange
        var profile = TempProfileStore.CreateTestProfile(
            "TestProfile",
            environmentUrl: "https://test.crm.dynamics.com",
            environmentName: "Test Env");
        await _tempStore.SeedProfilesAsync("TestProfile", profile);

        var session = new InteractiveSession(
            profileName: null, // Use active
            _tempStore.Store,
            _mockFactory);

        string? eventUrl = null;
        string? eventName = null;
        session.EnvironmentChanged += (url, name) =>
        {
            eventUrl = url;
            eventName = name;
        };

        // Act
        await session.InitializeAsync();

        // Assert - Uses lazy loading: no factory call until first query
        Assert.Empty(_mockFactory.CreationLog);

        // Assert - But environment info is set and event is fired
        Assert.Equal("https://test.crm.dynamics.com", eventUrl);
        Assert.Equal("Test Env", eventName);
        Assert.Equal("Test Env", session.CurrentEnvironmentDisplayName);
    }

    [Fact]
    public async Task InitializeAsync_WithNoActiveProfile_SkipsWarm()
    {
        // Arrange - Empty profile store with no active profile
        var session = new InteractiveSession(
            profileName: null,
            _tempStore.Store,
            _mockFactory);

        // Act
        await session.InitializeAsync();

        // Assert - No provider created since there's no profile
        Assert.Empty(_mockFactory.CreationLog);
    }

    [Fact]
    public async Task InitializeAsync_WithSpecificProfile_SetsProfileWithoutConnecting()
    {
        // Arrange
        var profile1 = TempProfileStore.CreateTestProfile(
            "Profile1",
            environmentUrl: "https://env1.crm.dynamics.com",
            environmentName: "Env 1");
        var profile2 = TempProfileStore.CreateTestProfile(
            "Profile2",
            environmentUrl: "https://env2.crm.dynamics.com",
            environmentName: "Env 2");
        await _tempStore.SeedProfilesAsync("Profile1", profile1, profile2);

        var session = new InteractiveSession(
            profileName: "Profile2", // Explicit profile
            _tempStore.Store,
            _mockFactory);

        string? eventUrl = null;
        string? eventName = null;
        session.EnvironmentChanged += (url, name) =>
        {
            eventUrl = url;
            eventName = name;
        };

        // Act
        await session.InitializeAsync();

        // Assert - Uses lazy loading: no factory call until first query
        Assert.Empty(_mockFactory.CreationLog);

        // Assert - Environment event fired with Profile2's environment
        Assert.Equal("https://env2.crm.dynamics.com", eventUrl);
        Assert.Equal("Env 2", eventName);
        Assert.Equal("Profile2", session.CurrentProfileName);
    }

    #endregion

    #region Provider Lifecycle Tests

    [Fact]
    public async Task GetServiceProviderAsync_CreatesProviderOnFirstCall()
    {
        // Arrange
        var session = new InteractiveSession(null, _tempStore.Store, _mockFactory);

        // Act
        await session.GetServiceProviderAsync("https://test.crm.dynamics.com");

        // Assert
        Assert.Single(_mockFactory.CreationLog);
        Assert.Equal("https://test.crm.dynamics.com", _mockFactory.CreationLog[0].EnvironmentUrl);
    }

    [Fact]
    public async Task GetServiceProviderAsync_ReusesSameUrlProvider()
    {
        // Arrange
        var session = new InteractiveSession(null, _tempStore.Store, _mockFactory);
        const string url = "https://test.crm.dynamics.com";

        // Act - Call twice with same URL
        var provider1 = await session.GetServiceProviderAsync(url);
        var provider2 = await session.GetServiceProviderAsync(url);

        // Assert - Should reuse the same provider (only one factory call)
        Assert.Single(_mockFactory.CreationLog);
        Assert.Same(provider1, provider2);
    }

    [Fact]
    public async Task GetServiceProviderAsync_RecreatesForDifferentUrl()
    {
        // Arrange
        var session = new InteractiveSession(null, _tempStore.Store, _mockFactory);

        // Act
        await session.GetServiceProviderAsync("https://env1.crm.dynamics.com");
        await session.GetServiceProviderAsync("https://env2.crm.dynamics.com");

        // Assert - Should create two providers
        Assert.Equal(2, _mockFactory.CreationLog.Count);
        Assert.Equal("https://env1.crm.dynamics.com", _mockFactory.CreationLog[0].EnvironmentUrl);
        Assert.Equal("https://env2.crm.dynamics.com", _mockFactory.CreationLog[1].EnvironmentUrl);
    }

    #endregion

    #region Profile Switching Tests

    [Fact]
    public async Task SetActiveProfileAsync_UpdatesProfileName()
    {
        // Arrange
        var profile1 = TempProfileStore.CreateTestProfile(
            "Profile1",
            environmentUrl: "https://env1.crm.dynamics.com");
        var profile2 = TempProfileStore.CreateTestProfile(
            "Profile2",
            environmentUrl: "https://env2.crm.dynamics.com");
        await _tempStore.SeedProfilesAsync("Profile1", profile1, profile2);

        var session = new InteractiveSession(null, _tempStore.Store, _mockFactory);
        await session.InitializeAsync();
        _mockFactory.Reset();

        // Act
        await session.SetActiveProfileAsync(
            "Profile2",
            "https://env2.crm.dynamics.com",
            "Env 2");

        // Assert - New provider should use Profile2
        // The re-warm is fire-and-forget, but we can verify the profile name
        // by calling GetServiceProviderAsync explicitly
        await session.GetServiceProviderAsync("https://env2.crm.dynamics.com");
        Assert.Contains(_mockFactory.CreationLog, c => c.ProfileName == "Profile2");
    }

    [Fact]
    public async Task SetActiveProfileAsync_InvalidatesExistingProvider()
    {
        // Arrange
        var profile = TempProfileStore.CreateTestProfile(
            "TestProfile",
            environmentUrl: "https://test.crm.dynamics.com");
        await _tempStore.SeedProfilesAsync("TestProfile", profile);

        var session = new InteractiveSession(null, _tempStore.Store, _mockFactory);
        await session.GetServiceProviderAsync("https://test.crm.dynamics.com");
        _mockFactory.Reset();

        // Act - Switch to same profile (simulates credential refresh)
        await session.SetActiveProfileAsync(
            "TestProfile",
            "https://test.crm.dynamics.com");

        // Give time for fire-and-forget re-warm
        await Task.Delay(100);

        // Assert - Provider should have been recreated
        Assert.True(_mockFactory.CreationLog.Count >= 1);
    }

    [Fact]
    public async Task SetActiveProfileAsync_FiresEnvironmentChangedEvent()
    {
        // Arrange
        var profile = TempProfileStore.CreateTestProfile(
            "TestProfile",
            environmentUrl: "https://test.crm.dynamics.com");
        await _tempStore.SeedProfilesAsync("TestProfile", profile);

        var session = new InteractiveSession(null, _tempStore.Store, _mockFactory);
        string? receivedUrl = null;
        string? receivedName = null;
        session.EnvironmentChanged += (url, name) =>
        {
            receivedUrl = url;
            receivedName = name;
        };

        // Act
        await session.SetActiveProfileAsync(
            "TestProfile",
            "https://test.crm.dynamics.com",
            "Test Environment");

        // Give time for fire-and-forget to complete
        await Task.Delay(200);

        // Assert
        Assert.Equal("https://test.crm.dynamics.com", receivedUrl);
        Assert.Equal("Test Environment", receivedName);
    }

    #endregion

    #region Environment Switching Tests

    [Fact]
    public async Task SetEnvironmentAsync_UpdatesCurrentEnvironmentUrl()
    {
        // Arrange - Need an active profile for SetEnvironmentAsync to work
        var profile = TempProfileStore.CreateTestProfile(
            "TestProfile",
            environmentUrl: "https://old.crm.dynamics.com");
        await _tempStore.SeedProfilesAsync("TestProfile", profile);

        var session = new InteractiveSession(null, _tempStore.Store, _mockFactory);

        // Act
        await session.SetEnvironmentAsync(
            "https://new.crm.dynamics.com",
            "New Environment");

        // Assert
        Assert.Equal("https://new.crm.dynamics.com", session.CurrentEnvironmentUrl);
        Assert.Equal("New Environment", session.CurrentEnvironmentDisplayName);
    }

    [Fact]
    public async Task SetEnvironmentAsync_InvalidatesOldProvider()
    {
        // Arrange - Need an active profile for SetEnvironmentAsync to work
        var profile = TempProfileStore.CreateTestProfile(
            "TestProfile",
            environmentUrl: "https://old.crm.dynamics.com");
        await _tempStore.SeedProfilesAsync("TestProfile", profile);

        var session = new InteractiveSession(null, _tempStore.Store, _mockFactory);
        await session.GetServiceProviderAsync("https://old.crm.dynamics.com");
        _mockFactory.Reset();

        // Act
        await session.SetEnvironmentAsync(
            "https://new.crm.dynamics.com",
            "New Environment");

        // Wait for warm
        await Task.Delay(100);

        // Assert - Should have created new provider
        Assert.True(_mockFactory.CreationLog.Count >= 1);
        Assert.Equal("https://new.crm.dynamics.com", _mockFactory.CreationLog[0].EnvironmentUrl);
    }

    #endregion

    #region Disposal Tests

    [Fact]
    public async Task DisposeAsync_CompletesWithinTimeout()
    {
        // Arrange
        var session = new InteractiveSession(null, _tempStore.Store, _mockFactory);
        await session.GetServiceProviderAsync("https://test.crm.dynamics.com");

        // Act & Assert - Should complete within 3 seconds
        var disposeTask = session.DisposeAsync().AsTask();
        var completed = await Task.WhenAny(disposeTask, Task.Delay(TimeSpan.FromSeconds(3))) == disposeTask;
        Assert.True(completed, "DisposeAsync should complete within timeout");
    }

    [Fact]
    public async Task DisposeAsync_IsIdempotent()
    {
        // Arrange
        var session = new InteractiveSession(null, _tempStore.Store, _mockFactory);
        await session.GetServiceProviderAsync("https://test.crm.dynamics.com");

        // Act - Dispose twice
        await session.DisposeAsync();
        await session.DisposeAsync(); // Should not throw

        // Assert - If we got here without exception, the test passed
        Assert.True(true);
    }

    #endregion

    #region Error Handling Tests

    [Fact]
    public async Task GetServiceProviderAsync_WhenFactoryThrows_PropagatesException()
    {
        // Arrange
        var expectedException = new InvalidOperationException("Connection failed");
        _mockFactory.ExceptionToThrow = expectedException;

        var session = new InteractiveSession(null, _tempStore.Store, _mockFactory);

        // Act & Assert
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => session.GetServiceProviderAsync("https://test.crm.dynamics.com"));
        Assert.Same(expectedException, exception);
    }

    [Fact]
    public async Task InvalidateAsync_WhenNoProvider_DoesNotThrow()
    {
        // Arrange
        var session = new InteractiveSession(null, _tempStore.Store, _mockFactory);

        // Act & Assert - Should not throw
        await session.InvalidateAsync();
        Assert.True(true);
    }

    [Fact]
    public async Task GetServiceProviderAsync_AfterDispose_ThrowsObjectDisposedException()
    {
        // Arrange
        var session = new InteractiveSession(null, _tempStore.Store, _mockFactory);
        await session.DisposeAsync();

        // Act & Assert
        await Assert.ThrowsAsync<ObjectDisposedException>(
            () => session.GetServiceProviderAsync("https://test.crm.dynamics.com"));
    }

    #endregion
}

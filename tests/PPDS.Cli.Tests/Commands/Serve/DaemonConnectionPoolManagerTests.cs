using FluentAssertions;
using PPDS.Auth.Profiles;
using PPDS.Cli.Infrastructure;
using Xunit;

namespace PPDS.Cli.Tests.Commands.Serve;

/// <summary>
/// Tests for DaemonConnectionPoolManager.
/// Note: Pool creation tests require live ProfileStore/credentials and are in integration tests.
/// These tests focus on interface contract and disposal behavior.
/// </summary>
public class DaemonConnectionPoolManagerTests
{
    #region Construction Tests

    [Fact]
    public void Constructor_CreatesInstance_WithDefaultLogger()
    {
        // Act
        var manager = new DaemonConnectionPoolManager();

        // Assert
        manager.Should().NotBeNull();
    }

    [Fact]
    public void Constructor_AcceptsCustomProfileLoader()
    {
        // Arrange
        Func<CancellationToken, Task<ProfileCollection>> customLoader =
            ct => Task.FromResult(new ProfileCollection());

        // Act
        var manager = new DaemonConnectionPoolManager(loadProfilesAsync: customLoader);

        // Assert
        manager.Should().NotBeNull();
    }

    [Fact]
    public void Constructor_AcceptsCustomTimeout()
    {
        // Arrange
        var customTimeout = TimeSpan.FromSeconds(30);

        // Act
        var manager = new DaemonConnectionPoolManager(poolCreationTimeout: customTimeout);

        // Assert
        manager.Should().NotBeNull();
    }

    #endregion

    #region InvalidateProfile Tests

    [Fact]
    public void InvalidateProfile_DoesNotThrow_WhenNoPoolsExist()
    {
        // Arrange
        var manager = new DaemonConnectionPoolManager();

        // Act - Should not throw even when no pools exist
        var act = () => manager.InvalidateProfile("nonexistent");

        // Assert
        act.Should().NotThrow();
    }

    [Fact]
    public void InvalidateProfile_DoesNotThrow_WhenProfileNameIsEmpty()
    {
        // Arrange
        var manager = new DaemonConnectionPoolManager();

        // Act - Should handle empty/null gracefully
        var act = () => manager.InvalidateProfile("");

        // Assert
        act.Should().NotThrow();
    }

    [Fact]
    public void InvalidateProfile_DoesNotThrow_WhenProfileNameIsNull()
    {
        // Arrange
        var manager = new DaemonConnectionPoolManager();

        // Act
        var act = () => manager.InvalidateProfile(null!);

        // Assert
        act.Should().NotThrow();
    }

    #endregion

    #region InvalidateEnvironment Tests

    [Fact]
    public void InvalidateEnvironment_DoesNotThrow_WhenNoPoolsExist()
    {
        // Arrange
        var manager = new DaemonConnectionPoolManager();

        // Act
        var act = () => manager.InvalidateEnvironment("https://nonexistent.crm.dynamics.com");

        // Assert
        act.Should().NotThrow();
    }

    [Fact]
    public void InvalidateEnvironment_DoesNotThrow_WhenUrlIsEmpty()
    {
        // Arrange
        var manager = new DaemonConnectionPoolManager();

        // Act
        var act = () => manager.InvalidateEnvironment("");

        // Assert
        act.Should().NotThrow();
    }

    #endregion

    #region Disposal Tests

    [Fact]
    public async Task DisposeAsync_CanBeCalledMultipleTimes()
    {
        // Arrange
        var manager = new DaemonConnectionPoolManager();

        // Act - Should not throw on multiple disposals
        await manager.DisposeAsync();
        var act = async () => await manager.DisposeAsync();

        // Assert
        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task GetOrCreatePoolAsync_ThrowsObjectDisposedException_AfterDisposal()
    {
        // Arrange
        var manager = new DaemonConnectionPoolManager();
        await manager.DisposeAsync();

        // Act
        var act = async () => await manager.GetOrCreatePoolAsync(
            new[] { "test" },
            "https://test.crm.dynamics.com");

        // Assert
        await act.Should().ThrowAsync<ObjectDisposedException>();
    }

    #endregion

    #region Argument Validation Tests

    [Fact]
    public async Task GetOrCreatePoolAsync_ThrowsArgumentException_WhenProfileNamesEmpty()
    {
        // Arrange
        var manager = new DaemonConnectionPoolManager();

        // Act
        var act = async () => await manager.GetOrCreatePoolAsync(
            Array.Empty<string>(),
            "https://test.crm.dynamics.com");

        // Assert
        await act.Should().ThrowAsync<ArgumentException>()
            .WithMessage("*At least one profile name*");
    }

    [Fact]
    public async Task GetOrCreatePoolAsync_ThrowsArgumentException_WhenProfileNamesNull()
    {
        // Arrange
        var manager = new DaemonConnectionPoolManager();

        // Act
        var act = async () => await manager.GetOrCreatePoolAsync(
            null!,
            "https://test.crm.dynamics.com");

        // Assert
        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task GetOrCreatePoolAsync_ThrowsArgumentException_WhenEnvironmentUrlInvalid(string? url)
    {
        // Arrange
        var manager = new DaemonConnectionPoolManager();

        // Act
        var act = async () => await manager.GetOrCreatePoolAsync(
            new[] { "test" },
            url!);

        // Assert
        await act.Should().ThrowAsync<ArgumentException>()
            .WithMessage("*Environment URL*");
    }

    #endregion

    #region Profile Loader Injection Tests

    [Fact]
    public async Task GetOrCreatePoolAsync_UsesInjectedProfileLoader()
    {
        // Arrange
        var loaderCalled = false;
        Func<CancellationToken, Task<ProfileCollection>> customLoader = ct =>
        {
            loaderCalled = true;
            // Return empty collection - will fail to find profile, but proves loader was called
            return Task.FromResult(new ProfileCollection());
        };

        var manager = new DaemonConnectionPoolManager(loadProfilesAsync: customLoader);

        // Act - Will throw because profile not found, but loader should be called
        try
        {
            await manager.GetOrCreatePoolAsync(
                new[] { "nonexistent" },
                "https://test.crm.dynamics.com");
        }
        catch (InvalidOperationException)
        {
            // Expected - profile not found
        }

        // Assert
        loaderCalled.Should().BeTrue("the injected profile loader should be called");
    }

    [Fact]
    public async Task GetOrCreatePoolAsync_ProfileNotFound_ThrowsInvalidOperationException()
    {
        // Arrange
        Func<CancellationToken, Task<ProfileCollection>> customLoader =
            ct => Task.FromResult(new ProfileCollection());

        var manager = new DaemonConnectionPoolManager(loadProfilesAsync: customLoader);

        // Act
        var act = async () => await manager.GetOrCreatePoolAsync(
            new[] { "nonexistent" },
            "https://test.crm.dynamics.com");

        // Assert
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*Profile 'nonexistent' not found*");
    }

    #endregion

    #region Timeout Tests

    [Fact]
    public async Task GetOrCreatePoolAsync_TimesOut_ThrowsTimeoutException()
    {
        // Arrange - loader that never completes
        Func<CancellationToken, Task<ProfileCollection>> slowLoader = async ct =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return new ProfileCollection();
        };

        var manager = new DaemonConnectionPoolManager(
            loadProfilesAsync: slowLoader,
            poolCreationTimeout: TimeSpan.FromMilliseconds(100));

        // Act
        var act = async () => await manager.GetOrCreatePoolAsync(
            new[] { "test" },
            "https://test.crm.dynamics.com");

        // Assert
        await act.Should().ThrowAsync<TimeoutException>()
            .WithMessage("*timed out*");
    }

    [Fact]
    public async Task GetOrCreatePoolAsync_CallerCancellation_ThrowsOperationCanceledException()
    {
        // Arrange - loader that waits for cancellation
        Func<CancellationToken, Task<ProfileCollection>> slowLoader = async ct =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return new ProfileCollection();
        };

        var manager = new DaemonConnectionPoolManager(
            loadProfilesAsync: slowLoader,
            poolCreationTimeout: TimeSpan.FromMinutes(5)); // Long timeout

        using var cts = new CancellationTokenSource();
        cts.CancelAfter(TimeSpan.FromMilliseconds(50)); // Cancel quickly

        // Act
        var act = async () => await manager.GetOrCreatePoolAsync(
            new[] { "test" },
            "https://test.crm.dynamics.com",
            cancellationToken: cts.Token);

        // Assert - Should throw OperationCanceledException, not TimeoutException
        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task GetOrCreatePoolAsync_TimeoutRemovesFailedEntry_NextCallCanRetry()
    {
        // Arrange
        var callCount = 0;
        Func<CancellationToken, Task<ProfileCollection>> slowThenFastLoader = async ct =>
        {
            callCount++;
            if (callCount == 1)
            {
                // First call - slow, will timeout
                await Task.Delay(TimeSpan.FromSeconds(10), ct);
            }
            // Subsequent calls - return immediately (but still empty, so will fail on profile lookup)
            return new ProfileCollection();
        };

        var manager = new DaemonConnectionPoolManager(
            loadProfilesAsync: slowThenFastLoader,
            poolCreationTimeout: TimeSpan.FromMilliseconds(100));

        // Act - First call times out
        try
        {
            await manager.GetOrCreatePoolAsync(
                new[] { "test" },
                "https://test.crm.dynamics.com");
        }
        catch (TimeoutException)
        {
            // Expected
        }

        // Act - Second call should retry (not use cached failed entry)
        try
        {
            await manager.GetOrCreatePoolAsync(
                new[] { "test" },
                "https://test.crm.dynamics.com");
        }
        catch (InvalidOperationException)
        {
            // Expected - profile not found, but proves loader was called again
        }

        // Assert - Loader should have been called twice
        callCount.Should().Be(2, "timeout should have removed failed entry, allowing retry");
    }

    #endregion

    #region Cache Key Tests

    [Fact]
    public void GenerateCacheKey_SameProfilesReordered_ProducesSameKey()
    {
        // Act
        var key1 = DaemonConnectionPoolManager.GenerateCacheKey(new[] { "a", "b" }, "https://test.crm.dynamics.com");
        var key2 = DaemonConnectionPoolManager.GenerateCacheKey(new[] { "b", "a" }, "https://test.crm.dynamics.com");

        // Assert
        key1.Should().Be(key2, "sorted profile names should produce the same cache key");
    }

    [Fact]
    public void GenerateCacheKey_DifferentProfiles_ProducesDifferentKeys()
    {
        // Act
        var key1 = DaemonConnectionPoolManager.GenerateCacheKey(new[] { "a" }, "https://test.crm.dynamics.com");
        var key2 = DaemonConnectionPoolManager.GenerateCacheKey(new[] { "b" }, "https://test.crm.dynamics.com");

        // Assert
        key1.Should().NotBe(key2, "different profiles should produce different cache keys");
    }

    [Fact]
    public void GenerateCacheKey_DifferentEnvironments_ProducesDifferentKeys()
    {
        // Act
        var key1 = DaemonConnectionPoolManager.GenerateCacheKey(new[] { "a" }, "https://org1.crm.dynamics.com");
        var key2 = DaemonConnectionPoolManager.GenerateCacheKey(new[] { "a" }, "https://org2.crm.dynamics.com");

        // Assert
        key1.Should().NotBe(key2, "different environments should produce different cache keys");
    }

    [Fact]
    public void GenerateCacheKey_SameEnvironmentWithTrailingSlash_ProducesSameKey()
    {
        // Act
        var key1 = DaemonConnectionPoolManager.GenerateCacheKey(new[] { "a" }, "https://test.crm.dynamics.com");
        var key2 = DaemonConnectionPoolManager.GenerateCacheKey(new[] { "a" }, "https://test.crm.dynamics.com/");

        // Assert
        key1.Should().Be(key2, "URLs differing only by trailing slash should produce the same cache key");
    }

    [Fact]
    public void GenerateCacheKey_ProfilesSortedCaseInsensitively()
    {
        // Act - "b" should sort after "A" when using case-insensitive comparison
        var key1 = DaemonConnectionPoolManager.GenerateCacheKey(new[] { "b", "A" }, "https://test.crm.dynamics.com");
        var key2 = DaemonConnectionPoolManager.GenerateCacheKey(new[] { "A", "b" }, "https://test.crm.dynamics.com");

        // Assert - Both should produce same key due to case-insensitive sorting
        key1.Should().Be(key2, "profiles should be sorted case-insensitively");
        // Note: Profile names preserve their original case in the key (only sorting is case-insensitive)
    }

    [Fact]
    public void GenerateCacheKey_CaseInsensitiveUrl_ProducesSameKey()
    {
        // Act
        var key1 = DaemonConnectionPoolManager.GenerateCacheKey(new[] { "a" }, "https://TEST.crm.dynamics.com");
        var key2 = DaemonConnectionPoolManager.GenerateCacheKey(new[] { "a" }, "https://test.crm.dynamics.com");

        // Assert
        key1.Should().Be(key2, "URLs should be case-insensitive for cache key generation");
    }

    #endregion
}

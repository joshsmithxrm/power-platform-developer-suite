using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using PPDS.Auth.Profiles;
using PPDS.Mcp.Infrastructure;
using Xunit;

namespace PPDS.Mcp.Tests.Infrastructure;

/// <summary>
/// Unit tests for <see cref="McpConnectionPoolManager"/>.
/// </summary>
public sealed class McpConnectionPoolManagerTests
{
    #region GenerateCacheKey Tests

    [Fact]
    public void GenerateCacheKey_SingleProfile_GeneratesCorrectFormat()
    {
        // Arrange
        var profileNames = new[] { "myprofile" };
        var environmentUrl = "https://org.crm.dynamics.com";

        // Act
        var key = McpConnectionPoolManager.GenerateCacheKey(profileNames, environmentUrl);

        // Assert
        key.Should().Be("myprofile|https://org.crm.dynamics.com");
    }

    [Fact]
    public void GenerateCacheKey_MultipleProfiles_SortsAlphabetically()
    {
        // Arrange
        var profileNames = new[] { "zebra", "alpha", "mango" };
        var environmentUrl = "https://org.crm.dynamics.com";

        // Act
        var key = McpConnectionPoolManager.GenerateCacheKey(profileNames, environmentUrl);

        // Assert
        key.Should().Be("alpha,mango,zebra|https://org.crm.dynamics.com");
    }

    [Fact]
    public void GenerateCacheKey_NormalizesUrl_RemovesTrailingSlash()
    {
        // Arrange
        var profileNames = new[] { "profile" };
        var environmentUrl = "https://org.crm.dynamics.com/";

        // Act
        var key = McpConnectionPoolManager.GenerateCacheKey(profileNames, environmentUrl);

        // Assert
        key.Should().Be("profile|https://org.crm.dynamics.com");
    }

    [Fact]
    public void GenerateCacheKey_NormalizesUrl_LowerCases()
    {
        // Arrange
        var profileNames = new[] { "profile" };
        var environmentUrl = "HTTPS://ORG.CRM.DYNAMICS.COM/";

        // Act
        var key = McpConnectionPoolManager.GenerateCacheKey(profileNames, environmentUrl);

        // Assert
        key.Should().Be("profile|https://org.crm.dynamics.com");
    }

    [Fact]
    public void GenerateCacheKey_PreservesProfileNameCase()
    {
        // Arrange - profile names preserve case in key but sort case-insensitively
        var profileNames = new[] { "ZebraProfile", "AlphaProfile" };
        var environmentUrl = "https://org.crm.dynamics.com";

        // Act
        var key = McpConnectionPoolManager.GenerateCacheKey(profileNames, environmentUrl);

        // Assert - sorted case-insensitively (Alpha before Zebra)
        key.Should().Be("AlphaProfile,ZebraProfile|https://org.crm.dynamics.com");
    }

    [Fact]
    public void GenerateCacheKey_SameProfilesDifferentOrder_ProducesSameKey()
    {
        // Arrange
        var profileNames1 = new[] { "profile1", "profile2", "profile3" };
        var profileNames2 = new[] { "profile3", "profile1", "profile2" };
        var environmentUrl = "https://org.crm.dynamics.com";

        // Act
        var key1 = McpConnectionPoolManager.GenerateCacheKey(profileNames1, environmentUrl);
        var key2 = McpConnectionPoolManager.GenerateCacheKey(profileNames2, environmentUrl);

        // Assert - same profiles in any order should produce same cache key
        key1.Should().Be(key2);
    }

    #endregion

    #region GetOrCreatePoolAsync Validation Tests

    [Fact]
    public async Task GetOrCreatePoolAsync_NullProfileNames_ThrowsArgumentException()
    {
        // Arrange
        await using var manager = new McpConnectionPoolManager();

        // Act
        Func<Task> act = () => manager.GetOrCreatePoolAsync(null!, "https://org.crm.dynamics.com");

        // Assert
        await act.Should().ThrowAsync<ArgumentException>()
            .WithParameterName("profileNames");
    }

    [Fact]
    public async Task GetOrCreatePoolAsync_EmptyProfileNames_ThrowsArgumentException()
    {
        // Arrange
        await using var manager = new McpConnectionPoolManager();

        // Act
        Func<Task> act = () => manager.GetOrCreatePoolAsync(
            Array.Empty<string>(),
            "https://org.crm.dynamics.com");

        // Assert
        await act.Should().ThrowAsync<ArgumentException>()
            .WithParameterName("profileNames")
            .WithMessage("*At least one profile name is required*");
    }

    [Fact]
    public async Task GetOrCreatePoolAsync_NullEnvironmentUrl_ThrowsArgumentException()
    {
        // Arrange
        await using var manager = new McpConnectionPoolManager();

        // Act
        Func<Task> act = () => manager.GetOrCreatePoolAsync(new[] { "profile" }, null!);

        // Assert
        await act.Should().ThrowAsync<ArgumentException>()
            .WithParameterName("environmentUrl");
    }

    [Fact]
    public async Task GetOrCreatePoolAsync_EmptyEnvironmentUrl_ThrowsArgumentException()
    {
        // Arrange
        await using var manager = new McpConnectionPoolManager();

        // Act
        Func<Task> act = () => manager.GetOrCreatePoolAsync(new[] { "profile" }, "   ");

        // Assert
        await act.Should().ThrowAsync<ArgumentException>()
            .WithParameterName("environmentUrl")
            .WithMessage("*Environment URL is required*");
    }

    [Fact]
    public async Task GetOrCreatePoolAsync_AfterDisposal_ThrowsObjectDisposedException()
    {
        // Arrange
        var manager = new McpConnectionPoolManager();
        await manager.DisposeAsync();

        // Act
        Func<Task> act = () => manager.GetOrCreatePoolAsync(
            new[] { "profile" },
            "https://org.crm.dynamics.com");

        // Assert
        await act.Should().ThrowAsync<ObjectDisposedException>();
    }

    #endregion

    #region InvalidateProfile Tests

    [Fact]
    public async Task InvalidateProfile_AfterDisposal_ThrowsObjectDisposedException()
    {
        // Arrange
        var manager = new McpConnectionPoolManager();
        await manager.DisposeAsync();

        // Act
        var act = () => manager.InvalidateProfile("profile");

        // Assert
        act.Should().Throw<ObjectDisposedException>();
    }

    [Fact]
    public async Task InvalidateProfile_NullOrWhitespace_DoesNotThrow()
    {
        // Arrange
        await using var manager = new McpConnectionPoolManager();

        // Act & Assert - should not throw
        manager.InvalidateProfile(null!);
        manager.InvalidateProfile("");
        manager.InvalidateProfile("   ");
    }

    #endregion

    #region InvalidateEnvironment Tests

    [Fact]
    public async Task InvalidateEnvironment_AfterDisposal_ThrowsObjectDisposedException()
    {
        // Arrange
        var manager = new McpConnectionPoolManager();
        await manager.DisposeAsync();

        // Act
        var act = () => manager.InvalidateEnvironment("https://org.crm.dynamics.com");

        // Assert
        act.Should().Throw<ObjectDisposedException>();
    }

    [Fact]
    public async Task InvalidateEnvironment_NullOrWhitespace_DoesNotThrow()
    {
        // Arrange
        await using var manager = new McpConnectionPoolManager();

        // Act & Assert - should not throw
        manager.InvalidateEnvironment(null!);
        manager.InvalidateEnvironment("");
        manager.InvalidateEnvironment("   ");
    }

    #endregion

    #region Disposal Tests

    [Fact]
    public async Task DisposeAsync_MultipleCalls_DoesNotThrow()
    {
        // Arrange
        var manager = new McpConnectionPoolManager();

        // Act & Assert - multiple disposals should not throw
        await manager.DisposeAsync();
        await manager.DisposeAsync();
        await manager.DisposeAsync();
    }

    #endregion

    #region Pool Creation with Mock Profile Loader

    [Fact]
    public async Task GetOrCreatePoolAsync_ProfileNotFound_ThrowsInvalidOperationException()
    {
        // Arrange - create manager with mock profile loader that returns empty collection
        var emptyCollection = new ProfileCollection();
        await using var manager = new McpConnectionPoolManager(
            loggerFactory: NullLoggerFactory.Instance,
            loadProfilesAsync: _ => Task.FromResult(emptyCollection));

        // Act
        Func<Task> act = () => manager.GetOrCreatePoolAsync(
            new[] { "nonexistent" },
            "https://org.crm.dynamics.com");

        // Assert
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*Profile 'nonexistent' not found*");
    }

    [Fact]
    public async Task GetOrCreatePoolAsync_Timeout_ThrowsTimeoutException()
    {
        // Arrange - create manager with a profile loader that hangs forever
        var hangingLoader = new TaskCompletionSource<ProfileCollection>();

        await using var manager = new McpConnectionPoolManager(
            loggerFactory: NullLoggerFactory.Instance,
            loadProfilesAsync: _ => hangingLoader.Task,
            poolCreationTimeout: TimeSpan.FromMilliseconds(50)); // Very short timeout

        // Act
        Func<Task> act = () => manager.GetOrCreatePoolAsync(
            new[] { "profile" },
            "https://org.crm.dynamics.com");

        // Assert
        await act.Should().ThrowAsync<TimeoutException>()
            .WithMessage("*Pool creation timed out*");
    }

    [Fact]
    public void GenerateCacheKey_SameProfilesDifferentOrder_ProducesSameKeyForCaching()
    {
        // Arrange - verify that caching works correctly by checking key generation
        // Full pool creation/reuse testing is done in PPDS.LiveTests.
        var profileNames1 = new[] { "alpha", "beta" };
        var profileNames2 = new[] { "beta", "alpha" }; // Same profiles, different order
        var url = "https://org.crm.dynamics.com";

        // Act
        var key1 = McpConnectionPoolManager.GenerateCacheKey(profileNames1, url);
        var key2 = McpConnectionPoolManager.GenerateCacheKey(profileNames2, url);

        // Assert - keys should match, proving same pool would be returned
        key1.Should().Be(key2);
    }

    #endregion
}

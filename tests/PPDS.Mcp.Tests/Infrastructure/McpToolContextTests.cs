using FluentAssertions;
using Moq;
using PPDS.Auth.Credentials;
using PPDS.Auth.Profiles;
using PPDS.Dataverse.Pooling;
using PPDS.Mcp.Infrastructure;
using Xunit;

namespace PPDS.Mcp.Tests.Infrastructure;

/// <summary>
/// Unit tests for <see cref="McpToolContext"/>.
/// </summary>
public sealed class McpToolContextTests
{
    #region Constructor Tests

    [Fact]
    public void Constructor_NullPoolManager_ThrowsArgumentNullException()
    {
        // Act
        var act = () => new McpToolContext(null!, new ProfileStore(), new Mock<ISecureCredentialStore>().Object, new McpSessionOptions());

        // Assert
        act.Should().Throw<ArgumentNullException>()
            .WithParameterName("poolManager");
    }

    [Fact]
    public void Constructor_NullProfileStore_ThrowsArgumentNullException()
    {
        // Act
        var act = () => new McpToolContext(
            new Mock<IMcpConnectionPoolManager>().Object,
            null!,
            new Mock<ISecureCredentialStore>().Object,
            new McpSessionOptions());

        // Assert
        act.Should().Throw<ArgumentNullException>()
            .WithParameterName("profileStore");
    }

    [Fact]
    public void Constructor_NullCredentialStore_ThrowsArgumentNullException()
    {
        // Act
        var act = () => new McpToolContext(
            new Mock<IMcpConnectionPoolManager>().Object,
            new ProfileStore(),
            null!,
            new McpSessionOptions());

        // Assert
        act.Should().Throw<ArgumentNullException>()
            .WithParameterName("credentialStore");
    }

    [Fact]
    public void Constructor_WithPoolManager_Succeeds()
    {
        // Arrange
        var mockPoolManager = new Mock<IMcpConnectionPoolManager>();

        // Act
        var context = new McpToolContext(
            mockPoolManager.Object,
            new ProfileStore(),
            new Mock<ISecureCredentialStore>().Object,
            new McpSessionOptions());

        // Assert
        context.Should().NotBeNull();
    }

    [Fact]
    public void Constructor_NullLoggerFactory_UsesNullLoggerFactory()
    {
        // Arrange
        var mockPoolManager = new Mock<IMcpConnectionPoolManager>();

        // Act - should not throw even with null logger factory
        var context = new McpToolContext(
            mockPoolManager.Object,
            new ProfileStore(),
            new Mock<ISecureCredentialStore>().Object,
            new McpSessionOptions(),
            loggerFactory: null);

        // Assert
        context.Should().NotBeNull();
    }

    #endregion

    #region InvalidateEnvironment Tests

    [Fact]
    public void InvalidateEnvironment_DelegatesToPoolManager()
    {
        // Arrange
        var mockPoolManager = new Mock<IMcpConnectionPoolManager>();
        var context = new McpToolContext(
            mockPoolManager.Object,
            new ProfileStore(),
            new Mock<ISecureCredentialStore>().Object,
            new McpSessionOptions());
        var environmentUrl = "https://org.crm.dynamics.com";

        // Act
        context.InvalidateEnvironment(environmentUrl);

        // Assert
        mockPoolManager.Verify(
            m => m.InvalidateEnvironment(environmentUrl),
            Times.Once);
    }

    [Fact]
    public void InvalidateEnvironment_PassesExactUrl()
    {
        // Arrange
        var mockPoolManager = new Mock<IMcpConnectionPoolManager>();
        var context = new McpToolContext(
            mockPoolManager.Object,
            new ProfileStore(),
            new Mock<ISecureCredentialStore>().Object,
            new McpSessionOptions());
        var environmentUrl = "https://org.crm.dynamics.com/with/trailing/slash/";

        // Act
        context.InvalidateEnvironment(environmentUrl);

        // Assert - URL should be passed exactly as received
        mockPoolManager.Verify(
            m => m.InvalidateEnvironment("https://org.crm.dynamics.com/with/trailing/slash/"),
            Times.Once);
    }

    #endregion

    #region GetActiveProfileAsync Tests

    [Fact(Skip = "Requires no profiles.json to exist - tested via integration tests")]
    public async Task GetActiveProfileAsync_NoProfilesFile_ThrowsInvalidOperationException()
    {
        // This test requires no ~/.ppds/profiles.json to exist.
        // In development environments with profiles, this test is skipped.
        // Coverage provided by PPDS.LiveTests when run in clean CI environment.
        var mockPoolManager = new Mock<IMcpConnectionPoolManager>();
        var context = new McpToolContext(
            mockPoolManager.Object,
            new ProfileStore(),
            new Mock<ISecureCredentialStore>().Object,
            new McpSessionOptions());

        // Act
        Func<Task> act = () => context.GetActiveProfileAsync();

        // Assert - should throw because no active profile
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*No active profile*");
    }

    #endregion

    #region GetPoolAsync Tests

    [Fact(Skip = "Requires no profiles.json to exist - tested via integration tests")]
    public async Task GetPoolAsync_NoActiveProfile_ThrowsInvalidOperationException()
    {
        // This test requires no ~/.ppds/profiles.json to exist.
        // In development environments with profiles, this test is skipped.
        var mockPoolManager = new Mock<IMcpConnectionPoolManager>();
        var context = new McpToolContext(
            mockPoolManager.Object,
            new ProfileStore(),
            new Mock<ISecureCredentialStore>().Object,
            new McpSessionOptions());

        // Act
        Func<Task> act = () => context.GetPoolAsync();

        // Assert
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*No active profile*");
    }

    #endregion

    #region Session Options Tests

    [Fact]
    public void IsReadOnly_DefaultOptions_ReturnsFalse()
    {
        // Arrange
        var context = new McpToolContext(
            new Mock<IMcpConnectionPoolManager>().Object,
            new ProfileStore(),
            new Mock<ISecureCredentialStore>().Object,
            new McpSessionOptions());

        // Act & Assert
        context.IsReadOnly.Should().BeFalse();
    }

    [Fact]
    public void IsReadOnly_ReadOnlyOptionTrue_ReturnsTrue()
    {
        // Arrange
        var options = new McpSessionOptions { ReadOnly = true };
        var context = new McpToolContext(
            new Mock<IMcpConnectionPoolManager>().Object,
            new ProfileStore(),
            new Mock<ISecureCredentialStore>().Object,
            options);

        // Act & Assert
        context.IsReadOnly.Should().BeTrue();
    }

    [Fact]
    public void ValidateEnvironmentSwitch_NoAllowlistConfigured_ThrowsInvalidOperationException()
    {
        // Arrange - empty allowlist means switching is disabled
        var context = new McpToolContext(
            new Mock<IMcpConnectionPoolManager>().Object,
            new ProfileStore(),
            new Mock<ISecureCredentialStore>().Object,
            new McpSessionOptions());

        // Act
        var act = () => context.ValidateEnvironmentSwitch("https://org.crm.dynamics.com");

        // Assert
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*Environment switching is disabled*");
    }

    [Fact]
    public void ValidateEnvironmentSwitch_UrlNotInAllowlist_ThrowsInvalidOperationException()
    {
        // Arrange
        var options = new McpSessionOptions
        {
            AllowedEnvironments = new List<string>
            {
                "https://dev.crm.dynamics.com",
                "https://test.crm.dynamics.com"
            }
        };
        var context = new McpToolContext(
            new Mock<IMcpConnectionPoolManager>().Object,
            new ProfileStore(),
            new Mock<ISecureCredentialStore>().Object,
            options);

        // Act
        var act = () => context.ValidateEnvironmentSwitch("https://prod.crm.dynamics.com");

        // Assert
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*not in the allowed list*");
    }

    [Fact]
    public void ValidateEnvironmentSwitch_UrlInAllowlist_Succeeds()
    {
        // Arrange
        var options = new McpSessionOptions
        {
            AllowedEnvironments = new List<string>
            {
                "https://dev.crm.dynamics.com",
                "https://test.crm.dynamics.com"
            }
        };
        var context = new McpToolContext(
            new Mock<IMcpConnectionPoolManager>().Object,
            new ProfileStore(),
            new Mock<ISecureCredentialStore>().Object,
            options);

        // Act
        var act = () => context.ValidateEnvironmentSwitch("https://dev.crm.dynamics.com");

        // Assert - should not throw
        act.Should().NotThrow();
    }

    [Fact]
    public void EnvironmentUrlOverride_DefaultOptions_ReturnsNull()
    {
        // Arrange
        var context = new McpToolContext(
            new Mock<IMcpConnectionPoolManager>().Object,
            new ProfileStore(),
            new Mock<ISecureCredentialStore>().Object,
            new McpSessionOptions());

        // Act & Assert
        context.EnvironmentUrlOverride.Should().BeNull();
    }

    [Fact]
    public void EnvironmentUrlOverride_WithEnvironmentConfigured_ReturnsUrl()
    {
        // Arrange
        var options = new McpSessionOptions
        {
            Environment = "https://prod.crm.dynamics.com"
        };
        var context = new McpToolContext(
            new Mock<IMcpConnectionPoolManager>().Object,
            new ProfileStore(),
            new Mock<ISecureCredentialStore>().Object,
            options);

        // Act & Assert
        context.EnvironmentUrlOverride.Should().Be("https://prod.crm.dynamics.com");
    }

    #endregion

    #region CreateServiceProviderAsync Tests

    [Fact(Skip = "Requires no profiles.json to exist - tested via integration tests")]
    public async Task CreateServiceProviderAsync_NoActiveProfile_ThrowsInvalidOperationException()
    {
        // This test requires no ~/.ppds/profiles.json to exist.
        // In development environments with profiles, this test is skipped.
        var mockPoolManager = new Mock<IMcpConnectionPoolManager>();
        var context = new McpToolContext(
            mockPoolManager.Object,
            new ProfileStore(),
            new Mock<ISecureCredentialStore>().Object,
            new McpSessionOptions());

        // Act
        Func<Task> act = () => context.CreateServiceProviderAsync();

        // Assert
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*No active profile*");
    }

    #endregion
}

using FluentAssertions;
using Moq;
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
        var act = () => new McpToolContext(null!);

        // Assert
        act.Should().Throw<ArgumentNullException>()
            .WithParameterName("poolManager");
    }

    [Fact]
    public void Constructor_WithPoolManager_Succeeds()
    {
        // Arrange
        var mockPoolManager = new Mock<IMcpConnectionPoolManager>();

        // Act
        var context = new McpToolContext(mockPoolManager.Object);

        // Assert
        context.Should().NotBeNull();
    }

    [Fact]
    public void Constructor_NullLoggerFactory_UsesNullLoggerFactory()
    {
        // Arrange
        var mockPoolManager = new Mock<IMcpConnectionPoolManager>();

        // Act - should not throw even with null logger factory
        var context = new McpToolContext(mockPoolManager.Object, loggerFactory: null);

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
        var context = new McpToolContext(mockPoolManager.Object);
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
        var context = new McpToolContext(mockPoolManager.Object);
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
        var context = new McpToolContext(mockPoolManager.Object);

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
        var context = new McpToolContext(mockPoolManager.Object);

        // Act
        Func<Task> act = () => context.GetPoolAsync();

        // Assert
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*No active profile*");
    }

    #endregion

    #region CreateServiceProviderAsync Tests

    [Fact(Skip = "Requires no profiles.json to exist - tested via integration tests")]
    public async Task CreateServiceProviderAsync_NoActiveProfile_ThrowsInvalidOperationException()
    {
        // This test requires no ~/.ppds/profiles.json to exist.
        // In development environments with profiles, this test is skipped.
        var mockPoolManager = new Mock<IMcpConnectionPoolManager>();
        var context = new McpToolContext(mockPoolManager.Object);

        // Act
        Func<Task> act = () => context.CreateServiceProviderAsync();

        // Assert
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*No active profile*");
    }

    #endregion
}

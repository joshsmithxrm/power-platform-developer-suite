using FluentAssertions;
using Moq;
using PPDS.Auth.Credentials;
using PPDS.Auth.Profiles;
using PPDS.Mcp.Infrastructure;
using PPDS.Mcp.Tools;
using Xunit;

namespace PPDS.Mcp.Tests.Tools;

/// <summary>
/// Unit tests for <see cref="WebResourcesPublishTool"/>.
/// </summary>
[Trait("Category", "Unit")]
public sealed class WebResourcesPublishToolTests
{
    #region Constructor Tests

    [Fact]
    public void Constructor_NullContext_ThrowsArgumentNullException()
    {
        // Act
        var act = () => new WebResourcesPublishTool(null!);

        // Assert
        act.Should().Throw<ArgumentNullException>()
            .WithParameterName("context");
    }

    #endregion

    #region Parameter Validation Tests

    [Fact]
    public async Task ExecuteAsync_NullIds_ThrowsArgumentException()
    {
        // Arrange
        var mockPoolManager = new Mock<IMcpConnectionPoolManager>();
        var context = new McpToolContext(
            mockPoolManager.Object,
            new ProfileStore(),
            new Mock<ISecureCredentialStore>().Object,
            new McpSessionOptions());
        var tool = new WebResourcesPublishTool(context);

        // Act
        Func<Task> act = () => tool.ExecuteAsync(null!);

        // Assert
        await act.Should().ThrowAsync<ArgumentException>()
            .WithMessage("*At least one web resource ID is required*");
    }

    [Fact]
    public async Task ExecuteAsync_EmptyIds_ThrowsArgumentException()
    {
        // Arrange
        var mockPoolManager = new Mock<IMcpConnectionPoolManager>();
        var context = new McpToolContext(
            mockPoolManager.Object,
            new ProfileStore(),
            new Mock<ISecureCredentialStore>().Object,
            new McpSessionOptions());
        var tool = new WebResourcesPublishTool(context);

        // Act
        Func<Task> act = () => tool.ExecuteAsync([]);

        // Assert
        await act.Should().ThrowAsync<ArgumentException>()
            .WithMessage("*At least one web resource ID is required*");
    }

    #endregion
}

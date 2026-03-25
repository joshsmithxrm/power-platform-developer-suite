using FluentAssertions;
using Moq;
using PPDS.Auth.Credentials;
using PPDS.Auth.Profiles;
using PPDS.Mcp.Infrastructure;
using PPDS.Mcp.Tools;
using Xunit;

namespace PPDS.Mcp.Tests.Tools;

/// <summary>
/// Unit tests for <see cref="SolutionsComponentsTool"/>.
/// </summary>
[Trait("Category", "Unit")]
public sealed class SolutionsComponentsToolTests
{
    #region Constructor Tests

    [Fact]
    public void Constructor_NullContext_ThrowsArgumentNullException()
    {
        // Act
        var act = () => new SolutionsComponentsTool(null!);

        // Assert
        act.Should().Throw<ArgumentNullException>()
            .WithParameterName("context");
    }

    #endregion

    #region Parameter Validation Tests

    [Fact]
    public async Task ExecuteAsync_NullSolutionId_ThrowsArgumentException()
    {
        // Arrange
        var mockPoolManager = new Mock<IMcpConnectionPoolManager>();
        var context = new McpToolContext(
            mockPoolManager.Object,
            new ProfileStore(),
            new Mock<ISecureCredentialStore>().Object,
            new McpSessionOptions());
        var tool = new SolutionsComponentsTool(context);

        // Act
        Func<Task> act = () => tool.ExecuteAsync(null!);

        // Assert
        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task ExecuteAsync_InvalidGuidSolutionId_ThrowsArgumentException()
    {
        // Arrange
        var mockPoolManager = new Mock<IMcpConnectionPoolManager>();
        var context = new McpToolContext(
            mockPoolManager.Object,
            new ProfileStore(),
            new Mock<ISecureCredentialStore>().Object,
            new McpSessionOptions());
        var tool = new SolutionsComponentsTool(context);

        // Act
        Func<Task> act = () => tool.ExecuteAsync("not-a-guid");

        // Assert
        await act.Should().ThrowAsync<ArgumentException>()
            .WithMessage("*Invalid solution ID*");
    }

    #endregion
}

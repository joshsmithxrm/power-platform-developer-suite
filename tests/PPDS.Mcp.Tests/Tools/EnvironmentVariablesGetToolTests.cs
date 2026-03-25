using FluentAssertions;
using Moq;
using PPDS.Auth.Credentials;
using PPDS.Auth.Profiles;
using PPDS.Mcp.Infrastructure;
using PPDS.Mcp.Tools;
using Xunit;

namespace PPDS.Mcp.Tests.Tools;

/// <summary>
/// Unit tests for <see cref="EnvironmentVariablesGetTool"/>.
/// </summary>
[Trait("Category", "Unit")]
public sealed class EnvironmentVariablesGetToolTests
{
    #region Constructor Tests

    [Fact]
    public void Constructor_NullContext_ThrowsArgumentNullException()
    {
        // Act
        var act = () => new EnvironmentVariablesGetTool(null!);

        // Assert
        act.Should().Throw<ArgumentNullException>()
            .WithParameterName("context");
    }

    #endregion

    #region Parameter Validation Tests

    [Fact]
    public async Task ExecuteAsync_NullSchemaName_ThrowsArgumentException()
    {
        // Arrange
        var mockPoolManager = new Mock<IMcpConnectionPoolManager>();
        var context = new McpToolContext(
            mockPoolManager.Object,
            new ProfileStore(),
            new Mock<ISecureCredentialStore>().Object,
            new McpSessionOptions());
        var tool = new EnvironmentVariablesGetTool(context);

        // Act
        Func<Task> act = () => tool.ExecuteAsync(null!);

        // Assert
        await act.Should().ThrowAsync<ArgumentException>()
            .WithParameterName("schemaName");
    }

    [Fact]
    public async Task ExecuteAsync_EmptySchemaName_ThrowsArgumentException()
    {
        // Arrange
        var mockPoolManager = new Mock<IMcpConnectionPoolManager>();
        var context = new McpToolContext(
            mockPoolManager.Object,
            new ProfileStore(),
            new Mock<ISecureCredentialStore>().Object,
            new McpSessionOptions());
        var tool = new EnvironmentVariablesGetTool(context);

        // Act
        Func<Task> act = () => tool.ExecuteAsync("");

        // Assert
        await act.Should().ThrowAsync<ArgumentException>()
            .WithParameterName("schemaName");
    }

    [Fact]
    public async Task ExecuteAsync_WhitespaceSchemaName_ThrowsArgumentException()
    {
        // Arrange
        var mockPoolManager = new Mock<IMcpConnectionPoolManager>();
        var context = new McpToolContext(
            mockPoolManager.Object,
            new ProfileStore(),
            new Mock<ISecureCredentialStore>().Object,
            new McpSessionOptions());
        var tool = new EnvironmentVariablesGetTool(context);

        // Act
        Func<Task> act = () => tool.ExecuteAsync("   ");

        // Assert
        await act.Should().ThrowAsync<ArgumentException>()
            .WithParameterName("schemaName");
    }

    #endregion
}

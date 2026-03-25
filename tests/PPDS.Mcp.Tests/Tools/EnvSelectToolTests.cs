using FluentAssertions;
using Moq;
using PPDS.Auth.Credentials;
using PPDS.Auth.Profiles;
using PPDS.Mcp.Infrastructure;
using PPDS.Mcp.Tools;
using Xunit;

namespace PPDS.Mcp.Tests.Tools;

/// <summary>
/// Unit tests for <see cref="EnvSelectTool"/>.
/// </summary>
[Trait("Category", "Unit")]
public sealed class EnvSelectToolTests
{
    #region Constructor Tests

    [Fact]
    public void Constructor_NullContext_ThrowsArgumentNullException()
    {
        // Act
        var act = () => new EnvSelectTool(null!);

        // Assert
        act.Should().Throw<ArgumentNullException>()
            .WithParameterName("context");
    }

    #endregion

    #region Parameter Validation Tests

    [Fact]
    public async Task ExecuteAsync_NullEnvironment_ThrowsArgumentException()
    {
        // Arrange
        var context = CreateContext();
        var tool = new EnvSelectTool(context);

        // Act
        Func<Task> act = () => tool.ExecuteAsync(null!);

        // Assert
        await act.Should().ThrowAsync<ArgumentException>()
            .WithParameterName("environment");
    }

    [Fact]
    public async Task ExecuteAsync_EmptyEnvironment_ThrowsArgumentException()
    {
        // Arrange
        var context = CreateContext();
        var tool = new EnvSelectTool(context);

        // Act
        Func<Task> act = () => tool.ExecuteAsync("");

        // Assert
        await act.Should().ThrowAsync<ArgumentException>()
            .WithParameterName("environment");
    }

    [Fact]
    public async Task ExecuteAsync_WhitespaceEnvironment_ThrowsArgumentException()
    {
        // Arrange
        var context = CreateContext();
        var tool = new EnvSelectTool(context);

        // Act
        Func<Task> act = () => tool.ExecuteAsync("   ");

        // Assert
        await act.Should().ThrowAsync<ArgumentException>()
            .WithParameterName("environment");
    }

    #endregion

    private static McpToolContext CreateContext()
    {
        return new McpToolContext(
            new Mock<IMcpConnectionPoolManager>().Object,
            new ProfileStore(),
            new Mock<ISecureCredentialStore>().Object,
            new McpSessionOptions());
    }
}

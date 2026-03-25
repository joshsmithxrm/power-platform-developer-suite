using FluentAssertions;
using Moq;
using PPDS.Auth.Credentials;
using PPDS.Auth.Profiles;
using PPDS.Mcp.Infrastructure;
using PPDS.Mcp.Tools;
using Xunit;

namespace PPDS.Mcp.Tests.Tools;

/// <summary>
/// Unit tests for <see cref="ConnectionReferencesGetTool"/>.
/// </summary>
[Trait("Category", "Unit")]
public sealed class ConnectionReferencesGetToolTests
{
    #region Constructor Tests

    [Fact]
    public void Constructor_NullContext_ThrowsArgumentNullException()
    {
        // Act
        var act = () => new ConnectionReferencesGetTool(null!);

        // Assert
        act.Should().Throw<ArgumentNullException>()
            .WithParameterName("context");
    }

    #endregion

    #region Parameter Validation Tests

    [Fact]
    public async Task ExecuteAsync_NullLogicalName_ThrowsArgumentException()
    {
        // Arrange
        var context = CreateContext();
        var tool = new ConnectionReferencesGetTool(context);

        // Act
        Func<Task> act = () => tool.ExecuteAsync(null!);

        // Assert
        await act.Should().ThrowAsync<ArgumentException>()
            .WithParameterName("logicalName");
    }

    [Fact]
    public async Task ExecuteAsync_EmptyLogicalName_ThrowsArgumentException()
    {
        // Arrange
        var context = CreateContext();
        var tool = new ConnectionReferencesGetTool(context);

        // Act
        Func<Task> act = () => tool.ExecuteAsync("");

        // Assert
        await act.Should().ThrowAsync<ArgumentException>()
            .WithParameterName("logicalName");
    }

    [Fact]
    public async Task ExecuteAsync_WhitespaceLogicalName_ThrowsArgumentException()
    {
        // Arrange
        var context = CreateContext();
        var tool = new ConnectionReferencesGetTool(context);

        // Act
        Func<Task> act = () => tool.ExecuteAsync("   ");

        // Assert
        await act.Should().ThrowAsync<ArgumentException>()
            .WithParameterName("logicalName");
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

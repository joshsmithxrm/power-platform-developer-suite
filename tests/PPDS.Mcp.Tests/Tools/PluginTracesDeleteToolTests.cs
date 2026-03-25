using FluentAssertions;
using Moq;
using PPDS.Auth.Credentials;
using PPDS.Auth.Profiles;
using PPDS.Mcp.Infrastructure;
using PPDS.Mcp.Tools;
using Xunit;

namespace PPDS.Mcp.Tests.Tools;

/// <summary>
/// Unit tests for <see cref="PluginTracesDeleteTool"/>.
/// </summary>
[Trait("Category", "Unit")]
public sealed class PluginTracesDeleteToolTests
{
    #region Constructor Tests

    [Fact]
    public void Constructor_NullContext_ThrowsArgumentNullException()
    {
        // Act
        var act = () => new PluginTracesDeleteTool(null!);

        // Assert
        act.Should().Throw<ArgumentNullException>()
            .WithParameterName("context");
    }

    #endregion

    #region Parameter Validation Tests

    [Fact]
    public async Task ExecuteAsync_NoParameters_ReturnsError()
    {
        // Arrange
        var context = CreateContext();
        var tool = new PluginTracesDeleteTool(context);

        // Act
        var result = await tool.ExecuteAsync();

        // Assert
        result.Error.Should().NotBeNullOrWhiteSpace();
        result.Error.Should().Contain("At least one parameter is required");
    }

    [Fact]
    public async Task ExecuteAsync_MultipleModesProvided_ReturnsError()
    {
        // Arrange
        var context = CreateContext();
        var tool = new PluginTracesDeleteTool(context);

        // Act — provide both ids and olderThanDays
        var result = await tool.ExecuteAsync(
            ids: new[] { Guid.NewGuid().ToString() },
            olderThanDays: 7);

        // Assert
        result.Error.Should().NotBeNullOrWhiteSpace();
        result.Error.Should().Contain("Only one deletion mode");
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

using FluentAssertions;
using Moq;
using PPDS.Auth.Credentials;
using PPDS.Auth.Profiles;
using PPDS.Mcp.Infrastructure;
using PPDS.Mcp.Tools;
using Xunit;

namespace PPDS.Mcp.Tests.Tools;

/// <summary>
/// Unit tests for <see cref="PluginTracesTimelineTool"/>.
/// </summary>
[Trait("Category", "Unit")]
public sealed class PluginTracesTimelineToolTests
{
    #region Constructor Tests

    [Fact]
    public void Constructor_NullContext_ThrowsArgumentNullException()
    {
        // Act
        var act = () => new PluginTracesTimelineTool(null!);

        // Assert
        act.Should().Throw<ArgumentNullException>()
            .WithParameterName("context");
    }

    #endregion

    #region Parameter Validation Tests

    [Fact]
    public async Task ExecuteAsync_NullCorrelationId_ThrowsArgumentException()
    {
        // Arrange
        var context = CreateContext();
        var tool = new PluginTracesTimelineTool(context);

        // Act
        Func<Task> act = () => tool.ExecuteAsync(null!);

        // Assert
        await act.Should().ThrowAsync<ArgumentNullException>()
            .WithParameterName("correlationId");
    }

    [Fact]
    public async Task ExecuteAsync_EmptyCorrelationId_ThrowsArgumentException()
    {
        // Arrange
        var context = CreateContext();
        var tool = new PluginTracesTimelineTool(context);

        // Act
        Func<Task> act = () => tool.ExecuteAsync("");

        // Assert
        await act.Should().ThrowAsync<ArgumentException>()
            .WithParameterName("correlationId");
    }

    [Fact]
    public async Task ExecuteAsync_WhitespaceCorrelationId_ThrowsArgumentException()
    {
        // Arrange
        var context = CreateContext();
        var tool = new PluginTracesTimelineTool(context);

        // Act
        Func<Task> act = () => tool.ExecuteAsync("   ");

        // Assert
        await act.Should().ThrowAsync<ArgumentException>()
            .WithParameterName("correlationId");
    }

    [Fact]
    public async Task ExecuteAsync_InvalidGuidCorrelationId_ThrowsArgumentException()
    {
        // Arrange
        var context = CreateContext();
        var tool = new PluginTracesTimelineTool(context);

        // Act
        Func<Task> act = () => tool.ExecuteAsync("not-a-guid");

        // Assert
        await act.Should().ThrowAsync<ArgumentException>()
            .WithParameterName("correlationId")
            .WithMessage("*Invalid correlation ID format*");
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

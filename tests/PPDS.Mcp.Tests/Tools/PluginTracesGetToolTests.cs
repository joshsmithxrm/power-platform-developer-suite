using FluentAssertions;
using Moq;
using PPDS.Auth.Credentials;
using PPDS.Auth.Profiles;
using PPDS.Mcp.Infrastructure;
using PPDS.Mcp.Tools;
using Xunit;

namespace PPDS.Mcp.Tests.Tools;

/// <summary>
/// Unit tests for <see cref="PluginTracesGetTool"/>.
/// </summary>
[Trait("Category", "Unit")]
public sealed class PluginTracesGetToolTests
{
    #region Constructor Tests

    [Fact]
    public void Constructor_NullContext_ThrowsArgumentNullException()
    {
        // Act
        var act = () => new PluginTracesGetTool(null!);

        // Assert
        act.Should().Throw<ArgumentNullException>()
            .WithParameterName("context");
    }

    #endregion

    #region Parameter Validation Tests

    [Fact]
    public async Task ExecuteAsync_NullTraceId_ThrowsArgumentException()
    {
        // Arrange
        var context = CreateContext();
        var tool = new PluginTracesGetTool(context);

        // Act
        Func<Task> act = () => tool.ExecuteAsync(null!);

        // Assert
        await act.Should().ThrowAsync<ArgumentNullException>()
            .WithParameterName("traceId");
    }

    [Fact]
    public async Task ExecuteAsync_EmptyTraceId_ThrowsArgumentException()
    {
        // Arrange
        var context = CreateContext();
        var tool = new PluginTracesGetTool(context);

        // Act
        Func<Task> act = () => tool.ExecuteAsync("");

        // Assert
        await act.Should().ThrowAsync<ArgumentException>()
            .WithParameterName("traceId");
    }

    [Fact]
    public async Task ExecuteAsync_WhitespaceTraceId_ThrowsArgumentException()
    {
        // Arrange
        var context = CreateContext();
        var tool = new PluginTracesGetTool(context);

        // Act
        Func<Task> act = () => tool.ExecuteAsync("   ");

        // Assert
        await act.Should().ThrowAsync<ArgumentException>()
            .WithParameterName("traceId");
    }

    [Fact]
    public async Task ExecuteAsync_InvalidGuidTraceId_ThrowsArgumentException()
    {
        // Arrange
        var context = CreateContext();
        var tool = new PluginTracesGetTool(context);

        // Act
        Func<Task> act = () => tool.ExecuteAsync("not-a-guid");

        // Assert
        await act.Should().ThrowAsync<ArgumentException>()
            .WithParameterName("traceId")
            .WithMessage("*Invalid trace ID format*");
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

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
    public async Task ExecuteAsync_NoParameters_ThrowsArgumentException()
    {
        // Arrange
        var context = CreateContext();
        var tool = new PluginTracesDeleteTool(context);

        // Act
        Func<Task> act = () => tool.ExecuteAsync();

        // Assert
        await act.Should().ThrowAsync<ArgumentException>()
            .WithMessage("*At least one parameter is required*");
    }

    [Fact]
    public async Task ExecuteAsync_MultipleModesProvided_ThrowsArgumentException()
    {
        // Arrange
        var context = CreateContext();
        var tool = new PluginTracesDeleteTool(context);

        // Act
        Func<Task> act = () => tool.ExecuteAsync(
            ids: new[] { Guid.NewGuid().ToString() },
            olderThanDays: 7);

        // Assert
        await act.Should().ThrowAsync<ArgumentException>()
            .WithMessage("*Only one deletion mode*");
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

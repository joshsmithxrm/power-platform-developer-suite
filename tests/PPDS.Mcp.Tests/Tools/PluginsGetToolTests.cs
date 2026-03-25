using FluentAssertions;
using Moq;
using PPDS.Auth.Credentials;
using PPDS.Auth.Profiles;
using PPDS.Mcp.Infrastructure;
using PPDS.Mcp.Tools;
using Xunit;

namespace PPDS.Mcp.Tests.Tools;

/// <summary>
/// Unit tests for <see cref="PluginsGetTool"/>.
/// </summary>
[Trait("Category", "Unit")]
public sealed class PluginsGetToolTests
{
    #region Constructor Tests

    [Fact]
    public void Constructor_NullContext_ThrowsArgumentNullException()
    {
        // Act
        var act = () => new PluginsGetTool(null!);

        // Assert
        act.Should().Throw<ArgumentNullException>()
            .WithParameterName("context");
    }

    #endregion

    #region Parameter Validation Tests

    [Fact]
    public async Task ExecuteAsync_NullType_ThrowsNullReferenceException()
    {
        // Arrange
        // The tool calls type.ToLowerInvariant() without a null guard, so null type
        // causes a NullReferenceException before reaching the switch.
        var context = CreateContext();
        var tool = new PluginsGetTool(context);

        // Act
        Func<Task> act = () => tool.ExecuteAsync(null!, "test-name");

        // Assert
        await act.Should().ThrowAsync<NullReferenceException>();
    }

    [Fact]
    public async Task ExecuteAsync_InvalidType_ThrowsArgumentException()
    {
        // Arrange
        var context = CreateContext();
        var tool = new PluginsGetTool(context);

        // Act
        Func<Task> act = () => tool.ExecuteAsync("invalid", "test-name");

        // Assert
        await act.Should().ThrowAsync<ArgumentException>()
            .WithMessage("*Invalid type*");
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

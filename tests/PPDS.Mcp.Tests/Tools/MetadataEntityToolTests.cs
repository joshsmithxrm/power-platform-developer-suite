using FluentAssertions;
using Moq;
using PPDS.Auth.Credentials;
using PPDS.Auth.Profiles;
using PPDS.Mcp.Infrastructure;
using PPDS.Mcp.Tools;
using Xunit;

namespace PPDS.Mcp.Tests.Tools;

/// <summary>
/// Unit tests for <see cref="MetadataEntityTool"/>.
/// </summary>
[Trait("Category", "Unit")]
public sealed class MetadataEntityToolTests
{
    #region Constructor Tests

    [Fact]
    public void Constructor_NullContext_ThrowsArgumentNullException()
    {
        // Act
        var act = () => new MetadataEntityTool(null!);

        // Assert
        act.Should().Throw<ArgumentNullException>()
            .WithParameterName("context");
    }

    #endregion

    #region Parameter Validation Tests

    [Fact]
    public async Task ExecuteAsync_NullEntityName_ThrowsArgumentException()
    {
        // Arrange
        var context = CreateContext();
        var tool = new MetadataEntityTool(context);

        // Act
        Func<Task> act = () => tool.ExecuteAsync(null!);

        // Assert
        await act.Should().ThrowAsync<ArgumentException>()
            .WithParameterName("entityName")
            .WithMessage("*'entityName' parameter is required*");
    }

    [Fact]
    public async Task ExecuteAsync_EmptyEntityName_ThrowsArgumentException()
    {
        // Arrange
        var context = CreateContext();
        var tool = new MetadataEntityTool(context);

        // Act
        Func<Task> act = () => tool.ExecuteAsync("");

        // Assert
        await act.Should().ThrowAsync<ArgumentException>()
            .WithParameterName("entityName");
    }

    [Fact]
    public async Task ExecuteAsync_WhitespaceEntityName_ThrowsArgumentException()
    {
        // Arrange
        var context = CreateContext();
        var tool = new MetadataEntityTool(context);

        // Act
        Func<Task> act = () => tool.ExecuteAsync("   ");

        // Assert
        await act.Should().ThrowAsync<ArgumentException>()
            .WithParameterName("entityName");
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

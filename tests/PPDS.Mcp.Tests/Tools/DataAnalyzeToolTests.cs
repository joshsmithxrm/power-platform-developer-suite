using FluentAssertions;
using Moq;
using PPDS.Auth.Credentials;
using PPDS.Auth.Profiles;
using PPDS.Mcp.Infrastructure;
using PPDS.Mcp.Tools;
using Xunit;

namespace PPDS.Mcp.Tests.Tools;

/// <summary>
/// Unit tests for <see cref="DataAnalyzeTool"/>.
/// </summary>
[Trait("Category", "Unit")]
public sealed class DataAnalyzeToolTests
{
    #region Constructor Tests

    [Fact]
    public void Constructor_NullContext_ThrowsArgumentNullException()
    {
        // Act
        var act = () => new DataAnalyzeTool(null!);

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
        var tool = new DataAnalyzeTool(context);

        // Act
        Func<Task> act = () => tool.ExecuteAsync(null!);

        // Assert
        await act.Should().ThrowAsync<ArgumentNullException>()
            .WithParameterName("entityName");
    }

    [Fact]
    public async Task ExecuteAsync_EmptyEntityName_ThrowsArgumentException()
    {
        // Arrange
        var context = CreateContext();
        var tool = new DataAnalyzeTool(context);

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
        var tool = new DataAnalyzeTool(context);

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

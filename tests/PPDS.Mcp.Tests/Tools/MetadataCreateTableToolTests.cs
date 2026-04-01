using FluentAssertions;
using Moq;
using PPDS.Auth.Credentials;
using PPDS.Auth.Profiles;
using PPDS.Mcp.Infrastructure;
using PPDS.Mcp.Tools;
using Xunit;

namespace PPDS.Mcp.Tests.Tools;

/// <summary>
/// Unit tests for <see cref="MetadataCreateTableTool"/>.
/// </summary>
[Trait("Category", "Unit")]
public sealed class MetadataCreateTableToolTests
{
    #region Constructor Tests

    [Fact]
    public void Constructor_NullContext_ThrowsArgumentNullException()
    {
        // Act
        var act = () => new MetadataCreateTableTool(null!);

        // Assert
        act.Should().Throw<ArgumentNullException>()
            .WithParameterName("context");
    }

    #endregion

    #region Parameter Validation Tests

    [Fact]
    public async Task ExecuteAsync_NullSolution_ThrowsArgumentException()
    {
        // Arrange
        var tool = CreateTool();

        // Act
        Func<Task> act = () => tool.ExecuteAsync(null!, "new_Test", "Test", "Tests", "Desc", "UserOwned");

        // Assert
        await act.Should().ThrowAsync<ArgumentException>()
            .WithParameterName("solution");
    }

    [Fact]
    public async Task ExecuteAsync_EmptySolution_ThrowsArgumentException()
    {
        // Arrange
        var tool = CreateTool();

        // Act
        Func<Task> act = () => tool.ExecuteAsync("", "new_Test", "Test", "Tests", "Desc", "UserOwned");

        // Assert
        await act.Should().ThrowAsync<ArgumentException>()
            .WithParameterName("solution");
    }

    [Fact]
    public async Task ExecuteAsync_WhitespaceSolution_ThrowsArgumentException()
    {
        // Arrange
        var tool = CreateTool();

        // Act
        Func<Task> act = () => tool.ExecuteAsync("   ", "new_Test", "Test", "Tests", "Desc", "UserOwned");

        // Assert
        await act.Should().ThrowAsync<ArgumentException>()
            .WithParameterName("solution");
    }

    [Fact]
    public async Task ExecuteAsync_NullSchemaName_ThrowsArgumentException()
    {
        // Arrange
        var tool = CreateTool();

        // Act
        Func<Task> act = () => tool.ExecuteAsync("MySolution", null!, "Test", "Tests", "Desc", "UserOwned");

        // Assert
        await act.Should().ThrowAsync<ArgumentException>()
            .WithParameterName("schemaName");
    }

    [Fact]
    public async Task ExecuteAsync_NullDisplayName_ThrowsArgumentException()
    {
        // Arrange
        var tool = CreateTool();

        // Act
        Func<Task> act = () => tool.ExecuteAsync("MySolution", "new_Test", null!, "Tests", "Desc", "UserOwned");

        // Assert
        await act.Should().ThrowAsync<ArgumentException>()
            .WithParameterName("displayName");
    }

    [Fact]
    public async Task ExecuteAsync_NullOwnershipType_ThrowsArgumentException()
    {
        // Arrange
        var tool = CreateTool();

        // Act
        Func<Task> act = () => tool.ExecuteAsync("MySolution", "new_Test", "Test", "Tests", "Desc", null!);

        // Assert
        await act.Should().ThrowAsync<ArgumentException>()
            .WithParameterName("ownershipType");
    }

    #endregion

    #region Helpers

    private static MetadataCreateTableTool CreateTool()
    {
        var mockPoolManager = new Mock<IMcpConnectionPoolManager>();
        var context = new McpToolContext(
            mockPoolManager.Object,
            new ProfileStore(),
            new Mock<ISecureCredentialStore>().Object,
            new McpSessionOptions());
        return new MetadataCreateTableTool(context);
    }

    #endregion
}

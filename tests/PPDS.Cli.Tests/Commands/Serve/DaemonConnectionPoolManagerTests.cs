using FluentAssertions;
using PPDS.Cli.Infrastructure;
using Xunit;

namespace PPDS.Cli.Tests.Commands.Serve;

/// <summary>
/// Tests for DaemonConnectionPoolManager.
/// Note: Pool creation tests require live ProfileStore/credentials and are in integration tests.
/// These tests focus on interface contract and disposal behavior.
/// </summary>
public class DaemonConnectionPoolManagerTests
{
    #region Construction Tests

    [Fact]
    public void Constructor_CreatesInstance_WithDefaultLogger()
    {
        // Act
        var manager = new DaemonConnectionPoolManager();

        // Assert
        manager.Should().NotBeNull();
    }

    #endregion

    #region InvalidateProfile Tests

    [Fact]
    public void InvalidateProfile_DoesNotThrow_WhenNoPoolsExist()
    {
        // Arrange
        var manager = new DaemonConnectionPoolManager();

        // Act - Should not throw even when no pools exist
        var act = () => manager.InvalidateProfile("nonexistent");

        // Assert
        act.Should().NotThrow();
    }

    [Fact]
    public void InvalidateProfile_DoesNotThrow_WhenProfileNameIsEmpty()
    {
        // Arrange
        var manager = new DaemonConnectionPoolManager();

        // Act - Should handle empty/null gracefully
        var act = () => manager.InvalidateProfile("");

        // Assert
        act.Should().NotThrow();
    }

    [Fact]
    public void InvalidateProfile_DoesNotThrow_WhenProfileNameIsNull()
    {
        // Arrange
        var manager = new DaemonConnectionPoolManager();

        // Act
        var act = () => manager.InvalidateProfile(null!);

        // Assert
        act.Should().NotThrow();
    }

    #endregion

    #region InvalidateEnvironment Tests

    [Fact]
    public void InvalidateEnvironment_DoesNotThrow_WhenNoPoolsExist()
    {
        // Arrange
        var manager = new DaemonConnectionPoolManager();

        // Act
        var act = () => manager.InvalidateEnvironment("https://nonexistent.crm.dynamics.com");

        // Assert
        act.Should().NotThrow();
    }

    [Fact]
    public void InvalidateEnvironment_DoesNotThrow_WhenUrlIsEmpty()
    {
        // Arrange
        var manager = new DaemonConnectionPoolManager();

        // Act
        var act = () => manager.InvalidateEnvironment("");

        // Assert
        act.Should().NotThrow();
    }

    #endregion

    #region Disposal Tests

    [Fact]
    public async Task DisposeAsync_CanBeCalledMultipleTimes()
    {
        // Arrange
        var manager = new DaemonConnectionPoolManager();

        // Act - Should not throw on multiple disposals
        await manager.DisposeAsync();
        var act = async () => await manager.DisposeAsync();

        // Assert
        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task GetOrCreatePoolAsync_ThrowsObjectDisposedException_AfterDisposal()
    {
        // Arrange
        var manager = new DaemonConnectionPoolManager();
        await manager.DisposeAsync();

        // Act
        var act = async () => await manager.GetOrCreatePoolAsync(
            new[] { "test" },
            "https://test.crm.dynamics.com");

        // Assert
        await act.Should().ThrowAsync<ObjectDisposedException>();
    }

    #endregion

    #region Argument Validation Tests

    [Fact]
    public async Task GetOrCreatePoolAsync_ThrowsArgumentException_WhenProfileNamesEmpty()
    {
        // Arrange
        var manager = new DaemonConnectionPoolManager();

        // Act
        var act = async () => await manager.GetOrCreatePoolAsync(
            Array.Empty<string>(),
            "https://test.crm.dynamics.com");

        // Assert
        await act.Should().ThrowAsync<ArgumentException>()
            .WithMessage("*At least one profile name*");
    }

    [Fact]
    public async Task GetOrCreatePoolAsync_ThrowsArgumentException_WhenProfileNamesNull()
    {
        // Arrange
        var manager = new DaemonConnectionPoolManager();

        // Act
        var act = async () => await manager.GetOrCreatePoolAsync(
            null!,
            "https://test.crm.dynamics.com");

        // Assert
        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task GetOrCreatePoolAsync_ThrowsArgumentException_WhenEnvironmentUrlInvalid(string? url)
    {
        // Arrange
        var manager = new DaemonConnectionPoolManager();

        // Act
        var act = async () => await manager.GetOrCreatePoolAsync(
            new[] { "test" },
            url!);

        // Assert
        await act.Should().ThrowAsync<ArgumentException>()
            .WithMessage("*Environment URL*");
    }

    #endregion
}

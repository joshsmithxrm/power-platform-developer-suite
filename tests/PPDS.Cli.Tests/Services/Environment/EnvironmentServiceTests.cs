using Microsoft.Extensions.Logging;
using Moq;
using PPDS.Auth.Cloud;
using PPDS.Auth.Profiles;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Cli.Services.Environment;
using Xunit;

namespace PPDS.Cli.Tests.Services.Environment;

/// <summary>
/// Unit tests for <see cref="EnvironmentService"/>.
/// </summary>
/// <remarks>
/// Tests that require external services (GlobalDiscoveryService, EnvironmentResolutionService)
/// are limited to validation and error handling scenarios.
/// Full integration testing is covered in PPDS.LiveTests.
/// </remarks>
public class EnvironmentServiceTests : IDisposable
{
    private readonly string _tempFilePath;
    private readonly ProfileStore _store;
    private readonly Mock<ILogger<EnvironmentService>> _mockLogger;
    private readonly EnvironmentService _service;

    public EnvironmentServiceTests()
    {
        _tempFilePath = Path.Combine(Path.GetTempPath(), $"ppds-test-env-{Guid.NewGuid():N}.json");
        _store = new ProfileStore(_tempFilePath);
        _mockLogger = new Mock<ILogger<EnvironmentService>>();
        _service = new EnvironmentService(_store, _mockLogger.Object);
    }

    public void Dispose()
    {
        _store.Dispose();
        if (File.Exists(_tempFilePath))
        {
            // Best-effort cleanup - test temp file deletion should not fail tests
            try { File.Delete(_tempFilePath); } catch { }
        }
    }

    #region Constructor Tests

    [Fact]
    public void Constructor_WithNullStore_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new EnvironmentService(null!, _mockLogger.Object));
    }

    [Fact]
    public void Constructor_WithNullLogger_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new EnvironmentService(_store, null!));
    }

    #endregion

    #region SupportsDiscovery Tests

    [Theory]
    [InlineData(AuthMethod.InteractiveBrowser, true)]
    [InlineData(AuthMethod.DeviceCode, true)]
    [InlineData(AuthMethod.ClientSecret, false)]
    [InlineData(AuthMethod.CertificateFile, false)]
    public void SupportsDiscovery_ReturnsCorrectValue(AuthMethod method, bool expected)
    {
        var result = _service.SupportsDiscovery(method);

        Assert.Equal(expected, result);
    }

    #endregion

    #region GetCurrentEnvironmentAsync Tests

    [Fact]
    public async Task GetCurrentEnvironmentAsync_WithNoActiveProfile_ReturnsNull()
    {
        var result = await _service.GetCurrentEnvironmentAsync();

        Assert.Null(result);
    }

    [Fact]
    public async Task GetCurrentEnvironmentAsync_WithNoEnvironment_ReturnsNull()
    {
        // Arrange
        var collection = new ProfileCollection();
        collection.Add(new AuthProfile { Name = "test" }); // Gets index 1
        collection.SetActiveByIndex(1); // First profile's index is 1
        await _store.SaveAsync(collection);
        _store.ClearCache();

        // Act
        var result = await _service.GetCurrentEnvironmentAsync();

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public async Task GetCurrentEnvironmentAsync_WithEnvironment_ReturnsEnvironmentSummary()
    {
        // Arrange
        var collection = new ProfileCollection();
        var profile = new AuthProfile
        {
            Name = "test",
            Environment = new EnvironmentInfo
            {
                EnvironmentId = "env-123",
                Url = "https://test.crm.dynamics.com",
                DisplayName = "Test Environment",
                Type = "Production",
                Region = "NAM"
            }
        };
        collection.Add(profile);
        collection.SetActiveByIndex(1);
        await _store.SaveAsync(collection);
        _store.ClearCache();

        // Act
        var result = await _service.GetCurrentEnvironmentAsync();

        // Assert
        Assert.NotNull(result);
        Assert.Equal("env-123", result.EnvironmentId);
        Assert.Equal("https://test.crm.dynamics.com", result.Url);
        Assert.Equal("Test Environment", result.DisplayName);
        Assert.Equal("Production", result.Type);
        Assert.Equal("NAM", result.Region);
    }

    #endregion

    #region ClearEnvironmentAsync Tests

    [Fact]
    public async Task ClearEnvironmentAsync_WithNoActiveProfile_ThrowsPpdsException()
    {
        var ex = await Assert.ThrowsAsync<PpdsException>(
            () => _service.ClearEnvironmentAsync());

        Assert.Equal(ErrorCodes.Profile.NoActiveProfile, ex.ErrorCode);
    }

    [Fact]
    public async Task ClearEnvironmentAsync_WithNoEnvironment_ReturnsFalse()
    {
        // Arrange
        var collection = new ProfileCollection();
        collection.Add(new AuthProfile { Name = "test" }); // Gets index 1
        collection.SetActiveByIndex(1); // First profile's index is 1
        await _store.SaveAsync(collection);
        _store.ClearCache();

        // Act
        var result = await _service.ClearEnvironmentAsync();

        // Assert
        Assert.False(result);
    }

    [Fact]
    public async Task ClearEnvironmentAsync_WithEnvironment_ReturnsTrue()
    {
        // Arrange
        var collection = new ProfileCollection();
        var profile = new AuthProfile
        {
            Name = "test",
            Environment = new EnvironmentInfo { Url = "https://test.crm.dynamics.com" }
        };
        collection.Add(profile);
        collection.SetActiveByIndex(1);
        await _store.SaveAsync(collection);
        _store.ClearCache();

        // Act
        var result = await _service.ClearEnvironmentAsync();

        // Assert
        Assert.True(result);
    }

    [Fact]
    public async Task ClearEnvironmentAsync_WithEnvironment_ClearsFromProfile()
    {
        // Arrange
        var collection = new ProfileCollection();
        var profile = new AuthProfile
        {
            Name = "test",
            Environment = new EnvironmentInfo { Url = "https://test.crm.dynamics.com" }
        };
        collection.Add(profile);
        collection.SetActiveByIndex(1);
        await _store.SaveAsync(collection);
        _store.ClearCache();

        // Act
        await _service.ClearEnvironmentAsync();

        // Assert
        _store.ClearCache();
        var currentEnv = await _service.GetCurrentEnvironmentAsync();
        Assert.Null(currentEnv);
    }

    #endregion

    #region DiscoverEnvironmentsAsync Tests

    [Fact]
    public async Task DiscoverEnvironmentsAsync_WithNoActiveProfile_ThrowsPpdsException()
    {
        var ex = await Assert.ThrowsAsync<PpdsException>(
            () => _service.DiscoverEnvironmentsAsync());

        Assert.Equal(ErrorCodes.Profile.NoActiveProfile, ex.ErrorCode);
    }

    [Fact]
    public async Task DiscoverEnvironmentsAsync_WithUnsupportedAuthMethod_ThrowsPpdsException()
    {
        // Arrange
        var collection = new ProfileCollection();
        collection.Add(new AuthProfile
        {
            Name = "spn",
            AuthMethod = AuthMethod.ClientSecret
        });
        collection.SetActiveByIndex(1);
        await _store.SaveAsync(collection);
        _store.ClearCache();

        // Act & Assert
        var ex = await Assert.ThrowsAsync<PpdsException>(
            () => _service.DiscoverEnvironmentsAsync());

        Assert.Equal(ErrorCodes.Operation.NotSupported, ex.ErrorCode);
        Assert.Contains("ClientSecret", ex.UserMessage);
    }

    #endregion

    #region SetEnvironmentAsync Tests

    [Fact]
    public async Task SetEnvironmentAsync_WithEmptyIdentifier_ThrowsValidationException()
    {
        await Assert.ThrowsAsync<PpdsValidationException>(
            () => _service.SetEnvironmentAsync(""));
    }

    [Fact]
    public async Task SetEnvironmentAsync_WithWhitespaceIdentifier_ThrowsValidationException()
    {
        await Assert.ThrowsAsync<PpdsValidationException>(
            () => _service.SetEnvironmentAsync("   "));
    }

    [Fact]
    public async Task SetEnvironmentAsync_WithNoActiveProfile_ThrowsPpdsException()
    {
        var ex = await Assert.ThrowsAsync<PpdsException>(
            () => _service.SetEnvironmentAsync("https://test.crm.dynamics.com"));

        Assert.Equal(ErrorCodes.Profile.NoActiveProfile, ex.ErrorCode);
    }

    #endregion

    #region EnvironmentSummary Mapping Tests

    [Fact]
    public async Task GetCurrentEnvironmentAsync_MapsAllFieldsCorrectly()
    {
        // Arrange
        var collection = new ProfileCollection();
        var profile = new AuthProfile
        {
            Name = "test",
            Environment = new EnvironmentInfo
            {
                EnvironmentId = "env-guid-123",
                OrganizationId = "org-guid-456",
                Url = "https://contoso.crm.dynamics.com",
                DisplayName = "Contoso Production",
                UniqueName = "contoso_production",
                Type = "Production",
                Region = "NAM"
            }
        };
        collection.Add(profile);
        collection.SetActiveByIndex(1);
        await _store.SaveAsync(collection);
        _store.ClearCache();

        // Act
        var result = await _service.GetCurrentEnvironmentAsync();

        // Assert
        Assert.NotNull(result);
        Assert.Equal("env-guid-123", result.EnvironmentId);
        Assert.Equal("org-guid-456", result.OrganizationId);
        Assert.Equal("https://contoso.crm.dynamics.com", result.Url);
        Assert.Equal("Contoso Production", result.DisplayName);
        Assert.Equal("contoso_production", result.UniqueName);
        Assert.Equal("Production", result.Type);
        Assert.Equal("NAM", result.Region);
    }

    #endregion
}

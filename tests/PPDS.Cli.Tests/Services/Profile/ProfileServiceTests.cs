using Microsoft.Extensions.Logging;
using Moq;
using PPDS.Auth.Cloud;
using PPDS.Auth.Profiles;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Cli.Services.Profile;
using Xunit;

namespace PPDS.Cli.Tests.Services.Profile;

/// <summary>
/// Unit tests for <see cref="ProfileService"/>.
/// </summary>
public class ProfileServiceTests : IDisposable
{
    private readonly string _tempFilePath;
    private readonly ProfileStore _store;
    private readonly Mock<ILogger<ProfileService>> _mockLogger;
    private readonly ProfileService _service;

    public ProfileServiceTests()
    {
        _tempFilePath = Path.Combine(Path.GetTempPath(), $"ppds-test-profiles-{Guid.NewGuid():N}.json");
        _store = new ProfileStore(_tempFilePath);
        _mockLogger = new Mock<ILogger<ProfileService>>();
        _service = new ProfileService(_store, _mockLogger.Object);
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
            new ProfileService(null!, _mockLogger.Object));
    }

    [Fact]
    public void Constructor_WithNullLogger_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new ProfileService(_store, null!));
    }

    #endregion

    #region GetProfilesAsync Tests

    [Fact]
    public async Task GetProfilesAsync_WithNoProfiles_ReturnsEmptyList()
    {
        var profiles = await _service.GetProfilesAsync();

        Assert.NotNull(profiles);
        Assert.Empty(profiles);
    }

    [Fact]
    public async Task GetProfilesAsync_WithProfiles_ReturnsAllProfiles()
    {
        // Arrange
        var collection = new ProfileCollection();
        collection.Add(new AuthProfile { Name = "profile1" });
        collection.Add(new AuthProfile { Name = "profile2" });
        await _store.SaveAsync(collection);
        _store.ClearCache();

        // Act
        var profiles = await _service.GetProfilesAsync();

        // Assert
        Assert.Equal(2, profiles.Count);
        Assert.Contains(profiles, p => p.DisplayIdentifier == "profile1");
        Assert.Contains(profiles, p => p.DisplayIdentifier == "profile2");
    }

    [Fact]
    public async Task GetProfilesAsync_WithActiveProfile_MarksAsActive()
    {
        // Arrange
        var collection = new ProfileCollection();
        collection.Add(new AuthProfile { Name = "profile1" }); // Gets index 1
        collection.Add(new AuthProfile { Name = "profile2" }); // Gets index 2
        collection.SetActiveByIndex(2); // Make second profile (index 2) active
        await _store.SaveAsync(collection);
        _store.ClearCache();

        // Act
        var profiles = await _service.GetProfilesAsync();

        // Assert
        Assert.Single(profiles, p => p.IsActive);
        Assert.True(profiles.First(p => p.DisplayIdentifier == "profile2").IsActive);
    }

    #endregion

    #region GetActiveProfileAsync Tests

    [Fact]
    public async Task GetActiveProfileAsync_WithNoProfiles_ReturnsNull()
    {
        var active = await _service.GetActiveProfileAsync();

        Assert.Null(active);
    }

    [Fact]
    public async Task GetActiveProfileAsync_WithActiveProfile_ReturnsActive()
    {
        // Arrange
        var collection = new ProfileCollection();
        collection.Add(new AuthProfile { Name = "profile1" }); // Gets index 1
        collection.SetActiveByIndex(1); // First profile's index is 1
        await _store.SaveAsync(collection);
        _store.ClearCache();

        // Act
        var active = await _service.GetActiveProfileAsync();

        // Assert
        Assert.NotNull(active);
        Assert.Equal("profile1", active.DisplayIdentifier);
        Assert.True(active.IsActive);
    }

    #endregion

    #region SetActiveProfileAsync Tests

    [Fact]
    public async Task SetActiveProfileAsync_WithValidName_SetsActive()
    {
        // Arrange
        var collection = new ProfileCollection();
        collection.Add(new AuthProfile { Name = "profile1" });
        collection.Add(new AuthProfile { Name = "profile2" });
        await _store.SaveAsync(collection);
        _store.ClearCache();

        // Act
        var result = await _service.SetActiveProfileAsync("profile2");

        // Assert
        Assert.Equal("profile2", result.DisplayIdentifier);
        Assert.True(result.IsActive);

        // Verify persisted
        _store.ClearCache();
        var active = await _service.GetActiveProfileAsync();
        Assert.Equal("profile2", active?.DisplayIdentifier);
    }

    [Fact]
    public async Task SetActiveProfileAsync_WithValidIndex_SetsActive()
    {
        // Arrange
        var collection = new ProfileCollection();
        collection.Add(new AuthProfile { Name = "profile1" }); // Gets index 1
        collection.Add(new AuthProfile { Name = "profile2" }); // Gets index 2
        await _store.SaveAsync(collection);
        _store.ClearCache();

        // Act
        var result = await _service.SetActiveProfileAsync("2"); // Index 2 = profile2

        // Assert
        Assert.Equal("profile2", result.DisplayIdentifier);
    }

    [Fact]
    public async Task SetActiveProfileAsync_WithNonexistentProfile_ThrowsNotFoundException()
    {
        // Arrange
        var collection = new ProfileCollection();
        collection.Add(new AuthProfile { Name = "profile1" });
        await _store.SaveAsync(collection);
        _store.ClearCache();

        // Act & Assert
        var ex = await Assert.ThrowsAsync<PpdsNotFoundException>(
            () => _service.SetActiveProfileAsync("nonexistent"));

        Assert.Contains("Profile", ex.Message);
    }

    #endregion

    #region DeleteProfileAsync Tests

    [Fact]
    public async Task DeleteProfileAsync_WithExistingProfile_ReturnsTrue()
    {
        // Arrange
        var collection = new ProfileCollection();
        collection.Add(new AuthProfile { Name = "profile1" });
        await _store.SaveAsync(collection);
        _store.ClearCache();

        // Act
        var result = await _service.DeleteProfileAsync("profile1");

        // Assert
        Assert.True(result);
    }

    [Fact]
    public async Task DeleteProfileAsync_WithExistingProfile_RemovesFromStore()
    {
        // Arrange
        var collection = new ProfileCollection();
        collection.Add(new AuthProfile { Name = "profile1" });
        collection.Add(new AuthProfile { Name = "profile2" });
        await _store.SaveAsync(collection);
        _store.ClearCache();

        // Act
        await _service.DeleteProfileAsync("profile1");

        // Assert
        _store.ClearCache();
        var profiles = await _service.GetProfilesAsync();
        Assert.Single(profiles);
        Assert.Equal("profile2", profiles[0].DisplayIdentifier);
    }

    [Fact]
    public async Task DeleteProfileAsync_WithNonexistentProfile_ReturnsFalse()
    {
        // Arrange
        var collection = new ProfileCollection();
        collection.Add(new AuthProfile { Name = "profile1" });
        await _store.SaveAsync(collection);
        _store.ClearCache();

        // Act
        var result = await _service.DeleteProfileAsync("nonexistent");

        // Assert
        Assert.False(result);
    }

    [Fact]
    public async Task DeleteProfileAsync_ByIndex_DeletesCorrectProfile()
    {
        // Arrange
        var collection = new ProfileCollection();
        collection.Add(new AuthProfile { Name = "profile1" }); // Gets index 1
        collection.Add(new AuthProfile { Name = "profile2" }); // Gets index 2
        await _store.SaveAsync(collection);
        _store.ClearCache();

        // Act
        await _service.DeleteProfileAsync("1"); // Delete first profile (index 1)

        // Assert
        _store.ClearCache();
        var profiles = await _service.GetProfilesAsync();
        Assert.Single(profiles);
        Assert.Equal("profile2", profiles[0].DisplayIdentifier);
    }

    #endregion

    #region UpdateProfileAsync Tests

    [Fact]
    public async Task UpdateProfileAsync_WithNewName_UpdatesName()
    {
        // Arrange
        var collection = new ProfileCollection();
        collection.Add(new AuthProfile { Name = "oldname" });
        await _store.SaveAsync(collection);
        _store.ClearCache();

        // Act
        var result = await _service.UpdateProfileAsync("oldname", newName: "newname");

        // Assert
        Assert.Equal("newname", result.DisplayIdentifier);

        // Verify persisted
        _store.ClearCache();
        var profiles = await _service.GetProfilesAsync();
        Assert.Single(profiles);
        Assert.Equal("newname", profiles[0].DisplayIdentifier);
    }

    [Fact]
    public async Task UpdateProfileAsync_WithNonexistentProfile_ThrowsNotFoundException()
    {
        // Act & Assert
        await Assert.ThrowsAsync<PpdsNotFoundException>(
            () => _service.UpdateProfileAsync("nonexistent", newName: "newname"));
    }

    [Fact]
    public async Task UpdateProfileAsync_WithDuplicateName_ThrowsValidationException()
    {
        // Arrange
        var collection = new ProfileCollection();
        collection.Add(new AuthProfile { Name = "profile1" });
        collection.Add(new AuthProfile { Name = "profile2" });
        await _store.SaveAsync(collection);
        _store.ClearCache();

        // Act & Assert
        await Assert.ThrowsAsync<PpdsValidationException>(
            () => _service.UpdateProfileAsync("profile1", newName: "profile2"));
    }

    #endregion

    #region ClearAllAsync Tests

    [Fact]
    public async Task ClearAllAsync_ClearsAllProfiles()
    {
        // Arrange
        var collection = new ProfileCollection();
        collection.Add(new AuthProfile { Name = "profile1" });
        collection.Add(new AuthProfile { Name = "profile2" });
        await _store.SaveAsync(collection);
        _store.ClearCache();

        // Act
        await _service.ClearAllAsync();

        // Assert
        _store.ClearCache();
        var profiles = await _service.GetProfilesAsync();
        Assert.Empty(profiles);
    }

    #endregion

    #region ProfileSummary Mapping Tests

    [Fact]
    public async Task GetProfilesAsync_MapsAuthMethodCorrectly()
    {
        // Arrange
        var collection = new ProfileCollection();
        collection.Add(new AuthProfile { Name = "user", AuthMethod = AuthMethod.InteractiveBrowser });
        collection.Add(new AuthProfile { Name = "spn", AuthMethod = AuthMethod.ClientSecret });
        await _store.SaveAsync(collection);
        _store.ClearCache();

        // Act
        var profiles = await _service.GetProfilesAsync();

        // Assert
        Assert.Equal(AuthMethod.InteractiveBrowser, profiles.First(p => p.DisplayIdentifier == "user").AuthMethod);
        Assert.Equal(AuthMethod.ClientSecret, profiles.First(p => p.DisplayIdentifier == "spn").AuthMethod);
    }

    [Fact]
    public async Task GetProfilesAsync_MapsEnvironmentInfoCorrectly()
    {
        // Arrange
        var collection = new ProfileCollection();
        collection.Add(new AuthProfile
        {
            Name = "test",
            Environment = new EnvironmentInfo
            {
                Url = "https://test.crm.dynamics.com",
                DisplayName = "Test Environment"
            }
        });
        await _store.SaveAsync(collection);
        _store.ClearCache();

        // Act
        var profiles = await _service.GetProfilesAsync();

        // Assert
        Assert.Equal("https://test.crm.dynamics.com", profiles[0].EnvironmentUrl);
        Assert.Equal("Test Environment", profiles[0].EnvironmentName);
    }

    #endregion
}

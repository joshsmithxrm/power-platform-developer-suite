using PPDS.Auth.Profiles;

namespace PPDS.Cli.Tests.Mocks;

/// <summary>
/// Helper for creating isolated ProfileStore instances in tests.
/// Uses a temporary directory that is cleaned up on disposal.
/// </summary>
public sealed class TempProfileStore : IDisposable
{
    private readonly string _tempDir;
    private readonly ProfileStore _store;
    private bool _disposed;

    /// <summary>
    /// Creates a new temporary profile store.
    /// </summary>
    public TempProfileStore()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "ppds-test-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);

        var filePath = Path.Combine(_tempDir, "profiles.json");
        _store = new ProfileStore(filePath);
    }

    /// <summary>
    /// Gets the underlying ProfileStore.
    /// </summary>
    public ProfileStore Store => _store;

    /// <summary>
    /// Gets the temporary directory path.
    /// </summary>
    public string TempDirectory => _tempDir;

    /// <summary>
    /// Seeds the store with the specified profiles.
    /// </summary>
    /// <param name="activeProfileName">The name of the profile to set as active (null for no active profile).</param>
    /// <param name="profiles">The profiles to add.</param>
    public async Task SeedProfilesAsync(string? activeProfileName, params AuthProfile[] profiles)
    {
        var collection = new ProfileCollection
        {
            Version = ProfileStore.CurrentVersion,
            ActiveProfileName = activeProfileName
        };

        foreach (var profile in profiles)
        {
            collection.Add(profile);
        }

        await _store.SaveAsync(collection);
    }

    /// <summary>
    /// Creates a test profile with the specified name and environment.
    /// </summary>
    public static AuthProfile CreateTestProfile(
        string name,
        AuthMethod authMethod = AuthMethod.DeviceCode,
        string? environmentUrl = null,
        string? environmentName = null)
    {
        return new AuthProfile
        {
            Name = name,
            AuthMethod = authMethod,
            TenantId = "00000000-0000-0000-0000-000000000000",
            Environment = environmentUrl != null
                ? new EnvironmentInfo { Url = environmentUrl, DisplayName = environmentName ?? environmentUrl }
                : null
        };
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _store.Dispose();

        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, recursive: true);
            }
        }
        catch
        {
            // Best effort cleanup
        }
    }
}

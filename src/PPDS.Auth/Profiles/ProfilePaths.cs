using System;
using System.IO;

namespace PPDS.Auth.Profiles;

/// <summary>
/// Provides platform-specific paths for profile storage.
/// </summary>
public static class ProfilePaths
{
    /// <summary>
    /// Application name used in paths.
    /// </summary>
    public const string AppName = "PPDS";

    /// <summary>
    /// Environment variable to override the data directory.
    /// </summary>
    public const string ConfigDirEnvVar = "PPDS_CONFIG_DIR";

    /// <summary>
    /// Profile storage file name.
    /// </summary>
    public const string ProfilesFileName = "profiles.json";

    /// <summary>
    /// Environment configuration file name.
    /// </summary>
    public const string EnvironmentsFileName = "environments.json";

    /// <summary>
    /// MSAL token cache file name.
    /// </summary>
    public const string TokenCacheFileName = "msal_token_cache.bin";

    /// <summary>
    /// Secure credential cache file name.
    /// </summary>
    public const string CredentialCacheFileName = "ppds.credentials.dat";

    /// <summary>
    /// Gets the PPDS data directory for the current platform.
    /// </summary>
    /// <remarks>
    /// Priority: PPDS_CONFIG_DIR env var > platform default.
    /// Windows default: %LOCALAPPDATA%\PPDS
    /// macOS/Linux default: ~/.ppds
    /// </remarks>
    public static string DataDirectory
    {
        get
        {
            // Check for environment variable override (useful for testing and CI)
            var envOverride = Environment.GetEnvironmentVariable(ConfigDirEnvVar);
            if (!string.IsNullOrWhiteSpace(envOverride))
            {
                return envOverride;
            }

            if (OperatingSystem.IsWindows())
            {
                var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                return Path.Combine(localAppData, AppName);
            }
            else
            {
                var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                return Path.Combine(home, $".{AppName.ToLowerInvariant()}");
            }
        }
    }

    /// <summary>
    /// Gets the full path to the profiles file.
    /// </summary>
    public static string ProfilesFile => Path.Combine(DataDirectory, ProfilesFileName);

    /// <summary>
    /// Gets the full path to the environment configuration file.
    /// </summary>
    public static string EnvironmentsFile => Path.Combine(DataDirectory, EnvironmentsFileName);

    /// <summary>
    /// Gets the full path to the MSAL token cache file.
    /// </summary>
    public static string TokenCacheFile => Path.Combine(DataDirectory, TokenCacheFileName);

    /// <summary>
    /// Gets the full path to the secure credential cache file.
    /// </summary>
    public static string CredentialCacheFile => Path.Combine(DataDirectory, CredentialCacheFileName);

    /// <summary>
    /// Ensures the data directory exists.
    /// </summary>
    public static void EnsureDirectoryExists()
    {
        var dir = DataDirectory;
        if (!Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }
    }
}

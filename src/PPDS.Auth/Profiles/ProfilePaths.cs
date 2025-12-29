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
    /// Profile storage file name.
    /// </summary>
    public const string ProfilesFileName = "profiles.json";

    /// <summary>
    /// MSAL token cache file name.
    /// </summary>
    public const string TokenCacheFileName = "msal_token_cache.bin";

    /// <summary>
    /// Gets the PPDS data directory for the current platform.
    /// </summary>
    /// <remarks>
    /// Windows: %LOCALAPPDATA%\PPDS
    /// macOS/Linux: ~/.ppds
    /// </remarks>
    public static string DataDirectory
    {
        get
        {
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
    /// Gets the full path to the MSAL token cache file.
    /// </summary>
    public static string TokenCacheFile => Path.Combine(DataDirectory, TokenCacheFileName);

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

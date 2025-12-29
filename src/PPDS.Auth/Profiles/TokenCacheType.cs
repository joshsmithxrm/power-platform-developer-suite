using System.Runtime.InteropServices;

namespace PPDS.Auth.Profiles;

/// <summary>
/// Token cache storage type.
/// </summary>
public enum TokenCacheType
{
    /// <summary>
    /// Token cache stored in OS credential store (Windows DPAPI, macOS Keychain, Linux libsecret).
    /// </summary>
    OperatingSystem,

    /// <summary>
    /// Token cache stored in a file (fallback for systems without secure storage).
    /// </summary>
    File,

    /// <summary>
    /// Token cache stored in memory only (not persisted).
    /// </summary>
    Memory
}

/// <summary>
/// Utility to detect the token cache type for the current platform.
/// </summary>
public static class TokenCacheDetector
{
    /// <summary>
    /// Gets the token cache type for the current platform.
    /// </summary>
    public static TokenCacheType GetCacheType()
    {
        // Windows always uses DPAPI via MSAL Extensions
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return TokenCacheType.OperatingSystem;
        }

        // macOS uses Keychain via MSAL Extensions
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            return TokenCacheType.OperatingSystem;
        }

        // Linux: check if libsecret is available
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            // Check for common desktop environments with secret service
            var hasSecretService =
                !string.IsNullOrEmpty(System.Environment.GetEnvironmentVariable("DISPLAY")) ||
                !string.IsNullOrEmpty(System.Environment.GetEnvironmentVariable("WAYLAND_DISPLAY"));

            // In headless environments or without secret service, fall back to file
            return hasSecretService ? TokenCacheType.OperatingSystem : TokenCacheType.File;
        }

        // Unknown platform - assume file
        return TokenCacheType.File;
    }
}

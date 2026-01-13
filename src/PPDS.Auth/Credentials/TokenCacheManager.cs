using System.IO;
using System.Threading.Tasks;
using PPDS.Auth.Profiles;

namespace PPDS.Auth.Credentials;

/// <summary>
/// Manages token cache operations including clearing cached credentials.
/// </summary>
public static class TokenCacheManager
{
    /// <summary>
    /// Clears the MSAL file-based token cache.
    /// </summary>
    /// <param name="tokenCachePath">
    /// Optional path to the token cache file. If null, uses the default global path.
    /// Pass a custom path when using isolated test directories to avoid clearing production cache.
    /// </param>
    /// <remarks>
    /// This method deletes the unprotected file-based token cache used by the CLI.
    /// The cache file location matches <see cref="MsalClientBuilder.CreateAndRegisterCacheAsync"/>.
    /// </remarks>
    public static Task ClearAllCachesAsync(string? tokenCachePath = null)
    {
        var path = tokenCachePath ?? ProfilePaths.TokenCacheFile;
        if (File.Exists(path))
        {
            File.Delete(path);
        }

        return Task.CompletedTask;
    }
}

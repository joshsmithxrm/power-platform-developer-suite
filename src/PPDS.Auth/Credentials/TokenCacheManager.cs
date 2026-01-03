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
    /// <remarks>
    /// This method deletes the unprotected file-based token cache used by the CLI.
    /// The cache file location matches <see cref="MsalClientBuilder.CreateAndRegisterCacheAsync"/>.
    /// </remarks>
    public static Task ClearAllCachesAsync()
    {
        if (File.Exists(ProfilePaths.TokenCacheFile))
        {
            File.Delete(ProfilePaths.TokenCacheFile);
        }

        return Task.CompletedTask;
    }
}

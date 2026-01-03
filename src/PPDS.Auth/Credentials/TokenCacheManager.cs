using System.IO;
using System.Threading.Tasks;
using Microsoft.Identity.Client.Extensions.Msal;
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
    /// This method clears the unprotected file-based token cache used by the CLI.
    /// The cache configuration matches <see cref="MsalClientBuilder.CreateAndRegisterCacheAsync"/>.
    /// </remarks>
    public static async Task ClearAllCachesAsync()
    {
        // Delete the file-based token cache directly
        if (File.Exists(ProfilePaths.TokenCacheFile))
        {
            File.Delete(ProfilePaths.TokenCacheFile);
        }

        // Also clear via MsalCacheHelper to ensure consistency
        try
        {
            // Match the storage configuration from MsalClientBuilder
            var storageProperties = new StorageCreationPropertiesBuilder(
                    ProfilePaths.TokenCacheFileName,
                    ProfilePaths.DataDirectory)
                .WithUnprotectedFile()
                .Build();

#pragma warning disable CS0618 // Clear() is obsolete but appropriate for full logout
            var cacheHelper = await MsalCacheHelper.CreateAsync(storageProperties).ConfigureAwait(false);
            cacheHelper.Clear();
#pragma warning restore CS0618
        }
        catch (MsalCachePersistenceException)
        {
            // Cache persistence not available - file deletion above handles this case
        }
    }
}

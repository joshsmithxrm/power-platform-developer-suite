using System;
using System.Threading.Tasks;
using Microsoft.Identity.Client;
using Microsoft.Identity.Client.Extensions.Msal;
using PPDS.Auth.Cloud;
using PPDS.Auth.Profiles;

namespace PPDS.Auth.Credentials;

/// <summary>
/// Builder for creating and configuring MSAL public client applications with persistent token cache.
/// Consolidates common MSAL initialization patterns used across credential providers.
/// </summary>
internal static class MsalClientBuilder
{
    /// <summary>
    /// Microsoft's well-known public client ID for first-party apps.
    /// This is the same client ID used by Azure CLI, PowerShell, and PAC CLI.
    /// </summary>
    public const string MicrosoftPublicClientId = "51f81489-12ee-4a9e-aaae-a2591f45987d";

    /// <summary>
    /// Redirect URI options for different authentication flows.
    /// </summary>
    public enum RedirectUriOption
    {
        /// <summary>No redirect URI configured.</summary>
        None,

        /// <summary>Use MSAL's default redirect URI.</summary>
        Default,

        /// <summary>Use localhost for browser-based auth.</summary>
        Localhost
    }

    /// <summary>
    /// Creates and configures a public client application.
    /// </summary>
    /// <param name="cloud">The cloud environment.</param>
    /// <param name="tenantId">The tenant ID, or null for multi-tenant.</param>
    /// <param name="redirectUri">The redirect URI option for the auth flow.</param>
    /// <returns>A configured public client application.</returns>
    public static IPublicClientApplication CreateClient(
        CloudEnvironment cloud,
        string? tenantId,
        RedirectUriOption redirectUri = RedirectUriOption.None)
    {
        var cloudInstance = CloudEndpoints.GetAzureCloudInstance(cloud);
        var tenant = string.IsNullOrWhiteSpace(tenantId) ? "organizations" : tenantId;

        var builder = PublicClientApplicationBuilder
            .Create(MicrosoftPublicClientId)
            .WithAuthority(cloudInstance, tenant);

        builder = redirectUri switch
        {
            RedirectUriOption.Default => builder.WithDefaultRedirectUri(),
            RedirectUriOption.Localhost => builder.WithRedirectUri("http://localhost"),
            _ => builder
        };

        return builder.Build();
    }

    /// <summary>
    /// Creates and registers a persistent token cache for the client.
    /// </summary>
    /// <param name="client">The MSAL client to register the cache with.</param>
    /// <param name="warnOnFailure">If true, writes a warning to Console.Error on cache failure.</param>
    /// <returns>The cache helper if successful, or null if cache persistence failed.</returns>
    public static async Task<MsalCacheHelper?> CreateAndRegisterCacheAsync(
        IPublicClientApplication client,
        bool warnOnFailure = true)
    {
        try
        {
            ProfilePaths.EnsureDirectoryExists();

            var storageProperties = new StorageCreationPropertiesBuilder(
                    ProfilePaths.TokenCacheFileName,
                    ProfilePaths.DataDirectory)
                .WithUnprotectedFile() // Fallback for Linux without libsecret
                .Build();

            var cacheHelper = await MsalCacheHelper.CreateAsync(storageProperties).ConfigureAwait(false);
            cacheHelper.RegisterCache(client.UserTokenCache);

            return cacheHelper;
        }
        catch (MsalCachePersistenceException ex)
        {
            if (warnOnFailure)
            {
                AuthenticationOutput.WriteLine(
                    $"Warning: Token cache persistence unavailable ({ex.Message}). You may need to re-authenticate each session.");
            }

            return null;
        }
    }

    /// <summary>
    /// Unregisters and cleans up the cache helper.
    /// </summary>
    /// <param name="cacheHelper">The cache helper to clean up.</param>
    /// <param name="client">The client whose cache was registered.</param>
    public static void UnregisterCache(MsalCacheHelper? cacheHelper, IPublicClientApplication? client)
    {
        if (cacheHelper != null && client != null)
        {
            try
            {
                cacheHelper.UnregisterCache(client.UserTokenCache);
            }
            catch (Exception)
            {
                // Cleanup should never throw - swallow all errors
            }
        }
    }
}

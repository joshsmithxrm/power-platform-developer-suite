using System;
using System.Runtime.InteropServices;
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

        AuthDebugLog.WriteLine($"[MsalClient] Creating client: cloud={cloud}, tenantId={tenantId ?? "(null)"}, authority={cloudInstance}/{tenant}");

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
        var cacheFilePath = System.IO.Path.Combine(ProfilePaths.DataDirectory, ProfilePaths.TokenCacheFileName);

        try
        {
            ProfilePaths.EnsureDirectoryExists();

            var cacheFileExists = System.IO.File.Exists(cacheFilePath);
            var cacheFileSize = cacheFileExists ? new System.IO.FileInfo(cacheFilePath).Length : 0;

            AuthDebugLog.WriteLine($"[MsalCache] Registering cache: path={cacheFilePath}, exists={cacheFileExists}, size={cacheFileSize} bytes");

            var cacheHelper = await CreatePlatformCacheHelperAsync(warnOnFailure).ConfigureAwait(false);

            // Verify persistence works before registering - performs write/read/clear test
            // Throws MsalCachePersistenceException if persistence is unavailable
            cacheHelper.VerifyPersistence();
            AuthDebugLog.WriteLine("[MsalCache] Persistence verification PASSED");

            cacheHelper.RegisterCache(client.UserTokenCache);

            cacheFileExists = System.IO.File.Exists(cacheFilePath);
            AuthDebugLog.WriteLine($"[MsalCache] Cache registered successfully. File exists: {cacheFileExists}");

            return cacheHelper;
        }
        catch (MsalCachePersistenceException ex)
        {
            AuthDebugLog.WriteLine($"[MsalCache] Persistence verification FAILED: {ex.Message}");
            AuthDebugLog.WriteLine($"[MsalCache] Cache file path: {cacheFilePath}");

            if (warnOnFailure)
            {
                AuthenticationOutput.WriteLine(
                    $"Warning: Token cache persistence unavailable ({ex.Message}). You may need to re-authenticate each session.");
            }

            return null;
        }
    }

    /// <summary>
    /// Builds a platform-appropriate <see cref="MsalCacheHelper"/>:
    /// Windows uses DPAPI (default), macOS uses the Keychain (default),
    /// Linux prefers libsecret via <c>WithLinuxKeyring</c> and only falls
    /// back to unprotected file storage when the keyring is unavailable.
    /// </summary>
    private static async Task<MsalCacheHelper> CreatePlatformCacheHelperAsync(bool warnOnFailure)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            // Prefer Secret Service / GNOME Keyring. Fall back to
            // unprotected file only when the keyring is unavailable.
            try
            {
                var keyringProps = new StorageCreationPropertiesBuilder(
                        ProfilePaths.TokenCacheFileName,
                        ProfilePaths.DataDirectory)
                    .WithLinuxKeyring(
                        schemaName: "com.ppds.tokencache",
                        collection: MsalCacheHelper.LinuxKeyRingDefaultCollection,
                        secretLabel: "PPDS MSAL token cache",
                        attribute1: new System.Collections.Generic.KeyValuePair<string, string>("Version", "1"),
                        attribute2: new System.Collections.Generic.KeyValuePair<string, string>("Product", "PPDS"))
                    .Build();

                var helper = await MsalCacheHelper.CreateAsync(keyringProps).ConfigureAwait(false);
                helper.VerifyPersistence();
                AuthDebugLog.WriteLine("[MsalCache] Using Linux keyring (libsecret) for token cache.");
                return helper;
            }
            catch (MsalCachePersistenceException ex)
            {
                AuthDebugLog.WriteLine($"[MsalCache] Linux keyring unavailable ({ex.Message}); falling back to unprotected file.");
                if (warnOnFailure)
                {
                    AuthenticationOutput.WriteLine(
                        "Warning: Linux keyring (libsecret) unavailable. Token cache will be stored unprotected on disk. " +
                        "Install libsecret-1-0 / gnome-keyring to enable encrypted storage.");
                }

                var fallbackProps = new StorageCreationPropertiesBuilder(
                        ProfilePaths.TokenCacheFileName,
                        ProfilePaths.DataDirectory)
                    .WithUnprotectedFile()
                    .Build();

                // Pre-create the fallback file at 0600 BEFORE MSAL writes any
                // tokens to it. MsalCacheHelper.CreateAsync does not touch
                // disk — the first write happens on the first auth cycle.
                // Without pre-creation, the ClampLinuxFallbackFileMode call
                // below would find no file and skip, leaving first-run
                // tokens exposed at the system umask (often 0644) until a
                // second launch triggered another chmod attempt.
                EnsureLinuxFallbackFileAtTightMode(ProfilePaths.TokenCacheFile);

                var fallbackHelper = await MsalCacheHelper.CreateAsync(fallbackProps).ConfigureAwait(false);
                // Belt-and-braces: re-clamp after helper init in case MSAL
                // touched the file during initialization. No-op if mode is
                // already 0600.
                ClampLinuxFallbackFileMode(ProfilePaths.TokenCacheFile);
                return fallbackHelper;
            }
        }

        // Windows (DPAPI) and macOS (Keychain) - MSAL selects the
        // appropriate platform-native store by default when neither
        // WithLinuxKeyring nor WithUnprotectedFile is specified.
        var defaultProps = new StorageCreationPropertiesBuilder(
                ProfilePaths.TokenCacheFileName,
                ProfilePaths.DataDirectory)
            .Build();
        return await MsalCacheHelper.CreateAsync(defaultProps).ConfigureAwait(false);
    }

    /// <summary>
    /// Ensures the Linux fallback token-cache file exists at mode 0600 BEFORE
    /// MSAL writes any tokens to it. Creates an empty file if missing, then
    /// applies the mode. POSIX preserves file mode across truncate-writes,
    /// so MSAL's subsequent token writes inherit owner-only visibility.
    /// Best-effort — failures logged but never thrown so cache creation never
    /// fails solely due to a filesystem hiccup.
    /// </summary>
    /// <param name="path">The unprotected cache file path.</param>
    private static void EnsureLinuxFallbackFileAtTightMode(string path)
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        try
        {
            var dir = System.IO.Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir))
            {
                System.IO.Directory.CreateDirectory(dir);
            }
            if (!System.IO.File.Exists(path))
            {
                // Atomic create-with-mode: FileStreamOptions.UnixCreateMode
                // (NET 7+) sets the inode permissions at creation time, so
                // there is no window where the file exists at the system
                // umask before being clamped. Closes the File.Create →
                // SetUnixFileMode race that an earlier version of this
                // code had (Gemini review #3107844xxx).
                var options = new System.IO.FileStreamOptions
                {
                    Mode = System.IO.FileMode.CreateNew,
                    Access = System.IO.FileAccess.Write,
                    UnixCreateMode = System.IO.UnixFileMode.UserRead | System.IO.UnixFileMode.UserWrite,
                };
                using (System.IO.File.Open(path, options)) { }
            }
            // Belt-and-braces clamp in case the file existed at a wider mode
            // (e.g. carried over from a previous version that used the
            // racy File.Create pattern).
            System.IO.File.SetUnixFileMode(
                path,
                System.IO.UnixFileMode.UserRead | System.IO.UnixFileMode.UserWrite);
            AuthDebugLog.WriteLine($"[MsalCache] Ensured fallback file at 0600: {path}");
        }
        catch (Exception ex)
        {
            AuthDebugLog.WriteLine($"[MsalCache] Failed to ensure fallback file mode ({ex.GetType().Name}): {ex.Message}");
        }
    }

    /// <summary>
    /// Clamps the Linux unprotected-file fallback to mode 0600 (owner read/write
    /// only). Best-effort — swallows exceptions so cache creation never fails
    /// solely due to a chmod hiccup, but logs the failure for diagnosis.
    /// </summary>
    /// <param name="path">The unprotected cache file path.</param>
    private static void ClampLinuxFallbackFileMode(string path)
    {
        // Belt-and-braces: caller already gates on Linux, but keep guard so this
        // helper is safe if reused elsewhere.
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        try
        {
            if (System.IO.File.Exists(path))
            {
                System.IO.File.SetUnixFileMode(
                    path,
                    System.IO.UnixFileMode.UserRead | System.IO.UnixFileMode.UserWrite);
                AuthDebugLog.WriteLine($"[MsalCache] Clamped fallback file mode to 0600: {path}");
            }
        }
        catch (Exception ex)
        {
            AuthDebugLog.WriteLine($"[MsalCache] Failed to clamp fallback file mode ({ex.GetType().Name}): {ex.Message}");
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

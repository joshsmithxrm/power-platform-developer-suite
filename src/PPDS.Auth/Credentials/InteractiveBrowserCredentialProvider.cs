using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Identity.Client;
using Microsoft.Identity.Client.Extensions.Msal;
using Microsoft.PowerPlatform.Dataverse.Client;
using Microsoft.PowerPlatform.Dataverse.Client.Model;
using PPDS.Auth.Cloud;
using PPDS.Auth.Profiles;

namespace PPDS.Auth.Credentials;

/// <summary>
/// Provides authentication using interactive browser flow.
/// Automatically opens the system browser for user sign-in.
/// </summary>
public sealed class InteractiveBrowserCredentialProvider : ICredentialProvider
{
    private readonly CloudEnvironment _cloud;
    private readonly string? _tenantId;
    private readonly string? _username;
    private readonly string? _homeAccountId;
    private readonly Action<DeviceCodeInfo>? _deviceCodeCallback;
    private readonly Func<Action<DeviceCodeInfo>?, PreAuthDialogResult>? _beforeInteractiveAuth;

    private IPublicClientApplication? _msalClient;
    private MsalCacheHelper? _cacheHelper;
    private AuthenticationResult? _cachedResult;
    private string? _cachedResultUrl;
    private bool _disposed;

    /// <inheritdoc />
    public AuthMethod AuthMethod => AuthMethod.InteractiveBrowser;

    /// <inheritdoc />
    public string? Identity => _cachedResult?.Account?.Username;

    /// <inheritdoc />
    public DateTimeOffset? TokenExpiresAt => _cachedResult?.ExpiresOn;

    /// <inheritdoc />
    public string? TenantId => _cachedResult?.TenantId;

    /// <inheritdoc />
    public string? ObjectId => _cachedResult?.UniqueId;

    /// <inheritdoc />
    public string? HomeAccountId => _cachedResult?.Account?.HomeAccountId?.Identifier;

    /// <inheritdoc />
    public string? AccessToken => _cachedResult?.AccessToken;

    /// <inheritdoc />
    public System.Security.Claims.ClaimsPrincipal? IdTokenClaims => _cachedResult?.ClaimsPrincipal;

    /// <summary>
    /// Creates a new interactive browser credential provider.
    /// </summary>
    /// <param name="cloud">The cloud environment.</param>
    /// <param name="tenantId">Optional tenant ID (defaults to "organizations" for multi-tenant).</param>
    /// <param name="username">Optional username for silent auth lookup.</param>
    /// <param name="homeAccountId">Optional MSAL home account identifier for precise account lookup.</param>
    /// <param name="deviceCodeCallback">Optional callback for device code display (used for device code fallback).</param>
    /// <param name="beforeInteractiveAuth">Optional callback invoked before opening browser for auth.
    /// Returns the user's choice (OpenBrowser, UseDeviceCode, or Cancel).
    /// The callback receives a device code callback to use if device code is selected.</param>
    public InteractiveBrowserCredentialProvider(
        CloudEnvironment cloud = CloudEnvironment.Public,
        string? tenantId = null,
        string? username = null,
        string? homeAccountId = null,
        Action<DeviceCodeInfo>? deviceCodeCallback = null,
        Func<Action<DeviceCodeInfo>?, PreAuthDialogResult>? beforeInteractiveAuth = null)
    {
        _cloud = cloud;
        _tenantId = tenantId;
        _username = username;
        _homeAccountId = homeAccountId;
        _deviceCodeCallback = deviceCodeCallback;
        _beforeInteractiveAuth = beforeInteractiveAuth;
    }

    /// <summary>
    /// Creates a provider from an auth profile.
    /// </summary>
    /// <param name="profile">The auth profile.</param>
    /// <param name="deviceCodeCallback">Optional callback for device code display (used for device code fallback).</param>
    /// <param name="beforeInteractiveAuth">Optional callback invoked before opening browser for auth.
    /// Returns the user's choice (OpenBrowser, UseDeviceCode, or Cancel).</param>
    /// <returns>A new provider instance.</returns>
    public static InteractiveBrowserCredentialProvider FromProfile(
        AuthProfile profile,
        Action<DeviceCodeInfo>? deviceCodeCallback = null,
        Func<Action<DeviceCodeInfo>?, PreAuthDialogResult>? beforeInteractiveAuth = null)
    {
        return new InteractiveBrowserCredentialProvider(
            profile.Cloud,
            profile.TenantId,
            profile.Username,
            profile.HomeAccountId,
            deviceCodeCallback,
            beforeInteractiveAuth);
    }

    /// <summary>
    /// Checks if interactive browser authentication is available.
    /// Returns false for headless environments (SSH, containers, no display).
    /// </summary>
    public static bool IsAvailable()
    {
        // Check for SSH session
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("SSH_CLIENT")) ||
            !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("SSH_TTY")))
        {
            return false;
        }

        // Check for CI/CD environments
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("CI")) ||
            !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("TF_BUILD")) ||
            !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("GITHUB_ACTIONS")))
        {
            return false;
        }

        // Check for container environment
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER")))
        {
            return false;
        }

        // On Linux, check for DISPLAY (X11) or WAYLAND_DISPLAY
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            var hasDisplay = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISPLAY")) ||
                             !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY"));
            return hasDisplay;
        }

        // Windows and macOS typically have a display
        return true;
    }

    /// <inheritdoc />
    public async Task<ServiceClient> CreateServiceClientAsync(
        string environmentUrl,
        CancellationToken cancellationToken = default,
        bool forceInteractive = false)
    {
        if (string.IsNullOrWhiteSpace(environmentUrl))
            throw new ArgumentNullException(nameof(environmentUrl));

        // Normalize URL
        environmentUrl = environmentUrl.TrimEnd('/');

        // Ensure MSAL client is initialized
        await EnsureMsalClientInitializedAsync().ConfigureAwait(false);

        // Get token and prime the cache (may prompt user for interactive auth)
        await GetTokenAsync(environmentUrl, forceInteractive, cancellationToken).ConfigureAwait(false);

        // Create ServiceClient using ConnectionOptions.
        // The provider function uses cached tokens and refreshes silently when needed.
        var options = new ConnectionOptions
        {
            ServiceUri = new Uri(environmentUrl),
            AccessTokenProviderFunctionAsync = _ => GetTokenAsync(environmentUrl, forceInteractive: false, CancellationToken.None)
        };
        var client = new ServiceClient(options);

        try
        {
            // Force org metadata discovery before client is cloned by pool.
            // ServiceClient uses lazy initialization - properties like ConnectedOrgFriendlyName
            // are only populated when first accessed. The connection pool clones clients before
            // properties are accessed, so clones would have empty metadata.
            // Skip for globaldisco - it's the discovery service, not an actual org.
            if (!environmentUrl.Contains("globaldisco", StringComparison.OrdinalIgnoreCase))
            {
                _ = client.ConnectedOrgFriendlyName;
            }

            if (!client.IsReady)
            {
                var error = client.LastError ?? "Unknown error";
                throw new AuthenticationException($"Failed to connect to Dataverse: {error}");
            }

            return client;
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Gets an access token for the specified Dataverse URL.
    /// </summary>
    /// <param name="environmentUrl">The environment URL.</param>
    /// <param name="forceInteractive">If true, skip silent auth and prompt user directly.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    private async Task<string> GetTokenAsync(string environmentUrl, bool forceInteractive, CancellationToken cancellationToken)
    {
        var scopes = new[] { $"{environmentUrl}/.default" };
        AuthDebugLog.WriteLine($"GetTokenAsync: url={environmentUrl}, forceInteractive={forceInteractive}");

        // For profile creation, skip silent auth and go straight to interactive
        if (!forceInteractive)
        {
            // Try to get token silently from cache first (must match target URL to avoid scope mismatch)
            if (_cachedResult != null
                && _cachedResult.ExpiresOn > DateTimeOffset.UtcNow.AddMinutes(5)
                && string.Equals(_cachedResultUrl, environmentUrl, StringComparison.OrdinalIgnoreCase))
            {
                AuthDebugLog.WriteLine("  Using in-memory cached token (expires " + _cachedResult.ExpiresOn.ToString("HH:mm:ss") + ")");
                return _cachedResult.AccessToken;
            }

            AuthDebugLog.WriteLine("  In-memory cache miss or expired, attempting silent acquisition...");

            // Try to find the correct account for silent acquisition
            var account = await FindAccountAsync().ConfigureAwait(false);

            if (account != null)
            {
                AuthDebugLog.WriteLine($"  Found account for silent auth: {account.Username}");
                try
                {
                    _cachedResult = await _msalClient!
                        .AcquireTokenSilent(scopes, account)
                        .ExecuteAsync(cancellationToken)
                        .ConfigureAwait(false);
                    _cachedResultUrl = environmentUrl;
                    AuthDebugLog.WriteLine("  Silent acquisition SUCCEEDED");
                    return _cachedResult.AccessToken;
                }
                catch (MsalUiRequiredException ex)
                {
                    // Silent acquisition failed, need interactive
                    AuthDebugLog.WriteLine($"  Silent acquisition FAILED: MsalUiRequiredException - {ex.Message}");
                }
            }
            else
            {
                AuthDebugLog.WriteLine("  No account found for silent auth - skipping to interactive");
            }
        }

        // Interactive browser authentication
        AuthDebugLog.WriteLine("  Starting interactive browser authentication...");

        // Invoke callback before opening browser (allows TUI to show dialog)
        if (_beforeInteractiveAuth != null)
        {
            var dialogResult = _beforeInteractiveAuth(_deviceCodeCallback);
            AuthDebugLog.WriteLine($"  Pre-auth dialog result: {dialogResult}");

            switch (dialogResult)
            {
                case PreAuthDialogResult.Cancel:
                    throw new OperationCanceledException("Authentication was declined by the user.");

                case PreAuthDialogResult.UseDeviceCode:
                    AuthDebugLog.WriteLine("  User selected device code fallback");
                    return await GetTokenViaDeviceCodeAsync(environmentUrl, cancellationToken)
                        .ConfigureAwait(false);

                case PreAuthDialogResult.OpenBrowser:
                    // Continue with browser auth below
                    break;
            }
        }

        AuthenticationOutput.WriteLine();
        AuthenticationOutput.WriteLine("Opening browser for authentication...");

        try
        {
            _cachedResult = await _msalClient!
                .AcquireTokenInteractive(scopes)
                .WithUseEmbeddedWebView(false) // Use system browser
                .WithPrompt(Microsoft.Identity.Client.Prompt.SelectAccount) // Always show account picker
                .ExecuteAsync(cancellationToken)
                .ConfigureAwait(false);
            _cachedResultUrl = environmentUrl;
        }
        catch (MsalClientException ex) when (ex.ErrorCode == "authentication_canceled")
        {
            throw new OperationCanceledException("Authentication was canceled by the user.", ex);
        }

        AuthenticationOutput.WriteLine($"Authenticated as: {_cachedResult.Account.Username}");
        AuthenticationOutput.WriteLine();

        return _cachedResult.AccessToken;
    }

    /// <summary>
    /// Gets a token using device code flow as a fallback.
    /// Called when user selects "Use Device Code" in the pre-auth dialog.
    /// </summary>
    /// <param name="environmentUrl">The environment URL.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    private async Task<string> GetTokenViaDeviceCodeAsync(string environmentUrl, CancellationToken cancellationToken)
    {
        if (_deviceCodeCallback == null)
        {
            throw new InvalidOperationException(
                "Device code fallback requested but no device code callback was provided.");
        }

        var scopes = new[] { $"{environmentUrl}/.default" };

        AuthDebugLog.WriteLine("  Starting device code authentication (fallback)...");

        // Use device code flow with the provided callback
        _cachedResult = await _msalClient!
            .AcquireTokenWithDeviceCode(scopes, deviceCodeResult =>
            {
                // Convert MSAL's DeviceCodeResult to our DeviceCodeInfo
                var info = new DeviceCodeInfo(
                    deviceCodeResult.UserCode,
                    deviceCodeResult.VerificationUrl,
                    deviceCodeResult.Message);

                // Invoke the callback to display the code to the user
                _deviceCodeCallback(info);

                return Task.CompletedTask;
            })
            .ExecuteAsync(cancellationToken)
            .ConfigureAwait(false);
        _cachedResultUrl = environmentUrl;

        AuthDebugLog.WriteLine($"  Device code authentication succeeded: {_cachedResult.Account.Username}");
        AuthenticationOutput.WriteLine($"Authenticated as: {_cachedResult.Account.Username}");
        AuthenticationOutput.WriteLine();

        return _cachedResult.AccessToken;
    }

    /// <summary>
    /// Finds the correct cached account for this profile.
    /// </summary>
    private Task<IAccount?> FindAccountAsync()
        => MsalAccountHelper.FindAccountAsync(_msalClient!, _homeAccountId, _tenantId, _username);

    /// <summary>
    /// Ensures the MSAL client is initialized with token cache.
    /// </summary>
    private async Task EnsureMsalClientInitializedAsync()
    {
        if (_msalClient != null)
            return;

        _msalClient = MsalClientBuilder.CreateClient(_cloud, _tenantId, MsalClientBuilder.RedirectUriOption.Localhost);
        _cacheHelper = await MsalClientBuilder.CreateAndRegisterCacheAsync(_msalClient).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<CachedTokenInfo?> GetCachedTokenInfoAsync(
        string environmentUrl,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(environmentUrl))
            return null;

        environmentUrl = environmentUrl.TrimEnd('/');
        var scopes = new[] { $"{environmentUrl}/.default" };

        AuthDebugLog.WriteLine($"[InteractiveBrowser] GetCachedTokenInfoAsync: url={environmentUrl}");

        // Initialize MSAL client to load persistent cache
        await EnsureMsalClientInitializedAsync().ConfigureAwait(false);

        // Check in-memory cache first (must match target URL)
        if (_cachedResult != null
            && string.Equals(_cachedResultUrl, environmentUrl, StringComparison.OrdinalIgnoreCase))
        {
            AuthDebugLog.WriteLine($"  In-memory cache has token expiring at {_cachedResult.ExpiresOn:HH:mm:ss}");
            return CachedTokenInfo.Create(_cachedResult.ExpiresOn, _cachedResult.Account?.Username);
        }

        // Try to find account in persistent cache
        var account = await FindAccountAsync().ConfigureAwait(false);
        if (account == null)
        {
            AuthDebugLog.WriteLine("  No cached account found");
            return null;
        }

        AuthDebugLog.WriteLine($"  Found cached account: {account.Username}");

        try
        {
            // Try silent acquisition - this will use cached tokens if available
            // WithForceRefresh(false) ensures we only check cache, don't refresh
            var result = await _msalClient!
                .AcquireTokenSilent(scopes, account)
                .WithForceRefresh(false)
                .ExecuteAsync(cancellationToken)
                .ConfigureAwait(false);

            // Update in-memory cache
            _cachedResult = result;
            _cachedResultUrl = environmentUrl;

            AuthDebugLog.WriteLine($"  Silent acquisition returned token expiring at {result.ExpiresOn:HH:mm:ss}");
            return CachedTokenInfo.Create(result.ExpiresOn, result.Account?.Username);
        }
        catch (MsalUiRequiredException ex)
        {
            // Token is expired or requires user interaction
            AuthDebugLog.WriteLine($"  Token requires re-authentication: {ex.Message}");
            return null;
        }
        catch (MsalServiceException ex)
        {
            // Service error (network, etc.) - can't determine token state
            AuthDebugLog.WriteLine($"  Service error checking token: {ex.Message}");
            return null;
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
            return;

        MsalClientBuilder.UnregisterCache(_cacheHelper, _msalClient);
        _disposed = true;
    }
}

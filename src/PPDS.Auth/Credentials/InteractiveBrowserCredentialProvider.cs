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

    private IPublicClientApplication? _msalClient;
    private MsalCacheHelper? _cacheHelper;
    private AuthenticationResult? _cachedResult;
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
    public InteractiveBrowserCredentialProvider(
        CloudEnvironment cloud = CloudEnvironment.Public,
        string? tenantId = null,
        string? username = null,
        string? homeAccountId = null)
    {
        _cloud = cloud;
        _tenantId = tenantId;
        _username = username;
        _homeAccountId = homeAccountId;
    }

    /// <summary>
    /// Creates a provider from an auth profile.
    /// </summary>
    /// <param name="profile">The auth profile.</param>
    /// <returns>A new provider instance.</returns>
    public static InteractiveBrowserCredentialProvider FromProfile(AuthProfile profile)
    {
        return new InteractiveBrowserCredentialProvider(
            profile.Cloud,
            profile.TenantId,
            profile.Username,
            profile.HomeAccountId);
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

        // Create ServiceClient using ConnectionOptions to ensure org metadata discovery.
        // The provider function uses cached tokens and refreshes silently when needed.
        // SkipDiscovery = false forces org metadata population (ConnectedOrgFriendlyName, etc.)
        var options = new ConnectionOptions
        {
            ServiceUri = new Uri(environmentUrl),
            AccessTokenProviderFunctionAsync = _ => GetTokenAsync(environmentUrl, forceInteractive: false, CancellationToken.None),
            SkipDiscovery = false
        };
        var client = new ServiceClient(options);

        // Force org metadata discovery before client is cloned by pool.
        // Discovery is lazy - accessing a property triggers it.
        _ = client.ConnectedOrgFriendlyName;

        if (!client.IsReady)
        {
            var error = client.LastError ?? "Unknown error";
            client.Dispose();
            throw new AuthenticationException($"Failed to connect to Dataverse: {error}");
        }

        return client;
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

        // For profile creation, skip silent auth and go straight to interactive
        if (!forceInteractive)
        {
            // Try to get token silently from cache first
            if (_cachedResult != null && _cachedResult.ExpiresOn > DateTimeOffset.UtcNow.AddMinutes(5))
            {
                return _cachedResult.AccessToken;
            }

            // Try to find the correct account for silent acquisition
            var account = await FindAccountAsync().ConfigureAwait(false);

            if (account != null)
            {
                try
                {
                    _cachedResult = await _msalClient!
                        .AcquireTokenSilent(scopes, account)
                        .ExecuteAsync(cancellationToken)
                        .ConfigureAwait(false);
                    return _cachedResult.AccessToken;
                }
                catch (MsalUiRequiredException)
                {
                    // Silent acquisition failed, need interactive
                }
            }
        }

        // Interactive browser authentication
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
    public void Dispose()
    {
        if (_disposed)
            return;

        MsalClientBuilder.UnregisterCache(_cacheHelper, _msalClient);
        _disposed = true;
    }
}

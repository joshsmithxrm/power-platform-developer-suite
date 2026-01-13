using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Identity.Client;
using Microsoft.Identity.Client.Extensions.Msal;
using Microsoft.PowerPlatform.Dataverse.Client;
using PPDS.Auth.Cloud;
using PPDS.Auth.Profiles;

namespace PPDS.Auth.Credentials;

/// <summary>
/// Provides authentication using username and password (ROPC flow).
/// </summary>
public sealed class UsernamePasswordCredentialProvider : ICredentialProvider
{
    private readonly CloudEnvironment _cloud;
    private readonly string? _tenantId;
    private readonly string _username;
    private readonly string _password;

    private IPublicClientApplication? _msalClient;
    private MsalCacheHelper? _cacheHelper;
    private AuthenticationResult? _cachedResult;
    private bool _disposed;

    /// <inheritdoc />
    public AuthMethod AuthMethod => AuthMethod.UsernamePassword;

    /// <inheritdoc />
    public string? Identity => _cachedResult?.Account?.Username ?? _username;

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
    /// Creates a new username/password credential provider.
    /// </summary>
    /// <param name="username">The username.</param>
    /// <param name="password">The password.</param>
    /// <param name="cloud">The cloud environment.</param>
    /// <param name="tenantId">Optional tenant ID.</param>
    public UsernamePasswordCredentialProvider(
        string username,
        string password,
        CloudEnvironment cloud = CloudEnvironment.Public,
        string? tenantId = null)
    {
        _username = username ?? throw new ArgumentNullException(nameof(username));
        _password = password ?? throw new ArgumentNullException(nameof(password));
        _cloud = cloud;
        _tenantId = tenantId;
    }

    /// <inheritdoc />
    public async Task<ServiceClient> CreateServiceClientAsync(
        string environmentUrl,
        CancellationToken cancellationToken = default,
        bool forceInteractive = false)
    {
        if (string.IsNullOrWhiteSpace(environmentUrl))
            throw new ArgumentNullException(nameof(environmentUrl));

        environmentUrl = environmentUrl.TrimEnd('/');

        await EnsureMsalClientInitializedAsync().ConfigureAwait(false);

        var token = await GetTokenAsync(environmentUrl, cancellationToken).ConfigureAwait(false);

        var client = new ServiceClient(
            new Uri(environmentUrl),
            _ => Task.FromResult(token),
            useUniqueInstance: true);

        if (!client.IsReady)
        {
            var error = client.LastError ?? "Unknown error";
            client.Dispose();
            throw new AuthenticationException($"Failed to connect to Dataverse: {error}");
        }

        return client;
    }

    private async Task<string> GetTokenAsync(string environmentUrl, CancellationToken cancellationToken)
    {
        var scopes = new[] { $"{environmentUrl}/.default" };

        try
        {
            _cachedResult = await _msalClient!
                .AcquireTokenByUsernamePassword(scopes, _username, _password)
                .ExecuteAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        catch (MsalUiRequiredException ex)
        {
            throw new AuthenticationException(
                "Username/password authentication failed. This may be due to MFA requirements or conditional access policies.", ex);
        }
        catch (MsalServiceException ex)
        {
            throw new AuthenticationException($"Authentication failed: {ex.Message}", ex);
        }

        return _cachedResult.AccessToken;
    }

    private async Task EnsureMsalClientInitializedAsync()
    {
        if (_msalClient != null)
            return;

        _msalClient = MsalClientBuilder.CreateClient(_cloud, _tenantId, MsalClientBuilder.RedirectUriOption.None);
        _cacheHelper = await MsalClientBuilder.CreateAndRegisterCacheAsync(_msalClient, warnOnFailure: false).ConfigureAwait(false);
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

        await EnsureMsalClientInitializedAsync().ConfigureAwait(false);

        // Check in-memory cache first
        if (_cachedResult != null)
        {
            return CachedTokenInfo.Create(_cachedResult.ExpiresOn, _cachedResult.Account?.Username ?? _username);
        }

        // Try to find account in persistent cache
        var accounts = await _msalClient!.GetAccountsAsync().ConfigureAwait(false);
        var account = accounts.FirstOrDefault(a =>
            string.Equals(a.Username, _username, StringComparison.OrdinalIgnoreCase));

        if (account == null)
            return null;

        try
        {
            var result = await _msalClient!
                .AcquireTokenSilent(scopes, account)
                .WithForceRefresh(false)
                .ExecuteAsync(cancellationToken)
                .ConfigureAwait(false);

            _cachedResult = result;
            return CachedTokenInfo.Create(result.ExpiresOn, result.Account?.Username ?? _username);
        }
        catch (MsalUiRequiredException)
        {
            return null;
        }
        catch (MsalServiceException)
        {
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

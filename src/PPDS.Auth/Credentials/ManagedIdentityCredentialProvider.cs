using System;
using System.Threading;
using System.Threading.Tasks;
using Azure.Core;
using Azure.Identity;
using Microsoft.PowerPlatform.Dataverse.Client;
using Microsoft.PowerPlatform.Dataverse.Client.Model;
using PPDS.Auth.Profiles;

namespace PPDS.Auth.Credentials;

/// <summary>
/// Provides authentication using Azure Managed Identity.
/// </summary>
/// <remarks>
/// Supports both system-assigned and user-assigned managed identities.
/// Only works when running in an Azure environment (VM, App Service, Functions, AKS, etc.).
/// </remarks>
public sealed class ManagedIdentityCredentialProvider : ICredentialProvider
{
    private readonly string? _clientId;
    private readonly ManagedIdentityCredential _credential;

    private AccessToken? _cachedToken;
    private bool _disposed;

    /// <inheritdoc />
    public AuthMethod AuthMethod => AuthMethod.ManagedIdentity;

    /// <inheritdoc />
    public string? Identity => string.IsNullOrEmpty(_clientId)
        ? "(system-assigned)"
        : $"app:{_clientId[..Math.Min(8, _clientId.Length)]}...";

    /// <inheritdoc />
    public DateTimeOffset? TokenExpiresAt => _cachedToken?.ExpiresOn;

    /// <inheritdoc />
    public string? TenantId => null; // Managed identity doesn't expose tenant

    /// <inheritdoc />
    public string? ObjectId => null; // Managed identity doesn't expose OID

    /// <inheritdoc />
    public string? HomeAccountId => null; // Managed identity doesn't use MSAL user cache

    /// <inheritdoc />
    public string? AccessToken => _cachedToken?.Token;

    /// <inheritdoc />
    public System.Security.Claims.ClaimsPrincipal? IdTokenClaims => null;

    /// <summary>
    /// Creates a new managed identity credential provider.
    /// </summary>
    /// <param name="clientId">
    /// Optional client ID for user-assigned managed identity.
    /// Leave null for system-assigned managed identity.
    /// </param>
    public ManagedIdentityCredentialProvider(string? clientId = null)
    {
        _clientId = clientId;
        _credential = string.IsNullOrEmpty(clientId)
            ? new ManagedIdentityCredential()
            : new ManagedIdentityCredential(clientId);
    }

    /// <summary>
    /// Creates a provider from an auth profile.
    /// </summary>
    /// <param name="profile">The auth profile.</param>
    /// <returns>A new provider instance.</returns>
    public static ManagedIdentityCredentialProvider FromProfile(AuthProfile profile)
    {
        if (profile.AuthMethod != AuthMethod.ManagedIdentity)
            throw new ArgumentException($"Profile auth method must be ManagedIdentity, got {profile.AuthMethod}", nameof(profile));

        // ApplicationId is optional - null means system-assigned identity
        return new ManagedIdentityCredentialProvider(profile.ApplicationId);
    }

    /// <inheritdoc />
    public async Task<ServiceClient> CreateServiceClientAsync(
        string environmentUrl,
        CancellationToken cancellationToken = default,
        bool forceInteractive = false) // Ignored for managed identity
    {
        if (string.IsNullOrWhiteSpace(environmentUrl))
            throw new ArgumentNullException(nameof(environmentUrl));

        // Normalize URL
        environmentUrl = environmentUrl.TrimEnd('/');

        // Create ServiceClient using ConnectionOptions.
        // The provider function acquires tokens on demand and refreshes when needed.
        ServiceClient client;
        try
        {
            var options = new ConnectionOptions
            {
                ServiceUri = new Uri(environmentUrl),
                AccessTokenProviderFunctionAsync = _ => GetTokenAsync(environmentUrl, CancellationToken.None)
            };
            client = new ServiceClient(options);

            // Force org metadata discovery before client is cloned by pool.
            // ServiceClient uses lazy initialization - properties like ConnectedOrgFriendlyName
            // are only populated when first accessed. The connection pool clones clients before
            // properties are accessed, so clones would have empty metadata.
            // PAC CLI uses the same pattern: immediately accessing properties after Connect().
            _ = client.ConnectedOrgFriendlyName;
        }
        catch (Exception ex)
        {
            throw new AuthenticationException($"Failed to create ServiceClient: {ex.Message}", ex);
        }

        if (!client.IsReady)
        {
            var error = client.LastError ?? "Unknown error";
            client.Dispose();
            throw new AuthenticationException($"Failed to connect to Dataverse: {error}");
        }

        return client;
    }

    /// <summary>
    /// Gets an access token for the Dataverse environment.
    /// </summary>
    private async Task<string> GetTokenAsync(string environmentUrl, CancellationToken cancellationToken)
    {
        // Check if we have a valid cached token
        if (_cachedToken.HasValue && _cachedToken.Value.ExpiresOn > DateTimeOffset.UtcNow.AddMinutes(5))
        {
            return _cachedToken.Value.Token;
        }

        // Request new token
        var scope = $"{environmentUrl}/.default";
        var context = new TokenRequestContext(new[] { scope });

        try
        {
            _cachedToken = await _credential.GetTokenAsync(context, cancellationToken).ConfigureAwait(false);
            return _cachedToken.Value.Token;
        }
        catch (CredentialUnavailableException ex)
        {
            throw new AuthenticationException(
                "Managed identity is not available in this environment. " +
                "Managed identity only works when running in Azure (VM, App Service, Functions, AKS, etc.). " +
                $"Error: {ex.Message}", ex);
        }
        catch (AuthenticationFailedException ex)
        {
            throw new AuthenticationException($"Managed identity authentication failed: {ex.Message}", ex);
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
    }
}

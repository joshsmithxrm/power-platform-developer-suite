using System;
using System.Threading;
using System.Threading.Tasks;
using Azure.Core;
using Azure.Identity;
using Microsoft.PowerPlatform.Dataverse.Client;
using PPDS.Auth.Cloud;
using PPDS.Auth.Profiles;

namespace PPDS.Auth.Credentials;

/// <summary>
/// Provides authentication using Azure DevOps OIDC (workload identity federation).
/// For use in Azure DevOps CI/CD pipelines.
/// </summary>
public sealed class AzureDevOpsFederatedCredentialProvider : ICredentialProvider
{
    private readonly string _applicationId;
    private readonly string _tenantId;
    private readonly CloudEnvironment _cloud;

    private TokenCredential? _credential;
    private AccessToken? _cachedToken;
    private bool _disposed;

    /// <inheritdoc />
    public AuthMethod AuthMethod => AuthMethod.AzureDevOpsFederated;

    /// <inheritdoc />
    public string? Identity => $"app:{_applicationId[..Math.Min(8, _applicationId.Length)]}...";

    /// <inheritdoc />
    public DateTimeOffset? TokenExpiresAt => _cachedToken?.ExpiresOn;

    /// <inheritdoc />
    public string? TenantId => _tenantId;

    /// <inheritdoc />
    public string? ObjectId => null; // Not available for federated auth without additional calls

    /// <inheritdoc />
    public string? HomeAccountId => null; // Federated auth doesn't use MSAL user cache

    /// <inheritdoc />
    public string? AccessToken => _cachedToken?.Token;

    /// <inheritdoc />
    public System.Security.Claims.ClaimsPrincipal? IdTokenClaims => null;

    /// <summary>
    /// Creates a new Azure DevOps federated credential provider.
    /// </summary>
    /// <param name="applicationId">The application (client) ID.</param>
    /// <param name="tenantId">The tenant ID.</param>
    /// <param name="cloud">The cloud environment.</param>
    public AzureDevOpsFederatedCredentialProvider(
        string applicationId,
        string tenantId,
        CloudEnvironment cloud = CloudEnvironment.Public)
    {
        _applicationId = applicationId ?? throw new ArgumentNullException(nameof(applicationId));
        _tenantId = tenantId ?? throw new ArgumentNullException(nameof(tenantId));
        _cloud = cloud;
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

        EnsureCredentialInitialized();

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
        var context = new TokenRequestContext(scopes);

        try
        {
            _cachedToken = await _credential!.GetTokenAsync(context, cancellationToken).ConfigureAwait(false);
        }
        catch (AuthenticationFailedException ex)
        {
            throw new AuthenticationException(
                $"Azure DevOps federated authentication failed. Ensure SYSTEM_ACCESSTOKEN is available and the service connection is configured: {ex.Message}", ex);
        }

        return _cachedToken.Value.Token;
    }

    private void EnsureCredentialInitialized()
    {
        if (_credential != null)
            return;

        // Azure DevOps sets these environment variables
        var oidcToken = Environment.GetEnvironmentVariable("SYSTEM_OIDCREQUESTURI");
        var accessToken = Environment.GetEnvironmentVariable("SYSTEM_ACCESSTOKEN");

        if (string.IsNullOrEmpty(oidcToken) && string.IsNullOrEmpty(accessToken))
        {
            throw new AuthenticationException(
                "Azure DevOps pipeline environment not detected. " +
                "Ensure the pipeline has access to SYSTEM_ACCESSTOKEN and uses a workload identity federation service connection.");
        }

        var authorityHost = CloudEndpoints.GetAuthorityHost(_cloud);

        // Use AzurePipelinesCredential for Azure DevOps workload identity federation
        _credential = new AzurePipelinesCredential(
            _tenantId,
            _applicationId,
            Environment.GetEnvironmentVariable("AZURESUBSCRIPTION_SERVICE_CONNECTION_ID") ?? "",
            Environment.GetEnvironmentVariable("SYSTEM_OIDCREQUESTURI") ?? "",
            new AzurePipelinesCredentialOptions { AuthorityHost = authorityHost });
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
    }
}

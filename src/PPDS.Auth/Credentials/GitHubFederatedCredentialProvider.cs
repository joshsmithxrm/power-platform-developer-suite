using System;
using System.Threading;
using System.Threading.Tasks;
using Azure.Core;
using Azure.Identity;
using Microsoft.PowerPlatform.Dataverse.Client;
using Microsoft.PowerPlatform.Dataverse.Client.Model;
using PPDS.Auth.Cloud;
using PPDS.Auth.Profiles;

namespace PPDS.Auth.Credentials;

/// <summary>
/// Provides authentication using GitHub Actions OIDC (workload identity federation).
/// For use in GitHub Actions CI/CD pipelines.
/// </summary>
public sealed class GitHubFederatedCredentialProvider : ICredentialProvider
{
    private readonly string _applicationId;
    private readonly string _tenantId;
    private readonly CloudEnvironment _cloud;

    private TokenCredential? _credential;
    private AccessToken? _cachedToken;
    private bool _disposed;

    /// <inheritdoc />
    public AuthMethod AuthMethod => AuthMethod.GitHubFederated;

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
    /// Creates a new GitHub federated credential provider.
    /// </summary>
    /// <param name="applicationId">The application (client) ID.</param>
    /// <param name="tenantId">The tenant ID.</param>
    /// <param name="cloud">The cloud environment.</param>
    public GitHubFederatedCredentialProvider(
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

        // Get token and prime the cache (uses cancellationToken for cancellable first request)
        await GetTokenAsync(environmentUrl, cancellationToken).ConfigureAwait(false);

        // Create ServiceClient using ConnectionOptions.
        // The provider function uses cached tokens and refreshes when needed.
        var options = new ConnectionOptions
        {
            ServiceUri = new Uri(environmentUrl),
            AccessTokenProviderFunctionAsync = _ => GetTokenAsync(environmentUrl, CancellationToken.None)
        };
        var client = new ServiceClient(options);

        // Force org metadata discovery before client is cloned by pool.
        // ServiceClient uses lazy initialization - properties like ConnectedOrgFriendlyName
        // are only populated when first accessed. The connection pool clones clients before
        // properties are accessed, so clones would have empty metadata.
        _ = client.ConnectedOrgFriendlyName;

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
                $"GitHub federated authentication failed. Ensure ACTIONS_ID_TOKEN_REQUEST_URL and ACTIONS_ID_TOKEN_REQUEST_TOKEN are set: {ex.Message}", ex);
        }

        return _cachedToken.Value.Token;
    }

    private void EnsureCredentialInitialized()
    {
        if (_credential != null)
            return;

        // Get the GitHub OIDC token from environment
        var tokenUrl = Environment.GetEnvironmentVariable("ACTIONS_ID_TOKEN_REQUEST_URL");
        var tokenRequestToken = Environment.GetEnvironmentVariable("ACTIONS_ID_TOKEN_REQUEST_TOKEN");

        if (string.IsNullOrEmpty(tokenUrl) || string.IsNullOrEmpty(tokenRequestToken))
        {
            throw new AuthenticationException(
                "GitHub Actions OIDC environment not detected. " +
                "Ensure the workflow has 'id-token: write' permission and uses the azure/login action or similar.");
        }

        var authorityHost = CloudEndpoints.GetAuthorityHost(_cloud);

        // Use ClientAssertionCredential with GitHub OIDC
        _credential = new ClientAssertionCredential(
            _tenantId,
            _applicationId,
            async (token) =>
            {
                using var client = new System.Net.Http.HttpClient();
                client.DefaultRequestHeaders.Authorization =
                    new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", tokenRequestToken);

                var response = await client.GetAsync($"{tokenUrl}&audience=api://AzureADTokenExchange", token);
                response.EnsureSuccessStatusCode();

                var content = await response.Content.ReadAsStringAsync(token);
                var json = System.Text.Json.JsonDocument.Parse(content);
                return json.RootElement.GetProperty("value").GetString()!;
            },
            new ClientAssertionCredentialOptions { AuthorityHost = authorityHost });
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
    }
}

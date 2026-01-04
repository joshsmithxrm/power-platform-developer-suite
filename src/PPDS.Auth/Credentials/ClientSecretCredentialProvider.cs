using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.PowerPlatform.Dataverse.Client;
using PPDS.Auth.Cloud;
using PPDS.Auth.Profiles;

namespace PPDS.Auth.Credentials;

/// <summary>
/// Provides authentication using client ID and client secret (Service Principal).
/// </summary>
public sealed class ClientSecretCredentialProvider : ICredentialProvider
{
    /// <summary>
    /// Timeout for ServiceClient connection.
    /// Must fail fast so callers see actionable errors instead of hanging.
    /// </summary>
    private static readonly TimeSpan ConnectionTimeout = TimeSpan.FromSeconds(60);

    private readonly string _applicationId;
    private readonly string _clientSecret;
    private readonly string _tenantId;
    private readonly CloudEnvironment _cloud;

    private DateTimeOffset? _tokenExpiresAt;
    private bool _disposed;

    /// <inheritdoc />
    public AuthMethod AuthMethod => AuthMethod.ClientSecret;

    /// <inheritdoc />
    public string? Identity => _applicationId;

    /// <inheritdoc />
    public DateTimeOffset? TokenExpiresAt => _tokenExpiresAt;

    /// <inheritdoc />
    public string? TenantId => _tenantId;

    /// <inheritdoc />
    public string? ObjectId => null; // Service principals don't have a user OID

    /// <inheritdoc />
    public string? HomeAccountId => null; // Service principals don't use MSAL user cache

    /// <inheritdoc />
    public string? AccessToken => null; // Connection string auth doesn't expose the token

    /// <inheritdoc />
    public System.Security.Claims.ClaimsPrincipal? IdTokenClaims => null;

    /// <summary>
    /// Creates a new client secret credential provider.
    /// </summary>
    /// <param name="applicationId">The application (client) ID.</param>
    /// <param name="clientSecret">The client secret.</param>
    /// <param name="tenantId">The tenant ID.</param>
    /// <param name="cloud">The cloud environment.</param>
    public ClientSecretCredentialProvider(
        string applicationId,
        string clientSecret,
        string tenantId,
        CloudEnvironment cloud = CloudEnvironment.Public)
    {
        _applicationId = applicationId ?? throw new ArgumentNullException(nameof(applicationId));
        _clientSecret = clientSecret ?? throw new ArgumentNullException(nameof(clientSecret));
        _tenantId = tenantId ?? throw new ArgumentNullException(nameof(tenantId));
        _cloud = cloud;
    }

    /// <summary>
    /// Creates a provider from an auth profile and credential from secure store.
    /// </summary>
    /// <param name="profile">The auth profile.</param>
    /// <param name="credential">The credential containing the client secret.</param>
    /// <returns>A new provider instance.</returns>
    public static ClientSecretCredentialProvider FromProfile(AuthProfile profile, StoredCredential credential)
    {
        if (profile.AuthMethod != AuthMethod.ClientSecret)
            throw new ArgumentException($"Profile auth method must be ClientSecret, got {profile.AuthMethod}", nameof(profile));

        if (string.IsNullOrWhiteSpace(profile.ApplicationId))
            throw new ArgumentException("Profile ApplicationId is required", nameof(profile));

        if (string.IsNullOrWhiteSpace(profile.TenantId))
            throw new ArgumentException("Profile TenantId is required", nameof(profile));

        if (credential == null)
            throw new ArgumentNullException(nameof(credential));

        if (string.IsNullOrWhiteSpace(credential.ClientSecret))
            throw new ArgumentException("Credential ClientSecret is required", nameof(credential));

        return new ClientSecretCredentialProvider(
            profile.ApplicationId,
            credential.ClientSecret,
            profile.TenantId,
            profile.Cloud);
    }

    /// <summary>
    /// Creates a provider from an auth profile using an environment variable secret.
    /// </summary>
    /// <param name="profile">The auth profile.</param>
    /// <param name="clientSecret">The client secret (e.g., from PPDS_SPN_SECRET env var).</param>
    /// <returns>A new provider instance.</returns>
    public static ClientSecretCredentialProvider FromProfileWithSecret(AuthProfile profile, string clientSecret)
    {
        if (profile.AuthMethod != AuthMethod.ClientSecret)
            throw new ArgumentException($"Profile auth method must be ClientSecret, got {profile.AuthMethod}", nameof(profile));

        if (string.IsNullOrWhiteSpace(profile.ApplicationId))
            throw new ArgumentException("Profile ApplicationId is required", nameof(profile));

        if (string.IsNullOrWhiteSpace(profile.TenantId))
            throw new ArgumentException("Profile TenantId is required", nameof(profile));

        if (string.IsNullOrWhiteSpace(clientSecret))
            throw new ArgumentException("Client secret is required", nameof(clientSecret));

        return new ClientSecretCredentialProvider(
            profile.ApplicationId,
            clientSecret,
            profile.TenantId,
            profile.Cloud);
    }

    /// <inheritdoc />
    public async Task<ServiceClient> CreateServiceClientAsync(
        string environmentUrl,
        CancellationToken cancellationToken = default,
        bool forceInteractive = false) // Ignored for service principals
    {
        if (string.IsNullOrWhiteSpace(environmentUrl))
            throw new ArgumentNullException(nameof(environmentUrl));

        // Normalize URL
        environmentUrl = environmentUrl.TrimEnd('/');

        // Build connection string
        var connectionString = BuildConnectionString(environmentUrl);

        // Create ServiceClient - wrap in Task.Run since constructor is sync and can block.
        // Use internal timeout combined with caller's token to ensure we fail fast.
        // WaitAsync enforces the timeout even if ServiceClient constructor ignores cancellation.
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(ConnectionTimeout);

        ServiceClient client;
        try
        {
            client = await Task.Run(() => new ServiceClient(connectionString), timeoutCts.Token)
                .WaitAsync(timeoutCts.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Internal timeout expired - caller didn't cancel
            throw new AuthenticationException(
                $"Connection to Dataverse timed out after {ConnectionTimeout.TotalSeconds}s for {environmentUrl}. " +
                "Check network connectivity and environment URL.");
        }
        catch (OperationCanceledException)
        {
            // Caller cancelled
            throw new AuthenticationException(
                $"Connection to Dataverse was cancelled for {environmentUrl}.");
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

        // Estimate token expiration (typically 1 hour for client credentials)
        _tokenExpiresAt = DateTimeOffset.UtcNow.AddHours(1);

        return client;
    }

    /// <summary>
    /// Builds a connection string for the ServiceClient.
    /// </summary>
    private string BuildConnectionString(string environmentUrl)
    {
        var builder = new System.Text.StringBuilder();
        builder.Append("AuthType=ClientSecret;");
        builder.Append($"Url={environmentUrl};");
        builder.Append($"ClientId={_applicationId};");
        builder.Append($"ClientSecret={_clientSecret};");
        builder.Append($"TenantId={_tenantId};");

        // Add authority for non-public clouds
        if (_cloud != CloudEnvironment.Public)
        {
            var authority = CloudEndpoints.GetAuthorityUrl(_cloud, _tenantId);
            builder.Append($"Authority={authority};");
        }

        return builder.ToString().TrimEnd(';');
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
    }
}

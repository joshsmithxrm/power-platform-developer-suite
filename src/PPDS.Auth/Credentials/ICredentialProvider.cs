using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.PowerPlatform.Dataverse.Client;
using PPDS.Auth.Profiles;

namespace PPDS.Auth.Credentials;

/// <summary>
/// Provides authenticated ServiceClient instances for a specific auth method.
/// </summary>
public interface ICredentialProvider : IDisposable
{
    /// <summary>
    /// Gets the authentication method this provider handles.
    /// </summary>
    AuthMethod AuthMethod { get; }

    /// <summary>
    /// Creates an authenticated ServiceClient for the specified environment URL.
    /// </summary>
    /// <param name="environmentUrl">The Dataverse environment URL.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <param name="forceInteractive">If true, skip silent auth and prompt user directly. Use for profile creation.</param>
    /// <returns>An authenticated ServiceClient.</returns>
    /// <exception cref="AuthenticationException">If authentication fails.</exception>
    Task<ServiceClient> CreateServiceClientAsync(
        string environmentUrl,
        CancellationToken cancellationToken = default,
        bool forceInteractive = false);

    /// <summary>
    /// Gets the identity string for display (e.g., username or app ID).
    /// Available after successful authentication.
    /// </summary>
    string? Identity { get; }

    /// <summary>
    /// Gets the token expiration time.
    /// Available after successful authentication.
    /// </summary>
    DateTimeOffset? TokenExpiresAt { get; }

    /// <summary>
    /// Gets the tenant ID from the authentication result.
    /// Available after successful authentication.
    /// </summary>
    string? TenantId { get; }

    /// <summary>
    /// Gets the Entra Object ID (OID) from the authentication result.
    /// Available after successful authentication.
    /// </summary>
    string? ObjectId { get; }

    /// <summary>
    /// Gets the MSAL home account identifier.
    /// Format: {objectId}.{tenantId} - uniquely identifies the account+tenant for token cache lookup.
    /// Available after successful authentication.
    /// </summary>
    string? HomeAccountId { get; }

    /// <summary>
    /// Gets the access token from the last authentication.
    /// Available after successful authentication. Used for extracting JWT claims.
    /// </summary>
    string? AccessToken { get; }

    /// <summary>
    /// Gets the ID token claims from the last authentication (if available).
    /// The ID token typically contains user claims like country that aren't in the access token.
    /// </summary>
    System.Security.Claims.ClaimsPrincipal? IdTokenClaims { get; }
}

/// <summary>
/// Result of creating a credential provider.
/// </summary>
public sealed class CredentialResult
{
    /// <summary>
    /// Gets whether the authentication was successful.
    /// </summary>
    public bool Success { get; init; }

    /// <summary>
    /// Gets the authenticated ServiceClient, if successful.
    /// </summary>
    public ServiceClient? Client { get; init; }

    /// <summary>
    /// Gets the identity string (username or app ID).
    /// </summary>
    public string? Identity { get; init; }

    /// <summary>
    /// Gets the token expiration time.
    /// </summary>
    public DateTimeOffset? TokenExpiresAt { get; init; }

    /// <summary>
    /// Gets the error message, if authentication failed.
    /// </summary>
    public string? ErrorMessage { get; init; }

    /// <summary>
    /// Gets the exception, if authentication failed.
    /// </summary>
    public Exception? Exception { get; init; }

    /// <summary>
    /// Creates a successful result.
    /// </summary>
    public static CredentialResult Succeeded(ServiceClient client, string? identity, DateTimeOffset? expiresAt)
    {
        return new CredentialResult
        {
            Success = true,
            Client = client,
            Identity = identity,
            TokenExpiresAt = expiresAt
        };
    }

    /// <summary>
    /// Creates a failed result.
    /// </summary>
    public static CredentialResult Failed(string message, Exception? exception = null)
    {
        return new CredentialResult
        {
            Success = false,
            ErrorMessage = message,
            Exception = exception
        };
    }
}

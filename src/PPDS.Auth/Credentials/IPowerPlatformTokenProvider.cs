using System;
using System.Threading;
using System.Threading.Tasks;

namespace PPDS.Auth.Credentials;

/// <summary>
/// Provides access tokens for Power Platform REST APIs (Power Apps, Power Automate, etc.).
/// Unlike <see cref="ICredentialProvider"/> which creates ServiceClient for Dataverse,
/// this interface acquires tokens for the Power Platform management APIs.
/// </summary>
public interface IPowerPlatformTokenProvider : IDisposable
{
    /// <summary>
    /// Acquires an access token for the Power Apps API.
    /// Resource: https://api.powerapps.com (varies by cloud).
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A valid access token.</returns>
    /// <exception cref="AuthenticationException">If authentication fails.</exception>
    Task<PowerPlatformToken> GetPowerAppsTokenAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Acquires an access token for the Power Automate (Flow) API.
    /// Resource: https://api.flow.microsoft.com (varies by cloud).
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A valid access token.</returns>
    /// <exception cref="AuthenticationException">If authentication fails.</exception>
    Task<PowerPlatformToken> GetPowerAutomateTokenAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Acquires an access token for the specified Power Platform resource.
    /// </summary>
    /// <param name="resource">The resource URL (e.g., https://api.powerapps.com).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A valid access token.</returns>
    /// <exception cref="AuthenticationException">If authentication fails.</exception>
    Task<PowerPlatformToken> GetTokenForResourceAsync(string resource, CancellationToken cancellationToken = default);
}

/// <summary>
/// Represents an access token for Power Platform APIs.
/// </summary>
public sealed class PowerPlatformToken
{
    /// <summary>
    /// Gets the access token string.
    /// </summary>
    public required string AccessToken { get; init; }

    /// <summary>
    /// Gets the token expiration time.
    /// </summary>
    public required DateTimeOffset ExpiresOn { get; init; }

    /// <summary>
    /// Gets the resource the token is valid for.
    /// </summary>
    public required string Resource { get; init; }

    /// <summary>
    /// Gets the identity that acquired the token (username or app ID).
    /// </summary>
    public string? Identity { get; init; }

    /// <summary>
    /// Returns true if the token is expired or will expire within the buffer period.
    /// </summary>
    /// <param name="bufferMinutes">Buffer time before expiration to consider expired.</param>
    public bool IsExpired(int bufferMinutes = 5)
        => ExpiresOn <= DateTimeOffset.UtcNow.AddMinutes(bufferMinutes);
}

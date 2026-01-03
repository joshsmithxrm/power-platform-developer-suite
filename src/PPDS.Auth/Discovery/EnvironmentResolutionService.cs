using System;
using System.Threading;
using System.Threading.Tasks;
using PPDS.Auth.Credentials;
using PPDS.Auth.Profiles;

namespace PPDS.Auth.Discovery;

/// <summary>
/// Service for resolving environments using a multi-layer strategy.
/// </summary>
/// <remarks>
/// Resolution strategy:
/// 1. If identifier looks like a URL → Try direct Dataverse connection first
/// 2. If not a URL OR direct connection fails → Try Global Discovery (interactive profiles only)
/// 3. For service principals without a URL → Return helpful error
/// </remarks>
public sealed class EnvironmentResolutionService : IDisposable
{
    private readonly AuthProfile _profile;
    private readonly Action<DeviceCodeInfo>? _deviceCodeCallback;
    private readonly ISecureCredentialStore? _credentialStore;
    private ICredentialProvider? _credentialProvider;
    private bool _disposed;

    /// <summary>
    /// Creates a new environment resolution service.
    /// </summary>
    /// <param name="profile">The auth profile to use for resolution.</param>
    /// <param name="deviceCodeCallback">Optional callback for device code display.</param>
    /// <param name="credentialStore">Optional secure credential store for looking up secrets.</param>
    public EnvironmentResolutionService(
        AuthProfile profile,
        Action<DeviceCodeInfo>? deviceCodeCallback = null,
        ISecureCredentialStore? credentialStore = null)
    {
        _profile = profile ?? throw new ArgumentNullException(nameof(profile));
        _deviceCodeCallback = deviceCodeCallback;
        _credentialStore = credentialStore;
    }

    /// <summary>
    /// Resolves an environment by identifier using the multi-layer strategy.
    /// </summary>
    /// <param name="identifier">The environment identifier (URL, name, ID, etc.).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A result containing the resolved environment info on success, or an error message on failure.</returns>
    public async Task<EnvironmentResolutionResult> ResolveAsync(
        string identifier,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(identifier))
            return EnvironmentResolutionResult.Failed("Environment identifier is required.");

        identifier = identifier.Trim();

        // Check if identifier looks like a URL
        if (IsUrl(identifier))
        {
            // Try direct connection first (works for all auth types including service principals)
            var directResult = await TryDirectConnectionAsync(identifier, cancellationToken);
            if (directResult.Success)
                return directResult;

            // If direct connection failed and this is NOT an interactive profile,
            // we can't fall back to Global Discovery
            if (!CanUseGlobalDiscovery(_profile.AuthMethod))
            {
                return EnvironmentResolutionResult.Failed(
                    $"Failed to connect to environment: {directResult.ErrorMessage}");
            }

            // Fall through to Global Discovery for interactive profiles
        }
        else
        {
            // Not a URL - need Global Discovery to resolve by name/ID
            if (!CanUseGlobalDiscovery(_profile.AuthMethod))
            {
                return EnvironmentResolutionResult.Failed(
                    "Service principals require a full environment URL (e.g., https://org.crm.dynamics.com). " +
                    "Name-based resolution requires Global Discovery which is only available for user authentication.");
            }
        }

        // Try Global Discovery
        return await TryGlobalDiscoveryAsync(identifier, cancellationToken);
    }

    /// <summary>
    /// Attempts to resolve environment via direct Dataverse connection.
    /// </summary>
    private async Task<EnvironmentResolutionResult> TryDirectConnectionAsync(
        string url,
        CancellationToken cancellationToken)
    {
        try
        {
            // Normalize URL
            url = NormalizeUrl(url);

            // Create credential provider using async factory (supports secure store lookups)
            _credentialProvider ??= await CredentialProviderFactory.CreateAsync(
                _profile, _credentialStore, _deviceCodeCallback, cancellationToken)
                .ConfigureAwait(false);

            // Connect to Dataverse and get org metadata
            using var client = await _credentialProvider.CreateServiceClientAsync(
                url,
                cancellationToken,
                forceInteractive: false);

            if (!client.IsReady)
            {
                return EnvironmentResolutionResult.Failed(
                    client.LastError ?? "Failed to connect to Dataverse");
            }

            // Extract org metadata from ServiceClient
            var envInfo = new EnvironmentInfo
            {
                Url = url,
                DisplayName = !string.IsNullOrEmpty(client.ConnectedOrgFriendlyName)
                    ? client.ConnectedOrgFriendlyName
                    : ExtractEnvironmentName(url),
                UniqueName = client.ConnectedOrgUniqueName,
                OrganizationId = client.ConnectedOrgId != Guid.Empty
                    ? client.ConnectedOrgId.ToString()
                    : null,
                EnvironmentId = client.EnvironmentId
            };

            return EnvironmentResolutionResult.Succeeded(envInfo, ResolutionMethod.DirectConnection);
        }
        catch (Exception ex)
        {
            return EnvironmentResolutionResult.Failed($"Direct connection failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Attempts to resolve environment via Global Discovery Service.
    /// </summary>
    private async Task<EnvironmentResolutionResult> TryGlobalDiscoveryAsync(
        string identifier,
        CancellationToken cancellationToken)
    {
        try
        {
            using var gds = GlobalDiscoveryService.FromProfile(_profile);
            var environments = await gds.DiscoverEnvironmentsAsync(cancellationToken);

            DiscoveredEnvironment? resolved;
            try
            {
                resolved = EnvironmentResolver.Resolve(environments, identifier);
            }
            catch (AmbiguousMatchException ex)
            {
                return EnvironmentResolutionResult.Failed(ex.Message);
            }

            if (resolved == null)
            {
                return EnvironmentResolutionResult.Failed(
                    $"Environment '{identifier}' not found. Use 'ppds env list' to see available environments.");
            }

            var envInfo = new EnvironmentInfo
            {
                Url = resolved.ApiUrl,
                DisplayName = resolved.FriendlyName,
                UniqueName = resolved.UniqueName,
                EnvironmentId = resolved.EnvironmentId,
                OrganizationId = resolved.Id.ToString(),
                Type = resolved.EnvironmentType,
                Region = resolved.Region
            };

            return EnvironmentResolutionResult.Succeeded(envInfo, ResolutionMethod.GlobalDiscovery);
        }
        catch (Exception ex)
        {
            return EnvironmentResolutionResult.Failed($"Global Discovery failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Checks if the identifier looks like a URL.
    /// </summary>
    private static bool IsUrl(string identifier)
    {
        // Check for common URL patterns
        if (identifier.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            identifier.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // Check for hostname patterns like "org.crm.dynamics.com"
        if (identifier.Contains(".crm", StringComparison.OrdinalIgnoreCase) &&
            identifier.Contains(".dynamics.com", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return false;
    }

    /// <summary>
    /// Normalizes a URL to ensure it has https:// prefix.
    /// </summary>
    private static string NormalizeUrl(string url)
    {
        url = url.Trim().TrimEnd('/');

        if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            url = "https://" + url;
        }

        return url;
    }

    /// <summary>
    /// Extracts a display name from a URL.
    /// </summary>
    private static string ExtractEnvironmentName(string url)
    {
        try
        {
            var uri = new Uri(url);
            var host = uri.Host;

            // Extract org name from subdomain (e.g., "orgname" from "orgname.crm.dynamics.com")
            var parts = host.Split('.');
            if (parts.Length > 0)
            {
                return parts[0];
            }

            return host;
        }
        catch (Exception)
        {
            // Best-effort parsing - return input if URL parsing fails for any reason
            return url;
        }
    }

    /// <summary>
    /// Checks if the auth method can use Global Discovery Service.
    /// Delegates to <see cref="GlobalDiscoveryService.SupportsGlobalDiscovery"/> for consistency.
    /// </summary>
    private static bool CanUseGlobalDiscovery(AuthMethod authMethod)
        => GlobalDiscoveryService.SupportsGlobalDiscovery(authMethod);

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
            return;

        _credentialProvider?.Dispose();
        _disposed = true;
    }
}

/// <summary>
/// Result of an environment resolution attempt.
/// </summary>
public sealed class EnvironmentResolutionResult
{
    /// <summary>
    /// Gets whether the resolution was successful.
    /// </summary>
    public bool Success { get; private init; }

    /// <summary>
    /// Gets the resolved environment info (null if failed).
    /// </summary>
    public EnvironmentInfo? Environment { get; private init; }

    /// <summary>
    /// Gets the method used to resolve the environment.
    /// </summary>
    public ResolutionMethod Method { get; private init; }

    /// <summary>
    /// Gets the error message (null if successful).
    /// </summary>
    public string? ErrorMessage { get; private init; }

    private EnvironmentResolutionResult() { }

    /// <summary>
    /// Creates a successful result.
    /// </summary>
    public static EnvironmentResolutionResult Succeeded(EnvironmentInfo environment, ResolutionMethod method)
    {
        return new EnvironmentResolutionResult
        {
            Success = true,
            Environment = environment,
            Method = method
        };
    }

    /// <summary>
    /// Creates a failed result.
    /// </summary>
    public static EnvironmentResolutionResult Failed(string errorMessage)
    {
        return new EnvironmentResolutionResult
        {
            Success = false,
            ErrorMessage = errorMessage
        };
    }
}

/// <summary>
/// Method used to resolve an environment.
/// </summary>
public enum ResolutionMethod
{
    /// <summary>
    /// Resolved via direct Dataverse connection.
    /// </summary>
    DirectConnection,

    /// <summary>
    /// Resolved via Global Discovery Service.
    /// </summary>
    GlobalDiscovery
}

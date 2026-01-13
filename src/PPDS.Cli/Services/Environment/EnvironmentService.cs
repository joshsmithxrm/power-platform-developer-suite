using Microsoft.Extensions.Logging;
using PPDS.Auth.Credentials;
using PPDS.Auth.Discovery;
using PPDS.Auth.Profiles;
using PPDS.Cli.Infrastructure.Errors;

namespace PPDS.Cli.Services.Environment;

/// <summary>
/// Application service for managing Dataverse environments.
/// </summary>
/// <remarks>
/// See ADR-0015 for architectural context.
/// </remarks>
public sealed class EnvironmentService : IEnvironmentService
{
    private readonly ProfileStore _store;
    private readonly ILogger<EnvironmentService> _logger;

    /// <summary>
    /// Creates a new environment service.
    /// </summary>
    public EnvironmentService(ProfileStore store, ILogger<EnvironmentService> logger)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<EnvironmentSummary>> DiscoverEnvironmentsAsync(
        Action<DeviceCodeInfo>? deviceCodeCallback = null,
        CancellationToken cancellationToken = default)
    {
        var collection = await _store.LoadAsync(cancellationToken);
        var profile = collection.ActiveProfile;

        if (profile == null)
        {
            throw new PpdsException(
                ErrorCodes.Profile.NoActiveProfile,
                "No active profile. Create a profile with 'ppds auth create' first.");
        }

        if (!GlobalDiscoveryService.SupportsGlobalDiscovery(profile.AuthMethod))
        {
            throw new PpdsException(
                ErrorCodes.Operation.NotSupported,
                $"Environment discovery is not supported for {profile.AuthMethod} authentication. " +
                "Use 'ppds env select --environment <url>' to connect directly to an environment.");
        }

        try
        {
            using var gds = GlobalDiscoveryService.FromProfile(profile, deviceCodeCallback);
            var environments = await gds.DiscoverEnvironmentsAsync(cancellationToken);

            // Persist HomeAccountId if captured during authentication.
            // This enables silent auth on subsequent discovery calls (fixes repeated login prompts).
            if (!string.IsNullOrEmpty(gds.CapturedHomeAccountId) &&
                !string.Equals(profile.HomeAccountId, gds.CapturedHomeAccountId, StringComparison.Ordinal))
            {
                profile.HomeAccountId = gds.CapturedHomeAccountId;
                await _store.SaveAsync(collection, cancellationToken);
                _logger.LogDebug("Updated profile HomeAccountId after environment discovery authentication");
            }

            _logger.LogInformation("Discovered {Count} environments", environments.Count);

            return environments
                .Select(EnvironmentSummary.FromDiscovered)
                .ToList();
        }
        catch (NotSupportedException ex)
        {
            throw new PpdsException(ErrorCodes.Operation.NotSupported, ex.Message, ex);
        }
        catch (AuthenticationException ex)
        {
            throw new PpdsAuthException(
                ErrorCodes.Auth.InvalidCredentials,
                $"Authentication failed during environment discovery: {ex.Message}",
                ex)
            {
                RequiresReauthentication = true
            };
        }
        catch (Exception ex) when (ex is not PpdsException)
        {
            throw new PpdsException(
                ErrorCodes.Connection.DiscoveryFailed,
                $"Environment discovery failed: {ex.Message}",
                ex);
        }
    }

    /// <inheritdoc />
    public async Task<EnvironmentSummary?> GetCurrentEnvironmentAsync(CancellationToken cancellationToken = default)
    {
        var collection = await _store.LoadAsync(cancellationToken);
        var profile = collection.ActiveProfile;

        if (profile?.Environment == null)
        {
            return null;
        }

        return EnvironmentSummary.FromEnvironmentInfo(profile.Environment);
    }

    /// <inheritdoc />
    public async Task<EnvironmentSummary> SetEnvironmentAsync(
        string identifier,
        Action<DeviceCodeInfo>? deviceCodeCallback = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(identifier))
        {
            throw new PpdsValidationException("identifier", "Environment identifier is required.");
        }

        var collection = await _store.LoadAsync(cancellationToken);
        var profile = collection.ActiveProfile;

        if (profile == null)
        {
            throw new PpdsException(
                ErrorCodes.Profile.NoActiveProfile,
                "No active profile. Create a profile with 'ppds auth create' first.");
        }

        try
        {
            using var credentialStore = new NativeCredentialStore();
            using var resolver = new EnvironmentResolutionService(
                profile,
                deviceCodeCallback,
                credentialStore);

            var result = await resolver.ResolveAsync(identifier, cancellationToken);

            if (!result.Success)
            {
                // Check if it's a not-found vs other error
                if (result.ErrorMessage?.Contains("not found", StringComparison.OrdinalIgnoreCase) is true)
                {
                    throw new PpdsNotFoundException("Environment", identifier);
                }

                throw new PpdsException(
                    ErrorCodes.Connection.EnvironmentNotFound,
                    result.ErrorMessage ?? "Failed to resolve environment.");
            }

            // Update profile with resolved environment
            profile.Environment = result.Environment;
            await _store.SaveAsync(collection, cancellationToken);

            _logger.LogInformation(
                "Set environment to {EnvironmentName} ({EnvironmentUrl}) via {Method}",
                result.Environment!.DisplayName,
                result.Environment.Url,
                result.Method);

            return EnvironmentSummary.FromEnvironmentInfo(result.Environment);
        }
        catch (AuthenticationException ex)
        {
            throw new PpdsAuthException(
                ErrorCodes.Auth.InvalidCredentials,
                $"Authentication failed while connecting to environment: {ex.Message}",
                ex)
            {
                RequiresReauthentication = true
            };
        }
        catch (AmbiguousMatchException ex)
        {
            throw new PpdsException(ErrorCodes.Connection.AmbiguousEnvironment, ex.Message, ex);
        }
        catch (Exception ex) when (ex is not PpdsException)
        {
            throw new PpdsException(
                ErrorCodes.Connection.EnvironmentNotFound,
                $"Failed to connect to environment: {ex.Message}",
                ex);
        }
    }

    /// <inheritdoc />
    public async Task<bool> ClearEnvironmentAsync(CancellationToken cancellationToken = default)
    {
        var collection = await _store.LoadAsync(cancellationToken);
        var profile = collection.ActiveProfile;

        if (profile == null)
        {
            throw new PpdsException(
                ErrorCodes.Profile.NoActiveProfile,
                "No active profile. Create a profile with 'ppds auth create' first.");
        }

        if (profile.Environment == null)
        {
            return false;
        }

        var previousEnv = profile.Environment.DisplayName;
        profile.Environment = null;
        await _store.SaveAsync(collection, cancellationToken);

        _logger.LogInformation("Cleared environment (was: {EnvironmentName})", previousEnv);

        return true;
    }

    /// <inheritdoc />
    public bool SupportsDiscovery(AuthMethod authMethod)
    {
        return GlobalDiscoveryService.SupportsGlobalDiscovery(authMethod);
    }
}

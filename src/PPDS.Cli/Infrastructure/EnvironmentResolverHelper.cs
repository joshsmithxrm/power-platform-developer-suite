using PPDS.Auth.Discovery;
using PPDS.Auth.Profiles;

namespace PPDS.Cli.Infrastructure;

/// <summary>
/// Result of resolving an environment identifier.
/// </summary>
public sealed class ResolvedEnvironment
{
    /// <summary>
    /// Gets the environment URL.
    /// </summary>
    public required string Url { get; init; }

    /// <summary>
    /// Gets the environment display name.
    /// </summary>
    public required string DisplayName { get; init; }

    /// <summary>
    /// Gets the environment unique name.
    /// </summary>
    public string? UniqueName { get; init; }

    /// <summary>
    /// Gets the Power Platform environment ID.
    /// Required for Power Apps Admin API operations.
    /// </summary>
    public string? EnvironmentId { get; init; }
}

/// <summary>
/// Helper for resolving environment identifiers (name, ID, or URL) to URLs.
/// </summary>
public static class EnvironmentResolverHelper
{
    /// <summary>
    /// Resolves an environment identifier to a URL using the profile's discovered environments.
    /// </summary>
    /// <param name="profile">The authentication profile to use for discovery.</param>
    /// <param name="environmentIdentifier">The environment identifier (name, ID, or URL).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The resolved environment information.</returns>
    /// <exception cref="InvalidOperationException">If the environment cannot be resolved.</exception>
    public static async Task<ResolvedEnvironment> ResolveAsync(
        AuthProfile profile,
        string environmentIdentifier,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentException.ThrowIfNullOrWhiteSpace(environmentIdentifier);

        // If it looks like a full URL, use it directly
        if (Uri.TryCreate(environmentIdentifier, UriKind.Absolute, out var uri) &&
            (uri.Scheme == "https" || uri.Scheme == "http"))
        {
            return new ResolvedEnvironment
            {
                Url = environmentIdentifier.TrimEnd('/'),
                DisplayName = uri.Host
            };
        }

        // Otherwise, discover environments and resolve
        using var gds = GlobalDiscoveryService.FromProfile(profile);
        var environments = await gds.DiscoverEnvironmentsAsync(cancellationToken);

        DiscoveredEnvironment? resolved;
        try
        {
            resolved = EnvironmentResolver.Resolve(environments, environmentIdentifier);
        }
        catch (AmbiguousMatchException ex)
        {
            throw new InvalidOperationException(ex.Message, ex);
        }

        if (resolved == null)
        {
            var availableEnvs = environments.Count > 0
                ? string.Join(", ", environments.Take(5).Select(e => e.FriendlyName))
                : "(none discovered)";

            throw new InvalidOperationException(
                $"Environment '{environmentIdentifier}' not found.\n" +
                $"Available environments: {availableEnvs}\n" +
                $"Use 'ppds env list' to see all available environments.");
        }

        return new ResolvedEnvironment
        {
            Url = resolved.ApiUrl,
            DisplayName = resolved.FriendlyName,
            UniqueName = resolved.UniqueName,
            EnvironmentId = resolved.EnvironmentId
        };
    }

    /// <summary>
    /// Resolves environments for both source and target, caching discovery results if same profile.
    /// </summary>
    /// <param name="sourceProfile">The source authentication profile.</param>
    /// <param name="targetProfile">The target authentication profile.</param>
    /// <param name="sourceEnvIdentifier">The source environment identifier.</param>
    /// <param name="targetEnvIdentifier">The target environment identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Tuple of resolved source and target environments.</returns>
    public static async Task<(ResolvedEnvironment Source, ResolvedEnvironment Target)> ResolveSourceTargetAsync(
        AuthProfile sourceProfile,
        AuthProfile targetProfile,
        string sourceEnvIdentifier,
        string targetEnvIdentifier,
        CancellationToken cancellationToken = default)
    {
        // Check if both are already URLs (skip discovery entirely)
        var sourceIsUrl = Uri.TryCreate(sourceEnvIdentifier, UriKind.Absolute, out var sourceUri) &&
                          (sourceUri.Scheme == "https" || sourceUri.Scheme == "http");
        var targetIsUrl = Uri.TryCreate(targetEnvIdentifier, UriKind.Absolute, out var targetUri) &&
                          (targetUri.Scheme == "https" || targetUri.Scheme == "http");

        if (sourceIsUrl && targetIsUrl)
        {
            return (
                new ResolvedEnvironment { Url = sourceEnvIdentifier.TrimEnd('/'), DisplayName = sourceUri!.Host },
                new ResolvedEnvironment { Url = targetEnvIdentifier.TrimEnd('/'), DisplayName = targetUri!.Host }
            );
        }

        // If same profile, share discovery results
        var sameProfile = sourceProfile.Index == targetProfile.Index;

        if (sameProfile)
        {
            using var gds = GlobalDiscoveryService.FromProfile(sourceProfile);
            var environments = await gds.DiscoverEnvironmentsAsync(cancellationToken);

            var sourceResolved = ResolveFromList(environments, sourceEnvIdentifier, "source");
            var targetResolved = ResolveFromList(environments, targetEnvIdentifier, "target");

            return (sourceResolved, targetResolved);
        }

        // Different profiles - resolve separately
        var source = await ResolveAsync(sourceProfile, sourceEnvIdentifier, cancellationToken);
        var target = await ResolveAsync(targetProfile, targetEnvIdentifier, cancellationToken);

        return (source, target);
    }

    private static ResolvedEnvironment ResolveFromList(
        IReadOnlyList<DiscoveredEnvironment> environments,
        string identifier,
        string label)
    {
        // Check if it's already a URL
        if (Uri.TryCreate(identifier, UriKind.Absolute, out var uri) &&
            (uri.Scheme == "https" || uri.Scheme == "http"))
        {
            return new ResolvedEnvironment
            {
                Url = identifier.TrimEnd('/'),
                DisplayName = uri.Host
            };
        }

        DiscoveredEnvironment? resolved;
        try
        {
            resolved = EnvironmentResolver.Resolve(environments, identifier);
        }
        catch (AmbiguousMatchException ex)
        {
            throw new InvalidOperationException($"Ambiguous {label} environment: {ex.Message}", ex);
        }

        if (resolved == null)
        {
            var availableEnvs = environments.Count > 0
                ? string.Join(", ", environments.Take(5).Select(e => e.FriendlyName))
                : "(none discovered)";

            throw new InvalidOperationException(
                $"{char.ToUpper(label[0])}{label[1..]} environment '{identifier}' not found.\n" +
                $"Available environments: {availableEnvs}\n" +
                $"Use 'ppds env list' to see all available environments.");
        }

        return new ResolvedEnvironment
        {
            Url = resolved.ApiUrl,
            DisplayName = resolved.FriendlyName,
            UniqueName = resolved.UniqueName,
            EnvironmentId = resolved.EnvironmentId
        };
    }
}

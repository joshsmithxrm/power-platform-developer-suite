using PPDS.Auth.Discovery;
using PPDS.Auth.Profiles;
using PPDS.Cli.Services.Environment;

namespace PPDS.Mcp.Infrastructure;

public sealed partial class McpToolContext
{
    // Offline regression seams. Production uses the real providers only after policy checks.
    internal Func<AuthProfile, string, CancellationToken, Task<EnvironmentResolutionResult>>? DirectEnvironmentResolver { get; init; }
    internal Func<AuthProfile, CancellationToken, Task<IReadOnlyList<DiscoveredEnvironment>>>? EnvironmentDiscovery { get; init; }

    internal async Task<EnvironmentResolutionResult> ResolveEnvironmentSelectionAsync(
        string identifier, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(identifier);
        var profile = await GetActiveProfileAsync(cancellationToken).ConfigureAwait(false);
        identifier = identifier.Trim();

        // URL-shaped input never reaches a generic resolver until policy permits its exact URL.
        // Names use discovery below, so no future direct-resolution heuristic can bypass this check.
        var absolute = Uri.TryCreate(identifier, UriKind.Absolute, out _);
        var dataverseHost = identifier.Contains(".crm", StringComparison.OrdinalIgnoreCase)
            && identifier.Contains(".dynamics.com", StringComparison.OrdinalIgnoreCase);
        if (absolute || identifier.Contains("://", StringComparison.Ordinal)
            || identifier.StartsWith("//", StringComparison.Ordinal) || dataverseHost)
        {
            var candidate = !absolute && dataverseHost && !identifier.Contains("://", StringComparison.Ordinal)
                ? "https://" + identifier : identifier;
            var target = EnvironmentConfirmationService.NormalizeUrl(candidate);
            ValidateSelectionTarget(profile, target);
            cancellationToken.ThrowIfCancellationRequested();
            if (DirectEnvironmentResolver is not null)
                return await DirectEnvironmentResolver(profile, target, cancellationToken).ConfigureAwait(false);

            using var resolver = new EnvironmentResolutionService(profile, credentialStore: _credentialStore);
            return await resolver.ResolveAsync(target, cancellationToken).ConfigureAwait(false);
        }

        var global = GlobalDiscoveryService.SupportsGlobalDiscovery(profile.AuthMethod);
        if (!global && !BapEnvironmentService.SupportsAuthMethod(profile.AuthMethod))
            return EnvironmentResolutionResult.Failed(
                $"Auth method '{profile.AuthMethod}' does not support name-based environment resolution. Provide an allowed full environment URL.");

        IReadOnlyList<DiscoveredEnvironment> environments;
        if (EnvironmentDiscovery is not null)
            environments = await EnvironmentDiscovery(profile, cancellationToken).ConfigureAwait(false);
        else if (global)
        {
            using var discovery = GlobalDiscoveryService.FromProfile(profile);
            environments = await discovery.DiscoverEnvironmentsAsync(cancellationToken).ConfigureAwait(false);
        }
        else
        {
            using var discovery = await BapEnvironmentService.FromProfileAsync(
                profile, _credentialStore, cancellationToken).ConfigureAwait(false);
            environments = await discovery.DiscoverEnvironmentsAsync(cancellationToken).ConfigureAwait(false);
        }

        cancellationToken.ThrowIfCancellationRequested();
        var resolved = ResolveUniqueEnvironment(environments, identifier);
        if (resolved is null)
            return EnvironmentResolutionResult.Failed($"Environment '{identifier}' not found. Use ppds_env_list to see available environments.");
        var resolvedUrl = EnvironmentConfirmationService.NormalizeUrl(resolved.ApiUrl);
        ValidateSelectionTarget(profile, resolvedUrl);
        return EnvironmentResolutionResult.Succeeded(new EnvironmentInfo
        {
            Url = resolvedUrl,
            DisplayName = resolved.FriendlyName,
            UniqueName = resolved.UniqueName,
            OrganizationId = resolved.Id != Guid.Empty ? resolved.Id.ToString() : null,
            EnvironmentId = resolved.EnvironmentId,
            Type = resolved.EnvironmentType,
            Region = resolved.Region
        }, global ? ResolutionMethod.GlobalDiscovery : ResolutionMethod.BapDiscovery);
    }

    private void ValidateSelectionTarget(AuthProfile profile, string target)
    {
        if (profile.Environment is null
            || !string.Equals(EnvironmentConfirmationService.NormalizeUrl(profile.Environment.Url), target, StringComparison.Ordinal))
            ValidateEnvironmentSwitch(target);
    }

    private static DiscoveredEnvironment? ResolveUniqueEnvironment(
        IReadOnlyList<DiscoveredEnvironment> environments, string identifier)
    {
        var isId = Guid.TryParse(identifier, out var id);
        Func<DiscoveredEnvironment, bool>[] exactMatches =
        [
            environment => isId && environment.Id == id,
            environment => string.Equals(environment.ApiUrl.TrimEnd('/'), identifier.TrimEnd('/'), StringComparison.OrdinalIgnoreCase)
                || string.Equals(environment.Url?.TrimEnd('/'), identifier.TrimEnd('/'), StringComparison.OrdinalIgnoreCase),
            environment => string.Equals(environment.UniqueName, identifier, StringComparison.OrdinalIgnoreCase),
            environment => string.Equals(environment.FriendlyName, identifier, StringComparison.OrdinalIgnoreCase)
        ];
        foreach (var predicate in exactMatches)
        {
            var matches = environments.Where(predicate).ToList();
            if (matches.Count > 1)
                throw new AmbiguousMatchException($"Multiple environments match '{identifier}'. Use an unambiguous environment URL or ID.");
            if (matches.Count == 1) return matches[0];
        }
        return EnvironmentResolver.Resolve(environments, identifier);
    }
}

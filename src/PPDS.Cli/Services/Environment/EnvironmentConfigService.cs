using PPDS.Auth.Profiles;
using PPDS.Cli.Infrastructure.Errors;

namespace PPDS.Cli.Services.Environment;

/// <summary>
/// Default implementation of <see cref="IEnvironmentConfigService"/>.
/// </summary>
/// <remarks>
/// A change that lowers an environment's write protection (see
/// <see cref="WriteProtectionResolver.FindLowering"/>) is itself a protected action: it is saved only
/// with a confirmation naming the environment, and never on a host whose
/// <see cref="ProtectionLoweringPolicy"/> refuses it (#1829). Every surface saves through this service,
/// so none can skip the rule.
/// </remarks>
public sealed class EnvironmentConfigService : IEnvironmentConfigService
{
    /// <summary>
    /// Built-in type defaults. User overrides in environments.json take precedence.
    /// </summary>
    private static readonly Dictionary<EnvironmentType, EnvironmentColor> BuiltInTypeDefaults = new()
    {
        [EnvironmentType.Production] = EnvironmentColor.Red,
        [EnvironmentType.Sandbox] = EnvironmentColor.Brown,
        [EnvironmentType.Development] = EnvironmentColor.Green,
        [EnvironmentType.Test] = EnvironmentColor.Yellow,
        [EnvironmentType.Trial] = EnvironmentColor.Cyan,
    };

    private readonly EnvironmentConfigStore _store;
    private readonly ProtectionLoweringPolicy _loweringPolicy;

    /// <summary>Creates the service. Lowering protection needs a confirmation unless <paramref name="loweringPolicy"/> refuses it outright.</summary>
    public EnvironmentConfigService(EnvironmentConfigStore store, ProtectionLoweringPolicy? loweringPolicy = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _loweringPolicy = loweringPolicy ?? ProtectionLoweringPolicy.RequireConfirmation;
    }

    public async Task<EnvironmentConfig?> GetConfigAsync(string url, CancellationToken ct = default)
        => await _store.GetConfigAsync(url, ct).ConfigureAwait(false);

    public async Task<IReadOnlyList<EnvironmentConfig>> GetAllConfigsAsync(CancellationToken ct = default)
    {
        var collection = await _store.LoadAsync(ct).ConfigureAwait(false);
        return collection.Environments.AsReadOnly();
    }

    public async Task<EnvironmentConfig> SaveConfigAsync(
        string url, string? label = null, EnvironmentType? type = null, EnvironmentColor? color = null,
        bool clearColor = false,
        string? confirmLowerProtection = null,
        CancellationToken ct = default)
    {
        var assessment = await AssessConfigChangeAsync(url, type, ct).ConfigureAwait(false);
        EnsureLoweringAllowed(assessment, confirmLowerProtection);
        return await _store.SaveConfigAsync(url, label, type, color, clearColor, ct: ct).ConfigureAwait(false);
    }

    public async Task<ProtectionChangeAssessment> AssessConfigChangeAsync(
        string url, EnvironmentType? type, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(url);
        var before = await _store.GetConfigAsync(url, ct).ConfigureAwait(false);

        // Label and colour never affect protection; only the type does (an explicit protection value
        // cannot be set through this service). A change that leaves the type alone still creates an
        // entry when there is none, which FindLowering judges by the confirmation-gate rule only.
        var after = new EnvironmentConfig
        {
            Url = before?.Url ?? EnvironmentConfig.NormalizeUrl(url),
            Type = type ?? before?.Type,
            Protection = before?.Protection,
            DiscoveredType = before?.DiscoveredType
        };

        var lowerings = WriteProtectionResolver.FindLowering(before, after);
        return new ProtectionChangeAssessment(
            url, before?.Type, after.Type, lowerings, DescribeDiscoveryContradiction(before, after));
    }

    public async Task<bool> RemoveConfigAsync(string url, CancellationToken ct = default)
    {
        // Every writer treats a missing entry as Production, so removal never lowers protection.
        // The check stays so that a future resolver rule cannot make removal a silent way round it.
        var before = await _store.GetConfigAsync(url, ct).ConfigureAwait(false);
        var lowerings = WriteProtectionResolver.FindLowering(before, after: null);
        EnsureLoweringAllowed(
            new ProtectionChangeAssessment(url, before?.Type, null, lowerings, null),
            confirmLowerProtection: null);
        return await _store.RemoveConfigAsync(url, ct).ConfigureAwait(false);
    }

    private void EnsureLoweringAllowed(ProtectionChangeAssessment assessment, string? confirmLowerProtection)
    {
        if (!assessment.LowersProtection)
            return;

        var target = assessment.EnvironmentUrl;
        if (!_loweringPolicy.AllowsConfirmedLowering)
        {
            throw new PpdsException(EnvironmentConfirmationErrorCodes.ProtectionLoweringRefused,
                $"{assessment.Describe()} This server does not change an environment's protection; " +
                "change it from the PPDS CLI, the TUI or the VS Code extension. No change was saved.");
        }

        if (confirmLowerProtection is null)
        {
            throw new PpdsException(EnvironmentConfirmationErrorCodes.ProtectionLoweringConfirmationRequired,
                $"{assessment.Describe()} Writes there would no longer ask for the confirmation they ask for now. " +
                "The change needs a confirmation that names this environment. No change was saved.");
        }

        if (!string.Equals(
                EnvironmentConfirmationService.NormalizeUrl(target),
                EnvironmentConfirmationService.NormalizeUrl(confirmLowerProtection),
                StringComparison.Ordinal))
        {
            throw new PpdsException(EnvironmentConfirmationErrorCodes.TargetMismatch,
                $"The acknowledgement does not name the environment being changed ('{target}'). No change was saved.");
        }
    }

    private static string? DescribeDiscoveryContradiction(EnvironmentConfig? before, EnvironmentConfig after)
    {
        var discovered = ParseDiscoveryType(before?.DiscoveredType);
        if (discovered == EnvironmentType.Unknown || after.Type is null || after.Type == discovered)
            return null;

        return WriteProtectionResolver.Resolve(discovered, null) > WriteProtectionResolver.ResolveForConfirmation(after)
            ? $"Discovery reports this environment as {discovered}."
            : null;
    }

    public async Task SaveTypeDefaultAsync(EnvironmentType typeName, EnvironmentColor color, CancellationToken ct = default)
    {
        var collection = await _store.LoadAsync(ct).ConfigureAwait(false);
        collection.TypeDefaults[typeName] = color;
        await _store.SaveAsync(collection, ct).ConfigureAwait(false);
    }

    public async Task<bool> RemoveTypeDefaultAsync(EnvironmentType typeName, CancellationToken ct = default)
    {
        var collection = await _store.LoadAsync(ct).ConfigureAwait(false);
        if (collection.TypeDefaults.Remove(typeName))
        {
            await _store.SaveAsync(collection, ct).ConfigureAwait(false);
            return true;
        }
        return false;
    }

    public async Task<IReadOnlyDictionary<EnvironmentType, EnvironmentColor>> GetAllTypeDefaultsAsync(CancellationToken ct = default)
    {
        var collection = await _store.LoadAsync(ct).ConfigureAwait(false);
        var merged = new Dictionary<EnvironmentType, EnvironmentColor>(BuiltInTypeDefaults);
        foreach (var (key, value) in collection.TypeDefaults)
        {
            merged[key] = value;
        }
        return merged;
    }

    public async Task<EnvironmentColor> ResolveColorAsync(string url, CancellationToken ct = default)
    {
        var config = await _store.GetConfigAsync(url, ct).ConfigureAwait(false);

        // Priority 1: per-environment explicit color
        if (config is not null && config.Color != null)
            return config.Color.Value;

        // Priority 2: type-based color (resolve type first)
        var envType = config?.Type ?? DetectTypeFromUrl(url);
        if (envType != EnvironmentType.Unknown)
        {
            var allDefaults = await GetAllTypeDefaultsAsync(ct).ConfigureAwait(false);
            if (allDefaults.TryGetValue(envType, out var typeColor))
                return typeColor;
        }

        // Priority 3: fallback
        return EnvironmentColor.Gray;
    }

    public async Task<EnvironmentType> ResolveTypeAsync(string url, string? discoveredType = null, CancellationToken ct = default)
    {
        // Priority 1: user config type
        var config = await _store.GetConfigAsync(url, ct).ConfigureAwait(false);
        if (config?.Type != null)
            return config.Type.Value;

        // Priority 2: discovery API type
        if (!string.IsNullOrWhiteSpace(discoveredType))
        {
            var parsed = ParseDiscoveryType(discoveredType);
            if (parsed != EnvironmentType.Unknown)
                return parsed;
        }

        // Priority 3: URL heuristics
        var heuristic = DetectTypeFromUrl(url);
        if (heuristic != EnvironmentType.Unknown)
            return heuristic;

        return EnvironmentType.Unknown;
    }

    public async Task<string?> ResolveLabelAsync(string url, CancellationToken ct = default)
    {
        var config = await _store.GetConfigAsync(url, ct).ConfigureAwait(false);
        return config?.Label;
    }

    /// <summary>
    /// Maps Discovery API type strings to EnvironmentType enum values.
    /// </summary>
    public static EnvironmentType ParseDiscoveryType(string? discoveryType) => discoveryType?.ToLowerInvariant() switch
    {
        "production" => EnvironmentType.Production,
        "sandbox" => EnvironmentType.Sandbox,
        "developer" => EnvironmentType.Development,
        "development" => EnvironmentType.Development,
        "trial" => EnvironmentType.Trial,
        "test" => EnvironmentType.Test,
        _ => EnvironmentType.Unknown
    };

    #region URL Heuristics (fallback only)

    private static readonly string[] DevKeywords = ["dev", "develop", "development"];
    private static readonly string[] TestKeywords = ["test", "qa", "uat"];
    private static readonly string[] TrialKeywords = ["trial", "demo", "preview"];

    /// <summary>
    /// Last-resort keyword-based detection from the org name portion of the URL.
    /// Only used when no user config or Discovery API type exists.
    /// Does NOT use the CRM regional suffix (crm, crm2, crm4, etc.) — those
    /// indicate geographic region, not environment type.
    /// </summary>
    /// <remarks>
    /// Extracts the org name (subdomain before the first dot) from the URL hostname,
    /// splits it on common delimiters (-, ., _), and checks for exact segment matches
    /// to avoid false positives (e.g., "adventureworks" matching "dev").
    /// </remarks>
    internal static EnvironmentType DetectTypeFromUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return EnvironmentType.Unknown;

        var segments = ExtractOrgSegments(url);
        if (segments.Length == 0) return EnvironmentType.Unknown;

        if (segments.Any(s => DevKeywords.Any(k => s.Equals(k, StringComparison.OrdinalIgnoreCase))))
            return EnvironmentType.Development;
        if (segments.Any(s => TestKeywords.Any(k => s.Equals(k, StringComparison.OrdinalIgnoreCase))))
            return EnvironmentType.Test;
        if (segments.Any(s => TrialKeywords.Any(k => s.Equals(k, StringComparison.OrdinalIgnoreCase))))
            return EnvironmentType.Trial;

        return EnvironmentType.Unknown;
    }

    /// <summary>
    /// Extracts the org name from a URL hostname and splits it into segments.
    /// For "https://contoso-dev.crm.dynamics.com", returns ["contoso", "dev"].
    /// </summary>
    private static string[] ExtractOrgSegments(string url)
    {
        try
        {
            string host;
            if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
            {
                host = uri.Host;
            }
            else
            {
                // Fallback: treat the whole string as a potential hostname
                host = url;
            }

            // The org name is the subdomain before the first dot
            var dotIndex = host.IndexOf('.');
            var orgName = dotIndex > 0 ? host[..dotIndex] : host;

            return orgName.Split(['-', '.', '_'], StringSplitOptions.RemoveEmptyEntries);
        }
        catch
        {
            return [];
        }
    }

    #endregion
}

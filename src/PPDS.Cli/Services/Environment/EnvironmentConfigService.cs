using PPDS.Auth.Profiles;

namespace PPDS.Cli.Services.Environment;

/// <summary>
/// Default implementation of <see cref="IEnvironmentConfigService"/>.
/// </summary>
public sealed class EnvironmentConfigService : IEnvironmentConfigService
{
    /// <summary>
    /// Built-in type defaults. Custom user types in environments.json override these.
    /// </summary>
    private static readonly Dictionary<string, EnvironmentColor> BuiltInTypeDefaults = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Production"] = EnvironmentColor.Red,
        ["Sandbox"] = EnvironmentColor.Brown,
        ["Development"] = EnvironmentColor.Green,
        ["Test"] = EnvironmentColor.Yellow,
        ["Trial"] = EnvironmentColor.Cyan,
    };

    private readonly EnvironmentConfigStore _store;

    public EnvironmentConfigService(EnvironmentConfigStore store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
    }

    public async Task<EnvironmentConfig?> GetConfigAsync(string url, CancellationToken ct = default)
        => await _store.GetConfigAsync(url, ct).ConfigureAwait(false);

    public async Task<IReadOnlyList<EnvironmentConfig>> GetAllConfigsAsync(CancellationToken ct = default)
    {
        var collection = await _store.LoadAsync(ct).ConfigureAwait(false);
        return collection.Environments.AsReadOnly();
    }

    public async Task<EnvironmentConfig> SaveConfigAsync(
        string url, string? label = null, string? type = null, EnvironmentColor? color = null,
        bool clearColor = false,
        CancellationToken ct = default)
        => await _store.SaveConfigAsync(url, label, type, color, clearColor, ct).ConfigureAwait(false);

    public async Task<bool> RemoveConfigAsync(string url, CancellationToken ct = default)
        => await _store.RemoveConfigAsync(url, ct).ConfigureAwait(false);

    public async Task SaveTypeDefaultAsync(string typeName, EnvironmentColor color, CancellationToken ct = default)
    {
        var collection = await _store.LoadAsync(ct).ConfigureAwait(false);
        collection.TypeDefaults[typeName] = color;
        await _store.SaveAsync(collection, ct).ConfigureAwait(false);
    }

    public async Task<bool> RemoveTypeDefaultAsync(string typeName, CancellationToken ct = default)
    {
        var collection = await _store.LoadAsync(ct).ConfigureAwait(false);
        if (collection.TypeDefaults.Remove(typeName))
        {
            await _store.SaveAsync(collection, ct).ConfigureAwait(false);
            return true;
        }
        return false;
    }

    public async Task<IReadOnlyDictionary<string, EnvironmentColor>> GetAllTypeDefaultsAsync(CancellationToken ct = default)
    {
        var collection = await _store.LoadAsync(ct).ConfigureAwait(false);
        var merged = new Dictionary<string, EnvironmentColor>(BuiltInTypeDefaults, StringComparer.OrdinalIgnoreCase);
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
        var type = (config is not null ? config.Type : null) ?? DetectTypeFromUrl(url);
        if (type != null)
        {
            var allDefaults = await GetAllTypeDefaultsAsync(ct).ConfigureAwait(false);
            if (allDefaults.TryGetValue(type, out var typeColor))
                return typeColor;
        }

        // Priority 3: fallback
        return EnvironmentColor.Gray;
    }

    public async Task<string?> ResolveTypeAsync(string url, string? discoveredType = null, CancellationToken ct = default)
    {
        // Priority 1: user config type
        var config = await _store.GetConfigAsync(url, ct).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(config?.Type))
            return config!.Type;

        // Priority 2: discovery API type
        if (!string.IsNullOrWhiteSpace(discoveredType))
            return discoveredType;

        // Priority 3: URL heuristics
        return DetectTypeFromUrl(url);
    }

    public async Task<string?> ResolveLabelAsync(string url, CancellationToken ct = default)
    {
        var config = await _store.GetConfigAsync(url, ct).ConfigureAwait(false);
        return config?.Label;
    }

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
    internal static string? DetectTypeFromUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;

        var segments = ExtractOrgSegments(url);
        if (segments.Length == 0) return null;

        if (segments.Any(s => DevKeywords.Any(k => s.Equals(k, StringComparison.OrdinalIgnoreCase))))
            return "Development";
        if (segments.Any(s => TestKeywords.Any(k => s.Equals(k, StringComparison.OrdinalIgnoreCase))))
            return "Test";
        if (segments.Any(s => TrialKeywords.Any(k => s.Equals(k, StringComparison.OrdinalIgnoreCase))))
            return "Trial";

        return null;
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

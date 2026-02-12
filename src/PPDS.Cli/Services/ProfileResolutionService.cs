using PPDS.Auth.Profiles;

namespace PPDS.Cli.Services;

/// <summary>
/// Resolves environment profile labels to EnvironmentConfig instances.
/// Used by cross-environment query planning to map bracket syntax ([LABEL].entity)
/// to the correct connection pool.
/// </summary>
public sealed class ProfileResolutionService
{
    private readonly Dictionary<string, EnvironmentConfig> _labelIndex;

    public ProfileResolutionService(IEnumerable<EnvironmentConfig> configs)
    {
        _labelIndex = new Dictionary<string, EnvironmentConfig>(StringComparer.OrdinalIgnoreCase);
        foreach (var config in configs)
        {
            if (!string.IsNullOrEmpty(config.Label))
            {
                _labelIndex[config.Label] = config;
            }
        }
    }

    /// <summary>
    /// Resolves a label to its EnvironmentConfig. Returns null if not found.
    /// </summary>
    public EnvironmentConfig? ResolveByLabel(string label)
    {
        return _labelIndex.TryGetValue(label, out var config) ? config : null;
    }
}

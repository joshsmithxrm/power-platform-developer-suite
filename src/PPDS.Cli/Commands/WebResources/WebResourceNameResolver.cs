using PPDS.Dataverse.Services;

namespace PPDS.Cli.Commands.WebResources;

/// <summary>
/// Resolves web resource identifiers (GUID, exact name, partial name) against a list of resources.
/// Used by list, get, url, and publish commands.
/// </summary>
public static class WebResourceNameResolver
{
    /// <summary>
    /// Resolution result for single-resource commands (get, url, publish).
    /// IsSuccess is true only when exactly one match is found.
    /// </summary>
    public sealed record ResolveResult(bool IsSuccess, IReadOnlyList<WebResourceInfo> Matches);

    /// <summary>
    /// Resolves a single identifier to a web resource. Returns success only for exactly one match.
    /// Resolution order: GUID → exact name → partial match (ends with).
    /// </summary>
    public static ResolveResult Resolve(string identifier, IReadOnlyList<WebResourceInfo> resources)
    {
        // 1. Try GUID
        if (Guid.TryParse(identifier, out var guid))
        {
            var byId = resources.Where(r => r.Id == guid).ToList();
            return new ResolveResult(byId.Count == 1, byId);
        }

        // 2. Try exact name (case-insensitive)
        var exact = resources
            .Where(r => r.Name.Equals(identifier, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (exact.Count > 0)
        {
            return new ResolveResult(exact.Count == 1, exact);
        }

        // 3. Partial match: name ends with /identifier or equals identifier
        var partial = resources
            .Where(r => r.Name.EndsWith("/" + identifier, StringComparison.OrdinalIgnoreCase)
                     || r.Name.EndsWith(identifier, StringComparison.OrdinalIgnoreCase))
            .ToList();
        return new ResolveResult(partial.Count == 1, partial);
    }

    /// <summary>
    /// Filters resources by partial name match. For list commands where multiple matches are expected.
    /// Matches: exact name, name contains, name starts with (prefix match).
    /// </summary>
    public static IReadOnlyList<WebResourceInfo> Filter(string pattern, IReadOnlyList<WebResourceInfo> resources)
    {
        return resources
            .Where(r => r.Name.Contains(pattern, StringComparison.OrdinalIgnoreCase))
            .ToList();
    }
}

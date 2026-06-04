namespace PPDS.Cli.Services.ModelDrivenApps;

/// <summary>
/// Represents the hierarchical Area → Group → SubArea sitemap structure.
/// </summary>
public sealed record SitemapStructure(IReadOnlyList<SitemapArea> Areas);

/// <summary>
/// A top-level navigation area in the sitemap.
/// </summary>
public sealed record SitemapArea(string Id, string? Title, IReadOnlyList<SitemapGroup> Groups);

/// <summary>
/// A navigation group within an area.
/// </summary>
public sealed record SitemapGroup(string Id, string? Title, IReadOnlyList<SitemapSubArea> SubAreas);

/// <summary>
/// A navigation entry (table/entity) within a group.
/// </summary>
public sealed record SitemapSubArea(string Id, string? Entity, string? Title, string? Url);

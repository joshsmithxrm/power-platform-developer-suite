namespace PPDS.Cli.Services.ModelDrivenApps;

/// <summary>
/// Detailed information about a model-driven app.
/// </summary>
public sealed record ModelDrivenAppDetails(
    Guid AppModuleId,
    Guid AppModuleIdUnique,
    string DisplayName,
    string UniqueName,
    string? Description,
    string? PublisherName,
    int EntityCount,
    int ExplicitFormCount,
    int ExplicitViewCount,
    int ExplicitChartCount,
    int SitemapCount);

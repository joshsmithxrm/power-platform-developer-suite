namespace PPDS.Cli.Services.ModelDrivenApps;

/// <summary>
/// Options for the set-sitemap-xml operation.
/// </summary>
public sealed record SetSitemapOptions(string? Solution, bool Publish);

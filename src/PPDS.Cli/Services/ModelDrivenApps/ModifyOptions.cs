namespace PPDS.Cli.Services.ModelDrivenApps;

/// <summary>
/// Shared options for write operations that support solution and publish.
/// </summary>
/// <param name="Solution">Optional solution to add the app/sitemap to.</param>
/// <param name="Publish">Publish the app after the change.</param>
/// <param name="Confirm">Bypass production write protection on a Production-flagged environment (#1195).</param>
public sealed record ModifyOptions(string? Solution, bool Publish, bool Confirm = false);

namespace PPDS.Cli.Services.ModelDrivenApps;

/// <summary>
/// Shared options for write operations that support solution and publish.
/// </summary>
public sealed record ModifyOptions(string? Solution, bool Publish);

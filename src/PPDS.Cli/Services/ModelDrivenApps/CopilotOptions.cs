namespace PPDS.Cli.Services.ModelDrivenApps;

/// <summary>
/// Options for <c>add-copilot</c> / <c>remove-copilot</c> operations.
/// </summary>
/// <param name="Publish">Publish the app after the change.</param>
/// <param name="DryRun">Preview the change without writing to Dataverse.</param>
public sealed record CopilotOptions(bool Publish, bool DryRun);

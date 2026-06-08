namespace PPDS.Cli.Services.ModelDrivenApps;

/// <summary>
/// Options for <c>add-copilot</c> / <c>remove-copilot</c> operations.
/// </summary>
/// <param name="Publish">Publish the app after the change.</param>
/// <param name="DryRun">Preview the change without writing to Dataverse.</param>
/// <param name="Force">Bypass the app-eligibility preflight (add-copilot only); logged to stderr.</param>
/// <param name="Confirm">Bypass production write protection on a Production-flagged environment (#1195).</param>
public sealed record CopilotOptions(bool Publish, bool DryRun, bool Force = false, bool Confirm = false);

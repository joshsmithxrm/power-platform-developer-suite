namespace PPDS.Cli.Services.ModelDrivenApps;

/// <summary>
/// Categories of problem the <c>inspect-app-assistant</c> diagnostic can report for a
/// model-driven app's Copilot (bot) <c>appelement</c> bindings.
/// </summary>
public enum AppAssistantFindingKind
{
    /// <summary>
    /// A bot is bound at the <c>appelement</c> level but is not an app-assistant
    /// (<c>islightweightbot</c> is not <c>true</c>), so it will never render in-app.
    /// </summary>
    NotAppAssistant,

    /// <summary>More than one <c>appelement</c> binds the same bot for the app.</summary>
    DuplicateBinding,

    /// <summary>
    /// A copilot-shaped <c>appelement</c> row with a null <c>objectid</c> (no target bot).
    /// </summary>
    OrphanAppElement
}

/// <summary>
/// A single problem found by <c>inspect-app-assistant</c>.
/// </summary>
/// <param name="Kind">The category of problem.</param>
/// <param name="BotName">The bound bot display name, when known (null for orphans).</param>
/// <param name="BotId">The bound bot id, when known (null for orphans).</param>
/// <param name="IsLightweightBot">
/// The bot's app-assistant flag, when known; null when the bot could not be resolved or for orphans.
/// </param>
/// <param name="AppElementIds">The offending appelement row id(s).</param>
/// <param name="Remediation">A one-line hint describing how to fix the finding.</param>
public sealed record AppAssistantFinding(
    AppAssistantFindingKind Kind,
    string? BotName,
    Guid? BotId,
    bool? IsLightweightBot,
    IReadOnlyList<Guid> AppElementIds,
    string Remediation);

/// <summary>
/// Result of a read-only <c>inspect-app-assistant</c> diagnostic run for one app.
/// </summary>
/// <param name="AppName">The inspected app (as supplied by the caller).</param>
/// <param name="AppModuleId">The resolved appmodule id.</param>
/// <param name="Findings">The problems found; empty when the app is healthy.</param>
public sealed record AppAssistantDiagnostics(
    string AppName,
    Guid AppModuleId,
    IReadOnlyList<AppAssistantFinding> Findings)
{
    /// <summary>True when no problems were found.</summary>
    public bool IsHealthy => Findings.Count == 0;
}

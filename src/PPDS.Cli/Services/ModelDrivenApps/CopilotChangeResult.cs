namespace PPDS.Cli.Services.ModelDrivenApps;

/// <summary>
/// Result of an <c>add-copilot</c> / <c>remove-copilot</c> operation.
/// </summary>
/// <param name="AppName">The app the change targeted.</param>
/// <param name="AppModuleId">The resolved appmodule id.</param>
/// <param name="BotId">The resolved Copilot (bot) id.</param>
/// <param name="BotName">The Copilot display name.</param>
/// <param name="BotSchemaName">The Copilot schema name (used for the appelement name).</param>
/// <param name="AppElementId">The created/removed appelement id, or null for a dry run.</param>
/// <param name="UniqueName">The appelement unique name.</param>
/// <param name="DryRun">True when no write was performed.</param>
/// <param name="Published">True when the app was published as part of the change.</param>
/// <param name="EligibilityReason">
/// Null when the app supports the model-driven app assistant; otherwise the reason it is unsupported
/// (issue #1192). Surfaced on a <see cref="DryRun"/> verdict, or alongside <see cref="Forced"/>.
/// </param>
/// <param name="Forced">True when an unsupported app was wired anyway via --force.</param>
public sealed record CopilotChangeResult(
    string AppName,
    Guid AppModuleId,
    Guid BotId,
    string BotName,
    string BotSchemaName,
    Guid? AppElementId,
    string UniqueName,
    bool DryRun,
    bool Published,
    string? EligibilityReason = null,
    bool Forced = false);

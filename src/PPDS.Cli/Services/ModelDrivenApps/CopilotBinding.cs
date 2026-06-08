namespace PPDS.Cli.Services.ModelDrivenApps;

/// <summary>
/// A Copilot Studio agent (bot) wired into a model-driven app via an <c>appelement</c>
/// whose polymorphic <c>objectid</c> targets the <c>bot</c> table.
/// </summary>
/// <param name="AppElementId">The appelement row id.</param>
/// <param name="UniqueName">The appelement unique name.</param>
/// <param name="Name">The appelement name (the bot schema name).</param>
/// <param name="BotId">The bound bot id.</param>
/// <param name="BotName">The bound bot display name, when available from the lookup.</param>
public sealed record CopilotBinding(
    Guid AppElementId,
    string UniqueName,
    string Name,
    Guid BotId,
    string? BotName);

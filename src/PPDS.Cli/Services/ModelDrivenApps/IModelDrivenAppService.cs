using PPDS.Cli.Infrastructure.Progress;

namespace PPDS.Cli.Services.ModelDrivenApps;

/// <summary>
/// Service interface for model-driven app navigation management.
/// </summary>
public interface IModelDrivenAppService
{
    /// <summary>Returns all model-driven apps in the environment.</summary>
    Task<IReadOnlyList<ModelDrivenAppSummary>> ListAppsAsync(CancellationToken ct);

    /// <summary>Returns detailed information about a single app by display or unique name.</summary>
    Task<ModelDrivenAppDetails> GetAppAsync(string appName, CancellationToken ct);

    /// <summary>Returns the hierarchical sitemap structure for an app. Set <paramref name="unpublished"/> to read the latest draft.</summary>
    Task<SitemapStructure> GetSitemapAsync(string appName, bool unpublished, CancellationToken ct);

    /// <summary>Returns the raw sitemap XML for an app. Set <paramref name="unpublished"/> to read the latest draft.</summary>
    Task<string> GetSitemapXmlAsync(string appName, bool unpublished, CancellationToken ct);

    /// <summary>Replaces the sitemap XML for an app after validating against the XSD.</summary>
    Task SetSitemapXmlAsync(string appName, string xml, SetSitemapOptions options, IProgressReporter? progress, CancellationToken ct);

    /// <summary>Adds one or more tables (entities) to the app's sitemap navigation.</summary>
    Task AddTableAsync(string appName, IReadOnlyList<string> entities, AddTableOptions options, IProgressReporter? progress, CancellationToken ct);

    /// <summary>Removes a table from the app's sitemap and cleans up its components.</summary>
    Task RemoveTableAsync(string appName, string entity, ModifyOptions options, IProgressReporter? progress, CancellationToken ct);

    /// <summary>Sets explicit form visibility for an entity in the app.</summary>
    Task SetFormsAsync(string appName, string entity, ComponentSelectionOptions options, IProgressReporter? progress, CancellationToken ct);

    /// <summary>Sets explicit view visibility for an entity in the app.</summary>
    Task SetViewsAsync(string appName, string entity, ComponentSelectionOptions options, IProgressReporter? progress, CancellationToken ct);

    /// <summary>Sets explicit chart visibility for an entity in the app.</summary>
    Task SetChartsAsync(string appName, string entity, ComponentSelectionOptions options, IProgressReporter? progress, CancellationToken ct);

    /// <summary>Wires a Copilot Studio agent (bot) into the app by creating an appelement whose objectid targets the bot.</summary>
    Task<CopilotChangeResult> AddCopilotAsync(string appName, string bot, CopilotOptions options, IProgressReporter? progress, CancellationToken ct);

    /// <summary>Removes a Copilot (bot) binding from the app.</summary>
    Task<CopilotChangeResult> RemoveCopilotAsync(string appName, string bot, CopilotOptions options, IProgressReporter? progress, CancellationToken ct);

    /// <summary>Lists the Copilots (bots) wired into the app.</summary>
    Task<IReadOnlyList<CopilotBinding>> ListCopilotsAsync(string appName, CancellationToken ct);

    /// <summary>
    /// Read-only diagnostic ("copilot doctor") that inspects the app's Copilot (bot)
    /// <c>appelement</c> bindings and reports problems (non-app-assistant bots, duplicate
    /// bindings, orphan rows) without writing anything.
    /// </summary>
    Task<AppAssistantDiagnostics> InspectAppAssistantAsync(string appName, CancellationToken ct);
}

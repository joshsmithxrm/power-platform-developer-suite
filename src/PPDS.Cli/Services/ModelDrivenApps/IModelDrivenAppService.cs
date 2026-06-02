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

    /// <summary>Returns the hierarchical sitemap structure for an app.</summary>
    Task<SitemapStructure> GetSitemapAsync(string appName, CancellationToken ct);

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
}

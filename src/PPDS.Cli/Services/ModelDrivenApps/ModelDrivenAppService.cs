using System.Xml.Linq;
using Microsoft.Crm.Sdk.Messages;
using Microsoft.Extensions.Logging;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Cli.Infrastructure.Progress;
using PPDS.Dataverse.Metadata;
using PPDS.Dataverse.Pooling;

namespace PPDS.Cli.Services.ModelDrivenApps;

/// <summary>
/// Service for managing model-driven app navigation via CLI operations.
/// </summary>
public sealed class ModelDrivenAppService : IModelDrivenAppService
{
    private readonly IDataverseConnectionPool _pool;
    private readonly ICachedMetadataProvider _metadata;
    private readonly SitemapXmlValidator _validator;
    private readonly ILogger<ModelDrivenAppService> _logger;

    private const int ComponentTypeEntity = 1;
    private const int ComponentTypeView = 26;
    private const int ComponentTypeChart = 59;
    private const int ComponentTypeForm = 60;
    private const int ComponentTypeSitemap = 62;
    private const int ComponentTypeApp = 80;

    /// <summary>
    /// Initializes a new instance of the <see cref="ModelDrivenAppService"/> class.
    /// </summary>
    public ModelDrivenAppService(
        IDataverseConnectionPool pool,
        ICachedMetadataProvider metadata,
        SitemapXmlValidator validator,
        ILogger<ModelDrivenAppService> logger)
    {
        _pool = pool ?? throw new ArgumentNullException(nameof(pool));
        _metadata = metadata ?? throw new ArgumentNullException(nameof(metadata));
        _validator = validator ?? throw new ArgumentNullException(nameof(validator));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ModelDrivenAppSummary>> ListAppsAsync(CancellationToken ct)
    {
        await using var client = await _pool.GetClientAsync(cancellationToken: ct);

        var query = new QueryExpression("appmodule")
        {
            ColumnSet = new ColumnSet("appmoduleid", "appmoduleidunique", "name", "uniquename"),
            Orders = { new OrderExpression("name", OrderType.Ascending) }
        };

        EntityCollection result;
        try
        {
            result = await client.RetrieveMultipleAsync(query, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new PpdsException(ModelDrivenAppErrorCodes.ListFailed, "Failed to list model-driven apps.", ex);
        }

        var appIds = result.Entities
            .Select(e => e.GetAttributeValue<Guid>("appmoduleidunique"))
            .ToList();

        var componentCounts = await GetComponentCountsAsync(client, appIds, ct);

        return result.Entities.Select(e =>
        {
            var unique = e.GetAttributeValue<Guid>("appmoduleidunique");
            var total = componentCounts.TryGetValue(unique, out var counts)
                ? counts.Values.Sum()
                : 0;
            return new ModelDrivenAppSummary(
                e.GetAttributeValue<Guid>("appmoduleid"),
                e.GetAttributeValue<string>("name") ?? string.Empty,
                e.GetAttributeValue<string>("uniquename") ?? string.Empty,
                total);
        }).ToList();
    }

    /// <inheritdoc />
    public async Task<ModelDrivenAppDetails> GetAppAsync(string appName, CancellationToken ct)
    {
        await using var client = await _pool.GetClientAsync(cancellationToken: ct);

        var (appModuleId, appModuleIdUnique, uniqueName) = await ResolveAppAsync(appName, client, ct);

        var query = new QueryExpression("appmodule")
        {
            ColumnSet = new ColumnSet("appmoduleid", "appmoduleidunique", "name", "uniquename", "description", "publisherid"),
            TopCount = 1
        };
        query.Criteria.AddCondition("appmoduleid", ConditionOperator.Equal, appModuleId);

        var publisherLink = query.AddLink("publisher", "publisherid", "publisherid", JoinOperator.LeftOuter);
        publisherLink.EntityAlias = "pub";
        publisherLink.Columns.AddColumn("friendlyname");

        var appResult = await client.RetrieveMultipleAsync(query, ct);
        var appEntity = appResult.Entities.FirstOrDefault()
            ?? throw new PpdsException(ModelDrivenAppErrorCodes.AppNotFound, $"App '{appName}' not found.");

        var publisherName = appEntity.GetAttributeValue<AliasedValue>("pub.friendlyname")?.Value as string;
        var description = appEntity.GetAttributeValue<string>("description");

        var counts = await GetComponentCountsForAppAsync(client, appModuleIdUnique, ct);

        return new ModelDrivenAppDetails(
            appModuleId,
            appModuleIdUnique,
            appEntity.GetAttributeValue<string>("name") ?? string.Empty,
            appEntity.GetAttributeValue<string>("uniquename") ?? string.Empty,
            string.IsNullOrWhiteSpace(description) ? null : description,
            publisherName,
            counts.GetValueOrDefault(ComponentTypeEntity),
            counts.GetValueOrDefault(ComponentTypeForm),
            counts.GetValueOrDefault(ComponentTypeView),
            counts.GetValueOrDefault(ComponentTypeChart),
            counts.GetValueOrDefault(ComponentTypeSitemap));
    }

    /// <inheritdoc />
    public async Task<SitemapStructure> GetSitemapAsync(string appName, CancellationToken ct)
    {
        await using var client = await _pool.GetClientAsync(cancellationToken: ct);

        var (_, appModuleIdUnique, _) = await ResolveAppAsync(appName, client, ct);
        var xml = await FetchSitemapXmlAsync(client, appModuleIdUnique, ct);

        return ParseSitemapStructure(xml);
    }

    /// <inheritdoc />
    public async Task SetSitemapXmlAsync(string appName, string xml, SetSitemapOptions options, IProgressReporter? progress, CancellationToken ct)
    {
        XDocument doc;
        try
        {
            doc = XDocument.Parse(xml);
        }
        catch (Exception ex)
        {
            throw new PpdsValidationException("xml", $"XML is not well-formed: {ex.Message}")
            {
                ErrorCode = ModelDrivenAppErrorCodes.InvalidSitemapXml
            };
        }

        _validator.Validate(doc);

        progress?.ReportPhase("Updating sitemap", appName);

        await using var client = await _pool.GetClientAsync(cancellationToken: ct);
        var (appModuleId, appModuleIdUnique, uniqueName) = await ResolveAppAsync(appName, client, ct);
        var sitemapId = await GetSitemapIdAsync(client, appModuleIdUnique, ct);

        await PatchSitemapAsync(client, sitemapId, xml, ct);

        if (options.Solution != null)
        {
            await AddToSolutionAsync(client, options.Solution, appModuleIdUnique, sitemapId, ct);
        }

        if (options.Publish)
        {
            await PublishAppAsync(client, uniqueName, ct);
        }
    }

    /// <inheritdoc />
    public async Task AddTableAsync(string appName, IReadOnlyList<string> entities, AddTableOptions options, IProgressReporter? progress, CancellationToken ct)
    {
        progress?.ReportPhase("Loading app", appName);

        var entitySummaries = await _metadata.GetEntitiesAsync(ct);
        var entityLookup = entitySummaries.ToDictionary(e => e.LogicalName, StringComparer.OrdinalIgnoreCase);

        await using var client = await _pool.GetClientAsync(cancellationToken: ct);
        var (appModuleId, appModuleIdUnique, uniqueName) = await ResolveAppAsync(appName, client, ct);
        var sitemapId = await GetSitemapIdAsync(client, appModuleIdUnique, ct);
        var sitemapXml = await FetchSitemapXmlByIdAsync(client, sitemapId, ct);

        var doc = XDocument.Parse(sitemapXml);
        var siteMapEl = doc.Root!;

        // Locate or create Area
        XElement areaEl;
        if (!string.IsNullOrEmpty(options.Area))
        {
            areaEl = siteMapEl.Elements("Area")
                .FirstOrDefault(a => string.Equals(a.Attribute("Id")?.Value, options.Area, StringComparison.OrdinalIgnoreCase)
                                  || string.Equals(a.Attribute("Title")?.Value, options.Area, StringComparison.OrdinalIgnoreCase))
                ?? CreateAndAppendArea(siteMapEl, options.Area, options.Area);
        }
        else
        {
            areaEl = siteMapEl.Elements("Area").FirstOrDefault()
                ?? CreateAndAppendArea(siteMapEl, "Area1", "Main");
        }

        // Locate or create Group
        XElement groupEl;
        if (!string.IsNullOrEmpty(options.Group))
        {
            groupEl = areaEl.Elements("Group")
                .FirstOrDefault(g => string.Equals(g.Attribute("Id")?.Value, options.Group, StringComparison.OrdinalIgnoreCase)
                                  || string.Equals(g.Attribute("Title")?.Value, options.Group, StringComparison.OrdinalIgnoreCase))
                ?? CreateAndAppendGroup(areaEl, options.Group, options.Group);
        }
        else
        {
            groupEl = areaEl.Elements("Group").FirstOrDefault()
                ?? CreateAndAppendGroup(areaEl, "Group1", string.Empty);
        }

        // Collect all entities already in sitemap for duplicate detection
        var existingEntities = siteMapEl.Descendants("SubArea")
            .Select(s => s.Attribute("Entity")?.Value)
            .Where(e => e != null)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var entityName in entities)
        {
            progress?.ReportInfo($"Adding {entityName}");

            if (!entityLookup.TryGetValue(entityName, out var entitySummary))
            {
                throw new PpdsException(ModelDrivenAppErrorCodes.EntityNotFound,
                    $"Table '{entityName}' does not exist in the environment.");
            }

            if (existingEntities.Contains(entityName))
            {
                throw new PpdsException(ModelDrivenAppErrorCodes.EntityAlreadyInApp,
                    $"Table '{entityName}' is already in app '{appName}'.");
            }

            var title = !string.IsNullOrEmpty(options.Title) ? options.Title : entitySummary.DisplayName;
            var subAreaId = $"subarea_{Guid.NewGuid():N}";

            var subAreaEl = new XElement("SubArea",
                new XAttribute("Id", subAreaId),
                new XAttribute("Entity", entitySummary.LogicalName),
                new XAttribute("Title", title));

            groupEl.Add(subAreaEl);
            existingEntities.Add(entitySummary.LogicalName);
        }

        var updatedXml = doc.ToString(SaveOptions.DisableFormatting);
        _validator.Validate(XDocument.Parse(updatedXml));

        progress?.ReportPhase("Updating sitemap");
        await PatchSitemapAsync(client, sitemapId, updatedXml, ct);

        if (options.Solution != null)
        {
            await AddToSolutionAsync(client, options.Solution, appModuleIdUnique, sitemapId, ct);
        }

        if (options.Publish)
        {
            await PublishAppAsync(client, uniqueName, ct);
        }
    }

    /// <inheritdoc />
    public async Task RemoveTableAsync(string appName, string entity, ModifyOptions options, IProgressReporter? progress, CancellationToken ct)
    {
        progress?.ReportPhase("Loading app", appName);

        await using var client = await _pool.GetClientAsync(cancellationToken: ct);
        var (appModuleId, appModuleIdUnique, uniqueName) = await ResolveAppAsync(appName, client, ct);
        var sitemapId = await GetSitemapIdAsync(client, appModuleIdUnique, ct);
        var sitemapXml = await FetchSitemapXmlByIdAsync(client, sitemapId, ct);

        var doc = XDocument.Parse(sitemapXml);
        var subAreaEl = doc.Descendants("SubArea")
            .FirstOrDefault(s => string.Equals(s.Attribute("Entity")?.Value, entity, StringComparison.OrdinalIgnoreCase));

        if (subAreaEl == null)
        {
            throw new PpdsException(ModelDrivenAppErrorCodes.EntityNotInApp,
                $"Table '{entity}' is not in app '{appName}'. Add it first with: ppds model-driven-app add-table {entity} --app \"{appName}\"");
        }

        subAreaEl.Remove();

        progress?.ReportPhase("Removing components");

        // Remove all explicit forms/views/charts for the entity
        await RemoveExplicitComponentsForEntityAsync(client, appModuleId, appModuleIdUnique, entity, ct);

        var updatedXml = doc.ToString(SaveOptions.DisableFormatting);
        _validator.Validate(XDocument.Parse(updatedXml));

        progress?.ReportPhase("Updating sitemap");
        await PatchSitemapAsync(client, sitemapId, updatedXml, ct);

        if (options.Solution != null)
        {
            await AddToSolutionAsync(client, options.Solution, appModuleIdUnique, sitemapId, ct);
        }

        if (options.Publish)
        {
            await PublishAppAsync(client, uniqueName, ct);
        }
    }

    /// <inheritdoc />
    public async Task SetFormsAsync(string appName, string entity, ComponentSelectionOptions options, IProgressReporter? progress, CancellationToken ct)
    {
        ValidateComponentOptions(options, "--form");

        await using var client = await _pool.GetClientAsync(cancellationToken: ct);
        var (appModuleId, appModuleIdUnique, uniqueName) = await ResolveAppAsync(appName, client, ct);

        await EnsureEntityInSitemapAsync(client, appModuleIdUnique, entity, appName, ct);

        progress?.ReportPhase("Updating forms", entity);

        // Always reset explicit forms first
        var existingFormRefs = await GetExplicitEntityComponentsAsync(
            client, appModuleIdUnique, ComponentTypeForm, "systemform", "formid", "objecttypecode", entity, ct);

        if (existingFormRefs.Count > 0)
        {
            await RemoveAppComponentsAsync(client, appModuleId, existingFormRefs, ct);
        }

        if (!options.All)
        {
            var formIds = await ResolveFormIdsAsync(client, entity, options.ComponentNames, ct);
            await AddAppComponentsAsync(client, appModuleId, "systemform", formIds, ct);
        }

        if (options.Solution != null)
        {
            var sitemapId = await GetSitemapIdAsync(client, appModuleIdUnique, ct);
            await AddToSolutionAsync(client, options.Solution, appModuleIdUnique, sitemapId, ct);
        }

        if (options.Publish)
        {
            await PublishAppAsync(client, uniqueName, ct);
        }
    }

    /// <inheritdoc />
    public async Task SetViewsAsync(string appName, string entity, ComponentSelectionOptions options, IProgressReporter? progress, CancellationToken ct)
    {
        ValidateComponentOptions(options, "--view");

        await using var client = await _pool.GetClientAsync(cancellationToken: ct);
        var (appModuleId, appModuleIdUnique, uniqueName) = await ResolveAppAsync(appName, client, ct);

        await EnsureEntityInSitemapAsync(client, appModuleIdUnique, entity, appName, ct);

        progress?.ReportPhase("Updating views", entity);

        var existingViewRefs = await GetExplicitEntityComponentsAsync(
            client, appModuleIdUnique, ComponentTypeView, "savedquery", "savedqueryid", "returnedtypecode", entity, ct);

        if (existingViewRefs.Count > 0)
        {
            await RemoveAppComponentsAsync(client, appModuleId, existingViewRefs, ct);
        }

        if (!options.All)
        {
            var viewIds = await ResolveViewIdsAsync(client, entity, options.ComponentNames, ct);
            await AddAppComponentsAsync(client, appModuleId, "savedquery", viewIds, ct);
        }

        if (options.Solution != null)
        {
            var sitemapId = await GetSitemapIdAsync(client, appModuleIdUnique, ct);
            await AddToSolutionAsync(client, options.Solution, appModuleIdUnique, sitemapId, ct);
        }

        if (options.Publish)
        {
            await PublishAppAsync(client, uniqueName, ct);
        }
    }

    /// <inheritdoc />
    public async Task SetChartsAsync(string appName, string entity, ComponentSelectionOptions options, IProgressReporter? progress, CancellationToken ct)
    {
        ValidateComponentOptions(options, "--chart");

        await using var client = await _pool.GetClientAsync(cancellationToken: ct);
        var (appModuleId, appModuleIdUnique, uniqueName) = await ResolveAppAsync(appName, client, ct);

        await EnsureEntityInSitemapAsync(client, appModuleIdUnique, entity, appName, ct);

        progress?.ReportPhase("Updating charts", entity);

        var existingChartRefs = await GetExplicitEntityComponentsAsync(
            client, appModuleIdUnique, ComponentTypeChart, "savedqueryvisualization", "savedqueryvisualizationid", "primaryentitytypecode", entity, ct);

        if (existingChartRefs.Count > 0)
        {
            await RemoveAppComponentsAsync(client, appModuleId, existingChartRefs, ct);
        }

        if (!options.All)
        {
            var chartIds = await ResolveChartIdsAsync(client, entity, options.ComponentNames, ct);
            await AddAppComponentsAsync(client, appModuleId, "savedqueryvisualization", chartIds, ct);
        }

        if (options.Solution != null)
        {
            var sitemapId = await GetSitemapIdAsync(client, appModuleIdUnique, ct);
            await AddToSolutionAsync(client, options.Solution, appModuleIdUnique, sitemapId, ct);
        }

        if (options.Publish)
        {
            await PublishAppAsync(client, uniqueName, ct);
        }
    }

    // ── Private helpers ────────────────────────────────────────────────────────

    private async Task<(Guid AppModuleId, Guid AppModuleIdUnique, string UniqueName)> ResolveAppAsync(
        string appName,
        IPooledClient client,
        CancellationToken ct)
    {
        var query = new QueryExpression("appmodule")
        {
            ColumnSet = new ColumnSet("appmoduleid", "appmoduleidunique", "uniquename", "name"),
            TopCount = 50
        };

        EntityCollection result;
        try
        {
            result = await client.RetrieveMultipleAsync(query, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new PpdsException(ModelDrivenAppErrorCodes.GetFailed, $"Failed to resolve app '{appName}'.", ex);
        }

        var match = result.Entities.FirstOrDefault(e =>
            string.Equals(e.GetAttributeValue<string>("name"), appName, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(e.GetAttributeValue<string>("uniquename"), appName, StringComparison.OrdinalIgnoreCase));

        if (match == null)
        {
            var available = string.Join(", ", result.Entities
                .Select(e => e.GetAttributeValue<string>("name") ?? e.GetAttributeValue<string>("uniquename"))
                .Where(n => n != null));
            throw new PpdsException(ModelDrivenAppErrorCodes.AppNotFound,
                $"Model-driven app '{appName}' not found. Available apps: {available}");
        }

        return (
            match.GetAttributeValue<Guid>("appmoduleid"),
            match.GetAttributeValue<Guid>("appmoduleidunique"),
            match.GetAttributeValue<string>("uniquename") ?? string.Empty);
    }

    private async Task<Guid> GetSitemapIdAsync(IPooledClient client, Guid appModuleIdUnique, CancellationToken ct)
    {
        var query = new QueryExpression("appmodulecomponent")
        {
            ColumnSet = new ColumnSet("objectid"),
            TopCount = 1
        };
        query.Criteria.AddCondition("appmoduleidunique", ConditionOperator.Equal, appModuleIdUnique);
        query.Criteria.AddCondition("componenttype", ConditionOperator.Equal, ComponentTypeSitemap);

        var result = await client.RetrieveMultipleAsync(query, ct);
        var sitemapId = result.Entities.FirstOrDefault()?.GetAttributeValue<Guid>("objectid");

        if (sitemapId == null || sitemapId == Guid.Empty)
        {
            throw new PpdsException(ModelDrivenAppErrorCodes.GetFailed, "App has no associated sitemap record.");
        }

        return sitemapId.Value;
    }

    private async Task<string> FetchSitemapXmlAsync(IPooledClient client, Guid appModuleIdUnique, CancellationToken ct)
    {
        var sitemapId = await GetSitemapIdAsync(client, appModuleIdUnique, ct);
        return await FetchSitemapXmlByIdAsync(client, sitemapId, ct);
    }

    private async Task<string> FetchSitemapXmlByIdAsync(IPooledClient client, Guid sitemapId, CancellationToken ct)
    {
        var sitemap = await client.RetrieveAsync("sitemap", sitemapId, new ColumnSet("sitemapxml"), ct);
        return sitemap.GetAttributeValue<string>("sitemapxml") ?? "<SiteMap />";
    }

    private async Task PatchSitemapAsync(IPooledClient client, Guid sitemapId, string xml, CancellationToken ct)
    {
        var update = new Entity("sitemap", sitemapId)
        {
            ["sitemapxml"] = xml
        };
        try
        {
            await client.UpdateAsync(update, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new PpdsException(ModelDrivenAppErrorCodes.UpdateFailed, "Failed to update sitemap record.", ex);
        }
    }

    private async Task AddToSolutionAsync(IPooledClient client, string solutionName, Guid appModuleIdUnique, Guid sitemapId, CancellationToken ct)
    {
        try
        {
            var addApp = new AddSolutionComponentRequest
            {
                SolutionUniqueName = solutionName,
                ComponentId = appModuleIdUnique,
                ComponentType = ComponentTypeApp,
                AddRequiredComponents = false
            };
            await client.ExecuteAsync(addApp, ct);

            var addSitemap = new AddSolutionComponentRequest
            {
                SolutionUniqueName = solutionName,
                ComponentId = sitemapId,
                ComponentType = ComponentTypeSitemap,
                AddRequiredComponents = false
            };
            await client.ExecuteAsync(addSitemap, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to add app or sitemap to solution '{SolutionName}'", solutionName);
            throw new PpdsException(ModelDrivenAppErrorCodes.UpdateFailed,
                $"Failed to add components to solution '{solutionName}': {ex.Message}", ex);
        }
    }

    private async Task PublishAppAsync(IPooledClient client, string uniqueName, CancellationToken ct)
    {
        try
        {
            var request = new PublishXmlRequest
            {
                ParameterXml = $"<importexportxml><appmodules><appmodule>{uniqueName}</appmodule></appmodules></importexportxml>"
            };
            await client.ExecuteAsync(request, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to publish app '{UniqueName}'", uniqueName);
            throw new PpdsException(ModelDrivenAppErrorCodes.UpdateFailed,
                $"Sitemap updated. Publish failed: {ex.Message}. Retry with: ppds publish --app \"{uniqueName}\"", ex);
        }
    }

    private async Task<Dictionary<Guid, Dictionary<int, int>>> GetComponentCountsAsync(
        IPooledClient client,
        List<Guid> appModuleUniqueIds,
        CancellationToken ct)
    {
        if (appModuleUniqueIds.Count == 0)
        {
            return new Dictionary<Guid, Dictionary<int, int>>();
        }

        var query = new QueryExpression("appmodulecomponent")
        {
            ColumnSet = new ColumnSet("appmoduleidunique", "componenttype")
        };

        var appIds = appModuleUniqueIds.Select(id => (object)id).ToArray();
        query.Criteria.AddCondition("appmoduleidunique", ConditionOperator.In, appIds);

        var result = await client.RetrieveMultipleAsync(query, ct);

        var counts = new Dictionary<Guid, Dictionary<int, int>>();
        foreach (var e in result.Entities)
        {
            var appId = e.GetAttributeValue<Guid>("appmoduleidunique");
            var compType = e.GetAttributeValue<OptionSetValue>("componenttype")?.Value ?? 0;

            if (!counts.TryGetValue(appId, out var typeCounts))
            {
                typeCounts = new Dictionary<int, int>();
                counts[appId] = typeCounts;
            }

            typeCounts[compType] = typeCounts.GetValueOrDefault(compType) + 1;
        }

        return counts;
    }

    private async Task<Dictionary<int, int>> GetComponentCountsForAppAsync(
        IPooledClient client,
        Guid appModuleIdUnique,
        CancellationToken ct)
    {
        var query = new QueryExpression("appmodulecomponent")
        {
            ColumnSet = new ColumnSet("componenttype")
        };
        query.Criteria.AddCondition("appmoduleidunique", ConditionOperator.Equal, appModuleIdUnique);

        var result = await client.RetrieveMultipleAsync(query, ct);

        return result.Entities
            .GroupBy(e => e.GetAttributeValue<OptionSetValue>("componenttype")?.Value ?? 0)
            .ToDictionary(g => g.Key, g => g.Count());
    }

    private static SitemapStructure ParseSitemapStructure(string xml)
    {
        var doc = XDocument.Parse(xml);
        var areas = doc.Root?.Elements("Area").Select(areaEl => new SitemapArea(
            areaEl.Attribute("Id")?.Value ?? string.Empty,
            areaEl.Attribute("Title")?.Value,
            areaEl.Elements("Group").Select(groupEl => new SitemapGroup(
                groupEl.Attribute("Id")?.Value ?? string.Empty,
                groupEl.Attribute("Title")?.Value,
                groupEl.Elements("SubArea").Select(subEl => new SitemapSubArea(
                    subEl.Attribute("Id")?.Value ?? string.Empty,
                    subEl.Attribute("Entity")?.Value,
                    subEl.Attribute("Title")?.Value,
                    subEl.Attribute("Url")?.Value)).ToList()
            )).ToList()
        )).ToList() ?? [];

        return new SitemapStructure(areas);
    }

    private static XElement CreateAndAppendArea(XElement parent, string id, string title)
    {
        var el = new XElement("Area",
            new XAttribute("Id", id),
            new XAttribute("Title", title));
        parent.Add(el);
        return el;
    }

    private static XElement CreateAndAppendGroup(XElement parent, string id, string title)
    {
        var attrs = new List<XAttribute> { new("Id", id) };
        if (!string.IsNullOrEmpty(title))
        {
            attrs.Add(new XAttribute("Title", title));
        }

        var el = new XElement("Group", attrs);
        parent.Add(el);
        return el;
    }

    private async Task EnsureEntityInSitemapAsync(
        IPooledClient client,
        Guid appModuleIdUnique,
        string entity,
        string appName,
        CancellationToken ct)
    {
        var sitemapXml = await FetchSitemapXmlAsync(client, appModuleIdUnique, ct);
        var doc = XDocument.Parse(sitemapXml);
        var found = doc.Descendants("SubArea")
            .Any(s => string.Equals(s.Attribute("Entity")?.Value, entity, StringComparison.OrdinalIgnoreCase));

        if (!found)
        {
            throw new PpdsException(ModelDrivenAppErrorCodes.EntityNotInApp,
                $"Table '{entity}' is not in app '{appName}'. Add it first with: ppds model-driven-app add-table {entity} --app \"{appName}\"");
        }
    }

    private async Task<List<EntityReference>> GetExplicitEntityComponentsAsync(
        IPooledClient client,
        Guid appModuleIdUnique,
        int componentType,
        string componentEntityName,
        string componentPrimaryKey,
        string entityTypeField,
        string entityLogicalName,
        CancellationToken ct)
    {
        var query = new QueryExpression("appmodulecomponent")
        {
            ColumnSet = new ColumnSet("objectid")
        };
        query.Criteria.AddCondition("appmoduleidunique", ConditionOperator.Equal, appModuleIdUnique);
        query.Criteria.AddCondition("componenttype", ConditionOperator.Equal, componentType);

        var componentLink = query.AddLink(componentEntityName, "objectid", componentPrimaryKey);
        componentLink.EntityAlias = "comp";
        componentLink.LinkCriteria.AddCondition(entityTypeField, ConditionOperator.Equal, entityLogicalName);

        var result = await client.RetrieveMultipleAsync(query, ct);
        return result.Entities
            .Select(e => new EntityReference(componentEntityName, e.GetAttributeValue<Guid>("objectid")))
            .ToList();
    }

    private async Task RemoveExplicitComponentsForEntityAsync(
        IPooledClient client,
        Guid appModuleId,
        Guid appModuleIdUnique,
        string entity,
        CancellationToken ct)
    {
        var formRefs = await GetExplicitEntityComponentsAsync(
            client, appModuleIdUnique, ComponentTypeForm, "systemform", "formid", "objecttypecode", entity, ct);
        var viewRefs = await GetExplicitEntityComponentsAsync(
            client, appModuleIdUnique, ComponentTypeView, "savedquery", "savedqueryid", "returnedtypecode", entity, ct);
        var chartRefs = await GetExplicitEntityComponentsAsync(
            client, appModuleIdUnique, ComponentTypeChart, "savedqueryvisualization", "savedqueryvisualizationid", "primaryentitytypecode", entity, ct);

        var allRefs = formRefs.Concat(viewRefs).Concat(chartRefs).ToList();
        if (allRefs.Count > 0)
        {
            await RemoveAppComponentsAsync(client, appModuleId, allRefs, ct);
        }
    }

    private async Task RemoveAppComponentsAsync(
        IPooledClient client,
        Guid appModuleId,
        List<EntityReference> components,
        CancellationToken ct)
    {
        if (components.Count == 0)
        {
            return;
        }

        var request = new OrganizationRequest("RemoveAppComponents");
        request["AppId"] = new EntityReference("appmodule", appModuleId);
        request["Components"] = new EntityReferenceCollection(components);

        try
        {
            await client.ExecuteAsync(request, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to remove {Count} app components", components.Count);
            throw new PpdsException(ModelDrivenAppErrorCodes.UpdateFailed,
                $"Failed to remove app components: {ex.Message}", ex);
        }
    }

    private async Task AddAppComponentsAsync(
        IPooledClient client,
        Guid appModuleId,
        string componentEntityName,
        List<Guid> componentIds,
        CancellationToken ct)
    {
        if (componentIds.Count == 0)
        {
            return;
        }

        var refs = componentIds.Select(id => new EntityReference(componentEntityName, id)).ToList();
        var request = new OrganizationRequest("AddAppComponents");
        request["AppId"] = new EntityReference("appmodule", appModuleId);
        request["Components"] = new EntityReferenceCollection(refs);

        try
        {
            await client.ExecuteAsync(request, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new PpdsException(ModelDrivenAppErrorCodes.UpdateFailed,
                $"Failed to add app components: {ex.Message}", ex);
        }
    }

    private async Task<List<Guid>> ResolveFormIdsAsync(
        IPooledClient client,
        string entity,
        IReadOnlyList<string> formNames,
        CancellationToken ct)
    {
        var query = new QueryExpression("systemform")
        {
            ColumnSet = new ColumnSet("formid", "name")
        };
        query.Criteria.AddCondition("objecttypecode", ConditionOperator.Equal, entity);

        var result = await client.RetrieveMultipleAsync(query, ct);
        var available = result.Entities.ToDictionary(
            e => e.GetAttributeValue<string>("name") ?? string.Empty,
            e => e.GetAttributeValue<Guid>("formid"),
            StringComparer.OrdinalIgnoreCase);

        return ResolveComponentIds(formNames, available, "form", entity);
    }

    private async Task<List<Guid>> ResolveViewIdsAsync(
        IPooledClient client,
        string entity,
        IReadOnlyList<string> viewNames,
        CancellationToken ct)
    {
        var query = new QueryExpression("savedquery")
        {
            ColumnSet = new ColumnSet("savedqueryid", "name")
        };
        query.Criteria.AddCondition("returnedtypecode", ConditionOperator.Equal, entity);

        var result = await client.RetrieveMultipleAsync(query, ct);
        var available = result.Entities.ToDictionary(
            e => e.GetAttributeValue<string>("name") ?? string.Empty,
            e => e.GetAttributeValue<Guid>("savedqueryid"),
            StringComparer.OrdinalIgnoreCase);

        return ResolveComponentIds(viewNames, available, "view", entity);
    }

    private async Task<List<Guid>> ResolveChartIdsAsync(
        IPooledClient client,
        string entity,
        IReadOnlyList<string> chartNames,
        CancellationToken ct)
    {
        var query = new QueryExpression("savedqueryvisualization")
        {
            ColumnSet = new ColumnSet("savedqueryvisualizationid", "name")
        };
        query.Criteria.AddCondition("primaryentitytypecode", ConditionOperator.Equal, entity);

        var result = await client.RetrieveMultipleAsync(query, ct);
        var available = result.Entities.ToDictionary(
            e => e.GetAttributeValue<string>("name") ?? string.Empty,
            e => e.GetAttributeValue<Guid>("savedqueryvisualizationid"),
            StringComparer.OrdinalIgnoreCase);

        return ResolveComponentIds(chartNames, available, "chart", entity);
    }

    private static List<Guid> ResolveComponentIds(
        IReadOnlyList<string> names,
        Dictionary<string, Guid> available,
        string componentType,
        string entity)
    {
        var ids = new List<Guid>();
        foreach (var name in names)
        {
            if (!available.TryGetValue(name, out var id))
            {
                var availableNames = string.Join(", ", available.Keys.Take(10));
                throw new PpdsException(ModelDrivenAppErrorCodes.ComponentNotFound,
                    $"{componentType} '{name}' not found for entity '{entity}'. Available: {availableNames}");
            }

            ids.Add(id);
        }

        return ids;
    }

    private static void ValidateComponentOptions(ComponentSelectionOptions options, string optionName)
    {
        if (options.All && options.ComponentNames.Count > 0)
        {
            throw new PpdsException(ModelDrivenAppErrorCodes.InvalidArguments,
                $"--all and {optionName} are mutually exclusive. Specify one or the other.");
        }

        if (!options.All && options.ComponentNames.Count == 0)
        {
            throw new PpdsException(ModelDrivenAppErrorCodes.InvalidArguments,
                $"Either --all or at least one {optionName} is required.");
        }
    }
}

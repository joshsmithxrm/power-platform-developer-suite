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
            ColumnSet = new ColumnSet("appmoduleid", "name", "uniquename"),
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

        var appIds = result.Entities.Select(e => e.GetAttributeValue<Guid>("appmoduleid")).ToList();
        var componentCounts = await GetComponentCountsByAppIdAsync(client, appIds, ct);

        return result.Entities.Select(e =>
        {
            var appId = e.GetAttributeValue<Guid>("appmoduleid");
            var total = componentCounts.TryGetValue(appId, out var counts) ? counts.Values.Sum() : 0;
            return new ModelDrivenAppSummary(
                appId,
                e.GetAttributeValue<string>("name") ?? string.Empty,
                e.GetAttributeValue<string>("uniquename") ?? string.Empty,
                total);
        }).ToList();
    }

    /// <inheritdoc />
    public async Task<ModelDrivenAppDetails> GetAppAsync(string appName, CancellationToken ct)
    {
        await using var client = await _pool.GetClientAsync(cancellationToken: ct);

        var (appModuleId, _) = await ResolveAppAsync(appName, client, ct);

        var query = new QueryExpression("appmodule")
        {
            ColumnSet = new ColumnSet("appmoduleid", "name", "uniquename", "description", "publisherid"),
            TopCount = 1
        };
        query.Criteria.AddCondition("appmoduleid", ConditionOperator.Equal, appModuleId);

        var publisherLink = query.AddLink("publisher", "publisherid", "publisherid", JoinOperator.LeftOuter);
        publisherLink.EntityAlias = "pub";
        publisherLink.Columns.AddColumn("friendlyname");

        EntityCollection appResult;
        try
        {
            appResult = await client.RetrieveMultipleAsync(query, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new PpdsException(ModelDrivenAppErrorCodes.GetFailed, $"Failed to retrieve details for app '{appName}'.", ex);
        }

        var appEntity = appResult.Entities.FirstOrDefault()
            ?? throw new PpdsException(ModelDrivenAppErrorCodes.AppNotFound, $"App '{appName}' not found.");

        var publisherName = appEntity.GetAttributeValue<AliasedValue>("pub.friendlyname")?.Value as string;
        var description = appEntity.GetAttributeValue<string>("description");

        var counts = await GetComponentCountsForAppAsync(client, appModuleId, ct);

        return new ModelDrivenAppDetails(
            appModuleId,
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

        var (appModuleId, _) = await ResolveAppAsync(appName, client, ct);
        var xml = await FetchSitemapXmlForAppAsync(client, appModuleId, ct);

        return ParseSitemapStructure(xml);
    }

    /// <inheritdoc />
    public async Task SetSitemapXmlAsync(string appName, string xml, SetSitemapOptions options, IProgressReporter? progress, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(xml))
        {
            throw new PpdsValidationException("xml", "Sitemap XML cannot be empty.")
            {
                ErrorCode = ModelDrivenAppErrorCodes.InvalidSitemapXml
            };
        }

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
        var (appModuleId, uniqueName) = await ResolveAppAsync(appName, client, ct);
        var sitemapId = await GetSitemapIdAsync(client, appModuleId, ct);

        await PatchSitemapAsync(client, sitemapId, xml, ct);

        if (options.Solution != null)
        {
            await AddToSolutionAsync(client, options.Solution, appModuleId, sitemapId, ct);
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
        var (appModuleId, uniqueName) = await ResolveAppAsync(appName, client, ct);
        var sitemapId = await GetSitemapIdAsync(client, appModuleId, ct);
        var sitemapXml = await FetchSitemapXmlByIdAsync(client, sitemapId, ct);

        var doc = XDocument.Parse(sitemapXml);
        var siteMapEl = doc.Root!;

        // Locate or create Area
        XElement areaEl;
        if (!string.IsNullOrEmpty(options.Area))
        {
            var existingArea = siteMapEl.Elements("Area")
                .FirstOrDefault(a => string.Equals(a.Attribute("Id")?.Value, options.Area, StringComparison.OrdinalIgnoreCase)
                                  || string.Equals(a.Attribute("Title")?.Value, options.Area, StringComparison.OrdinalIgnoreCase));
            if (existingArea != null)
            {
                areaEl = existingArea;
            }
            else
            {
                var areaId = System.Text.RegularExpressions.Regex.Replace(options.Area, @"[^a-zA-Z0-9_]", "_");
                if (string.IsNullOrEmpty(areaId) || areaId.All(c => c == '_'))
                    areaId = $"area_{Guid.NewGuid():N}";
                areaEl = CreateAndAppendArea(siteMapEl, areaId, options.Area);
            }
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
            var existingGroup = areaEl.Elements("Group")
                .FirstOrDefault(g => string.Equals(g.Attribute("Id")?.Value, options.Group, StringComparison.OrdinalIgnoreCase)
                                  || string.Equals(g.Attribute("Title")?.Value, options.Group, StringComparison.OrdinalIgnoreCase));
            if (existingGroup != null)
            {
                groupEl = existingGroup;
            }
            else
            {
                var groupId = System.Text.RegularExpressions.Regex.Replace(options.Group, @"[^a-zA-Z0-9_]", "_");
                if (string.IsNullOrEmpty(groupId) || groupId.All(c => c == '_'))
                    groupId = $"group_{Guid.NewGuid():N}";
                groupEl = CreateAndAppendGroup(areaEl, groupId, options.Group);
            }
        }
        else
        {
            groupEl = areaEl.Elements("Group").FirstOrDefault()
                ?? CreateAndAppendGroup(areaEl, "Group1", string.Empty);
        }

        var existingEntities = siteMapEl.Descendants("SubArea")
            .Select(s => s.Attribute("Entity")?.Value)
            .Where(e => e != null)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var isFirst = true;
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

            // --title applies to first entity only; subsequent entities use their DisplayName.
            var title = (isFirst && !string.IsNullOrEmpty(options.Title)) ? options.Title : (entitySummary.DisplayName ?? entitySummary.LogicalName);
            isFirst = false;
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
            await AddToSolutionAsync(client, options.Solution, appModuleId, sitemapId, ct);
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
        var (appModuleId, uniqueName) = await ResolveAppAsync(appName, client, ct);
        var sitemapId = await GetSitemapIdAsync(client, appModuleId, ct);
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
        await RemoveExplicitComponentsForEntityAsync(client, appModuleId, entity, ct);

        var updatedXml = doc.ToString(SaveOptions.DisableFormatting);
        _validator.Validate(XDocument.Parse(updatedXml));

        progress?.ReportPhase("Updating sitemap");
        await PatchSitemapAsync(client, sitemapId, updatedXml, ct);

        if (options.Solution != null)
        {
            await AddToSolutionAsync(client, options.Solution, appModuleId, sitemapId, ct);
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
        var (appModuleId, uniqueName) = await ResolveAppAsync(appName, client, ct);

        await EnsureEntityInSitemapAsync(client, appModuleId, entity, appName, ct);

        progress?.ReportPhase("Updating forms", entity);

        var existingFormRefs = await GetExplicitEntityComponentsAsync(
            client, appModuleId, ComponentTypeForm, "systemform", "formid", "objecttypecode", entity, ct);

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
            var sitemapId = await GetSitemapIdAsync(client, appModuleId, ct);
            await AddToSolutionAsync(client, options.Solution, appModuleId, sitemapId, ct);
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
        var (appModuleId, uniqueName) = await ResolveAppAsync(appName, client, ct);

        await EnsureEntityInSitemapAsync(client, appModuleId, entity, appName, ct);

        progress?.ReportPhase("Updating views", entity);

        var existingViewRefs = await GetExplicitEntityComponentsAsync(
            client, appModuleId, ComponentTypeView, "savedquery", "savedqueryid", "returnedtypecode", entity, ct);

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
            var sitemapId = await GetSitemapIdAsync(client, appModuleId, ct);
            await AddToSolutionAsync(client, options.Solution, appModuleId, sitemapId, ct);
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
        var (appModuleId, uniqueName) = await ResolveAppAsync(appName, client, ct);

        await EnsureEntityInSitemapAsync(client, appModuleId, entity, appName, ct);

        progress?.ReportPhase("Updating charts", entity);

        var existingChartRefs = await GetExplicitEntityComponentsAsync(
            client, appModuleId, ComponentTypeChart, "savedqueryvisualization", "savedqueryvisualizationid", "primaryentitytypecode", entity, ct);

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
            var sitemapId = await GetSitemapIdAsync(client, appModuleId, ct);
            await AddToSolutionAsync(client, options.Solution, appModuleId, sitemapId, ct);
        }

        if (options.Publish)
        {
            await PublishAppAsync(client, uniqueName, ct);
        }
    }

    // ── Private helpers ────────────────────────────────────────────────────────

    private async Task<(Guid AppModuleId, string UniqueName)> ResolveAppAsync(
        string appName,
        IPooledClient client,
        CancellationToken ct)
    {
        // Filter server-side to avoid over-fetching all apps (no TopCount cap).
        var query = new QueryExpression("appmodule")
        {
            ColumnSet = new ColumnSet("appmoduleid", "uniquename", "name"),
            TopCount = 1
        };

        var nameFilter = new FilterExpression(LogicalOperator.Or);
        nameFilter.AddCondition("name", ConditionOperator.Equal, appName);
        nameFilter.AddCondition("uniquename", ConditionOperator.Equal, appName);
        query.Criteria.AddFilter(nameFilter);

        EntityCollection result;
        try
        {
            result = await client.RetrieveMultipleAsync(query, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new PpdsException(ModelDrivenAppErrorCodes.GetFailed, $"Failed to resolve app '{appName}'.", ex);
        }

        var match = result.Entities.FirstOrDefault();

        if (match == null)
        {
            throw new PpdsException(ModelDrivenAppErrorCodes.AppNotFound,
                $"Model-driven app '{appName}' not found. Run 'ppds model-driven-app list' to see available apps.");
        }

        return (
            match.GetAttributeValue<Guid>("appmoduleid"),
            match.GetAttributeValue<string>("uniquename") ?? string.Empty);
    }

    // Queries appmodulecomponent joined to appmodule via appmoduleidunique relationship,
    // filtering by appmodule.appmoduleid to avoid EntityReference cast issues.
    private async Task<Guid> GetSitemapIdAsync(IPooledClient client, Guid appModuleId, CancellationToken ct)
    {
        var query = new QueryExpression("appmodulecomponent")
        {
            ColumnSet = new ColumnSet("objectid"),
            TopCount = 1
        };
        query.Criteria.AddCondition("componenttype", ConditionOperator.Equal, ComponentTypeSitemap);

        var appLink = query.AddLink("appmodule", "appmoduleidunique", "appmoduleidunique", JoinOperator.Inner);
        appLink.LinkCriteria.AddCondition("appmoduleid", ConditionOperator.Equal, appModuleId);

        EntityCollection result;
        try
        {
            result = await client.RetrieveMultipleAsync(query, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new PpdsException(ModelDrivenAppErrorCodes.GetFailed, "Failed to retrieve sitemap component.", ex);
        }

        var sitemapId = result.Entities.FirstOrDefault()?.GetAttributeValue<Guid>("objectid");

        if (sitemapId == null || sitemapId == Guid.Empty)
        {
            throw new PpdsException(ModelDrivenAppErrorCodes.GetFailed, "App has no associated sitemap record.");
        }

        return sitemapId.Value;
    }

    private async Task<string> FetchSitemapXmlForAppAsync(IPooledClient client, Guid appModuleId, CancellationToken ct)
    {
        var sitemapId = await GetSitemapIdAsync(client, appModuleId, ct);
        return await FetchSitemapXmlByIdAsync(client, sitemapId, ct);
    }

    private async Task<string> FetchSitemapXmlByIdAsync(IPooledClient client, Guid sitemapId, CancellationToken ct)
    {
        Entity sitemap;
        try
        {
            sitemap = await client.RetrieveAsync("sitemap", sitemapId, new ColumnSet("sitemapxml"), ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new PpdsException(ModelDrivenAppErrorCodes.GetFailed, "Failed to retrieve sitemap XML.", ex);
        }

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

    private async Task AddToSolutionAsync(IPooledClient client, string solutionName, Guid appModuleId, Guid sitemapId, CancellationToken ct)
    {
        try
        {
            var addApp = new AddSolutionComponentRequest
            {
                SolutionUniqueName = solutionName,
                ComponentId = appModuleId,
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
            // Escape uniqueName to prevent malformed XML if the app name contains special characters.
            var escapedName = System.Security.SecurityElement.Escape(uniqueName) ?? uniqueName;
            var request = new PublishXmlRequest
            {
                ParameterXml = $"<importexportxml><appmodules><appmodule>{escapedName}</appmodule></appmodules></importexportxml>"
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

    // Uses link-entity to avoid EntityReference cast issues with appmoduleidunique on appmodulecomponent.
    // Groups by appmoduleid from the joined appmodule entity.
    private async Task<Dictionary<Guid, Dictionary<int, int>>> GetComponentCountsByAppIdAsync(
        IPooledClient client,
        List<Guid> appModuleIds,
        CancellationToken ct)
    {
        if (appModuleIds.Count == 0)
        {
            return [];
        }

        var query = new QueryExpression("appmodulecomponent")
        {
            ColumnSet = new ColumnSet("componenttype")
        };

        var appLink = query.AddLink("appmodule", "appmoduleidunique", "appmoduleidunique", JoinOperator.Inner);
        appLink.EntityAlias = "am";
        appLink.Columns.AddColumn("appmoduleid");

        var appIdArray = appModuleIds.Select(id => (object)id).ToArray();
        appLink.LinkCriteria.AddCondition("appmoduleid", ConditionOperator.In, appIdArray);

        EntityCollection result;
        try
        {
            result = await client.RetrieveMultipleAsync(query, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new PpdsException(ModelDrivenAppErrorCodes.ListFailed, "Failed to retrieve component counts for apps.", ex);
        }

        var counts = new Dictionary<Guid, Dictionary<int, int>>();
        foreach (var e in result.Entities)
        {
            if (e.GetAttributeValue<AliasedValue>("am.appmoduleid")?.Value is not Guid appId)
            {
                continue;
            }

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
        Guid appModuleId,
        CancellationToken ct)
    {
        var query = new QueryExpression("appmodulecomponent")
        {
            ColumnSet = new ColumnSet("componenttype")
        };

        var appLink = query.AddLink("appmodule", "appmoduleidunique", "appmoduleidunique", JoinOperator.Inner);
        appLink.LinkCriteria.AddCondition("appmoduleid", ConditionOperator.Equal, appModuleId);

        EntityCollection result;
        try
        {
            result = await client.RetrieveMultipleAsync(query, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new PpdsException(ModelDrivenAppErrorCodes.GetFailed, "Failed to retrieve component counts.", ex);
        }

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
        Guid appModuleId,
        string entity,
        string appName,
        CancellationToken ct)
    {
        var sitemapXml = await FetchSitemapXmlForAppAsync(client, appModuleId, ct);
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
        Guid appModuleId,
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
        query.Criteria.AddCondition("componenttype", ConditionOperator.Equal, componentType);

        var appLink = query.AddLink("appmodule", "appmoduleidunique", "appmoduleidunique", JoinOperator.Inner);
        appLink.LinkCriteria.AddCondition("appmoduleid", ConditionOperator.Equal, appModuleId);

        var componentLink = query.AddLink(componentEntityName, "objectid", componentPrimaryKey);
        componentLink.EntityAlias = "comp";
        componentLink.LinkCriteria.AddCondition(entityTypeField, ConditionOperator.Equal, entityLogicalName);

        EntityCollection result;
        try
        {
            result = await client.RetrieveMultipleAsync(query, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new PpdsException(ModelDrivenAppErrorCodes.GetFailed, $"Failed to retrieve {componentEntityName} components.", ex);
        }

        return result.Entities
            .Select(e => new EntityReference(componentEntityName, e.GetAttributeValue<Guid>("objectid")))
            .ToList();
    }

    private async Task RemoveExplicitComponentsForEntityAsync(
        IPooledClient client,
        Guid appModuleId,
        string entity,
        CancellationToken ct)
    {
        // Remove explicit forms, views, and charts (AC-19)
        var formRefs = await GetExplicitEntityComponentsAsync(
            client, appModuleId, ComponentTypeForm, "systemform", "formid", "objecttypecode", entity, ct);
        var viewRefs = await GetExplicitEntityComponentsAsync(
            client, appModuleId, ComponentTypeView, "savedquery", "savedqueryid", "returnedtypecode", entity, ct);
        var chartRefs = await GetExplicitEntityComponentsAsync(
            client, appModuleId, ComponentTypeChart, "savedqueryvisualization", "savedqueryvisualizationid", "primaryentitytypecode", entity, ct);

        var componentRefs = formRefs.Concat(viewRefs).Concat(chartRefs).ToList();
        if (componentRefs.Count > 0)
        {
            await RemoveAppComponentsAsync(client, appModuleId, componentRefs, ct);
        }

        // Remove entity component (type 1) via metadata ID (AC-20)
        var entitySummaries = await _metadata.GetEntitiesAsync(ct);
        var entityMeta = entitySummaries.FirstOrDefault(
            e => string.Equals(e.LogicalName, entity, StringComparison.OrdinalIgnoreCase));

        if (entityMeta != null && entityMeta.MetadataId != Guid.Empty)
        {
            var entityRef = new EntityReference("entity", entityMeta.MetadataId);
            await RemoveAppComponentsAsync(client, appModuleId, [entityRef], ct);
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

        EntityCollection result;
        try { result = await client.RetrieveMultipleAsync(query, ct); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new PpdsException(ModelDrivenAppErrorCodes.GetFailed, $"Failed to retrieve forms for entity '{entity}'.", ex);
        }

        var available = result.Entities
            .GroupBy(e => e.GetAttributeValue<string>("name") ?? string.Empty, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().GetAttributeValue<Guid>("formid"), StringComparer.OrdinalIgnoreCase);

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

        EntityCollection result;
        try { result = await client.RetrieveMultipleAsync(query, ct); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new PpdsException(ModelDrivenAppErrorCodes.GetFailed, $"Failed to retrieve views for entity '{entity}'.", ex);
        }

        var available = result.Entities
            .GroupBy(e => e.GetAttributeValue<string>("name") ?? string.Empty, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().GetAttributeValue<Guid>("savedqueryid"), StringComparer.OrdinalIgnoreCase);

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

        EntityCollection result;
        try { result = await client.RetrieveMultipleAsync(query, ct); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new PpdsException(ModelDrivenAppErrorCodes.GetFailed, $"Failed to retrieve charts for entity '{entity}'.", ex);
        }

        var available = result.Entities
            .GroupBy(e => e.GetAttributeValue<string>("name") ?? string.Empty, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().GetAttributeValue<Guid>("savedqueryvisualizationid"), StringComparer.OrdinalIgnoreCase);

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

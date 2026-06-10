using System.Xml.Linq;
using Microsoft.Crm.Sdk.Messages;
using Microsoft.Extensions.Logging;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using PPDS.Cli.Infrastructure;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Cli.Infrastructure.Progress;
using PPDS.Cli.Services.Environment;
using PPDS.Cli.Services.WebApi;
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
    private readonly IEnvironmentConfigService _envConfig;
    private readonly ResolvedConnectionInfo _connectionInfo;

    private const int ComponentTypeEntity = 1;
    private const int ComponentTypeView = 26;
    private const int ComponentTypeChart = 59;
    private const int ComponentTypeForm = 60;
    private const int ComponentTypeSitemap = 62;
    private const int ComponentTypeApp = 80;

    // appelement.uniquename is a unique key with a 100-character maximum.
    private const int MaxUniqueNameLength = 100;

    /// <summary>
    /// Initializes a new instance of the <see cref="ModelDrivenAppService"/> class.
    /// </summary>
    public ModelDrivenAppService(
        IDataverseConnectionPool pool,
        ICachedMetadataProvider metadata,
        SitemapXmlValidator validator,
        ILogger<ModelDrivenAppService> logger,
        IEnvironmentConfigService envConfig,
        ResolvedConnectionInfo connectionInfo)
    {
        _pool = pool ?? throw new ArgumentNullException(nameof(pool));
        _metadata = metadata ?? throw new ArgumentNullException(nameof(metadata));
        _validator = validator ?? throw new ArgumentNullException(nameof(validator));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _envConfig = envConfig ?? throw new ArgumentNullException(nameof(envConfig));
        _connectionInfo = connectionInfo ?? throw new ArgumentNullException(nameof(connectionInfo));
    }

    // ── Production write protection (issue #1195) ───────────────────────────────

    /// <summary>
    /// Mirrors the <c>ppds api request</c> guard for SDK-based writes: blocks a mutating operation on a
    /// Production-flagged environment unless the caller passed --confirm. Protection-level resolution is
    /// shared with the api-request path via <see cref="WriteProtectionResolver"/> (no divergent copy).
    /// </summary>
    private async Task EnsureWriteAllowedAsync(bool confirm, CancellationToken ct)
    {
        var level = await WriteProtectionResolver.ResolveAsync(_envConfig, _connectionInfo.EnvironmentUrl, ct);
        if (WebApiWriteGuard.IsBlocked(level, confirm))
        {
            throw new PpdsException(
                ModelDrivenAppErrorCodes.WriteBlockedOnProduction,
                $"Mutating request blocked on Production environment '{_connectionInfo.EnvironmentUrl}'. Add --confirm to proceed.");
        }
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
            var request = new RetrieveUnpublishedMultipleRequest { Query = query };
            var response = (RetrieveUnpublishedMultipleResponse)await client.ExecuteAsync(request, ct);
            result = response.EntityCollection;
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
    public async Task<SitemapStructure> GetSitemapAsync(string appName, bool unpublished, CancellationToken ct)
    {
        await using var client = await _pool.GetClientAsync(cancellationToken: ct);

        var (appModuleId, _) = await ResolveAppAsync(appName, client, ct);
        var xml = await FetchSitemapXmlForAppAsync(client, appModuleId, ct, unpublished);

        return ParseSitemapStructure(xml);
    }

    /// <inheritdoc />
    public async Task<string> GetSitemapXmlAsync(string appName, bool unpublished, CancellationToken ct)
    {
        await using var client = await _pool.GetClientAsync(cancellationToken: ct);

        var (appModuleId, _) = await ResolveAppAsync(appName, client, ct);
        return await FetchSitemapXmlForAppAsync(client, appModuleId, ct, unpublished);
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
        await EnsureWriteAllowedAsync(options.Confirm, ct);
        var sitemapId = await GetSitemapIdAsync(client, appModuleId, ct);

        await PatchSitemapAsync(client, sitemapId, xml, ct);

        if (options.Solution != null)
        {
            await AddToSolutionAsync(client, options.Solution, appModuleId, sitemapId, ct);
        }

        if (options.Publish)
        {
            await PublishAppAsync(client, appModuleId, uniqueName, ct);
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
        await EnsureWriteAllowedAsync(options.Confirm, ct);
        var existingSitemapId = await TryGetSitemapIdAsync(client, appModuleId, ct);
        var sitemapXml = existingSitemapId.HasValue
            ? await FetchSitemapXmlByIdAsync(client, existingSitemapId.Value, ct, unpublished: true)
            : "<SiteMap />";

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
        var entityComponents = new EntityReferenceCollection();
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

            // Register the table component by its OWN logical name, not the literal "entity":
            // "entity" is itself a real table (the metadata-definition table), so AddAppComponents
            // would resolve the reference to that table (adding a spurious "Entity" component) and
            // ignore the table we mean. The objectid Dataverse stores is the table's MetadataId.
            if (entitySummary.MetadataId != Guid.Empty)
                entityComponents.Add(new EntityReference(entitySummary.LogicalName, entitySummary.MetadataId));
        }

        var updatedXml = doc.ToString(SaveOptions.DisableFormatting);
        _validator.Validate(XDocument.Parse(updatedXml));

        Guid sitemapId;
        if (existingSitemapId.HasValue)
        {
            progress?.ReportPhase("Updating sitemap");
            await PatchSitemapAsync(client, existingSitemapId.Value, updatedXml, ct);
            sitemapId = existingSitemapId.Value;
        }
        else
        {
            progress?.ReportPhase("Creating sitemap");
            sitemapId = await CreateSitemapForAppAsync(client, appModuleId, uniqueName, updatedXml, ct);
        }

        if (entityComponents.Count > 0)
        {
            progress?.ReportPhase("Registering table components");
            await AddAppComponentsAsync(client, appModuleId, entityComponents, ct);
        }

        if (options.Solution != null)
        {
            await AddToSolutionAsync(client, options.Solution, appModuleId, sitemapId, ct);
        }

        if (options.Publish)
        {
            await PublishAppAsync(client, appModuleId, uniqueName, ct);
        }
    }

    /// <inheritdoc />
    public async Task RemoveTableAsync(string appName, string entity, ModifyOptions options, IProgressReporter? progress, CancellationToken ct)
    {
        progress?.ReportPhase("Loading app", appName);

        await using var client = await _pool.GetClientAsync(cancellationToken: ct);
        var (appModuleId, uniqueName) = await ResolveAppAsync(appName, client, ct);
        await EnsureWriteAllowedAsync(options.Confirm, ct);
        var sitemapId = await GetSitemapIdAsync(client, appModuleId, ct);
        var sitemapXml = await FetchSitemapXmlByIdAsync(client, sitemapId, ct, unpublished: true);

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
            await PublishAppAsync(client, appModuleId, uniqueName, ct);
        }
    }

    /// <inheritdoc />
    public async Task SetFormsAsync(string appName, string entity, ComponentSelectionOptions options, IProgressReporter? progress, CancellationToken ct)
    {
        ValidateComponentOptions(options, "--form");

        await using var client = await _pool.GetClientAsync(cancellationToken: ct);
        var (appModuleId, uniqueName) = await ResolveAppAsync(appName, client, ct);
        await EnsureWriteAllowedAsync(options.Confirm, ct);

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
            await PublishAppAsync(client, appModuleId, uniqueName, ct);
        }
    }

    /// <inheritdoc />
    public async Task SetViewsAsync(string appName, string entity, ComponentSelectionOptions options, IProgressReporter? progress, CancellationToken ct)
    {
        ValidateComponentOptions(options, "--view");

        await using var client = await _pool.GetClientAsync(cancellationToken: ct);
        var (appModuleId, uniqueName) = await ResolveAppAsync(appName, client, ct);
        await EnsureWriteAllowedAsync(options.Confirm, ct);

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
            await PublishAppAsync(client, appModuleId, uniqueName, ct);
        }
    }

    /// <inheritdoc />
    public async Task SetChartsAsync(string appName, string entity, ComponentSelectionOptions options, IProgressReporter? progress, CancellationToken ct)
    {
        ValidateComponentOptions(options, "--chart");

        await using var client = await _pool.GetClientAsync(cancellationToken: ct);
        var (appModuleId, uniqueName) = await ResolveAppAsync(appName, client, ct);
        await EnsureWriteAllowedAsync(options.Confirm, ct);

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
            await PublishAppAsync(client, appModuleId, uniqueName, ct);
        }
    }

    /// <inheritdoc />
    public async Task<CopilotChangeResult> AddCopilotAsync(
        string appName, string bot, CopilotOptions options, IProgressReporter? progress, CancellationToken ct)
    {
        progress?.ReportPhase("Loading app", appName);

        await using var client = await _pool.GetClientAsync(cancellationToken: ct);
        var (appModuleId, appDisplayName, appUniqueName) = await ResolveAppRecordAsync(appName, client, ct);
        var (botId, botName, botSchemaName) = await ResolveBotAsync(bot, client, ct);

        // Best-effort guard. appelement reads can lag writes, so a freshly-added binding may not
        // be visible yet — this catches the common case without promising perfect idempotency.
        var existing = await FindCopilotBindingsAsync(client, appModuleId, botId, ct);
        if (existing.Count > 0)
        {
            throw new PpdsException(ModelDrivenAppErrorCodes.CopilotAlreadyInApp,
                $"Copilot '{botName}' is already wired into app '{appName}'. " +
                $"Remove it first with: ppds model-driven-app remove-copilot --app \"{appName}\" --bot \"{bot}\"");
        }

        // Eligibility preflight (issue #1192): some apps never render an app-assistant agent, so wiring
        // the binding silently fails to surface a Copilot. Evaluate before any write.
        progress?.ReportPhase("Checking eligibility", appDisplayName);
        var eligibilityReason = await EvaluateCopilotEligibilityAsync(
            client, appModuleId, appDisplayName, appUniqueName, ct);

        // Cap at the appelement.uniquename 100-char limit; an over-length value throws a non-duplicate
        // exception on create that the fallback path would not recognize.
        var baseUniqueName = Truncate($"{SchemaPrefix(botSchemaName)}_{appUniqueName}_schemaname_{botSchemaName}", MaxUniqueNameLength);

        if (options.DryRun)
        {
            // Report the verdict without writing; --dry-run never blocks (no mutation occurs).
            return new CopilotChangeResult(appName, appModuleId, botId, botName, botSchemaName,
                AppElementId: null, baseUniqueName, DryRun: true, Published: false,
                EligibilityReason: eligibilityReason, Forced: false);
        }

        var forced = false;
        if (eligibilityReason != null)
        {
            if (!options.Force)
            {
                throw new PpdsException(ModelDrivenAppErrorCodes.CopilotAppUnsupported,
                    $"App '{appName}' does not support the model-driven app assistant agent: {eligibilityReason} " +
                    "See https://learn.microsoft.com/en-us/power-apps/maker/model-driven-apps/add-app-assistant-agent (Limitations). " +
                    "Re-run with --force to wire it anyway.");
            }

            forced = true;
            // Surface the override on stderr (command) and the daemon/RPC log.
            _logger.LogWarning(
                "add-copilot eligibility check overridden by --force for app '{App}': {Reason}", appName, eligibilityReason);
        }

        // Production write protection (issue #1195): block on a Production-flagged env without --confirm.
        await EnsureWriteAllowedAsync(options.Confirm, ct);

        progress?.ReportPhase("Wiring Copilot", botName);
        Guid appElementId;
        string uniqueName;
        try
        {
            (appElementId, uniqueName) = await CreateCopilotAppElementAsync(
                client, appModuleId, botId, botSchemaName, baseUniqueName, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new PpdsException(ModelDrivenAppErrorCodes.UpdateFailed,
                $"Failed to wire Copilot '{botName}' into app '{appName}': {ex.Message}", ex);
        }

        var published = false;
        if (options.Publish)
        {
            await PublishAppAsync(client, appModuleId, appUniqueName, ct);
            published = true;
        }

        return new CopilotChangeResult(appName, appModuleId, botId, botName, botSchemaName,
            appElementId, uniqueName, DryRun: false, published,
            EligibilityReason: forced ? eligibilityReason : null, Forced: forced);
    }

    /// <inheritdoc />
    public async Task<CopilotChangeResult> RemoveCopilotAsync(
        string appName, string bot, CopilotOptions options, IProgressReporter? progress, CancellationToken ct)
    {
        progress?.ReportPhase("Loading app", appName);

        await using var client = await _pool.GetClientAsync(cancellationToken: ct);
        var (appModuleId, appUniqueName) = await ResolveAppAsync(appName, client, ct);
        var (botId, botName, botSchemaName) = await ResolveBotAsync(bot, client, ct);

        var bindings = await FindCopilotBindingsAsync(client, appModuleId, botId, ct);
        if (bindings.Count == 0)
        {
            throw new PpdsException(ModelDrivenAppErrorCodes.CopilotNotInApp,
                $"Copilot '{botName}' is not wired into app '{appName}'.");
        }

        if (options.DryRun)
        {
            return new CopilotChangeResult(appName, appModuleId, botId, botName, botSchemaName,
                bindings[0].AppElementId, bindings[0].UniqueName, DryRun: true, Published: false);
        }

        // Production write protection (issue #1195): block on a Production-flagged env without --confirm.
        await EnsureWriteAllowedAsync(options.Confirm, ct);

        progress?.ReportPhase("Removing Copilot", botName);
        foreach (var binding in bindings)
        {
            try
            {
                await client.DeleteAsync("appelement", binding.AppElementId, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                throw new PpdsException(ModelDrivenAppErrorCodes.UpdateFailed,
                    $"Failed to remove Copilot '{botName}' from app '{appName}': {ex.Message}", ex);
            }
        }

        var published = false;
        if (options.Publish)
        {
            await PublishAppAsync(client, appModuleId, appUniqueName, ct);
            published = true;
        }

        return new CopilotChangeResult(appName, appModuleId, botId, botName, botSchemaName,
            bindings[0].AppElementId, bindings[0].UniqueName, DryRun: false, published);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<CopilotBinding>> ListCopilotsAsync(string appName, CancellationToken ct)
    {
        await using var client = await _pool.GetClientAsync(cancellationToken: ct);
        var (appModuleId, _) = await ResolveAppAsync(appName, client, ct);
        return await FindCopilotBindingsAsync(client, appModuleId, botId: null, ct);
    }

    /// <inheritdoc />
    public async Task<AppAssistantDiagnostics> InspectAppAssistantAsync(string appName, CancellationToken ct)
    {
        await using var client = await _pool.GetClientAsync(cancellationToken: ct);
        var (appModuleId, _) = await ResolveAppAsync(appName, client, ct);

        // Reuse the #1186 binding-discovery helper for bot-bound appelements (findings 1 & 2),
        // then a sibling read for orphan (null-objectid) copilot-shaped rows (finding 3).
        // Both reads are RetrieveMultiple only — this method never mutates.
        var bindings = await FindCopilotBindingsAsync(client, appModuleId, botId: null, ct);
        var orphans = await FindOrphanCopilotAppElementsAsync(client, appModuleId, ct);

        var distinctBotIds = bindings.Select(b => b.BotId).Distinct().ToList();
        var botInfo = await GetBotAppAssistantInfoAsync(client, distinctBotIds, ct);

        var findings = new List<AppAssistantFinding>();

        foreach (var group in bindings.GroupBy(b => b.BotId))
        {
            var botId = group.Key;
            var appElementIds = group.Select(b => b.AppElementId).ToList();
            var hasInfo = botInfo.TryGetValue(botId, out var info);
            var botName = (hasInfo ? info!.Name : null) ?? group.First().BotName;
            var botLabel = botName ?? botId.ToString();
            bool? isLightweight = hasInfo ? info!.IsLightweightBot : null;

            // Finding 1: bound but not an app-assistant (islightweightbot != true) → never renders in-app.
            if (isLightweight != true)
            {
                findings.Add(new AppAssistantFinding(
                    AppAssistantFindingKind.NotAppAssistant,
                    botName, botId, isLightweight, appElementIds,
                    $"Bot '{botLabel}' is bound but is not an app assistant (isLightweightBot=false), " +
                    "so it will not render in-app. Configure it as an app assistant in Copilot Studio, " +
                    $"or remove the binding: ppds model-driven-app remove-copilot --app \"{appName}\" --bot \"{botId}\"."));
            }

            // Finding 2: more than one appelement binds the same bot.
            if (appElementIds.Count > 1)
            {
                findings.Add(new AppAssistantFinding(
                    AppAssistantFindingKind.DuplicateBinding,
                    botName, botId, isLightweight, appElementIds,
                    $"Bot '{botLabel}' is bound by {appElementIds.Count} appelements; keep one and remove the " +
                    $"rest with: ppds model-driven-app remove-copilot --app \"{appName}\" --bot \"{botId}\"."));
            }
        }

        // Finding 3: orphan copilot-shaped appelements with a null objectid (no target bot).
        foreach (var orphanId in orphans)
        {
            findings.Add(new AppAssistantFinding(
                AppAssistantFindingKind.OrphanAppElement,
                BotName: null, BotId: null, IsLightweightBot: null, new[] { orphanId },
                $"Appelement {orphanId} is copilot-shaped but has a null objectid (no target bot); " +
                "it is a dangling row left by a prior failed wiring. Remove it in the maker portal."));
        }

        return new AppAssistantDiagnostics(appName, appModuleId, findings);
    }

    // ── Private helpers ────────────────────────────────────────────────────────

    private async Task<(Guid AppModuleId, string UniqueName)> ResolveAppAsync(
        string appName,
        IPooledClient client,
        CancellationToken ct)
    {
        var (appModuleId, _, uniqueName) = await ResolveAppRecordAsync(appName, client, ct);
        return (appModuleId, uniqueName);
    }

    // Resolves the appmodule and surfaces its display name as well as the unique name.
    // Used by add-copilot's eligibility preflight, which matches on both fields.
    private async Task<(Guid AppModuleId, string Name, string UniqueName)> ResolveAppRecordAsync(
        string appName,
        IPooledClient client,
        CancellationToken ct)
    {
        // appmodule is a publishable entity. RetrieveMultiple only returns published records, so draft
        // apps (never published) would not be found — the query below uses RetrieveUnpublishedMultiple
        // so both draft and published apps are visible. Filter server-side to avoid over-fetching.
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
            var request = new RetrieveUnpublishedMultipleRequest { Query = query };
            var response = (RetrieveUnpublishedMultipleResponse)await client.ExecuteAsync(request, ct);
            result = response.EntityCollection;
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
            match.GetAttributeValue<string>("name") ?? string.Empty,
            match.GetAttributeValue<string>("uniquename") ?? string.Empty);
    }

    // Resolves a Copilot (bot) by id, schema name, or display name.
    private async Task<(Guid BotId, string Name, string SchemaName)> ResolveBotAsync(
        string bot, IPooledClient client, CancellationToken ct)
    {
        var query = new QueryExpression("bot")
        {
            ColumnSet = new ColumnSet("botid", "name", "schemaname"),
            TopCount = 2
        };

        if (Guid.TryParse(bot, out var botGuid))
        {
            query.Criteria.AddCondition("botid", ConditionOperator.Equal, botGuid);
        }
        else
        {
            var filter = new FilterExpression(LogicalOperator.Or);
            filter.AddCondition("name", ConditionOperator.Equal, bot);
            filter.AddCondition("schemaname", ConditionOperator.Equal, bot);
            query.Criteria.AddFilter(filter);
        }

        EntityCollection result;
        try
        {
            result = await client.RetrieveMultipleAsync(query, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new PpdsException(ModelDrivenAppErrorCodes.GetFailed, $"Failed to resolve Copilot '{bot}'.", ex);
        }

        if (result.Entities.Count == 0)
        {
            throw new PpdsException(ModelDrivenAppErrorCodes.CopilotNotFound,
                $"Copilot (bot) '{bot}' not found. Provide the bot's display name, schema name, or id.");
        }

        if (result.Entities.Count > 1)
        {
            throw new PpdsException(ModelDrivenAppErrorCodes.CopilotAmbiguous,
                $"Multiple Copilots match '{bot}'. Specify the bot id or schema name to disambiguate.");
        }

        var entity = result.Entities[0];
        return (
            entity.GetAttributeValue<Guid>("botid"),
            entity.GetAttributeValue<string>("name") ?? string.Empty,
            entity.GetAttributeValue<string>("schemaname") ?? string.Empty);
    }

    // Finds appelement rows in the app whose polymorphic objectid targets the bot table.
    // When botId is supplied, restricts to that bot. Non-bot bindings (aiskillconfig, mcpserver)
    // and unbound rows are filtered out by the objectid logical name.
    private async Task<List<CopilotBinding>> FindCopilotBindingsAsync(
        IPooledClient client, Guid appModuleId, Guid? botId, CancellationToken ct)
    {
        var query = new QueryExpression("appelement")
        {
            ColumnSet = new ColumnSet("appelementid", "uniquename", "name", "objectid")
        };
        query.Criteria.AddCondition("parentappmoduleid", ConditionOperator.Equal, appModuleId);
        if (botId.HasValue)
        {
            query.Criteria.AddCondition("objectid", ConditionOperator.Equal, botId.Value);
        }

        EntityCollection result;
        try
        {
            result = await client.RetrieveMultipleAsync(query, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new PpdsException(ModelDrivenAppErrorCodes.GetFailed, "Failed to query Copilot bindings.", ex);
        }

        var bindings = new List<CopilotBinding>();
        foreach (var entity in result.Entities)
        {
            var objectRef = entity.GetAttributeValue<EntityReference>("objectid");
            if (objectRef == null || !string.Equals(objectRef.LogicalName, "bot", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            // RetrieveMultiple does not populate EntityReference.Name; the bot display name comes from
            // the formatted value of the lookup column (falling back to the reference name if absent).
            var botDisplayName = entity.FormattedValues.Contains("objectid")
                ? entity.FormattedValues["objectid"]
                : objectRef.Name;

            bindings.Add(new CopilotBinding(
                entity.GetAttributeValue<Guid>("appelementid"),
                entity.GetAttributeValue<string>("uniquename") ?? string.Empty,
                entity.GetAttributeValue<string>("name") ?? string.Empty,
                objectRef.Id,
                botDisplayName));
        }

        return bindings;
    }

    // Known first-party / template apps that do not support the model-driven app assistant agent (#1192).
    // Matched case-insensitively against the app display name AND unique name. The support matrix changes,
    // so this is best-effort and bypassable with --force; Microsoft's doc remains the source of truth.
    // More specific tokens precede their substrings so the reported reason names the closest match.
    // Ref: https://learn.microsoft.com/en-us/power-apps/maker/model-driven-apps/add-app-assistant-agent
    private static readonly string[] UnsupportedAppTokens =
    {
        "Field Service Mobile",
        "Connected Field Service",
        "Field Service",
        "Sales Hub",
        "Customer Service Hub",
        "Customer Service workspace",
        "Project Operations",
        "Customer Insights",
        "Omnichannel"
    };

    // Returns null when the app supports the app-assistant agent; otherwise a human-readable reason.
    private async Task<string?> EvaluateCopilotEligibilityAsync(
        IPooledClient client, Guid appModuleId, string appDisplayName, string appUniqueName, CancellationToken ct)
    {
        foreach (var token in UnsupportedAppTokens)
        {
            if (ContainsToken(appDisplayName, token) || ContainsToken(appUniqueName, token))
            {
                return $"'{token}' apps are listed as unsupported.";
            }
        }

        // Table-pair rule: an app containing BOTH the Lead and Opportunity tables is unsupported.
        // Reported distinctly from the named-app cases.
        var entities = await GetAppEntityLogicalNamesAsync(client, appModuleId, ct);
        if (entities.Contains("lead") && entities.Contains("opportunity"))
        {
            return "it contains both the Lead and Opportunity tables.";
        }

        return null;
    }

    private static bool ContainsToken(string? value, string token) =>
        !string.IsNullOrEmpty(value) && value.Contains(token, StringComparison.OrdinalIgnoreCase);

    // Collects the entity logical names referenced by the app's sitemap navigation (SubArea@Entity),
    // lower-cased for case-insensitive membership tests.
    private async Task<HashSet<string>> GetAppEntityLogicalNamesAsync(
        IPooledClient client, Guid appModuleId, CancellationToken ct)
    {
        var sitemapXml = await FetchSitemapXmlForAppAsync(client, appModuleId, ct, unpublished: true);
        var doc = XDocument.Parse(sitemapXml);
        return doc.Descendants("SubArea")
            .Select(s => s.Attribute("Entity")?.Value)
            .Where(e => !string.IsNullOrEmpty(e))
            .Select(e => e!.ToLowerInvariant())
            .ToHashSet();
    }

    // The appelement unique name convention observed in maker-created rows:
    // {publisherPrefix}_{appUniqueName}_schemaname_{botSchemaName}, where the publisher
    // prefix is the leading segment of the bot's schema name.
    private static string SchemaPrefix(string schemaName)
    {
        var idx = schemaName.IndexOf('_');
        return idx > 0 ? schemaName[..idx] : schemaName;
    }

    private static string Truncate(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..maxLength];

    // Creates the appelement binding. appelement.uniquename is a unique key, and appelement mutations
    // reconcile slowly server-side, so a stale row left by a prior failed wiring can occupy the
    // maker-convention name. Rather than depend on a slow delete to free it, fall back to a
    // uniquely-suffixed name on collision — the binding is defined by objectid + parentappmoduleid,
    // not the unique name. Returns the created id and the name actually used.
    private static async Task<(Guid Id, string UniqueName)> CreateCopilotAppElementAsync(
        IPooledClient client, Guid appModuleId, Guid botId, string botSchemaName, string baseUniqueName, CancellationToken ct)
    {
        try
        {
            var id = await client.CreateAsync(
                BuildCopilotAppElement(appModuleId, botId, botSchemaName, baseUniqueName), ct);
            return (id, baseUniqueName);
        }
        catch (Exception ex) when (ex is not OperationCanceledException && IsDuplicateKeyViolation(ex))
        {
            // Suffix is "_" + 8 hex chars (9). Truncate the base first so the result stays within
            // the 100-char uniquename limit even when the base is already near the cap.
            var suffix = "_" + Guid.NewGuid().ToString("N")[..8];
            var fallbackName = Truncate(baseUniqueName, MaxUniqueNameLength - suffix.Length) + suffix;
            var id = await client.CreateAsync(
                BuildCopilotAppElement(appModuleId, botId, botSchemaName, fallbackName), ct);
            return (id, fallbackName);
        }
    }

    private static Entity BuildCopilotAppElement(Guid appModuleId, Guid botId, string name, string uniqueName) =>
        new("appelement")
        {
            ["name"] = name,
            ["uniquename"] = uniqueName,
            ["parentappmoduleid"] = new EntityReference("appmodule", appModuleId),
            // The SDK EntityReference carries the explicit target type ("bot"). The Web API @odata.bind
            // cannot: all three objectid targets (bot, aiskillconfig, mcpserver) share the "objectid"
            // navigation property, so a bare bind mis-resolves to the first target (aiskillconfig).
            ["objectid"] = new EntityReference("bot", botId)
        };

    private static bool IsDuplicateKeyViolation(Exception ex) =>
        ex.Message.Contains("database constraint", StringComparison.OrdinalIgnoreCase)
        || (ex.Message.Contains("uniquename", StringComparison.OrdinalIgnoreCase)
            && ex.Message.Contains("Cannot complete", StringComparison.OrdinalIgnoreCase));

    private async Task<Guid> GetSitemapIdAsync(IPooledClient client, Guid appModuleId, CancellationToken ct)
    {
        var id = await TryGetSitemapIdAsync(client, appModuleId, ct);
        return id ?? throw new PpdsException(ModelDrivenAppErrorCodes.GetFailed, "App has no associated sitemap record.");
    }

    // Returns null only when the app truly has no sitemap (never-published app with no navigation yet).
    // Three-tier lookup:
    //   Tier 1: appmodulecomponent (fast path; works for apps that have been published at least once)
    //   Tier 2: match sitemap.sitemapnameunique == appmodule.uniquename (handles draft apps whose
    //           sitemap isn't in appmodulecomponent yet). The Power Apps maker creates an app-aware
    //           sitemap whose unique name equals the app's uniquename; this shared name is the only
    //           reliable link for a draft sitemap, since sitemap has no FK to appmodule.
    //   Tier 3: OData navigation property (works for published apps with a published sitemap).
    private async Task<Guid?> TryGetSitemapIdAsync(IPooledClient client, Guid appModuleId, CancellationToken ct)
    {
        // Tier 1: appmodulecomponent SDK query (works for published apps).
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
        if (sitemapId.HasValue && sitemapId.Value != Guid.Empty)
            return sitemapId;

        // Tier 2: match the app's sitemap by shared unique name. The Power Apps maker creates an
        // app-aware sitemap whose sitemapnameunique equals the app's uniquename; this is how a draft
        // (never-published) app links to its sitemap, since sitemap has no FK to appmodule and the
        // sitemap is not registered in appmodulecomponent until the app is published. Query
        // unpublished so draft sitemaps are visible.
        var appUniqueName = await TryResolveAppUniqueNameAsync(client, appModuleId, ct);
        if (!string.IsNullOrEmpty(appUniqueName))
        {
            var nameQuery = new QueryExpression("sitemap")
            {
                ColumnSet = new ColumnSet("sitemapid"),
                TopCount = 1
            };
            nameQuery.Criteria.AddCondition("sitemapnameunique", ConditionOperator.Equal, appUniqueName);

            try
            {
                var nameRequest = new RetrieveUnpublishedMultipleRequest { Query = nameQuery };
                var nameResponse = (RetrieveUnpublishedMultipleResponse)await client.ExecuteAsync(nameRequest, ct);
                var nameMatch = nameResponse.EntityCollection.Entities.FirstOrDefault();
                if (nameMatch != null)
                {
                    var id = nameMatch.GetAttributeValue<Guid>("sitemapid");
                    if (id != Guid.Empty)
                        return id;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogDebug(ex, "Sitemap lookup by app uniquename '{UniqueName}' failed for app {AppModuleId}.", appUniqueName, appModuleId);
            }
        }

        // Tier 3: OData navigation property (works for published apps with a published sitemap).
        var sitemapViaNav = await TryGetSitemapIdViaNavigationAsync(client, appModuleId, ct);
        if (sitemapViaNav.HasValue)
            return sitemapViaNav;

        return null;
    }

    private async Task<Guid?> TryGetSitemapIdViaNavigationAsync(IPooledClient client, Guid appModuleId, CancellationToken ct)
    {
        // Tier A: direct navigation property. Works when a published sitemap is linked.
        try
        {
            var json = await client.GetRawWebApiAsync(
                $"appmodules({appModuleId:D})/appmodulesitemap?$select=sitemapid", ct);
            if (!string.IsNullOrWhiteSpace(json))
            {
                using var doc = System.Text.Json.JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("sitemapid", out var el)
                    && el.ValueKind == System.Text.Json.JsonValueKind.String
                    && Guid.TryParse(el.GetString(), out var g) && g != Guid.Empty)
                    return g;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "appmodulesitemap direct GET failed for app {AppModuleId}.", appModuleId);
        }

        // Tier B: $ref endpoint. Returns just the entity reference URL, which may succeed even
        // when the full navigation returns 404 for an unpublished sitemap.
        try
        {
            var json = await client.GetRawWebApiAsync(
                $"appmodules({appModuleId:D})/appmodulesitemap/$ref", ct);
            if (!string.IsNullOrWhiteSpace(json))
            {
                using var doc = System.Text.Json.JsonDocument.Parse(json);
                // Response: {"@odata.id":"https://org.crm.dynamics.com/api/data/v9.2/sitemaps(guid)"}
                if (doc.RootElement.TryGetProperty("@odata.id", out var idProp))
                {
                    var odataId = idProp.GetString();
                    if (!string.IsNullOrEmpty(odataId))
                    {
                        var start = odataId.LastIndexOf('(') + 1;
                        var end = odataId.LastIndexOf(')');
                        if (start > 0 && end > start
                            && Guid.TryParse(odataId.AsSpan(start, end - start), out var refGuid)
                            && refGuid != Guid.Empty)
                            return refGuid;
                    }
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "appmodulesitemap $ref GET failed for app {AppModuleId}.", appModuleId);
        }

        return null;
    }

    // Resolves the app's uniquename from a draft-or-published appmodule record. Used to find the
    // maker-created sitemap by shared unique name (Tier 2) and to name a sitemap we create so it is
    // discoverable on subsequent runs. Returns null if the app can't be resolved.
    private async Task<string?> TryResolveAppUniqueNameAsync(IPooledClient client, Guid appModuleId, CancellationToken ct)
    {
        var query = new QueryExpression("appmodule")
        {
            ColumnSet = new ColumnSet("uniquename"),
            TopCount = 1
        };
        query.Criteria.AddCondition("appmoduleid", ConditionOperator.Equal, appModuleId);

        try
        {
            var request = new RetrieveUnpublishedMultipleRequest { Query = query };
            var response = (RetrieveUnpublishedMultipleResponse)await client.ExecuteAsync(request, ct);
            return response.EntityCollection.Entities.FirstOrDefault()?.GetAttributeValue<string>("uniquename");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Failed to resolve uniquename for app {AppModuleId}.", appModuleId);
            return null;
        }
    }

    private async Task<Guid> CreateSitemapForAppAsync(IPooledClient client, Guid appModuleId, string appUniqueName, string sitemapXml, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(appUniqueName))
        {
            throw new PpdsException(ModelDrivenAppErrorCodes.UpdateFailed,
                "Cannot create a sitemap for an app with no uniquename.");
        }

        var sitemap = new Entity("sitemap")
        {
            // Match the maker convention: an app-aware sitemap shares the app's uniquename. Naming
            // it this way means a subsequent run finds it via Tier 2 instead of creating a second
            // sitemap, which Dataverse rejects with "App can't have multiple site maps" (0x80050111).
            ["sitemapnameunique"] = appUniqueName,
            ["sitemapxml"] = sitemapXml
        };

        Guid sitemapId;
        try
        {
            sitemapId = await client.CreateAsync(sitemap, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new PpdsException(ModelDrivenAppErrorCodes.UpdateFailed, "Failed to create sitemap for app.", ex);
        }

        await AddAppComponentsAsync(client, appModuleId, "sitemap", [sitemapId], ct);
        return sitemapId;
    }

    private async Task<string> FetchSitemapXmlForAppAsync(IPooledClient client, Guid appModuleId, CancellationToken ct, bool unpublished = false)
    {
        var sitemapId = await GetSitemapIdAsync(client, appModuleId, ct);
        return await FetchSitemapXmlByIdAsync(client, sitemapId, ct, unpublished);
    }

    private async Task<string> FetchSitemapXmlByIdAsync(IPooledClient client, Guid sitemapId, CancellationToken ct, bool unpublished = false)
    {
        Entity sitemap;
        try
        {
            if (unpublished)
            {
                // RetrieveUnpublished is not supported for sitemap entities; use RetrieveUnpublishedMultiple instead.
                var query = new QueryExpression("sitemap")
                {
                    ColumnSet = new ColumnSet("sitemapxml"),
                    TopCount = 1
                };
                query.Criteria.AddCondition("sitemapid", ConditionOperator.Equal, sitemapId);

                var multipleRequest = new RetrieveUnpublishedMultipleRequest { Query = query };
                var multipleResponse = (RetrieveUnpublishedMultipleResponse)await client.ExecuteAsync(multipleRequest, ct);
                sitemap = multipleResponse.EntityCollection.Entities.FirstOrDefault()
                    ?? throw new InvalidOperationException($"Sitemap record {sitemapId} not found.");
            }
            else
            {
                sitemap = await client.RetrieveAsync("sitemap", sitemapId, new ColumnSet("sitemapxml"), ct);
            }
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

    private async Task PublishAppAsync(IPooledClient client, Guid appModuleId, string uniqueName, CancellationToken ct)
    {
        try
        {
            var request = new PublishXmlRequest
            {
                ParameterXml = $"<importexportxml><appmodules><appmodule>{appModuleId:D}</appmodule></appmodules></importexportxml>"
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
        var sitemapXml = await FetchSitemapXmlForAppAsync(client, appModuleId, ct, unpublished: true);
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
            // Reference the table by its OWN logical name (not the literal "entity", which is itself
            // a real table) so the remove targets the table component that add-table registered.
            var entityRef = new EntityReference(entityMeta.LogicalName, entityMeta.MetadataId);
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
        request["AppId"] = appModuleId;
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

    // Registers component records (forms, views, charts, sitemap) that all live in the same entity:
    // the EntityReference logical name is that entity, the id is the record id.
    private Task AddAppComponentsAsync(
        IPooledClient client,
        Guid appModuleId,
        string componentEntityName,
        List<Guid> componentIds,
        CancellationToken ct)
        => AddAppComponentsAsync(
            client,
            appModuleId,
            new EntityReferenceCollection(
                componentIds.Select(id => new EntityReference(componentEntityName, id)).ToList()),
            ct);

    private async Task AddAppComponentsAsync(
        IPooledClient client,
        Guid appModuleId,
        EntityReferenceCollection components,
        CancellationToken ct)
    {
        if (components.Count == 0)
        {
            return;
        }

        var request = new OrganizationRequest("AddAppComponents");
        request["AppId"] = appModuleId;
        request["Components"] = components;

        try
        {
            await client.ExecuteAsync(request, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to add {Count} app components", components.Count);
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

    // ── inspect-app-assistant read-only helpers (#1193) ──────────────────────────

    // The maker-convention appelement uniquename embeds "_schemaname_" (see the add-copilot
    // BuildCopilotAppElement convention). A null-objectid appelement carrying this marker is a
    // dangling copilot binding; an unrelated appelement without it is left alone.
    private const string CopilotAppElementNameMarker = "_schemaname_";

    // islightweightbot is a Dataverse two-options field that can be unvalued; keep it nullable so a
    // missing value reads as "unknown" rather than being forced to false.
    private sealed record BotAppAssistantInfo(string? Name, bool? IsLightweightBot);

    // Bulk-reads the app-assistant flag (and name) for the supplied bots. Read-only.
    private async Task<Dictionary<Guid, BotAppAssistantInfo>> GetBotAppAssistantInfoAsync(
        IPooledClient client, IReadOnlyCollection<Guid> botIds, CancellationToken ct)
    {
        if (botIds.Count == 0)
        {
            return [];
        }

        var query = new QueryExpression("bot")
        {
            ColumnSet = new ColumnSet("botid", "name", "islightweightbot")
        };
        query.Criteria.AddCondition("botid", ConditionOperator.In, botIds.Select(id => (object)id).ToArray());

        EntityCollection result;
        try
        {
            result = await client.RetrieveMultipleAsync(query, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new PpdsException(ModelDrivenAppErrorCodes.GetFailed, "Failed to query Copilot (bot) app-assistant flags.", ex);
        }

        var map = new Dictionary<Guid, BotAppAssistantInfo>();
        foreach (var e in result.Entities)
        {
            map[e.GetAttributeValue<Guid>("botid")] = new BotAppAssistantInfo(
                e.GetAttributeValue<string>("name"),
                e.GetAttributeValue<bool?>("islightweightbot"));
        }

        return map;
    }

    // Finds copilot-shaped appelement rows in the app whose objectid is null (no target bot).
    // Mirrors FindCopilotBindingsAsync's query but keeps the rows that helper discards. Read-only.
    private async Task<List<Guid>> FindOrphanCopilotAppElementsAsync(
        IPooledClient client, Guid appModuleId, CancellationToken ct)
    {
        var query = new QueryExpression("appelement")
        {
            ColumnSet = new ColumnSet("appelementid", "uniquename", "objectid")
        };
        query.Criteria.AddCondition("parentappmoduleid", ConditionOperator.Equal, appModuleId);

        EntityCollection result;
        try
        {
            result = await client.RetrieveMultipleAsync(query, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new PpdsException(ModelDrivenAppErrorCodes.GetFailed, "Failed to query Copilot appelements.", ex);
        }

        var orphans = new List<Guid>();
        foreach (var entity in result.Entities)
        {
            // A populated objectid (bot or any other target) is not an orphan.
            if (entity.GetAttributeValue<EntityReference>("objectid") != null)
            {
                continue;
            }

            var uniqueName = entity.GetAttributeValue<string>("uniquename") ?? string.Empty;
            if (uniqueName.Contains(CopilotAppElementNameMarker, StringComparison.OrdinalIgnoreCase))
            {
                orphans.Add(entity.GetAttributeValue<Guid>("appelementid"));
            }
        }

        return orphans;
    }
}

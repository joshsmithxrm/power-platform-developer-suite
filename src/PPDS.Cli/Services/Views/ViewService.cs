using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using Microsoft.Crm.Sdk.Messages;
using Microsoft.Extensions.Logging;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Cli.Infrastructure.Progress;
using PPDS.Cli.Infrastructure.Safety;
using PPDS.Dataverse.Client;
using PPDS.Dataverse.Metadata;
using PPDS.Dataverse.Models;
using PPDS.Dataverse.Pooling;

namespace PPDS.Cli.Services.Views;

/// <summary>
/// Application service for managing Dataverse savedqueries views.
/// </summary>
public class ViewService : IViewService
{
    private readonly IDataverseConnectionPool _pool;
    private readonly ICachedMetadataProvider _cachedMetadata;
    private readonly IShakedownGuard _guard;
    private readonly ILogger<ViewService> _logger;

    public ViewService(
        IDataverseConnectionPool pool,
        ICachedMetadataProvider cachedMetadata,
        IShakedownGuard guard,
        ILogger<ViewService> logger)
    {
        _pool = pool ?? throw new ArgumentNullException(nameof(pool));
        _cachedMetadata = cachedMetadata ?? throw new ArgumentNullException(nameof(cachedMetadata));
        _guard = guard ?? throw new ArgumentNullException(nameof(guard));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    // ─── Read path ─────────────────────────────────────────────────────────────

    /// <inheritdoc />
    public async Task<ListResult<ViewInfo>> ListAsync(
        string entityLogicalName,
        IProgressReporter? progressReporter = null,
        CancellationToken cancellationToken = default)
    {
        var otc = await ResolveObjectTypeCodeAsync(entityLogicalName, cancellationToken);

        await using var client = await _pool.GetClientAsync(cancellationToken: cancellationToken);

        var query = new QueryExpression("savedquery")
        {
            ColumnSet = new ColumnSet("savedqueryid", "name", "querytype", "returnedtypecode", "ismanaged", "modifiedon"),
            Criteria = new FilterExpression
            {
                Conditions =
                {
                    new ConditionExpression("returnedtypecode", ConditionOperator.Equal, otc)
                }
            },
            Orders = { new OrderExpression("name", OrderType.Ascending) }
        };

        EntityCollection result;
        try
        {
            result = await client.RetrieveMultipleAsync(query, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new PpdsException(ErrorCodes.View.ListFailed,
                $"Failed to list views for entity '{entityLogicalName}'.", ex);
        }

        var items = result.Entities.Select(MapToViewInfo).ToList();
        return new ListResult<ViewInfo>
        {
            Items = items,
            TotalCount = items.Count
        };
    }

    /// <inheritdoc />
    public async Task<ViewDetail> GetAsync(
        string entityLogicalName,
        string viewName,
        IProgressReporter? progressReporter = null,
        CancellationToken cancellationToken = default)
    {
        var otc = await ResolveObjectTypeCodeAsync(entityLogicalName, cancellationToken);

        await using var client = await _pool.GetClientAsync(cancellationToken: cancellationToken);

        var query = new QueryExpression("savedquery")
        {
            ColumnSet = new ColumnSet("savedqueryid", "name", "querytype", "returnedtypecode", "layoutxml", "fetchxml", "ismanaged", "modifiedon"),
            Criteria = new FilterExpression
            {
                Conditions =
                {
                    new ConditionExpression("returnedtypecode", ConditionOperator.Equal, otc),
                    new ConditionExpression("name", ConditionOperator.Equal, viewName)
                }
            }
        };

        EntityCollection result;
        try
        {
            result = await client.RetrieveMultipleAsync(query, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new PpdsException(ErrorCodes.View.GetFailed,
                $"Failed to retrieve view '{viewName}' for entity '{entityLogicalName}'.", ex);
        }

        if (result.Entities.Count == 0)
            throw new PpdsException(ErrorCodes.View.NotFound,
                $"View '{viewName}' not found for entity '{entityLogicalName}'.");
        if (result.Entities.Count > 1)
            throw new PpdsException(ErrorCodes.View.Ambiguous,
                $"Multiple views named '{viewName}' found for entity '{entityLogicalName}'. Use a unique view name.");

        var entity = result.Entities[0];
        var id = entity.GetAttributeValue<Guid>("savedqueryid");
        var queryType = entity.GetAttributeValue<int>("querytype");
        var queryTypeLabel = GetQueryTypeLabel(queryType);
        var layoutXml = entity.GetAttributeValue<string>("layoutxml") ?? "<grid><row /></grid>";
        var fetchXml = entity.GetAttributeValue<string>("fetchxml") ?? "<fetch><entity /></fetch>";

        var columns = ParseLayoutXml(layoutXml);
        var (sorts, filter) = ParseFetchXml(fetchXml);

        return new ViewDetail(
            id,
            viewName,
            queryType,
            queryTypeLabel,
            entityLogicalName,
            columns,
            sorts,
            filter);
    }

    // ─── Column mutations ───────────────────────────────────────────────────────

    /// <inheritdoc />
    public async Task AddColumnAsync(
        string entityLogicalName, string viewName,
        IReadOnlyList<ColumnSpec> columns,
        string? viaRelationship = null,
        bool publish = false, string? solution = null,
        IProgressReporter? progressReporter = null,
        CancellationToken cancellationToken = default)
    {
        _guard.EnsureCanMutate("views.add-column");

        await using var client = await _pool.GetClientAsync(cancellationToken: cancellationToken);
        var (savedQueryId, entity) = await FetchViewRecordAsync(
            client, entityLogicalName, viewName,
            new ColumnSet("savedqueryid", "layoutxml", "fetchxml"), cancellationToken);

        var layoutXml = entity.GetAttributeValue<string>("layoutxml") ?? "<grid><row /></grid>";
        var fetchXml = entity.GetAttributeValue<string>("fetchxml") ?? "<fetch><entity /></fetch>";

        bool isRelated = viaRelationship != null;
        string? relName = null, relEntity = null, relPkName = null, relAlias = null;

        if (isRelated)
        {
            var rels = await _cachedMetadata.GetRelationshipsAsync(entityLogicalName, cancellationToken);
            var rel = rels.ManyToOne.FirstOrDefault(r => r.ReferencingAttribute == viaRelationship)
                ?? rels.OneToMany.FirstOrDefault(r => r.ReferencingAttribute == viaRelationship);
            if (rel == null)
                throw new PpdsException(ErrorCodes.View.RelationshipNotFound,
                    $"Relationship attribute '{viaRelationship}' not found in metadata for entity '{entityLogicalName}'.");

            relName = viaRelationship;
            relEntity = rel.ReferencedEntity;
            relPkName = rel.ReferencedAttribute;
            relAlias = viaRelationship;
        }

        var layoutDoc = XDocument.Parse(layoutXml);
        var layoutChanged = AddCellsWithWarnings(layoutDoc, columns, isRelated, relName, relEntity, relPkName, relAlias);

        bool fetchChanged = false;
        var fetchDoc = XDocument.Parse(fetchXml);
        if (isRelated && relName != null)
        {
            fetchDoc = AddRelatedLinkEntity(fetchDoc, relEntity!, relPkName!, viaRelationship!, relAlias!, columns);
            fetchChanged = true;
        }

        var update = new Entity("savedquery", savedQueryId);
        update["layoutxml"] = layoutDoc.ToString(SaveOptions.DisableFormatting);
        if (fetchChanged)
            update["fetchxml"] = fetchDoc.ToString(SaveOptions.DisableFormatting);

        try
        {
            await client.UpdateAsync(update, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new PpdsException(ErrorCodes.View.UpdateFailed,
                $"Failed to update view '{viewName}' after adding columns.", ex);
        }

        await PostMutationAsync(client, savedQueryId, entityLogicalName, publish, solution, cancellationToken);
    }

    /// <inheritdoc />
    public async Task RemoveColumnAsync(
        string entityLogicalName, string viewName,
        string attributeName,
        bool publish = false, string? solution = null,
        IProgressReporter? progressReporter = null,
        CancellationToken cancellationToken = default)
    {
        _guard.EnsureCanMutate("views.remove-column");

        await using var client = await _pool.GetClientAsync(cancellationToken: cancellationToken);
        var (savedQueryId, entity) = await FetchViewRecordAsync(
            client, entityLogicalName, viewName,
            new ColumnSet("savedqueryid", "layoutxml", "fetchxml"), cancellationToken);

        var layoutXml = entity.GetAttributeValue<string>("layoutxml") ?? "<grid><row /></grid>";
        var layoutDoc = XDocument.Parse(layoutXml);
        layoutDoc = RemoveCell(layoutDoc, attributeName);

        var update = new Entity("savedquery", savedQueryId);
        update["layoutxml"] = layoutDoc.ToString(SaveOptions.DisableFormatting);

        try
        {
            await client.UpdateAsync(update, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new PpdsException(ErrorCodes.View.UpdateFailed,
                $"Failed to update view '{viewName}' after removing column '{attributeName}'.", ex);
        }

        await PostMutationAsync(client, savedQueryId, entityLogicalName, publish, solution, cancellationToken);
    }

    /// <inheritdoc />
    public async Task UpdateColumnAsync(
        string entityLogicalName, string viewName,
        string attributeName, int width,
        bool publish = false, string? solution = null,
        IProgressReporter? progressReporter = null,
        CancellationToken cancellationToken = default)
    {
        _guard.EnsureCanMutate("views.update-column");

        await using var client = await _pool.GetClientAsync(cancellationToken: cancellationToken);
        var (savedQueryId, entity) = await FetchViewRecordAsync(
            client, entityLogicalName, viewName,
            new ColumnSet("savedqueryid", "layoutxml"), cancellationToken);

        var layoutXml = entity.GetAttributeValue<string>("layoutxml") ?? "<grid><row /></grid>";
        var layoutDoc = XDocument.Parse(layoutXml);
        layoutDoc = UpdateCellWidth(layoutDoc, attributeName, width);

        var update = new Entity("savedquery", savedQueryId);
        update["layoutxml"] = layoutDoc.ToString(SaveOptions.DisableFormatting);

        try
        {
            await client.UpdateAsync(update, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new PpdsException(ErrorCodes.View.UpdateFailed,
                $"Failed to update view '{viewName}' after updating column width.", ex);
        }

        await PostMutationAsync(client, savedQueryId, entityLogicalName, publish, solution, cancellationToken);
    }

    /// <inheritdoc />
    public async Task ReorderColumnsAsync(
        string entityLogicalName, string viewName,
        IReadOnlyList<string> orderedAttributes,
        bool publish = false, string? solution = null,
        IProgressReporter? progressReporter = null,
        CancellationToken cancellationToken = default)
    {
        _guard.EnsureCanMutate("views.reorder-columns");

        await using var client = await _pool.GetClientAsync(cancellationToken: cancellationToken);
        var (savedQueryId, entity) = await FetchViewRecordAsync(
            client, entityLogicalName, viewName,
            new ColumnSet("savedqueryid", "layoutxml"), cancellationToken);

        var layoutXml = entity.GetAttributeValue<string>("layoutxml") ?? "<grid><row /></grid>";
        var layoutDoc = XDocument.Parse(layoutXml);
        layoutDoc = ReorderCells(layoutDoc, orderedAttributes);

        var update = new Entity("savedquery", savedQueryId);
        update["layoutxml"] = layoutDoc.ToString(SaveOptions.DisableFormatting);

        try
        {
            await client.UpdateAsync(update, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new PpdsException(ErrorCodes.View.UpdateFailed,
                $"Failed to update view '{viewName}' after reordering columns.", ex);
        }

        await PostMutationAsync(client, savedQueryId, entityLogicalName, publish, solution, cancellationToken);
    }

    // ─── Sort mutations ─────────────────────────────────────────────────────────

    /// <inheritdoc />
    public async Task SetSortAsync(
        string entityLogicalName, string viewName,
        IReadOnlyList<ViewSortOrder> sorts,
        bool publish = false, string? solution = null,
        IProgressReporter? progressReporter = null,
        CancellationToken cancellationToken = default)
    {
        _guard.EnsureCanMutate("views.set-sort");

        await using var client = await _pool.GetClientAsync(cancellationToken: cancellationToken);
        var (savedQueryId, entity) = await FetchViewRecordAsync(
            client, entityLogicalName, viewName,
            new ColumnSet("savedqueryid", "fetchxml"), cancellationToken);

        var fetchXml = entity.GetAttributeValue<string>("fetchxml") ?? "<fetch><entity /></fetch>";
        var fetchDoc = XDocument.Parse(fetchXml);
        fetchDoc = SetOrderElements(fetchDoc, sorts);

        var update = new Entity("savedquery", savedQueryId);
        update["fetchxml"] = fetchDoc.ToString(SaveOptions.DisableFormatting);

        try
        {
            await client.UpdateAsync(update, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new PpdsException(ErrorCodes.View.UpdateFailed,
                $"Failed to update view '{viewName}' after setting sort.", ex);
        }

        await PostMutationAsync(client, savedQueryId, entityLogicalName, publish, solution, cancellationToken);
    }

    /// <inheritdoc />
    public async Task ClearSortAsync(
        string entityLogicalName, string viewName,
        bool publish = false, string? solution = null,
        IProgressReporter? progressReporter = null,
        CancellationToken cancellationToken = default)
    {
        _guard.EnsureCanMutate("views.clear-sort");

        await using var client = await _pool.GetClientAsync(cancellationToken: cancellationToken);
        var (savedQueryId, entity) = await FetchViewRecordAsync(
            client, entityLogicalName, viewName,
            new ColumnSet("savedqueryid", "fetchxml"), cancellationToken);

        var fetchXml = entity.GetAttributeValue<string>("fetchxml") ?? "<fetch><entity /></fetch>";
        var fetchDoc = XDocument.Parse(fetchXml);
        fetchDoc = RemoveOrderElements(fetchDoc);

        var update = new Entity("savedquery", savedQueryId);
        update["fetchxml"] = fetchDoc.ToString(SaveOptions.DisableFormatting);

        try
        {
            await client.UpdateAsync(update, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new PpdsException(ErrorCodes.View.UpdateFailed,
                $"Failed to update view '{viewName}' after clearing sort.", ex);
        }

        await PostMutationAsync(client, savedQueryId, entityLogicalName, publish, solution, cancellationToken);
    }

    // ─── Filter mutations ───────────────────────────────────────────────────────

    /// <inheritdoc />
    public async Task SetFilterAsync(
        string entityLogicalName, string viewName,
        string filterXmlFragment,
        bool publish = false, string? solution = null,
        IProgressReporter? progressReporter = null,
        CancellationToken cancellationToken = default)
    {
        _guard.EnsureCanMutate("views.set-filter");

        var filterElement = XElement.Parse(filterXmlFragment);
        if (filterElement.Name.LocalName != "filter")
            throw new PpdsException(ErrorCodes.Validation.SchemaInvalid,
                "Filter XML must have <filter> as the root element.");

        await using var client = await _pool.GetClientAsync(cancellationToken: cancellationToken);
        var (savedQueryId, entity) = await FetchViewRecordAsync(
            client, entityLogicalName, viewName,
            new ColumnSet("savedqueryid", "fetchxml"), cancellationToken);

        var fetchXml = entity.GetAttributeValue<string>("fetchxml") ?? "<fetch><entity /></fetch>";
        var fetchDoc = XDocument.Parse(fetchXml);
        fetchDoc = SetFilterElement(fetchDoc, filterElement);

        var update = new Entity("savedquery", savedQueryId);
        update["fetchxml"] = fetchDoc.ToString(SaveOptions.DisableFormatting);

        try
        {
            await client.UpdateAsync(update, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new PpdsException(ErrorCodes.View.UpdateFailed,
                $"Failed to update view '{viewName}' after setting filter.", ex);
        }

        await PostMutationAsync(client, savedQueryId, entityLogicalName, publish, solution, cancellationToken);
    }

    /// <inheritdoc />
    public async Task ClearFilterAsync(
        string entityLogicalName, string viewName,
        bool publish = false, string? solution = null,
        IProgressReporter? progressReporter = null,
        CancellationToken cancellationToken = default)
    {
        _guard.EnsureCanMutate("views.clear-filter");

        await using var client = await _pool.GetClientAsync(cancellationToken: cancellationToken);
        var (savedQueryId, entity) = await FetchViewRecordAsync(
            client, entityLogicalName, viewName,
            new ColumnSet("savedqueryid", "fetchxml"), cancellationToken);

        var fetchXml = entity.GetAttributeValue<string>("fetchxml") ?? "<fetch><entity /></fetch>";
        var fetchDoc = XDocument.Parse(fetchXml);
        fetchDoc = RemoveFilterElement(fetchDoc);

        var update = new Entity("savedquery", savedQueryId);
        update["fetchxml"] = fetchDoc.ToString(SaveOptions.DisableFormatting);

        try
        {
            await client.UpdateAsync(update, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new PpdsException(ErrorCodes.View.UpdateFailed,
                $"Failed to update view '{viewName}' after clearing filter.", ex);
        }

        await PostMutationAsync(client, savedQueryId, entityLogicalName, publish, solution, cancellationToken);
    }

    /// <inheritdoc />
    public async Task SetFetchXmlAsync(
        string entityLogicalName, string viewName,
        string fetchXml,
        bool publish = false, string? solution = null,
        IProgressReporter? progressReporter = null,
        CancellationToken cancellationToken = default)
    {
        _guard.EnsureCanMutate("views.set-fetchxml");

        var fetchDoc = XDocument.Parse(fetchXml);
        if (fetchDoc.Root?.Name.LocalName != "fetch")
            throw new PpdsException(ErrorCodes.Validation.SchemaInvalid,
                "FetchXML must have <fetch> as the root element.");

        await using var client = await _pool.GetClientAsync(cancellationToken: cancellationToken);
        var (savedQueryId, _) = await FetchViewRecordAsync(
            client, entityLogicalName, viewName,
            new ColumnSet("savedqueryid"), cancellationToken);

        var update = new Entity("savedquery", savedQueryId);
        update["fetchxml"] = fetchXml;

        try
        {
            await client.UpdateAsync(update, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new PpdsException(ErrorCodes.View.UpdateFailed,
                $"Failed to update view '{viewName}' after setting fetchxml.", ex);
        }

        await PostMutationAsync(client, savedQueryId, entityLogicalName, publish, solution, cancellationToken);
    }

    // ─── Private helpers ────────────────────────────────────────────────────────

    private async Task<int> ResolveObjectTypeCodeAsync(string entityLogicalName, CancellationToken ct)
    {
        var entities = await _cachedMetadata.GetEntitiesAsync(ct);
        var meta = entities.FirstOrDefault(e => e.LogicalName == entityLogicalName);
        if (meta == null)
            throw new PpdsException(ErrorCodes.View.NotFound,
                $"Entity '{entityLogicalName}' not found in metadata.");
        return meta.ObjectTypeCode;
    }

    private async Task<(Guid SavedQueryId, Entity Entity)> FetchViewRecordAsync(
        IDataverseClient client,
        string entityLogicalName,
        string viewName,
        ColumnSet columnSet,
        CancellationToken ct)
    {
        var otc = await ResolveObjectTypeCodeAsync(entityLogicalName, ct);

        var query = new QueryExpression("savedquery")
        {
            ColumnSet = columnSet,
            Criteria = new FilterExpression
            {
                Conditions =
                {
                    new ConditionExpression("returnedtypecode", ConditionOperator.Equal, otc),
                    new ConditionExpression("name", ConditionOperator.Equal, viewName)
                }
            }
        };

        EntityCollection result;
        try
        {
            result = await client.RetrieveMultipleAsync(query, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new PpdsException(ErrorCodes.View.GetFailed,
                $"Failed to retrieve view '{viewName}' for entity '{entityLogicalName}'.", ex);
        }

        if (result.Entities.Count == 0)
            throw new PpdsException(ErrorCodes.View.NotFound,
                $"View '{viewName}' not found for entity '{entityLogicalName}'.");
        if (result.Entities.Count > 1)
            throw new PpdsException(ErrorCodes.View.Ambiguous,
                $"Multiple views named '{viewName}' found for entity '{entityLogicalName}'.");

        var entity = result.Entities[0];
        var savedQueryId = entity.GetAttributeValue<Guid>("savedqueryid");
        return (savedQueryId, entity);
    }

    private async Task PostMutationAsync(
        IDataverseClient client,
        Guid viewId,
        string entityLogicalName,
        bool publish,
        string? solution,
        CancellationToken ct)
    {
        // Solution first (AC-16: solution before publish)
        if (solution != null)
        {
            var addToSolution = new AddSolutionComponentRequest
            {
                ComponentId = viewId,
                ComponentType = 26, // savedquery
                SolutionUniqueName = solution,
                AddRequiredComponents = false
            };
            try
            {
                await client.ExecuteAsync(addToSolution, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                throw new PpdsException(ErrorCodes.View.AddToSolutionFailed,
                    $"Failed to add view to solution '{solution}'.", ex);
            }
        }

        if (publish)
        {
            var publishXml = $"<importexportxml><entities><entity>{entityLogicalName}</entity></entities></importexportxml>";
            var publishRequest = new PublishXmlRequest { ParameterXml = publishXml };
            try
            {
                await client.ExecuteAsync(publishRequest, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                throw new PpdsException(ErrorCodes.View.PublishFailed,
                    $"Failed to publish entity '{entityLogicalName}'.", ex);
            }
        }
    }

    private static bool AddCellsWithWarnings(
        XDocument layout,
        IReadOnlyList<ColumnSpec> columns,
        bool isRelated,
        string? relName,
        string? relEntity,
        string? relPkName,
        string? relAlias)
    {
        var row = layout.Descendants("row").FirstOrDefault();
        if (row == null) return false;

        bool changed = false;
        foreach (var col in columns)
        {
            var existing = row.Elements("cell")
                .FirstOrDefault(c => (string?)c.Attribute("name") == col.AttributeName);
            if (existing != null)
            {
                Console.Error.WriteLine($"Warning: column '{col.AttributeName}' already exists in view — skipping (idempotent).");
                continue;
            }

            XElement cell;
            if (isRelated && relName != null && relEntity != null && relPkName != null)
            {
                cell = new XElement("cell",
                    new XAttribute("name", col.AttributeName),
                    new XAttribute("width", col.Width),
                    new XAttribute("disableSorting", "1"),
                    new XAttribute("LookupStyle", "1"),
                    new XAttribute("RelatedEntityName", relEntity),
                    new XAttribute("RelatedEntityPrimaryKeyName", relPkName),
                    new XAttribute("RelationshipName", relName));
            }
            else
            {
                cell = new XElement("cell",
                    new XAttribute("name", col.AttributeName),
                    new XAttribute("width", col.Width));
            }

            row.Add(cell);
            changed = true;
        }
        return changed;
    }

    private static XDocument AddRelatedLinkEntity(
        XDocument fetch,
        string relEntity,
        string relPkName,
        string viaRelationship,
        string relAlias,
        IReadOnlyList<ColumnSpec> columns)
    {
        var entityElem = fetch.Descendants("entity").FirstOrDefault();
        if (entityElem == null) return fetch;

        var linkEntity = entityElem.Elements("link-entity")
            .FirstOrDefault(le => (string?)le.Attribute("alias") == relAlias);

        if (linkEntity == null)
        {
            linkEntity = new XElement("link-entity",
                new XAttribute("name", relEntity),
                new XAttribute("from", relPkName),
                new XAttribute("to", viaRelationship),
                new XAttribute("alias", relAlias));
            entityElem.Add(linkEntity);
        }

        foreach (var col in columns)
        {
            var existingAttr = linkEntity.Elements("attribute")
                .FirstOrDefault(a => (string?)a.Attribute("name") == col.AttributeName);
            if (existingAttr == null)
            {
                linkEntity.Add(new XElement("attribute", new XAttribute("name", col.AttributeName)));
            }
        }

        return fetch;
    }

    // ─── Internal XML helpers (pure; testable) ──────────────────────────────────

    internal static List<ViewColumn> ParseLayoutXml(string layoutXml)
    {
        var doc = XDocument.Parse(layoutXml);
        var row = doc.Descendants("row").FirstOrDefault();
        if (row == null) return [];

        return row.Elements("cell").Select(cell =>
        {
            var name = (string?)cell.Attribute("name") ?? "";
            var width = int.TryParse((string?)cell.Attribute("width"), out var w) ? w : 150;
            var isRelated = cell.Attribute("RelatedEntityName") != null;
            var relAttr = (string?)cell.Attribute("RelationshipName");
            var relEntity = (string?)cell.Attribute("RelatedEntityName");
            var relPkName = (string?)cell.Attribute("RelatedEntityPrimaryKeyName");
            return new ViewColumn(name, width, isRelated, relAttr, relEntity, relPkName);
        }).ToList();
    }

    internal static (List<ViewSortOrder> Sorts, ViewFilter? Filter) ParseFetchXml(string fetchXml)
    {
        var doc = XDocument.Parse(fetchXml);
        var entityElem = doc.Descendants("entity").FirstOrDefault();
        if (entityElem == null) return ([], null);

        var sorts = entityElem.Elements("order")
            .Select(o =>
            {
                var attr = (string?)o.Attribute("attribute") ?? "";
                var desc = (string?)o.Attribute("descending") == "true";
                return new ViewSortOrder(attr, desc);
            }).ToList();

        var filterElem = entityElem.Element("filter");
        ViewFilter? filter = filterElem != null
            ? new ViewFilter(filterElem.ToString(SaveOptions.DisableFormatting))
            : null;

        return (sorts, filter);
    }

    internal static string GetQueryTypeLabel(int queryType) => queryType switch
    {
        0 => "Standard",
        1 => "Advanced Find Default",
        2 => "Associated",
        4 => "Quick Find",
        _ => $"QueryType({queryType})"
    };

    internal static XDocument RemoveCell(XDocument layout, string attributeName)
    {
        var row = layout.Descendants("row").FirstOrDefault();
        var cell = row?.Elements("cell")
            .FirstOrDefault(c => (string?)c.Attribute("name") == attributeName);
        if (cell == null)
            throw new PpdsException(ErrorCodes.View.ColumnNotFound,
                $"Column '{attributeName}' not found in view layout.");
        cell.Remove();
        return layout;
    }

    internal static XDocument UpdateCellWidth(XDocument layout, string attributeName, int width)
    {
        var row = layout.Descendants("row").FirstOrDefault();
        var cell = row?.Elements("cell")
            .FirstOrDefault(c => (string?)c.Attribute("name") == attributeName);
        if (cell == null)
            throw new PpdsException(ErrorCodes.View.ColumnNotFound,
                $"Column '{attributeName}' not found in view layout.");
        cell.SetAttributeValue("width", width);
        return layout;
    }

    internal static XDocument ReorderCells(XDocument layout, IReadOnlyList<string> orderedAttributes)
    {
        var row = layout.Descendants("row").FirstOrDefault();
        if (row == null) return layout;

        var existingCells = row.Elements("cell").ToList();
        var reordered = orderedAttributes
            .Select(attr => existingCells.FirstOrDefault(c => (string?)c.Attribute("name") == attr))
            .Where(c => c != null)
            .ToList();

        foreach (var cell in existingCells) cell.Remove();
        foreach (var cell in reordered) row.Add(cell!);

        return layout;
    }

    internal static XDocument SetOrderElements(XDocument fetch, IReadOnlyList<ViewSortOrder> sorts)
    {
        var entityElem = fetch.Descendants("entity").FirstOrDefault();
        if (entityElem == null) return fetch;

        entityElem.Elements("order").Remove();

        var filterElem = entityElem.Element("filter");
        foreach (var sort in sorts)
        {
            var orderElem = new XElement("order",
                new XAttribute("attribute", sort.AttributeName),
                new XAttribute("descending", sort.Descending ? "true" : "false"));

            if (filterElem != null)
                filterElem.AddBeforeSelf(orderElem);
            else
                entityElem.Add(orderElem);
        }

        return fetch;
    }

    internal static XDocument RemoveOrderElements(XDocument fetch)
    {
        fetch.Descendants("entity").FirstOrDefault()?.Elements("order").Remove();
        return fetch;
    }

    internal static XDocument SetFilterElement(XDocument fetch, XElement filter)
    {
        var entityElem = fetch.Descendants("entity").FirstOrDefault();
        if (entityElem == null) return fetch;

        entityElem.Element("filter")?.Remove();
        entityElem.Add(filter);
        return fetch;
    }

    internal static XDocument RemoveFilterElement(XDocument fetch)
    {
        fetch.Descendants("entity").FirstOrDefault()?.Element("filter")?.Remove();
        return fetch;
    }

    private static ViewInfo MapToViewInfo(Entity entity)
    {
        var id = entity.GetAttributeValue<Guid>("savedqueryid");
        var name = entity.GetAttributeValue<string>("name") ?? "";
        var queryType = entity.GetAttributeValue<int>("querytype");
        var isManaged = entity.GetAttributeValue<bool>("ismanaged");
        var modifiedOn = entity.Contains("modifiedon")
            ? (DateTime?)entity.GetAttributeValue<DateTime>("modifiedon")
            : null;

        return new ViewInfo(id, name, queryType, GetQueryTypeLabel(queryType), isManaged, modifiedOn);
    }
}

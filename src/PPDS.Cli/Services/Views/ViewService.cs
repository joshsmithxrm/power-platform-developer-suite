using System;
using System.Collections.Generic;
using System.Linq;
using System.ServiceModel;
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
        bool unpublished = false,
        IProgressReporter? progressReporter = null,
        CancellationToken cancellationToken = default)
    {
        var otc = await ResolveObjectTypeCodeAsync(entityLogicalName, cancellationToken);

        await using var client = await _pool.GetClientAsync(cancellationToken: cancellationToken);

        var query = new QueryExpression("savedquery")
        {
            ColumnSet = new ColumnSet("savedqueryid", "name", "querytype", "returnedtypecode", "layoutxml", "fetchxml", "ismanaged", "modifiedon"),
            Criteria = new FilterExpression()
        };
        query.Criteria.AddCondition("returnedtypecode", ConditionOperator.Equal, otc);
        if (Guid.TryParse(viewName.Trim('{', '}'), out var viewGuid))
            query.Criteria.AddCondition("savedqueryid", ConditionOperator.Equal, viewGuid);
        else
            query.Criteria.AddCondition("name", ConditionOperator.Equal, viewName);

        // Read-for-display defaults to the published version; --unpublished opts into the draft so
        // callers can inspect pending (not-yet-published) edits. (Mutations always read unpublished.)
        EntityCollection result;
        try
        {
            if (unpublished)
            {
                var unpubResponse = (RetrieveUnpublishedMultipleResponse)await client.ExecuteAsync(
                    new RetrieveUnpublishedMultipleRequest { Query = query }, cancellationToken);
                result = unpubResponse.EntityCollection;
            }
            else
            {
                result = await client.RetrieveMultipleAsync(query, cancellationToken);
            }
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
        var resolvedName = entity.GetAttributeValue<string>("name") ?? viewName;
        var queryType = entity.GetAttributeValue<int>("querytype");
        var queryTypeLabel = GetQueryTypeLabel(queryType);
        var layoutXml = entity.GetAttributeValue<string>("layoutxml") ?? "<grid><row /></grid>";
        var fetchXml = entity.GetAttributeValue<string>("fetchxml") ?? "<fetch><entity /></fetch>";

        IReadOnlyList<ViewColumn> columns;
        IReadOnlyList<ViewSortOrder> sorts;
        ViewFilter? filter;
        try
        {
            columns = ParseLayoutXml(layoutXml, fetchXml);
            (sorts, filter) = ParseFetchXml(fetchXml);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not PpdsException)
        {
            throw new PpdsException(ErrorCodes.Validation.SchemaInvalid,
                $"View '{resolvedName}' contains malformed XML in layoutxml or fetchxml.", ex);
        }

        return new ViewDetail(
            id,
            resolvedName,
            queryType,
            queryTypeLabel,
            entityLogicalName,
            columns,
            sorts,
            filter,
            fetchXml,
            layoutXml);
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
            new ColumnSet("savedqueryid", "layoutxml", "fetchxml", "ismanaged", "returnedtypecode"), cancellationToken);

        var layoutXml = entity.GetAttributeValue<string>("layoutxml");
        GuardHasLayout(layoutXml, viewName);
        layoutXml ??= "<grid><row /></grid>";
        var fetchXml = entity.GetAttributeValue<string>("fetchxml") ?? "<fetch><entity /></fetch>";

        bool isRelated = viaRelationship != null;
        string? relName = null, relEntity = null, relPkName = null, relAlias = null;

        if (isRelated)
        {
            var rels = await _cachedMetadata.GetRelationshipsAsync(entityLogicalName, cancellationToken);
            var rel = rels.ManyToOne.FirstOrDefault(r =>
                string.Equals(r.ReferencingAttribute, viaRelationship, StringComparison.OrdinalIgnoreCase));
            if (rel == null)
                throw new PpdsException(ErrorCodes.View.RelationshipNotFound,
                    $"Relationship attribute '{viaRelationship}' not found in metadata for entity '{entityLogicalName}'.");

            relName = viaRelationship;
            relEntity = rel.ReferencedEntity;
            relPkName = rel.ReferencedAttribute;
            relAlias = viaRelationship;
        }

        var layoutDoc = XDocument.Parse(layoutXml);
        AddCellsWithWarnings(layoutDoc, columns, relAlias, progressReporter);

        var fetchDoc = XDocument.Parse(fetchXml);
        if (isRelated && relName != null)
        {
            fetchDoc = AddRelatedLinkEntity(fetchDoc, relEntity!, relPkName!, viaRelationship!, relAlias!, columns);
        }
        else
        {
            var baseEntity = fetchDoc.Root?.Element("entity");
            if (baseEntity == null)
                throw new PpdsException(ErrorCodes.Validation.SchemaInvalid, "View FetchXML has no base entity.");
            EnsureAttributes(baseEntity, columns);
        }

        var update = new Entity("savedquery", savedQueryId);
        update["layoutxml"] = layoutDoc.ToString(SaveOptions.DisableFormatting);
        update["fetchxml"] = fetchDoc.ToString(SaveOptions.DisableFormatting);

        await ApplyViewWriteAsync(
            client, savedQueryId, viewName, "adding columns",
            update, entity, cancellationToken);

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
            new ColumnSet("savedqueryid", "layoutxml", "ismanaged", "returnedtypecode"), cancellationToken);

        var layoutXml = entity.GetAttributeValue<string>("layoutxml");
        GuardHasLayout(layoutXml, viewName);
        layoutXml ??= "<grid><row /></grid>";
        var layoutDoc = XDocument.Parse(layoutXml);
        layoutDoc = RemoveCell(layoutDoc, attributeName);

        var update = new Entity("savedquery", savedQueryId);
        update["layoutxml"] = layoutDoc.ToString(SaveOptions.DisableFormatting);

        await ApplyViewWriteAsync(
            client, savedQueryId, viewName, $"removing column '{attributeName}'",
            update, entity, cancellationToken);

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
            new ColumnSet("savedqueryid", "layoutxml", "ismanaged", "returnedtypecode"), cancellationToken);

        var layoutXml = entity.GetAttributeValue<string>("layoutxml");
        GuardHasLayout(layoutXml, viewName);
        layoutXml ??= "<grid><row /></grid>";
        var layoutDoc = XDocument.Parse(layoutXml);
        layoutDoc = UpdateCellWidth(layoutDoc, attributeName, width);

        var update = new Entity("savedquery", savedQueryId);
        update["layoutxml"] = layoutDoc.ToString(SaveOptions.DisableFormatting);

        await ApplyViewWriteAsync(
            client, savedQueryId, viewName, "updating column width",
            update, entity, cancellationToken);

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
            new ColumnSet("savedqueryid", "layoutxml", "ismanaged", "returnedtypecode"), cancellationToken);

        var layoutXml = entity.GetAttributeValue<string>("layoutxml");
        GuardHasLayout(layoutXml, viewName);
        layoutXml ??= "<grid><row /></grid>";
        var layoutDoc = XDocument.Parse(layoutXml);
        layoutDoc = ReorderCells(layoutDoc, orderedAttributes);

        var update = new Entity("savedquery", savedQueryId);
        update["layoutxml"] = layoutDoc.ToString(SaveOptions.DisableFormatting);

        await ApplyViewWriteAsync(
            client, savedQueryId, viewName, "reordering columns",
            update, entity, cancellationToken);

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
            new ColumnSet("savedqueryid", "fetchxml", "ismanaged", "returnedtypecode"), cancellationToken);

        var fetchXml = entity.GetAttributeValue<string>("fetchxml") ?? "<fetch><entity /></fetch>";
        var fetchDoc = XDocument.Parse(fetchXml);
        fetchDoc = SetOrderElements(fetchDoc, sorts);

        var update = new Entity("savedquery", savedQueryId);
        update["fetchxml"] = fetchDoc.ToString(SaveOptions.DisableFormatting);

        await ApplyViewWriteAsync(
            client, savedQueryId, viewName, "setting sort",
            update, entity, cancellationToken);

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
            new ColumnSet("savedqueryid", "fetchxml", "ismanaged", "returnedtypecode"), cancellationToken);

        var fetchXml = entity.GetAttributeValue<string>("fetchxml") ?? "<fetch><entity /></fetch>";
        var fetchDoc = XDocument.Parse(fetchXml);
        fetchDoc = RemoveOrderElements(fetchDoc);

        var update = new Entity("savedquery", savedQueryId);
        update["fetchxml"] = fetchDoc.ToString(SaveOptions.DisableFormatting);

        await ApplyViewWriteAsync(
            client, savedQueryId, viewName, "clearing sort",
            update, entity, cancellationToken);

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
            new ColumnSet("savedqueryid", "fetchxml", "ismanaged", "returnedtypecode"), cancellationToken);

        var fetchXml = entity.GetAttributeValue<string>("fetchxml") ?? "<fetch><entity /></fetch>";
        var fetchDoc = XDocument.Parse(fetchXml);
        fetchDoc = SetFilterElement(fetchDoc, filterElement);

        var update = new Entity("savedquery", savedQueryId);
        update["fetchxml"] = fetchDoc.ToString(SaveOptions.DisableFormatting);

        await ApplyViewWriteAsync(
            client, savedQueryId, viewName, "setting filter",
            update, entity, cancellationToken);

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
            new ColumnSet("savedqueryid", "fetchxml", "ismanaged", "returnedtypecode"), cancellationToken);

        var fetchXml = entity.GetAttributeValue<string>("fetchxml") ?? "<fetch><entity /></fetch>";
        var fetchDoc = XDocument.Parse(fetchXml);
        fetchDoc = RemoveFilterElement(fetchDoc);

        var update = new Entity("savedquery", savedQueryId);
        update["fetchxml"] = fetchDoc.ToString(SaveOptions.DisableFormatting);

        await ApplyViewWriteAsync(
            client, savedQueryId, viewName, "clearing filter",
            update, entity, cancellationToken);

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
        var (savedQueryId, entity) = await FetchViewRecordAsync(
            client, entityLogicalName, viewName,
            new ColumnSet("savedqueryid", "fetchxml", "ismanaged", "returnedtypecode"), cancellationToken);

        var update = new Entity("savedquery", savedQueryId);
        update["fetchxml"] = fetchXml;

        await ApplyViewWriteAsync(
            client, savedQueryId, viewName, "setting fetchxml",
            update, entity, cancellationToken);

        await PostMutationAsync(client, savedQueryId, entityLogicalName, publish, solution, cancellationToken);
    }

    // ─── Private helpers ────────────────────────────────────────────────────────

    /// <summary>
    /// Surfaces the underlying Dataverse fault (hex code + message) from a wrapped exception
    /// so callers see the real cause (e.g. 0x80040216) instead of an opaque wrapper message.
    /// </summary>
    private static string DescribeDataverseFault(Exception ex)
    {
        for (Exception? current = ex; current != null; current = current.InnerException)
        {
            if (current is FaultException<OrganizationServiceFault> typed && typed.Detail != null)
                return $"Dataverse error 0x{typed.Detail.ErrorCode:x8}: {typed.Detail.Message}";
        }
        return ex.Message;
    }

    /// <summary>
    /// Writes a savedquery field update, then confirms it persisted. Carries returnedtypecode so the
    /// write succeeds on managed views (#1200), surfaces the real Dataverse fault on failure (#1190),
    /// and fails loudly when the platform accepts the write but silently drops it (#1194) rather than
    /// reporting a false success.
    /// </summary>
    private async Task ApplyViewWriteAsync(
        IDataverseClient client,
        Guid savedQueryId,
        string viewName,
        string operation,
        Entity update,
        Entity source,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(update);

        // Carry returnedtypecode so the platform has the entity context for the write. Without it,
        // updates that change fetchxml are rejected with 0x80040216 ("An unexpected error occurred")
        // on managed views — most visibly when editing find columns on a managed Quick Find view.
        // The Power Apps maker sends it the same way (confirmed by capturing its network call). (#1200)
        if (source.Contains("returnedtypecode"))
            update["returnedtypecode"] = source["returnedtypecode"];

        var isManaged = source.GetAttributeValue<bool>("ismanaged");

        try
        {
            await client.UpdateAsync(update, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var fault = DescribeDataverseFault(ex);
            if (isManaged)
                throw new PpdsException(ErrorCodes.View.ManagedComponentNotEditable,
                    $"Cannot update XML on managed view '{viewName}' ({fault}). " +
                    "The managed component may be locked for this change (iscustomizable = false).", ex);
            throw new PpdsException(ErrorCodes.View.UpdateFailed,
                $"Failed to update view '{viewName}' after {operation}: {fault}", ex);
        }

        // Check all XML fields together, even for equivalent no-op writes. Another writer may have
        // changed/deleted the draft, or only one of layoutxml/fetchxml may reflect our request.
        await VerifyViewWritePersistedAsync(client, savedQueryId, viewName, update, ct);
    }

    /// <summary>
    /// Verifies the intended XML in the unpublished draft, with bounded retries for read lag.
    /// Unavailable reads are reported separately from observed mismatches; neither confirms success.
    /// </summary>
    private async Task VerifyViewWritePersistedAsync(
        IDataverseClient client, Guid savedQueryId, string viewName, Entity update, CancellationToken ct)
    {
        var fields = update.Attributes.Keys.Where(k => k is "layoutxml" or "fetchxml").ToArray();
        bool observedMismatch = false;
        Exception? readError = null;
        const int maxAttempts = 3;
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            var query = new QueryExpression("savedquery")
            {
                ColumnSet = new ColumnSet(fields),
                Criteria = new FilterExpression
                {
                    Conditions = { new ConditionExpression("savedqueryid", ConditionOperator.Equal, savedQueryId) }
                }
            };

            try
            {
                var response = (RetrieveUnpublishedMultipleResponse?)await client.ExecuteAsync(
                    new RetrieveUnpublishedMultipleRequest { Query = query }, ct);
                var result = response?.EntityCollection;
                if (result == null)
                    throw new InvalidOperationException("RetrieveUnpublished returned no result collection.");
                if (result.Entities.Count == 1 && fields.All(field =>
                    result.Entities[0].GetAttributeValue<string>(field) is string current &&
                    XmlEquivalent(current, update.GetAttributeValue<string>(field), field)))
                    return;
                observedMismatch = true;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex,
                    "Could not verify the write to savedquery {SavedQueryId} via RetrieveUnpublished.", savedQueryId);
                readError = ex;
            }
            if (attempt < maxAttempts)
                await Task.Delay(TimeSpan.FromMilliseconds(750), ct);
        }
        if (observedMismatch)
            throw new PpdsException(ErrorCodes.View.UpdateNotPersisted,
                $"Update to view '{viewName}' was accepted, but the draft does not match the requested XML " +
                "or the record is missing. A concurrent edit or platform normalization may have intervened. " +
                "Inspect the unpublished view before retrying; changes may have been partially applied.");
        throw new PpdsException(ErrorCodes.View.VerificationFailed,
            $"Update to view '{viewName}' was accepted, but its persistence could not be verified. " +
            "Inspect the unpublished view before publishing or retrying.", readError!);
    }

    /// <summary>Structural XML equality (ignores formatting); falls back to ordinal compare.</summary>
    private static bool XmlEquivalent(string a, string b, string fieldName)
    {
        if (string.Equals(a, b, StringComparison.Ordinal)) return true;
        try
        {
            var left = XDocument.Parse(a);
            var right = XDocument.Parse(b);
            if (fieldName == "fetchxml")
            {
                // Dataverse maintains this root annotation. It does not change query semantics.
                left.Root?.Attribute("savedqueryid")?.Remove();
                right.Root?.Attribute("savedqueryid")?.Remove();
            }
            NormalizeAttributeOrder(left);
            NormalizeAttributeOrder(right);
            return XNode.DeepEquals(left.Root, right.Root);
        }
        catch
        {
            return false;
        }
    }

    private static void NormalizeAttributeOrder(XDocument document)
    {
        foreach (var element in document.Descendants())
        {
            var attributes = element.Attributes().OrderBy(a => a.Name.ToString(), StringComparer.Ordinal).ToList();
            element.RemoveAttributes();
            element.Add(attributes);
        }
    }

    private async Task<int> ResolveObjectTypeCodeAsync(string entityLogicalName, CancellationToken ct)
    {
        var entities = await _cachedMetadata.GetEntitiesAsync(ct);
        var meta = entities.FirstOrDefault(e => string.Equals(e.LogicalName, entityLogicalName, StringComparison.OrdinalIgnoreCase));
        if (meta == null)
            throw new PpdsException(ErrorCodes.View.NotFound,
                $"Entity '{entityLogicalName}' not found in metadata.");
        return meta.ObjectTypeCode;
    }

    private static void GuardHasLayout(string? layoutXml, string viewName)
    {
        if (string.IsNullOrWhiteSpace(layoutXml))
            throw new PpdsException(ErrorCodes.View.NoLayout,
                $"View '{viewName}' has no layout definition and cannot be modified. " +
                "This is typical of system-generated views (e.g. auto-created 'My' views) " +
                "that are not customizable.");
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
            Criteria = new FilterExpression()
        };
        query.Criteria.AddCondition("returnedtypecode", ConditionOperator.Equal, otc);
        if (Guid.TryParse(viewName.Trim('{', '}'), out var viewGuid))
            query.Criteria.AddCondition("savedqueryid", ConditionOperator.Equal, viewGuid);
        else
            query.Criteria.AddCondition("name", ConditionOperator.Equal, viewName);

        // savedquery is a publishable entity. RetrieveMultiple returns the published version,
        // which means any pending draft changes (e.g., a prior reorder-columns without --publish)
        // would be invisible and overwritten. RetrieveUnpublishedMultiple returns the current
        // working state so mutating operations always compose correctly.
        EntityCollection result;
        try
        {
            var unpublishedRequest = new RetrieveUnpublishedMultipleRequest { Query = query };
            var unpublishedResponse = (RetrieveUnpublishedMultipleResponse)await client.ExecuteAsync(unpublishedRequest, ct);
            result = unpublishedResponse.EntityCollection;
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
        string? relAlias,
        IProgressReporter? progressReporter = null)
    {
        var row = layout.Descendants("row").FirstOrDefault();
        if (row == null) return false;

        bool changed = false;
        foreach (var col in columns)
        {
            var name = relAlias == null ? col.AttributeName : $"{relAlias}.{col.AttributeName}";
            var existing = row.Elements("cell")
                .FirstOrDefault(c => string.Equals((string?)c.Attribute("name"), name, StringComparison.OrdinalIgnoreCase));
            if (existing != null)
            {
                progressReporter?.ReportWarning($"Column '{col.AttributeName}' already exists in view — skipping (idempotent).");
                continue;
            }

            var cell = new XElement("cell", new XAttribute("name", name), new XAttribute("width", col.Width));

            row.Add(cell);
            changed = true;
        }
        return changed;
    }

    internal static XDocument AddRelatedLinkEntity(
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
                new XAttribute("link-type", "outer"),
                new XAttribute("alias", relAlias));
            entityElem.Add(linkEntity);
        }
        else if ((string?)linkEntity.Attribute("name") != relEntity ||
                 (string?)linkEntity.Attribute("from") != relPkName ||
                 (string?)linkEntity.Attribute("to") != viaRelationship)
        {
            throw new PpdsException(ErrorCodes.Validation.SchemaInvalid,
                $"FetchXML alias '{relAlias}' is already used by another relationship. Rename that alias before adding columns.");
        }

        EnsureAttributes(linkEntity, columns);
        return fetch;
    }

    private static void EnsureAttributes(XElement entity, IReadOnlyList<ColumnSpec> columns)
    {
        if (entity.Element("all-attributes") != null) return;
        foreach (var col in columns)
        {
            var existingAttr = entity.Elements("attribute")
                .FirstOrDefault(a => (string?)a.Attribute("name") == col.AttributeName && a.Attribute("alias") == null);
            if (existingAttr == null)
            {
                entity.Add(new XElement("attribute", new XAttribute("name", col.AttributeName)));
            }
        }

    }

    // ─── Internal XML helpers (pure; testable) ──────────────────────────────────

    internal static List<ViewColumn> ParseLayoutXml(string layoutXml, string? fetchXml = null)
    {
        var doc = XDocument.Parse(layoutXml);
        var row = doc.Descendants("row").FirstOrDefault();
        if (row == null) return [];

        var links = fetchXml == null ? [] : XDocument.Parse(fetchXml).Descendants("link-entity").ToList();
        return row.Elements("cell").Select(cell =>
        {
            var name = (string?)cell.Attribute("name") ?? "";
            var width = int.TryParse((string?)cell.Attribute("width"), out var w) ? w : 150;
            var alias = name.Contains('.') ? name[..name.IndexOf('.')] : null;
            var link = links.FirstOrDefault(l => (string?)l.Attribute("alias") == alias);
            var isRelated = alias != null || cell.Attribute("RelatedEntityName") != null;
            var relAttr = (string?)link?.Attribute("to") ?? (string?)cell.Attribute("RelationshipName");
            var relEntity = (string?)link?.Attribute("name") ?? (string?)cell.Attribute("RelatedEntityName");
            var relPkName = (string?)link?.Attribute("from") ?? (string?)cell.Attribute("RelatedEntityPrimaryKeyName");
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
            .FirstOrDefault(c => string.Equals((string?)c.Attribute("name"), attributeName, StringComparison.OrdinalIgnoreCase));
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
            .FirstOrDefault(c => string.Equals((string?)c.Attribute("name"), attributeName, StringComparison.OrdinalIgnoreCase));
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
            .Select(attr => existingCells.FirstOrDefault(c => string.Equals((string?)c.Attribute("name"), attr, StringComparison.OrdinalIgnoreCase)))
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

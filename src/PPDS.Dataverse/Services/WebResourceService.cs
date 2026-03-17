using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using PPDS.Dataverse.Generated;
using PPDS.Dataverse.Pooling;

namespace PPDS.Dataverse.Services;

/// <summary>
/// Service for querying, updating, and publishing Dataverse web resources.
/// </summary>
/// <remarks>
/// <para>
/// This service lives in PPDS.Dataverse (which cannot reference PPDS.Cli), so it throws
/// standard exceptions (<see cref="InvalidOperationException"/>, <see cref="KeyNotFoundException"/>).
/// The RPC handler layer in PPDS.Cli wraps these in <c>PpdsException</c> with the appropriate
/// <c>ErrorCodes.WebResource.*</c> codes.
/// </para>
/// </remarks>
public class WebResourceService : IWebResourceService
{
    private readonly IDataverseConnectionPool _pool;
    private readonly ISolutionService _solutionService;
    private readonly ILogger<WebResourceService> _logger;

    // Text type codes for the textOnly filter
    private static readonly int[] TextTypes = [1, 2, 3, 4, 9, 11, 12];

    // Standard columns for list queries (metadata only, no content)
    private static readonly string[] ListColumns =
    [
        WebResource.Fields.WebResourceId,
        WebResource.Fields.Name,
        WebResource.Fields.DisplayName,
        WebResource.Fields.WebResourceType,
        WebResource.Fields.IsManaged,
        WebResource.Fields.CreatedBy,
        WebResource.Fields.CreatedOn,
        WebResource.Fields.ModifiedBy,
        WebResource.Fields.ModifiedOn
    ];

    /// <summary>
    /// Initializes a new instance of the <see cref="WebResourceService"/> class.
    /// </summary>
    /// <param name="pool">The connection pool.</param>
    /// <param name="solutionService">The solution service for component lookups.</param>
    /// <param name="logger">The logger.</param>
    public WebResourceService(
        IDataverseConnectionPool pool,
        ISolutionService solutionService,
        ILogger<WebResourceService> logger)
    {
        _pool = pool ?? throw new ArgumentNullException(nameof(pool));
        _solutionService = solutionService ?? throw new ArgumentNullException(nameof(solutionService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public async Task<List<WebResourceInfo>> ListAsync(
        Guid? solutionId = null,
        bool textOnly = false,
        int top = 5000,
        CancellationToken cancellationToken = default)
    {
        await using var client = await _pool.GetClientAsync(cancellationToken: cancellationToken);

        var query = new QueryExpression(WebResource.EntityLogicalName)
        {
            ColumnSet = new ColumnSet(ListColumns),
            TopCount = top
        };

        // Text-only filter: include only text type codes
        if (textOnly)
        {
            query.Criteria.AddCondition(
                WebResource.Fields.WebResourceType,
                ConditionOperator.In,
                TextTypes.Cast<object>().ToArray());
        }

        // Solution filter: get component IDs via SolutionService, then IN filter
        if (solutionId.HasValue)
        {
            var components = await _solutionService.GetComponentsAsync(
                solutionId.Value,
                componentType: 61, // WebResource
                cancellationToken: cancellationToken);

            var webResourceIds = components
                .Select(c => c.ObjectId)
                .Where(id => id != Guid.Empty)
                .ToArray();

            if (webResourceIds.Length == 0)
            {
                return []; // No web resources in this solution
            }

            query.Criteria.AddCondition(
                WebResource.Fields.WebResourceId,
                ConditionOperator.In,
                webResourceIds.Cast<object>().ToArray());
        }

        // Default sort: name ascending
        query.AddOrder(WebResource.Fields.Name, OrderType.Ascending);

        _logger.LogDebug(
            "Querying web resources with solutionId: {SolutionId}, textOnly: {TextOnly}, top: {Top}",
            solutionId, textOnly, top);

        var results = await client.RetrieveMultipleAsync(query, cancellationToken);
        return results.Entities.Select(MapToWebResourceInfo).ToList();
    }

    /// <inheritdoc />
    public async Task<WebResourceInfo?> GetAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var client = await _pool.GetClientAsync(cancellationToken: cancellationToken);

        var query = new QueryExpression(WebResource.EntityLogicalName)
        {
            ColumnSet = new ColumnSet(ListColumns),
            TopCount = 1
        };
        query.Criteria.AddCondition(WebResource.Fields.WebResourceId, ConditionOperator.Equal, id);

        _logger.LogDebug("Getting web resource: {Id}", id);

        var results = await client.RetrieveMultipleAsync(query, cancellationToken);
        return results.Entities.FirstOrDefault() is { } entity ? MapToWebResourceInfo(entity) : null;
    }

    /// <inheritdoc />
    public async Task<WebResourceContent?> GetContentAsync(
        Guid id,
        bool published = false,
        CancellationToken cancellationToken = default)
    {
        await using var client = await _pool.GetClientAsync(cancellationToken: cancellationToken);

        var contentColumns = new ColumnSet(
            WebResource.Fields.Content,
            WebResource.Fields.Name,
            WebResource.Fields.WebResourceType,
            WebResource.Fields.ModifiedOn);

        Entity entity;
        if (published)
        {
            // Standard retrieve returns published content
            var query = new QueryExpression(WebResource.EntityLogicalName)
            {
                ColumnSet = contentColumns,
                TopCount = 1
            };
            query.Criteria.AddCondition(WebResource.Fields.WebResourceId, ConditionOperator.Equal, id);

            var results = await client.RetrieveMultipleAsync(query, cancellationToken);
            if (results.Entities.FirstOrDefault() is not { } found)
            {
                return null;
            }
            entity = found;
        }
        else
        {
            // RetrieveUnpublished returns latest saved (unpublished) content
            entity = await client.RetrieveUnpublishedAsync(
                WebResource.EntityLogicalName, id, contentColumns, cancellationToken);
        }

        var base64Content = entity.GetAttributeValue<string>(WebResource.Fields.Content);
        var decodedContent = base64Content != null
            ? Encoding.UTF8.GetString(Convert.FromBase64String(base64Content))
            : null;

        return new WebResourceContent(
            id,
            entity.GetAttributeValue<string>(WebResource.Fields.Name) ?? "",
            entity.GetAttributeValue<OptionSetValue>(WebResource.Fields.WebResourceType)?.Value ?? 0,
            decodedContent,
            entity.GetAttributeValue<DateTime?>(WebResource.Fields.ModifiedOn));
    }

    /// <inheritdoc />
    public async Task<DateTime?> GetModifiedOnAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var client = await _pool.GetClientAsync(cancellationToken: cancellationToken);

        var query = new QueryExpression(WebResource.EntityLogicalName)
        {
            ColumnSet = new ColumnSet(WebResource.Fields.ModifiedOn),
            TopCount = 1
        };
        query.Criteria.AddCondition(WebResource.Fields.WebResourceId, ConditionOperator.Equal, id);

        _logger.LogDebug("Getting modifiedOn for web resource: {Id}", id);

        var results = await client.RetrieveMultipleAsync(query, cancellationToken);
        return results.Entities.FirstOrDefault()?.GetAttributeValue<DateTime?>(WebResource.Fields.ModifiedOn);
    }

    /// <inheritdoc />
    public async Task UpdateContentAsync(Guid id, string content, CancellationToken cancellationToken = default)
    {
        // Validate editability — get metadata to check if text type
        var info = await GetAsync(id, cancellationToken);
        if (info == null)
        {
            throw new KeyNotFoundException($"Web resource '{id}' not found.");
        }

        if (!info.IsTextType)
        {
            throw new InvalidOperationException(
                $"Web resource '{info.Name}' is a {info.TypeName} file and cannot be edited. Binary types are read-only.");
        }

        await using var client = await _pool.GetClientAsync(cancellationToken: cancellationToken);

        var base64Content = Convert.ToBase64String(Encoding.UTF8.GetBytes(content));

        var update = new Entity(WebResource.EntityLogicalName, id)
        {
            [WebResource.Fields.Content] = base64Content
        };

        await client.UpdateAsync(update, cancellationToken);

        _logger.LogInformation("Updated web resource content: {Name} ({Id})", info.Name, id);
    }

    /// <inheritdoc />
    public async Task<int> PublishAsync(IReadOnlyList<Guid> ids, CancellationToken cancellationToken = default)
    {
        if (ids.Count == 0) return 0;

        await using var client = await _pool.GetClientAsync(cancellationToken: cancellationToken);

        // Build PublishXml parameter XML for web resources
        var webResourceXml = string.Join("",
            ids.Select(id => $"<webresource>{{{id}}}</webresource>"));
        var parameterXml = $"<importexportxml><webresources>{webResourceXml}</webresources></importexportxml>";

        // Use extension method for per-environment concurrency protection
        var environmentKey = client.ConnectedOrgUniqueName ?? "default";
        await client.PublishXmlAsync(parameterXml, environmentKey, cancellationToken);

        _logger.LogInformation("Published {Count} web resource(s)", ids.Count);
        return ids.Count;
    }

    /// <inheritdoc />
    public async Task PublishAllAsync(CancellationToken cancellationToken = default)
    {
        await using var client = await _pool.GetClientAsync(cancellationToken: cancellationToken);

        // Use extension method for per-environment concurrency protection
        var environmentKey = client.ConnectedOrgUniqueName ?? "default";
        await client.PublishAllXmlAsync(environmentKey, cancellationToken);

        _logger.LogInformation("Published all customizations");
    }

    private static WebResourceInfo MapToWebResourceInfo(Entity entity)
    {
        var createdByRef = entity.GetAttributeValue<EntityReference>(WebResource.Fields.CreatedBy);
        var modifiedByRef = entity.GetAttributeValue<EntityReference>(WebResource.Fields.ModifiedBy);
        var typeValue = entity.GetAttributeValue<OptionSetValue>(WebResource.Fields.WebResourceType);

        return new WebResourceInfo(
            entity.Id,
            entity.GetAttributeValue<string>(WebResource.Fields.Name) ?? "",
            entity.GetAttributeValue<string>(WebResource.Fields.DisplayName),
            typeValue?.Value ?? 0,
            entity.GetAttributeValue<bool?>(WebResource.Fields.IsManaged) ?? false,
            createdByRef?.Name,
            entity.GetAttributeValue<DateTime?>(WebResource.Fields.CreatedOn),
            modifiedByRef?.Name,
            entity.GetAttributeValue<DateTime?>(WebResource.Fields.ModifiedOn));
    }
}

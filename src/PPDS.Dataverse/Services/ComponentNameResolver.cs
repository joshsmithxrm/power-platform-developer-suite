using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using PPDS.Dataverse.Metadata;
using PPDS.Dataverse.Pooling;

namespace PPDS.Dataverse.Services;

public class ComponentNameResolver : IComponentNameResolver
{
    private const int MaxBatchSize = 100;

    private readonly ICachedMetadataProvider _metadataProvider;
    private readonly IDataverseConnectionPool _pool;
    private readonly ILogger<ComponentNameResolver> _logger;

    private static readonly Dictionary<int, ComponentTypeMapping> TypeMappings = new()
    {
        [26]  = new("savedquery", "name", null, null),
        [29]  = new("workflow", "uniquename", null, "name"),
        [60]  = new("systemform", "name", null, null),
        [61]  = new("webresource", "name", null, null),
        [66]  = new("customcontrol", "name", null, null),
        [90]  = new("plugintype", "name", null, null),
        [91]  = new("pluginassembly", "name", null, null),
        [92]  = new("sdkmessageprocessingstep", "name", null, null),
        [300] = new("canvasapp", "name", null, "displayname"),
        [371] = new("connector", "name", null, "displayname"),
        [372] = new("connector", "name", null, "displayname"),
        [380] = new("environmentvariabledefinition", null, "schemaname", "displayname"),
        [381] = new("environmentvariablevalue", null, "schemaname", null),
    };

    public ComponentNameResolver(
        ICachedMetadataProvider metadataProvider,
        IDataverseConnectionPool pool,
        ILogger<ComponentNameResolver> logger)
    {
        _metadataProvider = metadataProvider ?? throw new ArgumentNullException(nameof(metadataProvider));
        _pool = pool ?? throw new ArgumentNullException(nameof(pool));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<IReadOnlyDictionary<Guid, ComponentNames>> ResolveAsync(
        int componentType,
        IReadOnlyList<Guid> objectIds,
        CancellationToken cancellationToken = default)
    {
        if (objectIds.Count == 0)
            return new Dictionary<Guid, ComponentNames>();

        var stopwatch = Stopwatch.StartNew();
        ComponentTypeMapping? mapping = null;

        try
        {
            IReadOnlyDictionary<Guid, ComponentNames> result;

            if (componentType == 1)
            {
                result = await ResolveEntitiesAsync(objectIds, cancellationToken);
            }
            else if (TypeMappings.TryGetValue(componentType, out mapping))
            {
                result = await ResolveFromTableAsync(mapping, objectIds, cancellationToken);
            }
            else
            {
                return new Dictionary<Guid, ComponentNames>();
            }

            stopwatch.Stop();
            _logger.LogInformation(
                "Resolved {Count} {TypeName} names in {ElapsedMs}ms",
                result.Count,
                componentType == 1 ? "Entity" : mapping!.TableName,
                stopwatch.ElapsedMilliseconds);

            return result;
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            _logger.LogWarning(
                ex,
                "Failed to resolve names for component type {ComponentType} ({Count} IDs) after {ElapsedMs}ms",
                componentType, objectIds.Count, stopwatch.ElapsedMilliseconds);
            return new Dictionary<Guid, ComponentNames>();
        }
    }

    private async Task<IReadOnlyDictionary<Guid, ComponentNames>> ResolveEntitiesAsync(
        IReadOnlyList<Guid> objectIds,
        CancellationToken cancellationToken)
    {
        var entities = await _metadataProvider.GetEntitiesAsync(cancellationToken);
        var entityByMetadataId = entities.ToDictionary(e => e.MetadataId);
        var lookup = new Dictionary<Guid, ComponentNames>();

        foreach (var objectId in objectIds)
        {
            if (entityByMetadataId.TryGetValue(objectId, out var entity))
            {
                lookup[objectId] = new ComponentNames(
                    entity.LogicalName,
                    entity.SchemaName,
                    entity.DisplayName);
            }
        }

        return lookup;
    }

    private async Task<IReadOnlyDictionary<Guid, ComponentNames>> ResolveFromTableAsync(
        ComponentTypeMapping mapping,
        IReadOnlyList<Guid> objectIds,
        CancellationToken cancellationToken)
    {
        var result = new Dictionary<Guid, ComponentNames>();

        for (var i = 0; i < objectIds.Count; i += MaxBatchSize)
        {
            var batch = objectIds.Skip(i).Take(MaxBatchSize).ToArray();
            var batchResult = await QueryBatchAsync(mapping, batch, cancellationToken);

            foreach (var kvp in batchResult)
                result[kvp.Key] = kvp.Value;
        }

        return result;
    }

    private async Task<Dictionary<Guid, ComponentNames>> QueryBatchAsync(
        ComponentTypeMapping mapping,
        Guid[] objectIds,
        CancellationToken cancellationToken)
    {
        await using var client = await _pool.GetClientAsync(cancellationToken: cancellationToken);

        var columns = new List<string>();
        if (mapping.LogicalNameField != null) columns.Add(mapping.LogicalNameField);
        if (mapping.SchemaNameField != null) columns.Add(mapping.SchemaNameField);
        if (mapping.DisplayNameField != null) columns.Add(mapping.DisplayNameField);

        var primaryKey = mapping.TableName + "id";
        var query = new QueryExpression(mapping.TableName)
        {
            ColumnSet = new ColumnSet(columns.ToArray())
        };
        query.Criteria.AddCondition(primaryKey, ConditionOperator.In, objectIds.Cast<object>().ToArray());

        var response = await client.RetrieveMultipleAsync(query, cancellationToken);

        var result = new Dictionary<Guid, ComponentNames>();
        foreach (var entity in response.Entities)
        {
            var logicalName = mapping.LogicalNameField != null
                ? NullIfEmpty(entity.GetAttributeValue<string>(mapping.LogicalNameField)) : null;
            var schemaName = mapping.SchemaNameField != null
                ? NullIfEmpty(entity.GetAttributeValue<string>(mapping.SchemaNameField)) : null;
            var displayName = mapping.DisplayNameField != null
                ? NullIfEmpty(entity.GetAttributeValue<string>(mapping.DisplayNameField)) : null;

            result[entity.Id] = new ComponentNames(logicalName, schemaName, displayName);
        }

        return result;
    }

    private static string? NullIfEmpty(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;

    private sealed record ComponentTypeMapping(
        string TableName,
        string? LogicalNameField,
        string? SchemaNameField,
        string? DisplayNameField);
}

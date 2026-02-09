using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using PPDS.Dataverse.Metadata.Models;
using PPDS.Dataverse.Query;

namespace PPDS.Dataverse.Metadata;

/// <summary>
/// Queries Dataverse metadata via <see cref="IMetadataService"/> and returns results
/// as virtual table rows (dictionaries of <see cref="QueryValue"/>).
/// Bridges the rich DTO-based metadata API to the flat row format used by the query engine.
/// </summary>
public sealed class MetadataQueryExecutor : IMetadataQueryExecutor
{
    private readonly IMetadataService? _metadataService;

    /// <summary>
    /// Creates executor with an optional metadata service.
    /// When null, all queries return empty results (useful for offline/testing scenarios).
    /// </summary>
    public MetadataQueryExecutor(IMetadataService? metadataService = null)
    {
        _metadataService = metadataService;
    }

    /// <inheritdoc />
    public bool IsMetadataTable(string schemaQualifiedName)
    {
        return MetadataTableDefinitions.IsMetadataTable(schemaQualifiedName);
    }

    /// <inheritdoc />
    public IReadOnlyList<string> GetAvailableColumns(string tableName)
    {
        var columns = MetadataTableDefinitions.GetColumns(tableName);
        if (columns == null)
        {
            throw new ArgumentException($"Unknown metadata table: {tableName}");
        }

        return columns;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<IReadOnlyDictionary<string, QueryValue>>> QueryMetadataAsync(
        string tableName,
        IReadOnlyList<string>? requestedColumns = null,
        CancellationToken cancellationToken = default)
    {
        if (_metadataService == null)
        {
            return Array.Empty<IReadOnlyDictionary<string, QueryValue>>();
        }

        var normalizedTable = MetadataTableDefinitions.GetTableName(tableName);

        return normalizedTable.ToLowerInvariant() switch
        {
            "entity" => await QueryEntityMetadataAsync(requestedColumns, cancellationToken).ConfigureAwait(false),
            "attribute" => await QueryAttributeMetadataAsync(requestedColumns, cancellationToken).ConfigureAwait(false),
            "relationship_1_n" => await QueryOneToManyRelationshipsAsync(requestedColumns, cancellationToken).ConfigureAwait(false),
            "relationship_n_n" => await QueryManyToManyRelationshipsAsync(requestedColumns, cancellationToken).ConfigureAwait(false),
            "optionset" => await QueryOptionSetsAsync(requestedColumns, cancellationToken).ConfigureAwait(false),
            "optionsetvalue" => await QueryOptionSetValuesAsync(requestedColumns, cancellationToken).ConfigureAwait(false),
            _ => throw new ArgumentException($"Unknown metadata table: {tableName}")
        };
    }

    #region Table-specific queries

    private async Task<IReadOnlyList<IReadOnlyDictionary<string, QueryValue>>> QueryEntityMetadataAsync(
        IReadOnlyList<string>? requestedColumns, CancellationToken cancellationToken)
    {
        var entities = await _metadataService!.GetEntitiesAsync(cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        return entities.Select(e => FilterColumns(new Dictionary<string, QueryValue>(StringComparer.OrdinalIgnoreCase)
        {
            ["logicalname"] = QueryValue.Simple(e.LogicalName),
            ["displayname"] = QueryValue.Simple(e.DisplayName),
            ["pluraldisplayname"] = QueryValue.Simple(null), // EntitySummary doesn't include plural
            ["description"] = QueryValue.Simple(e.Description),
            ["schemaname"] = QueryValue.Simple(e.SchemaName),
            ["objecttypecode"] = QueryValue.Simple(e.ObjectTypeCode),
            ["iscustomentity"] = QueryValue.Simple(e.IsCustomEntity),
            ["isactivity"] = QueryValue.Simple(false), // Not in EntitySummary
            ["ownershiptype"] = QueryValue.Simple(e.OwnershipType),
            ["isvalidforadvancedfind"] = QueryValue.Simple(false), // Not in EntitySummary
            ["iscustomizable"] = QueryValue.Simple(false), // Not in EntitySummary
            ["isintersect"] = QueryValue.Simple(false), // Not in EntitySummary
            ["isvirtual"] = QueryValue.Simple(false), // Not in EntitySummary
            ["hasnotes"] = QueryValue.Simple(false), // Not in EntitySummary
            ["hasactivities"] = QueryValue.Simple(false), // Not in EntitySummary
            ["changetracking"] = QueryValue.Simple(false), // Not in EntitySummary
            ["entitysetname"] = QueryValue.Simple(e.EntitySetName)
        }, requestedColumns)).ToList();
    }

    private async Task<IReadOnlyList<IReadOnlyDictionary<string, QueryValue>>> QueryAttributeMetadataAsync(
        IReadOnlyList<string>? requestedColumns, CancellationToken cancellationToken)
    {
        // Get all entities first, then fetch attributes for each
        var entities = await _metadataService!.GetEntitiesAsync(cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        var allRows = new List<IReadOnlyDictionary<string, QueryValue>>();

        foreach (var entity in entities)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var attributes = await _metadataService.GetAttributesAsync(entity.LogicalName, cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            foreach (var attr in attributes)
            {
                allRows.Add(FilterColumns(new Dictionary<string, QueryValue>(StringComparer.OrdinalIgnoreCase)
                {
                    ["logicalname"] = QueryValue.Simple(attr.LogicalName),
                    ["entitylogicalname"] = QueryValue.Simple(entity.LogicalName),
                    ["displayname"] = QueryValue.Simple(attr.DisplayName),
                    ["description"] = QueryValue.Simple(attr.Description),
                    ["attributetype"] = QueryValue.Simple(attr.AttributeType),
                    ["schemaname"] = QueryValue.Simple(attr.SchemaName),
                    ["isrequired"] = QueryValue.Simple(attr.RequiredLevel != null && attr.RequiredLevel != "None"),
                    ["iscustomattribute"] = QueryValue.Simple(attr.IsCustomAttribute),
                    ["issearchable"] = QueryValue.Simple(attr.IsSearchable),
                    ["maxlength"] = QueryValue.Simple(attr.MaxLength),
                    ["minvalue"] = QueryValue.Simple(attr.MinValue),
                    ["maxvalue"] = QueryValue.Simple(attr.MaxValue),
                    ["precision"] = QueryValue.Simple(attr.Precision),
                    ["format"] = QueryValue.Simple(attr.Format),
                    ["imemode"] = QueryValue.Simple(null) // Not in AttributeMetadataDto
                }, requestedColumns));
            }
        }

        return allRows;
    }

    private async Task<IReadOnlyList<IReadOnlyDictionary<string, QueryValue>>> QueryOneToManyRelationshipsAsync(
        IReadOnlyList<string>? requestedColumns, CancellationToken cancellationToken)
    {
        var entities = await _metadataService!.GetEntitiesAsync(cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        var allRows = new List<IReadOnlyDictionary<string, QueryValue>>();
        var seenSchemaNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var entity in entities)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var relationships = await _metadataService.GetRelationshipsAsync(
                entity.LogicalName, "OneToMany", cancellationToken).ConfigureAwait(false);

            foreach (var rel in relationships.OneToMany)
            {
                // Avoid duplicates since the same relationship appears on both entities
                if (!seenSchemaNames.Add(rel.SchemaName))
                {
                    continue;
                }

                allRows.Add(FilterColumns(new Dictionary<string, QueryValue>(StringComparer.OrdinalIgnoreCase)
                {
                    ["schemaname"] = QueryValue.Simple(rel.SchemaName),
                    ["referencingentity"] = QueryValue.Simple(rel.ReferencingEntity),
                    ["referencedentity"] = QueryValue.Simple(rel.ReferencedEntity),
                    ["referencingattribute"] = QueryValue.Simple(rel.ReferencingAttribute),
                    ["referencedattribute"] = QueryValue.Simple(rel.ReferencedAttribute),
                    ["iscustomrelationship"] = QueryValue.Simple(rel.IsCustomRelationship),
                    ["isvalidforadvancedfind"] = QueryValue.Simple(false), // Not in RelationshipMetadataDto
                    ["relationshiptype"] = QueryValue.Simple(rel.RelationshipType),
                    ["securitytypes"] = QueryValue.Simple(rel.SecurityTypes)
                }, requestedColumns));
            }
        }

        return allRows;
    }

    private async Task<IReadOnlyList<IReadOnlyDictionary<string, QueryValue>>> QueryManyToManyRelationshipsAsync(
        IReadOnlyList<string>? requestedColumns, CancellationToken cancellationToken)
    {
        var entities = await _metadataService!.GetEntitiesAsync(cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        var allRows = new List<IReadOnlyDictionary<string, QueryValue>>();
        var seenSchemaNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var entity in entities)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var relationships = await _metadataService.GetRelationshipsAsync(
                entity.LogicalName, "ManyToMany", cancellationToken).ConfigureAwait(false);

            foreach (var rel in relationships.ManyToMany)
            {
                if (!seenSchemaNames.Add(rel.SchemaName))
                {
                    continue;
                }

                allRows.Add(FilterColumns(new Dictionary<string, QueryValue>(StringComparer.OrdinalIgnoreCase)
                {
                    ["schemaname"] = QueryValue.Simple(rel.SchemaName),
                    ["entity1logicalname"] = QueryValue.Simple(rel.Entity1LogicalName),
                    ["entity2logicalname"] = QueryValue.Simple(rel.Entity2LogicalName),
                    ["intersectentityname"] = QueryValue.Simple(rel.IntersectEntityName),
                    ["entity1intersectattribute"] = QueryValue.Simple(rel.Entity1IntersectAttribute),
                    ["entity2intersectattribute"] = QueryValue.Simple(rel.Entity2IntersectAttribute),
                    ["iscustomrelationship"] = QueryValue.Simple(rel.IsCustomRelationship)
                }, requestedColumns));
            }
        }

        return allRows;
    }

    private async Task<IReadOnlyList<IReadOnlyDictionary<string, QueryValue>>> QueryOptionSetsAsync(
        IReadOnlyList<string>? requestedColumns, CancellationToken cancellationToken)
    {
        var optionSets = await _metadataService!.GetGlobalOptionSetsAsync(cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        return optionSets.Select(os => FilterColumns(new Dictionary<string, QueryValue>(StringComparer.OrdinalIgnoreCase)
        {
            ["name"] = QueryValue.Simple(os.Name),
            ["displayname"] = QueryValue.Simple(os.DisplayName),
            ["description"] = QueryValue.Simple(os.Description),
            ["isglobal"] = QueryValue.Simple(os.IsGlobal),
            ["optionsettype"] = QueryValue.Simple(os.OptionSetType)
        }, requestedColumns)).ToList();
    }

    private async Task<IReadOnlyList<IReadOnlyDictionary<string, QueryValue>>> QueryOptionSetValuesAsync(
        IReadOnlyList<string>? requestedColumns, CancellationToken cancellationToken)
    {
        var optionSets = await _metadataService!.GetGlobalOptionSetsAsync(cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        var allRows = new List<IReadOnlyDictionary<string, QueryValue>>();

        foreach (var os in optionSets)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var details = await _metadataService.GetOptionSetAsync(os.Name, cancellationToken)
                .ConfigureAwait(false);

            foreach (var option in details.Options)
            {
                allRows.Add(FilterColumns(new Dictionary<string, QueryValue>(StringComparer.OrdinalIgnoreCase)
                {
                    ["optionsetname"] = QueryValue.Simple(os.Name),
                    ["value"] = QueryValue.Simple(option.Value),
                    ["label"] = QueryValue.Simple(option.Label),
                    ["description"] = QueryValue.Simple(option.Description)
                }, requestedColumns));
            }
        }

        return allRows;
    }

    #endregion

    #region Helpers

    /// <summary>
    /// Filters a row to only include the requested columns.
    /// If requestedColumns is null, returns all columns.
    /// </summary>
    private static IReadOnlyDictionary<string, QueryValue> FilterColumns(
        Dictionary<string, QueryValue> allColumns,
        IReadOnlyList<string>? requestedColumns)
    {
        if (requestedColumns == null)
        {
            return allColumns;
        }

        var filtered = new Dictionary<string, QueryValue>(requestedColumns.Count, StringComparer.OrdinalIgnoreCase);
        foreach (var col in requestedColumns)
        {
            if (allColumns.TryGetValue(col, out var value))
            {
                filtered[col] = value;
            }
            else
            {
                filtered[col] = QueryValue.Null;
            }
        }

        return filtered;
    }

    #endregion
}

using System;
using System.Collections.Generic;

namespace PPDS.Dataverse.Metadata;

/// <summary>
/// Defines the schema of metadata virtual tables.
/// Each table maps to a Dataverse metadata API endpoint and exposes
/// a fixed set of columns that can be queried via SQL.
/// </summary>
public static class MetadataTableDefinitions
{
    /// <summary>Known metadata virtual table names and their available columns.</summary>
    public static readonly IReadOnlyDictionary<string, IReadOnlyList<string>> Tables =
        new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["entity"] = new[]
            {
                "logicalname", "displayname", "pluraldisplayname", "description",
                "schemaname", "objecttypecode", "iscustomentity", "isactivity",
                "ownershiptype", "isvalidforadvancedfind", "iscustomizable",
                "isintersect", "isvirtual", "hasnotes", "hasactivities",
                "changetracking", "entitysetname"
            },
            ["attribute"] = new[]
            {
                "logicalname", "entitylogicalname", "displayname", "description",
                "attributetype", "schemaname", "isrequired", "iscustomattribute",
                "issearchable", "maxlength", "minvalue", "maxvalue",
                "precision", "format", "imemode"
            },
            ["relationship_1_n"] = new[]
            {
                "schemaname", "referencingentity", "referencedentity",
                "referencingattribute", "referencedattribute",
                "iscustomrelationship", "isvalidforadvancedfind",
                "relationshiptype", "securitytypes"
            },
            ["relationship_n_n"] = new[]
            {
                "schemaname", "entity1logicalname", "entity2logicalname",
                "intersectentityname", "entity1intersectattribute",
                "entity2intersectattribute", "iscustomrelationship"
            },
            ["optionset"] = new[]
            {
                "name", "displayname", "description", "isglobal",
                "optionsettype"
            },
            ["optionsetvalue"] = new[]
            {
                "optionsetname", "value", "label", "description"
            },
            ["relationship"] = new[]
            {
                "schemaname", "referencingentity", "referencedentity",
                "referencingattribute", "referencedattribute",
                "iscustomrelationship", "isvalidforadvancedfind",
                "relationshiptype", "securitytypes",
                "entity1logicalname", "entity2logicalname",
                "intersectentityname"
            },
            ["key"] = new[]
            {
                "logicalname", "entitylogicalname", "displayname",
                "keyattributes", "iscustomizable", "ismanaged",
                "schemaname"
            }
        };

    /// <summary>
    /// Returns true if the name matches a metadata virtual table.
    /// The table name must be fully qualified (metadata.entity).
    /// </summary>
    public static bool IsMetadataTable(string name)
    {
        if (name.StartsWith("metadata.", StringComparison.OrdinalIgnoreCase))
        {
            var tableName = name.Substring("metadata.".Length);
            return Tables.ContainsKey(tableName);
        }

        return false;
    }

    /// <summary>
    /// Extracts the table name from a potentially schema-qualified name.
    /// "metadata.entity" returns "entity"; "entity" returns "entity".
    /// </summary>
    public static string GetTableName(string schemaQualifiedName)
    {
        if (schemaQualifiedName.StartsWith("metadata.", StringComparison.OrdinalIgnoreCase))
        {
            return schemaQualifiedName.Substring("metadata.".Length);
        }

        return schemaQualifiedName;
    }

    /// <summary>
    /// Gets the available columns for a metadata virtual table.
    /// Returns null if the table name is not recognized.
    /// </summary>
    public static IReadOnlyList<string>? GetColumns(string tableName)
    {
        var normalizedName = GetTableName(tableName);
        return Tables.TryGetValue(normalizedName, out var columns) ? columns : null;
    }
}

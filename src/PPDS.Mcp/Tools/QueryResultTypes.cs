using System.Text.Json.Serialization;
using PPDS.Dataverse.Query;

namespace PPDS.Mcp.Tools;

/// <summary>
/// Result of a query execution.
/// </summary>
public sealed class QueryResult
{
    /// <summary>
    /// Primary entity logical name.
    /// </summary>
    [JsonPropertyName("entityName")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? EntityName { get; set; }

    /// <summary>
    /// Column metadata.
    /// </summary>
    [JsonPropertyName("columns")]
    public List<QueryColumnInfo> Columns { get; set; } = [];

    /// <summary>
    /// Result records.
    /// </summary>
    [JsonPropertyName("records")]
    public List<Dictionary<string, object?>> Records { get; set; } = [];

    /// <summary>
    /// Number of records returned.
    /// </summary>
    [JsonPropertyName("count")]
    public int Count { get; set; }

    /// <summary>
    /// Whether more records are available.
    /// </summary>
    [JsonPropertyName("moreRecords")]
    public bool MoreRecords { get; set; }

    /// <summary>
    /// The FetchXML that was executed (for debugging).
    /// </summary>
    [JsonPropertyName("executedFetchXml")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ExecutedFetchXml { get; set; }

    /// <summary>
    /// Query execution time in milliseconds.
    /// </summary>
    [JsonPropertyName("executionTimeMs")]
    public long ExecutionTimeMs { get; set; }
}

/// <summary>
/// Column information in query results.
/// </summary>
public sealed class QueryColumnInfo
{
    /// <summary>
    /// Attribute logical name.
    /// </summary>
    [JsonPropertyName("logicalName")]
    public string LogicalName { get; set; } = "";

    /// <summary>
    /// Column alias (if specified in query).
    /// </summary>
    [JsonPropertyName("alias")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Alias { get; set; }

    /// <summary>
    /// Human-readable display name.
    /// </summary>
    [JsonPropertyName("displayName")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? DisplayName { get; set; }

    /// <summary>
    /// Data type (String, Integer, Money, DateTime, etc.).
    /// </summary>
    [JsonPropertyName("dataType")]
    public string DataType { get; set; } = "";

    /// <summary>
    /// Linked entity alias (for joined columns).
    /// </summary>
    [JsonPropertyName("linkedEntityAlias")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? LinkedEntityAlias { get; set; }
}

/// <summary>
/// Helper methods for mapping query results.
/// </summary>
internal static class QueryResultMapper
{
    /// <summary>
    /// Maps a Dataverse query result to the MCP QueryResult type.
    /// </summary>
    /// <param name="result">The Dataverse query result.</param>
    /// <param name="fetchXml">The executed FetchXML.</param>
    /// <returns>The mapped query result.</returns>
    public static QueryResult MapToResult(PPDS.Dataverse.Query.QueryResult result, string fetchXml)
    {
        return new QueryResult
        {
            EntityName = result.EntityLogicalName,
            Columns = result.Columns.Select(c => new QueryColumnInfo
            {
                LogicalName = c.LogicalName,
                Alias = c.Alias,
                DisplayName = c.DisplayName,
                DataType = c.DataType.ToString(),
                LinkedEntityAlias = c.LinkedEntityAlias
            }).ToList(),
            Records = result.Records.Select(r =>
                r.ToDictionary(
                    kvp => kvp.Key,
                    kvp => MapQueryValue(kvp.Value))).ToList(),
            Count = result.Count,
            MoreRecords = result.MoreRecords,
            ExecutedFetchXml = fetchXml,
            ExecutionTimeMs = result.ExecutionTimeMs
        };
    }

    /// <summary>
    /// Maps a QueryValue to a JSON-serializable object.
    /// </summary>
    /// <param name="value">The query value.</param>
    /// <returns>The mapped value.</returns>
    public static object? MapQueryValue(QueryValue? value)
    {
        if (value == null) return null;

        // For lookups, return structured object.
        if (value.LookupEntityId.HasValue)
        {
            return new Dictionary<string, object?>
            {
                ["value"] = value.Value,
                ["formatted"] = value.FormattedValue,
                ["entityType"] = value.LookupEntityType,
                ["entityId"] = value.LookupEntityId
            };
        }

        // For values with formatting, return structured object.
        if (value.FormattedValue != null)
        {
            return new Dictionary<string, object?>
            {
                ["value"] = value.Value,
                ["formatted"] = value.FormattedValue
            };
        }

        // Simple value.
        return value.Value;
    }
}

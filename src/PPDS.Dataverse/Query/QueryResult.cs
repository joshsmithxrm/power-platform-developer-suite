using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace PPDS.Dataverse.Query;

/// <summary>
/// Represents the result of a query execution against Dataverse.
/// Contains the records, column metadata, and paging information.
/// </summary>
public sealed class QueryResult
{
    /// <summary>
    /// The logical name of the primary entity being queried.
    /// </summary>
    [JsonPropertyName("entityName")]
    public required string EntityLogicalName { get; init; }

    /// <summary>
    /// Metadata about the columns in the result set.
    /// </summary>
    [JsonPropertyName("columns")]
    public required IReadOnlyList<QueryColumn> Columns { get; init; }

    /// <summary>
    /// The records returned by the query.
    /// Each record is a dictionary mapping column names to their values.
    /// </summary>
    [JsonPropertyName("records")]
    public required IReadOnlyList<IReadOnlyDictionary<string, QueryValue>> Records { get; init; }

    /// <summary>
    /// The number of records in this result page.
    /// </summary>
    [JsonPropertyName("count")]
    public required int Count { get; init; }

    /// <summary>
    /// The total number of records matching the query, if count was requested.
    /// Only populated when the query includes returntotalrecordcount.
    /// </summary>
    [JsonPropertyName("totalCount")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? TotalCount { get; init; }

    /// <summary>
    /// Whether there are more records available beyond this page.
    /// </summary>
    [JsonPropertyName("moreRecords")]
    public bool MoreRecords { get; init; }

    /// <summary>
    /// The paging cookie for fetching the next page of results.
    /// Pass this to the next query execution to continue.
    /// </summary>
    [JsonPropertyName("pagingCookie")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? PagingCookie { get; init; }

    /// <summary>
    /// The current page number (1-based).
    /// </summary>
    [JsonPropertyName("pageNumber")]
    public int PageNumber { get; init; } = 1;

    /// <summary>
    /// The time taken to execute the query.
    /// </summary>
    [JsonPropertyName("executionTimeMs")]
    public long ExecutionTimeMs { get; init; }

    /// <summary>
    /// The FetchXML that was actually executed.
    /// For SQL queries, this is the transpiled FetchXML.
    /// </summary>
    [JsonPropertyName("executedFetchXml")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ExecutedFetchXml { get; init; }

    /// <summary>
    /// Whether the query was an aggregate query.
    /// </summary>
    [JsonPropertyName("isAggregate")]
    public bool IsAggregate { get; init; }

    /// <summary>
    /// Creates an empty result with no records.
    /// </summary>
    public static QueryResult Empty(string entityLogicalName) => new()
    {
        EntityLogicalName = entityLogicalName,
        Columns = [],
        Records = [],
        Count = 0,
        MoreRecords = false
    };
}

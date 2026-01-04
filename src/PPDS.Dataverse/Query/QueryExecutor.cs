using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using Microsoft.Extensions.Logging;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using PPDS.Dataverse.Pooling;

namespace PPDS.Dataverse.Query;

/// <summary>
/// Executes FetchXML queries against Dataverse using the connection pool.
/// </summary>
public class QueryExecutor : IQueryExecutor
{
    private readonly IDataverseConnectionPool _connectionPool;
    private readonly ILogger<QueryExecutor>? _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="QueryExecutor"/> class.
    /// </summary>
    /// <param name="connectionPool">The connection pool.</param>
    /// <param name="logger">Optional logger.</param>
    public QueryExecutor(
        IDataverseConnectionPool connectionPool,
        ILogger<QueryExecutor>? logger = null)
    {
        _connectionPool = connectionPool ?? throw new ArgumentNullException(nameof(connectionPool));
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<QueryResult> ExecuteFetchXmlAsync(
        string fetchXml,
        int? pageNumber = null,
        string? pagingCookie = null,
        bool includeCount = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fetchXml);

        var stopwatch = Stopwatch.StartNew();

        // Parse the FetchXML to extract metadata and apply paging
        var fetchDoc = XDocument.Parse(fetchXml);
        var fetchElement = fetchDoc.Root
            ?? throw new ArgumentException("Invalid FetchXML: missing root element", nameof(fetchXml));

        var entityElement = fetchElement.Element("entity")
            ?? throw new ArgumentException("Invalid FetchXML: missing entity element", nameof(fetchXml));

        var entityLogicalName = entityElement.Attribute("name")?.Value
            ?? throw new ArgumentException("Invalid FetchXML: entity missing name attribute", nameof(fetchXml));

        var isAggregate = string.Equals(fetchElement.Attribute("aggregate")?.Value, "true", StringComparison.OrdinalIgnoreCase);

        // Extract column metadata before execution
        var columns = ExtractColumns(entityElement, entityLogicalName, isAggregate);

        // Apply paging if specified
        var effectivePageNumber = pageNumber ?? 1;
        if (pageNumber.HasValue || !string.IsNullOrEmpty(pagingCookie))
        {
            fetchElement.SetAttributeValue("page", effectivePageNumber.ToString());
            if (!string.IsNullOrEmpty(pagingCookie))
            {
                fetchElement.SetAttributeValue("paging-cookie", pagingCookie);
            }
        }

        // Add return-total-count if requested
        if (includeCount)
        {
            fetchElement.SetAttributeValue("returntotalrecordcount", "true");
        }

        var modifiedFetchXml = fetchDoc.ToString(SaveOptions.DisableFormatting);

        _logger?.LogDebug("Executing FetchXML query for entity {Entity}, page {Page}",
            entityLogicalName, effectivePageNumber);

        // Execute the query
        await using var client = await _connectionPool.GetClientAsync(cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        var fetchExpression = new FetchExpression(modifiedFetchXml);
        var entityCollection = await client.RetrieveMultipleAsync(fetchExpression, cancellationToken)
            .ConfigureAwait(false);

        stopwatch.Stop();

        // Map results
        var records = MapRecords(entityCollection, columns, isAggregate);

        // For all-attributes, infer columns from all records (attributes with null values are omitted from responses)
        if (columns.Count == 0 && records.Count > 0)
        {
            columns = InferColumnsFromRecords(records);
        }

        _logger?.LogInformation("Query returned {Count} records in {ElapsedMs}ms (moreRecords: {MoreRecords})",
            entityCollection.Entities.Count, stopwatch.ElapsedMilliseconds, entityCollection.MoreRecords);

        return new QueryResult
        {
            EntityLogicalName = entityLogicalName,
            Columns = columns,
            Records = records,
            Count = records.Count,
            TotalCount = entityCollection.TotalRecordCount > 0 ? entityCollection.TotalRecordCount : null,
            MoreRecords = entityCollection.MoreRecords,
            PagingCookie = entityCollection.PagingCookie,
            PageNumber = effectivePageNumber,
            ExecutionTimeMs = stopwatch.ElapsedMilliseconds,
            ExecutedFetchXml = modifiedFetchXml,
            IsAggregate = isAggregate
        };
    }

    /// <inheritdoc />
    public async Task<QueryResult> ExecuteFetchXmlAllPagesAsync(
        string fetchXml,
        int maxRecords = 5000,
        CancellationToken cancellationToken = default)
    {
        var allRecords = new List<IReadOnlyDictionary<string, QueryValue>>();
        IReadOnlyList<QueryColumn>? columns = null;
        string? entityLogicalName = null;
        var isAggregate = false;
        string? executedFetchXml = null;
        var stopwatch = Stopwatch.StartNew();

        string? pagingCookie = null;
        var pageNumber = 1;

        while (allRecords.Count < maxRecords)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var result = await ExecuteFetchXmlAsync(
                fetchXml,
                pageNumber,
                pagingCookie,
                includeCount: false,
                cancellationToken
            ).ConfigureAwait(false);

            columns ??= result.Columns;
            entityLogicalName ??= result.EntityLogicalName;
            isAggregate = result.IsAggregate;
            executedFetchXml ??= result.ExecutedFetchXml;

            allRecords.AddRange(result.Records);

            if (!result.MoreRecords)
            {
                break;
            }

            pagingCookie = result.PagingCookie;
            pageNumber++;
        }

        stopwatch.Stop();

        return new QueryResult
        {
            EntityLogicalName = entityLogicalName ?? "",
            Columns = columns ?? [],
            Records = allRecords,
            Count = allRecords.Count,
            TotalCount = null,
            MoreRecords = false,
            PagingCookie = null,
            PageNumber = 1,
            ExecutionTimeMs = stopwatch.ElapsedMilliseconds,
            ExecutedFetchXml = executedFetchXml,
            IsAggregate = isAggregate
        };
    }

    /// <summary>
    /// Extracts column metadata from the FetchXML entity element.
    /// </summary>
    private static List<QueryColumn> ExtractColumns(
        XElement entityElement,
        string entityLogicalName,
        bool isAggregate)
    {
        var columns = new List<QueryColumn>();

        // Check for all-attributes
        var hasAllAttributes = entityElement.Element("all-attributes") != null;

        if (hasAllAttributes)
        {
            // With all-attributes, we can't know columns until we get results
            // Return empty list; columns will be inferred from first record
            return columns;
        }

        // Process explicit attribute elements
        foreach (var attr in entityElement.Elements("attribute"))
        {
            var column = ParseAttributeElement(attr, null, null, isAggregate);
            if (column != null)
            {
                columns.Add(column);
            }
        }

        // Process link-entity elements recursively
        ProcessLinkEntities(entityElement, columns, isAggregate);

        return columns;
    }

    /// <summary>
    /// Infers column metadata from all records when using all-attributes.
    /// Scans all records because Dataverse omits null attributes from responses.
    /// </summary>
    private static List<QueryColumn> InferColumnsFromRecords(
        IReadOnlyList<IReadOnlyDictionary<string, QueryValue>> records)
    {
        // Collect all unique keys across all records
        var allKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var record in records)
        {
            foreach (var key in record.Keys)
            {
                allKeys.Add(key);
            }
        }

        // Sort alphabetically for consistent output, but put common ID columns first
        return allKeys
            .OrderBy(k => k.EndsWith("id", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(k => k, StringComparer.OrdinalIgnoreCase)
            .Select(key => new QueryColumn
            {
                LogicalName = key,
                DataType = QueryColumnType.Unknown
            })
            .ToList();
    }

    /// <summary>
    /// Recursively processes link-entity elements to extract their attributes.
    /// </summary>
    private static void ProcessLinkEntities(
        XElement parentElement,
        List<QueryColumn> columns,
        bool isAggregate)
    {
        foreach (var linkEntity in parentElement.Elements("link-entity"))
        {
            var linkAlias = linkEntity.Attribute("alias")?.Value;
            var linkName = linkEntity.Attribute("name")?.Value;

            // Generate alias if not specified (Dataverse uses entity name + index)
            var effectiveAlias = linkAlias ?? linkName;

            foreach (var attr in linkEntity.Elements("attribute"))
            {
                var column = ParseAttributeElement(attr, effectiveAlias, linkName, isAggregate);
                if (column != null)
                {
                    columns.Add(column);
                }
            }

            // Recurse into nested link-entities
            ProcessLinkEntities(linkEntity, columns, isAggregate);
        }
    }

    /// <summary>
    /// Parses an attribute element into a QueryColumn.
    /// </summary>
    private static QueryColumn? ParseAttributeElement(
        XElement attr,
        string? linkedEntityAlias,
        string? linkedEntityName,
        bool isAggregate)
    {
        var name = attr.Attribute("name")?.Value;
        if (string.IsNullOrEmpty(name))
        {
            return null;
        }

        var alias = attr.Attribute("alias")?.Value;
        var aggregate = attr.Attribute("aggregate")?.Value;

        return new QueryColumn
        {
            LogicalName = name,
            Alias = alias,
            LinkedEntityAlias = linkedEntityAlias,
            LinkedEntityName = linkedEntityName,
            IsAggregate = !string.IsNullOrEmpty(aggregate),
            AggregateFunction = aggregate
        };
    }

    /// <summary>
    /// Maps EntityCollection records to QueryResult records.
    /// </summary>
    private static List<IReadOnlyDictionary<string, QueryValue>> MapRecords(
        EntityCollection entityCollection,
        List<QueryColumn> columns,
        bool isAggregate)
    {
        var records = new List<IReadOnlyDictionary<string, QueryValue>>();

        foreach (var entity in entityCollection.Entities)
        {
            var record = MapRecord(entity, columns, isAggregate);
            records.Add(record);
        }

        return records;
    }

    /// <summary>
    /// Maps a single Entity to a record dictionary.
    /// </summary>
    private static Dictionary<string, QueryValue> MapRecord(
        Entity entity,
        List<QueryColumn> columns,
        bool isAggregate)
    {
        var record = new Dictionary<string, QueryValue>(StringComparer.OrdinalIgnoreCase);

        // If we have explicit columns, map each one
        if (columns.Count > 0)
        {
            foreach (var column in columns)
            {
                var key = column.Alias ?? column.QualifiedName;
                var value = GetAttributeValue(entity, column, isAggregate);
                record[key] = value;
            }
        }
        else
        {
            // No explicit columns (all-attributes or empty) - map all attributes from entity
            foreach (var attr in entity.Attributes)
            {
                var value = MapAttributeValue(attr.Value, entity, attr.Key);
                record[attr.Key] = value;
            }
        }

        // Always include the primary ID if present and not already included
        if (!record.ContainsKey(entity.LogicalName + "id") && entity.Id != Guid.Empty)
        {
            record[entity.LogicalName + "id"] = QueryValue.Simple(entity.Id);
        }

        return record;
    }

    /// <summary>
    /// Gets the value for a specific column from an entity.
    /// </summary>
    private static QueryValue GetAttributeValue(Entity entity, QueryColumn column, bool isAggregate)
    {
        // Determine the key to look up in the entity
        string lookupKey;
        if (!string.IsNullOrEmpty(column.Alias))
        {
            lookupKey = column.Alias;
        }
        else if (!string.IsNullOrEmpty(column.LinkedEntityAlias))
        {
            lookupKey = $"{column.LinkedEntityAlias}.{column.LogicalName}";
        }
        else
        {
            lookupKey = column.LogicalName;
        }

        if (!entity.Attributes.TryGetValue(lookupKey, out var value))
        {
            return QueryValue.Null;
        }

        return MapAttributeValue(value, entity, lookupKey);
    }

    /// <summary>
    /// Maps an SDK attribute value to a QueryValue.
    /// </summary>
    private static QueryValue MapAttributeValue(object? value, Entity entity, string attributeKey)
    {
        if (value == null)
        {
            return QueryValue.Null;
        }

        // Get formatted value if available
        var formattedValue = entity.FormattedValues.TryGetValue(attributeKey, out var formatted)
            ? formatted
            : null;

        return value switch
        {
            // Aliased values from aggregates or link-entities
            AliasedValue aliased => MapAttributeValue(aliased.Value, entity, attributeKey),

            // Entity references (lookups)
            EntityReference entityRef => QueryValue.Lookup(
                entityRef.Id,
                entityRef.LogicalName,
                entityRef.Name ?? formattedValue),

            // Option set values
            OptionSetValue optionSet => QueryValue.WithFormatting(
                optionSet.Value,
                formattedValue),

            // Multi-select option set
            OptionSetValueCollection optionSetCollection => QueryValue.WithFormatting(
                optionSetCollection.Select(o => o.Value).ToArray(),
                formattedValue),

            // Money values
            Money money => QueryValue.WithFormatting(
                money.Value,
                formattedValue),

            // Boolean with formatted value
            bool boolValue => QueryValue.WithFormatting(
                boolValue,
                formattedValue),

            // DateTime - preserve as is, add formatted if available
            DateTime dateTime => QueryValue.WithFormatting(
                dateTime,
                formattedValue),

            // Guid
            Guid guid => QueryValue.Simple(guid),

            // Primitive types (string, int, decimal, double, etc.)
            _ => formattedValue != null
                ? QueryValue.WithFormatting(value, formattedValue)
                : QueryValue.Simple(value)
        };
    }
}

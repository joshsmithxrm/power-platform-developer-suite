using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Xrm.Sdk.Query;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Dataverse.Pooling;
using PPDS.Query.Parsing;
using PPDS.Query.Transpilation;

namespace PPDS.Cli.Services.Data;

/// <summary>
/// Implements <see cref="IDataQueryService"/> using the connection pool for all Dataverse access.
/// All business-query logic for Delete, Truncate, and Update commands lives here (Constitution A1).
/// </summary>
public sealed class DataQueryService : IDataQueryService
{
    private readonly IDataverseConnectionPool _pool;
    private readonly ILogger<DataQueryService> _logger;

    /// <summary>
    /// Initializes a new instance of <see cref="DataQueryService"/>.
    /// </summary>
    /// <param name="pool">The Dataverse connection pool.</param>
    /// <param name="logger">Logger instance.</param>
    public DataQueryService(IDataverseConnectionPool pool, ILogger<DataQueryService> logger)
    {
        _pool = pool ?? throw new ArgumentNullException(nameof(pool));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public async Task<Guid?> ResolveByAlternateKeyAsync(
        string entity,
        string keyString,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(entity)) throw new ArgumentNullException(nameof(entity));
        if (string.IsNullOrWhiteSpace(keyString)) throw new ArgumentNullException(nameof(keyString));

        // Two rows are asked for, so a key that is not unique is seen and refused rather than resolved to one
        // arbitrary record (#1918).
        var query = new QueryExpression(entity)
        {
            ColumnSet = new ColumnSet(false),
            TopCount = 2
        };

        var keyColumns = new List<string>();
        foreach (var pair in keyString.Split(','))
        {
            var parts = pair.Split('=', 2);
            if (parts.Length != 2)
            {
                throw new PpdsValidationException("keyString",
                    $"Invalid key format: '{pair}'. Expected field=value.");
            }
            keyColumns.Add(parts[0].Trim());
            query.Criteria.AddCondition(parts[0].Trim(), ConditionOperator.Equal, parts[1].Trim());
        }

        _logger.LogDebug("Resolving alternate key for entity {Entity}", entity);

        try
        {
            Microsoft.Xrm.Sdk.EntityCollection results;
            await using (var client = await _pool.GetClientAsync(cancellationToken: cancellationToken))
            {
                results = await client.RetrieveMultipleAsync(query, cancellationToken);
            }

            if (results.Entities.Count > 1)
            {
                throw new PpdsException(ErrorCodes.Validation.KeyMatchesMultipleRecords,
                    await DescribeAmbiguousKeyAsync(entity, keyColumns, results.Entities.Count, cancellationToken));
            }

            return results.Entities.Count == 1 ? results.Entities[0].Id : null;
        }
        catch (PpdsException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to resolve alternate key for entity {Entity}", entity);
            throw new PpdsException(ErrorCodes.Query.ExecutionFailed,
                $"Failed to resolve alternate key for '{entity}': {ex.Message}", ex);
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<Guid>> QueryIdsByFilterAsync(
        string entity,
        string whereFilter,
        int? limit,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(entity)) throw new ArgumentNullException(nameof(entity));
        if (string.IsNullOrWhiteSpace(whereFilter)) throw new ArgumentNullException(nameof(whereFilter));

        // Build SQL and transpile to FetchXML
        var sql = limit.HasValue
            ? $"SELECT TOP {limit.Value + 1} {entity}id FROM {entity} WHERE {whereFilter}"
            : $"SELECT {entity}id FROM {entity} WHERE {whereFilter}";

        string fetchXml;
        try
        {
            var parser = new QueryParser();
            var stmt = parser.ParseStatement(sql);
            var generator = new FetchXmlGenerator();
            fetchXml = generator.Generate(stmt);
        }
        catch (Exception ex)
        {
            throw new PpdsException(ErrorCodes.Query.ParseError,
                $"Failed to parse filter expression: {ex.Message}", ex);
        }

        _logger.LogDebug("Querying IDs for entity {Entity} with filter", entity);

        try
        {
            await using var client = await _pool.GetClientAsync(cancellationToken: cancellationToken);
            var query = new FetchExpression(fetchXml);
            var results = await client.RetrieveMultipleAsync(query, cancellationToken);

            var primaryKeyAttribute = $"{entity}id";
            var ids = new List<Guid>(results.Entities.Count);

            foreach (var record in results.Entities)
            {
                if (record.Contains(primaryKeyAttribute) && record[primaryKeyAttribute] is Guid id)
                {
                    ids.Add(id);
                }
                else
                {
                    ids.Add(record.Id);
                }
            }

            return ids;
        }
        catch (PpdsException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to query IDs for entity {Entity}", entity);
            throw new PpdsException(ErrorCodes.Query.ExecutionFailed,
                $"Failed to query records for '{entity}': {ex.Message}", ex);
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<Guid>> FetchBatchIdsAsync(
        string entity,
        int batchSize,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(entity)) throw new ArgumentNullException(nameof(entity));
        if (batchSize < 1) throw new ArgumentOutOfRangeException(nameof(batchSize), "Batch size must be at least 1.");

        var primaryKey = $"{entity}id";

        var query = new QueryExpression(entity)
        {
            ColumnSet = new ColumnSet(primaryKey),
            TopCount = batchSize
        };

        _logger.LogDebug("Fetching batch of up to {BatchSize} IDs for entity {Entity}", batchSize, entity);

        try
        {
            await using var client = await _pool.GetClientAsync(cancellationToken: cancellationToken);
            var result = await client.RetrieveMultipleAsync(query, cancellationToken);
            return result.Entities.Select(e => e.Id).ToList();
        }
        catch (PpdsException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to fetch batch IDs for entity {Entity}", entity);
            throw new PpdsException(ErrorCodes.Query.ExecutionFailed,
                $"Failed to fetch records for '{entity}': {ex.Message}", ex);
        }
    }

    /// <inheritdoc />
    public async Task<int> CountRecordsAsync(
        string entity,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(entity)) throw new ArgumentNullException(nameof(entity));

        var fetchXml = $@"<fetch aggregate='true'>
                <entity name='{entity}'>
                    <attribute name='{entity}id' alias='count' aggregate='count' />
                </entity>
            </fetch>";

        _logger.LogDebug("Counting records for entity {Entity}", entity);

        try
        {
            await using var client = await _pool.GetClientAsync(cancellationToken: cancellationToken);
            var result = await client.RetrieveMultipleAsync(new FetchExpression(fetchXml), cancellationToken);

            if (result.Entities.Count > 0 && result.Entities[0].Contains("count"))
            {
                var countValue = result.Entities[0]["count"];
                if (countValue is Microsoft.Xrm.Sdk.AliasedValue aliased)
                {
                    return Convert.ToInt32(aliased.Value);
                }
            }

            return 0;
        }
        catch (PpdsException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to count records for entity {Entity}", entity);
            throw new PpdsException(ErrorCodes.Query.ExecutionFailed,
                $"Failed to count records for '{entity}': {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Builds the refusal for a <c>--key</c> that matched more than one record. It says whether the key's columns are
    /// a defined alternate key of the table (read from the table's metadata), because the remedy differs: a defined
    /// key with duplicates means its index is not active yet; any other set of columns is not guaranteed unique.
    /// The message names the table and the columns, never the key's values or a record id.
    /// </summary>
    private async Task<string> DescribeAmbiguousKeyAsync(
        string entity,
        IReadOnlyList<string> keyColumns,
        int matchedAtLeast,
        CancellationToken cancellationToken)
    {
        var columns = string.Join(", ", keyColumns);
        var head = $"The key matches more than one record of '{entity}' (at least {matchedAtLeast}), so it does not " +
                   "name one record. Nothing was changed.";

        Microsoft.Xrm.Sdk.Metadata.EntityKeyMetadata[]? keys;
        try
        {
            await using var client = await _pool.GetClientAsync(cancellationToken: cancellationToken);
            var response = (Microsoft.Xrm.Sdk.Messages.RetrieveEntityResponse)await client.ExecuteAsync(
                new Microsoft.Xrm.Sdk.Messages.RetrieveEntityRequest
                {
                    LogicalName = entity,
                    EntityFilters = Microsoft.Xrm.Sdk.Metadata.EntityFilters.Entity,
                    RetrieveAsIfPublished = false
                },
                cancellationToken);
            keys = response.EntityMetadata?.Keys;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not read the alternate keys of entity {Entity}", entity);
            return $"{head} Whether the columns {columns} are an alternate key of the table could not be read. " +
                   "Use --id to name the record.";
        }

        var given = new HashSet<string>(keyColumns, StringComparer.OrdinalIgnoreCase);
        var match = keys?.FirstOrDefault(k =>
            k.KeyAttributes is { Length: > 0 } attributes &&
            attributes.Length == given.Count &&
            attributes.All(given.Contains));

        if (match is null)
        {
            return $"{head} The columns {columns} are not an alternate key of the table, so their values are not " +
                   "guaranteed to be unique. Use --id, or --key with the columns of an alternate key of the table.";
        }

        if (match.EntityKeyIndexStatus == Microsoft.Xrm.Sdk.EntityKeyIndexStatus.Active)
        {
            return $"{head} The columns {columns} are the alternate key '{match.LogicalName}' of the table and its " +
                   "index is active, yet more than one record matched. Use --id to name the record.";
        }

        return $"{head} The columns {columns} are the alternate key '{match.LogicalName}' of the table, but its index " +
               $"is not active ({match.EntityKeyIndexStatus}), so Dataverse does not yet keep the values unique. " +
               "Use --id, or remove the duplicates and wait until the key's index is active.";
    }
}

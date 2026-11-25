using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Dataverse.Pooling;

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

        var query = new QueryExpression(entity)
        {
            ColumnSet = new ColumnSet(false),
            TopCount = 1
        };

        foreach (var pair in keyString.Split(','))
        {
            var parts = pair.Split('=', 2);
            if (parts.Length != 2)
            {
                throw new PpdsValidationException("keyString",
                    $"Invalid key format: '{pair}'. Expected field=value.");
            }
            query.Criteria.AddCondition(parts[0].Trim(), ConditionOperator.Equal, parts[1].Trim());
        }

        _logger.LogDebug("Resolving alternate key for entity {Entity}", entity);

        try
        {
            await using var client = await _pool.GetClientAsync(cancellationToken: cancellationToken);
            var results = await client.RetrieveMultipleAsync(query, cancellationToken);
            return results.Entities.Count > 0 ? results.Entities[0].Id : null;
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

        // Admitted and translated before anything is read; a part FetchXML cannot express exactly is refused here.
        var filter = DataFilter.Translate(entity, whereFilter);

        _logger.LogDebug("Querying IDs for entity {Entity} with a filter of {Count} condition(s)",
            filter.EntityName, filter.ConditionCount);

        var ids = new List<Guid>();
        var seen = new HashSet<Guid>();
        var page = 1;
        string? pagingCookie = null;

        try
        {
            while (true)
            {
                EntityCollection results;
                await using (var client = await _pool.GetClientAsync(cancellationToken: cancellationToken))
                {
                    results = await client.RetrieveMultipleAsync(
                        new FetchExpression(filter.ToFetchXml(page, pagingCookie)), cancellationToken);
                }

                foreach (var record in results.Entities)
                {
                    var id = record.Contains(filter.PrimaryIdAttribute) && record[filter.PrimaryIdAttribute] is Guid value
                        ? value
                        : record.Id;
                    if (!seen.Add(id))
                    {
                        throw new PpdsException(ErrorCodes.Query.ExecutionFailed,
                            $"A record of '{filter.EntityName}' came back twice while the matching records were read " +
                            "page by page, so the set of records is not certain. Nothing was changed. Run the command again.");
                    }

                    ids.Add(id);
                }

                // With --limit, one record more than the limit is enough for the caller to refuse.
                if (limit.HasValue && ids.Count > limit.Value)
                    return ids;

                if (!results.MoreRecords)
                    return ids;

                if (results.Entities.Count == 0)
                {
                    throw new PpdsException(ErrorCodes.Query.ExecutionFailed,
                        $"Dataverse reported more records of '{filter.EntityName}' but returned an empty page, so not " +
                        "every matching record could be read. Nothing was changed.");
                }

                page++;
                pagingCookie = results.PagingCookie;
            }
        }
        catch (PpdsException)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to query IDs for entity {Entity}", filter.EntityName);
            throw new PpdsException(ErrorCodes.Query.ExecutionFailed,
                $"Failed to query records for '{filter.EntityName}': {ex.Message}", ex);
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
                if (countValue is AliasedValue aliased)
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
}
